using System;
using System.Diagnostics;

namespace DS4Windows
{
    /// <summary>
    /// Learns residual drift between VIIPER's direct PCM producer and the
    /// physical DualSense consumer from exact frame balance. The long,
    /// non-overlapping fit rejects the 320/512-frame callback sawtooth that
    /// must never be treated as an instantaneous audio-clock error.
    /// </summary>
    internal sealed class DualSenseDirectPcmBalanceClockServo
    {
        internal const double MeasurementWindowSeconds = 30.0;
        internal const double ErrorDeadbandPpm = 3.0;
        internal const double CorrectionGain = 0.5;
        internal const double MaximumAcceptedErrorPpm = 500.0;
        internal const double MaximumTrimPpm = 250.0;
        internal const double TrimSlewPpmPerSecond = 5.0;
        private const int MinimumSamples = 100;
        private const double NominalFramesPerSecond = 48000.0;
        private const long MinimumWindowFrames =
            (long)(NominalFramesPerSecond * MeasurementWindowSeconds);

        private bool initialized;
        private long producedEpoch;
        private long consumedEpoch;
        private long hostEpoch;
        private long previousProducedPosition;
        private long previousConsumedPosition;
        private long previousHostTimestamp;
        private int sampleCount;
        private double sumX;
        private double sumY;
        private double sumXX;
        private double sumXY;
        private double targetTrimPpm;
        private double appliedTrimPpm;
        private double lastMeasuredErrorPpm;
        private int completedWindows;
        private int rejectedWindows;
        private int resetWindows;

        internal double TargetTrimRatio => 1.0 + targetTrimPpm / 1_000_000.0;
        internal double AppliedTrimRatio => 1.0 + appliedTrimPpm / 1_000_000.0;
        internal double TargetTrimPpm => targetTrimPpm;
        internal double AppliedTrimPpm => appliedTrimPpm;
        internal double LastMeasuredErrorPpm => lastMeasuredErrorPpm;
        internal int CompletedWindows => completedWindows;
        internal int RejectedWindows => rejectedWindows;
        internal int ResetWindows => resetWindows;

        internal bool Observe(long producedFramePosition,
            long consumedFramePosition, long hostTimestamp)
        {
            if (!initialized)
            {
                BeginWindow(producedFramePosition, consumedFramePosition,
                    hostTimestamp);
                return false;
            }

            long producedStep = producedFramePosition -
                previousProducedPosition;
            long consumedStep = consumedFramePosition -
                previousConsumedPosition;
            long hostStep = hostTimestamp - previousHostTimestamp;
            if (producedStep < 0 || consumedStep <= 0 || hostStep <= 0)
            {
                ResetWindow();
                BeginWindow(producedFramePosition, consumedFramePosition,
                    hostTimestamp);
                return false;
            }

            previousProducedPosition = producedFramePosition;
            previousConsumedPosition = consumedFramePosition;
            previousHostTimestamp = hostTimestamp;

            long consumedFrames = consumedFramePosition - consumedEpoch;
            long producedFrames = producedFramePosition - producedEpoch;
            if (consumedFrames <= 0 || producedFrames < 0)
            {
                return false;
            }

            // Regress balance against exact consumed audio time. Host time is
            // only the minimum-window guard, never the rate signal.
            double x = consumedFrames / NominalFramesPerSecond;
            double y = producedFrames - (double)consumedFrames;
            sampleCount++;
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;

            double elapsedSeconds = (hostTimestamp - hostEpoch) /
                (double)Stopwatch.Frequency;
            if (elapsedSeconds < MeasurementWindowSeconds ||
                consumedFrames < MinimumWindowFrames ||
                sampleCount < MinimumSamples)
            {
                return false;
            }

            bool accepted = false;
            double denominator = sampleCount * sumXX - sumX * sumX;
            if (denominator > 0.0)
            {
                double balanceFramesPerSecond =
                    (sampleCount * sumXY - sumX * sumY) / denominator;
                double errorPpm = balanceFramesPerSecond /
                    NominalFramesPerSecond * 1_000_000.0;
                if (double.IsFinite(errorPpm) &&
                    Math.Abs(errorPpm) <= MaximumAcceptedErrorPpm)
                {
                    lastMeasuredErrorPpm = errorPpm;
                    if (Math.Abs(errorPpm) > ErrorDeadbandPpm)
                    {
                        targetTrimPpm = Math.Clamp(targetTrimPpm +
                            errorPpm * CorrectionGain,
                            -MaximumTrimPpm, MaximumTrimPpm);
                    }

                    completedWindows++;
                    accepted = true;
                }
            }

            if (!accepted)
            {
                rejectedWindows++;
            }

            BeginWindow(producedFramePosition, consumedFramePosition,
                hostTimestamp);
            return accepted;
        }

        internal double AdvanceAppliedTrim(double elapsedSeconds)
        {
            if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0)
            {
                return AppliedTrimRatio;
            }

            double maximumStepPpm = TrimSlewPpmPerSecond * elapsedSeconds;
            appliedTrimPpm += Math.Clamp(targetTrimPpm - appliedTrimPpm,
                -maximumStepPpm, maximumStepPpm);
            return AppliedTrimRatio;
        }

        internal void ResetWindow()
        {
            initialized = false;
            sampleCount = 0;
            sumX = 0.0;
            sumY = 0.0;
            sumXX = 0.0;
            sumXY = 0.0;
            resetWindows++;
        }

        internal void ResetLifecycle()
        {
            ResetWindow();
            targetTrimPpm = 0.0;
            appliedTrimPpm = 0.0;
            lastMeasuredErrorPpm = 0.0;
        }

        private void BeginWindow(long producedFramePosition,
            long consumedFramePosition, long hostTimestamp)
        {
            initialized = true;
            producedEpoch = producedFramePosition;
            consumedEpoch = consumedFramePosition;
            hostEpoch = hostTimestamp;
            previousProducedPosition = producedFramePosition;
            previousConsumedPosition = consumedFramePosition;
            previousHostTimestamp = hostTimestamp;
            sampleCount = 0;
            sumX = 0.0;
            sumY = 0.0;
            sumXX = 0.0;
            sumXY = 0.0;
        }
    }
}
