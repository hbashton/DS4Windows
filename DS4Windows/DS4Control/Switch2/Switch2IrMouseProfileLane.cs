/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The IR threshold and velocity policy consumed here is adapted from the
GPL-3.0 licensed Switch2Connect project, commit
4487322a306f04efa27682e3f3a508635a84fd98. This file integrates that policy
with DS4Windows' existing profile mouse path and lifecycle generations.
*/

using System;

namespace DS4Windows.Switch2;

internal struct Switch2IrMouseProfileLaneState
{
    internal Switch2IrMouseProfileSideLaneState Left;
    internal Switch2IrMouseProfileSideLaneState Right;
    internal Switch2IrMouseSource Source;
    internal Switch2IrMouseScrollMode ScrollMode;
    internal long ProfileRevision;
    internal bool HasConfiguration;

    internal readonly bool HasLifecycle =>
        Left.HasLifecycle || Right.HasLifecycle;
}

internal struct Switch2IrMouseProfileSideLaneState
{
    internal Switch2IrMouseProjectionState Projection;
    internal ulong PairEpoch;
    internal ulong DeviceGeneration;
    internal ulong TransportGeneration;
    internal long LastTimestampQpc;
    internal Switch2IrActivationThreshold Threshold;
    internal bool HasLifecycle;
    internal bool HasTimestamp;
    internal bool HasThreshold;
}

/// <summary>
/// Profile-scoped bridge from the fixed-size Joy-Con 2 source sidecar to the
/// existing DS4Windows mouse accumulator. It performs no OS injection and
/// allocates nothing on the report path. Pair, device, transport, timestamp,
/// and configuration fences prevent state from crossing a reconnect, re-pair,
/// clock regression, or side-selection change.
/// </summary>
internal static class Switch2IrMouseProfileLane
{
    // Switch2Connect controller.py Controller.simulate_mouse uses the active
    // Joy-Con's physical Y axis with a 0.2 deadzone and a 60-unit scale.
    // Keep these fixed because its saved configuration does not expose or
    // persist a separate scroll sensitivity at the pinned source revision.
    internal const double StickScrollDeadzone = 0.2;
    internal const int StickScrollScale = 60;

    internal static bool TryAdvance(
        in Switch2JoyConRawInputStatus input, bool enabled,
        Switch2IrMouseSource source,
        Switch2IrActivationThreshold leftThreshold, double leftSensitivity,
        Switch2IrActivationThreshold rightThreshold, double rightSensitivity,
        Switch2IrMouseScrollMode scrollMode,
        long profileRevision,
        ref Switch2IrMouseProfileLaneState state,
        out Switch2IrMouseProjectionResult result)
    {
        result = default;
        if (!enabled || !IsValidSource(source) ||
            !IsValidScrollMode(scrollMode) || !input.IsValid ||
            input.ContractVersion != Switch2JoyConProfileInputFrame.CurrentVersion ||
            profileRevision < 0 || input.CompletionTimestampQpc < 0 ||
            input.QpcFrequency <= 0)
        {
            state = default;
            return false;
        }

        if (!ControllerFeedbackClock.TryConvertQpcTicks(
                (ulong)input.CompletionTimestampQpc,
                (ulong)input.QpcFrequency, out ulong timestampMicroseconds) ||
            timestampMicroseconds > long.MaxValue)
        {
            state = default;
            return false;
        }

        bool configurationChanged = !state.HasConfiguration ||
            state.Source != source ||
            state.ScrollMode != scrollMode ||
            state.ProfileRevision != profileRevision;
        if (configurationChanged)
        {
            state = default;
        }

        state.Source = source;
        state.ScrollMode = scrollMode;
        state.ProfileRevision = profileRevision;
        state.HasConfiguration = true;

        Switch2IrMouseSource selected = SelectSource(input, source);
        if (selected == Switch2IrMouseSource.Auto)
        {
            state = default;
            return false;
        }

        bool useLeft = selected is Switch2IrMouseSource.Left or
            Switch2IrMouseSource.Both;
        bool useRight = selected is Switch2IrMouseSource.Right or
            Switch2IrMouseSource.Both;
        if (!useLeft)
        {
            state.Left = default;
        }
        if (!useRight)
        {
            state.Right = default;
        }

        Switch2IrMouseProjectionResult leftResult = default;
        Switch2IrMouseProjectionResult rightResult = default;
        bool leftAdvanced = useLeft && TryAdvanceSide(input, isLeft: true,
            leftThreshold, leftSensitivity, scrollMode,
            (long)timestampMicroseconds,
            ref state.Left, out leftResult);
        bool rightAdvanced = useRight && TryAdvanceSide(input, isLeft: false,
            rightThreshold, rightSensitivity, scrollMode,
            (long)timestampMicroseconds,
            ref state.Right, out rightResult);
        if (!leftAdvanced && !rightAdvanced)
        {
            result = default;
            return false;
        }

        result = leftAdvanced && rightAdvanced ?
            Combine(leftResult, rightResult) :
            leftAdvanced ? leftResult : rightResult;
        return true;
    }

