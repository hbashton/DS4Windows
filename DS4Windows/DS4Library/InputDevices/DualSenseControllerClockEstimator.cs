/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.Threading;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Estimates the DualSense oscillator rate from its 3 MHz sensor clock.
    /// A long least-squares window rejects Bluetooth arrival jitter; callers
    /// must not use individual input-report arrival intervals as an audio
    /// clock.
    /// </summary>
    public sealed class DualSenseControllerClockEstimator
    {
        internal const double NominalSensorTicksPerSecond = 3_000_000.0;
        internal const double MeasurementWindowSeconds = 30.0;
        private const double MinimumAcceptedRatio = 0.995;
        private const double MaximumAcceptedRatio = 1.005;
        private const double MaximumHostGapSeconds = 2.0;
        private const int MinimumSamples = 100;

        private bool initialized;
        private uint previousRawTimestamp;
        private ulong unwrappedTimestamp;
        private ulong controllerEpoch;
        private long hostEpoch;
        private long previousHostTimestamp;
        private int sampleCount;
        private double sumX;
        private double sumY;
        private double sumXX;
        private double sumXY;
        private double publishedRatio = 1.0;
        private int completedWindows;

        public double Ratio => Volatile.Read(ref publishedRatio);
        public int CompletedWindows => Volatile.Read(ref completedWindows);
        public bool IsStable => CompletedWindows > 0;

        public bool Observe(uint sensorTimestamp, long hostTimestamp)
        {
            if (!initialized)
            {
                ResetWindow(sensorTimestamp, hostTimestamp);
                initialized = true;
                return false;
            }

            long hostDelta = hostTimestamp - previousHostTimestamp;
            if (hostDelta <= 0 || hostDelta >
                Stopwatch.Frequency * MaximumHostGapSeconds)
            {
                ResetWindow(sensorTimestamp, hostTimestamp);
                return false;
            }

            uint sensorDelta = unchecked(sensorTimestamp -
                previousRawTimestamp);
            previousRawTimestamp = sensorTimestamp;
            previousHostTimestamp = hostTimestamp;

            // Duplicated HID samples contain no new oscillator information.
            if (sensorDelta == 0)
            {
                return false;
            }

            if (sensorDelta > NominalSensorTicksPerSecond *
                MaximumHostGapSeconds)
            {
                ResetWindow(sensorTimestamp, hostTimestamp);
                return false;
            }

            unwrappedTimestamp += sensorDelta;
            double x = (hostTimestamp - hostEpoch) /
                (double)Stopwatch.Frequency;
            double y = (unwrappedTimestamp - controllerEpoch) /
                NominalSensorTicksPerSecond;

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

            ResetWindow(sensorTimestamp, hostTimestamp);
            return published;
        }

        private void ResetWindow(uint sensorTimestamp, long hostTimestamp)
        {
            previousRawTimestamp = sensorTimestamp;
            unwrappedTimestamp = sensorTimestamp;
            controllerEpoch = sensorTimestamp;
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
