/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The Hold/Tap XOR state machine, joined Joy-Con sharing, activation-button
consumption, and gyro auto-apply policy are adapted from the GPL-3.0
Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py and
src/virtual_controller.py. The result drives DS4Windows' existing shifted
actions; it does not introduce another mapper.
*/

using System;

namespace DS4Windows.Switch2;

public enum Switch2ModeShiftScope : byte
{
    Mouse,
    MouseJoystick,
    Steering,
}

internal readonly struct Switch2ModeShiftSettings :
    IEquatable<Switch2ModeShiftSettings>
{
    internal Switch2ModeShiftSettings(
        Switch2JoyConProfileButton holdButtons,
        Switch2JoyConProfileButton toggleButtons,
        bool autoApplyGyroMouse = true,
        bool autoApplyGyroMouseJoystick = false,
        bool autoApplySteering = false)
    {
        HoldButtons = holdButtons;
        ToggleButtons = toggleButtons;
        AutoApplyGyroMouse = autoApplyGyroMouse;
        AutoApplyGyroMouseJoystick = autoApplyGyroMouseJoystick;
        AutoApplySteering = autoApplySteering;
    }

    internal static Switch2ModeShiftSettings Default => new(
        Switch2JoyConProfileButton.None,
        Switch2JoyConProfileButton.None);

    internal Switch2JoyConProfileButton HoldButtons { get; }
    internal Switch2JoyConProfileButton ToggleButtons { get; }
    internal bool AutoApplyGyroMouse { get; }
    internal bool AutoApplyGyroMouseJoystick { get; }
    internal bool AutoApplySteering { get; }
    internal bool HasActivationButtons =>
        HoldButtons != Switch2JoyConProfileButton.None ||
        ToggleButtons != Switch2JoyConProfileButton.None;

    internal static Switch2ModeShiftSettings Normalize(
        in Switch2ModeShiftSettings value)
    {
        Switch2JoyConProfileButton hold =
            Switch2IrGyroMotionModifier.IsValidButtonMask(
                value.HoldButtons) ? value.HoldButtons :
                Switch2JoyConProfileButton.None;
        Switch2JoyConProfileButton toggle =
            Switch2IrGyroMotionModifier.IsValidButtonMask(
                value.ToggleButtons) ? value.ToggleButtons :
                Switch2JoyConProfileButton.None;
        return new(hold, toggle & ~hold, value.AutoApplyGyroMouse,
            value.AutoApplyGyroMouseJoystick, value.AutoApplySteering);
    }

    public bool Equals(Switch2ModeShiftSettings other) =>
        HoldButtons == other.HoldButtons &&
        ToggleButtons == other.ToggleButtons &&
        AutoApplyGyroMouse == other.AutoApplyGyroMouse &&
        AutoApplyGyroMouseJoystick == other.AutoApplyGyroMouseJoystick &&
        AutoApplySteering == other.AutoApplySteering;

    public override bool Equals(object obj) => obj is
        Switch2ModeShiftSettings other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(HoldButtons,
        ToggleButtons, AutoApplyGyroMouse, AutoApplyGyroMouseJoystick,
        AutoApplySteering);
}

internal struct Switch2ModeShiftState
{
    internal bool HasSource;
    internal Switch2GyroTriggerSourceIdentity Identity;
    internal Switch2ModeShiftSettings Settings;
    internal long ProfileRevision;
    internal long LastTimestampQpc;
    internal bool HasButtonBaseline;
    internal Switch2JoyConProfileButton PreviousToggleButtons;
    internal bool ToggleActive;
    internal bool AutoApplyActive;
}

internal static class Switch2ModeShift
{
    private static readonly Switch2ModeShiftScope[] editingScopes =
        new Switch2ModeShiftScope[Global.TEST_PROFILE_ITEM_COUNT];

    internal static Switch2ModeShiftScope ResolveScope(int device)
    {
        if (device >= 0 &&
            Global.GetSASteeringWheelEmulationAxis(device) !=
                SASteeringWheelEmulationAxisType.None)
        {
            return Switch2ModeShiftScope.Steering;
        }
        return device >= 0 && Global.GetGyroOutMode(device) ==
            GyroOutMode.MouseJoystick ?
                Switch2ModeShiftScope.MouseJoystick :
                Switch2ModeShiftScope.Mouse;
    }

    internal static Switch2ModeShiftScope ResolveEditingScope(int device) =>
        (uint)device < editingScopes.Length ? editingScopes[device] :
            Switch2ModeShiftScope.Mouse;

    internal static void SetEditingScope(int device,
        Switch2ModeShiftScope scope)
    {
        if ((uint)device < editingScopes.Length &&
            scope is Switch2ModeShiftScope.Mouse or
                Switch2ModeShiftScope.MouseJoystick or
                Switch2ModeShiftScope.Steering)
        {
            editingScopes[device] = scope;
        }
    }

