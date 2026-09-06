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
/// Declares how a four-actuator canonical frame is represented by the two
/// voice-coil groups of a Switch 2 controller. The policy is deliberately
/// narrow: body motors use SDL's licensed compatibility basis and each Xbox
/// impulse-trigger lane is approximated in only the corresponding encoded
/// group. Hardware sidedness remains a physical basis-test gate, and no
/// trigger actuator is claimed to exist on Switch 2 hardware.
/// </summary>
internal enum Switch2HdRumbleFeedbackPolicy : byte
{
    Invalid = 0,
    SideLocalImpulseDualBandSaturating = 1,
    SdlBodyOnlyCompatibility = 2,
}

internal enum Switch2HdRumbleFeedbackFidelity : byte
{
    Invalid = 0,
    SdlLogicalNeutral = 1,
    SdlBodyCompatibility = 2,
    SideLocalImpulseApproximation = 3,
    NativeSwitch2PassThrough = 4,
    DualSensePcmDualBand = 5,
    DualSenseAdaptiveTriggerApproximation = 6,
    NativeSwitch2ProfileEffect = 7,
    NativeSwitch2TestPreview = 8,
}

/// <summary>
/// Per-profile tuning for Xbox impulse-trigger HD-rumble conversion.
/// The closed ranges and dynamic/fixed frequency mapping are adapted from the
/// GPL-3.0 Switch2Connect implementation at commit
/// 61ac6642ce12fe7217e38a860b14863b18ca7e28. DS4Windows retains its existing
/// bounded amplitude basis and sole-writer lifecycle.
/// </summary>
internal readonly struct Switch2HdRumbleImpulseTuning :
    IEquatable<Switch2HdRumbleImpulseTuning>
{
    internal const byte MinimumLevel = 1;
    internal const byte MaximumLevel = 10;
    internal const byte DefaultFixedFrequencyLevel = 10;
    internal const byte DefaultStrengthLevel = 5;

    private Switch2HdRumbleImpulseTuning(bool dynamicFrequency,
        byte fixedFrequencyLevel, byte strengthLevel)
    {
        DynamicFrequency = dynamicFrequency;
        FixedFrequencyLevel = fixedFrequencyLevel;
        StrengthLevel = strengthLevel;
    }

    internal bool DynamicFrequency { get; }

    internal byte FixedFrequencyLevel { get; }

    internal byte StrengthLevel { get; }

    internal bool IsValid => FixedFrequencyLevel is >= MinimumLevel and <=
            MaximumLevel &&
        StrengthLevel is >= MinimumLevel and <= MaximumLevel;

    internal static Switch2HdRumbleImpulseTuning Default => new(
        dynamicFrequency: true, DefaultFixedFrequencyLevel,
        DefaultStrengthLevel);

    internal static bool TryCreate(bool dynamicFrequency,
        int fixedFrequencyLevel, int strengthLevel,
        out Switch2HdRumbleImpulseTuning tuning)
    {
        if (fixedFrequencyLevel is < MinimumLevel or > MaximumLevel ||
            strengthLevel is < MinimumLevel or > MaximumLevel)
        {
            tuning = default;
            return false;
        }
        tuning = new Switch2HdRumbleImpulseTuning(dynamicFrequency,
            (byte)fixedFrequencyLevel, (byte)strengthLevel);
        return true;
    }

    public bool Equals(Switch2HdRumbleImpulseTuning other) =>
        DynamicFrequency == other.DynamicFrequency &&
        FixedFrequencyLevel == other.FixedFrequencyLevel &&
        StrengthLevel == other.StrengthLevel;

    public override bool Equals(object obj) => obj is
        Switch2HdRumbleImpulseTuning other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(DynamicFrequency,
        FixedFrequencyLevel, StrengthLevel);

    public static bool operator ==(Switch2HdRumbleImpulseTuning left,
        Switch2HdRumbleImpulseTuning right) => left.Equals(right);

    public static bool operator !=(Switch2HdRumbleImpulseTuning left,
        Switch2HdRumbleImpulseTuning right) => !left.Equals(right);
}

