/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The edge-freeze, release-latch, and dampening policy in this file is adapted
from the GPL-3.0 licensed Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py and
src/config.py. It is integrated here as a profile policy over DS4Windows'
existing SixAxis path; it does not own another mapper or output loop.
*/

using System;

namespace DS4Windows.Switch2;

internal readonly struct Switch2IrGyroTuning :
    IEquatable<Switch2IrGyroTuning>
{
    internal Switch2IrGyroTuning(
        Switch2JoyConProfileButton deadzoneButtons, double deadzoneAmount,
        int pauseAfterPressedMilliseconds,
        int pauseAfterReleasedMilliseconds,
        int deadzoneEffectAfterReleasedMilliseconds,
        Switch2JoyConProfileButton dampeningButtons,
        double dampeningAmountPercent,
        int dampeningEffectAfterReleasedMilliseconds)
    {
        DeadzoneButtons = deadzoneButtons;
        DeadzoneAmount = deadzoneAmount;
        PauseAfterPressedMilliseconds = pauseAfterPressedMilliseconds;
        PauseAfterReleasedMilliseconds = pauseAfterReleasedMilliseconds;
        DeadzoneEffectAfterReleasedMilliseconds =
            deadzoneEffectAfterReleasedMilliseconds;
        DampeningButtons = dampeningButtons;
        DampeningAmountPercent = dampeningAmountPercent;
        DampeningEffectAfterReleasedMilliseconds =
            dampeningEffectAfterReleasedMilliseconds;
    }

    internal Switch2JoyConProfileButton DeadzoneButtons { get; }
    internal double DeadzoneAmount { get; }
    internal int PauseAfterPressedMilliseconds { get; }
    internal int PauseAfterReleasedMilliseconds { get; }
    internal int DeadzoneEffectAfterReleasedMilliseconds { get; }
    internal Switch2JoyConProfileButton DampeningButtons { get; }
    internal double DampeningAmountPercent { get; }
    internal int DampeningEffectAfterReleasedMilliseconds { get; }

    internal static Switch2IrGyroTuning Default => new(
        Switch2JoyConProfileButton.None,
        Switch2IrGyroMotionModifier.DefaultDeadzoneAmount,
        Switch2IrGyroMotionModifier.DefaultPauseAfterPressedMilliseconds,
        Switch2IrGyroMotionModifier.DefaultPauseAfterReleasedMilliseconds,
        Switch2IrGyroMotionModifier.
            DefaultDeadzoneEffectAfterReleasedMilliseconds,
        Switch2JoyConProfileButton.None,
        Switch2IrGyroMotionModifier.DefaultDampeningAmountPercent,
        Switch2IrGyroMotionModifier.
            DefaultDampeningEffectAfterReleasedMilliseconds);

    internal static Switch2IrGyroTuning Normalize(
        in Switch2IrGyroTuning value) => new(
            Switch2IrGyroMotionModifier.IsValidButtonMask(
                value.DeadzoneButtons) ? value.DeadzoneButtons :
                Switch2JoyConProfileButton.None,
            Switch2IrGyroMotionModifier.NormalizeDeadzone(
                value.DeadzoneAmount),
            Switch2IrGyroMotionModifier.NormalizeDuration(
                value.PauseAfterPressedMilliseconds,
                Switch2IrGyroMotionModifier.
                    DefaultPauseAfterPressedMilliseconds),
            Switch2IrGyroMotionModifier.NormalizeDuration(
                value.PauseAfterReleasedMilliseconds,
                Switch2IrGyroMotionModifier.
                    DefaultPauseAfterReleasedMilliseconds),
            Switch2IrGyroMotionModifier.NormalizeDuration(
                value.DeadzoneEffectAfterReleasedMilliseconds,
                Switch2IrGyroMotionModifier.
                    DefaultDeadzoneEffectAfterReleasedMilliseconds),
            Switch2IrGyroMotionModifier.IsValidButtonMask(
                value.DampeningButtons) ? value.DampeningButtons :
                Switch2JoyConProfileButton.None,
            Switch2IrGyroMotionModifier.NormalizeDampening(
                value.DampeningAmountPercent),
            Switch2IrGyroMotionModifier.NormalizeDuration(
                value.DampeningEffectAfterReleasedMilliseconds,
                Switch2IrGyroMotionModifier.
                    DefaultDampeningEffectAfterReleasedMilliseconds));

    public bool Equals(Switch2IrGyroTuning other) =>
        DeadzoneButtons == other.DeadzoneButtons &&
        DeadzoneAmount.Equals(other.DeadzoneAmount) &&
        PauseAfterPressedMilliseconds ==
            other.PauseAfterPressedMilliseconds &&
        PauseAfterReleasedMilliseconds ==
            other.PauseAfterReleasedMilliseconds &&
        DeadzoneEffectAfterReleasedMilliseconds ==
            other.DeadzoneEffectAfterReleasedMilliseconds &&
        DampeningButtons == other.DampeningButtons &&
        DampeningAmountPercent.Equals(other.DampeningAmountPercent) &&
        DampeningEffectAfterReleasedMilliseconds ==
            other.DampeningEffectAfterReleasedMilliseconds;

    public override bool Equals(object obj) => obj is Switch2IrGyroTuning
        other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(DeadzoneButtons);
        hash.Add(DeadzoneAmount);
        hash.Add(PauseAfterPressedMilliseconds);
        hash.Add(PauseAfterReleasedMilliseconds);
        hash.Add(DeadzoneEffectAfterReleasedMilliseconds);
        hash.Add(DampeningButtons);
        hash.Add(DampeningAmountPercent);
        hash.Add(DampeningEffectAfterReleasedMilliseconds);
        return hash.ToHashCode();
    }
}

