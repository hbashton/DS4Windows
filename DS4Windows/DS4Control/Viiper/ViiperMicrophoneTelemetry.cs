using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Collects diagnostic-only microphone pipeline observations. All counters
    /// are monotonic for a virtual-device connection and deliberately tolerate
    /// malformed observations without affecting realtime audio processing.
    /// </summary>
    internal sealed class ViiperMicrophoneTelemetry
    {
        private long lastSuccessfulSubmissionTimestamp;
        private long lastSubmissionGapTicks;
        private long maximumSubmissionGapTicks;
        private long observedSubmissionGaps;
        private long preProcessorAllZeroFrames;
        private long postProcessorAllZeroFrames;
        private long postProcessorAllZeroUnmutedFrames;
        private long preProcessorPeak;
        private long postProcessorPeak;
        private long compressedQueueHighWaterMark;

        internal long LastSubmissionGapTicks =>
            Interlocked.Read(ref lastSubmissionGapTicks);

        internal long MaximumSubmissionGapTicks =>
            Interlocked.Read(ref maximumSubmissionGapTicks);

        internal long ObservedSubmissionGaps =>
            Interlocked.Read(ref observedSubmissionGaps);

        internal long PreProcessorAllZeroFrames =>
            Interlocked.Read(ref preProcessorAllZeroFrames);

        internal long PostProcessorAllZeroFrames =>
            Interlocked.Read(ref postProcessorAllZeroFrames);

        internal long PostProcessorAllZeroUnmutedFrames =>
            Interlocked.Read(ref postProcessorAllZeroUnmutedFrames);

        internal long PreProcessorPeak =>
            Interlocked.Read(ref preProcessorPeak);

        internal long PostProcessorPeak =>
            Interlocked.Read(ref postProcessorPeak);

        internal long CompressedQueueHighWaterMark =>
            Interlocked.Read(ref compressedQueueHighWaterMark);

        internal void Reset()
        {
            Interlocked.Exchange(ref lastSuccessfulSubmissionTimestamp, 0);
            Interlocked.Exchange(ref lastSubmissionGapTicks, 0);
            Interlocked.Exchange(ref maximumSubmissionGapTicks, 0);
            Interlocked.Exchange(ref observedSubmissionGaps, 0);
            Interlocked.Exchange(ref preProcessorAllZeroFrames, 0);
            Interlocked.Exchange(ref postProcessorAllZeroFrames, 0);
            Interlocked.Exchange(ref postProcessorAllZeroUnmutedFrames, 0);
            Interlocked.Exchange(ref preProcessorPeak, 0);
            Interlocked.Exchange(ref postProcessorPeak, 0);
            Interlocked.Exchange(ref compressedQueueHighWaterMark, 0);
        }

        /// <summary>
        /// Starts a new active-capture interval without discarding the
        /// connection's cumulative maximum and counters. This prevents an
        /// intentional capture close from being reported as a submit gap.
        /// </summary>
        internal void ResetSubmissionBaseline()
        {
            Interlocked.Exchange(ref lastSuccessfulSubmissionTimestamp, 0);
            Interlocked.Exchange(ref lastSubmissionGapTicks, 0);
        }

        internal void ObservePreProcessorFrame(short[] samples,
            int sampleCount)
        {
            RecordMaximum(ref preProcessorPeak,
                FindPeakAbsoluteSample(samples, sampleCount));
            if (IsFrameAllZero(samples, sampleCount))
            {
                IncrementSaturating(ref preProcessorAllZeroFrames);
            }
        }

        internal void ObservePostProcessorFrame(short[] samples,
            int sampleCount, bool muted)
        {
            RecordMaximum(ref postProcessorPeak,
                FindPeakAbsoluteSample(samples, sampleCount));
            if (!IsFrameAllZero(samples, sampleCount))
            {
                return;
            }

            IncrementSaturating(ref postProcessorAllZeroFrames);
            if (!muted)
            {
                IncrementSaturating(ref postProcessorAllZeroUnmutedFrames);
            }
        }

        internal void ObserveCompressedQueueDepth(int queueDepth)
        {
            if (queueDepth > 0)
            {
                RecordMaximum(ref compressedQueueHighWaterMark, queueDepth);
            }
        }

        internal void RecordSuccessfulSubmission(long timestamp)
        {
            if (timestamp <= 0)
            {
                return;
            }

            // Advance the baseline only when the new Stopwatch timestamp is
            // newer. A bad/out-of-order observation therefore cannot create a
            // false giant gap on the following frame.
            long previous = Interlocked.Read(
                ref lastSuccessfulSubmissionTimestamp);
            while (timestamp > previous)
            {
                long observed = Interlocked.CompareExchange(
                    ref lastSuccessfulSubmissionTimestamp, timestamp,
                    previous);
                if (observed == previous)
                {
                    if (previous > 0)
                    {
                        long gap = timestamp - previous;
                        Interlocked.Exchange(ref lastSubmissionGapTicks, gap);
                        RecordMaximum(ref maximumSubmissionGapTicks, gap);
                        IncrementSaturating(ref observedSubmissionGaps);
                    }
                    return;
                }

                previous = observed;
            }
        }

        internal static bool IsFrameAllZero(short[] samples, int sampleCount)
        {
            // Diagnostics must never turn malformed input into an audio-path
            // exception. Invalid/empty frames are not classified as silence.
            if (samples == null || sampleCount <= 0 ||
                sampleCount > samples.Length)
            {
                return false;
            }

            for (int index = 0; index < sampleCount; index++)
            {
                if (samples[index] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static int FindPeakAbsoluteSample(short[] samples,
            int sampleCount)
        {
            if (samples == null || sampleCount <= 0 ||
                sampleCount > samples.Length)
            {
                return 0;
            }

            int peak = 0;
            for (int index = 0; index < sampleCount; index++)
            {
                peak = System.Math.Max(peak,
                    System.Math.Abs((int)samples[index]));
            }
            return peak;
        }

        internal static void IncrementSaturating(ref long target)
        {
            long current = Interlocked.Read(ref target);
            while (current < long.MaxValue)
            {
                long observed = Interlocked.CompareExchange(ref target,
                    current + 1, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        private static void RecordMaximum(ref long target, long candidate)
        {
            if (candidate <= 0)
            {
                return;
            }

            long current = Interlocked.Read(ref target);
            while (candidate > current)
            {
                long observed = Interlocked.CompareExchange(ref target,
                    candidate, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