/// <summary>
/// Per-profile body-rumble gain shared by canonical and source-preserved
/// Switch 2 feedback. The 0..200 percent range is the existing DS4Windows
/// Rumble Boost contract, so profiles retain one strength setting rather than
/// gaining a second controller-specific source of truth. Xbox impulse lanes
/// remain independently tuned because Switch 2 has no trigger actuator and
/// their HD-rumble representation is an explicit approximation.
/// </summary>
internal readonly struct Switch2HdRumbleBodyTuning :
    IEquatable<Switch2HdRumbleBodyTuning>
{
    internal const int MinimumStrengthPercent = 0;
    internal const int MaximumStrengthPercent = 200;
    internal const int DefaultStrengthPercent = 100;
    internal const byte MinimumXboxFrequencyLevel = 1;
    internal const byte MaximumXboxFrequencyLevel = 10;
    internal const byte DefaultXboxFrequencyLevel = 10;

    private readonly bool initialized;

    private Switch2HdRumbleBodyTuning(byte strengthPercent,
        bool xboxCarrierMode, byte xboxFrequencyLevel)
    {
        StrengthPercent = strengthPercent;
        XboxCarrierMode = xboxCarrierMode;
        XboxFrequencyLevel = xboxFrequencyLevel;
        initialized = true;
    }

    internal byte StrengthPercent { get; }

    internal bool XboxCarrierMode { get; }

    internal byte XboxFrequencyLevel { get; }

    internal bool IsValid => initialized &&
        StrengthPercent <= MaximumStrengthPercent &&
        XboxFrequencyLevel is >= MinimumXboxFrequencyLevel and <=
            MaximumXboxFrequencyLevel;

    internal static Switch2HdRumbleBodyTuning Default =>
        new(DefaultStrengthPercent, xboxCarrierMode: false,
            DefaultXboxFrequencyLevel);

    internal static bool TryCreate(int strengthPercent,
        out Switch2HdRumbleBodyTuning tuning) => TryCreate(strengthPercent,
            xboxCarrierMode: false, DefaultXboxFrequencyLevel, out tuning);

    internal static bool TryCreate(int strengthPercent,
        bool xboxCarrierMode, int xboxFrequencyLevel,
        out Switch2HdRumbleBodyTuning tuning)
    {
        if (strengthPercent is < MinimumStrengthPercent or >
                MaximumStrengthPercent ||
            xboxFrequencyLevel is < MinimumXboxFrequencyLevel or >
                MaximumXboxFrequencyLevel)
        {
            tuning = default;
            return false;
        }

        tuning = new Switch2HdRumbleBodyTuning((byte)strengthPercent,
            xboxCarrierMode, (byte)xboxFrequencyLevel);
        return true;
    }

    public bool Equals(Switch2HdRumbleBodyTuning other) =>
        initialized == other.initialized &&
        StrengthPercent == other.StrengthPercent &&
        XboxCarrierMode == other.XboxCarrierMode &&
        XboxFrequencyLevel == other.XboxFrequencyLevel;

    public override bool Equals(object obj) => obj is
        Switch2HdRumbleBodyTuning other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(initialized,
        StrengthPercent, XboxCarrierMode, XboxFrequencyLevel);

    public static bool operator ==(Switch2HdRumbleBodyTuning left,
        Switch2HdRumbleBodyTuning right) => left.Equals(right);

    public static bool operator !=(Switch2HdRumbleBodyTuning left,
        Switch2HdRumbleBodyTuning right) => !left.Equals(right);
}