    internal static bool TryAdvance(
        in Switch2GyroTriggerModifierInput input,
        in Switch2ModeShiftSettings requestedSettings,
        bool autoApplyActive, ref Switch2ModeShiftState state,
        out bool layerActive)
    {
        layerActive = false;
        if (input.ProfileRevision < 0 ||
            input.CompletionTimestampQpc < 0 || input.QpcFrequency <= 0 ||
            !ControllerFeedbackClock.TryConvertQpcTicks(
                (ulong)input.CompletionTimestampQpc,
                (ulong)input.QpcFrequency, out _))
        {
            state = default;
            return false;
        }

        Switch2ModeShiftSettings settings =
            Switch2ModeShiftSettings.Normalize(requestedSettings);
        bool boundaryChanged = !state.HasSource ||
            !state.Identity.Equals(input.Identity) ||
            !state.Settings.Equals(settings) ||
            state.ProfileRevision != input.ProfileRevision ||
            input.CompletionTimestampQpc < state.LastTimestampQpc;
        if (boundaryChanged)
        {
            state = default;
        }

        state.HasSource = true;
        state.Identity = input.Identity;
        state.Settings = settings;
        state.ProfileRevision = input.ProfileRevision;
        state.LastTimestampQpc = input.CompletionTimestampQpc;

        Switch2JoyConProfileButton togglePressed = input.Buttons &
            settings.ToggleButtons;
        if (!state.HasButtonBaseline)
        {
            // A held button at a transport/profile boundary is a baseline, not
            // a manufactured Tap edge. Hold still takes effect below.
            state.PreviousToggleButtons = togglePressed;
            state.HasButtonBaseline = true;
        }
        else
        {
            Switch2JoyConProfileButton newlyPressed = togglePressed &
                ~state.PreviousToggleButtons;
            if (newlyPressed != Switch2JoyConProfileButton.None)
            {
                // Switch2Connect reduces all Tap presses observed in one report
                // to one edge, so simultaneous presses toggle exactly once.
                state.ToggleActive = !state.ToggleActive;
            }
            state.PreviousToggleButtons = togglePressed;
        }

        // Donor behavior clears a Tap-entered layer when an auto-applied gyro
        // scope closes, including an edge that arrives on that closing report.
        if (state.AutoApplyActive && !autoApplyActive)
        {
            state.ToggleActive = false;
        }
        state.AutoApplyActive = autoApplyActive;

        bool holdActive = (input.Buttons & settings.HoldButtons) !=
            Switch2JoyConProfileButton.None;
        bool buttonActive = state.ToggleActive != holdActive;
        layerActive = autoApplyActive != buttonActive;
        return true;
    }

    // A horizontal rail is also the legacy mini-controller shoulder. That is
    // intentional for ordinary mappings, but an explicit Mode Shift command
    // consumes the entire physical button, whichever alias the user selected.
    internal static bool IsActivationControl(DS4Controls control,
        in Switch2ModeShiftSettings settings, DS4State source)
    {
        if (!HasExclusiveCurrentSource(source))
            return false;
        if (IsActivationControl(control, settings))
            return true;
        if (control is not (DS4Controls.L1 or DS4Controls.R1 or
            DS4Controls.Switch2JoyConLeftSL or DS4Controls.Switch2JoyConLeftSR or
            DS4Controls.Switch2JoyConRightSL or DS4Controls.Switch2JoyConRightSR))
            return false;
        DS4Controls sl = HorizontalSL(source);
        if (sl == DS4Controls.None)
            return false;
        DS4Controls sr = sl == DS4Controls.Switch2JoyConLeftSL ?
            DS4Controls.Switch2JoyConLeftSR : DS4Controls.Switch2JoyConRightSR;
        DS4Controls alias = control == DS4Controls.L1 ? sl :
            control == DS4Controls.R1 ? sr : control == sl ? DS4Controls.L1 :
            control == sr ? DS4Controls.R1 : DS4Controls.None;
        return IsActivationControl(alias, settings);
    }

    internal static Switch2ModeShiftSettings NormalizeForSource(
        DS4State source, in Switch2ModeShiftSettings requested)
    {
        var settings = Switch2ModeShiftSettings.Normalize(requested);
        if (!settings.HasActivationButtons)
            return settings;
        DS4Controls sl = HorizontalSL(source);
        if (sl == DS4Controls.None)
            return settings;
        var slBit = sl == DS4Controls.Switch2JoyConLeftSL ?
            Switch2JoyConProfileButton.LeftRailSL : Switch2JoyConProfileButton.RightRailSL;
        var srBit = sl == DS4Controls.Switch2JoyConLeftSL ?
            Switch2JoyConProfileButton.LeftRailSR : Switch2JoyConProfileButton.RightRailSR;
        var holdAliases = settings.HoldButtons;
        ExpandHold(slBit, Switch2JoyConProfileButton.LeftShoulder);
        ExpandHold(srBit, Switch2JoyConProfileButton.RightShoulder);
        return new(settings.HoldButtons, settings.ToggleButtons & ~holdAliases,
            settings.AutoApplyGyroMouse, settings.AutoApplyGyroMouseJoystick,
            settings.AutoApplySteering);

        void ExpandHold(Switch2JoyConProfileButton rail, Switch2JoyConProfileButton shoulder)
        {
            if ((settings.HoldButtons & (rail | shoulder)) != 0)
                holdAliases |= rail | shoulder;
        }
    }

