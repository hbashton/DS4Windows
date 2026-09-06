/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The sector-change Tap policy is adapted from the GPL-3.0 licensed
Switch2Connect project, commit 61ac6642ce12fe7217e38a860b14863b18ca7e28,
src/controller.py Controller._apply_joystick_scroll_wheel.
*/

using System;

namespace DS4Windows.Switch2;

public enum Switch2StickScrollActivationMode : byte
{
    Hold = 0,
    Tap = 1,
}

[Flags]
internal enum Switch2StickScrollSector : byte
{
    None = 0,
    Left = 1 << 0,
    Right = 1 << 1,
    Up = 1 << 2,
    Down = 1 << 3,
}

internal readonly struct Switch2StickScrollLifetime :
    IEquatable<Switch2StickScrollLifetime>
{
    internal Switch2StickScrollLifetime(byte sourceKind, ulong pairEpoch,
        ulong firstDeviceGeneration, ulong firstTransportGeneration,
        ulong secondDeviceGeneration, ulong secondTransportGeneration,
        long qpcFrequency)
    {
        SourceKind = sourceKind;
        PairEpoch = pairEpoch;
        FirstDeviceGeneration = firstDeviceGeneration;
        FirstTransportGeneration = firstTransportGeneration;
        SecondDeviceGeneration = secondDeviceGeneration;
        SecondTransportGeneration = secondTransportGeneration;
        QpcFrequency = qpcFrequency;
    }

    internal byte SourceKind { get; }
    internal ulong PairEpoch { get; }
    internal ulong FirstDeviceGeneration { get; }
    internal ulong FirstTransportGeneration { get; }
    internal ulong SecondDeviceGeneration { get; }
    internal ulong SecondTransportGeneration { get; }
    internal long QpcFrequency { get; }

    public bool Equals(Switch2StickScrollLifetime other) =>
        SourceKind == other.SourceKind && PairEpoch == other.PairEpoch &&
        FirstDeviceGeneration == other.FirstDeviceGeneration &&
        FirstTransportGeneration == other.FirstTransportGeneration &&
        SecondDeviceGeneration == other.SecondDeviceGeneration &&
        SecondTransportGeneration == other.SecondTransportGeneration &&
        QpcFrequency == other.QpcFrequency;

    public override bool Equals(object obj) =>
        obj is Switch2StickScrollLifetime other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(SourceKind,
        PairEpoch, FirstDeviceGeneration, FirstTransportGeneration,
        SecondDeviceGeneration, SecondTransportGeneration, QpcFrequency);
}

internal struct Switch2StickScrollTapLaneState
{
    internal Switch2StickScrollLifetime Lifetime;
    internal Switch2StickScrollActivationMode LeftMode;
    internal Switch2StickScrollActivationMode RightMode;
    internal Switch2StickScrollSector LeftSector;
    internal Switch2StickScrollSector RightSector;
    internal long TimestampQpc;
    internal long LeftLastEmitTimestampQpc;
    internal long RightLastEmitTimestampQpc;
    internal long ProfileRevision;
    internal bool HasBaseline;
}

internal readonly struct Switch2StickScrollTapFrame
{
    internal Switch2StickScrollTapFrame(bool isValid,
        Switch2StickScrollActivationMode leftMode,
        Switch2StickScrollActivationMode rightMode,
        Switch2StickScrollSector leftEmit,
        Switch2StickScrollSector rightEmit, int leftStep, int rightStep)
    {
        IsValid = isValid;
        LeftMode = leftMode;
        RightMode = rightMode;
        LeftEmit = leftEmit;
        RightEmit = rightEmit;
        LeftStep = leftStep;
        RightStep = rightStep;
    }

    internal bool IsValid { get; }
    internal Switch2StickScrollActivationMode LeftMode { get; }
    internal Switch2StickScrollActivationMode RightMode { get; }
    internal Switch2StickScrollSector LeftEmit { get; }
    internal Switch2StickScrollSector RightEmit { get; }
    internal int LeftStep { get; }
    internal int RightStep { get; }

    internal bool TryHandle(DS4Controls control, out bool emit, out int step)
    {
        emit = false;
        step = 0;
        if (!IsValid || !TryResolve(control, out bool left,
                out Switch2StickScrollSector direction))
        {
            return false;
        }

        Switch2StickScrollActivationMode mode = left ? LeftMode : RightMode;
        if (mode != Switch2StickScrollActivationMode.Tap)
        {
            return false;
        }

        Switch2StickScrollSector mask = left ? LeftEmit : RightEmit;
        emit = (mask & direction) != 0;
        step = left ? LeftStep : RightStep;
        return true;
    }

    private static bool TryResolve(DS4Controls control, out bool left,
        out Switch2StickScrollSector direction)
    {
        left = true;
        direction = control switch
        {
            DS4Controls.LXNeg => Switch2StickScrollSector.Left,
            DS4Controls.LXPos => Switch2StickScrollSector.Right,
            DS4Controls.LYNeg => Switch2StickScrollSector.Up,
            DS4Controls.LYPos => Switch2StickScrollSector.Down,
            DS4Controls.RXNeg => Switch2StickScrollSector.Left,
            DS4Controls.RXPos => Switch2StickScrollSector.Right,
            DS4Controls.RYNeg => Switch2StickScrollSector.Up,
            DS4Controls.RYPos => Switch2StickScrollSector.Down,
            _ => Switch2StickScrollSector.None,
        };
        if (direction == Switch2StickScrollSector.None)
        {
            return false;
        }

        left = control is DS4Controls.LXNeg or DS4Controls.LXPos or
            DS4Controls.LYNeg or DS4Controls.LYPos;
        return true;
    }
}

