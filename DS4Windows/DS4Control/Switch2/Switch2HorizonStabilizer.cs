/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The canonical timing bounds, orientation-to-horizon projection, and matching
accelerometer roll-compensation law are adapted from the GPL-3.0
Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/gyro.py
(canonical_timing, build_v2_gyro_output, and
build_v2_accelerometer_output). The estimator is an allocation-free
DS4Windows implementation of the same six-axis gyro/accelerometer contract;
magnetic yaw authority remains in Switch2MagnetometerYawAssist.
*/

using System;
using System.Numerics;

namespace DS4Windows.Switch2;

internal readonly struct Switch2HorizonProjectionResult
{
    internal Switch2HorizonProjectionResult(Vector3 gyroscope,
        Vector3 accelerometer, bool applied, bool orientationInitialized)
    {
        Gyroscope = gyroscope;
        Accelerometer = accelerometer;
        Applied = applied;
        OrientationInitialized = orientationInitialized;
    }

    internal Vector3 Gyroscope { get; }

    internal Vector3 Accelerometer { get; }

    internal bool Applied { get; }

    internal bool OrientationInitialized { get; }
}

/// <summary>
/// Maintains one body-to-world orientation for one serialized Switch 2 motion
/// lifetime. Unknown timing and sensor states fall back to the unmodified
/// physical sample; they never manufacture or retain a projected frame.
/// </summary>
internal sealed class Switch2HorizonStabilizer
{
    internal const double DefaultDeltaSeconds = 0.015;
    internal const double MinimumDeltaSeconds = 0.001;
    internal const double MaximumDeltaSeconds = 0.050;
    internal const double ResetGapSeconds = 0.100;
    internal const float AccelerometerCorrectionGain = 0.1f;
    internal const float AccelerometerRejectionDegrees = 10.0f;
    internal const float MinimumAccelerationG = 0.70f;
    internal const float MaximumAccelerationG = 1.30f;
    internal const float MaximumGyroscopeDps = 2000.0f;

    private Quaternion orientation = Quaternion.Identity;
    private bool orientationInitialized;
    private bool hasConfiguration;
    private bool enabled;
    private ulong sourceEpoch;

    internal bool TryApply(in Vector3 gyroscope,
        in Vector3 accelerometer, float gyroLsbPerDegreeSecond,
        float accelerometerLsbPerG, double elapsedSeconds,
        bool horizonEnabled, ulong observationEpoch, bool horizontal,
        out Switch2HorizonProjectionResult result)
    {
        result = default;
        if (!IsFinite(gyroscope) || !IsFinite(accelerometer) ||
            !float.IsFinite(gyroLsbPerDegreeSecond) ||
            gyroLsbPerDegreeSecond <= 0.0f ||
            !float.IsFinite(accelerometerLsbPerG) ||
            accelerometerLsbPerG <= 0.0f)
        {
            Reset();
            return false;
        }

        if (!hasConfiguration || enabled != horizonEnabled ||
            sourceEpoch != observationEpoch)
        {
            ResetState();
            hasConfiguration = true;
            enabled = horizonEnabled;
            sourceEpoch = observationEpoch;
        }

        if (!horizonEnabled)
        {
            result = Fallback(gyroscope, accelerometer);
            return true;
        }

        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0 ||
            elapsedSeconds > ResetGapSeconds)
        {
            ResetState();
            TryInitialize(accelerometer, accelerometerLsbPerG);
            result = Fallback(gyroscope, accelerometer);
            return true;
        }

        if (!orientationInitialized &&
            !TryInitialize(accelerometer, accelerometerLsbPerG))
        {
            result = Fallback(gyroscope, accelerometer);
            return true;
        }

        double boundedDelta = Math.Clamp(elapsedSeconds,
            MinimumDeltaSeconds, MaximumDeltaSeconds);
        if (!TryIntegrate(gyroscope, accelerometer,
                gyroLsbPerDegreeSecond, accelerometerLsbPerG,
                (float)boundedDelta))
        {
            ResetState();
            result = Fallback(gyroscope, accelerometer);
            return true;
        }