internal readonly struct Switch2IrGyroSideConfiguration :
    IEquatable<Switch2IrGyroSideConfiguration>
{
    internal Switch2IrGyroSideConfiguration(bool triggerEnabled,
        Switch2IrActivationThreshold activationThreshold,
        in Switch2IrGyroTuning tuning) : this(triggerEnabled,
            activationThreshold, tuning.DeadzoneButtons,
            tuning.DeadzoneAmount, tuning.PauseAfterPressedMilliseconds,
            tuning.PauseAfterReleasedMilliseconds,
            tuning.DeadzoneEffectAfterReleasedMilliseconds,
            tuning.DampeningButtons, tuning.DampeningAmountPercent,
            tuning.DampeningEffectAfterReleasedMilliseconds)
    {
    }

    internal Switch2IrGyroSideConfiguration(bool triggerEnabled,
        Switch2IrActivationThreshold activationThreshold,
        Switch2JoyConProfileButton deadzoneButtons, double deadzoneAmount,
        int pauseAfterPressedMilliseconds,
        int pauseAfterReleasedMilliseconds,
        int deadzoneEffectAfterReleasedMilliseconds,
        Switch2JoyConProfileButton dampeningButtons,
        double dampeningAmountPercent,
        int dampeningEffectAfterReleasedMilliseconds)
    {
        TriggerEnabled = triggerEnabled;
        ActivationThreshold = activationThreshold;
        DeadzoneButtons = deadzoneButtons;
        DeadzoneAmount = deadzoneAmount;
        PauseAfterPressedMilliseconds = pauseAfterPressedMilliseconds;
        PauseAfterReleasedMilliseconds = pauseAfterReleasedMilliseconds;
        DeadzoneEffectAfterReleasedMilliseconds =
            deadzoneEffectAfterReleasedMilliseconds;
        DampeningButtons = dampeningButtons;
        DampeningAmountPercent = dampeningAmountPercent;
        DampeningEffectAfterReleasedMilliseconds =
            dampeningEffectAfterReleasedMilliseconds;
    }

    internal bool TriggerEnabled { get; }
    internal Switch2IrActivationThreshold ActivationThreshold { get; }
    internal Switch2JoyConProfileButton DeadzoneButtons { get; }
    internal double DeadzoneAmount { get; }
    internal int PauseAfterPressedMilliseconds { get; }
    internal int PauseAfterReleasedMilliseconds { get; }
    internal int DeadzoneEffectAfterReleasedMilliseconds { get; }
    internal Switch2JoyConProfileButton DampeningButtons { get; }
    internal double DampeningAmountPercent { get; }
    internal int DampeningEffectAfterReleasedMilliseconds { get; }

    internal static Switch2IrGyroSideConfiguration Default(bool enabled,
        Switch2IrActivationThreshold threshold) => new(enabled, threshold,
            Switch2JoyConProfileButton.None,
            Switch2IrGyroMotionModifier.DefaultDeadzoneAmount,
            Switch2IrGyroMotionModifier.DefaultPauseAfterPressedMilliseconds,
            Switch2IrGyroMotionModifier.DefaultPauseAfterReleasedMilliseconds,
            Switch2IrGyroMotionModifier.
                DefaultDeadzoneEffectAfterReleasedMilliseconds,
            Switch2JoyConProfileButton.None,
            Switch2IrGyroMotionModifier.DefaultDampeningAmountPercent,
            Switch2IrGyroMotionModifier.
                DefaultDampeningEffectAfterReleasedMilliseconds);

    public bool Equals(Switch2IrGyroSideConfiguration other) =>
        TriggerEnabled == other.TriggerEnabled &&
        ActivationThreshold == other.ActivationThreshold &&
        DeadzoneButtons == other.DeadzoneButtons &&
        DeadzoneAmount.Equals(other.DeadzoneAmount) &&
        PauseAfterPressedMilliseconds ==
            other.PauseAfterPressedMilliseconds &&
        PauseAfterReleasedMilliseconds ==
            other.PauseAfterReleasedMilliseconds &&
        DeadzoneEffectAfterReleasedMilliseconds ==
            other.DeadzoneEffectAfterReleasedMilliseconds &&
        DampeningButtons == other.DampeningButtons &&
        DampeningAmountPercent.Equals(other.DampeningAmountPercent) &&
        DampeningEffectAfterReleasedMilliseconds ==
            other.DampeningEffectAfterReleasedMilliseconds;

    public override bool Equals(object obj) => obj is
        Switch2IrGyroSideConfiguration other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(TriggerEnabled);
        hash.Add(ActivationThreshold);
        hash.Add(DeadzoneButtons);
        hash.Add(DeadzoneAmount);
        hash.Add(PauseAfterPressedMilliseconds);
        hash.Add(PauseAfterReleasedMilliseconds);
        hash.Add(DeadzoneEffectAfterReleasedMilliseconds);
        hash.Add(DampeningButtons);
        hash.Add(DampeningAmountPercent);
        hash.Add(DampeningEffectAfterReleasedMilliseconds);
        return hash.ToHashCode();
    }
}

