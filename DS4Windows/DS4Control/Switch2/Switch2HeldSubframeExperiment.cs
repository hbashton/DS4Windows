using System;

namespace DS4Windows.Switch2;

/// <summary>
/// Private lab A/B only, not a Nintendo protocol correction. Pinned SDL BLE
/// sends zero-amplitude tails, while Switch2Connect also uses three active
/// subframes for held sources. Preserve the current production default and
/// vary only the two trailing amplitude pairs of ordinary button previews.
/// </summary>
internal static class Switch2HeldSubframeExperiment
{
    internal const string EnvironmentVariable = "DS4WINDOWS_SWITCH2_HELD_SUBFRAME_EXPERIMENT";
    internal const string NeutralTailsValue = "neutral-tails";

    // Called only while constructing a physical lifetime. No environment read,
    // configuration change, allocation, or added wait occurs on the input or
    // physical-write path; exact uncertain retries retain one immutable policy.
    internal static bool ReadColdConfiguration(Switch2ControllerModel model,
        Switch2Transport transport, bool joinedPair) => PortableLabContext.IsActive &&
        ShouldEnable(true, Environment.GetEnvironmentVariable(EnvironmentVariable),
            model, transport, joinedPair);

    internal static bool ShouldEnable(bool portableLab, string value,
        Switch2ControllerModel model, Switch2Transport transport, bool joinedPair) =>
        portableLab && value == NeutralTailsValue && !joinedPair &&
        transport == Switch2Transport.BluetoothLe &&
        model is Switch2ControllerModel.JoyCon2Left or Switch2ControllerModel.JoyCon2Right;

    internal static bool AppliesTo(bool enabled, in ControllerFeedbackDelivery delivery,
        bool sourcePreserved, bool impulseRelease, in Switch2HdRumbleFeedbackSynthesis synthesis) =>
        enabled && !sourcePreserved && !impulseRelease &&
        delivery.Origin == ControllerFeedbackPublicationOrigin.TestPreview &&
        delivery.Frame.LeftTrigger == 0 && delivery.Frame.RightTrigger == 0 &&
        synthesis.Command == ControllerFeedbackCommand.Apply &&
        synthesis.Fidelity == Switch2HdRumbleFeedbackFidelity.SdlBodyCompatibility;

    internal static Switch2HdRumbleFeedbackSynthesis NeutralizeTails(
        in Switch2HdRumbleFeedbackSynthesis source) => new(
            source.Source, source.Command, source.Fidelity,
            NeutralizeTails(source.Left), NeutralizeTails(source.Right),
            source.Sequence, source.DeviceGeneration, source.TransportGeneration,
            source.OwnershipEpoch, source.TimestampMicroseconds, source.TimeToLiveMicroseconds);

    private static Switch2HdRumbleGroup NeutralizeTails(in Switch2HdRumbleGroup group) =>
        new(group.First, WithoutAmplitude(group.Second), WithoutAmplitude(group.Third));

    private static Switch2HdRumbleSubframe WithoutAmplitude(in Switch2HdRumbleSubframe frame) =>
        new(frame.Oscillator0ControlCode, 0, frame.Oscillator1ControlCode, 0);
}
