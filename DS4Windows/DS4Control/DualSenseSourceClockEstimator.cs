using System;
using System.Diagnostics;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Estimates the long-run production rate of a block-based PCM source.
    /// A least-squares fit over thirty seconds rejects callback batching and
    /// scheduler jitter; instantaneous ring occupancy is deliberately absent.
    /// </summary>
    internal sealed class DualSenseSourceClockEstimator
    {
        internal const double MeasurementWindowSeconds = 30.0;
        private const double MinimumAcceptedRatio = 0.995;
        private const double MaximumAcceptedRatio = 1.005;
        private const double MaximumHostGapSeconds = 2.0;
        private const int MinimumSamples = 100;

        private readonly double nominalFramesPerSecond;
        private bool initialized;
        private long frameEpoch;
        private long previousFramePosition;
        private long hostEpoch;
        private long previousHostTimestamp;
        private int sampleCount;
        private double sumX;
        private double sumY;
        private double sumXX;
        private double sumXY;
        private double publishedRatio = 1.0;
        private int completedWindows;

        public DualSenseSourceClockEstimator(double nominalFramesPerSecond)
        {
            if (!double.IsFinite(nominalFramesPerSecond) ||
                nominalFramesPerSecond <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nominalFramesPerSecond));
            }

            this.nominalFramesPerSecond = nominalFramesPerSecond;
        }

        public double Ratio => Volatile.Read(ref publishedRatio);
        public int CompletedWindows => Volatile.Read(ref completedWindows);
        public bool IsStable => CompletedWindows > 0;

        public void Reset()
        {
            initialized = false;
            frameEpoch = 0;
            previousFramePosition = 0;
            hostEpoch = 0;
            previousHostTimestamp = 0;
            sampleCount = 0;
            sumX = 0.0;
            sumY = 0.0;
            sumXX = 0.0;
            sumXY = 0.0;
            Volatile.Write(ref publishedRatio, 1.0);
            Volatile.Write(ref completedWindows, 0);
        }

        public bool Observe(long framePosition, long hostTimestamp)
        {
            if (!initialized)
            {
                ResetWindow(framePosition, hostTimestamp);
                initialized = true;
                return false;
            }

            long hostDelta = hostTimestamp - previousHostTimestamp;
            long frameDelta = framePosition - previousFramePosition;
            if (hostDelta <= 0 || hostDelta >
                Stopwatch.Frequency * MaximumHostGapSeconds ||
                frameDelta < 0 || frameDelta >
                nominalFramesPerSecond * MaximumHostGapSeconds)
            {
                ResetWindow(framePosition, hostTimestamp);
                return false;
            }

            previousHostTimestamp = hostTimestamp;
            previousFramePosition = framePosition;
            if (frameDelta == 0)
            {
                return false;
            }

            double x = (hostTimestamp - hostEpoch) /
                (double)Stopwatch.Frequency;
            double y = (framePosition - frameEpoch) /
                nominalFramesPerSecond;
            sampleCount++;
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;

            if (x < MeasurementWindowSeconds || sampleCount < MinimumSamples)
            {
                return false;
            }

            bool published = false;
            double denominator = sampleCount * sumXX - sumX * sumX;
            if (denominator > 0.0)
            {
                double ratio = (sampleCount * sumXY - sumX * sumY) /
                    denominator;
                if (double.IsFinite(ratio) &&
                    ratio >= MinimumAcceptedRatio &&
                    ratio <= MaximumAcceptedRatio)
                {
                    Volatile.Write(ref publishedRatio, ratio);
                    Interlocked.Increment(ref completedWindows);
                    published = true;
                }
            }

            ResetWindow(framePosition, hostTimestamp);
            return published;
        }

        private void ResetWindow(long framePosition, long hostTimestamp)
        {
            frameEpoch = framePosition;
            previousFramePosition = framePosition;
            hostEpoch = hostTimestamp;
            previousHostTimestamp = hostTimestamp;
            sampleCount = 0;
            sumX = 0.0;
            sumY = 0.0;
            sumXX = 0.0;
            sumXY = 0.0;
        }
    }
}
