/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Numerics;

namespace DS4Windows.Switch2;

/// <summary>
/// Allocation-free stationary gyro-bias calibration for one physical IMU.
/// Time comes from the physical report's QPC domain, not wall-clock callbacks.
/// A bias is committed only after one contiguous, acceleration-qualified and
/// low-motion interval. Duplicate cached joined-half observations never count
/// twice. Manual recalibration keeps the last committed bias active until a
/// complete replacement interval is available.
/// </summary>
internal sealed class Switch2StationaryGyroCalibration
{
    internal const double RequiredStableSeconds = 5.0;
    internal const int MinimumStableSamples = 100;
    internal const double MaximumSampleDeltaSeconds = 0.10;
    internal const float MinimumAccelerationG = 0.85f;
    internal const float MaximumAccelerationG = 1.15f;
    internal const float MaximumStationaryGyroDps = 1.0f;
    internal const float MaximumCommittedBiasDps = 2.0f;

    private Vector3 committedBias;
    private bool hasCommittedBias;
    private bool calibrationRequested = true;
    private bool hasTimestamp;
    private long lastTimestampQpc;
    private long qpcFrequency;
    private double stableSeconds;
    private double weightedGyroX;
    private double weightedGyroY;
    private double weightedGyroZ;
    private double totalWeightSeconds;
    private int stableSamples;
    private ulong calibrationEpoch = 1;
    private ulong biasRevision;

    internal Vector3 Bias => committedBias;

    internal bool HasCommittedBias => hasCommittedBias;

    internal bool IsCalibrating => calibrationRequested;

    internal ulong CalibrationEpoch => calibrationEpoch;

    internal ulong BiasRevision => biasRevision;

    internal long CalibrationElapsedMilliseconds => calibrationRequested ?
        (long)Math.Min(long.MaxValue, stableSeconds * 1000.0) : 0;

    internal bool TryObserve(in Vector3 gyroscope,
        in Vector3 accelerometer, float gyroLsbPerDegreeSecond,
        float accelerometerLsbPerG, long timestampQpc,
        long observationQpcFrequency, out Vector3 correctedGyroscope)
    {
        correctedGyroscope = default;
        if (!IsFinite(gyroscope) || !IsFinite(accelerometer) ||
            !float.IsFinite(gyroLsbPerDegreeSecond) ||
            gyroLsbPerDegreeSecond <= 0.0f ||
            !float.IsFinite(accelerometerLsbPerG) ||
            accelerometerLsbPerG <= 0.0f || timestampQpc < 0 ||
            observationQpcFrequency <= 0)
        {
            ResetObservationClock();
            return false;
        }

        correctedGyroscope = gyroscope - committedBias;
        if (!hasTimestamp || qpcFrequency != observationQpcFrequency)
        {
            ResetAccumulation();
            hasTimestamp = true;
            lastTimestampQpc = timestampQpc;
            qpcFrequency = observationQpcFrequency;
            return true;
        }

        if (timestampQpc == lastTimestampQpc)
        {
            return true;
        }
        if (timestampQpc < lastTimestampQpc)
        {
            ResetObservationClock();
            hasTimestamp = true;
            lastTimestampQpc = timestampQpc;
            qpcFrequency = observationQpcFrequency;
            return true;
        }

        double elapsedSeconds = (timestampQpc - lastTimestampQpc) /
            (double)observationQpcFrequency;
        lastTimestampQpc = timestampQpc;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0 ||
            elapsedSeconds > MaximumSampleDeltaSeconds)
        {
            ResetAccumulation();
            return true;
        }

        if (!calibrationRequested)
        {
            return true;
        }

        float accelerationG = accelerometer.Length() /
            accelerometerLsbPerG;
        float correctedGyroDps = correctedGyroscope.Length() /
            gyroLsbPerDegreeSecond;
        bool stationary = float.IsFinite(accelerationG) &&
            accelerationG >= MinimumAccelerationG &&
            accelerationG <= MaximumAccelerationG &&
            float.IsFinite(correctedGyroDps) &&
            correctedGyroDps <= MaximumStationaryGyroDps;
        if (!stationary)
        {
            ResetAccumulation();
            return true;
        }

