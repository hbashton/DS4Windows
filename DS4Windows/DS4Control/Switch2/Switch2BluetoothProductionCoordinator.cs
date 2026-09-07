/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

internal readonly struct Switch2BluetoothAssociationCandidate
{
    internal Switch2BluetoothAssociationCandidate(int id,
        Switch2ControllerModel model, bool isRemembered = false)
    {
        Id = id;
        Model = model;
        IsRemembered = isRemembered;
    }

    internal int Id { get; }

    internal Switch2ControllerModel Model { get; }

    internal bool IsRemembered { get; }
}

internal readonly struct Switch2JoyConPairCandidate
{
    internal Switch2JoyConPairCandidate(int id, Switch2ControllerModel model,
        ulong arrivalOrdinal, InputControllerSlotToken slotToken = default)
    {
        Id = id;
        Model = model;
        ArrivalOrdinal = arrivalOrdinal;
        SlotToken = slotToken;
    }

    internal int Id { get; }
    internal Switch2ControllerModel Model { get; }
    internal ulong ArrivalOrdinal { get; }
    internal InputControllerSlotToken SlotToken { get; }
}

internal enum Switch2JoyConPairActivationFailure : byte
{
    None = 0,
    PairingUnavailable,
    AutomaticPairingEnabled,
    InvalidCandidate,
    InvalidRoles,
    StoreRejected,
    RuntimeRejected,
    ActivationRejected,
    Cancelled,
}

internal readonly struct Switch2JoyConPairActivationResult
{
    private Switch2JoyConPairActivationResult(
        Switch2JoyConPairActivationFailure failure)
    {
        Failure = failure;
    }

    internal Switch2JoyConPairActivationFailure Failure { get; }
    internal bool Succeeded => Failure ==
        Switch2JoyConPairActivationFailure.None;

    internal static Switch2JoyConPairActivationResult Success() =>
        new(Switch2JoyConPairActivationFailure.None);

    internal static Switch2JoyConPairActivationResult Failed(
        Switch2JoyConPairActivationFailure failure) => new(failure);
}

internal enum Switch2JoyConStandaloneActivationFailure : byte
{
    None = 0,
    InvalidCandidate,
    RuntimeRejected,
    ActivationRejected,
    Cancelled,
}

internal readonly struct Switch2JoyConStandaloneActivationResult
{
    private Switch2JoyConStandaloneActivationResult(
        Switch2JoyConStandaloneActivationFailure failure)
    {
        Failure = failure;
    }

    internal Switch2JoyConStandaloneActivationFailure Failure { get; }
    internal bool Succeeded => Failure ==
        Switch2JoyConStandaloneActivationFailure.None;
    internal static Switch2JoyConStandaloneActivationResult Success() =>
        new(Switch2JoyConStandaloneActivationFailure.None);
    internal static Switch2JoyConStandaloneActivationResult Failed(
        Switch2JoyConStandaloneActivationFailure failure) => new(failure);
}

/// <summary>
/// Production owner for the Windows BLE scan-to-registration control path.
/// It serializes connection attempts, retains no Bluetooth addresses outside
/// the Windows adapter, and publishes reports only through the existing
/// registration/ControlService pipeline. The notification hot path remains in
/// the fixed-capacity Bluetooth input owner and adds no task or timer cadence.
/// </summary>
internal sealed partial class Switch2BluetoothProductionCoordinator
{
    private readonly struct ScanStartResult
    {
        internal ScanStartResult(bool started, Task<bool> failedCleanup)
        {
            Started = started;
            FailedCleanup = failedCleanup;
        }

        internal bool Started { get; }
        internal Task<bool> FailedCleanup { get; }
    }

    private const int QueueCapacity = 16;
    private const int LifecycleTimeoutMilliseconds = 5_000;

    private readonly object sync = new();
    private readonly Switch2BluetoothWindowsAdapter adapter;
    private readonly Switch2RuntimeRegistrationService registrationService;
    private readonly ISwitch2ControlServiceSlotHost slotHost;
    private readonly Action<InputControllerSlotToken> attached;
    private readonly Action<string> diagnostic;
    private readonly ISwitch2JoyConPairCatalog pairCatalog;
    private readonly Switch2JoyConPairAssociationService pairAssociation;
    private readonly Func<bool> automaticPairingEnabled;
    private readonly Func<InputControllerSlotToken, ISwitch2JoyConOutputHandoff> beginOutputHandoff;
    private readonly ISwitch2MagnetometerCalibrationStore
        magnetometerCalibrationStore;
    private readonly ISwitch2JoyConHoldModeStore joyConHoldModeStore;
    private readonly ISwitch2GyroCalibrationStore gyroCalibrationStore;
    private readonly ISwitch2RawStickCalibrationStore rawStickCalibrationStore;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly List<Task> connectionTasks = new();
    private readonly Dictionary<int, Switch2BluetoothCandidateObservation>
        associationCandidates = new();
    // Scan-private peer tokens only. A failed open may become an explicit
    // reconnect choice only after the adapter publishes fresh authority from
    // a later advertisement; this never revives a consumed admission.
    private readonly HashSet<Switch2BluetoothPeerToken> failedRememberedPeers = new();
    private readonly HashSet<Switch2BluetoothPeerToken> quarantinedRememberedPeers = new();
    private readonly Dictionary<Switch2PersistentPeerId, PendingJoyCon>
        pendingJoyCons = new();
    private readonly Dictionary<int, Switch2PersistentPeerId>
        joyConPairCandidates = new();
    private readonly Dictionary<Switch2BluetoothPeerToken,
        InputControllerSlotToken> activePeerRegistrations = new();

    private CancellationTokenSource lifetimeCancellation;
    private TaskCompletionSource<ScanStartResult> scanStartCompletion;
    private Task<bool> stopTask;
    private ulong scanGeneration;
    private ulong nextPhysicalGeneration;
    private ulong nextJoyConArrivalOrdinal;
    private int nextAssociationId;
    private int nextJoyConCandidateId;
    private Switch2JoyConPairRecord[] pairRecords =
        Array.Empty<Switch2JoyConPairRecord>();
    private bool running;
    private Switch2BluetoothWindowsScanStartFailure lastStartFailure;

    internal Switch2BluetoothProductionCoordinator(
        Switch2BluetoothWindowsAdapter adapter,
        Switch2RuntimeRegistrationService registrationService,
        ISwitch2ControlServiceSlotHost slotHost,
        Action<InputControllerSlotToken> attached,
        Action<string> diagnostic)
        : this(adapter, registrationService, slotHost, attached, diagnostic,
            null, null, null)
    {
    }

    internal Switch2BluetoothProductionCoordinator(
        Switch2BluetoothWindowsAdapter adapter,
        Switch2RuntimeRegistrationService registrationService,
        ISwitch2ControlServiceSlotHost slotHost,
        Action<InputControllerSlotToken> attached,
        Action<string> diagnostic, ISwitch2JoyConPairCatalog pairCatalog)
        : this(adapter, registrationService, slotHost, attached, diagnostic,
            pairCatalog, null, null)
    {
    }

    internal Switch2BluetoothProductionCoordinator(
        Switch2BluetoothWindowsAdapter adapter,
        Switch2RuntimeRegistrationService registrationService,
        ISwitch2ControlServiceSlotHost slotHost,
        Action<InputControllerSlotToken> attached,
        Action<string> diagnostic, ISwitch2JoyConPairCatalog pairCatalog,
        Func<bool> automaticPairingEnabled) : this(adapter,
            registrationService, slotHost, attached, diagnostic, pairCatalog,
            automaticPairingEnabled, null)
    {
    }

    internal Switch2BluetoothProductionCoordinator(
        Switch2BluetoothWindowsAdapter adapter,
        Switch2RuntimeRegistrationService registrationService,
        ISwitch2ControlServiceSlotHost slotHost,
        Action<InputControllerSlotToken> attached,
        Action<string> diagnostic, ISwitch2JoyConPairCatalog pairCatalog,
        Func<bool> automaticPairingEnabled,
        ISwitch2MagnetometerCalibrationStore magnetometerCalibrationStore,
        ISwitch2JoyConHoldModeStore joyConHoldModeStore = null,
        ISwitch2GyroCalibrationStore gyroCalibrationStore = null,
        ISwitch2RawStickCalibrationStore rawStickCalibrationStore = null,
        Func<InputControllerSlotToken, ISwitch2JoyConOutputHandoff> beginOutputHandoff = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(
            nameof(adapter));
        this.registrationService = registrationService ??
            throw new ArgumentNullException(nameof(registrationService));
        this.slotHost = slotHost ?? throw new ArgumentNullException(
            nameof(slotHost));
        this.attached = attached;
        this.diagnostic = diagnostic;
        this.pairCatalog = pairCatalog;
        this.automaticPairingEnabled = automaticPairingEnabled;
        this.magnetometerCalibrationStore = magnetometerCalibrationStore;
        this.joyConHoldModeStore = joyConHoldModeStore;
        this.gyroCalibrationStore = gyroCalibrationStore;
        this.rawStickCalibrationStore = rawStickCalibrationStore;
        this.beginOutputHandoff = beginOutputHandoff;
        pairAssociation = pairCatalog == null ? null :
            new Switch2JoyConPairAssociationService(pairCatalog);
    }

