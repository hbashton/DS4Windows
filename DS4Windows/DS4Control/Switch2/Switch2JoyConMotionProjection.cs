/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

Axis orientation and native sensor scales are adapted from the GPL-3.0
Switch2Connect project, commit 4487322a306f04efa27682e3f3a508635a84fd98,
src/virtual_controller.py and src/controller.py. The resulting state enters
DS4Windows' existing SixAxis mapping path.
*/

using System;
using System.Numerics;

namespace DS4Windows.Switch2;

/// <summary>
/// Allocation-free Joy-Con 2 motion projection owned by one serialized runtime
/// device. It retains the previous SixAxis object for DS4Windows' established
/// delta-aware mapping path and optionally applies the pinned dual-gyro policy.
/// </summary>
internal sealed class Switch2JoyConMotionProjection
{
    private const float JoyConGyroLsbPerDegreeSecond = 16.384f;
    private const float Ds4WindowsGyroLsbPerDegreeSecond = 16.0f;
    private const float JoyConAccelerometerLsbPerG = 4096.0f;
    private const float Ds4WindowsAccelerometerLsbPerG = 8192.0f;
    private const double MagnetometerCacheSeconds = 0.10;

    private SixAxis current = new(0, 0, 0, 0, 0, 0, 0.0);
    private SixAxis previous = new(0, 0, 0, 0, 0, 0, 0.0);
    private Switch2DualJoyConGyroFusionState fusionState;
    private Switch2IrGyroMotionModifierState irGyroModifierState;
    private readonly Switch2MagnetometerYawAssist magnetometerYawAssist =
        new();
    private readonly Switch2HorizonStabilizer horizonStabilizer = new();
    private readonly Switch2StationaryGyroCalibration leftGyroCalibration =
        new();
    private readonly Switch2StationaryGyroCalibration rightGyroCalibration =
        new();
    private readonly Switch2MagnetometerCalibrationSession
        leftMagnetometerCalibrationSession = new();
    private readonly Switch2MagnetometerCalibrationSession
        rightMagnetometerCalibrationSession = new();
    private Switch2MagnetometerCalibration leftMagnetometerCalibration;
    private Switch2MagnetometerCalibration rightMagnetometerCalibration;
    private Switch2MagnetometerCalibrationQuality
        lastLeftMagnetometerCalibrationQuality;
    private Switch2MagnetometerCalibrationQuality
        lastRightMagnetometerCalibrationQuality;
    private ulong leftMagnetometerCalibrationEpoch;
    private ulong rightMagnetometerCalibrationEpoch;
    private Vector3 latestLeftMagnetometer;
    private Vector3 latestRightMagnetometer;
    private long latestLeftMagnetometerTimestampQpc;
    private long latestRightMagnetometerTimestampQpc;
    private bool hasLatestLeftMagnetometer;
    private bool hasLatestRightMagnetometer;
    private long lastTimestampQpc;
    private bool hasTimestamp;
    private bool hasFusionPolicy;
    private bool previousFusionEnabled;
    private Switch2DualGyroDominantSide previousDominantSide;
    private ulong previousFusionConfigurationEpoch;
    private bool hasPresentationMode;
    private Switch2JoyConProfileMode previousPresentationMode;

    internal bool TryApply(in Switch2JoyConProfileInputFrame frame,
        DS4State destination, bool fusionEnabled,
        Switch2DualGyroDominantSide dominantSide)
    {
        var policy = new Switch2DualGyroRuntimePolicy(fusionEnabled,
            Switch2DualGyroMode.SwitchDominantSide, dominantSide,
            leftActive: true, rightActive: true, configurationEpoch: 1);
        return TryApply(frame, destination, policy,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: Switch2MotionSoftDeadzone.Default,
            horizonStabilizationEnabled: false);
    }

    internal bool TryApply(in Switch2JoyConProfileInputFrame frame,
        DS4State destination, in Switch2DualGyroRuntimePolicy policy) =>
        TryApply(frame, destination, policy,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: Switch2MotionSoftDeadzone.Default,
            horizonStabilizationEnabled: false);

    internal bool TryApply(in Switch2JoyConProfileInputFrame frame,
        DS4State destination, in Switch2DualGyroRuntimePolicy policy,
        bool magnetometerYawAssistEnabled) => TryApply(frame, destination,
            policy, magnetometerYawAssistEnabled,
            Switch2MotionSoftDeadzone.Default,
            horizonStabilizationEnabled: false);