internal readonly struct Switch2IrGyroConfiguration :
    IEquatable<Switch2IrGyroConfiguration>
{
    internal Switch2IrGyroConfiguration(
        in Switch2IrGyroSideConfiguration left,
        in Switch2IrGyroSideConfiguration right, long profileRevision)
    {
        Left = left;
        Right = right;
        ProfileRevision = profileRevision;
    }

    internal Switch2IrGyroSideConfiguration Left { get; }
    internal Switch2IrGyroSideConfiguration Right { get; }
    internal long ProfileRevision { get; }
    internal bool Enabled => Left.TriggerEnabled || Right.TriggerEnabled;

    internal static Switch2IrGyroConfiguration Disabled => new(
        Switch2IrGyroSideConfiguration.Default(false,
            Switch2IrActivationThreshold.Strict),
        Switch2IrGyroSideConfiguration.Default(false,
            Switch2IrActivationThreshold.Strict), 0);

    public bool Equals(Switch2IrGyroConfiguration other) =>
        Left.Equals(other.Left) && Right.Equals(other.Right) &&
        ProfileRevision == other.ProfileRevision;

    public override bool Equals(object obj) => obj is
        Switch2IrGyroConfiguration other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Left, Right,
        ProfileRevision);
}

internal readonly struct Switch2IrGyroMotionModifierResult
{
    internal Switch2IrGyroMotionModifierResult(bool active,
        Switch2JoyConSide sourceSide, bool freeze, bool deadzoneActive,
        double deadzoneAmount, bool dampeningActive,
        double dampeningMultiplier)
    {
        Active = active;
        SourceSide = sourceSide;
        Freeze = freeze;
        DeadzoneActive = deadzoneActive;
        DeadzoneAmount = deadzoneAmount;
        DampeningActive = dampeningActive;
        DampeningMultiplier = dampeningMultiplier;
    }

    internal bool Active { get; }
    internal Switch2JoyConSide SourceSide { get; }
    internal bool Freeze { get; }
    internal bool DeadzoneActive { get; }
    internal double DeadzoneAmount { get; }
    internal bool DampeningActive { get; }
    internal double DampeningMultiplier { get; }
}

