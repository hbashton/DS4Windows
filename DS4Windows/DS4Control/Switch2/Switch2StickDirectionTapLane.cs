/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The sector-transition Tap policy is adapted from the GPL-3.0 licensed
Switch2Connect project, commit 61ac6642ce12fe7217e38a860b14863b18ca7e28,
src/controller.py Controller._apply_joystick_tokens.
*/

using System;

namespace DS4Windows.Switch2;

public enum Switch2StickDirectionActivationMode : byte
{
    Hold = 0,
    Tap = 1,
}

internal readonly struct Switch2StickDirectionActivationModes
{
    internal Switch2StickDirectionActivationModes(
        Switch2StickDirectionActivationMode leftUp,
        Switch2StickDirectionActivationMode leftDown,
        Switch2StickDirectionActivationMode leftLeft,
        Switch2StickDirectionActivationMode leftRight,
        Switch2StickDirectionActivationMode rightUp,
        Switch2StickDirectionActivationMode rightDown,
        Switch2StickDirectionActivationMode rightLeft,
        Switch2StickDirectionActivationMode rightRight)
    {
        LeftUp = leftUp;
        LeftDown = leftDown;
        LeftLeft = leftLeft;
        LeftRight = leftRight;
        RightUp = rightUp;
        RightDown = rightDown;
        RightLeft = rightLeft;
        RightRight = rightRight;
    }

    private Switch2StickDirectionActivationMode LeftUp { get; }
    private Switch2StickDirectionActivationMode LeftDown { get; }
    private Switch2StickDirectionActivationMode LeftLeft { get; }
    private Switch2StickDirectionActivationMode LeftRight { get; }
    private Switch2StickDirectionActivationMode RightUp { get; }
    private Switch2StickDirectionActivationMode RightDown { get; }
    private Switch2StickDirectionActivationMode RightLeft { get; }
    private Switch2StickDirectionActivationMode RightRight { get; }

    internal bool TryGetTapMasks(out Switch2StickScrollSector left,
        out Switch2StickScrollSector right)
    {
        left = Switch2StickScrollSector.None;
        right = Switch2StickScrollSector.None;
        if (!IsValid(LeftUp) || !IsValid(LeftDown) ||
            !IsValid(LeftLeft) || !IsValid(LeftRight) ||
            !IsValid(RightUp) || !IsValid(RightDown) ||
            !IsValid(RightLeft) || !IsValid(RightRight))
        {
            return false;
        }

        AddTap(LeftUp, Switch2StickScrollSector.Up, ref left);
        AddTap(LeftDown, Switch2StickScrollSector.Down, ref left);
        AddTap(LeftLeft, Switch2StickScrollSector.Left, ref left);
        AddTap(LeftRight, Switch2StickScrollSector.Right, ref left);
        AddTap(RightUp, Switch2StickScrollSector.Up, ref right);
        AddTap(RightDown, Switch2StickScrollSector.Down, ref right);
        AddTap(RightLeft, Switch2StickScrollSector.Left, ref right);
        AddTap(RightRight, Switch2StickScrollSector.Right, ref right);
        return true;
    }

    private static void AddTap(Switch2StickDirectionActivationMode mode,
        Switch2StickScrollSector direction,
        ref Switch2StickScrollSector mask)
    {
        if (mode == Switch2StickDirectionActivationMode.Tap)
        {
            mask |= direction;
        }
    }

    private static bool IsValid(Switch2StickDirectionActivationMode mode) =>
        mode is Switch2StickDirectionActivationMode.Hold or
            Switch2StickDirectionActivationMode.Tap;
}

internal struct Switch2StickDirectionTapLaneState
{
    internal Switch2StickScrollLifetime Lifetime;
    internal Switch2StickScrollSector LeftTapMask;
    internal Switch2StickScrollSector RightTapMask;
    internal Switch2StickScrollSector LeftSector;
    internal Switch2StickScrollSector RightSector;
    internal Switch2StickScrollSector LeftTriggeredDirections;
    internal Switch2StickScrollSector RightTriggeredDirections;
    internal long LeftExpiry;
    internal long RightExpiry;
    internal long UpExpiry;
    internal long DownExpiry;
    internal long RightLeftExpiry;
    internal long RightRightExpiry;
    internal long RightUpExpiry;
    internal long RightDownExpiry;
    internal long TimestampQpc;
    internal long ProfileRevision;
    internal bool HasBaseline;
}