    internal bool TryApply(in Switch2JoyConProfileInputFrame frame,
        DS4State destination, in Switch2DualGyroRuntimePolicy policy,
        bool magnetometerYawAssistEnabled,
        double virtualGyroSoftDeadzone) => TryApply(frame, destination,
            policy, magnetometerYawAssistEnabled,
            virtualGyroSoftDeadzone, horizonStabilizationEnabled: false);

    internal bool TryApply(in Switch2JoyConProfileInputFrame frame,
        DS4State destination, in Switch2DualGyroRuntimePolicy policy,
        bool magnetometerYawAssistEnabled, double virtualGyroSoftDeadzone,
        bool horizonStabilizationEnabled) => TryApply(frame, destination,
            policy, magnetometerYawAssistEnabled, virtualGyroSoftDeadzone,
            horizonStabilizationEnabled,
            Switch2IrGyroConfiguration.Disabled);

    internal bool TryApply(in Switch2JoyConProfileInputFrame frame,
        DS4State destination, in Switch2DualGyroRuntimePolicy policy,
        bool magnetometerYawAssistEnabled, double virtualGyroSoftDeadzone,
        bool horizonStabilizationEnabled,
        in Switch2IrGyroConfiguration irGyroConfiguration)
    {
        if (destination == null ||
            frame.Version != Switch2JoyConProfileInputFrame.CurrentVersion ||
            frame.QpcFrequency <= 0)
        {
            return false;
        }

        // The donor fusion retains an accelerometer handoff offset.  That
        // state belongs to one exact policy epoch; carrying it across a user
        // toggle or dominant-side change can manufacture a transient on the
        // first sample under the new profile policy.
        if (!hasFusionPolicy || previousFusionEnabled !=
                policy.FusionEnabled ||
            previousDominantSide != policy.DominantSide ||
            previousFusionConfigurationEpoch != policy.ConfigurationEpoch)
        {
            fusionState = default;
            previousFusionEnabled = policy.FusionEnabled;
            previousDominantSide = policy.DominantSide;
            previousFusionConfigurationEpoch =
                policy.ConfigurationEpoch;
            hasFusionPolicy = true;
        }

        // A profile can rotate a standalone Joy-Con without replacing the
        // physical lifetime. Do not feed a sample from the previous coordinate
        // basis into SixAxis' delta-aware history on the first rotated frame.
        if (!hasPresentationMode || previousPresentationMode != frame.Mode)
        {
            hasTimestamp = false;
            lastTimestampQpc = 0;
            previous.populate(0, 0, 0, 0, 0, 0, 0.0);
            current.populate(0, 0, 0, 0, 0, 0, 0.0, previous);
            previousPresentationMode = frame.Mode;
            hasPresentationMode = true;
        }

        bool fusedJoined = frame.Mode == Switch2JoyConProfileMode.Joined &&
            policy.FusionEnabled;
        if (!TryCreateCalibratedSample(frame.LeftSource,
                leftGyroCalibration,
                active: !fusedJoined || policy.LeftActive,
                out Switch2JoyConMotionSample leftSample) ||
            !TryCreateCalibratedSample(frame.RightSource,
                rightGyroCalibration,
                active: !fusedJoined || policy.RightActive,
                out Switch2JoyConMotionSample rightSample))
        {
            return false;
        }

        Vector3 gyroscope;
        Vector3 accelerometer;
        Switch2JoyConSide sourceSide;
        if (fusedJoined)
        {
            if (!Switch2DualJoyConGyroFusion.TryFuse(
                    leftSample, rightSample,
                    policy.DominantSide,
                    Vector3.Zero, ref fusionState,
                    out Switch2DualJoyConGyroFusionResult fused))
            {
                return false;
            }
            gyroscope = fused.Gyroscope;
            accelerometer = fused.Accelerometer;
            sourceSide = fused.OutputOwner;
        }
        else if (frame.Mode == Switch2JoyConProfileMode.Joined)
        {
            Switch2JoyConProfileSide source =
                frame.RightSource.HasCommonMotion ? frame.RightSource :
                    frame.LeftSource;
            if (!source.HasCommonMotion)
            {
                return false;
            }
            Switch2JoyConMotionSample sample = frame.RightSource.
                HasCommonMotion ? rightSample : leftSample;
            gyroscope = sample.Gyroscope - sample.GyroBias;
            accelerometer = sample.Accelerometer;
            sourceSide = ReferenceSide(source.Model);
        }
        else
        {
            Switch2JoyConProfileSide source = frame.LeftSource.IsPresent ?
                frame.LeftSource : frame.RightSource;
            if (!source.HasCommonMotion)
            {
                return false;
            }
            Switch2JoyConMotionSample sample = frame.LeftSource.IsPresent ?
                leftSample : rightSample;
            gyroscope = sample.Gyroscope - sample.GyroBias;
            accelerometer = sample.Accelerometer;
            sourceSide = ReferenceSide(source.Model);
        }

        double elapsed = 0.0;
        if (hasTimestamp && frame.CompletionTimestampQpc >= lastTimestampQpc)
        {
            elapsed = (frame.CompletionTimestampQpc - lastTimestampQpc) /
                (double)frame.QpcFrequency;
        }
        lastTimestampQpc = frame.CompletionTimestampQpc;
        hasTimestamp = true;

        Vector3 magnetometer = SelectMagnetometer(frame, sourceSide,
            out bool magnetometerFresh);
        ulong assistEpoch = unchecked(policy.ConfigurationEpoch *
            1099511628211UL ^ frame.PairEpoch ^
            ((ulong)frame.Mode << 56) ^ (ulong)sourceSide ^
            leftGyroCalibration.CalibrationEpoch * 16777619UL ^
            rightGyroCalibration.CalibrationEpoch * 2166136261UL ^
            leftMagnetometerCalibrationEpoch * 2246822519UL ^
            rightMagnetometerCalibrationEpoch * 3266489917UL);
        if (!magnetometerYawAssist.TryApply(gyroscope, accelerometer,
                magnetometer, JoyConGyroLsbPerDegreeSecond, elapsed,
                magnetometerYawAssistEnabled, assistEpoch,
                magnetometerFresh,
                out Switch2MagnetometerYawAssistResult assisted))
        {
            return false;
        }
        gyroscope = assisted.Gyroscope;
        bool horizontal = frame.Mode is
            Switch2JoyConProfileMode.StandaloneHorizontalLeft or
            Switch2JoyConProfileMode.StandaloneHorizontalRight;
        if (!horizonStabilizer.TryApply(gyroscope, accelerometer,
                JoyConGyroLsbPerDegreeSecond,
                JoyConAccelerometerLsbPerG, elapsed,
                horizonStabilizationEnabled, assistEpoch, horizontal,
                out Switch2HorizonProjectionResult horizon))
        {
            return false;
        }
        gyroscope = horizon.Gyroscope;
        accelerometer = horizon.Accelerometer;
        if (!Switch2IrGyroMotionModifier.TryAdvance(frame,
                irGyroConfiguration, ref irGyroModifierState,
                out Switch2IrGyroMotionModifierResult irGyroModifier))
        {
            return false;
        }
        double effectiveSoftDeadzone = irGyroModifier.DeadzoneActive ?
            Math.Max(virtualGyroSoftDeadzone,
                irGyroModifier.DeadzoneAmount) : virtualGyroSoftDeadzone;
        gyroscope = Switch2MotionSoftDeadzone.Apply(gyroscope,
            effectiveSoftDeadzone, horizontal);
        if (irGyroModifier.Freeze)
        {
            gyroscope = Vector3.Zero;
        }
        else if (irGyroModifier.DampeningActive)
        {
            gyroscope *= (float)irGyroModifier.DampeningMultiplier;
        }

        if (!TryOrient(frame.Mode, sourceSide, gyroscope, accelerometer,
                out Vector3 orientedGyroscope,
                out Vector3 orientedAccelerometer))
        {
            return false;
        }

        const float gyroScale = Ds4WindowsGyroLsbPerDegreeSecond /
            JoyConGyroLsbPerDegreeSecond;
        const float accelerometerScale =
            Ds4WindowsAccelerometerLsbPerG /
                JoyConAccelerometerLsbPerG;
        int gyroX = ClampRound(orientedGyroscope.X * gyroScale);
        int gyroY = ClampRound(orientedGyroscope.Y * gyroScale);
        int gyroZ = ClampRound(orientedGyroscope.Z * gyroScale);
        int accelX = ClampRound(orientedAccelerometer.X *
            accelerometerScale);
        int accelY = ClampRound(orientedAccelerometer.Y *
            accelerometerScale);
        int accelZ = ClampRound(orientedAccelerometer.Z *
            accelerometerScale);

        SixAxis swap = previous;
        previous = current;
        current = swap;
        // SixAxis.populate accepts semantic yaw/pitch/roll, while the pinned
        // donor orientation above is in DS report X/Y/Z order.
        current.populate(gyroY, gyroX, gyroZ, accelX, accelY, accelZ,
            elapsed, previous);
        destination.Motion = current;
        return true;
    }