internal struct Switch2IrGyroMotionModifierState
{
    internal bool HasConfiguration;
    internal Switch2IrGyroConfiguration Configuration;
    internal ulong PairEpoch;
    internal ulong LeftDeviceGeneration;
    internal ulong LeftTransportGeneration;
    internal ulong RightDeviceGeneration;
    internal ulong RightTransportGeneration;
    internal long LastTimestampQpc;
    internal bool HasTimestamp;
    internal bool PreviousLeftIrActive;
    internal bool PreviousRightIrActive;
    internal Switch2JoyConSide SelectedSide;
    internal bool HasButtonBaseline;
    internal Switch2JoyConProfileButton PreviousDeadzoneButtons;
    internal Switch2JoyConProfileButton PreviousDampeningButtons;
    internal long FreezeUntilMicroseconds;
    internal long DeadzoneLatchUntilMicroseconds;
    internal long DampeningLatchUntilMicroseconds;
}

/// <summary>
/// Allocation-free report-time modifier for the existing Joy-Con SixAxis
/// projection. An optical sensor becomes eligible only when that exact source
/// is present in the active legacy gyro-trigger list. Button edges are observed
/// across both halves of a joined pair, matching Switch2Connect's merged-pair
/// behavior while retaining DS4Windows' one canonical mapping pipeline.
/// </summary>
internal static class Switch2IrGyroMotionModifier
{
    // These append the legacy trigger list. Never insert before them because
    // profiles persist these numeric tokens.
    internal const int LeftIrGyroTriggerIndex = 27;
    internal const int RightIrGyroTriggerIndex = 28;

    internal const double DefaultDeadzoneAmount = 15.0;
    internal const int DefaultPauseAfterPressedMilliseconds = 100;
    internal const int DefaultPauseAfterReleasedMilliseconds = 100;
    internal const int DefaultDeadzoneEffectAfterReleasedMilliseconds = 200;
    internal const double DefaultDampeningAmountPercent = 90.0;
    internal const int DefaultDampeningEffectAfterReleasedMilliseconds = 200;
    internal const int MaximumDurationMilliseconds = 60_000;

    internal static readonly Switch2JoyConProfileButton KnownButtons =
        Switch2JoyConProfileButton.RightRailSR |
        (Switch2JoyConProfileButton)((uint)
            Switch2JoyConProfileButton.RightRailSR - 1u);

