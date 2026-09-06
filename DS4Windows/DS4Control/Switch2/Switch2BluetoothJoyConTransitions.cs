using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

internal sealed partial class Switch2BluetoothProductionCoordinator
{
    private readonly Dictionary<InputControllerSlotToken, (PendingJoyCon Left, PendingJoyCon Right)>
        joinedJoyCons = new();
    // At most one notification per slot index. Closes the race where physical
    // removal completes between TryAttachToHost returning and local tracking.
    private readonly Dictionary<int, InputControllerSlotToken> lastRemovedJoyConSlots = new();

    private void ReconcileEarlyJoyConRemoval(InputControllerSlotToken token)
    {
        bool removed;
        lock (sync) { removed = lastRemovedJoyConSlots.TryGetValue(token.Slot, out var prior) && prior == token; }
        if (removed) OnJoyConRuntimeRemoved(token);
    }

    private async ValueTask<bool> PrepareJoyConForJoinAsync(PendingJoyCon half,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (half.SlotToken.IsValid)
        {
            // This proves host rollback/output neutral as well as native BLE
            // release. A slot number or a completed Dispose call is not proof.
            if (!registrationService.TryRemove(half.SlotToken,
                    LifecycleTimeoutMilliseconds, out var failure))
            {
                FenceUncertainPeer(half.PeerToken, half.SlotToken);
                ReportDiagnostic($"Switch 2 Joy-Con pairing deferred: exact standalone retirement rejected ({failure.Kind}).");
                return false;
            }
            half.SlotToken = default;
            half.RuntimeOwnerHoldsLease = false;
            return await ReopenJoyConAsync(half, cancellationToken).ConfigureAwait(false);
        }
        return !half.Lease.HasReleasedResources;
    }

    private async ValueTask<bool> ReopenJoyConAsync(PendingJoyCon half,
        CancellationToken cancellationToken)
    {
        var reopened = await adapter.ReopenReleasedJoyConAsync(half.Lease,
            cancellationToken).ConfigureAwait(false);
        if (!reopened.Succeeded)
        {
            ReportDiagnostic($"Switch 2 Joy-Con transition reopen rejected: {reopened.Failure}.");
            return false;
        }
        half.Lease = reopened.Lease;
        half.RuntimeOwnerHoldsLease = false;
        lock (sync)
        {
            half.DeviceGeneration = NextPhysicalGenerationNoLock();
            half.TransportGeneration = NextPhysicalGenerationNoLock();
        }
        var calibration = await half.Lease.ReadCalibrationAsync(half.Model,
            half.DeviceGeneration, cancellationToken).ConfigureAwait(false);
        if (!calibration.Succeeded || !half.Lease.TryBindHdRumbleLifetime(half.Model,
                half.DeviceGeneration, half.TransportGeneration)) return false;
        half.Calibration = calibration.Calibration;
        return true;
    }

    private void OnJoyConRuntimeRemoved(InputControllerSlotToken token)
    {
        Task recovery = null;
        lock (sync)
        {
            lastRemovedJoyConSlots[token.Slot] = token;
            // Matching the full slot token prevents late callbacks from
            // removing a successor that happens to reuse the same slot index.
            var removed = new List<PendingJoyCon>();
            foreach (var half in pendingJoyCons.Values)
                if (half.SlotToken == token) removed.Add(half);
            foreach (var half in removed) RemovePendingNoLock(half);

            if (!joinedJoyCons.Remove(token, out var pair) || !running ||
                lifetimeCancellation == null) return;
            // Only an actual one-sided physical disconnect restores a survivor.
            // Stop, user Disconnect and ambiguous/dual loss must not reconnect.
            PendingJoyCon survivor = pair.Left.Lease.HasDisconnected && !pair.Right.Lease.HasDisconnected ? pair.Right :
                pair.Right.Lease.HasDisconnected && !pair.Left.Lease.HasDisconnected ? pair.Left : null;
            if (survivor == null) return;
            survivor.RuntimeOwnerHoldsLease = false;
            CancellationToken lifetime = lifetimeCancellation.Token;
            recovery = Task.Run(() => RestoreJoyConSurvivorAsync(survivor, lifetime));
            connectionTasks.Add(recovery);
        }
        _ = recovery.ContinueWith(RemoveCompletedTask, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RestoreJoyConSurvivorAsync(PendingJoyCon half, CancellationToken cancellationToken)
    {
        bool entered = false;
        bool handedOff = false;
        try
        {
            await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (!await ReopenJoyConAsync(half, cancellationToken).ConfigureAwait(false)) return;
            if (TryBufferJoyCon(half, out var left, out var right, out var record, out _))
            {
                handedOff = true;
                await ActivateJoinedAsync(left, right, record, cancellationToken).ConfigureAwait(false);
            }
            else if (half.IsBuffered)
            {
                handedOff = (await ActivateStandaloneAsync(half, cancellationToken).ConfigureAwait(false)).Succeeded;
                if (!handedOff) lock (sync) { RemovePendingNoLock(half); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ReportDiagnostic($"Switch 2 Joy-Con survivor recovery failed: {exception.GetType().Name}.");
        }
        finally
        {
            if (!handedOff && !half.RuntimeOwnerHoldsLease)
                await half.Lease.DisposeAsync().ConfigureAwait(false);
            if (entered) connectionGate.Release();
        }
    }
}
