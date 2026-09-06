/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The threshold bands and velocity scale in this file are adapted from the
GPL-3.0 licensed Switch2Connect project, commit
4487322a306f04efa27682e3f3a508635a84fd98, src/controller.py
Controller.simulate_mouse. The temporal activation gate is owned separately.
*/

using System;

namespace DS4Windows.Switch2;

public enum Switch2IrMouseSource : byte
{
    Auto = 0,
    Left = 1,
    Right = 2,
    Both = 3,
}

public enum Switch2IrActivationThreshold : byte
{
    Strict = 1,
    Balanced = 2,
    Relaxed = 3,
}

public enum Switch2IrMouseScrollMode : byte
{
    Vertical = 0,
    FourWay = 1,
}

internal readonly struct Switch2IrMouseProjectionResult
{
    internal Switch2IrMouseProjectionResult(bool thresholdActive,
        bool modeActive, int deltaX, int deltaY, double velocityX,
        double velocityY, int wheelDelta, int horizontalWheelDelta,
        bool hasVerificationEvent,
        in Switch2IrVerificationEvent verificationEvent)
    {
        ThresholdActive = thresholdActive;
        ModeActive = modeActive;
        DeltaX = deltaX;
        DeltaY = deltaY;
        VelocityX = velocityX;
        VelocityY = velocityY;
        WheelDelta = wheelDelta;
        HorizontalWheelDelta = horizontalWheelDelta;
        HasVerificationEvent = hasVerificationEvent;
        VerificationEvent = verificationEvent;
    }

    internal bool ThresholdActive { get; }

    internal bool ModeActive { get; }

    internal int DeltaX { get; }

    internal int DeltaY { get; }

    internal double VelocityX { get; }

    internal double VelocityY { get; }

    /// <summary>
    /// Vertical wheel movement in Win32 <c>WHEEL_DELTA</c> units. The optical
    /// projection itself always publishes zero; the profile lane adds the
    /// selected physical Joy-Con stick only while this result is active.
    /// </summary>
    internal int WheelDelta { get; }

    /// <summary>
    /// Horizontal wheel movement in Win32 <c>WHEEL_DELTA</c> units. Positive
    /// values scroll right and negative values scroll left.
    /// </summary>
    internal int HorizontalWheelDelta { get; }

    internal bool HasVerificationEvent { get; }

    internal Switch2IrVerificationEvent VerificationEvent { get; }
}

internal struct Switch2IrMouseProjectionState
{
    internal Switch2IrMouseActivationState Activation;
}

/// <summary>
/// Allocation-free projection from the Joy-Con 2 optical counters into a
/// relative mouse velocity. Coordinates are accumulating unsigned 16-bit
/// counters, so subtraction is explicitly wrapping. This owner performs no OS
/// injection and owns no output thread; a profile-scoped consumer must decide
/// whether and where to publish the result.
/// </summary>
internal static class Switch2IrMouseProjection
{
    internal const double DefaultSensitivity = 4.0;
    internal const double VelocityScale = 0.009;
    internal const double MinimumProfileSensitivity = 1.0;
    internal const double MaximumProfileSensitivity = 10.0;
    internal const double MaximumSensitivity = 100.0;

    internal static bool TryAdvance(bool enabled,
        Switch2IrActivationThreshold threshold, ushort x, ushort y,
        ushort roughness, ushort distance, long nowMicroseconds,
        double sensitivity, ref Switch2IrMouseProjectionState state,
        out Switch2IrMouseProjectionResult result)
    {
        result = default;
        if (!IsValidThreshold(threshold) || nowMicroseconds < 0 ||
            !double.IsFinite(sensitivity) || sensitivity < 0.0 ||
            sensitivity > MaximumSensitivity)
        {
            state = default;
            return false;
        }

        bool thresholdActive = enabled && IsThresholdActive(threshold,
            roughness, distance);
        Switch2IrMouseActivationState previous = state.Activation;
        var coordinates = new Switch2IrCoordinate(x, y);
        Switch2IrMouseActivationGate.Advance(thresholdActive, coordinates,
            nowMicroseconds, ref state.Activation,
            out bool hasActivationOrigin,
            out Switch2IrCoordinate activationOrigin,
            out bool hasVerificationEvent,
            out Switch2IrVerificationEvent verificationEvent);

        int deltaX = 0;
        int deltaY = 0;
        if (state.Activation.ModeActive)
        {
            if (hasActivationOrigin)
            {
                deltaX = Switch2IrMouseActivationGate.LoopingDifference16Bit(
                    activationOrigin.X, x);
                deltaY = Switch2IrMouseActivationGate.LoopingDifference16Bit(
                    activationOrigin.Y, y);
            }
            else if (previous.HasPreviousCoordinates)
            {
                deltaX = Switch2IrMouseActivationGate.LoopingDifference16Bit(
                    previous.PreviousCoordinates.X, x);
                deltaY = Switch2IrMouseActivationGate.LoopingDifference16Bit(
                    previous.PreviousCoordinates.Y, y);
            }
        }

        double scale = sensitivity * VelocityScale;
        result = new Switch2IrMouseProjectionResult(thresholdActive,
            state.Activation.ModeActive, deltaX, deltaY, deltaX * scale,
            deltaY * scale, wheelDelta: 0, horizontalWheelDelta: 0,
            hasVerificationEvent,
            verificationEvent);
        return true;
    }

    internal static bool IsThresholdActive(
        Switch2IrActivationThreshold threshold, ushort roughness,
        ushort distance)
    {
        if (distance == 0)
        {
            return false;
        }

        return threshold switch
        {
            Switch2IrActivationThreshold.Strict =>
                distance < 1_000 && roughness < 4_000,
            Switch2IrActivationThreshold.Balanced =>
                distance < 1_500 && roughness < 5_000,
            Switch2IrActivationThreshold.Relaxed =>
                distance < 3_000 && roughness < 10_000,
            _ => false,
        };
    }

    private static bool IsValidThreshold(
        Switch2IrActivationThreshold threshold) => threshold is
            Switch2IrActivationThreshold.Strict or
            Switch2IrActivationThreshold.Balanced or
            Switch2IrActivationThreshold.Relaxed;
}