    internal static bool TryAdvance(in Switch2JoyConProfileInputFrame frame,
        in Switch2IrGyroConfiguration configuration,
        ref Switch2IrGyroMotionModifierState state,
        out Switch2IrGyroMotionModifierResult result)
    {
        result = default;
        if (configuration.ProfileRevision < 0 || frame.Version !=
                Switch2JoyConProfileInputFrame.CurrentVersion ||
            frame.CompletionTimestampQpc < 0 || frame.QpcFrequency <= 0 ||
            !ControllerFeedbackClock.TryConvertQpcTicks(
                (ulong)frame.CompletionTimestampQpc,
                (ulong)frame.QpcFrequency, out ulong timestampMicroseconds) ||
            timestampMicroseconds > long.MaxValue)
        {
            state = default;
            return false;
        }

        if (!configuration.Enabled)
        {
            state = default;
            return true;
        }

        if (!TryValidate(configuration))
        {
            state = default;
            return false;
        }

        bool configurationChanged = !state.HasConfiguration ||
            !state.Configuration.Equals(configuration);
        bool lifecycleChanged = state.HasConfiguration && (
            state.PairEpoch != frame.PairEpoch ||
            state.LeftDeviceGeneration !=
                frame.LeftSource.DeviceGeneration ||
            state.LeftTransportGeneration !=
                frame.LeftSource.TransportGeneration ||
            state.RightDeviceGeneration !=
                frame.RightSource.DeviceGeneration ||
            state.RightTransportGeneration !=
                frame.RightSource.TransportGeneration);
        bool timestampRegressed = state.HasTimestamp &&
            frame.CompletionTimestampQpc < state.LastTimestampQpc;
        if (configurationChanged || lifecycleChanged || timestampRegressed)
        {
            state = default;
        }

        state.HasConfiguration = true;
        state.Configuration = configuration;
        state.PairEpoch = frame.PairEpoch;
        state.LeftDeviceGeneration = frame.LeftSource.DeviceGeneration;
        state.LeftTransportGeneration = frame.LeftSource.TransportGeneration;
        state.RightDeviceGeneration = frame.RightSource.DeviceGeneration;
        state.RightTransportGeneration =
            frame.RightSource.TransportGeneration;
        state.LastTimestampQpc = frame.CompletionTimestampQpc;
        state.HasTimestamp = true;

        bool leftIrActive = IsIrActive(frame.LeftSource,
            configuration.Left);
        bool rightIrActive = IsIrActive(frame.RightSource,
            configuration.Right);
        Switch2JoyConSide selected = SelectSide(leftIrActive, rightIrActive,
            state.PreviousLeftIrActive, state.PreviousRightIrActive,
            state.SelectedSide);
        state.PreviousLeftIrActive = leftIrActive;
        state.PreviousRightIrActive = rightIrActive;

        if (selected == Switch2JoyConSide.Invalid)
        {
            ResetActiveWindows(ref state);
            result = default;
            return true;
        }

        if (state.SelectedSide != selected)
        {
            ResetActiveWindows(ref state);
            state.SelectedSide = selected;
        }

        Switch2IrGyroSideConfiguration tuning = selected ==
            Switch2JoyConSide.Left ? configuration.Left : configuration.Right;
        Switch2JoyConProfileButton allButtons =
            frame.LeftSource.Buttons | frame.RightSource.Buttons;
        Switch2JoyConProfileButton deadzonePressed = allButtons &
            tuning.DeadzoneButtons;
        Switch2JoyConProfileButton dampeningPressed = allButtons &
            tuning.DampeningButtons;
        long now = (long)timestampMicroseconds;
        bool previousDeadzonePressed = state.HasButtonBaseline &&
            state.PreviousDeadzoneButtons !=
                Switch2JoyConProfileButton.None;
        bool previousDampeningPressed = state.HasButtonBaseline &&
            state.PreviousDampeningButtons !=
                Switch2JoyConProfileButton.None;

        if (!state.HasButtonBaseline)
        {
            state.PreviousDeadzoneButtons = deadzonePressed;
            state.PreviousDampeningButtons = dampeningPressed;
            state.HasButtonBaseline = true;
            previousDeadzonePressed = deadzonePressed !=
                Switch2JoyConProfileButton.None;
            previousDampeningPressed = dampeningPressed !=
                Switch2JoyConProfileButton.None;
        }
        else
        {
            Switch2JoyConProfileButton newlyPressed = deadzonePressed &
                ~state.PreviousDeadzoneButtons;
            Switch2JoyConProfileButton newlyReleased =
                state.PreviousDeadzoneButtons & ~deadzonePressed;
            if (newlyPressed != Switch2JoyConProfileButton.None)
            {
                state.FreezeUntilMicroseconds = AddDuration(now,
                    tuning.PauseAfterPressedMilliseconds);
            }
            else if (newlyReleased != Switch2JoyConProfileButton.None)
            {
                state.FreezeUntilMicroseconds = AddDuration(now,
                    tuning.PauseAfterReleasedMilliseconds);
            }
            state.PreviousDeadzoneButtons = deadzonePressed;
            state.PreviousDampeningButtons = dampeningPressed;
        }

        bool deadzoneActive = AdvanceLatch(deadzonePressed !=
                Switch2JoyConProfileButton.None, previousDeadzonePressed, now,
            tuning.DeadzoneEffectAfterReleasedMilliseconds,
            ref state.DeadzoneLatchUntilMicroseconds);
        bool dampeningActive = AdvanceLatch(dampeningPressed !=
                Switch2JoyConProfileButton.None, previousDampeningPressed, now,
            tuning.DampeningEffectAfterReleasedMilliseconds,
            ref state.DampeningLatchUntilMicroseconds);
        bool freeze = now < state.FreezeUntilMicroseconds;
        double multiplier = dampeningActive ?
            (100.0 - tuning.DampeningAmountPercent) / 100.0 : 1.0;
        result = new Switch2IrGyroMotionModifierResult(true, selected,
            freeze, deadzoneActive, deadzoneActive ?
                tuning.DeadzoneAmount : 0.0, dampeningActive, multiplier);
        return true;
    }