        if (!TryProject(gyroscope, accelerometer, orientation, horizontal,
                out Vector3 projectedGyroscope,
                out Vector3 projectedAccelerometer))
        {
            ResetState();
            result = Fallback(gyroscope, accelerometer);
            return true;
        }

        result = new Switch2HorizonProjectionResult(projectedGyroscope,
            projectedAccelerometer, applied: true,
            orientationInitialized: true);
        return true;
    }

    internal void Reset()
    {
        ResetState();
        hasConfiguration = false;
        enabled = false;
        sourceEpoch = 0;
    }

    internal static bool TryProject(in Vector3 gyroscope,
        in Vector3 accelerometer, in Quaternion bodyToWorld,
        bool horizontal, out Vector3 projectedGyroscope,
        out Vector3 projectedAccelerometer)
    {
        projectedGyroscope = default;
        projectedAccelerometer = default;
        if (!IsFinite(gyroscope) || !IsFinite(accelerometer) ||
            !TryNormalize(bodyToWorld, out Quaternion orientation))
        {
            return false;
        }

        Vector3 local = horizontal ?
            new Vector3(0.0f, gyroscope.Y, gyroscope.Z) :
            new Vector3(gyroscope.X, 0.0f, gyroscope.Z);
        Vector3 world = Vector3.Transform(local, orientation);
        Vector3 forwardLocal = horizontal ? Vector3.UnitX : Vector3.UnitY;
        Vector3 forwardWorld = Vector3.Transform(forwardLocal, orientation);
        float horizontalMagnitude = MathF.Sqrt(
            forwardWorld.X * forwardWorld.X +
            forwardWorld.Y * forwardWorld.Y);
        Vector3 rightHorizontal = horizontalMagnitude < 0.01f ?
            Vector3.UnitX : new Vector3(
                forwardWorld.Y / horizontalMagnitude,
                -forwardWorld.X / horizontalMagnitude, 0.0f);
        float pitch = Vector3.Dot(world, rightHorizontal);
        float yaw = world.Z;
        projectedGyroscope = horizontal ?
            new Vector3(0.0f, -pitch, yaw) :
            new Vector3(pitch, 0.0f, yaw);

        Quaternion inverse = Quaternion.Conjugate(orientation);
        Vector3 downBody = Vector3.Transform(-Vector3.UnitZ, inverse);
        float rollRadians = horizontal ?
            MathF.Atan2(-downBody.Y, -downBody.Z) :
            MathF.Atan2(downBody.X, -downBody.Z);
        Quaternion roll = Quaternion.CreateFromAxisAngle(
            horizontal ? Vector3.UnitX : Vector3.UnitY, rollRadians);
        projectedAccelerometer = Vector3.Transform(accelerometer, roll);
        return IsFinite(projectedGyroscope) &&
            IsFinite(projectedAccelerometer);
    }

    private bool TryInitialize(in Vector3 accelerometer,
        float accelerometerLsbPerG)
    {
        if (!TryNormalizeAcceleration(accelerometer,
                accelerometerLsbPerG, out Vector3 measuredGravity))
        {
            return false;
        }

        if (!TryCreateFromTo(measuredGravity, Vector3.UnitZ,
                out orientation))
        {
            return false;
        }

        orientationInitialized = true;
        return true;
    }

    private bool TryIntegrate(in Vector3 gyroscope,
        in Vector3 accelerometer, float gyroLsbPerDegreeSecond,
        float accelerometerLsbPerG, float elapsedSeconds)
    {
        Vector3 gyroDps = gyroscope / gyroLsbPerDegreeSecond;
        if (!IsFinite(gyroDps) || MathF.Abs(gyroDps.X) >
                MaximumGyroscopeDps || MathF.Abs(gyroDps.Y) >
                MaximumGyroscopeDps || MathF.Abs(gyroDps.Z) >
                MaximumGyroscopeDps)
        {
            return false;
        }

        Vector3 correctedRadians = gyroDps * (MathF.PI / 180.0f);
        if (TryNormalizeAcceleration(accelerometer,
                accelerometerLsbPerG, out Vector3 measuredGravity))
        {
            Vector3 estimatedGravity = Vector3.Transform(Vector3.UnitZ,
                Quaternion.Conjugate(orientation));
            float cosine = Math.Clamp(Vector3.Dot(measuredGravity,
                estimatedGravity), -1.0f, 1.0f);
            float errorDegrees = MathF.Acos(cosine) *
                (180.0f / MathF.PI);
            if (float.IsFinite(errorDegrees) && errorDegrees <=
                    AccelerometerRejectionDegrees)
            {
                Vector3 correction = Vector3.Cross(measuredGravity,
                    estimatedGravity) * AccelerometerCorrectionGain;
                correctedRadians += correction;
            }
        }

        Quaternion angularVelocity = new(correctedRadians, 0.0f);
        Quaternion derivative = Quaternion.Multiply(orientation,
            angularVelocity);
        Quaternion integrated = new(
            orientation.X + derivative.X * 0.5f * elapsedSeconds,
            orientation.Y + derivative.Y * 0.5f * elapsedSeconds,
            orientation.Z + derivative.Z * 0.5f * elapsedSeconds,
            orientation.W + derivative.W * 0.5f * elapsedSeconds);
        if (!TryNormalize(integrated, out orientation))
        {
            return false;
        }

        return true;
    }

    private static bool TryNormalizeAcceleration(in Vector3 acceleration,
        float accelerometerLsbPerG, out Vector3 normalized)
    {
        normalized = default;
        float magnitude = acceleration.Length();
        if (!float.IsFinite(magnitude) || magnitude <
                MinimumAccelerationG * accelerometerLsbPerG || magnitude >
                MaximumAccelerationG * accelerometerLsbPerG)
        {
            return false;
        }

        normalized = acceleration / magnitude;
        return IsFinite(normalized);
    }

    private static bool TryCreateFromTo(in Vector3 from, in Vector3 to,
        out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        float dot = Math.Clamp(Vector3.Dot(from, to), -1.0f, 1.0f);
        if (dot >= 0.999999f)
        {
            return true;
        }

        if (dot <= -0.999999f)
        {
            Vector3 axis = MathF.Abs(from.X) < 0.9f ?
                Vector3.Normalize(Vector3.Cross(from, Vector3.UnitX)) :
                Vector3.Normalize(Vector3.Cross(from, Vector3.UnitY));
            rotation = Quaternion.CreateFromAxisAngle(axis, MathF.PI);
            return TryNormalize(rotation, out rotation);
        }

        Vector3 cross = Vector3.Cross(from, to);
        rotation = new Quaternion(cross, 1.0f + dot);
        return TryNormalize(rotation, out rotation);
    }

    private static bool TryNormalize(in Quaternion value,
        out Quaternion normalized)
    {
        normalized = Quaternion.Identity;
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-12f)
        {
            return false;
        }

        float inverse = 1.0f / MathF.Sqrt(lengthSquared);
        normalized = new Quaternion(value.X * inverse, value.Y * inverse,
            value.Z * inverse, value.W * inverse);
        return float.IsFinite(normalized.X) &&
            float.IsFinite(normalized.Y) &&
            float.IsFinite(normalized.Z) &&
            float.IsFinite(normalized.W);
    }

    private Switch2HorizonProjectionResult Fallback(in Vector3 gyroscope,
        in Vector3 accelerometer) => new(gyroscope, accelerometer,
            applied: false, orientationInitialized);

    private void ResetState()
    {
        orientation = Quaternion.Identity;
        orientationInitialized = false;
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
