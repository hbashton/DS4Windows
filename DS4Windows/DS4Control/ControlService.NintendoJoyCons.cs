using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using static DS4Windows.Global;

namespace DS4Windows;

public partial class ControlService
{
    private readonly LegacyJoyConLinkCoordinator legacyJoyConLinks;
    private readonly LegacyJoyConPairStore legacyJoyConPairStore;
    internal event EventHandler NintendoJoyConTopologyChanged;

    internal NintendoJoyConCandidate[] GetNintendoJoyConPairCandidates() =>
        legacyJoyConLinks.GetStandaloneConnections().Select(item => new NintendoJoyConCandidate(default, item))
            .Concat(GetSwitch2JoyConPairCandidates().Select(item => new NintendoJoyConCandidate(item))).ToArray();

    internal NintendoJoyConJoined GetNintendoJoinedJoyCons(DS4Device device) =>
        new(GetJoinedJoyConToken(device), legacyJoyConLinks.GetJoinedGroup(device));

    internal async ValueTask<bool> LinkNintendoJoyConsAsync(NintendoJoyConCandidate preferred,
        NintendoJoyConCandidate other)
    {
        if (!preferred.IsValid || !other.IsValid || preferred.IsLeft == other.IsLeft ||
            preferred.IsOriginal != other.IsOriginal || Global.DeviceOptions.JoyConDeviceOpts.AutomaticPairing) return false;
        if (!preferred.IsOriginal)
        {
            var left = preferred.IsLeft ? preferred : other;
            var right = preferred.IsLeft ? other : preferred;
            return (await CreateAndActivateSwitch2JoyConPairAsync(left.Switch2.Id, right.Switch2.Id,
                preferredCandidateId: preferred.Switch2.Id)).Succeeded;
        }
        return await Task.Run(() =>
        {
            lock (serviceLifecycleLock)
            {
                if (!running || Global.DeviceOptions.JoyConDeviceOpts.AutomaticPairing) return false;
                return LinkLegacyJoyCons(preferred.Original, other.Original, remember: true);
            }
        });
    }

    internal async ValueTask<bool> UnlinkNintendoJoyConsAsync(NintendoJoyConJoined joined)
    {
        if (!joined.IsValid || Global.DeviceOptions.JoyConDeviceOpts.AutomaticPairing) return false;
        if (joined.Original == null) return (await UnlinkSwitch2JoyConsAsync(joined.Switch2)).Succeeded;
        return await Task.Run(() =>
        {
            lock (serviceLifecycleLock)
            {
                if (!running || Global.DeviceOptions.JoyConDeviceOpts.AutomaticPairing) return false;
                if (!ReferenceEquals(legacyJoyConLinks.GetJoinedGroup(joined.Original.Owner.Device), joined.Original))
                    return false;
                var leftId = joined.Original.Left.Device.ProfileLinkId;
                var rightId = joined.Original.Right.Device.ProfileLinkId;
                bool wasSaved = legacyJoyConPairStore.Pairs.Any(pair => pair.Left == leftId && pair.Right == rightId);
                legacyJoyConPairStore.Forget(leftId, rightId);
                bool result;
                try
                {
                    result = legacyJoyConLinks.TryUnlink(joined.Original, secondary =>
                    {
                        ReleaseLegacyJoyConLogicalState(joined.Original.Owner);
                        ClearLegacyJoyConMouse(secondary);
                        RestoreLegacyJoyConOutput(secondary);
                    });
                }
                catch
                {
                    RestoreSavedPairAfterFailedUnlink();
                    throw;
                }
                if (!result) RestoreSavedPairAfterFailedUnlink();
                if (result)
                {
                    NintendoJoyConTopologyChanged?.Invoke(this, EventArgs.Empty);
                }
                return result;

                void RestoreSavedPairAfterFailedUnlink()
                {
                    if (!wasSaved) return;
                    try { legacyJoyConPairStore.Remember(leftId, rightId, joined.Original.Owner.IsLeft); }
                    catch (Exception exception)
                    {
                        LogDebug($"Joy-Con unlink did not finish and its saved link could not be restored: {exception.Message}", true);
                    }
                }
            }
        });
    }

    internal async ValueTask<int> ReconcileAutomaticNintendoJoyConPairsAsync()
    {
        int original = await Task.Run(() => { lock (serviceLifecycleLock) return ReconcileLegacyJoyConPairs(); });
        return original + await ReconcileAutomaticSwitch2JoyConPairsAsync();
    }

