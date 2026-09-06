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

/// <summary>
/// Production composition owner for Switch 2 Pro USB. Discovery runs only on
/// this control-plane worker. Each admitted controller receives one exact
/// MI_00/MI_01 owned composite, one startup transaction, one runtime input
/// owner, and one canonical feedback writer. The report and feedback hot paths
/// contain no discovery, polling, or additional queue.
/// </summary>
internal sealed class Switch2ProUsbProductionCoordinator
{
    private const int ScanIntervalMilliseconds = 1_000;
    private const int LifecycleTimeoutMilliseconds = 5_000;
    private const int OutputOperationWaitMilliseconds = 1_000;

    private readonly object gate = new();
    private readonly ISwitch2ProUsbWindowsOwnedCompositePlatform discovery;
    private readonly ISwitch2ProUsbOwnedCompositeNativeAdapter opener;
    private readonly Switch2RuntimeRegistrationService registrations;
    private readonly ISwitch2ControlServiceSlotHost host;
    private readonly Action<InputControllerSlotToken> attached;
    private readonly Action<string> diagnostic;
    private readonly int scanIntervalMilliseconds;
    private readonly Switch2PersistentPeerIdentityDeriver identityDeriver;
    private readonly ISwitch2MagnetometerCalibrationStore
        magnetometerCalibrationStore;
    private readonly ISwitch2GyroCalibrationStore gyroCalibrationStore;
    private readonly ISwitch2RawStickCalibrationStore rawStickCalibrationStore;
    private readonly HashSet<Switch2PhysicalInputRegistration> attempted =
        new();
    private readonly Dictionary<Switch2PhysicalInputRegistration,
        InputControllerSlotToken> active = new();
    private readonly List<object> retainedTerminalAttention = new();

    private CancellationTokenSource cancellation;
    private Task worker;
    private ulong serviceGeneration;
    private long generationCounter = DateTime.UtcNow.Ticks;

    internal Switch2ProUsbProductionCoordinator(
        Switch2RuntimeRegistrationService registrations,
        ISwitch2ControlServiceSlotHost host,
        Action<InputControllerSlotToken> attached,
        Action<string> diagnostic) : this(
        new Switch2ProUsbWindowsNativePlatform(),
        new Switch2ProUsbWindowsOwnedCompositeAdapter(), registrations, host,
        attached, diagnostic, ScanIntervalMilliseconds, null, null)
    {
    }

    internal Switch2ProUsbProductionCoordinator(
        Switch2RuntimeRegistrationService registrations,
        ISwitch2ControlServiceSlotHost host,
        Action<InputControllerSlotToken> attached,
        Action<string> diagnostic,
        Switch2PersistentPeerIdentityDeriver identityDeriver,
        ISwitch2MagnetometerCalibrationStore magnetometerCalibrationStore,
        ISwitch2GyroCalibrationStore gyroCalibrationStore = null,
        ISwitch2RawStickCalibrationStore rawStickCalibrationStore = null) :
        this(new Switch2ProUsbWindowsNativePlatform(),
            new Switch2ProUsbWindowsOwnedCompositeAdapter(), registrations,
            host, attached, diagnostic, ScanIntervalMilliseconds,
            identityDeriver, magnetometerCalibrationStore,
            gyroCalibrationStore, rawStickCalibrationStore)
    {
    }

