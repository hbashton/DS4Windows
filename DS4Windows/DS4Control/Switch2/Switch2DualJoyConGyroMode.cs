/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The mode and Hold/Toggle edge semantics in this file are adapted from the
GPL-3.0 Switch2Connect project, commit
4487322a306f04efa27682e3f3a508635a84fd98, src/config.py and
src/virtual_controller.py (normalize_djg_settings and handle_djg_trigger).
Per-side OR activation including IR follows src/controller.py mapping_pairs,
trigger_djg and prev_djg at commit 61ac6642ce12fe7217e38a860b14863b18ca7e28.
The implementation remains inside DS4Windows' existing serialized profile
input lifetime and does not create an input mapper, worker, or output owner.
*/

using System;

namespace DS4Windows.Switch2;

public enum Switch2DualGyroMode : byte
{
    Invalid = 0,
    SwitchDominantSide,
    SwitchGyroSide,
    SingleSideToggle,
}

public enum Switch2DualGyroActivationMode : byte
{
    Invalid = 0,
    Hold,
    Toggle,
}

internal readonly struct Switch2DualGyroConfiguration :
    IEquatable<Switch2DualGyroConfiguration>
{
    private Switch2DualGyroConfiguration(bool enabled,
        Switch2DualGyroMode mode,
        Switch2DualGyroDominantSide dominantSide,
        Switch2DualGyroActivationMode activationMode,
        Switch2JoyConProfileButton leftActivationButton,
        Switch2JoyConProfileButton rightActivationButton,
        Switch2IrActivationThreshold leftIrThreshold =
            Switch2IrActivationThreshold.Strict,
        Switch2IrActivationThreshold rightIrThreshold =
            Switch2IrActivationThreshold.Strict,
        long profileRevision = 0)
    {
        Enabled = enabled;
        Mode = mode;
        DominantSide = dominantSide;
        ActivationMode = activationMode;
        LeftActivationButton = leftActivationButton;
        RightActivationButton = rightActivationButton;
        LeftIrThreshold = leftIrThreshold;
        RightIrThreshold = rightIrThreshold;
        ProfileRevision = profileRevision;
    }

    internal bool Enabled { get; }

    internal Switch2DualGyroMode Mode { get; }

    internal Switch2DualGyroDominantSide DominantSide { get; }

    internal Switch2DualGyroActivationMode ActivationMode { get; }

    internal Switch2JoyConProfileButton LeftActivationButton { get; }

    internal Switch2JoyConProfileButton RightActivationButton { get; }

    internal Switch2IrActivationThreshold LeftIrThreshold { get; }

    internal Switch2IrActivationThreshold RightIrThreshold { get; }

    internal long ProfileRevision { get; }

    internal static Switch2DualGyroConfiguration Default => new(false,
        Switch2DualGyroMode.SwitchDominantSide,
        Switch2DualGyroDominantSide.Right,
        Switch2DualGyroActivationMode.Hold,
        Switch2JoyConProfileButton.None,
        Switch2JoyConProfileButton.None);

    internal static bool TryCreate(bool enabled, Switch2DualGyroMode mode,
        Switch2DualGyroDominantSide dominantSide,
        Switch2DualGyroActivationMode activationMode,
        Switch2JoyConProfileButton leftActivationButton,
        Switch2JoyConProfileButton rightActivationButton,
        out Switch2DualGyroConfiguration configuration,
        Switch2IrActivationThreshold leftIrThreshold =
            Switch2IrActivationThreshold.Strict,
        Switch2IrActivationThreshold rightIrThreshold =
            Switch2IrActivationThreshold.Strict,
        long profileRevision = 0)
    {
        if (mode is < Switch2DualGyroMode.SwitchDominantSide or >
                Switch2DualGyroMode.SingleSideToggle ||
            activationMode is < Switch2DualGyroActivationMode.Hold or >
                Switch2DualGyroActivationMode.Toggle ||
            !IsValidActivationButton(leftActivationButton) ||
            !IsValidActivationButton(rightActivationButton) ||
            !IsValidThreshold(leftIrThreshold) ||
            !IsValidThreshold(rightIrThreshold) || profileRevision < 0 ||
            dominantSide is < Switch2DualGyroDominantSide.Left or >
                Switch2DualGyroDominantSide.None ||
            mode != Switch2DualGyroMode.SingleSideToggle &&
                dominantSide == Switch2DualGyroDominantSide.None)
        {
            configuration = default;
            return false;
        }

        configuration = new Switch2DualGyroConfiguration(enabled, mode,
            dominantSide, activationMode, leftActivationButton,
            rightActivationButton, leftIrThreshold, rightIrThreshold,
            profileRevision);
        return true;
    }

    internal static bool IsValidActivationButton(
        Switch2JoyConProfileButton button)
    {
        uint value = (uint)button;
        uint known = (uint)Switch2JoyConProfileButton.RightRailSR |
            ((uint)Switch2JoyConProfileButton.RightRailSR - 1u);
        return (value & ~known) == 0;
    }

    private static bool IsValidThreshold(Switch2IrActivationThreshold value) =>
        value is Switch2IrActivationThreshold.Strict or
            Switch2IrActivationThreshold.Balanced or
            Switch2IrActivationThreshold.Relaxed;

    public bool Equals(Switch2DualGyroConfiguration other) =>
        Enabled == other.Enabled && Mode == other.Mode &&
        DominantSide == other.DominantSide &&
        ActivationMode == other.ActivationMode &&
        LeftActivationButton == other.LeftActivationButton &&
        RightActivationButton == other.RightActivationButton &&
        LeftIrThreshold == other.LeftIrThreshold &&
        RightIrThreshold == other.RightIrThreshold &&
        ProfileRevision == other.ProfileRevision;

    public override bool Equals(object obj) => obj is
        Switch2DualGyroConfiguration other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Enabled, Mode,
        DominantSide, ActivationMode, LeftActivationButton,
        RightActivationButton, HashCode.Combine(LeftIrThreshold,
            RightIrThreshold, ProfileRevision));

    public static bool operator ==(Switch2DualGyroConfiguration left,
        Switch2DualGyroConfiguration right) => left.Equals(right);

    public static bool operator !=(Switch2DualGyroConfiguration left,
        Switch2DualGyroConfiguration right) => !left.Equals(right);
}

