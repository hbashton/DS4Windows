using System;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Estimates the physical DualShock 4 clock rate relative to host
    /// Stopwatch time. The controller's 16-bit sensor clock advances at 187.5 kHz
    /// (16/3 microseconds per tick). Bluetooth delivery delay is positive and
    /// bursty, so one minimum-delay observation is retained per controller
    /// second and a rolling least-squares slope is fitted through that lower
    /// envelope instead of trusting two noisy endpoints.
    /// </summary>
    internal sealed class DualShock4ControllerClockDiscipline
    {
        internal const int ControllerTicksPerSecond = 187_500;
        internal const int EnvelopeBucketControllerTicks =
            ControllerTicksPerSecond;
        internal const int MaximumControllerIntervalTicks =
            ControllerTicksPerSecond / 10;
        internal const int MinimumFitPoints = 8;
        internal const int MaximumFitPoints = 32;
        internal const double MinimumAcceptedObservationRatio = 0.98;
        internal const double MaximumAcceptedObservationRatio = 1.02;
        internal const double MinimumPublishedRatio = 0.995;
        internal const double MaximumPublishedRatio = 1.005;
        internal const double SmoothingFactor = 0.20;

        private readonly ulong[] fitControllerTicks =
            new ulong[MaximumFitPoints];
        private readonly long[] fitHostTicks = new long[MaximumFitPoints];

        private bool initialized;
        private ushort previousControllerTimestamp;
        private long previousHostTimestamp;
        private long stopwatchFrequency;
        private long originHostTimestamp;
        private ulong cumulativeControllerTicks;
        private ulong envelopeBucket;
        private bool hasEnvelopeCandidate;
        private ulong envelopeControllerTicks;
        private long envelopeHostTimestamp;
        private double envelopeResidualTicks;
        private int fitStart;
        private int fitCount;
        private double publishedRatio = 1.0;
        private double rawRatio = 1.0;
        private int acceptedFits;
        private int rejectedFits;

        internal double Ratio => Volatile.Read(ref publishedRatio);
        internal double RawRatio => Volatile.Read(ref rawRatio);
        internal bool HasEstimate => Volatile.Read(ref acceptedFits) > 0;
        internal int AcceptedFits => Volatile.Read(ref acceptedFits);
        internal int RejectedFits => Volatile.Read(ref rejectedFits);
        internal ulong CumulativeControllerTicks =>
            cumulativeControllerTicks;

        internal void Reset()
        {
            initialized = false;
            previousControllerTimestamp = 0;
            previousHostTimestamp = 0;
            stopwatchFrequency = 0;
            originHostTimestamp = 0;
            cumulativeControllerTicks = 0;
            envelopeBucket = 0;
            hasEnvelopeCandidate = false;
            envelopeControllerTicks = 0;
            envelopeHostTimestamp = 0;
            envelopeResidualTicks = 0.0;
            fitStart = 0;
            fitCount = 0;
            Volatile.Write(ref publishedRatio, 1.0);
            Volatile.Write(ref rawRatio, 1.0);
            Volatile.Write(ref acceptedFits, 0);
            Volatile.Write(ref rejectedFits, 0);
        }

        /// <summary>
        /// Adds one CRC-validated physical-controller observation. Returns
        /// true when it contributes a valid advancing clock interval.
        /// </summary>
        internal bool Observe(ushort controllerTimestamp,
            long hostTimestamp, long frequency)
        {
            if (frequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequency));
            }

            if (!initialized || frequency != stopwatchFrequency)
            {
                StartMeasurement(controllerTimestamp, hostTimestamp,
                    frequency);
                return false;
            }

            ushort controllerDelta = unchecked((ushort)(
                controllerTimestamp - previousControllerTimestamp));
            long hostDelta = hostTimestamp - previousHostTimestamp;
            if (controllerDelta == 0)
            {
                // Duplicate sensor samples carry no controller-clock
                // information. Keep the last distinct anchor so the next
                // advancing sample spans the complete host interval.
                return false;
            }

            long maximumHostGapTicks = Math.Max(1L, frequency / 4);
            if (controllerDelta > MaximumControllerIntervalTicks ||
                hostDelta <= 0 || hostDelta > maximumHostGapTicks)
            {
                RestartWindow(controllerTimestamp, hostTimestamp);
                return false;
            }

            previousControllerTimestamp = controllerTimestamp;
            previousHostTimestamp = hostTimestamp;
            cumulativeControllerTicks += controllerDelta;

            ulong bucket = cumulativeControllerTicks /
                EnvelopeBucketControllerTicks;
            double nominalHostTicks = cumulativeControllerTicks *
                (double)frequency / ControllerTicksPerSecond;
            double residual = hostTimestamp - originHostTimestamp -
                nominalHostTicks;
            if (bucket != envelopeBucket)
            {
                FinalizeEnvelopeCandidate();
                envelopeBucket = bucket;
                hasEnvelopeCandidate = false;
            }

            if (!hasEnvelopeCandidate || residual < envelopeResidualTicks)
            {
                hasEnvelopeCandidate = true;
                envelopeControllerTicks = cumulativeControllerTicks;
                envelopeHostTimestamp = hostTimestamp;
                envelopeResidualTicks = residual;
            }

            return true;
        }

        private void StartMeasurement(ushort controllerTimestamp,
            long hostTimestamp, long frequency)
        {
            initialized = true;
            previousControllerTimestamp = controllerTimestamp;
            previousHostTimestamp = hostTimestamp;
            stopwatchFrequency = frequency;
            originHostTimestamp = hostTimestamp;
            cumulativeControllerTicks = 0;
            envelopeBucket = 0;
            hasEnvelopeCandidate = true;
            envelopeControllerTicks = 0;
            envelopeHostTimestamp = hostTimestamp;
            envelopeResidualTicks = 0.0;
            fitStart = 0;
            fitCount = 0;
        }

        private void RestartWindow(ushort controllerTimestamp,
            long hostTimestamp)
        {
            // Preserve the already-smoothed ratio across a transient radio or
            // scheduler stall, but do not join regression points across it.
            StartMeasurement(controllerTimestamp, hostTimestamp,
                stopwatchFrequency);
        }

        private void FinalizeEnvelopeCandidate()
        {
            if (!hasEnvelopeCandidate)
            {
                return;
            }

            int index;
            if (fitCount < MaximumFitPoints)
            {
                index = (fitStart + fitCount) % MaximumFitPoints;
                fitCount++;
            }
            else
            {
                index = fitStart;
                fitStart = (fitStart + 1) % MaximumFitPoints;
            }
            fitControllerTicks[index] = envelopeControllerTicks;
            fitHostTicks[index] = envelopeHostTimestamp;
            TryUpdateRatio();
        }

        private void TryUpdateRatio()
        {
            if (fitCount < MinimumFitPoints)
            {
                return;
            }

            int firstIndex = fitStart;
            int lastIndex = (fitStart + fitCount - 1) % MaximumFitPoints;
            ulong firstController = fitControllerTicks[firstIndex];
            ulong controllerSpan = fitControllerTicks[lastIndex] -
                firstController;
            if (controllerSpan < (ulong)(MinimumFitPoints - 2) *
                    EnvelopeBucketControllerTicks)
            {
                return;
            }

            long firstHost = fitHostTicks[firstIndex];
            double sumX = 0.0;
            double sumY = 0.0;
            double sumXX = 0.0;
            double sumXY = 0.0;
            for (int point = 0; point < fitCount; point++)
            {
                int index = (fitStart + point) % MaximumFitPoints;
                double x = fitControllerTicks[index] - firstController;
                double y = fitHostTicks[index] - firstHost;
                sumX += x;
                sumY += y;
                sumXX += x * x;
                sumXY += x * y;
            }

            double denominator = fitCount * sumXX - sumX * sumX;
            if (denominator <= 0.0)
            {
                return;
            }

            double hostTicksPerControllerTick =
                (fitCount * sumXY - sumX * sumY) / denominator;
            double observationRatio = stopwatchFrequency /
                (hostTicksPerControllerTick * ControllerTicksPerSecond);
            if (!double.IsFinite(observationRatio) ||
                observationRatio < MinimumAcceptedObservationRatio ||
                observationRatio > MaximumAcceptedObservationRatio)
            {
                if (double.IsFinite(observationRatio))
                {
                    Volatile.Write(ref rawRatio, observationRatio);
                }
                Interlocked.Increment(ref rejectedFits);
                return;
            }

            Volatile.Write(ref rawRatio, observationRatio);
            double current = Ratio;
            double smoothed = current + SmoothingFactor *
                (observationRatio - current);
            Volatile.Write(ref publishedRatio, Math.Clamp(smoothed,
                MinimumPublishedRatio, MaximumPublishedRatio));
            Interlocked.Increment(ref acceptedFits);
        }
    }
}
