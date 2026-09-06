/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The press/release freeze, release latch, subtractive deadzone, and dampening
policy is adapted from the GPL-3.0 Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py. It presents a
modifier to DS4Windows' existing gyro outputs and does not own a mapper.
*/

using System;

namespace DS4Windows.Switch2;

internal readonly struct Switch2GyroTriggerSourceIdentity :
    IEquatable<Switch2GyroTriggerSourceIdentity>
{
    internal Switch2GyroTriggerSourceIdentity(bool joyCon, ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration,
        Switch2JoyConProfileMode joyConMode = Switch2JoyConProfileMode.Invalid)
    {
        JoyCon = joyCon;
        PairEpoch = pairEpoch;
        LeftDeviceGeneration = leftDeviceGeneration;
        LeftTransportGeneration = leftTransportGeneration;
        RightDeviceGeneration = rightDeviceGeneration;
        RightTransportGeneration = rightTransportGeneration;
        JoyConMode = joyCon ? joyConMode : Switch2JoyConProfileMode.Invalid;
    }

    internal bool JoyCon { get; }
    internal ulong PairEpoch { get; }
    internal ulong LeftDeviceGeneration { get; }
    internal ulong LeftTransportGeneration { get; }
    internal ulong RightDeviceGeneration { get; }
    internal ulong RightTransportGeneration { get; }
    // In-process mapping identity only: an orientation change must baseline
    // edge-driven modifiers without reconnecting or resetting the transport.
    internal Switch2JoyConProfileMode JoyConMode { get; }

    internal bool HasSamePhysicalSource(Switch2GyroTriggerSourceIdentity other) =>
        JoyCon == other.JoyCon && PairEpoch == other.PairEpoch &&
        LeftDeviceGeneration == other.LeftDeviceGeneration &&
        LeftTransportGeneration == other.LeftTransportGeneration &&
        RightDeviceGeneration == other.RightDeviceGeneration &&
        RightTransportGeneration == other.RightTransportGeneration;

    public bool Equals(Switch2GyroTriggerSourceIdentity other) =>
        HasSamePhysicalSource(other) && JoyConMode == other.JoyConMode;

    public override bool Equals(object obj) => obj is
        Switch2GyroTriggerSourceIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(JoyCon, PairEpoch,
        LeftDeviceGeneration, LeftTransportGeneration,
        RightDeviceGeneration, RightTransportGeneration, JoyConMode);
}

internal readonly struct Switch2GyroTriggerModifierInput
{
    internal Switch2GyroTriggerModifierInput(
        in Switch2GyroTriggerSourceIdentity identity,
        Switch2JoyConProfileButton buttons, long completionTimestampQpc,
        long qpcFrequency, long profileRevision, int tuningSourceKey,
        bool outputActive)
    {
        Identity = identity;
        Buttons = buttons;
        CompletionTimestampQpc = completionTimestampQpc;
        QpcFrequency = qpcFrequency;
        ProfileRevision = profileRevision;
        TuningSourceKey = tuningSourceKey;
        OutputActive = outputActive;
    }

    internal Switch2GyroTriggerSourceIdentity Identity { get; }
    internal Switch2JoyConProfileButton Buttons { get; }
    internal long CompletionTimestampQpc { get; }
    internal long QpcFrequency { get; }
    internal long ProfileRevision { get; }
    internal int TuningSourceKey { get; }
    internal bool OutputActive { get; }
}

internal readonly struct Switch2GyroTriggerModifierResult
{
    internal Switch2GyroTriggerModifierResult(bool outputActive, bool freeze,
        bool deadzoneActive, double deadzoneAmount, bool dampeningActive,
        double dampeningMultiplier)
    {
        OutputActive = outputActive;
        Freeze = freeze;
        DeadzoneActive = deadzoneActive;
        DeadzoneAmount = deadzoneAmount;
        DampeningActive = dampeningActive;
        DampeningMultiplier = dampeningMultiplier;
    }

    internal bool OutputActive { get; }
    internal bool Freeze { get; }
    internal bool DeadzoneActive { get; }
    internal double DeadzoneAmount { get; }
    internal bool DampeningActive { get; }
    internal double DampeningMultiplier { get; }
}