    internal void Reset(DS4State destination)
    {
        fusionState = default;
        irGyroModifierState = default;
        magnetometerYawAssist.Reset();
        horizonStabilizer.Reset();
        leftGyroCalibration.ResetObservationState();
        rightGyroCalibration.ResetObservationState();
        latestLeftMagnetometer = default;
        latestRightMagnetometer = default;
        latestLeftMagnetometerTimestampQpc = 0;
        latestRightMagnetometerTimestampQpc = 0;
        hasLatestLeftMagnetometer = false;
        hasLatestRightMagnetometer = false;
        hasFusionPolicy = false;
        previousFusionEnabled = false;
        previousDominantSide = default;
        previousFusionConfigurationEpoch = 0;
        hasPresentationMode = false;
        previousPresentationMode = default;
        hasTimestamp = false;
        lastTimestampQpc = 0;
        previous.populate(0, 0, 0, 0, 0, 0, 0.0);
        current.populate(0, 0, 0, 0, 0, 0, 0.0, previous);
        if (destination != null)
        {
            destination.Motion = current;
        }
    }

    internal void RestartGyroCalibration()
    {
        leftGyroCalibration.RestartPreservingBias();
        rightGyroCalibration.RestartPreservingBias();
    }

    internal long GyroCalibrationElapsedMilliseconds => Math.Max(
        leftGyroCalibration.CalibrationElapsedMilliseconds,
        rightGyroCalibration.CalibrationElapsedMilliseconds);