/// <summary>
/// Immutable synthesis result. Lifecycle and ownership fences are copied from
/// the source frame so a later sole writer cannot combine an old rumble value
/// with a new controller lifetime. This value performs no transport I/O and
/// owns no counter or cadence.
/// </summary>
internal readonly struct Switch2HdRumbleFeedbackSynthesis
{
    internal Switch2HdRumbleFeedbackSynthesis(
        ControllerFeedbackSource source,
        ControllerFeedbackCommand command,
        Switch2HdRumbleFeedbackFidelity fidelity,
        Switch2HdRumbleGroup left, Switch2HdRumbleGroup right,
        ulong sequence, ulong deviceGeneration, ulong transportGeneration,
        ulong ownershipEpoch, ulong timestampMicroseconds,
        ulong timeToLiveMicroseconds)
    {
        Source = source;
        Command = command;
        Fidelity = fidelity;
        Left = left;
        Right = right;
        Sequence = sequence;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        OwnershipEpoch = ownershipEpoch;
        TimestampMicroseconds = timestampMicroseconds;
        TimeToLiveMicroseconds = timeToLiveMicroseconds;
    }

    internal ControllerFeedbackSource Source { get; }

    internal ControllerFeedbackCommand Command { get; }

    internal Switch2HdRumbleFeedbackFidelity Fidelity { get; }

    internal Switch2HdRumbleGroup Left { get; }

    internal Switch2HdRumbleGroup Right { get; }

    internal ulong Sequence { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal ulong OwnershipEpoch { get; }

    internal ulong TimestampMicroseconds { get; }

    internal ulong TimeToLiveMicroseconds { get; }

    internal bool IsStop => Command == ControllerFeedbackCommand.Stop;

    internal bool IsNeutral => Command is ControllerFeedbackCommand.Neutral or
        ControllerFeedbackCommand.Stop;

    /// <summary>
    /// Revalidates source freshness at the actual delivery boundary. A queue
    /// or sole writer must call this immediately before encoding/submission;
    /// successful synthesis is not permission to replay the value later.
    /// </summary>
    internal bool IsFreshAt(ulong nowMicroseconds)
    {
        if (TimestampMicroseconds > nowMicroseconds)
        {
            return TimestampMicroseconds - nowMicroseconds <=
                ControllerFeedbackFrame.MaxFutureSkewMicroseconds;
        }

        return nowMicroseconds - TimestampMicroseconds <
            TimeToLiveMicroseconds;
    }
}

/// <summary>
/// Allocation-free, offline-only canonical-feedback translator. The fixed
/// control codes and conservative amplitude ceiling match the zlib-licensed
/// SDL Switch 2 compatibility implementation at the audited source pin. Each
/// state is repeated through all three wire subframes, matching the sustained
/// fallback used by the GPL-3.0 Switch2Connect implementation at commit
/// 61ac6642ce12fe7217e38a860b14863b18ca7e28 when a source update does not
/// provide three distinct temporal samples. Its trigger rule is an explicit
/// project policy, not a Nintendo wire fact: body-low/body-high are mirrored
/// to both sides, while an impulse-trigger value selects the high-frequency
/// carrier in only its own group. Body-high and impulse amplitudes use bounded
/// soft saturation, retaining overlap headroom without wrapping.
/// The low-frequency field remains the body lane; physical energy still
/// requires measurement.
/// </summary>
internal static class Switch2HdRumbleFeedbackTranslator
{
    // SDL c71abd08605b8bb7078372307a93274725c99fe0,
    // SDL_hidapi_switch2.c:37, 614-615, 1031-1078.
    internal const ushort SdlHighControlCode = 0x0187;
    internal const ushort SdlLowControlCode = 0x0112;
    internal const uint SdlAmplitudeCeiling16 = 29_000;
    internal const ushort MaximumPackedCompatibilityAmplitude =
        (ushort)(SdlAmplitudeCeiling16 >> 6);
    internal const ushort ImpulseHighFrequencyMinimum = 300;
    internal const ushort ImpulseHighFrequencyMaximum = 481;
    internal const ushort MaximumPackedAmplitude = 1023;
    internal const ushort XboxBodyLowControlCode = 0x00E1;
    internal const ushort XboxBodyHighControlMinimum = 241;
    internal const ushort XboxBodyHighControlMaximum = 347;

    internal static bool TryTranslate(in ControllerFeedbackFrame frame,
        ulong nowMicroseconds, Switch2HdRumbleFeedbackPolicy policy,
        out Switch2HdRumbleFeedbackSynthesis synthesis) => TryTranslate(
            frame, nowMicroseconds, policy,
            Switch2HdRumbleImpulseTuning.Default,
            Switch2HdRumbleBodyTuning.Default, out synthesis);

    internal static bool TryTranslate(in ControllerFeedbackFrame frame,
        ulong nowMicroseconds, Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        out Switch2HdRumbleFeedbackSynthesis synthesis) => TryTranslate(
            frame, nowMicroseconds, policy, impulseTuning,
            Switch2HdRumbleBodyTuning.Default, out synthesis);

    internal static bool TryTranslate(in ControllerFeedbackFrame frame,
        ulong nowMicroseconds, Switch2HdRumbleFeedbackPolicy policy,
        in Switch2HdRumbleImpulseTuning impulseTuning,
        in Switch2HdRumbleBodyTuning bodyTuning,
        out Switch2HdRumbleFeedbackSynthesis synthesis)
    {
        synthesis = default;
        if (policy is not (Switch2HdRumbleFeedbackPolicy.
                SideLocalImpulseDualBandSaturating or
                Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility) ||
            !impulseTuning.IsValid || !bodyTuning.IsValid ||
            !frame.HasValidInvariants() || !frame.IsFreshAt(nowMicroseconds))
        {
            return false;
        }

        if (frame.Command is ControllerFeedbackCommand.Neutral or
            ControllerFeedbackCommand.Stop)
        {
            synthesis = Create(frame,
                Switch2HdRumbleFeedbackFidelity.SdlLogicalNeutral,
                CreateCompatibilityGroup(0, 0, bodyTuning),
                CreateCompatibilityGroup(0, 0, bodyTuning));
            return true;
        }

        bool includeImpulse = policy == Switch2HdRumbleFeedbackPolicy.
            SideLocalImpulseDualBandSaturating;
        Switch2HdRumbleGroup left = includeImpulse ?
            CreateCompatibilityGroupWithImpulse(frame.BodyLow,
                frame.BodyHigh, frame.LeftTrigger, impulseTuning,
                bodyTuning) : CreateCompatibilityGroup(frame.BodyLow,
                    frame.BodyHigh, bodyTuning);
        Switch2HdRumbleGroup right = includeImpulse ?
            CreateCompatibilityGroupWithImpulse(frame.BodyLow,
                frame.BodyHigh, frame.RightTrigger, impulseTuning,
                bodyTuning) : CreateCompatibilityGroup(frame.BodyLow,
                    frame.BodyHigh, bodyTuning);
        Switch2HdRumbleFeedbackFidelity fidelity =
            !includeImpulse ||
                frame.LeftTrigger == 0 && frame.RightTrigger == 0 ?
                Switch2HdRumbleFeedbackFidelity.SdlBodyCompatibility :
                Switch2HdRumbleFeedbackFidelity.
                    SideLocalImpulseApproximation;
        synthesis = Create(frame, fidelity, left, right);
        return true;
    }

    internal static ushort ScaleCanonicalAmplitude(ushort amplitude)
    {
        // This intentionally preserves SDL's two integer truncation points:
        // first 0..65535 -> 0..29000, then the packed field takes bits 6..15.
        uint limited = (uint)amplitude * SdlAmplitudeCeiling16 /
            ushort.MaxValue;
        return (ushort)(limited >> 6);
    }

    internal static Switch2HdRumbleGroup CreateCompatibilityGroup(
        ushort lowAmplitude, ushort highAmplitude)
        => CreateCompatibilityGroup(lowAmplitude, highAmplitude,
            Switch2HdRumbleBodyTuning.Default);

    internal static Switch2HdRumbleGroup CreateCompatibilityGroup(
        ushort lowAmplitude, ushort highAmplitude,
        in Switch2HdRumbleBodyTuning bodyTuning)
    {
        Switch2HdRumbleSubframe subframe = CreateCompatibilitySubframe(
            lowAmplitude, highAmplitude, bodyTuning);
        return new Switch2HdRumbleGroup(subframe, subframe, subframe);
    }

    internal static Switch2HdRumbleSubframe CreateCompatibilitySubframe(
        ushort lowAmplitude, ushort highAmplitude) => new(
            SdlHighControlCode, ScaleCanonicalAmplitude(highAmplitude),
            SdlLowControlCode, ScaleCanonicalAmplitude(lowAmplitude));

    internal static Switch2HdRumbleSubframe CreateCompatibilitySubframe(
        ushort lowAmplitude, ushort highAmplitude,
        in Switch2HdRumbleBodyTuning bodyTuning) => new(
            GetBodyHighControlCode(bodyTuning),
            ScalePackedBodyAmplitude(ScaleCanonicalAmplitude(highAmplitude),
                bodyTuning),
            GetBodyLowControlCode(bodyTuning),
            ScalePackedBodyAmplitude(ScaleCanonicalAmplitude(lowAmplitude),
                bodyTuning));

    internal static ushort GetXboxBodyHighControlCode(
        in Switch2HdRumbleBodyTuning tuning)
    {
        if (!tuning.IsValid)
        {
            return 0;
        }

        // Switch2Connect 61ac664, controller.py set_vibration(): ordinary
        // Xbox HF starts at 0x1e1 (481); new_min=(481+1)/2=241 and
        // freq_factor=(level-1)*4/81. Python int() floors the result.
        uint offset = (uint)(tuning.XboxFrequencyLevel -
            Switch2HdRumbleBodyTuning.MinimumXboxFrequencyLevel);
        return (ushort)((XboxBodyHighControlMinimum * 81u +
            960u * offset) / 81u);
    }

    internal static ushort GetBodyHighControlCode(
        in Switch2HdRumbleBodyTuning tuning) => tuning.XboxCarrierMode ?
            GetXboxBodyHighControlCode(tuning) : SdlHighControlCode;

    internal static ushort GetBodyLowControlCode(
        in Switch2HdRumbleBodyTuning tuning) => tuning.XboxCarrierMode ?
            XboxBodyLowControlCode : SdlLowControlCode;

    internal static ushort ScalePackedBodyAmplitude(ushort amplitude,
        in Switch2HdRumbleBodyTuning tuning)
    {
        if (!tuning.IsValid || amplitude > MaximumPackedAmplitude)
        {
            return 0;
        }

        uint scaled = ((uint)amplitude * tuning.StrengthPercent +
            Switch2HdRumbleBodyTuning.DefaultStrengthPercent / 2u) /
            Switch2HdRumbleBodyTuning.DefaultStrengthPercent;
        return (ushort)Math.Min(MaximumPackedAmplitude, scaled);
    }

    internal static Switch2HdRumbleGroup ScaleSourcePreservedGroup(
        in Switch2HdRumbleGroup group,
        in Switch2HdRumbleBodyTuning tuning) => new(
            ScaleSourcePreservedSubframe(group.First, tuning),
            ScaleSourcePreservedSubframe(group.Second, tuning),
            ScaleSourcePreservedSubframe(group.Third, tuning));

    private static Switch2HdRumbleSubframe ScaleSourcePreservedSubframe(
        in Switch2HdRumbleSubframe subframe,
        in Switch2HdRumbleBodyTuning tuning) => new(
            tuning.XboxCarrierMode ? GetXboxBodyHighControlCode(tuning) :
                subframe.Oscillator0ControlCode,
            ScalePackedBodyAmplitude(subframe.Oscillator0AmplitudeCode,
                tuning),
            tuning.XboxCarrierMode ? XboxBodyLowControlCode :
                subframe.Oscillator1ControlCode,
            ScalePackedBodyAmplitude(subframe.Oscillator1AmplitudeCode,
                tuning));

    internal static ushort AddPackedAmplitudesSaturating(ushort first,
        ushort second)
    {
        uint sum = (uint)first + second;
        return (ushort)Math.Min(MaximumPackedAmplitude, sum);
    }

    /// <summary>
    /// Bounded amplitude-code mix: a + b - a*b/fullScale. A lone source is
    /// unchanged; overlap compresses gradually rather than hitting a flat
    /// hard clip. This is presentation tuning, not a physical energy law.
    /// Native passthrough and its explicitly requested strength are untouched.
    /// </summary>
    internal static ushort MixPackedAmplitudesWithHeadroom(ushort first, ushort second)
    {
        uint a = Math.Min(first, MaximumPackedAmplitude);
        uint b = Math.Min(second, MaximumPackedAmplitude);
        return (ushort)(a + b - (a * b + MaximumPackedAmplitude / 2u) / MaximumPackedAmplitude);
    }

    internal static ushort GetImpulseHighFrequency(ushort triggerAmplitude,
        in Switch2HdRumbleImpulseTuning tuning)
    {
        if (triggerAmplitude == 0 || !tuning.IsValid)
        {
            return 0;
        }
        uint position = tuning.DynamicFrequency ?
            Math.Max(1u, ((uint)triggerAmplitude * 100u + 32_767u) /
                ushort.MaxValue) : tuning.FixedFrequencyLevel;
        uint denominator = tuning.DynamicFrequency ? 99u : 9u;
        uint offset = tuning.DynamicFrequency ? position - 1u :
            position - Switch2HdRumbleImpulseTuning.MinimumLevel;
        return (ushort)((ImpulseHighFrequencyMinimum * denominator +
            offset * (ImpulseHighFrequencyMaximum -
                ImpulseHighFrequencyMinimum) + denominator / 2u) /
            denominator);
    }

    internal static ushort ScaleImpulseAmplitude(ushort amplitude,
        in Switch2HdRumbleImpulseTuning tuning)
    {
        if (!tuning.IsValid)
        {
            return 0;
        }
        uint compatibility = ScaleCanonicalAmplitude(amplitude);
        uint scaled = (compatibility * tuning.StrengthLevel +
            Switch2HdRumbleImpulseTuning.DefaultStrengthLevel / 2u) /
            Switch2HdRumbleImpulseTuning.DefaultStrengthLevel;
        return (ushort)Math.Min(MaximumPackedAmplitude, scaled);
    }

    private static Switch2HdRumbleGroup
        CreateCompatibilityGroupWithImpulse(ushort bodyLow,
            ushort bodyHigh, ushort triggerAmplitude,
            in Switch2HdRumbleImpulseTuning tuning,
            in Switch2HdRumbleBodyTuning bodyTuning)
    {
        ushort bodyHighPacked = ScalePackedBodyAmplitude(
            ScaleCanonicalAmplitude(bodyHigh), bodyTuning);
        ushort impulsePacked = ScaleImpulseAmplitude(triggerAmplitude,
            tuning);
        ushort highControl = impulsePacked == 0 ?
            GetBodyHighControlCode(bodyTuning) :
            GetImpulseHighFrequency(triggerAmplitude, tuning);
        var subframe = new Switch2HdRumbleSubframe(highControl,
            MixPackedAmplitudesWithHeadroom(bodyHighPacked, impulsePacked),
            GetBodyLowControlCode(bodyTuning),
            ScalePackedBodyAmplitude(ScaleCanonicalAmplitude(bodyLow),
                bodyTuning));
        return new Switch2HdRumbleGroup(subframe, subframe, subframe);
    }

    private static Switch2HdRumbleFeedbackSynthesis Create(
        in ControllerFeedbackFrame frame,
        Switch2HdRumbleFeedbackFidelity fidelity,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right) =>
        new(frame.Source, frame.Command, fidelity, left, right, frame.Sequence,
            frame.DeviceGeneration, frame.TransportGeneration,
            frame.OwnershipEpoch, frame.TimestampMicroseconds,
            frame.TimeToLiveMicroseconds);
}
