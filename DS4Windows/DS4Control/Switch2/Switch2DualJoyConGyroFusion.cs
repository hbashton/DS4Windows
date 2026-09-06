/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The fusion policy in this file is adapted from the GPL-3.0 licensed
Switch2Connect project, commit 4487322a306f04efa27682e3f3a508635a84fd98,
src/virtual_controller.py (_fuse_djg_axis, _merge_djg_direct_motion, and
gyro_fusion_callback). Coordinate decoding remains owned by DS4Windows.
*/

using System;
using System.Numerics;

namespace DS4Windows.Switch2;

internal enum Switch2JoyConSide : byte
{
    Invalid = 0,
    Left,
    Right,
}

public enum Switch2DualGyroDominantSide : byte
{
    Invalid = 0,
    Left,
    Right,
    None,
}

internal readonly struct Switch2JoyConMotionSample
{
    internal Switch2JoyConMotionSample(Vector3 gyroscope,
        Vector3 accelerometer, Vector3 gyroBias, bool active)
    {
        Gyroscope = gyroscope;
        Accelerometer = accelerometer;
        GyroBias = gyroBias;
        Active = active;
    }

    internal Vector3 Gyroscope { get; }

    internal Vector3 Accelerometer { get; }

    internal Vector3 GyroBias { get; }

    internal bool Active { get; }

    internal bool IsFinite => IsFiniteVector(Gyroscope) &&
        IsFiniteVector(Accelerometer) && IsFiniteVector(GyroBias);

    private static bool IsFiniteVector(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

internal struct Switch2DualJoyConGyroFusionState
{
    internal Vector3 AccelerometerOffset;
    internal bool PreviousDominantActive;
    internal bool PreviousSubActive;
    internal bool HasPreviousActivation;
}

internal readonly struct Switch2DualJoyConGyroFusionResult
{
    internal Switch2DualJoyConGyroFusionResult(Vector3 gyroscope,
        Vector3 accelerometer, Switch2JoyConSide outputOwner,
        float subContributionScale)
    {
        Gyroscope = gyroscope;
        Accelerometer = accelerometer;
        OutputOwner = outputOwner;
        SubContributionScale = subContributionScale;
    }

    internal Vector3 Gyroscope { get; }

    internal Vector3 Accelerometer { get; }

    /// <summary>
    /// The sole side allowed to emit mouse/stick motion for this fused value.
    /// Both physical reports may update the cache, but two output owners would
    /// double the cursor or virtual-stick movement.
    /// </summary>
    internal Switch2JoyConSide OutputOwner { get; }

    internal float SubContributionScale { get; }
}

/// <summary>
/// Allocation-free Dual Joy-Con Gyro fusion. The dominant side supplies the
/// orientation and the sub side can only accelerate same-direction movement.
/// Its contribution ramps in after magnitude 30 and is capped to the dominant
/// component, so the fused axis cannot exceed 2x the dominant axis.
/// </summary>
internal static class Switch2DualJoyConGyroFusion
{
    internal const float ContributionThreshold = 30.0f;
    internal const float ContributionRampWidth = 30.0f;
    internal const float AccelerometerOffsetDecay = 0.99f;
    internal const float AccelerometerOffsetSnapThreshold = 0.1f;

    internal static bool TryFuse(
        in Switch2JoyConMotionSample left,
        in Switch2JoyConMotionSample right,
        Switch2DualGyroDominantSide dominantSide,
        Vector3 outputBias,
        ref Switch2DualJoyConGyroFusionState state,
        out Switch2DualJoyConGyroFusionResult result)
    {
        result = default;
        if (!left.IsFinite || !right.IsFinite ||
            !IsFiniteVector(outputBias) ||
            !IsFiniteVector(state.AccelerometerOffset) ||
            dominantSide is < Switch2DualGyroDominantSide.Left or >
                Switch2DualGyroDominantSide.None)
        {
            return false;
        }

        if (dominantSide == Switch2DualGyroDominantSide.None)
        {
            result = FuseDirect(left, right);
            state.PreviousDominantActive = left.Active;
            state.PreviousSubActive = right.Active;
            state.HasPreviousActivation = true;
            return true;
        }

        bool leftDominant = dominantSide ==
            Switch2DualGyroDominantSide.Left;
        Switch2JoyConMotionSample dominant = leftDominant ? left : right;
        Switch2JoyConMotionSample sub = leftDominant ? right : left;

        if (state.HasPreviousActivation)
        {
            if (state.PreviousDominantActive && !dominant.Active && sub.Active)
            {
                state.AccelerometerOffset = dominant.Accelerometer -
                    sub.Accelerometer;
            }
            else if (!state.PreviousDominantActive && dominant.Active &&
                sub.Active)
            {
                state.AccelerometerOffset = sub.Accelerometer +
                    state.AccelerometerOffset - dominant.Accelerometer;
            }
        }
        state.PreviousDominantActive = dominant.Active;
        state.PreviousSubActive = sub.Active;
        state.HasPreviousActivation = true;

        Vector3 fusedGyroscope;
        Vector3 fusedAccelerometer;
        float scale = 0.0f;
        if (dominant.Active && sub.Active)
        {
            Vector3 dominantAdjusted = dominant.Gyroscope -
                dominant.GyroBias;
            Vector3 subAdjusted = sub.Gyroscope - sub.GyroBias;
            float magnitude = dominantAdjusted.Length();
            if (magnitude > ContributionThreshold)
            {
                scale = MathF.Min(1.0f,
                    (magnitude - ContributionThreshold) /
                        ContributionRampWidth);
            }
            fusedGyroscope = new Vector3(
                FuseAxis(dominantAdjusted.X, subAdjusted.X, scale),
                FuseAxis(dominantAdjusted.Y, subAdjusted.Y, scale),
                FuseAxis(dominantAdjusted.Z, subAdjusted.Z, scale)) +
                outputBias;
            fusedAccelerometer = dominant.Accelerometer +
                state.AccelerometerOffset;
        }
        else if (dominant.Active)
        {
            fusedGyroscope = dominant.Gyroscope - dominant.GyroBias +
                outputBias;
            fusedAccelerometer = dominant.Accelerometer +
                state.AccelerometerOffset;
        }
        else if (sub.Active)
        {
            fusedGyroscope = sub.Gyroscope - sub.GyroBias + outputBias;
            fusedAccelerometer = sub.Accelerometer;
        }
        else
        {
            fusedGyroscope = outputBias;
            fusedAccelerometer = Vector3.Zero;
        }

        state.AccelerometerOffset = DecayOffset(
            state.AccelerometerOffset);
        result = new Switch2DualJoyConGyroFusionResult(fusedGyroscope,
            fusedAccelerometer,
            leftDominant ? Switch2JoyConSide.Left :
                Switch2JoyConSide.Right,
            scale);
        return true;
    }

    private static Switch2DualJoyConGyroFusionResult FuseDirect(
        in Switch2JoyConMotionSample left,
        in Switch2JoyConMotionSample right)
    {
        if (left.Active && right.Active)
        {
            Vector3 acceleration = left.Accelerometer != Vector3.Zero &&
                right.Accelerometer != Vector3.Zero ?
                (left.Accelerometer + right.Accelerometer) * 0.5f :
                left.Accelerometer != Vector3.Zero ? left.Accelerometer :
                    right.Accelerometer;
            return new Switch2DualJoyConGyroFusionResult(
                left.Gyroscope - left.GyroBias +
                    right.Gyroscope - right.GyroBias, acceleration,
                Switch2JoyConSide.Left, 1.0f);
        }
        if (left.Active)
        {
            return new Switch2DualJoyConGyroFusionResult(
                left.Gyroscope - left.GyroBias,
                left.Accelerometer, Switch2JoyConSide.Left, 0.0f);
        }
        if (right.Active)
        {
            return new Switch2DualJoyConGyroFusionResult(
                right.Gyroscope - right.GyroBias,
                right.Accelerometer, Switch2JoyConSide.Right, 0.0f);
        }
        return new Switch2DualJoyConGyroFusionResult(Vector3.Zero,
            Vector3.Zero, Switch2JoyConSide.Left, 0.0f);
    }

    private static float FuseAxis(float dominant, float sub, float scale)
    {
        if ((dominant > 0.0f && sub > 0.0f) ||
            (dominant < 0.0f && sub < 0.0f))
        {
            float scaled = sub * scale;
            if (MathF.Abs(scaled) > MathF.Abs(dominant))
            {
                scaled = dominant;
            }
            return dominant + scaled;
        }
        return dominant;
    }

    private static Vector3 DecayOffset(in Vector3 offset) => new(
        DecayOffsetAxis(offset.X), DecayOffsetAxis(offset.Y),
        DecayOffsetAxis(offset.Z));

    private static float DecayOffsetAxis(float value) =>
        MathF.Abs(value) > AccelerometerOffsetSnapThreshold ?
            value * AccelerometerOffsetDecay : 0.0f;

    private static bool IsFiniteVector(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
