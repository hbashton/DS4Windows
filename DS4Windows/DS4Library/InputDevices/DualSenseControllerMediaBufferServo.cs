using System;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Slowly steers DualSense media-report cadence from the controller's
    /// noisy Bluetooth input byte 65. The value tracks queued media playout,
    /// but combined haptics can create isolated impossible jumps. Consequently
    /// this controller operates only on robust half-second medians and never
    /// phase-locks, bursts, skips, or changes source resampling.
    /// </summary>
    internal sealed class DualSenseControllerMediaBufferServo
    {
        // The live byte is not the configured 0x35 buffer-depth field. Local
        // controller traces put its steady median near 68 even when that field
        // is 96. Treat it as a slow occupancy signal around the measured
        // equilibrium; trying to drive it to 96 over-clocked presentation by
        // ~4,000 ppm and exhausted the fixed-rate producer reservoir.
        internal const int TargetLevel = 68;
        internal const int LowerDeadband = 62;
        internal const int UpperDeadband = 80;
        internal const double MinimumRatio = 0.999;
        internal const double MaximumRatio = 1.001;

        private const int MaximumValidLevel = 120;
        private const int ResetJumpThreshold = 32;
        private const int RelockTolerance = 8;
        private const int MinimumBucketSamples = 10;
        private const int HistoryCapacity = 8;
        private const int RequiredOutsideBuckets = 2;
        private const double ProportionalContribution = 0.00035;
        private const double IntegralContribution = 0.00035;
        private const double SlopeContribution = 0.00030;
        private const double RatioSlewPerSecond = 0.00020;

        private readonly long clockFrequency;
        private readonly long bucketTicks;
        private readonly long staleTicks;
        private readonly long futureToleranceTicks;
        private readonly long relockTicks;
        private readonly int[] histogram = new int[MaximumValidLevel + 1];
        private readonly double[] historyLevels = new double[HistoryCapacity];
        private readonly long[] historyQpc = new long[HistoryCapacity];
        private readonly double[] slopeScratch =
            new double[HistoryCapacity * (HistoryCapacity - 1) / 2];

        private int bucketSamples;
        private long bucketStartQpc;
        private int historyHead;
        private int historyCount;
        private long lastObservationQpc;
        private long lastControlQpc;
        private long lastSlewQpc;
        private int lastAcceptedLevel = -1;
        private int relockCandidate = -1;
        private int relockCandidateCount;
        private long relockCandidateStartQpc;
        private double integral;
        private double desiredRatio = 1.0;
        private double currentRatio = 1.0;
        private int outsideDirection;
        private int outsideBucketCount;

        public DualSenseControllerMediaBufferServo(long clockFrequency)
        {
            if (clockFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clockFrequency));
            }

            this.clockFrequency = clockFrequency;
            bucketTicks = Math.Max(1, clockFrequency);
            staleTicks = Math.Max(1, clockFrequency * 2);
            futureToleranceTicks = Math.Max(1, clockFrequency / 20);
            relockTicks = Math.Max(1, clockFrequency / 10);
        }

        public double CurrentRatio => currentRatio;
        public double DesiredRatio => desiredRatio;
        public double LastMedian { get; private set; } = double.NaN;
        public int CompletedBuckets { get; private set; }

        public void Reset()
        {
            Array.Clear(histogram, 0, histogram.Length);
            Array.Clear(historyLevels, 0, historyLevels.Length);
            Array.Clear(historyQpc, 0, historyQpc.Length);
            bucketSamples = 0;
            bucketStartQpc = 0;
            historyHead = 0;
            historyCount = 0;
            lastObservationQpc = 0;
            lastControlQpc = 0;
            lastSlewQpc = 0;
            lastAcceptedLevel = -1;
            ResetRelockCandidate();
            integral = 0.0;
            desiredRatio = 1.0;
            currentRatio = 1.0;
            outsideDirection = 0;
            outsideBucketCount = 0;
            LastMedian = double.NaN;
            CompletedBuckets = 0;
        }

        /// <summary>
        /// Observes at most one new feedback sample and advances the smooth
        /// output ratio to <paramref name="nowQpc"/>. Repeated calls with the
        /// same observation are expected from the 10.667 ms media loop.
        /// </summary>
        public double Update(int level, long observationQpc, long nowQpc)
        {
            if (nowQpc <= 0)
            {
                return currentRatio;
            }

            bool validLevel = level > 0 && level <= MaximumValidLevel;
            bool fresh = validLevel && observationQpc > 0 &&
                observationQpc <= nowQpc + futureToleranceTicks &&
                nowQpc - observationQpc <= staleTicks;
            if (fresh && observationQpc > lastObservationQpc)
            {
                lastObservationQpc = observationQpc;
                ObserveSample(level, observationQpc);
            }
            else if (lastObservationQpc == 0 ||
                nowQpc - lastObservationQpc > staleTicks)
            {
                desiredRatio = 1.0;
            }

            SlewToDesired(nowQpc);
            return currentRatio;
        }

        private void ObserveSample(int level, long observationQpc)
        {
            if (lastAcceptedLevel >= 0 &&
                Math.Abs(level - lastAcceptedLevel) > ResetJumpThreshold)
            {
                if (!ObserveRelockCandidate(level, observationQpc))
                {
                    return;
                }
            }
            else
            {
                ResetRelockCandidate();
            }

            lastAcceptedLevel = level;
            if (bucketStartQpc == 0)
            {
                bucketStartQpc = observationQpc;
            }
            else if (observationQpc - bucketStartQpc >= bucketTicks)
            {
                CompleteBucket(observationQpc);
                Array.Clear(histogram, 0, histogram.Length);
                bucketSamples = 0;
                bucketStartQpc = observationQpc;
            }

            histogram[level]++;
            bucketSamples++;
        }

        private bool ObserveRelockCandidate(int level, long observationQpc)
        {
            bool sameCandidate = relockCandidate >= 0 &&
                Math.Abs(level - relockCandidate) <= RelockTolerance &&
                observationQpc - relockCandidateStartQpc <= bucketTicks;
            if (!sameCandidate)
            {
                relockCandidate = level;
                relockCandidateCount = 1;
                relockCandidateStartQpc = observationQpc;
                return false;
            }

            relockCandidate = (relockCandidate * relockCandidateCount + level) /
                (relockCandidateCount + 1);
            relockCandidateCount++;
            bool confirmed = relockCandidateCount >= 3 &&
                observationQpc - relockCandidateStartQpc >= relockTicks;
            if (confirmed)
            {
                ResetRelockCandidate();
            }

            return confirmed;
        }

        private void CompleteBucket(long completedAtQpc)
        {
            if (bucketSamples < MinimumBucketSamples)
            {
                return;
            }

            int middle = (bucketSamples - 1) / 2;
            int accumulated = 0;
            int median = 1;
            for (; median <= MaximumValidLevel; median++)
            {
                accumulated += histogram[median];
                if (accumulated > middle)
                {
                    break;
                }
            }

            LastMedian = median;
            CompletedBuckets++;
            historyLevels[historyHead] = median;
            historyQpc[historyHead] = completedAtQpc;
            historyHead = (historyHead + 1) % HistoryCapacity;
            historyCount = Math.Min(historyCount + 1, HistoryCapacity);

            double elapsedSeconds = lastControlQpc == 0 ? 0.5 :
                Math.Clamp((completedAtQpc - lastControlQpc) /
                    (double)clockFrequency, 0.05, 1.0);
            lastControlQpc = completedAtQpc;

            int direction = median < LowerDeadband ? 1 :
                median > UpperDeadband ? -1 : 0;
            if (direction == 0)
            {
                outsideDirection = 0;
                outsideBucketCount = 0;
            }
            else if (direction == outsideDirection)
            {
                outsideBucketCount++;
            }
            else
            {
                outsideDirection = direction;
                outsideBucketCount = 1;
            }

            double normalizedError = outsideBucketCount >=
                RequiredOutsideBuckets ?
                    Math.Clamp((TargetLevel - median) / 24.0, -1.0, 1.0) :
                    0.0;
            if (normalizedError == 0.0)
            {
                integral = MoveTowards(integral, 0.0,
                    elapsedSeconds / 2.0);
            }
            else
            {
                integral = Math.Clamp(integral +
                    normalizedError * elapsedSeconds / 8.0, -1.0, 1.0);
            }

            // A negative slope means the controller reserve is draining, so
            // cadence must increase. Regression over the same robust buckets
            // avoids turning haptics-induced single-byte jumps into phase jitter.
            double levelSlopePerSecond = CalculateHistorySlope();
            double normalizedSlope = historyCount >= 4 ? Math.Clamp(
                -levelSlopePerSecond / 8.0, -1.0, 1.0) : 0.0;
            double correction =
                normalizedError * ProportionalContribution +
                integral * IntegralContribution +
                normalizedSlope * SlopeContribution;
            desiredRatio = Math.Clamp(1.0 + correction,
                MinimumRatio, MaximumRatio);
        }

        private double CalculateHistorySlope()
        {
            if (historyCount < 2)
            {
                return 0.0;
            }

            int oldest = (historyHead - historyCount + HistoryCapacity) %
                HistoryCapacity;
            int slopeCount = 0;
            for (int firstOffset = 0; firstOffset < historyCount - 1;
                firstOffset++)
            {
                int first = (oldest + firstOffset) % HistoryCapacity;
                for (int secondOffset = firstOffset + 1;
                    secondOffset < historyCount; secondOffset++)
                {
                    int second = (oldest + secondOffset) % HistoryCapacity;
                    double elapsedSeconds = (historyQpc[second] -
                        historyQpc[first]) / (double)clockFrequency;
                    if (elapsedSeconds > 0.0)
                    {
                        slopeScratch[slopeCount++] =
                            (historyLevels[second] - historyLevels[first]) /
                            elapsedSeconds;
                    }
                }
            }

            if (slopeCount == 0)
            {
                return 0.0;
            }

            Array.Sort(slopeScratch, 0, slopeCount);
            int middle = slopeCount / 2;
            return (slopeCount & 1) != 0 ? slopeScratch[middle] :
                (slopeScratch[middle - 1] + slopeScratch[middle]) * 0.5;
        }

        private void SlewToDesired(long nowQpc)
        {
            if (lastSlewQpc == 0)
            {
                lastSlewQpc = nowQpc;
                return;
            }

            double elapsedSeconds = Math.Clamp(
                (nowQpc - lastSlewQpc) / (double)clockFrequency, 0.0, 1.0);
            lastSlewQpc = nowQpc;
            currentRatio = MoveTowards(currentRatio, desiredRatio,
                RatioSlewPerSecond * elapsedSeconds);
        }

        private void ResetRelockCandidate()
        {
            relockCandidate = -1;
            relockCandidateCount = 0;
            relockCandidateStartQpc = 0;
        }

        private static double MoveTowards(double value, double target,
            double maximumDelta)
        {
            if (value < target)
            {
                return Math.Min(value + maximumDelta, target);
            }

            return Math.Max(value - maximumDelta, target);
        }
    }
}
