/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The relative magnetic-closure policy and conservative limits in this file are
adapted from the GPL-3.0 Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/gyro.py
(MotionMagneticClosureEstimator and its RobustBiasRate law). The implementation
is allocation-free DS4Windows code and remains inside the existing serialized
Switch 2 motion projection.
*/

using System;
using System.Numerics;

namespace DS4Windows.Switch2;

internal readonly struct Switch2MagnetometerYawAssistResult
{
    internal Switch2MagnetometerYawAssistResult(Vector3 gyroscope,
        bool magneticSampleAccepted, bool correctionApplied,
        float correctionDps, float estimatedBiasDps, int validBuckets)
    {
        Gyroscope = gyroscope;
        MagneticSampleAccepted = magneticSampleAccepted;
        CorrectionApplied = correctionApplied;
        CorrectionDps = correctionDps;
        EstimatedBiasDps = estimatedBiasDps;
        ValidBuckets = validBuckets;
    }

    internal Vector3 Gyroscope { get; }

    internal bool MagneticSampleAccepted { get; }

    internal bool CorrectionApplied { get; }

    internal float CorrectionDps { get; }

    internal float EstimatedBiasDps { get; }

    internal int ValidBuckets { get; }
}

/// <summary>
/// Learns a bounded yaw-rate bias from relative magnetometer/gyro motion. It
/// never gives the magnetic sensor direct authority over a frame: an invalid,
/// stale, disturbed, non-yaw-dominant, or insufficiently established sample
/// immediately falls back to the unmodified gyro vector.
/// </summary>
internal sealed class Switch2MagnetometerYawAssist
{
    internal const float AccelerometerLsbPerG = 4096.0f;
    internal const float MinimumAccelerationG = 0.70f;
    internal const float MaximumAccelerationG = 1.30f;
    internal const float MinimumMagnitudeRatio = 0.70f;
    internal const float MaximumMagnitudeRatio = 1.30f;
    internal const float MaximumMagnitudeStepRatio = 0.15f;
    internal const float CandidateStableStepRatio = 0.05f;
    internal const double CandidatePromotionSeconds = 0.25;
    internal const double RecoverySeconds = 1.00;
    internal const double BucketSeconds = 0.10;
    internal const int MinimumBucketSamples = 5;
    internal const float MinimumMotionDps = 3.0f;
    internal const float MinimumYawDominance = 0.75f;
    internal const float MaximumRelativeDeltaInnovationDegrees = 0.75f;
    internal const float ObservationCapDps = 0.25f;
    internal const double EstimateTimeConstantSeconds = 5.0;
    internal const float OutputCapDps = 0.10f;
    internal const int MinimumValidBuckets = 10;
    internal const int FullConfidenceBuckets = 30;
    internal const double BiasDecayDelaySeconds = 1.00;
    internal const double MaximumIntegratedDeltaSeconds = 0.10;
    internal const double BaselineTrackingSeconds = 20.0;

    private bool enabled;
    private ulong sourceEpoch;
    private bool hasConfiguration;
    private bool hasBaseline;
    private float baselineMagnitude;
    private float previousMagnitude;
    private bool hasPreviousDirection;
    private Vector3 previousHorizontalDirection;
    private double recoverySeconds;
    private bool hasCandidate;
    private float candidateMagnitude;
    private double candidateStableSeconds;
    private double bucketElapsedSeconds;
    private float bucketMagneticDeltaDegrees;
    private float bucketGyroDeltaDegrees;
    private int bucketSamples;
    private float estimatedBiasDps;
    private int validBuckets;
    private double biasObservationAgeSeconds = double.PositiveInfinity;
    private bool hasTrustedObservation;
    private double intervalElapsedSeconds;
    private float intervalGyroDeltaDegrees;
    private int intervalSamples;
    private bool intervalMotionValid;

    internal bool TryApply(in Vector3 gyroscope,
        in Vector3 accelerometer, in Vector3 magnetometer,
        float gyroLsbPerDegreeSecond, double elapsedSeconds,
        bool assistEnabled, ulong observationEpoch,
        bool magneticObservationFresh,
        out Switch2MagnetometerYawAssistResult result)
    {
        result = default;
        if (!IsFinite(gyroscope) || !IsFinite(accelerometer) ||
            !float.IsFinite(gyroLsbPerDegreeSecond) ||
            gyroLsbPerDegreeSecond <= 0.0f)
        {
            Reset();
            return false;
        }

        if (!hasConfiguration || enabled != assistEnabled ||
            sourceEpoch != observationEpoch)
        {
            Reset();
            hasConfiguration = true;
            enabled = assistEnabled;
            sourceEpoch = observationEpoch;
        }

        if (!assistEnabled)
        {
            result = Fallback(gyroscope, magneticSampleAccepted: false);
            return true;
        }

        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0)
        {
            SuspendObservation();
            result = Fallback(gyroscope, magneticSampleAccepted: false);
            return true;
        }