internal struct Switch2GyroTriggerModifierState
{
    internal bool HasSource;
    internal Switch2GyroTriggerSourceIdentity Identity;
    internal Switch2IrGyroTuning Tuning;
    internal long ProfileRevision;
    internal int TuningSourceKey;
    internal long LastTimestampQpc;
    internal bool HasButtonBaseline;
    internal Switch2JoyConProfileButton PreviousDeadzoneButtons;
    internal Switch2JoyConProfileButton PreviousDampeningButtons;
    internal long FreezeUntilMicroseconds;
    internal long DeadzoneLatchUntilMicroseconds;
    internal long DampeningLatchUntilMicroseconds;
}

internal static class Switch2GyroTriggerModifier
{
    internal static double ApplySoftDeadzone(double value,
        double axisWeight, double deadzoneAmount)
    {
        if (!double.IsFinite(value) || !double.IsFinite(axisWeight) ||
            !double.IsFinite(deadzoneAmount))
        {
            return 0.0;
        }

        double threshold = Math.Clamp(Math.Abs(axisWeight), 0.0, 1.0) *
            Math.Clamp(deadzoneAmount, Switch2MotionSoftDeadzone.Minimum,
                Switch2MotionSoftDeadzone.Maximum);
        double magnitude = Math.Abs(value);
        return magnitude > threshold ?
            Math.CopySign(magnitude - threshold, value) : 0.0;
    }

    internal static double ApplyDampening(double value,
        in Switch2GyroTriggerModifierResult modifier)
    {
        if (!double.IsFinite(value))
        {
            return 0.0;
        }
        if (!modifier.DampeningActive)
        {
            return value;
        }
        return value * Math.Clamp(modifier.DampeningMultiplier, 0.0, 1.0);
    }

    internal static bool TryReadInput(DS4State state, long profileRevision,
        int tuningSourceKey, bool outputActive,
        out Switch2GyroTriggerModifierInput input)
    {
        input = default;
        if (state == null || profileRevision < 0)
        {
            return false;
        }

        Switch2JoyConRawInputStatus joyCon =
            state.Switch2JoyConRawInputStatus;
        bool joyConValid = joyCon.IsValid && joyCon.ContractVersion ==
            Switch2JoyConProfileInputFrame.CurrentVersion;
        bool proValid = state.Switch2RawInputStatus.IsValid &&
            state.Switch2RawInputStatus.ContractVersion == Switch2ProProfileInputFrame.CurrentVersion;
        if (joyConValid == proValid)
        {
            return false;
        }
        Switch2GyroTriggerSourceIdentity identity;
        long timestampQpc;
        long qpcFrequency;
        if (joyConValid)
        {
            identity = new Switch2GyroTriggerSourceIdentity(true,
                joyCon.PairEpoch, joyCon.LeftDeviceGeneration,
                joyCon.LeftTransportGeneration,
                joyCon.RightDeviceGeneration,
                joyCon.RightTransportGeneration, joyCon.Mode);
            timestampQpc = joyCon.CompletionTimestampQpc;
            qpcFrequency = joyCon.QpcFrequency;
        }
        else
        {
            Switch2RawInputStatus pro = state.Switch2RawInputStatus;
            if (!pro.IsValid || pro.ContractVersion !=
                    Switch2ProProfileInputFrame.CurrentVersion)
            {
                return false;
            }
            identity = new Switch2GyroTriggerSourceIdentity(false, 0,
                pro.DeviceGeneration, pro.TransportGeneration, 0, 0);
            timestampQpc = pro.CompletionTimestampQpc;
            qpcFrequency = pro.QpcFrequency;
        }

        input = new Switch2GyroTriggerModifierInput(identity,
            ReadButtons(state), timestampQpc, qpcFrequency, profileRevision,
            tuningSourceKey, outputActive);
        return true;
    }

