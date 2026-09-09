using System;
using System.IO;
using System.Threading;
using DS4Windows.Switch2;

namespace DS4Windows;

internal static class LegacyJoyConProfileUiActions
{
    internal static bool IsStandalone(LegacyJoyConConnection connection) =>
        TryGetCurrentGroup(connection, out var group) && !group.Joined;

    internal static bool TrySetHoldMode(LegacyJoyConConnection connection, Switch2JoyConHoldMode mode,
        Func<int, string, bool> saveProfile, out bool persisted)
    {
        persisted = false;
        if (!IsStandalone(connection) || mode is not (Switch2JoyConHoldMode.Horizontal or Switch2JoyConHoldMode.Vertical) ||
            saveProfile == null || (uint)connection.Slot >= Global.MAX_DS4_CONTROLLER_COUNT) return false;
        bool applied = false, saved = false;
        Mapping.ExecuteSerializedProfileMutation(connection.Slot, () =>
        {
            if (!IsStandalone(connection)) return;
            Global.Switch2JoyConStandaloneHoldMode[connection.Slot] = mode;
            applied = true;
            string profile = Global.ProfilePath[connection.Slot];
            if (string.IsNullOrWhiteSpace(profile) || Path.GetFileName(profile) != profile) return;
            try { saved = saveProfile(connection.Slot, profile); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            { saved = false; }
        });
        persisted = saved;
        return applied;
    }

    internal static bool TryResetGyroCalibration(LegacyJoyConConnection owner, Action<DS4Device> reset,
        Action<DS4Device, Action> queue = null)
    {
        if (reset == null || !TryGetCurrentGroup(owner, out var group)) return false;
        bool resetAny = false;
        Reset(group.Left);
        Reset(group.Right);
        return resetAny;
        void Reset(LegacyJoyConConnection half)
        {
            if (!IsCurrentHalf(half)) return;
            // Original gyro calibration shares mutable sensor buffers with
            // its reader. UI requests must execute on that physical queue,
            // and recheck the exact pair before touching calibration state.
            if (queue != null) queue(half.Device, () => { if (IsCurrentHalf(half)) reset(half.Device); });
            else reset(half.Device);
            resetAny = true;
        }
        bool IsCurrentHalf(LegacyJoyConConnection half) => half != null && Volatile.Read(ref half.Connected) &&
            !Volatile.Read(ref half.Paused) && Volatile.Read(ref group.Active) &&
            ReferenceEquals(Volatile.Read(ref half.Group), group);
    }

    private static bool TryGetCurrentGroup(LegacyJoyConConnection connection, out LegacyJoyConGroup group)
    {
        group = connection == null ? null : Volatile.Read(ref connection.Group);
        return group != null && ReferenceEquals(group.Owner, connection) && Volatile.Read(ref group.Active) &&
            Volatile.Read(ref connection.Connected) && !Volatile.Read(ref connection.Paused);
    }
}