    private int ReconcileLegacyJoyConPairs()
    {
        if (!running) return 0;
        int count = 0;
        while (true)
        {
            var candidates = legacyJoyConLinks.GetStandaloneConnections();
            LegacyJoyConConnection left = null, right = null, preferred = null;
            foreach (var saved in legacyJoyConPairStore.Pairs)
            {
                left = candidates.FirstOrDefault(item => item.IsLeft && item.Device.ProfileLinkId == saved.Left);
                right = candidates.FirstOrDefault(item => !item.IsLeft && item.Device.ProfileLinkId == saved.Right);
                if (left != null && right != null) { preferred = saved.PreferLeft ? left : right; break; }
                left = right = null;
            }
            if (preferred == null && Global.DeviceOptions.JoyConDeviceOpts.AutomaticPairing)
            {
                left = candidates.FirstOrDefault(item => item.IsLeft);
                right = candidates.FirstOrDefault(item => !item.IsLeft);
                if (left != null && right != null) preferred = left.Generation < right.Generation ? left : right;
            }
            if (left == null || right == null) return count;
            try
            {
                if (!LinkLegacyJoyCons(preferred, ReferenceEquals(preferred, left) ? right : left)) return count;
                count++;
            }
            catch (Exception exception)
            {
                LogDebug($"Joy-Con automatic linking could not finish: {exception.Message}", true);
                return count;
            }
        }
    }

    private bool LinkLegacyJoyCons(LegacyJoyConConnection preferred, LegacyJoyConConnection other, bool remember = false)
    {
        bool result = legacyJoyConLinks.TryLink(preferred, other, (owner, secondary) =>
        {
            RequireExactLegacyJoyCon(owner);
            RequireExactLegacyJoyCon(secondary);
            ClearLegacyJoyConMouse(owner);
            ClearLegacyJoyConMouse(secondary);
            try
            {
                // Release held keys/buttons in the disappearing logical pad.
                // Its physical reader remains open; the first-selected output
                // is never removed or reconstructed by this transition.
                CommitNeutralMapping(secondary.Slot);
                secondary.Device.setRumble(0, 0);
                if (!useDInputOnly[secondary.Slot])
                    UnplugOutDev(secondary.Slot, secondary.Device, immediate: true);
                RequireExactLegacyJoyCon(owner);
                RequireExactLegacyJoyCon(secondary);
            }
            catch
            {
                RestoreLegacyJoyConOutput(secondary);
                throw;
            }
        }, out _);
        if (result)
        {
            if (remember)
            {
                var left = preferred.IsLeft ? preferred : other;
                var right = preferred.IsLeft ? other : preferred;
                try { legacyJoyConPairStore.Remember(left.Device.ProfileLinkId, right.Device.ProfileLinkId, preferred.IsLeft); }
                catch (Exception exception) { LogDebug($"Joy-Cons were linked for this session, but the link could not be saved: {exception.Message}", true); }
            }
            NintendoJoyConTopologyChanged?.Invoke(this, EventArgs.Empty);
        }
        return result;
    }

    private void RequireExactLegacyJoyCon(LegacyJoyConConnection connection)
    {
        if (connection == null || !connection.Connected || connection.Device.IsRemoving || connection.Device.IsRemoved ||
            !ReferenceEquals(DS4Controllers[connection.Slot], connection.Device) ||
            connection.Device is not JoyConDevice joyCon || !ReferenceEquals(joyCon.ProfileConnection, connection))
            throw new InvalidOperationException("That Joy-Con connection is no longer ready. Try again after it reconnects.");
    }

    private void RestoreLegacyJoyConOutput(LegacyJoyConConnection connection)
    {
        RequireExactLegacyJoyCon(connection);
        if (!Global.getDInputOnly(connection.Slot) && useDInputOnly[connection.Slot])
        {
            PluginOutDev(connection.Slot, connection.Device);
            if (useDInputOnly[connection.Slot])
                throw new InvalidOperationException("The separate Joy-Con virtual pad could not be created.");
        }
    }

    private static void ClearLegacyJoyConMouse(LegacyJoyConConnection connection)
    {
        if (connection.Device is JoyConDevice joyCon &&
            !joyCon.ClearNintendoMousePresentation(CancellationToken.None))
            throw new InvalidOperationException("Joy-Con mouse output is still draining. Please try linking again.");
    }