internal readonly struct Switch2StickDirectionTapFrame
{
    internal Switch2StickDirectionTapFrame(bool isValid,
        Switch2StickScrollSector leftTapMask,
        Switch2StickScrollSector rightTapMask,
        Switch2StickScrollSector leftActive,
        Switch2StickScrollSector rightActive)
    {
        IsValid = isValid;
        LeftTapMask = leftTapMask;
        RightTapMask = rightTapMask;
        LeftActive = leftActive;
        RightActive = rightActive;
    }

    internal bool IsValid { get; }
    private Switch2StickScrollSector LeftTapMask { get; }
    private Switch2StickScrollSector RightTapMask { get; }
    private Switch2StickScrollSector LeftActive { get; }
    private Switch2StickScrollSector RightActive { get; }

    internal bool TryOverride(DS4Controls control, out bool active)
    {
        active = false;
        if (!IsValid || !TryResolve(control, out bool left,
                out Switch2StickScrollSector direction))
        {
            return false;
        }

        Switch2StickScrollSector tapMask = left ? LeftTapMask : RightTapMask;
        if ((tapMask & direction) == 0)
        {
            return false;
        }

        Switch2StickScrollSector activeMask = left ? LeftActive : RightActive;
        active = (activeMask & direction) != 0;
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
/// Allocation-free 80 ms pulse gate for mapped stick directions. It owns only
/// Switch2Connect-compatible edge semantics; Mapping remains the sole owner of
/// binding resolution, synthetic output ownership, and virtual-pad delivery.
/// </summary>
internal static class Switch2StickDirectionTapLane
{
    internal const int PulseMilliseconds = 80;

    internal static bool TryAdvance(in Switch2RawInputStatus pro,
        in Switch2JoyConRawInputStatus joyCon, double lx, double ly, double rx,
        double ry, in Switch2StickDirectionActivationModes modes,
        long profileRevision, ref Switch2StickDirectionTapLaneState state,
        out Switch2StickDirectionTapFrame frame)
    {
        frame = default;
        if (!Switch2StickScrollTapLane.AreValidProfileCoordinates(lx, ly, rx, ry) ||
            profileRevision < 0 ||
            !modes.TryGetTapMasks(out Switch2StickScrollSector leftTapMask,
                out Switch2StickScrollSector rightTapMask) ||
            !Switch2StickScrollTapLane.TryGetSource(pro, joyCon,
                out Switch2StickScrollLifetime lifetime,
                out long timestampQpc))
        {
            state = default;
            return false;
        }

        Switch2StickScrollSector leftSector =
            Switch2StickScrollTapLane.ResolveSector(lx, ly);
        Switch2StickScrollSector rightSector =
            Switch2StickScrollTapLane.ResolveSector(rx, ry);
        bool reset = !state.HasBaseline ||
            !state.Lifetime.Equals(lifetime) ||
            state.ProfileRevision != profileRevision ||
            state.LeftTapMask != leftTapMask ||
            state.RightTapMask != rightTapMask ||
            timestampQpc < state.TimestampQpc;
        if (reset)
        {
            state = new Switch2StickDirectionTapLaneState
            {
                Lifetime = lifetime,
                LeftTapMask = leftTapMask,
                RightTapMask = rightTapMask,
                LeftSector = leftSector,
                RightSector = rightSector,
                LeftTriggeredDirections = leftSector,
                RightTriggeredDirections = rightSector,
                TimestampQpc = timestampQpc,
                ProfileRevision = profileRevision,
                HasBaseline = true,
            };
            frame = new Switch2StickDirectionTapFrame(true, leftTapMask,
                rightTapMask, Switch2StickScrollSector.None,
                Switch2StickScrollSector.None);
            return true;
        }

        if (timestampQpc > state.TimestampQpc)
        {
            long pulseTicks = Math.Max(1L, (long)Math.Ceiling(
                lifetime.QpcFrequency * (PulseMilliseconds / 1_000.0)));
            AdvanceStick(leftSector, leftTapMask, timestampQpc, pulseTicks,
                ref state.LeftSector, ref state.LeftTriggeredDirections,
                ref state.LeftExpiry, ref state.RightExpiry,
                ref state.UpExpiry, ref state.DownExpiry);
            AdvanceStick(rightSector, rightTapMask, timestampQpc, pulseTicks,
                ref state.RightSector, ref state.RightTriggeredDirections,
                ref state.RightLeftExpiry, ref state.RightRightExpiry,
                ref state.RightUpExpiry, ref state.RightDownExpiry);
            state.TimestampQpc = timestampQpc;
        }

        Switch2StickScrollSector leftActive = ResolveActive(timestampQpc,
            state.LeftExpiry, state.RightExpiry, state.UpExpiry,
            state.DownExpiry);
        Switch2StickScrollSector rightActive = ResolveActive(timestampQpc,
            state.RightLeftExpiry, state.RightRightExpiry,
            state.RightUpExpiry, state.RightDownExpiry);
        frame = new Switch2StickDirectionTapFrame(true, leftTapMask,
            rightTapMask, leftActive, rightActive);
        return true;
    }

    private static void AdvanceStick(Switch2StickScrollSector nextSector,
        Switch2StickScrollSector tapMask, long timestampQpc, long pulseTicks,
        ref Switch2StickScrollSector currentSector,
        ref Switch2StickScrollSector previouslyTriggered,
        ref long leftExpiry, ref long rightExpiry, ref long upExpiry,
        ref long downExpiry)
    {
        if (nextSector == Switch2StickScrollSector.None)
        {
            currentSector = Switch2StickScrollSector.None;
            previouslyTriggered = Switch2StickScrollSector.None;
            return;
        }
        if (nextSector == currentSector)
        {
            return;
        }

        Switch2StickScrollSector trigger = nextSector;
        if (IsDiagonal(nextSector) && IsCardinal(currentSector) &&
            (trigger & currentSector) != 0)
        {
            trigger &= ~currentSector;
        }
        else if (IsCardinal(nextSector) && IsDiagonal(currentSector) &&
            (previouslyTriggered & nextSector) != 0)
        {
            trigger &= ~nextSector;
        }

        currentSector = nextSector;
        previouslyTriggered = trigger;
        Switch2StickScrollSector tapTrigger = trigger & tapMask;
        long expiry = timestampQpc + pulseTicks;
        if ((tapTrigger & Switch2StickScrollSector.Left) != 0)
        {
            leftExpiry = expiry;
        }
        if ((tapTrigger & Switch2StickScrollSector.Right) != 0)
        {
            rightExpiry = expiry;
        }
        if ((tapTrigger & Switch2StickScrollSector.Up) != 0)
        {
            upExpiry = expiry;
        }
        if ((tapTrigger & Switch2StickScrollSector.Down) != 0)
        {
            downExpiry = expiry;
        }
    }

    private static Switch2StickScrollSector ResolveActive(long timestampQpc,
        long leftExpiry, long rightExpiry, long upExpiry, long downExpiry)
    {
        Switch2StickScrollSector result = Switch2StickScrollSector.None;
        if (leftExpiry > timestampQpc)
        {
            result |= Switch2StickScrollSector.Left;
        }
        if (rightExpiry > timestampQpc)
        {
            result |= Switch2StickScrollSector.Right;
        }
        if (upExpiry > timestampQpc)
        {
            result |= Switch2StickScrollSector.Up;
        }
        if (downExpiry > timestampQpc)
        {
            result |= Switch2StickScrollSector.Down;
        }
        return result;
    }

    private static bool IsCardinal(Switch2StickScrollSector sector) =>
        sector is Switch2StickScrollSector.Left or
            Switch2StickScrollSector.Right or
            Switch2StickScrollSector.Up or Switch2StickScrollSector.Down;

    private static bool IsDiagonal(Switch2StickScrollSector sector) =>
        sector != Switch2StickScrollSector.None && !IsCardinal(sector);
}
