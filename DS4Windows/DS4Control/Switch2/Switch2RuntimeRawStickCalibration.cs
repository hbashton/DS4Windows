/*
DS4Windows
Copyright (C) 2026 hbashton
This program is free software under the GNU General Public License, version 3
or (at your option) any later version. See LICENSE for details.
*/

using System;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.InputDevices;

namespace DS4Windows.Switch2;

internal enum Switch2RawStickCalibrationCommitResult : byte
{
    None,
    AppliedAndStored,
    InvalidOperation,
    NotReady,
    Busy,
    StorageFailed,
    StoredNotApplied,
}

/// <summary>Opaque, exact-operation receipt. All mutation belongs to the runtime gate.</summary>
internal sealed class Switch2RawStickCalibrationOperation
{
    internal Switch2RawStickCalibrationOperation(Switch2RawStickCalibrationBinding basis,
        in Switch2RawStickObservation observation, Switch2StickSide side,
        Switch2PersistentPeerId peer, bool reset, int slot, long profileRevision, CancellationToken cancellationToken)
    {
        Basis = basis; Model = observation.Descriptor.Identity.Model;
        Side = side; Peer = peer; Reset = reset; Slot = slot; ProfileRevision = profileRevision;
        CancellationToken = cancellationToken;
        Collector = reset ? null : new Switch2RawStickCalibrationCollector(observation.Descriptor, peer, side);
    }

    internal Switch2RawStickCalibrationBinding Basis { get; }
    internal Switch2ControllerModel Model { get; }
    internal Switch2StickSide Side { get; }
    internal Switch2PersistentPeerId Peer { get; }
    internal bool Reset { get; }
    internal int Slot { get; }
    internal long ProfileRevision { get; }
    internal CancellationToken CancellationToken { get; }
    internal Switch2RawStickCalibrationCollector Collector { get; }
    internal bool Saving { get; set; }
    // Read by the cold store sequencer without taking publicationGate.
    internal volatile bool Cancelled;
}

internal readonly record struct Switch2RawStickCalibrationProgress(
    Switch2RawStickCalibrationStage Stage, double RotationProgress,
    double StationaryProgress, bool Saving, bool Reset);

public sealed partial class Switch2RuntimeInputDevice
{
    private Switch2RawStickObservation rawStickLastLeft, rawStickLastRight;
    private Switch2RawStickCalibrationOperation rawStickOperation;
    // Extends across cold I/O and final adoption. A second save/reset is refused
    // rather than allowing disk writes and binding swaps to complete out of order.
    private bool rawStickMutationInProgress;

    internal bool TryBeginRawStickCalibration(Switch2StickSide side, bool reset,
        out Switch2RawStickCalibrationOperation operation, CancellationToken cancellationToken = default)
    {
        ReportHandler<EventArgs>[] subscribers;
        lock (publicationGate)
        {
            operation = null;
            if (rawStickOperation != null && !IsCurrentRawStickOperationNoLock(rawStickOperation))
                CancelRawStickCalibrationNoLock();
            var observed = side == Switch2StickSide.Left || DeviceType == InputDeviceType.Switch2Pro ?
                rawStickLastLeft : rawStickLastRight;
            if (cancellationToken.IsCancellationRequested || runtimeState != Switch2RuntimeInputDeviceState.Active || terminalNeutralReserved ||
                publicationInProgress || rawStickOperation != null || rawStickMutationInProgress ||
                proMotionProjection.IsMagnetometerCalibrationActive ||
                joyConMotionProjection.IsMagnetometerCalibrationActive ||
                rawStickCalibration == null || !observed.IsValid ||
                !observed.TryGetStick(side, default, out _) ||
                !rawStickCalibration.TryGetPeer(observed.Descriptor.Identity.Model, side, out var peer)) return false;

            operation = new Switch2RawStickCalibrationOperation(rawStickCalibration, observed,
                side, peer, reset, DeviceSlotNumber, ReadRawStickProfileRevisionNoLock(), cancellationToken);
            rawStickOperation = operation;
            // Setters serialize here too. Clear is nonblocking with respect to
            // external mouse output; its presentation fence runs below.
            highRateMousePresenter.ClearSources();
            // Release held mapped controls immediately through the ordinary
            // report/mapper seam; no terminal epoch or physical output is created.
            neutralState.CopyTo(stagingState);
            stagingHasMotion = false;
            if (!TryReserveStagingNoLock(out subscribers))
            {
                CancelRawStickCalibrationNoLock();
                operation = null;
                return false;
            }
        }
        var pending = operation;
        // Registration and disposal are outside publicationGate. Cancellation
        // can revoke capture while Report/output is blocked. The token remains
        // in the receipt after Begin returns, covering a delayed UI handoff.
        using var cancellation = cancellationToken.Register(() => CancelRawStickCalibration(pending));
        bool published = InvokeAndCommitPublication(subscribers, isTerminalNeutral: false);
        bool fenced = highRateMousePresenter.FencePresentation(cancellationToken);
        lock (publicationGate)
        {
            if (published && fenced && IsCurrentRawStickOperationNoLock(operation)) return true;
            if (ReferenceEquals(rawStickOperation, operation)) CancelRawStickCalibrationNoLock();
            operation = null;
            return false;
        }
    }

    internal bool TryGetRawStickCalibrationProgress(Switch2RawStickCalibrationOperation operation,
        out Switch2RawStickCalibrationProgress progress)
    {
        lock (publicationGate)
        {
            progress = default;
            if (!IsCurrentRawStickOperationNoLock(operation))
            {
                if (operation != null && ReferenceEquals(rawStickOperation, operation))
                    CancelRawStickCalibrationNoLock();
                return false;
            }
            progress = new Switch2RawStickCalibrationProgress(
                operation.Collector?.Stage ?? Switch2RawStickCalibrationStage.Ready,
                operation.Collector?.RotationProgress ?? 1,
                operation.Collector?.StationaryProgress ?? 1, operation.Saving, operation.Reset);
            return true;
        }
    }