    internal Switch2ProUsbProductionCoordinator(
        ISwitch2ProUsbWindowsOwnedCompositePlatform discovery,
        ISwitch2ProUsbOwnedCompositeNativeAdapter opener,
        Switch2RuntimeRegistrationService registrations,
        ISwitch2ControlServiceSlotHost host,
        Action<InputControllerSlotToken> attached,
        Action<string> diagnostic,
        int scanIntervalMilliseconds = ScanIntervalMilliseconds,
        Switch2PersistentPeerIdentityDeriver identityDeriver = null,
        ISwitch2MagnetometerCalibrationStore
            magnetometerCalibrationStore = null,
        ISwitch2GyroCalibrationStore gyroCalibrationStore = null,
        ISwitch2RawStickCalibrationStore rawStickCalibrationStore = null)
    {
        this.discovery = discovery ?? throw new ArgumentNullException(
            nameof(discovery));
        this.opener = opener ?? throw new ArgumentNullException(nameof(opener));
        this.registrations = registrations ?? throw new ArgumentNullException(
            nameof(registrations));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.attached = attached;
        this.diagnostic = diagnostic;
        this.identityDeriver = identityDeriver;
        this.magnetometerCalibrationStore = magnetometerCalibrationStore;
        this.gyroCalibrationStore = gyroCalibrationStore;
        this.rawStickCalibrationStore = rawStickCalibrationStore;
        if (scanIntervalMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scanIntervalMilliseconds));
        }
        this.scanIntervalMilliseconds = scanIntervalMilliseconds;
    }

    internal bool TryStart(ulong exactServiceGeneration)
    {
        if (exactServiceGeneration == 0)
        {
            return false;
        }
        lock (gate)
        {
            if (worker != null || cancellation != null ||
                serviceGeneration != 0)
            {
                return false;
            }
            serviceGeneration = exactServiceGeneration;
            cancellation = new CancellationTokenSource();
            worker = Task.Run(() => RunAsync(cancellation.Token));
            return true;
        }
    }

    internal async ValueTask<bool> StopAsync()
    {
        Task exactWorker;
        CancellationTokenSource exactCancellation;
        lock (gate)
        {
            exactWorker = worker;
            exactCancellation = cancellation;
            if (exactWorker == null || exactCancellation == null)
            {
                return serviceGeneration == 0;
            }
            exactCancellation.Cancel();
        }

        try
        {
            await exactWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Exact attached lifetimes are still retired below through the
            // shared registration transaction.
        }

        InputControllerSlotToken[] tokens;
        lock (gate)
        {
            tokens = new InputControllerSlotToken[active.Count];
            active.Values.CopyTo(tokens, 0);
        }
        bool complete = true;
        for (int index = 0; index < tokens.Length; index++)
        {
            if (!registrations.TryRemove(tokens[index],
                    LifecycleTimeoutMilliseconds, out var failure) &&
                IsTokenStillPresent(tokens[index]))
            {
                complete = false;
                Switch2RuntimeRegistrationParticipantResult result =
                    failure.ParticipantResult;
                var slotParticipant = failure.Participant as
                    Switch2ControlServiceSlotRegistrationParticipant;
                var ownedParticipant = slotParticipant?.InnerParticipant as
                    Switch2ProUsbOwnedCompositeRegistrationParticipant;
                Switch2ProUsbOwnedFeedbackQuiescenceResult feedbackResult =
                    ownedParticipant?.LastFeedbackQuiescenceResult ?? default;
                Switch2ProUsbRuntimeStopFailure inputStop =
                    ownedParticipant?.LastInputStopFailure ?? default;
                Diagnostic($"Switch 2 Pro USB retirement requires retry: " +
                    $"{failure.Kind}/{failure.TableFailure}; " +
                    $"participant={result.Operation}/{result.Outcome}/" +
                    $"{result.FailureKind}; owner={result.OwnerFailure}; " +
                    $"quarantine={failure.QuarantineReason}/" +
                    $"{result.QuarantineReason}; " +
                    $"ownedStop={ownedParticipant?.LastStopPhase ?? "none"}; " +
                    $"feedback={feedbackResult.Outcome}; " +
                    $"startup={ownedParticipant?.LastStartupRetirementFailure.ToString() ?? "none"}; " +
                    $"command=" +
                    $"{ownedParticipant?.LastCommandRetirementDiagnostic ?? "none"}; " +
                    $"input={inputStop.Kind}/{inputStop.PumpFailure}/" +
                    $"{inputStop.DisposeFailure}/" +
                    $"quarantine={inputStop.RequiresQuarantine}; " +
                    $"host={slotParticipant?.LastHostResult.Operation.ToString() ?? "none"}/" +
                    $"{slotParticipant?.LastHostResult.Outcome.ToString() ?? "none"}/" +
                    $"{slotParticipant?.LastHostResult.FailureKind.ToString() ?? "none"}.");
            }
        }

        lock (gate)
        {
            PruneInactiveNoLock(registrations.Table.GetSnapshot());
            complete &= active.Count == 0;
            if (complete)
            {
                worker = null;
                cancellation = null;
                serviceGeneration = 0;
                attempted.Clear();
                exactCancellation.Dispose();
            }
        }
        return complete;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ScanOnce(token);
            await Task.Delay(scanIntervalMilliseconds, token).
                ConfigureAwait(false);
        }
    }

    private void ScanOnce(CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        IReadOnlyList<Switch2ProUsbWindowsCandidate> candidates;
        bool discovered;
        try
        {
            discovered = discovery.TryDiscoverCandidates(out candidates);
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException exception)
        {
            Retain(exception.RetainedOwner ?? exception);
            Diagnostic("Switch 2 Pro USB discovery entered terminal " +
                "attention after ambiguous native cleanup.");
            return;
        }
        catch (Exception exception)
        {
            Retain(exception);
            Diagnostic("Switch 2 Pro USB discovery failed closed.");
            return;
        }

        // A failed/incomplete discovery is not evidence that every device left.
        if (!discovered || candidates == null || token.IsCancellationRequested)
        {
            return;
        }
        var present = new HashSet<Switch2PhysicalInputRegistration>();
        for (int index = 0; index < candidates.Count; index++)
        {
            Switch2ProUsbWindowsCandidate candidate = candidates[index];
            if (candidate != null && candidate.TryGetAdmittedRegistration(
                    out Switch2PhysicalInputRegistration registration))
            {
                present.Add(registration);
            }
        }

        InputControllerSlotToken[] missing;
        lock (gate)
        {
            PruneInactiveNoLock(registrations.Table.GetSnapshot());
            missing = FindMissingTokens(active, present);
            attempted.RemoveWhere(registration =>
                !present.Contains(registration) &&
                !active.ContainsKey(registration));
        }
        if (!discovered || token.IsCancellationRequested)
        {
            return;
        }

        // Discovery only requests retirement. The exact participant still
        // owns native-removal evidence, I/O draining and virtual neutralization.
        for (int index = 0; index < missing.Length && !token.IsCancellationRequested; index++)
        {
            if (registrations.TryRemove(missing[index], LifecycleTimeoutMilliseconds, out _))
            {
                Diagnostic("Switch 2 Pro USB disconnected; its exact runtime was retired.");
            }
        }
        lock (gate)
        {
            PruneInactiveNoLock(registrations.Table.GetSnapshot());
        }

        for (int index = 0; index < candidates.Count &&
                !token.IsCancellationRequested; index++)
        {
            TryAttach(candidates[index]);
        }
    }

    internal static InputControllerSlotToken[] FindMissingTokens(
        IReadOnlyDictionary<Switch2PhysicalInputRegistration, InputControllerSlotToken> attachedDevices,
        ISet<Switch2PhysicalInputRegistration> present)
    {
        var missing = new List<InputControllerSlotToken>();
        foreach (var pair in attachedDevices)
        {
            if (!present.Contains(pair.Key))
            {
                missing.Add(pair.Value);
            }
        }
        return missing.ToArray();
    }

    private void TryAttach(Switch2ProUsbWindowsCandidate candidate)
    {
        if (candidate == null || !candidate.TryGetAdmittedRegistration(
                out Switch2PhysicalInputRegistration registration))
        {
            return;
        }
        lock (gate)
        {
            if (serviceGeneration == 0 || active.ContainsKey(registration) ||
                !attempted.Add(registration))
            {
                return;
            }
        }

        if (!TryNextGeneration(out ulong deviceGeneration) ||
            !TryNextGeneration(out ulong transportGeneration) ||
            !Switch2PhysicalInputLifetime.TryCreate(registration,
                deviceGeneration, transportGeneration, Stopwatch.Frequency,
                out Switch2PhysicalInputLifetime lifetime))
        {
            RearmProvenUnownedAttempt(registration);
            Diagnostic("Switch 2 Pro USB generation allocation failed " +
                "closed.");
            return;
        }

        ISwitch2ProUsbOwnedCompositeLease lease = null;
        Switch2ProUsbOwnedCompositeLeaseBundle bundle = null;
        Switch2ProUsbOwnedFeedbackActivationLifetime feedback = null;
        Switch2ProUsbOwnedCompositeRegistrationParticipant participant = null;
        try
        {
            bool opened = opener.TryOpenOwnedComposite(registration, lifetime,
                out lease);
            if (!opened || lease == null)
            {
                if (lease == null)
                {
                    // No physical capability escaped, so a later discovery
                    // pass may safely retry a transient open failure.
                    RearmProvenUnownedAttempt(registration);
                }
                else
                {
                    // A malformed opener result still escaped ownership. Do
                    // not reacquire the same physical composite.
                    Retain(lease);
                }
                Diagnostic("Switch 2 Pro USB could not acquire its exact " +
                    "MI_00/MI_01 owned composite.");
                return;
            }
            if (!Switch2ProUsbOwnedCompositeLeaseBundle.TryAdmit(lease,
                    lifetime, out bundle, out var admissionFailure))
            {
                Retain(lease);
                Diagnostic($"Switch 2 Pro USB composite admission failed: " +
                    $"{admissionFailure}.");
                return;
            }
            if (!bundle.TryTakeAuthority(
                    out Switch2ProUsbOwnedCompositeAuthority authority))
            {
                Retain(bundle);
                Diagnostic("Switch 2 Pro USB composite authority was " +
                    "already consumed.");
                return;
            }
            Switch2ProUsbStartupTransaction completedStartup = null;
            Switch2InputCalibrationSnapshot calibration;
            if (bundle.TryGetCalibrationLease(authority,
                    out ISwitch2ProUsbCalibrationCommandLease
                        calibrationLease))
            {
                Switch2ProUsbStartupCreateFailure startupCreateFailure =
                    default;
                Switch2ProUsbStartupAdvanceResult startupAdvance = default;
                bool startupCreated = Switch2ProUsbStartupTransaction.
                    TryCreate(calibrationLease, lifetime,
                        out completedStartup, out startupCreateFailure);
                if (!startupCreated || !TryCompleteStartup(completedStartup,
                        LifecycleTimeoutMilliseconds,
                        lease.MaximumOutputOperationMilliseconds,
                        out startupAdvance))
                {
                    Retain(bundle);
                    Diagnostic("Switch 2 Pro USB pre-calibration startup " +
                        $"failed closed: {startupCreateFailure}/" +
                        $"{startupAdvance.CommandFailure}/" +
                        $"{startupAdvance.RetirementFailure}.");
                    return;
                }
                if (!Switch2ProUsbCalibrationTransaction.TryCreate(
                        calibrationLease, lifetime, completedStartup,
                        out var calibrationTransaction,
                        out var calibrationCreateFailure))
                {
                    Retain(bundle);
                    Diagnostic("Switch 2 Pro USB calibration transaction " +
                        $"creation failed closed: " +
                        $"{calibrationCreateFailure}.");
                    return;
                }
                if (!calibrationTransaction.TryRead(
                        LifecycleTimeoutMilliseconds,
                        out var calibrationRead))
                {
                    if (!calibrationRead.CanUseCenteredFallback ||
                        !Switch2InputCalibrationSnapshot.
                            TryCreateProUsbCenteredFallback(deviceGeneration,
                                out calibration))
                    {
                        Retain(bundle);
                        Diagnostic("Switch 2 Pro USB calibration read " +
                            $"failed closed: {calibrationRead.Failure}/" +
                            $"{calibrationRead.RetirementFailure}.");
                        return;
                    }
                    Diagnostic("Switch 2 Pro USB calibration command was " +
                        "proven unconsumed; using the centered wired " +
                        "fallback for this lifetime.");
                }
                else
                {
                    calibration = calibrationRead.Calibration;
                }
            }
            else if (!Switch2InputCalibrationSnapshot.
                    TryCreateProUsbCenteredFallback(deviceGeneration,
                        out calibration))
            {
                Retain(bundle);
                Diagnostic("Switch 2 Pro USB centered fallback calibration " +
                    "creation failed closed.");
                return;
            }
            if (!Switch2ProUsbOwnedFeedbackActivationLifetime.TryCreate(
                    bundle, authority, OutputOperationWaitMilliseconds,
                    out feedback, out var feedbackFailure))
            {
                Retain(feedbackFailure.RetainedBundle ?? (object)bundle);
                Diagnostic($"Switch 2 Pro USB feedback creation failed: " +
                    $"{feedbackFailure.Failure}.");
                return;
            }
            Switch2ProUsbOwnedCompositeParticipantCreateFailure
                createFailure;
            bool participantCreated = completedStartup == null ?
                Switch2ProUsbOwnedCompositeRegistrationParticipant.TryCreate(
                    bundle, authority, calibration, feedback,
                    LifecycleTimeoutMilliseconds, out participant,
                    out createFailure) :
                Switch2ProUsbOwnedCompositeRegistrationParticipant.
                    TryCreateWithCompletedStartup(bundle, authority,
                        calibration, feedback, LifecycleTimeoutMilliseconds,
                        completedStartup, out participant,
                        out createFailure);
            if (!participantCreated)
            {
                Retain(createFailure.RetainedFeedbackLifetime ??
                    (object)createFailure.RetainedRuntimeOwner ??
                    createFailure.RetainedBundle ?? bundle);
                Diagnostic($"Switch 2 Pro USB runtime composition failed: " +
                    $"{createFailure.Kind}.");
                return;
            }
            if (identityDeriver != null &&
                (magnetometerCalibrationStore != null ||
                 gyroCalibrationStore != null || rawStickCalibrationStore != null) &&
                identityDeriver.TryDerive(registration.ContainerIdentity,
                    Switch2ControllerModel.ProController2,
                    Switch2InputProtocolIdentity.ProController2UsbProductId,
                    out Switch2PersistentPeerId peerId))
            {
                if (magnetometerCalibrationStore != null &&
                    !participant.RuntimeOwner.RuntimeInputDevice.
                        TryBindMagnetometerCalibrationPersistence(
                            magnetometerCalibrationStore, peerId))
                {
                    Diagnostic("Switch 2 Pro USB magnetometer calibration persistence could not bind; this connection will continue without persisted magnetic correction.");
                }
                if (rawStickCalibrationStore != null &&
                    !participant.RuntimeOwner.RuntimeInputDevice.TryBindRawStickCalibrationPersistence(
                        rawStickCalibrationStore, peerId))
                {
                    Diagnostic("Switch 2 Pro USB local stick calibration could not bind; source calibration remains active.");
                }
                if (gyroCalibrationStore != null &&
                    !participant.RuntimeOwner.RuntimeInputDevice.
                        TryBindGyroCalibrationPersistence(
                            gyroCalibrationStore, peerId))
                {
                    Diagnostic("Switch 2 Pro USB gyro calibration persistence could not bind; this connection will recalibrate in memory.");
                }
            }
            if (!registrations.TryAttachOwnedUsb(participant, host,
                    LifecycleTimeoutMilliseconds,
                    out InputControllerSlotToken token,
                    out var attachFailure))
            {
                if (attachFailure.RequiresQuarantine)
                {
                    Retain(participant);
                }
                Switch2RuntimeRegistrationParticipantResult participantResult =
                    attachFailure.ParticipantResult;
                Switch2RuntimeRegistrationParticipantResult originalResult =
                    attachFailure.OriginalParticipantResult;
                var slotParticipant = attachFailure.Participant as
                    Switch2ControlServiceSlotRegistrationParticipant;
                Switch2ControlServiceSlotHostResult slotHostResult =
                    slotParticipant?.LastHostResult ?? default;
                string slotHostPrepare = host is
                        Switch2ControlServiceReversibleProfileSlotHost
                            productionHost ?
                    productionHost.LastPreparePhase : "unavailable";
                Diagnostic($"Switch 2 Pro USB registration failed: " +
                    $"{attachFailure.Kind}/{attachFailure.TableFailure}; " +
                    $"original={originalResult.Operation}/" +
                    $"{originalResult.Outcome}/" +
                    $"{originalResult.FailureKind}; " +
                    $"participant={participantResult.Operation}/" +
                    $"{participantResult.Outcome}/" +
                    $"{participantResult.FailureKind}; " +
                    $"owner={attachFailure.OwnerFailure}/" +
                    $"{participantResult.OwnerFailure}; " +
                    $"quarantine={attachFailure.QuarantineReason}/" +
                    $"{participantResult.QuarantineReason}; " +
                    $"inputPrepare={participant.LastInputPrepareFailure}/" +
                    $"{participant.LastInputPrepareExceptionType ?? "none"}/" +
                    $"{participant.LastInputPrepareProofShape}; " +
                    $"innerException=" +
                    $"{participant.FirstInnerInvocationExceptionType ?? "none"}; " +
                    $"innerResult={participant.FirstInvalidInnerResultShape}; " +
                    $"ownerPrepare=" +
                    $"{participant.LastInputOwnerPrepareDiagnostic}; " +
                    $"ownedPrepare={participant.LastPreparePhase}; " +
                    $"slotHost={slotHostResult.Operation}/" +
                    $"{slotHostResult.Outcome}/" +
                    $"{slotHostResult.FailureKind}; " +
                    $"slotPrepare={slotHostPrepare}.");
                return;
            }

            lock (gate)
            {
                active[registration] = token;
            }
            attached?.Invoke(token);
            Diagnostic("Switch 2 Pro USB attached through the exact " +
                "full-duplex runtime.");
        }
        catch (Switch2ProUsbWindowsCleanupAmbiguousException exception)
        {
            Retain(exception.RetainedOwner ?? exception);
            Diagnostic("Switch 2 Pro USB composition entered terminal " +
                "attention after ambiguous native cleanup.");
        }
        catch (Exception)
        {
            object retained = participant;
            retained ??= feedback;
            retained ??= bundle;
            retained ??= lease;
            if (retained == null)
            {
                RearmProvenUnownedAttempt(registration);
                Diagnostic("Switch 2 Pro USB composition threw before any " +
                    "physical capability escaped; discovery may retry.");
            }
            else
            {
                Retain(retained);
                Diagnostic("Switch 2 Pro USB composition threw and was " +
                    "retained for terminal attention.");
            }
        }
    }

    private bool TryNextGeneration(out ulong generation)
    {
        long value = Interlocked.Increment(ref generationCounter);
        if (value <= 0)
        {
            generation = 0;
            return false;
        }
        generation = (ulong)value;
        return true;
    }

    private static bool TryCompleteStartup(
        Switch2ProUsbStartupTransaction startup, int timeoutMilliseconds,
        int maximumOperationMilliseconds,
        out Switch2ProUsbStartupAdvanceResult lastResult)
    {
        lastResult = default;
        if (startup == null || timeoutMilliseconds <= 0 ||
            maximumOperationMilliseconds <= 0)
        {
            return false;
        }

        long deadline = Stopwatch.GetTimestamp() + (long)Math.Ceiling(
            timeoutMilliseconds * (double)Stopwatch.Frequency / 1_000d);
        for (int index = 0;
                index < Switch2ProUsbStartupTransaction.RequiredStepCount &&
                startup.State !=
                    Switch2ProUsbStartupTransactionState.Completed; index++)
        {
            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            int remaining = remainingTicks <= 0 ? 0 : Math.Max(1,
                (int)Math.Ceiling(remainingTicks * 1_000d /
                    Stopwatch.Frequency));
            if (remaining <= 0)
            {
                return false;
            }
            int commandTimeout = Math.Min(maximumOperationMilliseconds,
                Math.Max(1, remaining / 2));
            int retirementTimeout = Math.Min(maximumOperationMilliseconds,
                Math.Max(0, remaining - commandTimeout));
            if (!startup.TryAdvance(commandTimeout, retirementTimeout,
                    out lastResult))
            {
                return false;
            }
        }

        return startup.State ==
            Switch2ProUsbStartupTransactionState.Completed;
    }

    private bool IsTokenStillPresent(in InputControllerSlotToken token)
    {
        InputControllerSlotSnapshot[] snapshots =
            registrations.Table.GetSnapshot();
        for (int index = 0; index < snapshots.Length; index++)
        {
            if (snapshots[index].Token == token &&
                snapshots[index].State is not (
                    InputControllerSlotState.Empty or
                    InputControllerSlotState.Removed))
            {
                return true;
            }
        }
        return false;
    }

    private void PruneInactiveNoLock(
        InputControllerSlotSnapshot[] snapshots)
    {
        var retained = new HashSet<InputControllerSlotToken>();
        if (snapshots != null)
        {
            for (int index = 0; index < snapshots.Length; index++)
            {
                if (snapshots[index].State is not (
                        InputControllerSlotState.Empty or
                        InputControllerSlotState.Removed) &&
                    snapshots[index].Token.IsValid)
                {
                    retained.Add(snapshots[index].Token);
                }
            }
        }

        var stale = new List<Switch2PhysicalInputRegistration>();
        foreach (KeyValuePair<Switch2PhysicalInputRegistration,
                     InputControllerSlotToken> pair in active)
        {
            if (!retained.Contains(pair.Value))
            {
                stale.Add(pair.Key);
            }
        }
        for (int index = 0; index < stale.Count; index++)
        {
            active.Remove(stale[index]);
            // A token can disappear from the shared table only after its
            // exact participant has completed removal or was never published.
            // Quarantined/live entries remain in the snapshot and stay fenced.
            attempted.Remove(stale[index]);
        }
    }

    private void RearmProvenUnownedAttempt(
        in Switch2PhysicalInputRegistration registration)
    {
        lock (gate)
        {
            if (!active.ContainsKey(registration))
            {
                attempted.Remove(registration);
            }
        }
    }

    private void Retain(object owner)
    {
        if (owner == null)
        {
            return;
        }
        lock (gate)
        {
            retainedTerminalAttention.Add(owner);
        }
    }

    private void Diagnostic(string message)
    {
        try
        {
            diagnostic?.Invoke(message);
        }
        catch
        {
        }
    }
}