    private static bool HasExclusiveCurrentSource(DS4State source) => source != null &&
        (source.Switch2JoyConRawInputStatus.IsValid && source.Switch2JoyConRawInputStatus.ContractVersion ==
            Switch2JoyConProfileInputFrame.CurrentVersion) !=
        (source.Switch2RawInputStatus.IsValid && source.Switch2RawInputStatus.ContractVersion ==
            Switch2ProProfileInputFrame.CurrentVersion);

    private static DS4Controls HorizontalSL(DS4State source)
    {
        if (!HasExclusiveCurrentSource(source))
            return DS4Controls.None;
        var joyCon = source.Switch2JoyConRawInputStatus;
        if (!joyCon.IsValid || joyCon.ContractVersion != Switch2JoyConProfileInputFrame.CurrentVersion ||
            joyCon.PairEpoch != 0)
            return DS4Controls.None;
        return joyCon.Mode switch {
            Switch2JoyConProfileMode.StandaloneHorizontalLeft when joyCon.LeftPresent && !joyCon.RightPresent &&
                joyCon.LeftDeviceGeneration != 0 && joyCon.LeftTransportGeneration != 0 =>
                    DS4Controls.Switch2JoyConLeftSL,
            Switch2JoyConProfileMode.StandaloneHorizontalRight when joyCon.RightPresent && !joyCon.LeftPresent &&
                joyCon.RightDeviceGeneration != 0 && joyCon.RightTransportGeneration != 0 =>
                    DS4Controls.Switch2JoyConRightSL,
            _ => DS4Controls.None };
    }

    internal static bool IsActivationControl(DS4Controls control,
        in Switch2ModeShiftSettings requestedSettings)
    {
        Switch2JoyConProfileButton button = control switch
        {
            DS4Controls.Square => Switch2JoyConProfileButton.FaceWest,
            DS4Controls.Triangle => Switch2JoyConProfileButton.FaceNorth,
            DS4Controls.Cross => Switch2JoyConProfileButton.FaceSouth,
            DS4Controls.Circle => Switch2JoyConProfileButton.FaceEast,
            DS4Controls.Share => Switch2JoyConProfileButton.Back,
            DS4Controls.Options => Switch2JoyConProfileButton.Start,
            DS4Controls.PS => Switch2JoyConProfileButton.Guide,
            DS4Controls.Capture => Switch2JoyConProfileButton.Capture,
            DS4Controls.L3 => Switch2JoyConProfileButton.LeftStick,
            DS4Controls.R3 => Switch2JoyConProfileButton.RightStick,
            DS4Controls.L1 => Switch2JoyConProfileButton.LeftShoulder,
            DS4Controls.R1 => Switch2JoyConProfileButton.RightShoulder,
            DS4Controls.L2 => Switch2JoyConProfileButton.LeftTrigger,
            DS4Controls.R2 => Switch2JoyConProfileButton.RightTrigger,
            DS4Controls.DpadDown => Switch2JoyConProfileButton.DpadDown,
            DS4Controls.DpadUp => Switch2JoyConProfileButton.DpadUp,
            DS4Controls.DpadRight => Switch2JoyConProfileButton.DpadRight,
            DS4Controls.DpadLeft => Switch2JoyConProfileButton.DpadLeft,
            DS4Controls.BLP => Switch2JoyConProfileButton.LeftPaddle1,
            DS4Controls.BRP => Switch2JoyConProfileButton.RightPaddle1,
            DS4Controls.Switch2C => Switch2JoyConProfileButton.C,
            DS4Controls.Switch2JoyConLeftPaddle1 =>
                Switch2JoyConProfileButton.LeftPaddle1,
            DS4Controls.Switch2JoyConLeftSL => Switch2JoyConProfileButton.LeftRailSL,
            DS4Controls.Switch2JoyConLeftSR => Switch2JoyConProfileButton.LeftRailSR,
            DS4Controls.Switch2JoyConRightSL => Switch2JoyConProfileButton.RightRailSL,
            DS4Controls.Switch2JoyConRightSR => Switch2JoyConProfileButton.RightRailSR,
            DS4Controls.Switch2JoyConLeftPaddle2 =>
                Switch2JoyConProfileButton.LeftPaddle2,
            DS4Controls.Switch2JoyConRightPaddle1 =>
                Switch2JoyConProfileButton.RightPaddle1,
            DS4Controls.Switch2JoyConRightPaddle2 =>
                Switch2JoyConProfileButton.RightPaddle2,
            DS4Controls.Switch2JoyConLeftIrSensor =>
                Switch2JoyConProfileButton.LeftIrSensor,
            DS4Controls.Switch2JoyConRightIrSensor =>
                Switch2JoyConProfileButton.RightIrSensor,
            _ => Switch2JoyConProfileButton.None,
        };
        if (button == Switch2JoyConProfileButton.None)
        {
            return false;
        }

        Switch2ModeShiftSettings settings =
            Switch2ModeShiftSettings.Normalize(requestedSettings);
        return ((settings.HoldButtons | settings.ToggleButtons) & button) !=
            Switch2JoyConProfileButton.None;
    }
}