/// <summary>
/// Allocation-free sector edge gate for canonical stick-to-wheel actions.
/// It never injects output and never interprets bindings. Mapping remains the
/// sole owner of action resolution, wheel direction, release, and backend.
/// </summary>
internal static class Switch2StickScrollTapLane
{
    internal const int MinimumTapStep = 30;
    internal const int MaximumTapStep = 150;
    internal const double DefaultCenterDeadzone = 0.03;
    internal const int MinimumEmitIntervalMilliseconds = 30;

    internal static bool TryAdvance(in Switch2RawInputStatus pro,
        in Switch2JoyConRawInputStatus joyCon, double lx, double ly, double rx,
        double ry, Switch2StickScrollActivationMode leftMode,
        Switch2StickScrollActivationMode rightMode, long profileRevision,
        ref Switch2StickScrollTapLaneState state,
        out Switch2StickScrollTapFrame frame)
    {
        frame = default;
        if (!AreValidProfileCoordinates(lx, ly, rx, ry) ||
            !IsValidMode(leftMode) || !IsValidMode(rightMode) ||
            profileRevision < 0 || !TryGetSource(pro, joyCon,
                out Switch2StickScrollLifetime lifetime,
                out long timestampQpc))
        {
            state = default;
            return false;
        }

        Switch2StickScrollSector leftSector = ResolveSector(lx, ly);
        Switch2StickScrollSector rightSector = ResolveSector(rx, ry);
        int leftStep = ResolveStep(lx, ly);
        int rightStep = ResolveStep(rx, ry);
        bool reset = !state.HasBaseline ||
            !state.Lifetime.Equals(lifetime) ||
            state.ProfileRevision != profileRevision ||
            state.LeftMode != leftMode || state.RightMode != rightMode ||
            timestampQpc < state.TimestampQpc;
        if (reset)
        {
            state = new Switch2StickScrollTapLaneState
            {
                Lifetime = lifetime,
                LeftMode = leftMode,
                RightMode = rightMode,
                LeftSector = leftSector,
                RightSector = rightSector,
                TimestampQpc = timestampQpc,
                ProfileRevision = profileRevision,
                HasBaseline = true,
            };
            frame = new Switch2StickScrollTapFrame(true, leftMode,
                rightMode, Switch2StickScrollSector.None,
                Switch2StickScrollSector.None, leftStep, rightStep);
            return true;
        }

        if (timestampQpc == state.TimestampQpc)
        {
            frame = new Switch2StickScrollTapFrame(true, leftMode,
                rightMode, Switch2StickScrollSector.None,
                Switch2StickScrollSector.None, leftStep, rightStep);
            return true;
        }

        long throttleTicks = Math.Max(1L, (long)Math.Ceiling(
            lifetime.QpcFrequency *
            (MinimumEmitIntervalMilliseconds / 1_000.0)));
        bool advanceLeft = state.LeftLastEmitTimestampQpc == 0 ||
            timestampQpc - state.LeftLastEmitTimestampQpc >= throttleTicks;
        bool advanceRight = state.RightLastEmitTimestampQpc == 0 ||
            timestampQpc - state.RightLastEmitTimestampQpc >= throttleTicks;
        Switch2StickScrollSector leftEmit =
            advanceLeft && leftMode == Switch2StickScrollActivationMode.Tap &&
            leftSector != state.LeftSector ? leftSector :
                Switch2StickScrollSector.None;
        Switch2StickScrollSector rightEmit =
            advanceRight &&
            rightMode == Switch2StickScrollActivationMode.Tap &&
            rightSector != state.RightSector ? rightSector :
                Switch2StickScrollSector.None;
        if (advanceLeft)
        {
            state.LeftSector = leftSector;
        }
        if (advanceRight)
        {
            state.RightSector = rightSector;
        }
        state.TimestampQpc = timestampQpc;
        if (leftEmit != Switch2StickScrollSector.None)
        {
            state.LeftLastEmitTimestampQpc = timestampQpc;
        }
        if (rightEmit != Switch2StickScrollSector.None)
        {
            state.RightLastEmitTimestampQpc = timestampQpc;
        }
        frame = new Switch2StickScrollTapFrame(true, leftMode, rightMode,
            leftEmit, rightEmit, leftStep, rightStep);
        return true;
    }