    internal static bool ContainsSerializedTrigger(string triggers,
        int target)
    {
        if (string.IsNullOrWhiteSpace(triggers))
        {
            return false;
        }

        ReadOnlySpan<char> span = triggers.AsSpan();
        int start = 0;
        while (start <= span.Length)
        {
            int relativeComma = span[start..].IndexOf(',');
            int length = relativeComma < 0 ? span.Length - start :
                relativeComma;
            ReadOnlySpan<char> token = span.Slice(start, length).Trim();
            if (int.TryParse(token, out int parsed) && parsed == target)
            {
                return true;
            }
            if (relativeComma < 0)
            {
                break;
            }
            start += relativeComma + 1;
        }
        return false;
    }

    internal static bool IsValidButtonMask(
        Switch2JoyConProfileButton buttons) =>
        (buttons & ~KnownButtons) == 0;

    internal static double NormalizeDeadzone(double value) =>
        double.IsFinite(value) && value >= Switch2MotionSoftDeadzone.Minimum &&
        value <= Switch2MotionSoftDeadzone.Maximum ? value :
            DefaultDeadzoneAmount;

    internal static double NormalizeDampening(double value) =>
        double.IsFinite(value) && value >= 0.0 && value <= 100.0 ? value :
            DefaultDampeningAmountPercent;

    internal static int NormalizeDuration(int value, int fallback) =>
        value >= 0 && value <= MaximumDurationMilliseconds ? value :
            fallback;

    private static bool TryValidate(
        in Switch2IrGyroConfiguration configuration) =>
        configuration.ProfileRevision >= 0 &&
        TryValidate(configuration.Left) && TryValidate(configuration.Right);