    internal bool HasCalibratedLeftGyroBias =>
        leftGyroCalibration.HasCommittedBias;

    internal bool HasCalibratedRightGyroBias =>
        rightGyroCalibration.HasCommittedBias;

    internal ulong LeftGyroCalibrationBiasRevision =>
        leftGyroCalibration.BiasRevision;

    internal ulong RightGyroCalibrationBiasRevision =>
        rightGyroCalibration.BiasRevision;

    internal bool TryGetGyroCalibrationRecord(bool left,
        out Switch2GyroCalibrationRecord calibration)
    {
        Switch2StationaryGyroCalibration source = left ?
            leftGyroCalibration : rightGyroCalibration;
        if (source.TryGetBiasDps(JoyConGyroLsbPerDegreeSecond,
                out Vector3 biasDps))
        {
            return Switch2GyroCalibrationRecord.TryCreate(biasDps,
                out calibration);
        }
        calibration = default;
        return false;
    }

    internal bool TryAdoptGyroCalibration(bool left,
        in Switch2GyroCalibrationRecord calibration) =>
        calibration.IsValid && (left ? leftGyroCalibration :
            rightGyroCalibration).TryAdoptBiasDps(calibration.BiasDps,
                JoyConGyroLsbPerDegreeSecond);

    internal bool IsMagnetometerCalibrationActive =>
        leftMagnetometerCalibrationSession.IsCollecting ||
        rightMagnetometerCalibrationSession.IsCollecting;

    internal int LeftMagnetometerCalibrationSampleCount =>
        leftMagnetometerCalibrationSession.SampleCount;

    internal int RightMagnetometerCalibrationSampleCount =>
        rightMagnetometerCalibrationSession.SampleCount;

    internal Switch2MagnetometerCalibration LeftMagnetometerCalibration =>
        leftMagnetometerCalibration;

    internal Switch2MagnetometerCalibration RightMagnetometerCalibration =>
        rightMagnetometerCalibration;

    internal Switch2MagnetometerCalibrationQuality
        LastLeftMagnetometerCalibrationQuality =>
            lastLeftMagnetometerCalibrationQuality;

    internal Switch2MagnetometerCalibrationQuality
        LastRightMagnetometerCalibrationQuality =>
            lastRightMagnetometerCalibrationQuality;

    internal bool StartMagnetometerCalibration(bool left, bool right)
    {
        if (!left && !right)
        {
            return false;
        }
        if (left)
        {
            leftMagnetometerCalibrationSession.Start();
        }
        if (right)
        {
            rightMagnetometerCalibrationSession.Start();
        }
        magnetometerYawAssist.Reset();
        return true;
    }

