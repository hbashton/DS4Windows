using System;
using System.Threading;

namespace DS4Windows;

/// <summary>Release a paused logical owner's output without rebuilding its pad.</summary>
internal static class LegacyJoyConTerminalNeutral
{
    internal static bool TryRelease(LegacyJoyConConnection owner, LegacyJoyConGroup expected,
        DS4Device[] controllers, OutputDevice output, OutputDevice companion, Action releaseMapping)
    {
        if (owner == null || expected == null || controllers == null || releaseMapping == null ||
            (uint)owner.Slot >= (uint)controllers.Length ||
            !ReferenceEquals(controllers[owner.Slot], owner.Device) ||
            !ReferenceEquals(Volatile.Read(ref owner.Group), expected) ||
            !ReferenceEquals(expected.Owner, owner) || !Volatile.Read(ref expected.Active) ||
            !Volatile.Read(ref owner.Connected) || !Volatile.Read(ref owner.Paused) ||
            owner.Device.IsRemoving || owner.Device.IsRemoved) return false;
        try { releaseMapping(); }
        finally
        {
            // Explicit neutral bypasses debounce, anti-dead-zone, macros and
            // OSC contributions. It never withdraws/recreates either output.
            try { output?.ResetState(); }
            finally { if (!ReferenceEquals(companion, output)) companion?.ResetState(); }
        }
        return true;
    }
}