    private static Switch2IrMouseSource SelectSource(
        in Switch2JoyConRawInputStatus input, Switch2IrMouseSource source)
    {
        if (source != Switch2IrMouseSource.Auto)
        {
            return source;
        }

        // A joined pair defaults to the right optical sensor. Standalone
        // Joy-Con 2 controllers naturally select their only present side.
        if (input.RightPresent && input.RightHasCommonMotion)
        {
            return Switch2IrMouseSource.Right;
        }

        return input.LeftPresent && input.LeftHasCommonMotion ?
            Switch2IrMouseSource.Left : Switch2IrMouseSource.Auto;
    }

    private static bool TryAdvanceSide(
        in Switch2JoyConRawInputStatus input, bool isLeft,
        Switch2IrActivationThreshold threshold, double sensitivity,
        Switch2IrMouseScrollMode scrollMode,
        long timestampMicroseconds,
        ref Switch2IrMouseProfileSideLaneState state,
        out Switch2IrMouseProjectionResult result)
    {
        if (!IsValidThreshold(threshold) ||
            !double.IsFinite(sensitivity) || sensitivity <
                Switch2IrMouseProjection.MinimumProfileSensitivity ||
            sensitivity >
                Switch2IrMouseProjection.MaximumProfileSensitivity)
        {
            state = default;
            result = default;
            return false;
        }

        bool present = isLeft ? input.LeftPresent : input.RightPresent;
        bool common = isLeft ? input.LeftHasCommonMotion :
            input.RightHasCommonMotion;
        ulong deviceGeneration = isLeft ? input.LeftDeviceGeneration :
            input.RightDeviceGeneration;
        ulong transportGeneration = isLeft ?
            input.LeftTransportGeneration : input.RightTransportGeneration;
        if (!present || !common || deviceGeneration == 0 ||
            transportGeneration == 0)
        {
            state = default;
            result = default;
            return false;
        }

        bool lifecycleChanged = !state.HasLifecycle ||
            state.PairEpoch != input.PairEpoch ||
            state.DeviceGeneration != deviceGeneration ||
            state.TransportGeneration != transportGeneration;
        bool timestampRegressed = state.HasTimestamp &&
            input.CompletionTimestampQpc < state.LastTimestampQpc;
        bool thresholdChanged = !state.HasThreshold ||
            state.Threshold != threshold;
        if (lifecycleChanged || timestampRegressed || thresholdChanged)
        {
            state = default;
        }

        state.PairEpoch = input.PairEpoch;
        state.DeviceGeneration = deviceGeneration;
        state.TransportGeneration = transportGeneration;
        state.LastTimestampQpc = input.CompletionTimestampQpc;
        state.Threshold = threshold;
        state.HasLifecycle = true;
        state.HasTimestamp = true;
        state.HasThreshold = true;

        ushort x = isLeft ? input.LeftIrX : input.RightIrX;
        ushort y = isLeft ? input.LeftIrY : input.RightIrY;
        ushort roughness = isLeft ? input.LeftIrRoughness :
            input.RightIrRoughness;
        ushort distance = isLeft ? input.LeftIrDistance :
            input.RightIrDistance;
        if (!Switch2IrMouseProjection.TryAdvance(true, threshold, x, y,
            roughness, distance, timestampMicroseconds, sensitivity,
            ref state.Projection, out result))
        {
            return false;
        }

        if (result.ModeActive)
        {
            CalculateStickScroll(input, isLeft, scrollMode,
                out int wheelDelta, out int horizontalWheelDelta);
            if (wheelDelta != 0 || horizontalWheelDelta != 0)
            {
                result = new Switch2IrMouseProjectionResult(
                    result.ThresholdActive, result.ModeActive,
                    result.DeltaX, result.DeltaY, result.VelocityX,
                    result.VelocityY, wheelDelta, horizontalWheelDelta,
                    result.HasVerificationEvent, result.VerificationEvent);
            }
        }

        return true;
    }