    internal void CancelMagnetometerCalibration()
    {
        leftMagnetometerCalibrationSession.Cancel();
        rightMagnetometerCalibrationSession.Cancel();
        magnetometerYawAssist.Reset();
    }

    internal bool TryCompleteMagnetometerCalibration(bool left, bool right,
        out Switch2MagnetometerCalibrationQuality leftQuality,
        out Switch2MagnetometerCalibrationQuality rightQuality)
    {
        leftQuality = default;
        rightQuality = default;
        bool leftSucceeded = !left;
        bool rightSucceeded = !right;
        Switch2MagnetometerCalibration leftCandidate = default;
        Switch2MagnetometerCalibration rightCandidate = default;
        if (left)
        {
            leftSucceeded = leftMagnetometerCalibrationSession.TryComplete(
                out leftCandidate, out leftQuality);
            lastLeftMagnetometerCalibrationQuality = leftQuality;
        }
        if (right)
        {
            rightSucceeded = rightMagnetometerCalibrationSession.TryComplete(
                out rightCandidate, out rightQuality);
            lastRightMagnetometerCalibrationQuality = rightQuality;
        }

        // A joined calibration is atomic. A good half must not silently
        // replace its transform when its physical peer failed quality gates.
        bool succeeded = leftSucceeded && rightSucceeded;
        if (succeeded)
        {
            if (left)
            {
                leftMagnetometerCalibration = leftCandidate;
                leftMagnetometerCalibrationEpoch++;
            }
            if (right)
            {
                rightMagnetometerCalibration = rightCandidate;
                rightMagnetometerCalibrationEpoch++;
            }
            ClearMagnetometerCache();
        }
        magnetometerYawAssist.Reset();
        return succeeded;
    }

    internal bool TryAdoptMagnetometerCalibration(bool left,
        in Switch2MagnetometerCalibration calibration)
    {
        Switch2MagnetometerCalibrationSession session = left ?
            leftMagnetometerCalibrationSession :
            rightMagnetometerCalibrationSession;
        if (!calibration.IsValid || session.IsCollecting)
        {
            return false;
        }
        if (left)
        {
            leftMagnetometerCalibration = calibration;
            leftMagnetometerCalibrationEpoch++;
        }
        else
        {
            rightMagnetometerCalibration = calibration;
            rightMagnetometerCalibrationEpoch++;
        }
        ClearMagnetometerCache();
        magnetometerYawAssist.Reset();
        return true;
    }

    private static bool TryCreateCalibratedSample(
        in Switch2JoyConProfileSide source,
        Switch2StationaryGyroCalibration calibration, bool active,
        out Switch2JoyConMotionSample sample)
    {
        sample = source.ToMotionSample(active);
        if (!source.HasCommonMotion)
        {
            return true;
        }

        if (!calibration.TryObserve(sample.Gyroscope,
                sample.Accelerometer, JoyConGyroLsbPerDegreeSecond,
                JoyConAccelerometerLsbPerG,
                source.CompletionTimestampQpc, source.QpcFrequency,
                out _))
        {
            sample = default;
            return false;
        }

        sample = new Switch2JoyConMotionSample(sample.Gyroscope,
            sample.Accelerometer, calibration.Bias, sample.Active);
        return true;
    }

