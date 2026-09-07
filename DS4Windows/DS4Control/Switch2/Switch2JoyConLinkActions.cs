using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

/// <summary>A single-use cold-path reservation of an existing virtual pad.</summary>
internal interface ISwitch2JoyConOutputHandoff : IDisposable
{
    int InputSlot { get; }
    void PrepareSuccessor(Switch2RuntimeInputDevice device);
}

internal sealed partial class Switch2BluetoothProductionCoordinator
{
    internal InputControllerSlotToken GetJoinedJoyConToken(DS4Device device)
    {
        lock (sync)
            foreach (var token in joinedJoyCons.Keys)
                if (ReferenceEquals(token.Registration.Device, device)) return token;
        return default;
    }

    internal async ValueTask<Switch2JoyConPairActivationResult> UnlinkJoyConsAsync(
        InputControllerSlotToken token, CancellationToken cancellationToken = default)
    {
        CancellationToken lifetime;
        lock (sync)
        {
            if (!running || lifetimeCancellation == null)
                return Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.Cancelled);
            lifetime = lifetimeCancellation.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        bool entered = false;
        try
        {
            await connectionGate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            if (IsAutomaticPairingEnabled())
                return Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.AutomaticPairingEnabled);
            (PendingJoyCon Left, PendingJoyCon Right) pair;
            Switch2JoyConPairRecord[] remembered;
            lock (sync)
            {
                if (!running || !token.IsValid || !joinedJoyCons.TryGetValue(token, out pair))
                    return Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.InvalidCandidate);
                remembered = pairRecords.Where(record => record.LeftPeerId == pair.Left.PeerId &&
                    record.RightPeerId == pair.Right.PeerId).ToArray();
            }
            using ISwitch2JoyConOutputHandoff handoff = beginOutputHandoff?.Invoke(token);
            var deleted = new System.Collections.Generic.List<Switch2JoyConPairRecord>();
            // Pair records are not Bluetooth bonds. Delete only this pair's
            // exact revisions, and leave both devices usable on reconnect.
            foreach (var record in remembered)
            {
                if (pairAssociation == null || !pairAssociation.TryDeleteExplicitPair(
                        record.PairId, record.Revision, out _))
                {
                    RestoreDeletedPairRecords(deleted);
                    return Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.StoreRejected);
                }
                deleted.Add(record);
                lock (sync) pairRecords = pairRecords.Where(item => item.PairId != record.PairId).ToArray();
            }
            // Remove local tracking first so a concurrent physical disconnect
            // cannot schedule a second survivor recovery for this explicit action.
            lock (sync) joinedJoyCons.Remove(token);
            if (!registrationService.TryRemove(token, LifecycleTimeoutMilliseconds, out var failure))
            {
                RestoreDeletedPairRecords(deleted);
                FenceUncertainPeer(pair.Left.PeerToken, token);
                FenceUncertainPeer(pair.Right.PeerToken, token);
                ReportDiagnostic($"Switch 2 Joy-Con unlink retirement rejected: {failure.Kind}.");
                return Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.RuntimeRejected);
            }
            pair.Left.RuntimeOwnerHoldsLease = pair.Right.RuntimeOwnerHoldsLease = false;
            // Attempt both sides independently; one failed reconnect must not
            // strand the other. No new pairing decision occurs in this path.
            bool leftReady = await RestoreUnlinkedHalfAsync(pair.Left, linked.Token, handoff).ConfigureAwait(false);
            bool rightReady = await RestoreUnlinkedHalfAsync(pair.Right, linked.Token, null).ConfigureAwait(false);
            ReportDiagnostic($"Switch 2 Joy-Con unlink completed: left={(leftReady ? "ready" : "unavailable")}, right={(rightReady ? "ready" : "unavailable")}.");
            return leftReady && rightReady ? Switch2JoyConPairActivationResult.Success() :
                Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.ActivationRejected);
        }
        catch (OperationCanceledException)
        {
            return Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.Cancelled);
        }
        catch (Exception exception)
        {
            ReportDiagnostic($"Switch 2 Joy-Con unlink failed: {exception.GetType().Name}: {exception.Message}");
            return Switch2JoyConPairActivationResult.Failed(Switch2JoyConPairActivationFailure.RuntimeRejected);
        }
        finally { if (entered) connectionGate.Release(); }
    }

    private void RestoreDeletedPairRecords(System.Collections.Generic.List<Switch2JoyConPairRecord> records)
    {
        foreach (var record in records)
        {
            if (pairCatalog.TryReplace(record, 0))
            { lock (sync) pairRecords = AppendPairRecord(pairRecords, record); }
            else ReportDiagnostic("The Joy-Con pair could not be restored in saved settings after unlink failed.");
        }
    }

    private async ValueTask<bool> RestoreUnlinkedHalfAsync(PendingJoyCon half,
        CancellationToken cancellationToken, ISwitch2JoyConOutputHandoff handoff)
    {
        try
        {
            half.SlotToken = default;
            if (half.Lease.HasDisconnected ||
                !await ReopenJoyConAsync(half, cancellationToken).ConfigureAwait(false)) return false;
            lock (sync)
            {
                if (!running || cancellationToken.IsCancellationRequested) return false;
                half.CandidateId = ++nextJoyConCandidateId;
                half.IsBuffered = true;
                pendingJoyCons.Add(half.PeerId, half);
                joyConPairCandidates.Add(half.CandidateId, half.PeerId);
            }
            return (await ActivateStandaloneAsync(half, cancellationToken, handoff).ConfigureAwait(false)).Succeeded;
        }
        catch (Exception exception)
        {
            ReportDiagnostic($"Switch 2 unlinked {half.Model} could not reconnect: {exception.GetType().Name}.");
            return false;
        }
        finally
        {
            if (!half.SlotToken.IsValid) lock (sync) RemovePendingNoLock(half);
            if (!half.RuntimeOwnerHoldsLease)
            {
                lock (sync) RemovePendingNoLock(half);
                await half.Lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