        AgeBiasEstimate(elapsedSeconds);
        if (elapsedSeconds > MaximumIntegratedDeltaSeconds ||
            !TryNormalizeGravity(accelerometer, out Vector3 gravity) ||
            !TryMagneticDirection(magnetometer, gravity,
                out Vector3 horizontalDirection, out float magnitude))
        {
            SuspendObservation();
            result = Fallback(gyroscope, magneticSampleAccepted: false);
            return true;
        }

        if (!hasBaseline)
        {
            if (!magneticObservationFresh)
            {
                result = Fallback(gyroscope,
                    magneticSampleAccepted: false);
                return true;
            }

            hasBaseline = true;
            baselineMagnitude = magnitude;
            previousMagnitude = magnitude;
            hasPreviousDirection = true;
            previousHorizontalDirection = horizontalDirection;
            result = Fallback(gyroscope, magneticSampleAccepted: true);
            return true;
        }

        float ratio = magnitude / baselineMagnitude;
        float stepRatio = MathF.Abs(magnitude - previousMagnitude) /
            MathF.Max(previousMagnitude, 1.0e-6f);
        if (ratio < MinimumMagnitudeRatio || ratio > MaximumMagnitudeRatio ||
            stepRatio > MaximumMagnitudeStepRatio)
        {
            if (magneticObservationFresh)
            {
                ObserveCandidate(magnitude, elapsedSeconds);
            }
            SuspendObservation(clearCandidate: false);
            result = Fallback(gyroscope, magneticSampleAccepted: false);
            return true;
        }

        hasCandidate = false;
        candidateMagnitude = 0.0f;
        candidateStableSeconds = 0.0;
        previousMagnitude = magnitude;
        float baselineAlpha = 1.0f - MathF.Exp((float)(-elapsedSeconds /
            BaselineTrackingSeconds));
        baselineMagnitude += baselineAlpha * (magnitude - baselineMagnitude);
        recoverySeconds = Math.Min(RecoverySeconds,
            recoverySeconds + elapsedSeconds);

        float gyroAroundGravityDps = Vector3.Dot(gyroscope, gravity) /
            gyroLsbPerDegreeSecond;
        float gyroMagnitudeDps = gyroscope.Length() /
            gyroLsbPerDegreeSecond;
        float gyroDeltaDegrees = gyroAroundGravityDps *
            (float)elapsedSeconds;
        bool yawDominant = gyroMagnitudeDps > 1.0e-5f &&
            MathF.Abs(gyroAroundGravityDps) / gyroMagnitudeDps >=
                MinimumYawDominance;

        if (!hasPreviousDirection)
        {
            if (magneticObservationFresh)
            {
                hasPreviousDirection = true;
                previousHorizontalDirection = horizontalDirection;
                ClearInterval();
            }
            result = Fallback(gyroscope,
                magneticSampleAccepted: magneticObservationFresh);
            return true;
        }

        intervalElapsedSeconds += elapsedSeconds;
        intervalGyroDeltaDegrees += gyroDeltaDegrees;
        intervalSamples++;
        bool motionValid = MathF.Abs(gyroAroundGravityDps) >=
            MinimumMotionDps && yawDominant;
        intervalMotionValid = intervalSamples == 1 ? motionValid :
            intervalMotionValid && motionValid;

        if (intervalElapsedSeconds > MaximumIntegratedDeltaSeconds)
        {
            SuspendObservation();
            result = Fallback(gyroscope, magneticSampleAccepted: false);
            return true;
        }

        if (!magneticObservationFresh)
        {
            result = CorrectedOrFallback(gyroscope, gravity,
                gyroLsbPerDegreeSecond, magneticSampleAccepted: false,
                correctionEligible: hasTrustedObservation &&
                    intervalMotionValid);
            return true;
        }

