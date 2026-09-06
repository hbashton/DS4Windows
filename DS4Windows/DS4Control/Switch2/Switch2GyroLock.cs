/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The Hold/Tap Gyro Lock policy is adapted from the GPL-3.0
Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py.
*/

using System;

namespace DS4Windows.Switch2;

internal readonly struct Switch2GyroLockBinding :
    IEquatable<Switch2GyroLockBinding>
{
    internal Switch2GyroLockBinding(
        Switch2JoyConProfileButton holdButtons,
        Switch2JoyConProfileButton toggleButtons)
    {
        HoldButtons = holdButtons;
        ToggleButtons = toggleButtons;
    }

    internal Switch2JoyConProfileButton HoldButtons { get; }
    internal Switch2JoyConProfileButton ToggleButtons { get; }
    internal bool Enabled => HoldButtons != Switch2JoyConProfileButton.None ||
        ToggleButtons != Switch2JoyConProfileButton.None;

    internal static Switch2GyroLockBinding Normalize(
        in Switch2GyroLockBinding value)
    {
        Switch2JoyConProfileButton hold =
            Switch2IrGyroMotionModifier.IsValidButtonMask(
                value.HoldButtons) ? value.HoldButtons :
                Switch2JoyConProfileButton.None;
        Switch2JoyConProfileButton toggle =
            Switch2IrGyroMotionModifier.IsValidButtonMask(
                value.ToggleButtons) ? value.ToggleButtons :
                Switch2JoyConProfileButton.None;
        return new(hold, toggle & ~hold);
    }

    public bool Equals(Switch2GyroLockBinding other) =>
        HoldButtons == other.HoldButtons &&
        ToggleButtons == other.ToggleButtons;

    public override bool Equals(object obj) => obj is
        Switch2GyroLockBinding other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(HoldButtons, ToggleButtons);
}

internal sealed class Switch2GyroLockBindingTable
{
    private Switch2GyroLockBinding mouse;
    private Switch2GyroLockBinding mouseJoystick;

    internal Switch2GyroLockBinding Get(GyroOutMode mode) => mode switch
    {
        GyroOutMode.Mouse => mouse,
        GyroOutMode.MouseJoystick => mouseJoystick,
        _ => default,
    };

    internal bool TrySet(GyroOutMode mode,
        in Switch2GyroLockBinding binding)
    {
        Switch2GyroLockBinding normalized =
            Switch2GyroLockBinding.Normalize(binding);
        if (mode == GyroOutMode.Mouse)
        {
            mouse = normalized;
            return true;
        }
        if (mode == GyroOutMode.MouseJoystick)
        {
            mouseJoystick = normalized;
            return true;
        }
        return false;
    }
}

internal struct Switch2GyroLockState
{
    internal bool HasSource;
    internal Switch2GyroTriggerSourceIdentity Identity;
    internal Switch2GyroLockBinding Binding;
    internal GyroOutMode Mode;
    internal long ProfileRevision;
    internal long LastTimestampQpc;
    internal bool HasButtonBaseline;
    internal Switch2JoyConProfileButton PreviousToggleButtons;
    internal bool ToggleActive;
}

internal static class Switch2GyroLock
{
    internal static bool TryAdvance(
        in Switch2GyroTriggerModifierInput input, GyroOutMode mode,
        in Switch2GyroLockBinding requestedBinding,
        ref Switch2GyroLockState state, out bool locked)
    {
        locked = false;
        if (input.ProfileRevision < 0 ||
            input.CompletionTimestampQpc < 0 || input.QpcFrequency <= 0 ||
            mode is not GyroOutMode.Mouse and not GyroOutMode.MouseJoystick ||
            !ControllerFeedbackClock.TryConvertQpcTicks(
                (ulong)input.CompletionTimestampQpc,
                (ulong)input.QpcFrequency, out ulong timestampMicroseconds) ||
            timestampMicroseconds > long.MaxValue)
        {
            state = default;
            return false;
        }

        Switch2GyroLockBinding binding =
            Switch2GyroLockBinding.Normalize(requestedBinding);
        bool boundaryChanged = !state.HasSource ||
            !state.Identity.Equals(input.Identity) ||
            !state.Binding.Equals(binding) || state.Mode != mode ||
            state.ProfileRevision != input.ProfileRevision ||
            input.CompletionTimestampQpc < state.LastTimestampQpc;
        if (boundaryChanged)
        {
            state = default;
        }

        state.HasSource = true;
        state.Identity = input.Identity;
        state.Binding = binding;
        state.Mode = mode;
        state.ProfileRevision = input.ProfileRevision;
        state.LastTimestampQpc = input.CompletionTimestampQpc;

        if (!input.OutputActive || !binding.Enabled)
        {
            state.HasButtonBaseline = false;
            state.PreviousToggleButtons = Switch2JoyConProfileButton.None;
            state.ToggleActive = false;
            return true;
        }

        Switch2JoyConProfileButton togglePressed = input.Buttons &
            binding.ToggleButtons;
        if (!state.HasButtonBaseline)
        {
            state.PreviousToggleButtons = togglePressed;
            state.HasButtonBaseline = true;
        }
        else
        {
            Switch2JoyConProfileButton newlyPressed = togglePressed &
                ~state.PreviousToggleButtons;
            if (HasOddBitCount((uint)newlyPressed))
            {
                state.ToggleActive = !state.ToggleActive;
            }
            state.PreviousToggleButtons = togglePressed;
        }

        locked = state.ToggleActive ||
            (input.Buttons & binding.HoldButtons) !=
                Switch2JoyConProfileButton.None;
        return true;
    }

    private static bool HasOddBitCount(uint value)
    {
        value ^= value >> 16;
        value ^= value >> 8;
        value ^= value >> 4;
        value &= 0xFu;
        return ((0x6996u >> (int)value) & 1u) != 0;
    }
}
