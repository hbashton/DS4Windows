using System.Diagnostics;
using System.Threading;

namespace DS4Windows.InputDevices;

public partial class JoyConDevice
{
    internal bool NintendoUdpMotionSubscribed;

    private bool TryBeginNintendoBluetoothDisconnect(out LegacyJoyConConnection peer)
    {
        peer = null;
        var connection = ProfileConnection;
        if (connection == null) return true;
        var group = Volatile.Read(ref connection.Group);
        if (group == null) return false;
        lock (group.Gate)
        {
            if (!ReferenceEquals(ProfileConnection, connection) || !ReferenceEquals(connection.Group, group) ||
                !group.Active || !connection.Connected || connection.Paused || connection.DisconnectRequested ||
                IsRemoving || IsRemoved) return false;
            var other = group.Joined ? (ReferenceEquals(group.Left, connection) ? group.Right : group.Left) : null;
            if (other != null && (!other.Connected || other.Paused || other.DisconnectRequested ||
                other.Device is not JoyConDevice otherDevice ||
                !ReferenceEquals(otherDevice.ProfileConnection, other) || otherDevice.IsRemoving || otherDevice.IsRemoved))
                return false;
            connection.DisconnectRequested = true;
            if (other != null) other.DisconnectRequested = true;
            peer = other;
            return true;
        }
    }

    private void CompleteNintendoPeerDisconnect(LegacyJoyConConnection expected, bool callRemoval)
    {
        // The requesting half may already have been removed, promoting this
        // half to its reserved standalone group. The terminal claim survives
        // that promotion and forbids relinking until physical retirement.
        if (!ReferenceEquals(ProfileConnection, expected) || !Volatile.Read(ref expected.Connected) ||
            !Volatile.Read(ref expected.DisconnectRequested) || IsRemoving || IsRemoved) return;
        DisconnectBluetoothPhysical(callRemoval);
    }

    internal void PublishNintendoUdpMotion()
    {
        if (Volatile.Read(ref NintendoUdpMotionSubscribed)) MotionEvent?.Invoke(this, System.EventArgs.Empty);
    }

    private bool ShouldNintendoProfileDisconnect()
    {
        var connection = ProfileConnection;
        var group = connection == null ? null : Volatile.Read(ref connection.Group);
        if (group == null || !group.Active || !connection.Connected || connection.Paused) return false;
        long now = Stopwatch.GetTimestamp();
        if (!LegacyJoyConIdlePolicy.IsIdle(cState))
            LegacyJoyConIdlePolicy.RecordActivity(group, now);
        int slot = group.Owner.Slot;
        if ((uint)slot >= (uint)Global.Switch2AutoDisconnectMode.Length) return false;
        return LegacyJoyConIdlePolicy.ShouldDisconnect(Global.Switch2AutoDisconnectMode[slot],
            Volatile.Read(ref Global.Switch2AutoDisconnectTimeoutSeconds[slot]), group.Owner.Device.IdleTimeout,
            conType == ConnectionType.BT, Volatile.Read(ref connection.FirstReportTimestampQpc),
            Volatile.Read(ref group.LastActivityTimestampQpc), now, Stopwatch.Frequency, charging);
    }
}
