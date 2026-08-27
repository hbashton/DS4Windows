using System;

namespace DS4Windows
{
    /// <summary>
    /// Pure deadline advancement for the physical DualShock 4 audio report
    /// clock. Small wake-up jitter keeps the absolute clock phase; a real
    /// scheduling stall starts a new phase instead of compressing the next
    /// report interval to repay lateness.
    /// </summary>
    internal static class DualShock4AudioReportScheduler
    {
        // One millisecond separates ordinary waitable-timer jitter from a
        // scheduler stall while remaining only 1/16 of the direct lane's
        // 16 ms report period.
        internal const double DirectRebaseLatenessMilliseconds = 1.0;
        internal const double MaximumCadenceSlewFractionPerReport =
            0.00001;

        internal static long GetDirectRebaseLatenessTicks(long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            return Math.Max(1L, (long)Math.Round(frequency *
                DirectRebaseLatenessMilliseconds / 1000.0));
        }

        internal static long GetRelativeDueTime100Nanoseconds(
            long remainingTicks, long frequency)
        {
            if (remainingTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingTicks));
            }
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            long dueTime = Math.Max(1L, (long)Math.Ceiling(
                remainingTicks * (double)TimeSpan.TicksPerSecond /
                    frequency));
            return -dueTime;
        }

        internal static long MapControllerClockToCadenceTicks(
            long nominalCadenceTicks, double controllerClockRatio)
        {
            if (nominalCadenceTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nominalCadenceTicks));
            }

            double boundedRatio = double.IsFinite(controllerClockRatio) ?
                Math.Clamp(controllerClockRatio,
                    DualShock4ControllerClockDiscipline.
                        MinimumPublishedRatio,
                    DualShock4ControllerClockDiscipline.
                        MaximumPublishedRatio) : 1.0;
            return Math.Max(1L, (long)Math.Round(nominalCadenceTicks /
                boundedRatio));
        }

        internal static long SteerCadenceTicks(long currentCadenceTicks,
            long nominalCadenceTicks, double controllerClockRatio)
        {
            if (currentCadenceTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(currentCadenceTicks));
            }

            long target = MapControllerClockToCadenceTicks(
                nominalCadenceTicks, controllerClockRatio);
            long maximumStep = Math.Max(1L, (long)Math.Round(
                nominalCadenceTicks *
                    MaximumCadenceSlewFractionPerReport));
            return currentCadenceTicks + Math.Clamp(
                target - currentCadenceTicks, -maximumStep, maximumStep);
        }

        internal static long AdvanceDeadline(long scheduledDeadline,
            long actualWakeTimestamp, long cadenceTicks,
            long rebaseLatenessTicks, out bool rebased)
        {
            if (cadenceTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cadenceTicks));
            }
            if (rebaseLatenessTicks < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rebaseLatenessTicks));
            }

            long lateness = actualWakeTimestamp - scheduledDeadline;
            rebased = lateness >= rebaseLatenessTicks;
            long deadlineBase = rebased ? actualWakeTimestamp :
                scheduledDeadline;
            return deadlineBase + cadenceTicks;
        }

        internal static long SelectCurrentDeadline(long scheduledDeadline,
            long previousReportTimestamp, long cadenceTicks,
            long rebaseLatenessTicks, out bool rebased)
        {
            if (cadenceTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cadenceTicks));
            }
            if (rebaseLatenessTicks < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rebaseLatenessTicks));
            }

            rebased = false;
            if (previousReportTimestamp <= 0)
            {
                return scheduledDeadline;
            }

            long fullCadenceDeadline = previousReportTimestamp +
                cadenceTicks;
            if (fullCadenceDeadline - scheduledDeadline <
                rebaseLatenessTicks)
            {
                return scheduledDeadline;
            }

            rebased = true;
            return fullCadenceDeadline;
        }
    }
}