    internal bool TryStart(ulong exactScanGeneration,
        ReadOnlySpan<byte> selectedHostAddress,
        out Switch2BluetoothWindowsScanStartFailure failure)
    {
        if (exactScanGeneration == 0)
        {
            failure = Switch2BluetoothWindowsScanStartFailure.InvalidArgument;
            return false;
        }

        Switch2JoyConPairRecord[] loadedPairs =
            Array.Empty<Switch2JoyConPairRecord>();
        if (pairCatalog != null && !pairCatalog.TryLoadAll(out loadedPairs))
        {
            loadedPairs = Array.Empty<Switch2JoyConPairRecord>();
            diagnostic?.Invoke(
                "Switch 2 Joy-Con pair catalog could not be read; remembered explicit pairs are unavailable for this scan. Transient automatic pairing remains available when enabled.");
        }

        CancellationTokenSource cancellation;
        TaskCompletionSource<ScanStartResult> startCompletion;
        lock (sync)
        {
            if (running || lifetimeCancellation != null ||
                (stopTask != null && (!stopTask.IsCompletedSuccessfully ||
                    !stopTask.Result)))
            {
                failure = Switch2BluetoothWindowsScanStartFailure.
                    ScanAlreadyActive;
                return false;
            }
            cancellation = new CancellationTokenSource();
            startCompletion = scanStartCompletion =
                new TaskCompletionSource<ScanStartResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = null;
            lifetimeCancellation = cancellation;
            scanGeneration = exactScanGeneration;
            running = true;
            registrationService.RuntimeRemoved += OnJoyConRuntimeRemoved;
            lastStartFailure = Switch2BluetoothWindowsScanStartFailure.None;
            associationCandidates.Clear();
            failedRememberedPeers.Clear();
            quarantinedRememberedPeers.Clear();
            pendingJoyCons.Clear();
            joyConPairCandidates.Clear();
            activePeerRegistrations.Clear();
            joinedJoyCons.Clear();
            lastRemovedJoyConSlots.Clear();
            pairRecords = loadedPairs;
        }

        bool started = false;
        Task<bool> failedStartCleanup = Task.FromResult(false);
        try
        {
            started = adapter.TryStartScan(exactScanGeneration,
                selectedHostAddress, OnCandidate, out failure,
                out failedStartCleanup);
            lock (sync)
            {
                if (!started)
                {
                    running = false;
                    lastStartFailure = failure;
                    // Failed startup can still own native handlers. Keep its
                    // generation and cancellation until exact cleanup proves
                    // otherwise, including when Stop raced watcher creation.
                }
                else if (!running)
                {
                    failure = Switch2BluetoothWindowsScanStartFailure.
                        WatcherStartFailed;
                    return false;
                }
            }
            if (!started)
                return false;
        }
        finally
        {
            startCompletion.TrySetResult(new ScanStartResult(started,
                failedStartCleanup));
            if (!started)
            {
                // The same durable Stop path owns unsuccessful starts too.
                // Do not require a caller to notice failure before cleanup runs.
                lock (sync)
                {
                    // A concurrent Stop may have completed and admitted a new
                    // lifetime after the start result was published above.
                    if (ReferenceEquals(lifetimeCancellation, cancellation))
                        BeginStopNoLock();
                }
            }
        }

        diagnostic?.Invoke($"Switch 2 Bluetooth discovery active (generation {exactScanGeneration}).");
        return true;
    }

    internal Switch2BluetoothDiscoveryStatus GetDiscoveryStatus()
    {
        lock (sync)
        {
            if (stopTask != null && !stopTask.IsCompleted)
                return new(Switch2BluetoothDiscoveryState.Stopping);
            if (stopTask != null && (!stopTask.IsCompletedSuccessfully || !stopTask.Result))
                return new(Switch2BluetoothDiscoveryState.CleanupFailed);
            if (running)
            {
                if (!scanStartCompletion.Task.IsCompleted)
                    return new(Switch2BluetoothDiscoveryState.Starting);
                // IsScanning only reads adapter state; it does not enter WinRT.
                // Adapter candidate callbacks enter this coordinator only after
                // dropping their adapter lock.
                return new(adapter.IsScanning ? Switch2BluetoothDiscoveryState.Scanning :
                    Switch2BluetoothDiscoveryState.Interrupted);
            }
            return lastStartFailure == Switch2BluetoothWindowsScanStartFailure.None ?
                Switch2BluetoothDiscoveryStatus.Stopped :
                new(Switch2BluetoothDiscoveryState.StartFailed, lastStartFailure);
        }
    }

    internal Switch2BluetoothAssociationCandidate[]
        GetAssociationCandidates()
    {
        lock (sync)
        {
            var result = new Switch2BluetoothAssociationCandidate[
                associationCandidates.Count];
            int index = 0;
            foreach (KeyValuePair<int, Switch2BluetoothCandidateObservation>
                         item in associationCandidates)
            {
                result[index++] = new Switch2BluetoothAssociationCandidate(
                    item.Key, item.Value.Model, item.Value.Disposition ==
                        Switch2BluetoothObservationDisposition.RememberedThisHost);
            }
            return result;
        }
    }

    internal Switch2JoyConPairCandidate[] GetJoyConPairCandidates()
    {
        lock (sync)
        {
            var result = new Switch2JoyConPairCandidate[
                joyConPairCandidates.Count];
            int index = 0;
            foreach (KeyValuePair<int, Switch2PersistentPeerId> item in
                     joyConPairCandidates)
            {
                if (pendingJoyCons.TryGetValue(item.Value,
                        out PendingJoyCon pending))
                {
                    result[index++] = new Switch2JoyConPairCandidate(item.Key,
                        pending.Model, pending.ArrivalOrdinal, pending.SlotToken);
                }
            }
            if (index == result.Length)
            {
                Array.Sort(result, static (left, right) =>
                    left.ArrivalOrdinal.CompareTo(right.ArrivalOrdinal));
                return result;
            }
            Array.Resize(ref result, index);
            Array.Sort(result, static (left, right) =>
                left.ArrivalOrdinal.CompareTo(right.ArrivalOrdinal));
            return result;
        }
    }