    private static Switch2IrMouseProjectionResult Combine(
        in Switch2IrMouseProjectionResult left,
        in Switch2IrMouseProjectionResult right)
    {
        bool rightHasVerification = right.HasVerificationEvent;
        return new Switch2IrMouseProjectionResult(
            left.ThresholdActive || right.ThresholdActive,
            left.ModeActive || right.ModeActive,
            left.DeltaX + right.DeltaX,
            left.DeltaY + right.DeltaY,
            left.VelocityX + right.VelocityX,
            left.VelocityY + right.VelocityY,
            left.WheelDelta + right.WheelDelta,
            left.HorizontalWheelDelta + right.HorizontalWheelDelta,
            left.HasVerificationEvent || rightHasVerification,
            rightHasVerification ? right.VerificationEvent :
                left.VerificationEvent);
    }

    private static void CalculateStickScroll(
        in Switch2JoyConRawInputStatus input, bool isLeft,
        Switch2IrMouseScrollMode scrollMode,
        out int vertical, out int horizontal)
    {
        short physicalY = input.Mode switch
        {
            Switch2JoyConProfileMode.Joined when isLeft =>
                NegateSigned(input.LogicalLeftStickY),
            Switch2JoyConProfileMode.Joined =>
                NegateSigned(input.LogicalRightStickY),
            Switch2JoyConProfileMode.StandaloneVerticalLeft =>
                NegateSigned(input.LogicalLeftStickY),
            Switch2JoyConProfileMode.StandaloneVerticalRight =>
                NegateSigned(input.LogicalRightStickY),
            Switch2JoyConProfileMode.StandaloneHorizontalLeft =>
                input.LogicalLeftStickX,
            Switch2JoyConProfileMode.StandaloneHorizontalRight =>
                NegateSigned(input.LogicalLeftStickX),
            _ => 0,
        };

        short physicalX = input.Mode switch
        {
            Switch2JoyConProfileMode.Joined when isLeft =>
                input.LogicalLeftStickX,
            Switch2JoyConProfileMode.Joined => input.LogicalRightStickX,
            Switch2JoyConProfileMode.StandaloneVerticalLeft =>
                input.LogicalLeftStickX,
            Switch2JoyConProfileMode.StandaloneVerticalRight =>
                input.LogicalRightStickX,
            // Standalone horizontal projection rotates the physical stick:
            // mini-left logical Y=-physical X; mini-right logical Y=physical X.
            Switch2JoyConProfileMode.StandaloneHorizontalLeft =>
                NegateSigned(input.LogicalLeftStickY),
            Switch2JoyConProfileMode.StandaloneHorizontalRight =>
                input.LogicalLeftStickY,
            _ => 0,
        };

        vertical = ScaleStickScroll(physicalY);
        horizontal = scrollMode == Switch2IrMouseScrollMode.FourWay ?
            ScaleStickScroll(physicalX) : 0;
    }

    private static int ScaleStickScroll(short value)
    {
        double normalized = value < 0 ?
            value / 32768.0 : value / 32767.0;
        return Math.Abs(normalized) > StickScrollDeadzone ?
            (int)(normalized * StickScrollScale) : 0;
    }

    private static short NegateSigned(short value) => value == short.MinValue ?
        short.MaxValue : (short)-value;

    private static bool IsValidSource(Switch2IrMouseSource source) =>
        source is Switch2IrMouseSource.Auto or Switch2IrMouseSource.Left or
            Switch2IrMouseSource.Right or Switch2IrMouseSource.Both;

    private static bool IsValidScrollMode(Switch2IrMouseScrollMode mode) =>
        mode is Switch2IrMouseScrollMode.Vertical or
            Switch2IrMouseScrollMode.FourWay;

    private static bool IsValidThreshold(
        Switch2IrActivationThreshold threshold) => threshold is
            Switch2IrActivationThreshold.Strict or
            Switch2IrActivationThreshold.Balanced or
            Switch2IrActivationThreshold.Relaxed;
}
