namespace DS4Windows.Switch2;

internal enum Switch2RumbleMaintenanceDisposition : byte
{
    Idle,
    Active,
    Contended,
    RetryPending,
}

/// <summary>Output scheduling facts, not a physical delivery acknowledgement.</summary>
internal readonly record struct Switch2RumbleMaintenanceResult
{
    private Switch2RumbleMaintenanceResult(Switch2RumbleMaintenanceDisposition disposition,
        ulong nextDueMicroseconds = 0)
    {
        Disposition = disposition;
        NextDueMicroseconds = nextDueMicroseconds;
    }

    internal Switch2RumbleMaintenanceDisposition Disposition { get; }
    internal ulong NextDueMicroseconds { get; }
    internal static Switch2RumbleMaintenanceResult Idle => default;
    internal static Switch2RumbleMaintenanceResult Contended => new(Switch2RumbleMaintenanceDisposition.Contended);
    internal static Switch2RumbleMaintenanceResult RetryPending => new(Switch2RumbleMaintenanceDisposition.RetryPending);
    internal static Switch2RumbleMaintenanceResult Active(ulong nextDueMicroseconds = 0) =>
        new(Switch2RumbleMaintenanceDisposition.Active, nextDueMicroseconds);
}

internal static class Switch2RumbleMaintenanceSchedule
{
    internal const int MaximumConsecutiveFastRetries = 3;

    internal static int GetDelayMilliseconds(in Switch2RumbleMaintenanceResult result,
        int intervalMilliseconds, ulong completedMicroseconds, ref int consecutiveContention)
    {
        if (result.Disposition == Switch2RumbleMaintenanceDisposition.Contended)
        {
            // Only a failure to enter the output transaction gets this short
            // recovery. Persistent contention never creates a 1 kHz poll loop.
            if (consecutiveContention < MaximumConsecutiveFastRetries)
            {
                consecutiveContention++;
                return 1;
            }
            return intervalMilliseconds;
        }

        consecutiveContention = 0;
        if (result.Disposition != Switch2RumbleMaintenanceDisposition.Active ||
            result.NextDueMicroseconds == 0 || completedMicroseconds == 0)
            return intervalMilliseconds;

        if (result.NextDueMicroseconds <= completedMicroseconds) return 1;
        ulong remaining = result.NextDueMicroseconds - completedMicroseconds;
        ulong ceilingMilliseconds = remaining / 1000 + (remaining % 1000 == 0 ? 0UL : 1UL);
        return (int)System.Math.Min((ulong)intervalMilliseconds, ceilingMilliseconds);
    }

    internal static ulong NextDue(ulong writeStartMicroseconds, ulong intervalMicroseconds) =>
        ulong.MaxValue - writeStartMicroseconds < intervalMicroseconds ? ulong.MaxValue :
            writeStartMicroseconds + intervalMicroseconds;
}