        stableSeconds += elapsedSeconds;
        weightedGyroX += gyroscope.X * elapsedSeconds;
        weightedGyroY += gyroscope.Y * elapsedSeconds;
        weightedGyroZ += gyroscope.Z * elapsedSeconds;
        totalWeightSeconds += elapsedSeconds;
        stableSamples = stableSamples == int.MaxValue ? int.MaxValue :
            stableSamples + 1;
        if (stableSeconds < RequiredStableSeconds ||
            stableSamples < MinimumStableSamples ||
            totalWeightSeconds <= 0.0)
        {
            return true;
        }

        Vector3 replacement = new(
            (float)(weightedGyroX / totalWeightSeconds),
            (float)(weightedGyroY / totalWeightSeconds),
            (float)(weightedGyroZ / totalWeightSeconds));
        if (!IsFinite(replacement) || replacement.Length() /
                gyroLsbPerDegreeSecond > MaximumCommittedBiasDps)
        {
            ResetAccumulation();
            return true;
        }

        committedBias = replacement;
        hasCommittedBias = true;
        calibrationRequested = false;
        AdvanceBiasRevision();
        AdvanceCalibrationEpoch();
        correctedGyroscope = gyroscope - committedBias;
        ResetAccumulation();
        return true;
    }

    internal void RestartPreservingBias()
    {
        calibrationRequested = true;
        AdvanceCalibrationEpoch();
        ResetObservationClock();
    }

    internal void ResetObservationState() => ResetObservationClock();

    internal bool TryGetBiasDps(float gyroLsbPerDegreeSecond,
        out Vector3 biasDps)
    {
        if (!hasCommittedBias ||
            !float.IsFinite(gyroLsbPerDegreeSecond) ||
            gyroLsbPerDegreeSecond <= 0.0f)
        {
            biasDps = default;
            return false;
        }
        biasDps = committedBias / gyroLsbPerDegreeSecond;
        return IsFinite(biasDps) && biasDps.Length() <=
            MaximumCommittedBiasDps;
    }

    internal bool TryAdoptBiasDps(in Vector3 biasDps,
        float gyroLsbPerDegreeSecond)
    {
        if (!IsFinite(biasDps) || biasDps.Length() >
                MaximumCommittedBiasDps ||
            !float.IsFinite(gyroLsbPerDegreeSecond) ||
            gyroLsbPerDegreeSecond <= 0.0f)
        {
            return false;
        }
        Vector3 replacement = biasDps * gyroLsbPerDegreeSecond;
        if (!IsFinite(replacement))
        {
            return false;
        }

        committedBias = replacement;
        hasCommittedBias = true;
        calibrationRequested = false;
        AdvanceBiasRevision();
        AdvanceCalibrationEpoch();
        ResetObservationClock();
        return true;
    }

    internal void Reset()
    {
        committedBias = default;
        hasCommittedBias = false;
        calibrationRequested = true;
        calibrationEpoch = 1;
        biasRevision = 0;
        ResetObservationClock();
    }

    private void AdvanceBiasRevision()
    {
        biasRevision = unchecked(biasRevision + 1);
        if (biasRevision == 0)
        {
            biasRevision = 1;
        }
    }

    private void AdvanceCalibrationEpoch()
    {
        calibrationEpoch = unchecked(calibrationEpoch + 1);
        if (calibrationEpoch == 0)
        {
            calibrationEpoch = 1;
        }
    }

    private void ResetObservationClock()
    {
        hasTimestamp = false;
        lastTimestampQpc = 0;
        qpcFrequency = 0;
        ResetAccumulation();
    }

    private void ResetAccumulation()
    {
        stableSeconds = 0.0;
        weightedGyroX = 0.0;
        weightedGyroY = 0.0;
        weightedGyroZ = 0.0;
        totalWeightSeconds = 0.0;
        stableSamples = 0;
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
