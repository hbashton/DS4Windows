using DS4Windows.Switch2;
using System.Threading;

namespace DS4Windows;

internal static class LegacyJoyConIdlePolicy
{
    internal static void RecordActivity(LegacyJoyConGroup group, long timestamp)
    {
        long observed = Volatile.Read(ref group.LastActivityTimestampQpc);
        while (observed < timestamp)
        {
            long prior = Interlocked.CompareExchange(ref group.LastActivityTimestampQpc, timestamp, observed);
            if (prior == observed) return;
            observed = prior;
        }
    }

    internal static bool IsIdle(DS4State state) => state != null &&
        !(state.Cross || state.Circle || state.Square || state.Triangle ||
          state.DpadUp || state.DpadDown || state.DpadLeft || state.DpadRight ||
          state.L1 || state.R1 || state.L3 || state.R3 || state.Share || state.Options ||
          state.PS || state.Mute || state.Capture || state.SideL || state.SideR ||
          state.FnL || state.FnR || state.BLP || state.BRP || state.L2Btn || state.R2Btn) &&
        state.L2 == 0 && state.R2 == 0 &&
        state.LX > 63 && state.LX < 192 && state.LY > 63 && state.LY < 192 &&
        state.RX > 63 && state.RX < 192 && state.RY > 63 && state.RY < 192;

    internal static bool ShouldDisconnect(Switch2AutoDisconnectMode mode, long timeoutSeconds,
        int legacySeconds, bool wireless, long firstReportQpc, long lastActivityQpc, long now, long frequency,
        bool charging = false)
    {
        if (!wireless || firstReportQpc <= 0 || lastActivityQpc <= 0 || now <= 0 || frequency <= 0) return false;
        if (charging && Switch2AutoDisconnectPolicyResolver.NormalizeMode(mode) ==
                Switch2AutoDisconnectMode.LegacyProfile) return false;
        var policy = Switch2AutoDisconnectPolicyResolver.Resolve(mode, timeoutSeconds, legacySeconds);
        if (!policy.Enabled) return false;
        long start = policy.Mode == Switch2AutoDisconnectMode.Absolute ? firstReportQpc : lastActivityQpc;
        long duration = Switch2AutoDisconnectPolicyResolver.ToQpcTicks(policy.TimeoutSeconds, frequency);
        return duration > 0 && now >= start && now - start >= duration;
    }
}