internal struct Switch2DualGyroModeState
{
    internal bool HasConfiguration;
    internal Switch2DualGyroConfiguration Configuration;
    internal ulong PairEpoch;
    internal ulong ConfigurationEpoch;
    internal bool PreviousLeftPressed;
    internal bool PreviousRightPressed;
    internal bool LeftHoldPressAdmitted;
    internal bool RightHoldPressAdmitted;
    internal bool LeftActive;
    internal bool RightActive;
    internal Switch2DualGyroDominantSide RuntimeDominantSide;
    internal Switch2JoyConSide ActiveGyroSide;
}

internal readonly struct Switch2DualGyroRuntimePolicy
{
    internal Switch2DualGyroRuntimePolicy(bool fusionEnabled,
        Switch2DualGyroMode mode,
        Switch2DualGyroDominantSide dominantSide, bool leftActive,
        bool rightActive, ulong configurationEpoch)
    {
        FusionEnabled = fusionEnabled;
        Mode = mode;
        DominantSide = dominantSide;
        LeftActive = leftActive;
        RightActive = rightActive;
        ConfigurationEpoch = configurationEpoch;
    }

    internal bool FusionEnabled { get; }

    internal Switch2DualGyroMode Mode { get; }

    internal Switch2DualGyroDominantSide DominantSide { get; }

    internal bool LeftActive { get; }

    internal bool RightActive { get; }

    internal ulong ConfigurationEpoch { get; }
}

internal static class Switch2DualJoyConGyroMode
{
    internal static Switch2JoyConProfileButton ObserveActivationButtons(
        in Switch2JoyConProfileSide source, Switch2JoyConSide side,
        Switch2IrActivationThreshold threshold)
    {
        bool left = side == Switch2JoyConSide.Left;
        if (!source.IsPresent || side is not (Switch2JoyConSide.Left or
                Switch2JoyConSide.Right) || source.Model != (left ?
                Switch2ControllerModel.JoyCon2Left :
                Switch2ControllerModel.JoyCon2Right))
        {
            return Switch2JoyConProfileButton.None;
        }

        // IR is derived from this physical sensor, never accepted as a raw
        // button bit or taken from the opposite Joy-Con. Do not modify the
        // immutable frame or consume any ordinary profile/game controls.
        const uint physicalMask =
            (((uint)Switch2JoyConProfileButton.RightRailSR << 1) - 1u) &
            ~(uint)(Switch2JoyConProfileButton.LeftIrSensor |
                Switch2JoyConProfileButton.RightIrSensor);
        var buttons = source.Buttons & (Switch2JoyConProfileButton)physicalMask;
        if (source.HasCommonMotion &&
            Switch2IrMouseProjection.IsThresholdActive(threshold,
                source.IrRoughness, source.IrDistance))
        {
            buttons |= left ? Switch2JoyConProfileButton.LeftIrSensor :
                Switch2JoyConProfileButton.RightIrSensor;
        }
        return buttons;
    }

