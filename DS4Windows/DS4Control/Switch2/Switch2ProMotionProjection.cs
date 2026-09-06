/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

Axis orientation and native sensor scales are adapted from the GPL-3.0
Switch2Connect project, commit 4487322a306f04efa27682e3f3a508635a84fd98,
src/virtual_controller.py lines 3022-3043, with the Pro gyro scale corrected
from commit 61ac6642ce12fe7217e38a860b14863b18ca7e28 src/controller.py. The result
enters DS4Windows' existing SixAxis mapping path.
*/

using System;
using System.Numerics;

namespace DS4Windows.Switch2;

/// <summary>
/// Allocation-free Switch 2 Pro motion projection for one serialized runtime
/// lifetime.  It does not introduce a second motion or mapping scheduler.
/// </summary>
internal sealed class Switch2ProMotionProjection
{
    // ST +/-2000 dps scale used by Switch 2 Pro: 70 mdps/LSB.
    internal const float NativeGyroLsbPerDegreeSecond = 14.285714f;
    private const float Ds4WindowsGyroLsbPerDegreeSecond = 16.0f;
    private const float NativeAccelerometerLsbPerG = 4096.0f;
    private const float Ds4WindowsAccelerometerLsbPerG = 8192.0f;
    private const double MagnetometerCacheSeconds = 0.10;

    private SixAxis current = new(0, 0, 0, 0, 0, 0, 0.0);
    private SixAxis previous = new(0, 0, 0, 0, 0, 0, 0.0);
    private readonly Switch2MagnetometerYawAssist magnetometerYawAssist =
        new();
    private readonly Switch2HorizonStabilizer horizonStabilizer = new();
    private readonly Switch2StationaryGyroCalibration gyroCalibration =
        new();
    private readonly Switch2MagnetometerCalibrationSession
        magnetometerCalibrationSession = new();
    private Switch2MagnetometerCalibration magnetometerCalibration;
    private Switch2MagnetometerCalibrationQuality
        lastMagnetometerCalibrationQuality;
    private ulong magnetometerCalibrationEpoch;
    private Vector3 latestMagnetometer;
    private long latestMagnetometerTimestampQpc;
    private bool hasLatestMagnetometer;
    private long lastTimestampQpc;
    private bool hasTimestamp;

    internal bool TryApply(in Switch2ProProfileInputFrame frame,
        DS4State destination) => TryApply(frame, destination,
            magnetometerYawAssistEnabled: false,
            virtualGyroSoftDeadzone: Switch2MotionSoftDeadzone.Default,
            horizonStabilizationEnabled: false);

    internal bool TryApply(in Switch2ProProfileInputFrame frame,
        DS4State destination, bool magnetometerYawAssistEnabled) =>
        TryApply(frame, destination, magnetometerYawAssistEnabled,
            Switch2MotionSoftDeadzone.Default,
            horizonStabilizationEnabled: false);

    internal bool TryApply(in Switch2ProProfileInputFrame frame,
        DS4State destination, bool magnetometerYawAssistEnabled,
        double virtualGyroSoftDeadzone) => TryApply(frame, destination,
            magnetometerYawAssistEnabled, virtualGyroSoftDeadzone,
            horizonStabilizationEnabled: false);

