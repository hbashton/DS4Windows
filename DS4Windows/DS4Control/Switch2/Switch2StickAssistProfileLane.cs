/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

Switch2Connect commit 61ac6642ce12fe7217e38a860b14863b18ca7e28
advertises Stick Assist and establishes the applicable stick selection in
src/controller.py. At that revision the selected sx/sy values are not added to
the mouse target. This implementation supplies the advertised behavior inside
DS4Windows' existing profile mouse accumulator without copying that dead path
or creating another mouse injector.
*/

using System;

namespace DS4Windows.Switch2;

internal enum Switch2StickAssistSource : byte
{
    Invalid = 0,
    ProRight = 1,
    JoinedRight = 2,
    StandaloneLeft = 3,
    StandaloneRight = 4,
}

internal struct Switch2StickAssistProfileLaneState
{
    internal bool HasBaseline;
    internal Switch2StickAssistSource Source;
    internal ulong DeviceGeneration;
    internal ulong TransportGeneration;
    internal ulong PairEpoch;
    internal long ProfileRevision;
    internal long CompletionTimestampQpc;
    internal long QpcFrequency;
}

internal readonly struct Switch2StickAssistResult
{
    internal Switch2StickAssistResult(double deltaX, double deltaY,
        double velocityX, double velocityY)
    {
        DeltaX = deltaX;
        DeltaY = deltaY;
        VelocityX = velocityX;
        VelocityY = velocityY;
    }

    internal double DeltaX { get; }
    internal double DeltaY { get; }
    internal double VelocityX { get; }
    internal double VelocityY { get; }
}

internal static class Switch2StickAssistProfileLane
{
    internal const double MinimumSensitivity = 0.0;
    internal const double MaximumSensitivity = 10.0;
    internal const double DefaultSensitivity = 0.0;
    internal const double PixelsPerSecondPerSensitivityLevel = 48.0;
    internal const double MaximumIntervalSeconds = 0.050;

    internal static double NormalizeSensitivity(double value) =>
        double.IsFinite(value) && value >= MinimumSensitivity &&
            value <= MaximumSensitivity ? value : DefaultSensitivity;

    internal static bool TryAdvance(in Switch2RawInputStatus pro,
        in Switch2JoyConRawInputStatus joyCon, double leftX, double leftY,
        double rightX, double rightY, bool gyroMouseOutputActive,
        double sensitivity, long profileRevision,
        ref Switch2StickAssistProfileLaneState state,
        out Switch2StickAssistResult result)
    {
        result = default;
        sensitivity = NormalizeSensitivity(sensitivity);
        if (!Switch2StickScrollTapLane.AreValidProfileCoordinates(
                leftX, leftY, rightX, rightY) ||
            !gyroMouseOutputActive || sensitivity <= 0.0 ||
            !TrySelectSource(pro, joyCon, leftX, leftY, rightX, rightY,
                out Switch2StickAssistSource source, out ulong deviceGeneration,
                out ulong transportGeneration, out ulong pairEpoch,
                out long timestamp, out long frequency, out double stickX,
                out double stickY))
        {
            state = default;
            return false;
        }

        bool sameLifetime = state.HasBaseline && state.Source == source &&
            state.DeviceGeneration == deviceGeneration &&
            state.TransportGeneration == transportGeneration &&
            state.PairEpoch == pairEpoch &&
            state.ProfileRevision == profileRevision &&
            state.QpcFrequency == frequency;
        if (!sameLifetime || timestamp < state.CompletionTimestampQpc)
        {
            EstablishBaseline(source, deviceGeneration, transportGeneration,
                pairEpoch, profileRevision, timestamp, frequency, ref state);
            return false;
        }

        if (timestamp == state.CompletionTimestampQpc)
        {
            return false;
        }

        long elapsedTicks = timestamp - state.CompletionTimestampQpc;
        state.CompletionTimestampQpc = timestamp;
        double elapsedSeconds = elapsedTicks / (double)frequency;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0 ||
            elapsedSeconds > MaximumIntervalSeconds)
        {
            return false;
        }

        double normalizedX = NormalizeAxis(stickX);
        double normalizedY = NormalizeAxis(stickY);
        if (normalizedX == 0.0 && normalizedY == 0.0)
        {
            return false;
        }