    internal static bool TryAdvance(in Switch2GyroTriggerModifierInput input,
        in Switch2IrGyroTuning requestedTuning,
        ref Switch2GyroTriggerModifierState state,
        out Switch2GyroTriggerModifierResult result)
    {
        result = default;
        if (input.ProfileRevision < 0 ||
            input.CompletionTimestampQpc < 0 || input.QpcFrequency <= 0 ||
            !ControllerFeedbackClock.TryConvertQpcTicks(
                (ulong)input.CompletionTimestampQpc,
                (ulong)input.QpcFrequency, out ulong timestampMicroseconds) ||
            timestampMicroseconds > long.MaxValue)
        {
            state = default;
            return false;
        }

        Switch2IrGyroTuning tuning =
            Switch2IrGyroTuning.Normalize(requestedTuning);
        bool boundaryChanged = !state.HasSource ||
            !state.Identity.Equals(input.Identity) ||
            !state.Tuning.Equals(tuning) ||
            state.ProfileRevision != input.ProfileRevision ||
            state.TuningSourceKey != input.TuningSourceKey ||
            input.CompletionTimestampQpc < state.LastTimestampQpc;
        if (boundaryChanged)
        {
            state = default;
        }

        state.HasSource = true;
        state.Identity = input.Identity;
        state.Tuning = tuning;
        state.ProfileRevision = input.ProfileRevision;
        state.TuningSourceKey = input.TuningSourceKey;
        state.LastTimestampQpc = input.CompletionTimestampQpc;

        if (!input.OutputActive)
        {
            ResetWindows(ref state);
            return true;
        }

        Switch2JoyConProfileButton deadzonePressed = input.Buttons &
            tuning.DeadzoneButtons;
        Switch2JoyConProfileButton dampeningPressed = input.Buttons &
            tuning.DampeningButtons;
        long now = (long)timestampMicroseconds;
        bool previousDeadzonePressed = state.HasButtonBaseline &&
            state.PreviousDeadzoneButtons != Switch2JoyConProfileButton.None;
        bool previousDampeningPressed = state.HasButtonBaseline &&
            state.PreviousDampeningButtons != Switch2JoyConProfileButton.None;

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
                Switch2JoyConProfileButton.None, previousDampeningPressed,
            now, tuning.DampeningEffectAfterReleasedMilliseconds,
            ref state.DampeningLatchUntilMicroseconds);
        result = new Switch2GyroTriggerModifierResult(true,
            now < state.FreezeUntilMicroseconds, deadzoneActive,
            deadzoneActive ? tuning.DeadzoneAmount : 0.0,
            dampeningActive, dampeningActive ?
                (100.0 - tuning.DampeningAmountPercent) / 100.0 : 1.0);
        return true;
    }

    internal static Switch2JoyConProfileButton ReadButtons(DS4State state)
    {
        if (state == null)
        {
            return Switch2JoyConProfileButton.None;
        }

        Switch2JoyConProfileButton buttons = 0;
        Add(ref buttons, state.Square,
            Switch2JoyConProfileButton.FaceWest);
        Add(ref buttons, state.Triangle,
            Switch2JoyConProfileButton.FaceNorth);
        Add(ref buttons, state.Cross,
            Switch2JoyConProfileButton.FaceSouth);
        Add(ref buttons, state.Circle,
            Switch2JoyConProfileButton.FaceEast);
        Add(ref buttons, state.Share, Switch2JoyConProfileButton.Back);
        Add(ref buttons, state.Options, Switch2JoyConProfileButton.Start);
        Add(ref buttons, state.PS, Switch2JoyConProfileButton.Guide);
        Add(ref buttons, state.Capture, Switch2JoyConProfileButton.Capture);
        Add(ref buttons, state.L3, Switch2JoyConProfileButton.LeftStick);
        Add(ref buttons, state.R3, Switch2JoyConProfileButton.RightStick);
        Add(ref buttons, state.L1,
            Switch2JoyConProfileButton.LeftShoulder);
        Add(ref buttons, state.R1,
            Switch2JoyConProfileButton.RightShoulder);
        Add(ref buttons, state.L2Btn,
            Switch2JoyConProfileButton.LeftTrigger);
        Add(ref buttons, state.R2Btn,
            Switch2JoyConProfileButton.RightTrigger);
        Add(ref buttons, state.DpadDown,
            Switch2JoyConProfileButton.DpadDown);
        Add(ref buttons, state.DpadUp, Switch2JoyConProfileButton.DpadUp);
        Add(ref buttons, state.DpadRight,
            Switch2JoyConProfileButton.DpadRight);
        Add(ref buttons, state.DpadLeft,
            Switch2JoyConProfileButton.DpadLeft);

        Switch2JoyConRawInputStatus joyCon =
            state.Switch2JoyConRawInputStatus;
        bool joyConValid = joyCon.IsValid && joyCon.ContractVersion ==
            Switch2JoyConProfileInputFrame.CurrentVersion;
        bool proValid = state.Switch2RawInputStatus.IsValid &&
            state.Switch2RawInputStatus.ContractVersion == Switch2ProProfileInputFrame.CurrentVersion;
        if (joyConValid && !proValid)
        {
            Add(ref buttons, joyCon.CButton, Switch2JoyConProfileButton.C);
            Add(ref buttons, joyCon.LeftPaddle1,
                Switch2JoyConProfileButton.LeftPaddle1);
            Add(ref buttons, joyCon.LeftPaddle2,
                Switch2JoyConProfileButton.LeftPaddle2);
            Add(ref buttons, joyCon.RightPaddle1,
                Switch2JoyConProfileButton.RightPaddle1);
            Add(ref buttons, joyCon.RightPaddle2,
                Switch2JoyConProfileButton.RightPaddle2);
            Add(ref buttons, joyCon.LeftPresent && joyCon.LeftRailSL,
                Switch2JoyConProfileButton.LeftRailSL);
            Add(ref buttons, joyCon.LeftPresent && joyCon.LeftRailSR,
                Switch2JoyConProfileButton.LeftRailSR);
            Add(ref buttons, joyCon.RightPresent && joyCon.RightRailSL,
                Switch2JoyConProfileButton.RightRailSL);
            Add(ref buttons, joyCon.RightPresent && joyCon.RightRailSR,
                Switch2JoyConProfileButton.RightRailSR);
        }
        else if (proValid && !joyConValid)
        {
            Add(ref buttons, state.Switch2RawInputStatus.CButton,
                Switch2JoyConProfileButton.C);
            Add(ref buttons, state.BLP,
                Switch2JoyConProfileButton.LeftPaddle1);
            Add(ref buttons, state.BRP,
                Switch2JoyConProfileButton.RightPaddle1);
        }
        return buttons;
    }

    private static bool AdvanceLatch(bool pressed, bool previouslyPressed,
        long now, int releaseMilliseconds, ref long latchUntil)
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

    private static void ResetWindows(
        ref Switch2GyroTriggerModifierState state)
    {
        state.HasButtonBaseline = false;
        state.PreviousDeadzoneButtons = Switch2JoyConProfileButton.None;
        state.PreviousDampeningButtons = Switch2JoyConProfileButton.None;
        state.FreezeUntilMicroseconds = 0;
        state.DeadzoneLatchUntilMicroseconds = 0;
        state.DampeningLatchUntilMicroseconds = 0;
    }

    private static void Add(ref Switch2JoyConProfileButton target,
        bool active, Switch2JoyConProfileButton button)
    {
        if (active)
        {
            target |= button;
        }
    }
}