    internal async ValueTask<int> ReconcileAutomaticJoyConPairsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAutomaticPairingEnabled())
        {
            return 0;
        }

        CancellationToken lifetimeToken;
        lock (sync)
        {
            if (!running || lifetimeCancellation == null)
            {
                return 0;
            }
            lifetimeToken = lifetimeCancellation.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeToken, cancellationToken);
        bool gateEntered = false;
        int activated = 0;
        try
        {
            await connectionGate.WaitAsync(linked.Token).ConfigureAwait(false);
            gateEntered = true;
            while (IsAutomaticPairingEnabled())
            {
                PendingJoyCon left;
                PendingJoyCon right;
                Switch2JoyConPairRecord record;
                lock (sync)
                {
                    if (!running ||
                        !TryClaimAutomaticPairNoLock(out left, out right,
                            out record))
                    {
                        break;
                    }
                }

                Switch2JoyConPairActivationResult result = await
                    ActivateJoinedAsync(left, right, record, linked.Token).
                        ConfigureAwait(false);
                if (result.Succeeded)
                {
                    activated++;
                }
                else
                {
                    diagnostic?.Invoke(
                        $"Switch 2 automatic Joy-Con pair activation rejected: {result.Failure}.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke(
                $"Switch 2 automatic Joy-Con reconciliation failed: {exception.GetType().Name}.");
        }
        finally
        {
            if (gateEntered)
            {
                connectionGate.Release();
            }
        }
        return activated;
    }

    internal async ValueTask<Switch2JoyConPairActivationResult>
        CreateAndActivateJoyConPairAsync(int leftCandidateId,
            int rightCandidateId,
            CancellationToken cancellationToken = default, int preferredCandidateId = 0)
    {
        if (pairAssociation == null)
        {
            return Switch2JoyConPairActivationResult.Failed(
                Switch2JoyConPairActivationFailure.PairingUnavailable);
        }
        if (IsAutomaticPairingEnabled())
        {
            return Switch2JoyConPairActivationResult.Failed(
                Switch2JoyConPairActivationFailure.
                    AutomaticPairingEnabled);
        }

        CancellationToken lifetimeToken;
        lock (sync)
        {
            if (!running || lifetimeCancellation == null)
            {
                return Switch2JoyConPairActivationResult.Failed(
                    Switch2JoyConPairActivationFailure.Cancelled);
            }
            lifetimeToken = lifetimeCancellation.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeToken, cancellationToken);
        bool gateEntered = false;
        try
        {
            await connectionGate.WaitAsync(linked.Token).ConfigureAwait(false);
            gateEntered = true;

            if (IsAutomaticPairingEnabled())
            {
                return Switch2JoyConPairActivationResult.Failed(
                    Switch2JoyConPairActivationFailure.
                        AutomaticPairingEnabled);
            }

            PendingJoyCon left;
            PendingJoyCon right;
            lock (sync)
            {
                if (!running ||
                    !joyConPairCandidates.TryGetValue(leftCandidateId,
                        out Switch2PersistentPeerId leftPeer) ||
                    !joyConPairCandidates.TryGetValue(rightCandidateId,
                        out Switch2PersistentPeerId rightPeer) ||
                    !pendingJoyCons.TryGetValue(leftPeer, out left) ||
                    !pendingJoyCons.TryGetValue(rightPeer, out right) ||
                    ReferenceEquals(left, right))
                {
                    return Switch2JoyConPairActivationResult.Failed(
                        Switch2JoyConPairActivationFailure.InvalidCandidate);
                }
            }
            if (left.Model != Switch2ControllerModel.JoyCon2Left ||
                right.Model != Switch2ControllerModel.JoyCon2Right ||
                preferredCandidateId != 0 && preferredCandidateId != leftCandidateId &&
                    preferredCandidateId != rightCandidateId)
            {
                return Switch2JoyConPairActivationResult.Failed(
                    Switch2JoyConPairActivationFailure.InvalidRoles);
            }
            if (!Switch2JoyConAssociationPeer.TryCreate(left.PeerId,
                    left.Model, left.ProductId, out var leftPeerRecord) ||
                !Switch2JoyConAssociationPeer.TryCreate(right.PeerId,
                    right.Model, right.ProductId, out var rightPeerRecord) ||
                !pairAssociation.TryCreateExplicitPair(leftPeerRecord,
                    rightPeerRecord, out Switch2JoyConPairRecord record,
                    out _))
            {
                return Switch2JoyConPairActivationResult.Failed(
                    Switch2JoyConPairActivationFailure.StoreRejected);
            }

            lock (sync)
            {
                if (!IsExactPendingNoLock(left) ||
                    !IsExactPendingNoLock(right))
                {
                    return Switch2JoyConPairActivationResult.Failed(
                        Switch2JoyConPairActivationFailure.InvalidCandidate);
                }
                RemovePendingNoLock(left);
                RemovePendingNoLock(right);
                pairRecords = AppendPairRecord(pairRecords, record);
            }
            return await ActivateJoinedAsync(left, right, record,
                linked.Token, preferredCandidateId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Switch2JoyConPairActivationResult.Failed(
                Switch2JoyConPairActivationFailure.Cancelled);
        }
        finally
        {
            if (gateEntered)
            {
                connectionGate.Release();
            }
        }
    }

    internal async ValueTask<Switch2JoyConStandaloneActivationResult>
        ActivateJoyConSeparatelyAsync(int candidateId,
            CancellationToken cancellationToken = default)
    {
        CancellationToken lifetimeToken;
        lock (sync)
        {
            if (!running || lifetimeCancellation == null)
            {
                return Switch2JoyConStandaloneActivationResult.Failed(
                    Switch2JoyConStandaloneActivationFailure.Cancelled);
            }
            lifetimeToken = lifetimeCancellation.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeToken, cancellationToken);
        bool gateEntered = false;
        try
        {
            await connectionGate.WaitAsync(linked.Token).ConfigureAwait(false);
            gateEntered = true;
            PendingJoyCon pending;
            lock (sync)
            {
                if (!running ||
                    !joyConPairCandidates.TryGetValue(candidateId,
                        out Switch2PersistentPeerId peerId) ||
                    !pendingJoyCons.TryGetValue(peerId, out pending) ||
                    pending.Model is not (Switch2ControllerModel.JoyCon2Left or
                        Switch2ControllerModel.JoyCon2Right) ||
                    !IsExactPendingNoLock(pending))
                {
                    return Switch2JoyConStandaloneActivationResult.Failed(
                        Switch2JoyConStandaloneActivationFailure.
                            InvalidCandidate);
                }
            }
            if (pending.SlotToken.IsValid)
                return Switch2JoyConStandaloneActivationResult.Success();
            return await ActivateStandaloneAsync(pending, linked.Token).
                ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Switch2JoyConStandaloneActivationResult.Failed(
                Switch2JoyConStandaloneActivationFailure.Cancelled);
        }
        finally
        {
            if (gateEntered)
            {
                connectionGate.Release();
            }
        }
    }

    internal async ValueTask<
        Switch2BluetoothWindowsAssociationResult> AssociateAsync(
            int candidateId, CancellationToken cancellationToken = default)
    {
        Task<Switch2BluetoothWindowsAssociationResult> task;
        bool reconnect = false;
        lock (sync)
        {
            if (!running || lifetimeCancellation == null ||
                !associationCandidates.Remove(candidateId, out var observation))
            {
                task = Task.FromResult(Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.
                        InvalidObservation));
            }
            else
            {
                reconnect = observation.Disposition ==
                    Switch2BluetoothObservationDisposition.RememberedThisHost;
                CancellationToken lifetimeToken = lifetimeCancellation.Token;
                // Publish explicit association work before Stop snapshots the
                // lifetime, just as remembered-device opens already do.
                task = Task.Run(async () =>
                {
                    using var linked = CancellationTokenSource.
                        CreateLinkedTokenSource(lifetimeToken, cancellationToken);
                    if (observation.Disposition ==
                        Switch2BluetoothObservationDisposition.RememberedThisHost)
                    {
                        return await OpenRememberedAsync(observation, linked.Token).
                            ConfigureAwait(false);
                    }
                    return await adapter.AssociateAsync(observation, linked.Token).
                        ConfigureAwait(false);
                }, CancellationToken.None);
                connectionTasks.Add(task);
            }
        }
        ReportDiagnostic(reconnect ? "Switch 2 Bluetooth reconnect requested." :
            "Switch 2 Bluetooth association requested.");
        _ = task.ContinueWith(RemoveCompletedTask,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            var result = await task.ConfigureAwait(false);
            ReportDiagnostic(reconnect ? (result.Succeeded ?
                "Switch 2 Bluetooth remembered controller reconnected without changing its association." :
                $"Switch 2 Bluetooth reconnect failed: {result.Failure}.") : result.Succeeded ?
                "Switch 2 Bluetooth association committed; waiting for this host's reconnect advertisement." :
                $"Switch 2 Bluetooth association failed: {result.Failure}; last completed command: {result.LastCompletedStep}.");
            return result;
        }
        catch (Exception exception)
        {
            ReportDiagnostic(
                $"Switch 2 Bluetooth association faulted: {exception.GetType().Name}.");
            throw;
        }
    }

    private void ReportDiagnostic(string message)
    {
        // Two lifecycle entries per explicit request, never report-rate logs,
        // addresses, packet contents, keys or platform exception messages.
        // Diagnostics run outside sync and cannot abort controller ownership.
        try { diagnostic?.Invoke(message); }
        catch { }
    }

    internal async ValueTask<bool> StopAsync(
        CancellationToken cancellationToken = default)
    {
        Task<bool> exactStop;
        lock (sync)
        {
            exactStop = BeginStopNoLock();
        }

        try
        {
            return await exactStop.WaitAsync(TimeSpan.FromMilliseconds(
                LifecycleTimeoutMilliseconds), cancellationToken).
                ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private Task<bool> BeginStopNoLock()
    {
        if (stopTask != null)
            return stopTask;
        if (lifetimeCancellation == null)
            return Task.FromResult(true);

        running = false;
        ulong generation = scanGeneration;
        scanGeneration = 0;
        CancellationTokenSource cancellation = lifetimeCancellation;
        associationCandidates.Clear();
        failedRememberedPeers.Clear();
        quarantinedRememberedPeers.Clear();
        activePeerRegistrations.Clear();
        Task[] pending = connectionTasks.ToArray();
        Task<ScanStartResult> start = scanStartCompletion.Task;
        // Caller cancellation/timeout bounds observation, not native resource
        // ownership. Publish once under sync before scheduling external work.
        return stopTask = Task.Run(() => CompleteStopAsync(cancellation,
            generation, pending, start), CancellationToken.None);
    }

    private async Task<bool> CompleteStopAsync(
        CancellationTokenSource cancellation, ulong generation, Task[] pending,
        Task<ScanStartResult> start)
    {
        bool clean = true;
        try
        {
            cancellation.Cancel();
        }
        catch
        {
            clean = false;
        }

        // These are resource-completion tasks, not previously timed-out
        // observers. A late completion can therefore satisfy a later Stop.
        // A watcher may still be inside platform creation when Stop closes
        // admission. Do not retire an absent scan and let that watcher escape.
        ScanStartResult startResult = await start.ConfigureAwait(false);
        Task<bool> scanStopped = startResult.Started ?
            adapter.EndScanAndDrainAsync(generation) :
            startResult.FailedCleanup;
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Cancellation retires connection work normally.
        }
        catch
        {
            clean = false;
        }

        bool gateEntered = false;
        try
        {
            await connectionGate.WaitAsync().ConfigureAwait(false);
            gateEntered = true;
            PendingJoyCon[] abandoned;
            lock (sync)
            {
                abandoned = new PendingJoyCon[pendingJoyCons.Count];
                pendingJoyCons.Values.CopyTo(abandoned, 0);
            }
            var releases = new Task<bool>[abandoned.Length];
            for (int index = 0; index < abandoned.Length; index++)
            {
                releases[index] = abandoned[index].Lease.HasReleasedResources ||
                    abandoned[index].RuntimeOwnerHoldsLease ?
                    Task.FromResult(true) : abandoned[index].Lease.BeginAndWaitForResourceReleaseAsync();
            }
            foreach (bool released in await Task.WhenAll(releases).
                         ConfigureAwait(false))
            {
                clean &= released;
            }
            clean &= await scanStopped.ConfigureAwait(false);
            if (clean)
            {
                lock (sync)
                {
                    connectionTasks.RemoveAll(static task => task.IsCompleted);
                    pendingJoyCons.Clear();
                    joyConPairCandidates.Clear();
                    joinedJoyCons.Clear();
                    registrationService.RuntimeRemoved -= OnJoyConRuntimeRemoved;
                    pairRecords = Array.Empty<Switch2JoyConPairRecord>();
                    lifetimeCancellation = null;
                }
                cancellation.Dispose();
            }
            return clean;
        }
        catch
        {
            // Ambiguous cleanup retains the lifetime and denies restart.
            return false;
        }
        finally
        {
            if (gateEntered)
            {
                connectionGate.Release();
            }
        }
    }

    private void OnCandidate(Switch2BluetoothCandidateObservation observation)
    {
        if (observation.Disposition ==
            Switch2BluetoothObservationDisposition.RequiresExplicitAssociation)
        {
            lock (sync)
            {
                if (!running || observation.ScanGeneration != scanGeneration)
                {
                    return;
                }
                int id = NextAssociationIdNoLock();
                associationCandidates[id] = observation;
            }
            diagnostic?.Invoke($"Switch 2 {observation.Model} is available for explicit association.");
            return;
        }
        if (observation.Disposition !=
            Switch2BluetoothObservationDisposition.RememberedThisHost)
        {
            return;
        }

        CancellationToken token;
        Task task;
        lock (sync)
        {
            if (!running || lifetimeCancellation == null ||
                observation.ScanGeneration != scanGeneration)
            {
                return;
            }
            token = lifetimeCancellation.Token;
            if (quarantinedRememberedPeers.Contains(observation.PeerToken))
                return;
            if (failedRememberedPeers.Contains(observation.PeerToken))
            {
                // Only the adapter can issue this fresh candidate after exact
                // cleanup. A failed controller remains visible without an
                // automatic reconnect loop or another association ceremony.
                associationCandidates[NextAssociationIdNoLock()] = observation;
                return;
            }
            failedRememberedPeers.Add(observation.PeerToken);
            task = Task.Run(() => OpenRememberedAsync(observation, token),
                CancellationToken.None);
            connectionTasks.Add(task);
        }
        _ = task.ContinueWith(RemoveCompletedTask,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<Switch2BluetoothWindowsAssociationResult> OpenRememberedAsync(
        Switch2BluetoothCandidateObservation observation,
        CancellationToken cancellationToken)
    {
        bool gateEntered = false;
        bool connected = false;
        Switch2BluetoothWindowsInputLease lease = null;
        try
        {
            await connectionGate.WaitAsync(cancellationToken).
                ConfigureAwait(false);
            gateEntered = true;
            lock (sync)
            {
                if (!running || observation.ScanGeneration != scanGeneration)
                {
                    return Switch2BluetoothWindowsAssociationResult.Failed(
                        Switch2BluetoothWindowsAssociationFailure.StaleScan);
                }
                if (quarantinedRememberedPeers.Contains(observation.PeerToken))
                    return Switch2BluetoothWindowsAssociationResult.Failed(
                        Switch2BluetoothWindowsAssociationFailure.CleanupAmbiguous);
            }
            if (TryFencePriorRegistration(observation))
            {
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.InvalidObservation);
            }

            Switch2BluetoothWindowsOpenResult open = await adapter.
                OpenRememberedDuplexAsync(observation, cancellationToken).
                ConfigureAwait(false);
            if (!open.Succeeded)
            {
                diagnostic?.Invoke($"Switch 2 {observation.Model} Bluetooth open rejected: {open.Failure}" +
                    (open.SensorFailure != Switch2BluetoothSensorInitializationFailure.None ?
                        $"/{open.SensorFailure}." : "."));
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.DeviceOpenFailed);
            }
            lease = open.Lease;
            if (lease.JoyConSensorsInitialized)
                ReportDiagnostic($"Switch 2 {observation.Model} Bluetooth motion and optical mouse sensor startup acknowledged.");
            ReportDiagnostic(lease.ThroughputOptimizedRequested ?
                "Switch 2 Bluetooth throughput preference accepted by Windows; " +
                "negotiated report interval is measured separately." :
                "Switch 2 Bluetooth throughput preference unavailable or not " +
                "accepted; continuing with Windows-selected connection parameters.");

            ulong deviceGeneration;
            ulong transportGeneration;
            lock (sync)
            {
                if (!running || observation.ScanGeneration != scanGeneration)
                {
                    return Switch2BluetoothWindowsAssociationResult.Failed(
                        Switch2BluetoothWindowsAssociationFailure.StaleScan);
                }
                deviceGeneration = NextPhysicalGenerationNoLock();
                transportGeneration = NextPhysicalGenerationNoLock();
            }
            Switch2BluetoothCalibrationReadResult calibrationRead =
                await lease.ReadCalibrationAsync(observation.Model,
                    deviceGeneration, cancellationToken).ConfigureAwait(false);
            if (!calibrationRead.Succeeded)
            {
                diagnostic?.Invoke(
                    $"Switch 2 {observation.Model} factory calibration read rejected: {calibrationRead.Failure}/{calibrationRead.CommandFailure}.");
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.RuntimePreparationRejected);
            }
            if (calibrationRead.OptionalUserCommandFailure !=
                Switch2BluetoothMemoryReadChannelFailure.None)
            {
                diagnostic?.Invoke(
                    $"Switch 2 {observation.Model} optional user calibration read was unavailable ({calibrationRead.OptionalUserCommandFailure}); validated factory calibration remains active.");
            }
            Switch2InputCalibrationSnapshot calibration = calibrationRead.
                Calibration;
            if ((calibration.Left.Status is
                    Switch2CalibrationAdoptionStatus.FallbackMalformed or
                    Switch2CalibrationAdoptionStatus.FallbackUnadoptable) ||
                (calibration.Right.Status is
                    Switch2CalibrationAdoptionStatus.FallbackMalformed or
                    Switch2CalibrationAdoptionStatus.FallbackUnadoptable))
            {
                diagnostic?.Invoke(
                    $"Switch 2 {observation.Model} factory calibration was present but not adoptable; bounded symmetric fallback is active.");
            }

            if (!lease.TryBindHdRumbleLifetime(observation.Model,
                    deviceGeneration, transportGeneration))
            {
                diagnostic?.Invoke($"Switch 2 {observation.Model} HD-rumble output binding rejected.");
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.RuntimePreparationRejected);
            }

            if (observation.Model is Switch2ControllerModel.JoyCon2Left or
                    Switch2ControllerModel.JoyCon2Right)
            {
                var pendingJoyCon = new PendingJoyCon(lease,
                    observation.Model, observation.ProductId,
                    observation.PeerToken, lease.PersistentPeerId,
                    deviceGeneration,
                    transportGeneration, calibration);
                if (!TryBufferJoyCon(pendingJoyCon,
                        out PendingJoyCon matchedLeft,
                        out PendingJoyCon matchedRight,
                        out Switch2JoyConPairRecord matchedRecord,
                        out bool automaticMatch))
                {
                    if (!pendingJoyCon.IsBuffered)
                    {
                        diagnostic?.Invoke(
                            $"Switch 2 {observation.Model} duplicate persistent peer was rejected.");
                        return Switch2BluetoothWindowsAssociationResult.Failed(
                            Switch2BluetoothWindowsAssociationFailure.RuntimePreparationRejected);
                    }
                    lease = null;
                    var standalone = await ActivateStandaloneAsync(pendingJoyCon,
                        cancellationToken).ConfigureAwait(false);
                    connected = standalone.Succeeded;
                    if (!connected)
                        lock (sync) { RemovePendingNoLock(pendingJoyCon); }
                    return connected ? Switch2BluetoothWindowsAssociationResult.Reconnected() :
                        Switch2BluetoothWindowsAssociationResult.Failed(
                            Switch2BluetoothWindowsAssociationFailure.SlotActivationRejected);
                }

                lease = null;
                Switch2JoyConPairActivationResult joined = await
                    ActivateJoinedAsync(matchedLeft, matchedRight,
                        matchedRecord, cancellationToken).ConfigureAwait(false);
                if (!joined.Succeeded)
                {
                    diagnostic?.Invoke(
                        $"Switch 2 {(automaticMatch ? "automatic" : "remembered")} Joy-Con pair activation rejected: {joined.Failure}.");
                }
                connected = joined.Succeeded;
                return connected ? Switch2BluetoothWindowsAssociationResult.Reconnected() :
                    Switch2BluetoothWindowsAssociationResult.Failed(
                        Switch2BluetoothWindowsAssociationFailure.SlotActivationRejected);
            }

            if (!Switch2BluetoothRuntimeOwner.TryCreate(lease.Admission,
                    lease, deviceGeneration, transportGeneration,
                    Stopwatch.Frequency, calibration, QueueCapacity,
                    LifecycleTimeoutMilliseconds,
                    out Switch2BluetoothRuntimeOwner owner,
                    out InputControllerRegistration registration,
                    out Switch2BluetoothRuntimeCreateFailure createFailure))
            {
                // A retained owner denotes uncertain cleanup and must remain
                // quarantined. A null owner leaves this method responsible for
                // the still-prepared Windows lease.
                if (owner == null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    lease = null;
                }
                diagnostic?.Invoke($"Switch 2 {observation.Model} runtime creation rejected: {createFailure.Kind}.");
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.RuntimePreparationRejected);
            }
            if (magnetometerCalibrationStore != null &&
                lease.PersistentPeerId.IsValid &&
                !owner.RuntimeDevice.TryBindMagnetometerCalibrationPersistence(
                    magnetometerCalibrationStore, lease.PersistentPeerId))
            {
                diagnostic?.Invoke($"Switch 2 {observation.Model} magnetometer calibration persistence could not bind; this connection will continue without persisted magnetic correction.");
            }
            if (gyroCalibrationStore != null &&
                lease.PersistentPeerId.IsValid &&
                !owner.RuntimeDevice.TryBindGyroCalibrationPersistence(
                    gyroCalibrationStore, lease.PersistentPeerId))
            {
                diagnostic?.Invoke($"Switch 2 {observation.Model} gyro calibration persistence could not bind; this connection will recalibrate in memory.");
            }
            if (rawStickCalibrationStore != null && lease.PersistentPeerId.IsValid &&
                !owner.RuntimeDevice.TryBindRawStickCalibrationPersistence(rawStickCalibrationStore, lease.PersistentPeerId))
            {
                diagnostic?.Invoke("Switch 2 Pro local stick calibration could not bind; source calibration remains active.");
            }
            lease = null; // The exact runtime owner now owns the lease.

            bool activationCancelled;
            lock (sync)
            {
                activationCancelled = !running ||
                    observation.ScanGeneration != scanGeneration;
            }
            if (activationCancelled)
            {
                bool aborted = owner.TryAbortCreated(registration,
                    LifecycleTimeoutMilliseconds, out var abortFailure);
                diagnostic?.Invoke($"Switch 2 {observation.Model} activation was cancelled; cleanup={(aborted ? "complete" : abortFailure.ToString())}.");
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.Cancelled);
            }

            if (!registrationService.TryAttachToHost(owner, slotHost,
                    LifecycleTimeoutMilliseconds,
                    out InputControllerSlotToken token,
                    out Switch2RuntimeRegistrationTransactionFailure failure))
            {
                string cleanup = CleanupAfterAttachRejection(owner.State,
                    owner.RequiresQuarantine || failure.RequiresQuarantine);
                if (cleanup == null)
                {
                    bool aborted = owner.TryAbortCreated(registration,
                        LifecycleTimeoutMilliseconds, out var abortFailure);
                    cleanup = aborted ? "complete" : abortFailure.ToString();
                }
                if (cleanup != "complete" && cleanup != "transaction-complete")
                {
                    // Physical GATT release can precede an uncertain external
                    // slot rollback. A fresh advertisement during that gap is
                    // not authority to overlap the quarantined host lifetime.
                    FenceUncertainPeer(observation.PeerToken, token);
                }
                ReportDiagnostic(DescribeAttachRejection(
                    $"Switch 2 {observation.Model}", failure, cleanup));
                return Switch2BluetoothWindowsAssociationResult.Failed(
                    Switch2BluetoothWindowsAssociationFailure.SlotActivationRejected);
            }

            TrackActivePeer(observation.PeerToken, token);
            connected = true;
            attached?.Invoke(token);
            diagnostic?.Invoke($"Switch 2 {observation.Model} connected in controller slot {token.Slot + 1}.");
            return Switch2BluetoothWindowsAssociationResult.Reconnected();
        }
        catch (OperationCanceledException)
        {
            return Switch2BluetoothWindowsAssociationResult.Failed(
                Switch2BluetoothWindowsAssociationFailure.Cancelled);
        }
        catch (Exception exception)
        {
            diagnostic?.Invoke($"Switch 2 Bluetooth activation failed: {exception.GetType().Name}.");
            return Switch2BluetoothWindowsAssociationResult.Failed(
                Switch2BluetoothWindowsAssociationFailure.RuntimePreparationRejected);
        }
        finally
        {
            if (connected)
            {
                lock (sync)
                {
                    failedRememberedPeers.Remove(observation.PeerToken);
                }
            }
            if (lease != null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            if (gateEntered)
            {
                connectionGate.Release();
            }
        }
    }

    private void RemoveCompletedTask(Task completed)
    {
        lock (sync)
        {
            connectionTasks.Remove(completed);
        }
    }

    // A failed registration transaction may already have proved abort and
    // rolled the slot back. That owner intentionally no longer authenticates
    // new operations. Do not turn its successful cleanup into a second,
    // misleading InvalidRegistration failure or retry quarantined ownership.
    // Null means the still-Created caller-owned lifetime needs its first abort.
    internal static string CleanupAfterAttachRejection(
        Switch2BluetoothRuntimeOwnerState state, bool requiresQuarantine)
    {
        if (requiresQuarantine) return "quarantined";
        return state switch
        {
            Switch2BluetoothRuntimeOwnerState.Created => null,
            Switch2BluetoothRuntimeOwnerState.AbortedUnpublished or
                Switch2BluetoothRuntimeOwnerState.Removed => "transaction-complete",
            _ => $"retained-{state}",
        };
    }

    private string DescribeAttachRejection(string controller,
        in Switch2RuntimeRegistrationTransactionFailure failure,
        string cleanup)
    {
        var participant = failure.Participant as
            Switch2ControlServiceSlotRegistrationParticipant;
        Switch2ControlServiceSlotHostResult hostResult =
            participant?.LastHostResult ?? default;
        string phase = slotHost is
            Switch2ControlServiceReversibleProfileSlotHost productionHost ?
            productionHost.LastPreparePhase : "unavailable";
        var result = failure.ParticipantResult;
        return $"{controller} slot activation rejected: " +
            $"{failure.Kind}/{failure.TableFailure}; cleanup={cleanup}; " +
            $"participant={result.Operation}/{result.Outcome}/{result.FailureKind}; " +
            $"slotHost={hostResult.Operation}/{hostResult.Outcome}/{hostResult.FailureKind}; " +
            $"slotPrepare={phase}.";
    }

    private bool TryFencePriorRegistration(
        in Switch2BluetoothCandidateObservation observation)
    {
        InputControllerSlotToken prior;
        lock (sync)
        {
            if (!activePeerRegistrations.TryGetValue(observation.PeerToken,
                    out prior))
            {
                return false;
            }
        }

        bool present = false;
        bool quarantined = false;
        try
        {
            InputControllerSlotSnapshot[] snapshots =
                registrationService.Table.GetSnapshot();
            for (int index = 0; index < snapshots.Length; index++)
            {
                if (snapshots[index].Token != prior ||
                    snapshots[index].State is InputControllerSlotState.Empty or
                        InputControllerSlotState.Removed)
                {
                    continue;
                }
                present = true;
                quarantined = snapshots[index].State ==
                    InputControllerSlotState.Quarantined;
                break;
            }
        }
        catch
        {
            // An uninspectable predecessor cannot safely overlap a successor.
            _ = adapter.TryRejectRememberedInputCandidate(observation);
            return true;
        }

        if (!present)
        {
            lock (sync)
            {
                if (activePeerRegistrations.TryGetValue(
                        observation.PeerToken, out InputControllerSlotToken
                            exact) && exact == prior)
                {
                    activePeerRegistrations.Remove(observation.PeerToken);
                }
            }
            return false;
        }

        if (quarantined)
        {
            _ = adapter.TryRejectRememberedInputCandidate(observation);
            return true;
        }

        // Preserve the private address capability, but require a later
        // advertisement after the previous exact slot/profile/output lifetime
        // finishes removal. No timer or retry enters the input hot path.
        lock (sync)
        {
            // This is normal predecessor drainage, not a failed connection.
            // Keep ordinary automatic reconnect once that slot retires.
            failedRememberedPeers.Remove(observation.PeerToken);
        }
        _ = adapter.TryDeferRememberedInputCandidate(observation);
        return true;
    }

    private void TrackActivePeer(Switch2BluetoothPeerToken peerToken,
        in InputControllerSlotToken token)
    {
        if (!peerToken.IsValid || !token.IsValid)
        {
            return;
        }
        lock (sync)
        {
            if (running && peerToken.IsForScanGeneration(scanGeneration))
            {
                activePeerRegistrations[peerToken] = token;
            }
        }
    }

    private void FenceUncertainPeer(Switch2BluetoothPeerToken peerToken,
        in InputControllerSlotToken token)
    {
        TrackActivePeer(peerToken, token);
        lock (sync)
        {
            if (!running || !peerToken.IsForScanGeneration(scanGeneration)) return;
            quarantinedRememberedPeers.Add(peerToken);
            var staleChoices = new List<int>();
            foreach (var choice in associationCandidates)
                if (choice.Value.PeerToken == peerToken)
                    staleChoices.Add(choice.Key);
            foreach (int id in staleChoices)
                associationCandidates.Remove(id);
        }
    }

    private bool TryBufferJoyCon(PendingJoyCon pending,
        out PendingJoyCon matchedLeft, out PendingJoyCon matchedRight,
        out Switch2JoyConPairRecord matchedRecord,
        out bool automaticMatch)
    {
        matchedLeft = null;
        matchedRight = null;
        matchedRecord = default;
        automaticMatch = false;
        lock (sync)
        {
            if (!running || pending == null || !pending.PeerId.IsValid ||
                pendingJoyCons.ContainsKey(pending.PeerId))
            {
                return false;
            }

            pending.CandidateId = NextJoyConCandidateIdNoLock();
            if (pending.ArrivalOrdinal == 0)
                pending.ArrivalOrdinal = NextJoyConArrivalOrdinalNoLock();
            pending.IsBuffered = true;
            pendingJoyCons.Add(pending.PeerId, pending);
            joyConPairCandidates.Add(pending.CandidateId, pending.PeerId);

            if (IsAutomaticPairingEnabled())
            {
                if (TryClaimAutomaticPairNoLock(out matchedLeft,
                        out matchedRight, out matchedRecord))
                {
                    automaticMatch = true;
                    return true;
                }
                // Automatic mode is intentionally arrival-based. Do not let
                // an older explicit record pin surviving halves back to a
                // historical partner while magnet pairing is enabled.
                return false;
            }

            foreach (Switch2JoyConPairRecord record in pairRecords)
            {
                if (!pendingJoyCons.TryGetValue(record.LeftPeerId,
                        out PendingJoyCon left) ||
                    !pendingJoyCons.TryGetValue(record.RightPeerId,
                        out PendingJoyCon right) ||
                    left.Model != Switch2ControllerModel.JoyCon2Left ||
                    right.Model != Switch2ControllerModel.JoyCon2Right)
                {
                    continue;
                }

                RemovePendingNoLock(left);
                RemovePendingNoLock(right);
                matchedLeft = left;
                matchedRight = right;
                matchedRecord = record;
                return true;
            }
            return false;
        }
    }

    private bool TryClaimAutomaticPairNoLock(out PendingJoyCon left,
        out PendingJoyCon right, out Switch2JoyConPairRecord record)
    {
        left = null;
        right = null;
        record = default;
        if (joyConPairCandidates.Count < 2)
        {
            return false;
        }

        var candidates = new Switch2JoyConPairCandidate[
            joyConPairCandidates.Count];
        int index = 0;
        foreach (KeyValuePair<int, Switch2PersistentPeerId> item in
                 joyConPairCandidates)
        {
            if (pendingJoyCons.TryGetValue(item.Value,
                    out PendingJoyCon pending))
            {
                candidates[index++] = new Switch2JoyConPairCandidate(
                    item.Key, pending.Model, pending.ArrivalOrdinal);
            }
        }
        if (index != candidates.Length)
        {
            Array.Resize(ref candidates, index);
        }
        if (!Switch2JoyConAutomaticPairingPolicy.
                TrySelectOldestCompatiblePair(candidates,
                    out int leftCandidateId, out int rightCandidateId) ||
            !joyConPairCandidates.TryGetValue(leftCandidateId,
                out Switch2PersistentPeerId leftPeerId) ||
            !joyConPairCandidates.TryGetValue(rightCandidateId,
                out Switch2PersistentPeerId rightPeerId) ||
            !pendingJoyCons.TryGetValue(leftPeerId, out left) ||
            !pendingJoyCons.TryGetValue(rightPeerId, out right) ||
            left.Model != Switch2ControllerModel.JoyCon2Left ||
            right.Model != Switch2ControllerModel.JoyCon2Right ||
            !Switch2JoyConPairRecord.TryCreate(1,
                Switch2JoyConPairId.CreateRandom(), left.PeerId,
                right.PeerId, out record))
        {
            left = null;
            right = null;
            record = default;
            return false;
        }

        RemovePendingNoLock(left);
        RemovePendingNoLock(right);
        return true;
    }

    private async ValueTask<Switch2JoyConPairActivationResult>
        ActivateJoinedAsync(PendingJoyCon left, PendingJoyCon right,
            Switch2JoyConPairRecord record,
            CancellationToken cancellationToken, int preferredCandidateId = 0)
    {
        // Only the first selected (or oldest active automatic) controller may
        // donate its output. The handle reserves the exact output, never a
        // merely unbound slot that another controller could steal.
        PendingJoyCon preferred = preferredCandidateId == right?.CandidateId ? right :
            preferredCandidateId == left?.CandidateId ? left :
            left?.SlotToken.IsValid == true && right?.SlotToken.IsValid == true ?
                (left.ArrivalOrdinal <= right.ArrivalOrdinal ? left : right) :
            left?.SlotToken.IsValid == true ? left : right;
        try
        {
            using ISwitch2JoyConOutputHandoff handoff = preferred?.SlotToken.IsValid == true ?
                beginOutputHandoff?.Invoke(preferred.SlotToken) : null;
            return await ActivateJoinedCoreAsync(left, right, record, cancellationToken, handoff).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DisposePendingPairAsync(left, right).ConfigureAwait(false);
            return Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.Cancelled);
        }
        catch (Exception exception)
        {
            await DisposePendingPairAsync(left, right).ConfigureAwait(false);
            ReportDiagnostic($"Switch 2 Joy-Con transition failed: {exception.GetType().Name}.");
            return Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.RuntimeRejected);
        }
        finally
        {
            RestoreClaimedActiveJoyCon(left);
            RestoreClaimedActiveJoyCon(right);
        }
    }

    private void RestoreClaimedActiveJoyCon(PendingJoyCon half)
    {
        if (half?.SlotToken.IsValid != true) return;
        var snapshot = registrationService.Table.GetSnapshot()[half.SlotToken.Slot];
        if (snapshot.Token != half.SlotToken || snapshot.State != InputControllerSlotState.Attached) return;
        lock (sync)
        {
            if (!running || quarantinedRememberedPeers.Contains(half.PeerToken)) return;
            half.IsBuffered = true;
            pendingJoyCons[half.PeerId] = half;
            joyConPairCandidates[half.CandidateId] = half.PeerId;
        }
    }

    private async ValueTask<Switch2JoyConPairActivationResult>
        ActivateJoinedCoreAsync(PendingJoyCon left, PendingJoyCon right,
            Switch2JoyConPairRecord record, CancellationToken cancellationToken,
            ISwitch2JoyConOutputHandoff handoff)
    {
        // Claiming candidates removes them from the UI, not from the host.
        // Retire exact standalone tokens before issuing fresh pair admissions.
        if (left == null || right == null ||
            !await PrepareJoyConForJoinAsync(left, cancellationToken).ConfigureAwait(false) ||
            !await PrepareJoyConForJoinAsync(right, cancellationToken).ConfigureAwait(false))
        {
            await DisposePendingPairAsync(left, right).ConfigureAwait(false);
            return Switch2JoyConPairActivationResult.Failed(
                Switch2JoyConPairActivationFailure.RuntimeRejected);
        }
        if (left == null || right == null || !record.IsValid ||
            !Switch2JoyConPairConnectionAdmission.TryCreate(record,
                left.PeerId, left.Lease.Admission, right.PeerId,
                right.Lease.Admission,
                out Switch2JoyConPairConnectionAdmission admission))
        {
            await DisposePendingPairAsync(left, right).ConfigureAwait(false);
            return Switch2JoyConPairActivationResult.Failed(
                Switch2JoyConPairActivationFailure.InvalidCandidate);
        }

        ulong runtimeGeneration;
        ulong pairEpoch;
        lock (sync)
        {
            if (!running || record.Revision == 0)
            {
                runtimeGeneration = 0;
                pairEpoch = 0;
            }
            else
            {
                runtimeGeneration = NextPhysicalGenerationNoLock();
                pairEpoch = NextPhysicalGenerationNoLock();
            }
        }
        if (runtimeGeneration == 0 || pairEpoch == 0)
        {
            await DisposePendingPairAsync(left, right).ConfigureAwait(false);
            return Switch2JoyConPairActivationResult.Failed(
                Switch2JoyConPairActivationFailure.Cancelled);
        }

        if (!Switch2JoyConJoinedRuntimeOwner.TryCreate(admission,
                left.Lease, right.Lease, runtimeGeneration, pairEpoch,
                left.DeviceGeneration, left.TransportGeneration,
                left.Calibration, QueueCapacity, right.DeviceGeneration,
                right.TransportGeneration, right.Calibration, QueueCapacity,
                Stopwatch.Frequency, new Switch2JoyConPairPolicy(10_000),
                LifecycleTimeoutMilliseconds,
                out Switch2JoyConJoinedRuntimeOwner owner,
                out InputControllerRegistration registration,
                out Switch2JoyConJoinedRuntimeCreateFailure createFailure))
        {
            left.RuntimeOwnerHoldsLease = right.RuntimeOwnerHoldsLease = createFailure.RequiresQuarantine;
            if (!createFailure.RequiresQuarantine)
            {
                await DisposePendingPairAsync(left, right).
                    ConfigureAwait(false);
            }
            return Switch2JoyConPairActivationResult.Failed(
                Switch2JoyConPairActivationFailure.RuntimeRejected);
        }
        left.RuntimeOwnerHoldsLease = right.RuntimeOwnerHoldsLease = true;
        if (magnetometerCalibrationStore != null &&
            !owner.RuntimeDevice.TryBindMagnetometerCalibrationPersistence(
                magnetometerCalibrationStore, left.PeerId, right.PeerId))
        {
            diagnostic?.Invoke("Switch 2 joined Joy-Con magnetometer calibration persistence could not bind; this pair will continue without persisted magnetic correction.");
        }
        if (rawStickCalibrationStore != null &&
            !owner.RuntimeDevice.TryBindRawStickCalibrationPersistence(rawStickCalibrationStore, left.PeerId, right.PeerId))
        {
            diagnostic?.Invoke("Switch 2 joined Joy-Con local stick calibration could not bind; source calibration remains active.");
        }
        if (gyroCalibrationStore != null &&
            !owner.RuntimeDevice.TryBindGyroCalibrationPersistence(
                gyroCalibrationStore, left.PeerId, right.PeerId))
        {
            diagnostic?.Invoke("Switch 2 joined Joy-Con gyro calibration persistence could not bind; this pair will recalibrate in memory.");
        }

        bool cancelled;
        lock (sync)
        {
            cancelled = !running || admission.ScanGeneration != scanGeneration;
        }
        if (cancelled || cancellationToken.IsCancellationRequested)
        {
            owner.TryAbortCreated(registration, LifecycleTimeoutMilliseconds,
                out _);
            return Switch2JoyConPairActivationResult.Failed(
                Switch2JoyConPairActivationFailure.Cancelled);
        }

        InputControllerSlotToken token;
        Switch2RuntimeRegistrationTransactionFailure failure;
        try { handoff?.PrepareSuccessor(owner.RuntimeDevice); }
        catch
        {
            owner.TryAbortCreated(registration, LifecycleTimeoutMilliseconds, out _);
            throw;
        }
        bool activated = handoff != null ?
            registrationService.TryAttachExactSlot(handoff.InputSlot, owner, slotHost,
                LifecycleTimeoutMilliseconds, out token, out failure) :
            registrationService.TryAttachToHost(owner, slotHost,
                LifecycleTimeoutMilliseconds, out token, out failure);
        if (!activated)
        {
            string cleanup = CleanupAfterAttachRejection(owner.State,
                owner.RequiresQuarantine || failure.RequiresQuarantine);
            if (cleanup == null)
            {
                bool aborted = owner.TryAbortCreated(registration,
                    LifecycleTimeoutMilliseconds, out var abortFailure);
                cleanup = aborted ? "complete" : abortFailure.ToString();
            }
            if (cleanup != "complete" && cleanup != "transaction-complete")
            {
                FenceUncertainPeer(left.PeerToken, token);
                FenceUncertainPeer(right.PeerToken, token);
            }
            ReportDiagnostic(DescribeAttachRejection(
                "Switch 2 joined Joy-Con", failure, cleanup));
            return Switch2JoyConPairActivationResult.Failed(
                Switch2JoyConPairActivationFailure.ActivationRejected);
        }

        TrackActivePeer(left.PeerToken, token);
        TrackActivePeer(right.PeerToken, token);
        lock (sync) { joinedJoyCons[token] = (left, right); }
        ReconcileEarlyJoyConRemoval(token);
        attached?.Invoke(token);
        diagnostic?.Invoke(
            $"Switch 2 Joy-Con pair connected in controller slot {token.Slot + 1}.");
        return Switch2JoyConPairActivationResult.Success();
    }

    private async ValueTask<Switch2JoyConStandaloneActivationResult>
        ActivateStandaloneAsync(PendingJoyCon pending,
            CancellationToken cancellationToken, ISwitch2JoyConOutputHandoff handoff = null)
    {
        if (pending == null || cancellationToken.IsCancellationRequested)
        {
            if (pending != null)
            {
                await pending.Lease.DisposeAsync().ConfigureAwait(false);
            }
            return Switch2JoyConStandaloneActivationResult.Failed(
                Switch2JoyConStandaloneActivationFailure.Cancelled);
        }

        if (!Switch2BluetoothRuntimeOwner.TryCreate(
                pending.Lease.Admission, pending.Lease,
                pending.DeviceGeneration, pending.TransportGeneration,
                Stopwatch.Frequency, pending.Calibration, QueueCapacity,
                LifecycleTimeoutMilliseconds,
                out Switch2BluetoothRuntimeOwner owner,
                out InputControllerRegistration registration,
                out Switch2BluetoothRuntimeCreateFailure createFailure))
        {
            pending.RuntimeOwnerHoldsLease = owner != null;
            if (owner == null)
            {
                await pending.Lease.DisposeAsync().ConfigureAwait(false);
            }
            diagnostic?.Invoke(
                $"Switch 2 {pending.Model} standalone runtime creation rejected: {createFailure.Kind}.");
            return Switch2JoyConStandaloneActivationResult.Failed(
                Switch2JoyConStandaloneActivationFailure.RuntimeRejected);
        }
        pending.RuntimeOwnerHoldsLease = true;
        if (magnetometerCalibrationStore != null)
        {
            bool left = pending.Model == Switch2ControllerModel.JoyCon2Left;
            bool bound = owner.RuntimeDevice.
                TryBindMagnetometerCalibrationPersistence(
                    magnetometerCalibrationStore,
                    left ? pending.PeerId : default,
                    left ? default : pending.PeerId);
            if (!bound)
            {
                diagnostic?.Invoke($"Switch 2 {pending.Model} magnetometer calibration persistence could not bind; this connection will continue without persisted magnetic correction.");
            }
        }
        if (joyConHoldModeStore != null &&
            !owner.RuntimeDevice.TryBindJoyConHoldModePersistence(
                joyConHoldModeStore, pending.PeerId))
        {
            diagnostic?.Invoke($"Switch 2 {pending.Model} hold-mode persistence could not bind; this connection will use the active profile default.");
        }
        if (rawStickCalibrationStore != null)
        {
            bool leftSide = pending.Model == Switch2ControllerModel.JoyCon2Left;
            if (!owner.RuntimeDevice.TryBindRawStickCalibrationPersistence(rawStickCalibrationStore,
                    leftSide ? pending.PeerId : default, leftSide ? default : pending.PeerId))
            {
                diagnostic?.Invoke("Switch 2 standalone Joy-Con local stick calibration could not bind; source calibration remains active.");
            }
        }
        if (gyroCalibrationStore != null)
        {
            bool left = pending.Model == Switch2ControllerModel.JoyCon2Left;
            bool bound = owner.RuntimeDevice.
                TryBindGyroCalibrationPersistence(gyroCalibrationStore,
                    left ? pending.PeerId : default,
                    left ? default : pending.PeerId);
            if (!bound)
            {
                diagnostic?.Invoke($"Switch 2 {pending.Model} gyro calibration persistence could not bind; this connection will recalibrate in memory.");
            }
        }

        bool cancelled;
        lock (sync)
        {
            cancelled = !running || pending.Lease.Admission.ScanGeneration !=
                scanGeneration;
        }
        if (cancelled || cancellationToken.IsCancellationRequested)
        {
            owner.TryAbortCreated(registration, LifecycleTimeoutMilliseconds,
                out _);
            return Switch2JoyConStandaloneActivationResult.Failed(
                Switch2JoyConStandaloneActivationFailure.Cancelled);
        }

        InputControllerSlotToken token;
        Switch2RuntimeRegistrationTransactionFailure failure;
        try { handoff?.PrepareSuccessor(owner.RuntimeDevice); }
        catch
        {
            owner.TryAbortCreated(registration, LifecycleTimeoutMilliseconds, out _);
            throw;
        }
        bool activated = handoff != null ?
            registrationService.TryAttachExactSlot(handoff.InputSlot, owner, slotHost,
                LifecycleTimeoutMilliseconds, out token, out failure) :
            registrationService.TryAttachToHost(owner, slotHost,
                LifecycleTimeoutMilliseconds, out token, out failure);
        if (!activated)
        {
            string cleanup = CleanupAfterAttachRejection(owner.State,
                owner.RequiresQuarantine || failure.RequiresQuarantine);
            if (cleanup == null)
            {
                bool aborted = owner.TryAbortCreated(registration,
                    LifecycleTimeoutMilliseconds, out var abortFailure);
                cleanup = aborted ? "complete" : abortFailure.ToString();
            }
            if (cleanup != "complete" && cleanup != "transaction-complete")
                FenceUncertainPeer(pending.PeerToken, token);
            ReportDiagnostic(DescribeAttachRejection(
                $"Switch 2 {pending.Model} standalone", failure, cleanup));
            return Switch2JoyConStandaloneActivationResult.Failed(
                Switch2JoyConStandaloneActivationFailure.ActivationRejected);
        }

        TrackActivePeer(pending.PeerToken, token);
        lock (sync) { pending.SlotToken = token; }
        ReconcileEarlyJoyConRemoval(token);
        attached?.Invoke(token);
        diagnostic?.Invoke(
            $"Switch 2 {pending.Model} connected separately in controller slot {token.Slot + 1}.");
        return Switch2JoyConStandaloneActivationResult.Success();
    }

    private static async ValueTask DisposePendingPairAsync(PendingJoyCon left,
        PendingJoyCon right)
    {
        if (left != null && !left.RuntimeOwnerHoldsLease)
        {
            await left.Lease.DisposeAsync().ConfigureAwait(false);
        }
        if (right != null && !right.RuntimeOwnerHoldsLease && !ReferenceEquals(left, right))
        {
            await right.Lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool RemovePendingNoLock(PendingJoyCon pending)
    {
        if (!IsExactPendingNoLock(pending))
        {
            return false;
        }
        pendingJoyCons.Remove(pending.PeerId);
        joyConPairCandidates.Remove(pending.CandidateId);
        pending.IsBuffered = false;
        return true;
    }

    private bool IsExactPendingNoLock(PendingJoyCon pending) =>
        pending != null && pendingJoyCons.TryGetValue(pending.PeerId,
            out PendingJoyCon exact) && ReferenceEquals(exact, pending);

    private static Switch2JoyConPairRecord[] AppendPairRecord(
        Switch2JoyConPairRecord[] records, Switch2JoyConPairRecord record)
    {
        var next = new Switch2JoyConPairRecord[records.Length + 1];
        records.CopyTo(next, 0);
        next[^1] = record;
        return next;
    }

    private ulong NextPhysicalGenerationNoLock()
    {
        nextPhysicalGeneration++;
        if (nextPhysicalGeneration == 0)
        {
            nextPhysicalGeneration++;
        }
        return nextPhysicalGeneration;
    }

    private int NextAssociationIdNoLock()
    {
        do
        {
            nextAssociationId = nextAssociationId == int.MaxValue ? 1 :
                nextAssociationId + 1;
        }
        while (associationCandidates.ContainsKey(nextAssociationId));
        return nextAssociationId;
    }

    private int NextJoyConCandidateIdNoLock()
    {
        do
        {
            nextJoyConCandidateId = nextJoyConCandidateId == int.MaxValue ? 1 :
                nextJoyConCandidateId + 1;
        }
        while (joyConPairCandidates.ContainsKey(nextJoyConCandidateId));
        return nextJoyConCandidateId;
    }

    private ulong NextJoyConArrivalOrdinalNoLock()
    {
        nextJoyConArrivalOrdinal++;
        if (nextJoyConArrivalOrdinal == 0)
        {
            nextJoyConArrivalOrdinal++;
        }
        return nextJoyConArrivalOrdinal;
    }

    private bool IsAutomaticPairingEnabled()
    {
        try
        {
            return automaticPairingEnabled?.Invoke() == true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class PendingJoyCon
    {
        internal PendingJoyCon(Switch2BluetoothWindowsInputLease lease,
            Switch2ControllerModel model, ushort productId,
            Switch2BluetoothPeerToken peerToken,
            Switch2PersistentPeerId peerId, ulong deviceGeneration,
            ulong transportGeneration,
            in Switch2InputCalibrationSnapshot calibration)
        {
            Lease = lease ?? throw new ArgumentNullException(nameof(lease));
            Model = model;
            ProductId = productId;
            PeerToken = peerToken;
            PeerId = peerId;
            DeviceGeneration = deviceGeneration;
            TransportGeneration = transportGeneration;
            Calibration = calibration;
        }

        internal Switch2BluetoothWindowsInputLease Lease { get; set; }
        internal Switch2ControllerModel Model { get; }
        internal ushort ProductId { get; }
        internal Switch2BluetoothPeerToken PeerToken { get; }
        internal Switch2PersistentPeerId PeerId { get; }
        internal ulong DeviceGeneration { get; set; }
        internal ulong TransportGeneration { get; set; }
        internal Switch2InputCalibrationSnapshot Calibration { get; set; }
        internal InputControllerSlotToken SlotToken { get; set; }
        internal bool RuntimeOwnerHoldsLease { get; set; }
        internal int CandidateId { get; set; }
        internal ulong ArrivalOrdinal { get; set; }
        internal bool IsBuffered { get; set; }
    }
}
