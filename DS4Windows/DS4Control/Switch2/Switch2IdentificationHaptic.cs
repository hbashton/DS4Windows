/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows.Switch2;

/// <summary>
/// Source-exact two-pulse controller-identification signature adapted from
/// Switch2Connect commit 61ac6642ce12fe7217e38a860b14863b18ca7e28,
/// <c>ControllerFrame._on_vibrate_clicked</c>.
/// </summary>
internal static class Switch2IdentificationHaptic
{
    internal const int PulseDurationMilliseconds = 100;
    internal const int PulseGapMilliseconds = 100;

    internal static readonly Switch2HdRumbleSubframe SourcePulseSubframe = new(
        0x0e1, 800, 0x1e1, 800);

    internal static readonly Switch2HdRumbleSubframe ProPulseSubframe = new(
        0x0e1, 1_023, 0x1e1, 800);

    internal static readonly Switch2HdRumbleSubframe JoyConPulseSubframe = new(
        0x0e1, 696, 0x1e1, 327);

    internal static readonly Switch2HdRumbleGroup ProPulseGroup = new(
        ProPulseSubframe, ProPulseSubframe, ProPulseSubframe);

    internal static readonly Switch2HdRumbleGroup JoyConPulseGroup = new(
        JoyConPulseSubframe, JoyConPulseSubframe, JoyConPulseSubframe);

    internal static readonly ControllerFeedbackActuatorState ProMarker = new(
        ScaleAmplitude(1_023), ScaleAmplitude(800), 0, 0);

    internal static readonly ControllerFeedbackActuatorState JoyConMarker = new(
        ScaleAmplitude(696), ScaleAmplitude(327), 0, 0);

    private static ushort ScaleAmplitude(ushort value) => checked((ushort)(
        ((uint)value * ushort.MaxValue +
            Switch2HdRumbleSubframe.MaximumCode / 2U) /
        Switch2HdRumbleSubframe.MaximumCode));
}