    internal bool CancelRawStickCalibration(Switch2RawStickCalibrationOperation operation)
    {
        lock (publicationGate)
        {
            if (operation == null || !ReferenceEquals(rawStickOperation, operation)) return false;
            CancelRawStickCalibrationNoLock();
            return true;
        }
    }

    /// <summary>
    /// Explicit PC-side persistence only. The worker performs file I/O outside
    /// publicationGate. A successful write whose operation/lifetime has ended
    /// is reported separately and cannot activate any successor runtime.
    /// </summary>
    internal Task<Switch2RawStickCalibrationCommitResult> CompleteRawStickCalibrationAsync(
        Switch2RawStickCalibrationOperation operation) => Task.Run(() =>
            CompleteRawStickCalibration(operation));

    private Switch2RawStickCalibrationCommitResult CompleteRawStickCalibration(
        Switch2RawStickCalibrationOperation operation)
    {
        Switch2StickCalibration? value = null;
        Switch2RawStickCalibrationBinding updated;
        lock (publicationGate)
        {
            if (!IsCurrentRawStickOperationNoLock(operation))
                return Switch2RawStickCalibrationCommitResult.InvalidOperation;
            if (rawStickMutationInProgress) return Switch2RawStickCalibrationCommitResult.Busy;
            if (!operation.Reset)
            {
                if (!operation.Collector.TryGetResult(out var captured))
                    return Switch2RawStickCalibrationCommitResult.NotReady;
                value = captured;
            }
            if (!operation.Basis.TryWithCalibration(operation.Model, operation.Side, value, out updated))
                return Switch2RawStickCalibrationCommitResult.InvalidOperation;
            rawStickMutationInProgress = operation.Saving = true;
        }

        try
        {
            bool stored;
            try
            {
                var store = operation.Basis.Store;
                lock (store.SerializationGate)
                {
                    // A queued, cancelled predecessor must not overwrite a
                    // successor. Already-entered I/O finishes before any later
                    // runtime load/save/reset on the same backing store.
                    if (operation.Cancelled) return Switch2RawStickCalibrationCommitResult.InvalidOperation;
                    // Slot/profile changes need not have triggered a report or
                    // cancellation callback while this worker waited. Recheck
                    // them before entering I/O; never retain this lock for I/O.
                    lock (publicationGate)
                        if (!IsCurrentRawStickOperationNoLock(operation))
                            return Switch2RawStickCalibrationCommitResult.InvalidOperation;
                    stored = operation.Reset ?
                        store.TryRemove(operation.Peer, operation.Model, operation.Side) :
                        store.TryStore(operation.Peer, operation.Model, operation.Side, value.Value);
                }
            }
            catch { stored = false; }
            if (!stored) return Switch2RawStickCalibrationCommitResult.StorageFailed;

            // Already-reserved publications must finish before live adoption.
            // Wait releases the gate; no cold I/O or UI callback holds it.
            long deadline = Environment.TickCount64 + 1_000;
            lock (publicationGate)
            {
                while (publicationInProgress && IsCurrentRawStickOperationNoLock(operation))
                {
                    int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                    if (remaining == 0 || !Monitor.Wait(publicationGate, remaining))
                        return Switch2RawStickCalibrationCommitResult.StoredNotApplied;
                }
                if (!IsCurrentRawStickOperationNoLock(operation))
                    return Switch2RawStickCalibrationCommitResult.StoredNotApplied;
                Volatile.Write(ref rawStickCalibration, updated);
                rawStickOperation = null;
                return Switch2RawStickCalibrationCommitResult.AppliedAndStored;
            }
        }
        finally
        {
            lock (publicationGate)
            {
                rawStickMutationInProgress = operation.Saving = false;
                Monitor.PulseAll(publicationGate);
            }
        }
    }

    private void ObserveRawStickCalibrationNoLock(in Switch2RawStickObservation observation)
    {
        if (!observation.IsValid) return;
        if (observation.Descriptor.Identity.Model == Switch2ControllerModel.JoyCon2Right)
            rawStickLastRight = observation;
        else
            rawStickLastLeft = observation;
        if (rawStickOperation == null) return;
        if (!IsCurrentRawStickOperationNoLock(rawStickOperation))
        {
            CancelRawStickCalibrationNoLock();
            return;
        }
        // A joined publication repeats the unchanged half. Duplicate timestamps
        // are benign ignored samples; they never become a publication failure.
        rawStickOperation.Collector?.TryObserveRaw(observation);
    }

    private bool IsCurrentRawStickOperationNoLock(Switch2RawStickCalibrationOperation operation) =>
        operation != null && ReferenceEquals(rawStickOperation, operation) && !operation.Cancelled &&
        !operation.CancellationToken.IsCancellationRequested &&
        ReferenceEquals(rawStickCalibration, operation.Basis) &&
        runtimeState == Switch2RuntimeInputDeviceState.Active && !terminalNeutralReserved &&
        DeviceSlotNumber == operation.Slot && ReadRawStickProfileRevisionNoLock() == operation.ProfileRevision;

    private long ReadRawStickProfileRevisionNoLock() =>
        Math.Max(0, Global.ReadProfileSwitchRevision(DeviceSlotNumber));

    private void CancelRawStickCalibrationNoLock()
    {
        if (rawStickOperation == null) return;
        rawStickOperation.Cancelled = true;
        rawStickOperation.Collector?.Cancel();
        rawStickOperation = null;
    }
}
