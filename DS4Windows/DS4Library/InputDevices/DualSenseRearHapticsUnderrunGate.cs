using System;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// A bounded opportunity for a briefly late rear block to arrive. This is
    /// not a startup buffer, an EOS detector, or permission to replay samples.
    /// Only the physical presenter owns this state.
    /// </summary>
    internal sealed class DualSenseRearHapticsUnderrunGate
    {
        private readonly long maximumWaitQpc;
        private readonly long recentPublicationQpc;
        private long lastDeferredSequence;
        private long deadlineQpc;

        internal DualSenseRearHapticsUnderrunGate(long frequency)
        {
            if (frequency <= 0 || frequency > long.MaxValue / 32)
                throw new ArgumentOutOfRangeException(nameof(frequency));
            // One 32-frame block at 3 kHz (512 source frames at 48 kHz).
            maximumWaitQpc = (long)Math.Ceiling(frequency * 32.0 / 3000.0);
            // Captured enqueue-to-next-carrier ages reach 30.05 ms. Three
            // blocks cover that observed phase without treating persistent
            // payload expiry as proof that a source is still producing.
            recentPublicationQpc = checked(maximumWaitQpc * 3);
        }

        internal long Update(long nowQpc, bool ready, bool empty,
            long committedSequence, long committedEnqueuedQpc)
        {
            if (ready || !empty)
            {
                deadlineQpc = 0;
                return 0;
            }
            if (deadlineQpc != 0)
            {
                if (nowQpc < deadlineQpc) return deadlineQpc;
                deadlineQpc = 0;
                return 0;
            }
            if (committedSequence <= 0 || committedSequence == lastDeferredSequence ||
                committedEnqueuedQpc <= 0 || nowQpc < committedEnqueuedQpc ||
                nowQpc - committedEnqueuedQpc >= recentPublicationQpc ||
                nowQpc > long.MaxValue - maximumWaitQpc ||
                committedEnqueuedQpc > long.MaxValue - recentPublicationQpc)
                return 0;

            // One absolute deadline per accepted source block. Control/data
            // wakeups cannot renew it. At a real source end the normal silent
            // carrier resumes after this single bounded deferral.
            lastDeferredSequence = committedSequence;
            deadlineQpc = Math.Min(nowQpc + maximumWaitQpc,
                committedEnqueuedQpc + recentPublicationQpc);
            return deadlineQpc;
        }

        internal void Reset()
        {
            lastDeferredSequence = 0;
            deadlineQpc = 0;
        }
    }
}