        float cosine = Math.Clamp(Vector3.Dot(previousHorizontalDirection,
            horizontalDirection), -1.0f, 1.0f);
        float sine = Vector3.Dot(gravity, Vector3.Cross(
            previousHorizontalDirection, horizontalDirection));
        float magneticDeltaDegrees = -MathF.Atan2(sine, cosine) *
            (180.0f / MathF.PI);
        previousHorizontalDirection = horizontalDirection;
        gyroDeltaDegrees = intervalGyroDeltaDegrees;
        elapsedSeconds = intervalElapsedSeconds;
        float innovation = magneticDeltaDegrees - gyroDeltaDegrees;
        bool directionAgrees = MathF.Abs(magneticDeltaDegrees) <= 1.0e-5f ||
            MathF.Abs(gyroDeltaDegrees) <= 1.0e-5f ||
            magneticDeltaDegrees * gyroDeltaDegrees > 0.0f;
        bool learningSample = recoverySeconds >= RecoverySeconds &&
            intervalMotionValid && directionAgrees &&
            MathF.Abs(innovation) <= MaximumRelativeDeltaInnovationDegrees;

        if (learningSample)
        {
            hasTrustedObservation = true;
            biasObservationAgeSeconds = 0.0;
            bucketElapsedSeconds += elapsedSeconds;
            bucketMagneticDeltaDegrees += magneticDeltaDegrees;
            bucketGyroDeltaDegrees += gyroDeltaDegrees;
            bucketSamples++;
            if (bucketElapsedSeconds >= BucketSeconds &&
                bucketSamples >= MinimumBucketSamples)
            {
                CommitBucket();
            }
        }
        else
        {
            hasTrustedObservation = false;
            ClearBucket();
        }
        ClearInterval();

