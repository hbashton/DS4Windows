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
/// Source-exact connection/identification signature adapted from
/// Switch2Connect commit 61ac6642ce12fe7217e38a860b14863b18ca7e28.
/// Each active phase occupies subframe zero; the two following subframes use
/// the donor's neutral carrier values. Scheduling and physical ownership stay
/// outside this immutable description.
/// </summary>
internal static class Switch2ConnectionHaptic
{
    internal const int UsbInitialDelayMilliseconds = 1_200;
    internal const int BassDurationMilliseconds = 200;
    internal const int NeutralGapMilliseconds = 10;
    internal const int SharpClickDurationMilliseconds = 1_000;

    internal static readonly Switch2HdRumbleSubframe NeutralSubframe = new(
        0x0e1, 0, 0x1e1, 0);

    internal static readonly Switch2HdRumbleSubframe SourceBassSubframe = new(
        0x060, 0x350, 0x0c0, 0x250);

    internal static readonly Switch2HdRumbleSubframe SourceSharpClickSubframe =
        new(
        0x0e1, 0x030, 0x1e2, 0x300);

    // set_vibration(ignore_freq_scaling=True) still applies the donor's 1.3x
    // LF multiplier and Pro 1.0x HF multiplier before 10-bit clamping.
    internal static readonly Switch2HdRumbleSubframe ProBassSubframe = new(
        0x060, 1_023, 0x0c0, 592);

    internal static readonly Switch2HdRumbleSubframe ProSharpClickSubframe =
        new(0x0e1, 62, 0x1e2, 768);

    // Joy-Con uses the same 1.3x LF but a 0.6x HF multiplier, then scales both
    // bands together when their sum exceeds the physical 10-bit budget.
    internal static readonly Switch2HdRumbleSubframe JoyConBassSubframe = new(
        0x060, 759, 0x0c0, 264);

    internal static readonly Switch2HdRumbleSubframe JoyConSharpClickSubframe =
        new(0x0e1, 62, 0x1e2, 460);

    internal static readonly Switch2HdRumbleGroup ProBassGroup = new(
        ProBassSubframe, NeutralSubframe, NeutralSubframe);

    internal static readonly Switch2HdRumbleGroup JoyConBassGroup = new(
        JoyConBassSubframe, NeutralSubframe, NeutralSubframe);

    internal static readonly Switch2HdRumbleGroup ProSharpClickGroup = new(
        ProSharpClickSubframe, NeutralSubframe, NeutralSubframe);

    internal static readonly Switch2HdRumbleGroup JoyConSharpClickGroup = new(
        JoyConSharpClickSubframe, NeutralSubframe, NeutralSubframe);

    internal static readonly ControllerFeedbackActuatorState ProBassMarker =
        CreateMarker(ProBassSubframe);

    internal static readonly ControllerFeedbackActuatorState JoyConBassMarker =
        CreateMarker(JoyConBassSubframe);

    internal static readonly ControllerFeedbackActuatorState
        ProSharpClickMarker = CreateMarker(ProSharpClickSubframe);

    internal static readonly ControllerFeedbackActuatorState
        JoyConSharpClickMarker = CreateMarker(JoyConSharpClickSubframe);

    private static ControllerFeedbackActuatorState CreateMarker(
        in Switch2HdRumbleSubframe subframe) => new(
            ScaleAmplitude(subframe.Oscillator0AmplitudeCode),
            ScaleAmplitude(subframe.Oscillator1AmplitudeCode), 0, 0);

    private static ushort ScaleAmplitude(ushort value) => checked((ushort)(
        ((uint)value * ushort.MaxValue +
            Switch2HdRumbleSubframe.MaximumCode / 2U) /
        Switch2HdRumbleSubframe.MaximumCode));
}