    internal static bool TryOrient(Switch2JoyConProfileMode mode,
        Switch2JoyConSide side, in Vector3 gyro, in Vector3 acceleration,
        out Vector3 orientedGyro, out Vector3 orientedAcceleration)
    {
        switch (mode)
        {
            case Switch2JoyConProfileMode.Joined:
                orientedGyro = new Vector3(gyro.X, gyro.Z, -gyro.Y);
                orientedAcceleration = new Vector3(acceleration.X,
                    acceleration.Z, -acceleration.Y);
                return true;
            case Switch2JoyConProfileMode.StandaloneHorizontalLeft
                when side == Switch2JoyConSide.Left:
                orientedGyro = new Vector3(-gyro.Y, gyro.Z, gyro.X);
                orientedAcceleration = new Vector3(-acceleration.Y,
                    acceleration.Z, -acceleration.X);
                return true;
            case Switch2JoyConProfileMode.StandaloneHorizontalRight
                when side == Switch2JoyConSide.Right:
                orientedGyro = new Vector3(gyro.Y, gyro.Z, -gyro.X);
                orientedAcceleration = new Vector3(-acceleration.Y,
                    acceleration.Z, acceleration.X);
                return true;
            case Switch2JoyConProfileMode.StandaloneVerticalLeft
                when side == Switch2JoyConSide.Left:
                // Switch2Connect vertical-left is (Y,-X,Z) relative to its
                // horizontal sensor basis. Continue through the same DS4
                // report-axis projection used above.
                orientedGyro = new Vector3(gyro.X, gyro.Z, gyro.Y);
                orientedAcceleration = new Vector3(acceleration.X,
                    acceleration.Z, -acceleration.Y);
                return true;
            case Switch2JoyConProfileMode.StandaloneVerticalRight
                when side == Switch2JoyConSide.Right:
                // Switch2Connect vertical-right is (Y,X,-Z) relative to the
                // right horizontal basis.
                orientedGyro = new Vector3(gyro.X, gyro.Z, gyro.Y);
                orientedAcceleration = new Vector3(-acceleration.X,
                    acceleration.Z, -acceleration.Y);
                return true;
            default:
                orientedGyro = default;
                orientedAcceleration = default;
                return false;
        }
    }

    private static Switch2JoyConSide ReferenceSide(
        Switch2ControllerModel model) => model switch
        {
            Switch2ControllerModel.JoyCon2Left => Switch2JoyConSide.Left,
            Switch2ControllerModel.JoyCon2Right => Switch2JoyConSide.Right,
            _ => Switch2JoyConSide.Invalid,
        };

    private static int ClampRound(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }
        return (int)MathF.Round(Math.Clamp(value, short.MinValue,
            short.MaxValue));
    }

    private Vector3 SelectMagnetometer(
        in Switch2JoyConProfileInputFrame frame, Switch2JoyConSide side,
        out bool fresh)
    {
        fresh = false;
        Switch2JoyConProfileSide source = side == Switch2JoyConSide.Left ?
            frame.LeftSource : frame.RightSource;
        if (!source.IsPresent)
        {
            return Vector3.Zero;
        }

        Switch2Vector3Raw sample = source.Magnetometer;
        if (sample.X != 0 || sample.Y != 0 || sample.Z != 0)
        {
            Vector3 raw = new(sample.X, sample.Y, sample.Z);
            Switch2MagnetometerCalibrationSession session =
                side == Switch2JoyConSide.Left ?
                    leftMagnetometerCalibrationSession :
                    rightMagnetometerCalibrationSession;
            session.TryObserve(raw);
            Switch2MagnetometerCalibration calibration =
                side == Switch2JoyConSide.Left ?
                    leftMagnetometerCalibration :
                    rightMagnetometerCalibration;
            Vector3 value = calibration.TryTransform(raw,
                out Vector3 calibrated) ? calibrated : raw;
            fresh = true;
            if (side == Switch2JoyConSide.Left)
            {
                latestLeftMagnetometer = value;
                latestLeftMagnetometerTimestampQpc =
                    frame.CompletionTimestampQpc;
                hasLatestLeftMagnetometer = true;
            }
            else
            {
                latestRightMagnetometer = value;
                latestRightMagnetometerTimestampQpc =
                    frame.CompletionTimestampQpc;
                hasLatestRightMagnetometer = true;
            }
            return value;
        }

        Vector3 latest = side == Switch2JoyConSide.Left ?
            latestLeftMagnetometer : latestRightMagnetometer;
        long timestamp = side == Switch2JoyConSide.Left ?
            latestLeftMagnetometerTimestampQpc :
            latestRightMagnetometerTimestampQpc;
        bool hasLatest = side == Switch2JoyConSide.Left ?
            hasLatestLeftMagnetometer : hasLatestRightMagnetometer;
        if (hasLatest && frame.CompletionTimestampQpc >= timestamp &&
            (frame.CompletionTimestampQpc - timestamp) /
                (double)frame.QpcFrequency <= MagnetometerCacheSeconds)
        {
            return latest;
        }

        return Vector3.Zero;
    }

    private void ClearMagnetometerCache()
    {
        latestLeftMagnetometer = default;
        latestRightMagnetometer = default;
        latestLeftMagnetometerTimestampQpc = 0;
        latestRightMagnetometerTimestampQpc = 0;
        hasLatestLeftMagnetometer = false;
        hasLatestRightMagnetometer = false;
    }
}