    internal static bool TryResolve(ref Switch2DualGyroModeState state,
        ulong pairEpoch, Switch2JoyConProfileButton leftButtons,
        Switch2JoyConProfileButton rightButtons,
        in Switch2DualGyroConfiguration configuration,
        out Switch2DualGyroRuntimePolicy policy)
    {
        policy = default;
        if (!Switch2DualGyroConfiguration.TryCreate(configuration.Enabled,
                configuration.Mode, configuration.DominantSide,
                configuration.ActivationMode,
                configuration.LeftActivationButton,
                configuration.RightActivationButton, out _,
                configuration.LeftIrThreshold, configuration.RightIrThreshold,
                configuration.ProfileRevision))
        {
            // The invalid interval is an unknown configuration lifetime. Do
            // not preserve an edge baseline across it: the next valid frame
            // must synchronize its currently held buttons before transitions
            // can resume.
            state = default;
            return false;
        }

        bool leftPressed = IsPressed(leftButtons,
            configuration.LeftActivationButton);
        bool rightPressed = IsPressed(rightButtons,
            configuration.RightActivationButton);
        if (!state.HasConfiguration ||
            state.Configuration != configuration || state.PairEpoch !=
                pairEpoch)
        {
            state.HasConfiguration = true;
            state.Configuration = configuration;
            state.PairEpoch = pairEpoch;
            state.ConfigurationEpoch = state.ConfigurationEpoch ==
                ulong.MaxValue ? 1 : state.ConfigurationEpoch + 1;
            state.PreviousLeftPressed = leftPressed;
            state.PreviousRightPressed = rightPressed;
            state.LeftHoldPressAdmitted = false;
            state.RightHoldPressAdmitted = false;
            state.LeftActive = true;
            state.RightActive = true;
            state.RuntimeDominantSide = configuration.DominantSide ==
                    Switch2DualGyroDominantSide.None ?
                Switch2DualGyroDominantSide.Right :
                configuration.DominantSide;
            state.ActiveGyroSide = state.RuntimeDominantSide ==
                    Switch2DualGyroDominantSide.Left ?
                Switch2JoyConSide.Left : Switch2JoyConSide.Right;
        }
        else
        {
            ProcessEdge(ref state, Switch2JoyConSide.Left, leftPressed,
                state.PreviousLeftPressed);
            ProcessEdge(ref state, Switch2JoyConSide.Right, rightPressed,
                state.PreviousRightPressed);
            state.PreviousLeftPressed = leftPressed;
            state.PreviousRightPressed = rightPressed;
        }

        bool leftActive = true;
        bool rightActive = true;
        Switch2DualGyroDominantSide dominant = state.RuntimeDominantSide;
        if (configuration.Mode == Switch2DualGyroMode.SwitchGyroSide)
        {
            leftActive = state.ActiveGyroSide == Switch2JoyConSide.Left;
            rightActive = state.ActiveGyroSide == Switch2JoyConSide.Right;
            dominant = leftActive ? Switch2DualGyroDominantSide.Left :
                Switch2DualGyroDominantSide.Right;
        }
        else if (configuration.Mode ==
            Switch2DualGyroMode.SingleSideToggle)
        {
            leftActive = state.LeftActive;
            rightActive = state.RightActive;
            dominant = configuration.DominantSide;
        }

        policy = new Switch2DualGyroRuntimePolicy(configuration.Enabled,
            configuration.Mode, dominant, leftActive, rightActive,
            state.ConfigurationEpoch);
        return true;
    }

    private static void ProcessEdge(ref Switch2DualGyroModeState state,
        Switch2JoyConSide sourceSide, bool pressed, bool previousPressed)
    {
        bool trigger = state.Configuration.ActivationMode ==
                Switch2DualGyroActivationMode.Toggle ?
            pressed && !previousPressed : pressed != previousPressed;
        if (!trigger)
        {
            return;
        }

        if (state.Configuration.ActivationMode ==
            Switch2DualGyroActivationMode.Hold)
        {
            ref bool admitted = ref (sourceSide == Switch2JoyConSide.Left ?
                ref state.LeftHoldPressAdmitted :
                ref state.RightHoldPressAdmitted);
            if (!pressed && !admitted)
            {
                // A held input at a profile/pair/threshold boundary was only
                // baselined. Its release cannot undo an unadmitted press.
                return;
            }
            admitted = pressed;
        }

        switch (state.Configuration.Mode)
        {
            case Switch2DualGyroMode.SwitchDominantSide:
                state.RuntimeDominantSide = state.RuntimeDominantSide ==
                        Switch2DualGyroDominantSide.Left ?
                    Switch2DualGyroDominantSide.Right :
                    Switch2DualGyroDominantSide.Left;
                break;
            case Switch2DualGyroMode.SwitchGyroSide:
                state.ActiveGyroSide = state.ActiveGyroSide ==
                        Switch2JoyConSide.Left ? Switch2JoyConSide.Right :
                    Switch2JoyConSide.Left;
                break;
            case Switch2DualGyroMode.SingleSideToggle:
                if (sourceSide == Switch2JoyConSide.Left)
                {
                    state.LeftActive = !state.LeftActive;
                }
                else
                {
                    state.RightActive = !state.RightActive;
                }
                break;
        }
    }

    private static bool IsPressed(Switch2JoyConProfileButton buttons,
        Switch2JoyConProfileButton activationButton) => activationButton !=
            Switch2JoyConProfileButton.None &&
        (buttons & activationButton) != 0;
}