        result = CorrectedOrFallback(gyroscope, gravity,
            gyroLsbPerDegreeSecond, magneticSampleAccepted: true,
            correctionEligible: learningSample);
        return true;
    }

    internal void Reset()
    {
        enabled = false;
        sourceEpoch = 0;
        hasConfiguration = false;
        hasBaseline = false;
        baselineMagnitude = 0.0f;
        previousMagnitude = 0.0f;
        hasPreviousDirection = false;
        previousHorizontalDirection = default;
        recoverySeconds = 0.0;
        hasCandidate = false;
        candidateMagnitude = 0.0f;
        candidateStableSeconds = 0.0;
        ClearBucket();
        estimatedBiasDps = 0.0f;
        validBuckets = 0;
        biasObservationAgeSeconds = double.PositiveInfinity;
        hasTrustedObservation = false;
        ClearInterval();
    }

    private Switch2MagnetometerYawAssistResult Fallback(
        in Vector3 gyroscope, bool magneticSampleAccepted) => new(gyroscope,
            magneticSampleAccepted, correctionApplied: false,
            correctionDps: 0.0f, estimatedBiasDps, validBuckets);

    private void SuspendObservation(bool clearCandidate = true)
    {
        hasPreviousDirection = false;
        previousHorizontalDirection = default;
        recoverySeconds = 0.0;
        hasTrustedObservation = false;
        ClearBucket();
        ClearInterval();
        if (clearCandidate)
        {
            hasCandidate = false;
            candidateMagnitude = 0.0f;
            candidateStableSeconds = 0.0;
        }
    }

    private void ObserveCandidate(float magnitude, double elapsedSeconds)
    {
        if (!hasCandidate)
        {
            hasCandidate = true;
            candidateMagnitude = magnitude;
            candidateStableSeconds = 0.0;
            return;
        }

        float step = MathF.Abs(magnitude - candidateMagnitude) /
            MathF.Max(candidateMagnitude, 1.0e-6f);
        if (step > CandidateStableStepRatio)
        {
            candidateMagnitude = magnitude;
            candidateStableSeconds = 0.0;
            return;
        }

        candidateStableSeconds += elapsedSeconds;
        float alpha = 1.0f - MathF.Exp((float)(-elapsedSeconds /
            CandidatePromotionSeconds));
        candidateMagnitude += alpha * (magnitude - candidateMagnitude);
        if (candidateStableSeconds < CandidatePromotionSeconds)
        {
            return;
        }

        baselineMagnitude = candidateMagnitude;
        previousMagnitude = magnitude;
        hasCandidate = false;
        candidateMagnitude = 0.0f;
        candidateStableSeconds = 0.0;
    }

    private void CommitBucket()
    {
        float observation = (bucketMagneticDeltaDegrees -
            bucketGyroDeltaDegrees) / (float)bucketElapsedSeconds;
        observation = Math.Clamp(observation, -ObservationCapDps,
            ObservationCapDps);
        float alpha = 1.0f - MathF.Exp((float)(-bucketElapsedSeconds /
            EstimateTimeConstantSeconds));
        estimatedBiasDps += alpha * (observation - estimatedBiasDps);
        validBuckets = Math.Min(FullConfidenceBuckets, validBuckets + 1);
        ClearBucket();
    }

    private void AgeBiasEstimate(double elapsedSeconds)
    {
        if (!double.IsFinite(biasObservationAgeSeconds) ||
            !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0)
        {
            return;
        }

        double previousAge = biasObservationAgeSeconds;
        biasObservationAgeSeconds = Math.Min(double.MaxValue,
            biasObservationAgeSeconds + elapsedSeconds);
        double previousDecayAge = Math.Max(0.0,
            previousAge - BiasDecayDelaySeconds);
        double currentDecayAge = Math.Max(0.0,
            biasObservationAgeSeconds - BiasDecayDelaySeconds);
        double decaySeconds = currentDecayAge - previousDecayAge;
        if (decaySeconds <= 0.0)
        {
            return;
        }

        estimatedBiasDps *= MathF.Exp((float)(-decaySeconds /
            EstimateTimeConstantSeconds));
    }

    private void ClearBucket()
    {
        bucketElapsedSeconds = 0.0;
        bucketMagneticDeltaDegrees = 0.0f;
        bucketGyroDeltaDegrees = 0.0f;
        bucketSamples = 0;
    }

    private void ClearInterval()
    {
        intervalElapsedSeconds = 0.0;
        intervalGyroDeltaDegrees = 0.0f;
        intervalSamples = 0;
        intervalMotionValid = false;
    }

    private Switch2MagnetometerYawAssistResult CorrectedOrFallback(
        in Vector3 gyroscope, in Vector3 gravity,
        float gyroLsbPerDegreeSecond, bool magneticSampleAccepted,
        bool correctionEligible)
    {
        float sampleConfidence = validBuckets <= MinimumValidBuckets ? 0.0f :
            Math.Clamp((validBuckets - MinimumValidBuckets) /
                (float)(FullConfidenceBuckets - MinimumValidBuckets),
                0.0f, 1.0f);
        sampleConfidence = sampleConfidence * sampleConfidence *
            (3.0f - 2.0f * sampleConfidence);
        float ageConfidence = double.IsFinite(biasObservationAgeSeconds) ?
            MathF.Exp((float)(-Math.Max(0.0,
                biasObservationAgeSeconds - BiasDecayDelaySeconds) /
                EstimateTimeConstantSeconds)) : 0.0f;
        float confidence = sampleConfidence * ageConfidence;
        float correctionDps = Math.Clamp(estimatedBiasDps,
            -OutputCapDps, OutputCapDps) * confidence;
        bool correctionApplied = correctionEligible &&
            recoverySeconds >= RecoverySeconds && correctionDps != 0.0f;
        Vector3 corrected = correctionApplied ? gyroscope + gravity *
            (correctionDps * gyroLsbPerDegreeSecond) : gyroscope;
        return new Switch2MagnetometerYawAssistResult(corrected,
            magneticSampleAccepted, correctionApplied, correctionDps,
            estimatedBiasDps, validBuckets);
    }

    private static bool TryNormalizeGravity(in Vector3 accelerometer,
        out Vector3 gravity)
    {
        gravity = default;
        float magnitude = accelerometer.Length();
        if (!float.IsFinite(magnitude) || magnitude <
                MinimumAccelerationG * AccelerometerLsbPerG || magnitude >
                MaximumAccelerationG * AccelerometerLsbPerG)
        {
            return false;
        }

        gravity = accelerometer / magnitude;
        return IsFinite(gravity);
    }

    private static bool TryMagneticDirection(in Vector3 magnetometer,
        in Vector3 gravity, out Vector3 horizontalDirection,
        out float magnitude)
    {
        horizontalDirection = default;
        magnitude = 0.0f;
        if (!IsFinite(magnetometer))
        {
            return false;
        }

        magnitude = magnetometer.Length();
        if (!float.IsFinite(magnitude) || magnitude <= 1.0e-6f)
        {
            return false;
        }

        Vector3 horizontal = magnetometer - gravity * Vector3.Dot(
            magnetometer, gravity);
        float horizontalMagnitude = horizontal.Length();
        if (!float.IsFinite(horizontalMagnitude) ||
            horizontalMagnitude <= magnitude * 0.05f)
        {
            return false;
        }

        horizontalDirection = horizontal / horizontalMagnitude;
        return IsFinite(horizontalDirection);
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