    internal static bool TryGetSource(in Switch2RawInputStatus pro,
        in Switch2JoyConRawInputStatus joyCon,
        out Switch2StickScrollLifetime lifetime, out long timestampQpc)
    {
        lifetime = default;
        timestampQpc = 0;
        bool proValid = pro.IsValid && pro.ContractVersion ==
                Switch2ProProfileInputFrame.CurrentVersion &&
            pro.DeviceGeneration != 0 && pro.TransportGeneration != 0 &&
            pro.CompletionTimestampQpc > 0 && pro.QpcFrequency > 0;
        bool joyConValid = IsValidJoyConSource(joyCon);
        if (proValid == joyConValid)
        {
            return false;
        }

        if (proValid)
        {
            lifetime = new Switch2StickScrollLifetime(sourceKind: 1,
                pairEpoch: 0, pro.DeviceGeneration,
                pro.TransportGeneration, 0, 0, pro.QpcFrequency);
            timestampQpc = pro.CompletionTimestampQpc;
            return true;
        }

        lifetime = new Switch2StickScrollLifetime(sourceKind: 2,
            joyCon.PairEpoch, joyCon.LeftDeviceGeneration,
            joyCon.LeftTransportGeneration, joyCon.RightDeviceGeneration,
            joyCon.RightTransportGeneration, joyCon.QpcFrequency);
        timestampQpc = joyCon.CompletionTimestampQpc;
        return true;
    }

    private static bool IsValidJoyConSource(
        in Switch2JoyConRawInputStatus input)
    {
        if (!input.IsValid || input.ContractVersion !=
                Switch2JoyConProfileInputFrame.CurrentVersion ||
            input.CompletionTimestampQpc <= 0 || input.QpcFrequency <= 0)
        {
            return false;
        }

        bool leftValid = !input.LeftPresent ||
            input.LeftDeviceGeneration != 0 &&
            input.LeftTransportGeneration != 0;
        bool rightValid = !input.RightPresent ||
            input.RightDeviceGeneration != 0 &&
            input.RightTransportGeneration != 0;
        return (input.LeftPresent || input.RightPresent) && leftValid &&
            rightValid;
    }

    internal static bool AreValidProfileCoordinates(double lx, double ly,
        double rx, double ry) => IsValidProfileCoordinate(lx) &&
        IsValidProfileCoordinate(ly) && IsValidProfileCoordinate(rx) &&
        IsValidProfileCoordinate(ry);

    private static bool IsValidProfileCoordinate(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 255;

    internal static Switch2StickScrollSector ResolveSector(double x, double y)
    {
        if (!IsValidProfileCoordinate(x) || !IsValidProfileCoordinate(y))
            return Switch2StickScrollSector.None;
        double dx = x - 128;
        double dy = y - 128;
        double magnitude = Math.Min(1.0,
            Math.Sqrt(dx * dx + dy * dy) / 127.0);
        if (magnitude <= DefaultCenterDeadzone)
        {
            return Switch2StickScrollSector.None;
        }

        double angleRadians = Math.Atan2(-(y - 128), x - 128);
        double angle = (angleRadians >= 0 ? angleRadians :
            2 * Math.PI + angleRadians) * 180.0 / Math.PI;
        Switch2StickScrollSector sector = Switch2StickScrollSector.None;
        if (x < 128 && angle is >= 112.5 and <= 247.5)
        {
            sector |= Switch2StickScrollSector.Left;
        }
        else if (x > 128 && (angle <= 67.5 || angle >= 292.5))
        {
            sector |= Switch2StickScrollSector.Right;
        }
        if (y < 128 && angle is >= 22.5 and <= 157.5)
        {
            sector |= Switch2StickScrollSector.Up;
        }
        else if (y > 128 && angle is >= 202.5 and <= 337.5)
        {
            sector |= Switch2StickScrollSector.Down;
        }
        return sector;
    }

    private static int ResolveStep(double x, double y)
    {
        double dx = x - 128;
        double dy = y - 128;
        double magnitude = Math.Min(1.0,
            Math.Sqrt(dx * dx + dy * dy) / 127.0);
        double normalizedMagnitude = magnitude <= DefaultCenterDeadzone ?
            0.0 : (magnitude - DefaultCenterDeadzone) /
                (1.0 - DefaultCenterDeadzone);
        return Math.Max(MinimumTapStep,
            (int)(MaximumTapStep * normalizedMagnitude));
    }

    private static bool IsValidMode(
        Switch2StickScrollActivationMode mode) => mode is
            Switch2StickScrollActivationMode.Hold or
            Switch2StickScrollActivationMode.Tap;
}