    internal bool TryApply(in Switch2ProProfileInputFrame frame,
        DS4State destination, bool magnetometerYawAssistEnabled,
        double virtualGyroSoftDeadzone, bool horizonStabilizationEnabled)
    {
        if (destination == null ||
            frame.Version != Switch2ProProfileInputFrame.CurrentVersion ||
            !frame.HasCommonMotion || frame.QpcFrequency <= 0)
        {
            return false;
        }

        Vector3 rawGyroscope = new(frame.Gyroscope.X, frame.Gyroscope.Y,
            frame.Gyroscope.Z);
        Vector3 rawAccelerometer = new(frame.Accelerometer.X,
            frame.Accelerometer.Y, frame.Accelerometer.Z);
        if (!gyroCalibration.TryObserve(rawGyroscope, rawAccelerometer,
                NativeGyroLsbPerDegreeSecond,
                NativeAccelerometerLsbPerG,
                frame.CompletionTimestampQpc, frame.QpcFrequency,
                out Vector3 calibratedGyroscope))
        {
            return false;
        }
        rawGyroscope = calibratedGyroscope;
        Vector3 magnetometer = SelectMagnetometer(frame.Magnetometer,
            frame.CompletionTimestampQpc, frame.QpcFrequency,
            out bool magnetometerFresh);

        double elapsed = 0.0;
        if (hasTimestamp && frame.CompletionTimestampQpc >= lastTimestampQpc)
        {
            elapsed = (frame.CompletionTimestampQpc - lastTimestampQpc) /
                (double)frame.QpcFrequency;
        }
        lastTimestampQpc = frame.CompletionTimestampQpc;
        hasTimestamp = true;

        ulong motionEpoch = unchecked(frame.DeviceGeneration *
            1099511628211UL ^ gyroCalibration.CalibrationEpoch ^
            magnetometerCalibrationEpoch * 16777619UL);
        if (!magnetometerYawAssist.TryApply(rawGyroscope,
                rawAccelerometer, magnetometer,
                NativeGyroLsbPerDegreeSecond, elapsed,
                magnetometerYawAssistEnabled, motionEpoch,
                magnetometerFresh,
                out Switch2MagnetometerYawAssistResult assisted))
        {
            return false;
        }
        rawGyroscope = assisted.Gyroscope;
        if (!horizonStabilizer.TryApply(rawGyroscope, rawAccelerometer,
                NativeGyroLsbPerDegreeSecond,
                NativeAccelerometerLsbPerG, elapsed,
                horizonStabilizationEnabled, motionEpoch,
                horizontal: false,
                out Switch2HorizonProjectionResult horizon))
        {
            return false;
        }
        rawGyroscope = horizon.Gyroscope;
        rawAccelerometer = horizon.Accelerometer;
        rawGyroscope = Switch2MotionSoftDeadzone.Apply(rawGyroscope,
            virtualGyroSoftDeadzone, horizontal: false);

        // Pinned donor transform: base gyro=(x,-y,-z),
        // base accel=(-x,-y,-z), then DS motion order=(x,z,-y).
        const float gyroScale = Ds4WindowsGyroLsbPerDegreeSecond /
            NativeGyroLsbPerDegreeSecond;
        const float accelerometerScale =
            Ds4WindowsAccelerometerLsbPerG / NativeAccelerometerLsbPerG;
        int gyroX = ClampRound(rawGyroscope.X * gyroScale);
        int gyroY = ClampRound(-rawGyroscope.Z * gyroScale);
        int gyroZ = ClampRound(rawGyroscope.Y * gyroScale);
        int accelX = ClampRound(-rawAccelerometer.X * accelerometerScale);
        int accelY = ClampRound(-rawAccelerometer.Z * accelerometerScale);
        int accelZ = ClampRound(rawAccelerometer.Y * accelerometerScale);

        SixAxis swap = previous;
        previous = current;
        current = swap;
        // populate consumes semantic yaw/pitch/roll. The transform above is
        // already in DS report X/Y/Z order.
        current.populate(gyroY, gyroX, gyroZ, accelX, accelY, accelZ,
            elapsed, previous);
        destination.Motion = current;
        return true;
    }

    internal void Reset(DS4State destination)
    {
        hasTimestamp = false;
        lastTimestampQpc = 0;
        magnetometerYawAssist.Reset();
        horizonStabilizer.Reset();
        gyroCalibration.ResetObservationState();
        latestMagnetometer = default;
        latestMagnetometerTimestampQpc = 0;
        hasLatestMagnetometer = false;
        previous.populate(0, 0, 0, 0, 0, 0, 0.0);
        current.populate(0, 0, 0, 0, 0, 0, 0.0, previous);
        if (destination != null)
        {
            destination.Motion = current;
        }
    }

    internal void RestartGyroCalibration() =>
        gyroCalibration.RestartPreservingBias();

    internal long GyroCalibrationElapsedMilliseconds =>
        gyroCalibration.CalibrationElapsedMilliseconds;