    private static bool TryValidate(
        in Switch2IrGyroSideConfiguration configuration) =>
        !configuration.TriggerEnabled ||
        (configuration.ActivationThreshold is
             Switch2IrActivationThreshold.Strict or
             Switch2IrActivationThreshold.Balanced or
             Switch2IrActivationThreshold.Relaxed &&
         IsValidButtonMask(configuration.DeadzoneButtons) &&
         IsValidButtonMask(configuration.DampeningButtons) &&
         double.IsFinite(configuration.DeadzoneAmount) &&
         configuration.DeadzoneAmount >= Switch2MotionSoftDeadzone.Minimum &&
         configuration.DeadzoneAmount <= Switch2MotionSoftDeadzone.Maximum &&
         double.IsFinite(configuration.DampeningAmountPercent) &&
         configuration.DampeningAmountPercent >= 0.0 &&
         configuration.DampeningAmountPercent <= 100.0 &&
         IsValidDuration(configuration.PauseAfterPressedMilliseconds) &&
         IsValidDuration(configuration.PauseAfterReleasedMilliseconds) &&
         IsValidDuration(
             configuration.DeadzoneEffectAfterReleasedMilliseconds) &&
         IsValidDuration(
             configuration.DampeningEffectAfterReleasedMilliseconds));

    private static bool IsValidDuration(int value) => value >= 0 &&
        value <= MaximumDurationMilliseconds;

    private static bool IsIrActive(in Switch2JoyConProfileSide side,
        in Switch2IrGyroSideConfiguration configuration) =>
        configuration.TriggerEnabled && side.IsPresent &&
        side.HasCommonMotion && Switch2IrMouseProjection.IsThresholdActive(
            configuration.ActivationThreshold, side.IrRoughness,
            side.IrDistance);

    private static Switch2JoyConSide SelectSide(bool leftActive,
        bool rightActive, bool previousLeftActive, bool previousRightActive,
        Switch2JoyConSide previousSelection)
    {
        if (!leftActive && !rightActive)
        {
            return Switch2JoyConSide.Invalid;
        }
        if (leftActive && !rightActive)
        {
            return Switch2JoyConSide.Left;
        }
        if (rightActive && !leftActive)
        {
            return Switch2JoyConSide.Right;
        }

        bool newLeft = !previousLeftActive;
        bool newRight = !previousRightActive;
        if (newLeft != newRight)
        {
            return newLeft ? Switch2JoyConSide.Left : Switch2JoyConSide.Right;
        }
        if (previousSelection is Switch2JoyConSide.Left or
            Switch2JoyConSide.Right)
        {
            return previousSelection;
        }
        // Joined Auto and ambiguous simultaneous activation follow the
        // existing profile mouse policy: the right optical sensor wins.
        return Switch2JoyConSide.Right;
    }

    private static bool AdvanceLatch(bool pressed, bool previouslyPressed,
        long now,
        int releaseMilliseconds, ref long latchUntil)
    {
        if (pressed)
        {
            return true;
        }
        if (previouslyPressed)
        {
            latchUntil = AddDuration(now, releaseMilliseconds);
        }
        return now < latchUntil;
    }

    private static long AddDuration(long now, int milliseconds)
    {
        long microseconds = milliseconds * 1_000L;
        return now > long.MaxValue - microseconds ? long.MaxValue :
            now + microseconds;
    }

    private static void ResetActiveWindows(
        ref Switch2IrGyroMotionModifierState state)
    {
        state.SelectedSide = Switch2JoyConSide.Invalid;
        state.HasButtonBaseline = false;
        state.PreviousDeadzoneButtons = Switch2JoyConProfileButton.None;
        state.PreviousDampeningButtons = Switch2JoyConProfileButton.None;
        state.FreezeUntilMicroseconds = 0;
        state.DeadzoneLatchUntilMicroseconds = 0;
        state.DampeningLatchUntilMicroseconds = 0;
    }
}
