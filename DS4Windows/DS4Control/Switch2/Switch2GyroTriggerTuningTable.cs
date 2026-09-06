/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows.Switch2;

/// <summary>
/// Fixed-size per-profile lookup for Switch2Connect-compatible gyro trigger
/// tuning. Mouse and Mouse Joystick remain independent mapping scopes, and the
/// numeric trigger index is the existing append-only DS4Windows profile token.
/// </summary>
internal sealed class Switch2GyroTriggerTuningTable
{
    internal const int AlwaysOnTriggerIndex =
        Switch2IrGyroMotionModifier.RightIrGyroTriggerIndex + 1;
    // 29 remains the persisted Always On tuning slot. The activation token
    // for Always On remains -1; never reuse either for a physical control.
    internal const int CTriggerIndex = 30;
    internal const int LeftSLTriggerIndex = 31;
    internal const int LeftSRTriggerIndex = 32;
    internal const int RightSLTriggerIndex = 33;
    internal const int RightSRTriggerIndex = 34;
    internal const int TriggerCount = RightSRTriggerIndex + 1;

    private readonly Switch2IrGyroTuning[] mouse = CreateDefaults();
    private readonly Switch2IrGyroTuning[] mouseJoystick = CreateDefaults();

    internal Switch2IrGyroTuning Get(GyroOutMode mode, int triggerIndex)
    {
        if ((uint)triggerIndex >= TriggerCount)
        {
            return Switch2IrGyroTuning.Default;
        }
        return mode == GyroOutMode.MouseJoystick ?
            mouseJoystick[triggerIndex] : mode == GyroOutMode.Mouse ?
                mouse[triggerIndex] : Switch2IrGyroTuning.Default;
    }

    internal static int GetSourceKey(GyroOutMode mode, int triggerIndex)
    {
        if ((uint)triggerIndex >= TriggerCount)
        {
            return -1;
        }
        return mode == GyroOutMode.MouseJoystick ?
            TriggerCount + triggerIndex : mode == GyroOutMode.Mouse ?
                triggerIndex : -1;
    }

    internal bool TrySet(GyroOutMode mode, int triggerIndex,
        in Switch2IrGyroTuning tuning)
    {
        if ((uint)triggerIndex >= TriggerCount || mode is not
            GyroOutMode.Mouse and not GyroOutMode.MouseJoystick)
        {
            return false;
        }
        Switch2IrGyroTuning normalized =
            Switch2IrGyroTuning.Normalize(tuning);
        if (mode == GyroOutMode.MouseJoystick)
        {
            mouseJoystick[triggerIndex] = normalized;
        }
        else
        {
            mouse[triggerIndex] = normalized;
        }
        return true;
    }

    private static Switch2IrGyroTuning[] CreateDefaults()
    {
        var result = new Switch2IrGyroTuning[TriggerCount];
        Array.Fill(result, Switch2IrGyroTuning.Default);
        return result;
    }
}