    internal bool HasCalibratedGyroBias =>
        gyroCalibration.HasCommittedBias;

    internal ulong GyroCalibrationBiasRevision =>
        gyroCalibration.BiasRevision;

    internal bool TryGetGyroCalibrationRecord(
        out Switch2GyroCalibrationRecord calibration)
    {
        if (gyroCalibration.TryGetBiasDps(
                NativeGyroLsbPerDegreeSecond, out Vector3 biasDps))
        {
            return Switch2GyroCalibrationRecord.TryCreate(biasDps,
                out calibration);
        }
        calibration = default;
        return false;
    }

    internal bool TryAdoptGyroCalibration(
        in Switch2GyroCalibrationRecord calibration) =>
        calibration.IsValid && gyroCalibration.TryAdoptBiasDps(
            calibration.BiasDps, NativeGyroLsbPerDegreeSecond);

    internal bool IsMagnetometerCalibrationActive =>
        magnetometerCalibrationSession.IsCollecting;

    internal int MagnetometerCalibrationSampleCount =>
        magnetometerCalibrationSession.SampleCount;

    internal Switch2MagnetometerCalibration MagnetometerCalibration =>
        magnetometerCalibration;

    internal Switch2MagnetometerCalibrationQuality
        LastMagnetometerCalibrationQuality =>
            lastMagnetometerCalibrationQuality;

    internal void StartMagnetometerCalibration()
    {
        magnetometerCalibrationSession.Start();
        magnetometerYawAssist.Reset();
    }

    internal void CancelMagnetometerCalibration()
    {
        magnetometerCalibrationSession.Cancel();
        magnetometerYawAssist.Reset();
    }

    internal bool TryCompleteMagnetometerCalibration(
        out Switch2MagnetometerCalibrationQuality quality)
    {
        bool succeeded = magnetometerCalibrationSession.TryComplete(
            out Switch2MagnetometerCalibration candidate, out quality);
        lastMagnetometerCalibrationQuality = quality;
        if (succeeded)
        {
            magnetometerCalibration = candidate;
            magnetometerCalibrationEpoch++;
            ClearMagnetometerCache();
        }
        magnetometerYawAssist.Reset();
        return succeeded;
    }

    internal bool TryAdoptMagnetometerCalibration(
        in Switch2MagnetometerCalibration calibration)
    {
        if (!calibration.IsValid ||
            magnetometerCalibrationSession.IsCollecting)
        {
            return false;
        }
        magnetometerCalibration = calibration;
        magnetometerCalibrationEpoch++;
        ClearMagnetometerCache();
        magnetometerYawAssist.Reset();
        return true;
    }

    private static int ClampRound(float value) => !float.IsFinite(value) ? 0 :
        (int)MathF.Round(Math.Clamp(value, short.MinValue, short.MaxValue));

    private Vector3 SelectMagnetometer(in Switch2Vector3Raw sample,
        long timestampQpc, long qpcFrequency, out bool fresh)
    {
        fresh = false;
        if (sample.X != 0 || sample.Y != 0 || sample.Z != 0)
        {
            Vector3 raw = new(sample.X, sample.Y, sample.Z);
            magnetometerCalibrationSession.TryObserve(raw);
            latestMagnetometer = magnetometerCalibration.TryTransform(raw,
                out Vector3 calibrated) ? calibrated : raw;
            latestMagnetometerTimestampQpc = timestampQpc;
            hasLatestMagnetometer = true;
            fresh = true;
            return latestMagnetometer;
        }

        if (hasLatestMagnetometer && timestampQpc >=
                latestMagnetometerTimestampQpc &&
            (timestampQpc - latestMagnetometerTimestampQpc) /
                (double)qpcFrequency <= MagnetometerCacheSeconds)
        {
            return latestMagnetometer;
        }

        return Vector3.Zero;
    }

    private void ClearMagnetometerCache()
    {
        latestMagnetometer = default;
        latestMagnetometerTimestampQpc = 0;
        hasLatestMagnetometer = false;
    }
}