    private void PublishLegacyJoyConReport(LegacyJoyConConnection owner, DS4State current, DS4State previous)
    {
        // Called under the pair's publication gate. Never acquire the topology
        // or service lock here. Original Joy-Cons do not use the DS4/DS3 typed
        // worker lease: their exact connection/group is closed and drained by
        // Remove/Clear before the existing HID lifecycle retires presentation.
        if (!owner.Connected || owner.Device.IsRemoving || owner.Device.IsRemoved ||
            !ReferenceEquals(DS4Controllers[owner.Slot], owner.Device) ||
            owner.Device is not JoyConDevice joyCon || !ReferenceEquals(joyCon.ProfileConnection, owner)) return;
        joyCon.PublishProjectedMotion(current);
        if (!owner.Connected || owner.Device.IsRemoving || owner.Device.IsRemoved ||
            !ReferenceEquals(DS4Controllers[owner.Slot], owner.Device) || !ReferenceEquals(joyCon.ProfileConnection, owner)) return;
        OnReportCore(owner.Device, EventArgs.Empty, owner.Slot, owner.DiagnosticsSource, current, previous);
        if (owner.Connected && !owner.Device.IsRemoving && !owner.Device.IsRemoved &&
            ReferenceEquals(DS4Controllers[owner.Slot], owner.Device)) joyCon.PublishNintendoUdpMotion();
    }

    private void RemoveLegacyJoyConConnection(DS4Device device)
    {
        if (device is not JoyConDevice) return;
        var survivor = legacyJoyConLinks.Remove(device, retained =>
        {
            try { ReleaseLegacyJoyConLogicalState(retained); }
            catch (Exception exception)
            {
                // Do not abandon removal of the disconnected physical half if
                // a virtual driver rejects its surviving pad's neutral write.
                LogDebug($"Remaining Joy-Con neutral release could not be confirmed: {exception.Message}", true);
            }
        });
        ((JoyConDevice)device).StopNintendoMousePresentation();
        if (survivor == null) return;
        // Removal can begin on a physical reader. Recovery is a cold operation
        // after its existing exact-slot retirement has released the service lock.
        Task.Run(() =>
        {
            lock (serviceLifecycleLock)
            {
                if (!running || !legacyJoyConLinks.IsCurrent(survivor)) return;
                try
                {
                    ClearLegacyJoyConMouse(survivor);
                    if (!Volatile.Read(ref survivor.DisconnectRequested)) RestoreLegacyJoyConOutput(survivor);
                }
                catch (Exception exception) { LogDebug($"Remaining Joy-Con needs output recovery: {exception.Message}", true); }
                ReconcileLegacyJoyConPairs();
                NintendoJoyConTopologyChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void ReleaseLegacyJoyConLogicalState(LegacyJoyConConnection owner)
    {
        RequireExactLegacyJoyCon(owner);
        LegacyJoyConGroup expected = Volatile.Read(ref owner.Group);
        ClearLegacyJoyConMouse(owner);
        if (!LegacyJoyConTerminalNeutral.TryRelease(owner, expected, DS4Controllers,
                outputDevices[owner.Slot], Volatile.Read(ref gameBarCompatibilityOutputDevices[owner.Slot]),
                () =>
                {
                    int slot = owner.Slot;
                    var neutral = new DS4State();
                    touchPad[slot]?.PrepareGyroNeutralReport(terminal: true);
                    Mapping.RequestPostMapStickReset(slot);
                    neutral.CopyTo(CurrentState[slot]);
                    try
                    {
                        // Use normal release semantics for held keys/buttons.
                        // Explicit toggle/macro latches remain the user's
                        // configured behavior, not a global all-keys-up event.
                        if (!recordingMacro && (useTempProfile[slot] || containsCustomAction(slot) ||
                                containsCustomExtras(slot) || getProfileActionCount(slot) > 0))
                            Mapping.MapCustom(slot, CurrentState[slot], MappedState[slot],
                                ExposedState[slot], touchPad[slot], this);
                    }
                    finally
                    {
                        neutral.CopyTo(CurrentState[slot]);
                        neutral.CopyTo(TempState[slot]);
                        neutral.CopyTo(MappedState[slot]);
                        Mapping.Commit(slot);
                    }
                }))
            throw new InvalidOperationException("The Joy-Con logical owner changed before its held input could be released.");
    }
}