        double velocityScale = sensitivity *
            PixelsPerSecondPerSensitivityLevel;
        double velocityX = normalizedX * velocityScale;
        double velocityY = normalizedY * velocityScale;
        result = new Switch2StickAssistResult(
            velocityX * elapsedSeconds, velocityY * elapsedSeconds,
            velocityX, velocityY);
        return true;
    }

    private static bool TrySelectSource(in Switch2RawInputStatus pro,
        in Switch2JoyConRawInputStatus joyCon, double leftX, double leftY,
        double rightX, double rightY, out Switch2StickAssistSource source,
        out ulong deviceGeneration, out ulong transportGeneration,
        out ulong pairEpoch, out long timestamp, out long frequency,
        out double stickX, out double stickY)
    {
        source = Switch2StickAssistSource.Invalid;
        deviceGeneration = 0;
        transportGeneration = 0;
        pairEpoch = 0;
        timestamp = 0;
        frequency = 0;
        stickX = 128;
        stickY = 128;

        if (pro.IsValid && pro.DeviceGeneration != 0 &&
            pro.TransportGeneration != 0 && pro.CompletionTimestampQpc > 0 &&
            pro.QpcFrequency > 0)
        {
            source = Switch2StickAssistSource.ProRight;
            deviceGeneration = pro.DeviceGeneration;
            transportGeneration = pro.TransportGeneration;
            timestamp = pro.CompletionTimestampQpc;
            frequency = pro.QpcFrequency;
            stickX = rightX;
            stickY = rightY;
            return true;
        }

        if (!joyCon.IsValid || joyCon.CompletionTimestampQpc <= 0 ||
            joyCon.QpcFrequency <= 0)
        {
            return false;
        }

        switch (joyCon.Mode)
        {
            case Switch2JoyConProfileMode.Joined when joyCon.PairEpoch != 0 &&
                    joyCon.RightPresent &&
                    joyCon.RightDeviceGeneration != 0 &&
                    joyCon.RightTransportGeneration != 0:
                source = Switch2StickAssistSource.JoinedRight;
                deviceGeneration = joyCon.RightDeviceGeneration;
                transportGeneration = joyCon.RightTransportGeneration;
                pairEpoch = joyCon.PairEpoch;
                stickX = rightX;
                stickY = rightY;
                break;
            case Switch2JoyConProfileMode.StandaloneHorizontalLeft or
                    Switch2JoyConProfileMode.StandaloneVerticalLeft
                    when joyCon.LeftPresent &&
                        joyCon.LeftDeviceGeneration != 0 &&
                        joyCon.LeftTransportGeneration != 0:
                source = Switch2StickAssistSource.StandaloneLeft;
                deviceGeneration = joyCon.LeftDeviceGeneration;
                transportGeneration = joyCon.LeftTransportGeneration;
                stickX = leftX;
                stickY = leftY;
                break;
            case Switch2JoyConProfileMode.StandaloneHorizontalRight or
                    Switch2JoyConProfileMode.StandaloneVerticalRight
                    when joyCon.RightPresent &&
                        joyCon.RightDeviceGeneration != 0 &&
                        joyCon.RightTransportGeneration != 0:
                source = Switch2StickAssistSource.StandaloneRight;
                deviceGeneration = joyCon.RightDeviceGeneration;
                transportGeneration = joyCon.RightTransportGeneration;
                // Standalone Joy-Cons are mini-controller presentations. The
                // existing profile projection places either physical side's
                // orientation-corrected stick on the logical left stick.
                stickX = leftX;
                stickY = leftY;
                break;
            default:
                return false;
        }

        timestamp = joyCon.CompletionTimestampQpc;
        frequency = joyCon.QpcFrequency;
        return true;
    }

    private static void EstablishBaseline(Switch2StickAssistSource source,
        ulong deviceGeneration, ulong transportGeneration, ulong pairEpoch,
        long profileRevision, long timestamp, long frequency,
        ref Switch2StickAssistProfileLaneState state)
    {
        state.HasBaseline = true;
        state.Source = source;
        state.DeviceGeneration = deviceGeneration;
        state.TransportGeneration = transportGeneration;
        state.PairEpoch = pairEpoch;
        state.ProfileRevision = profileRevision;
        state.CompletionTimestampQpc = timestamp;
        state.QpcFrequency = frequency;
    }

    private static double NormalizeAxis(double value)
    {
        double centered = value - 128;
        if (centered is >= -1 and <= 1)
        {
            return 0.0;
        }

        return centered < 0 ? centered / 128.0 : centered / 127.0;
    }
}
