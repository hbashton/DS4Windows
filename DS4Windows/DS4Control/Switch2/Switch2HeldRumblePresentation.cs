namespace DS4Windows.Switch2;

/// <summary>
/// Presents ordinary held body rumble with one active subframe, following
/// SDL USB c71abd08605b8bb7078372307a93274725c99fe0 and SDL BLE
/// d98c5804a9d20b0d96e993741797878c86b8f1e1. BLE retains neutral carrier
/// fields in its tails; USB zeros the unused bytes. We retain our chosen
/// carriers and gain, changing only the two trailing amplitude pairs.
/// This policy does not infer a firmware playback duration or alter cadence.
/// </summary>
internal static class Switch2HeldRumblePresentation
{
    // Apply at the delivery boundary, not in the shared compatibility-group
    // builder: that builder also supplies body samples mixed into rich
    // DualSense feedback. Never erase real temporal samples or impulse lanes.
    internal static bool AppliesTo(in ControllerFeedbackDelivery delivery,
        bool sourcePreserved, bool impulseRelease,
        in Switch2HdRumbleFeedbackSynthesis synthesis) =>
        !sourcePreserved && !impulseRelease &&
        delivery.Frame.LeftTrigger == 0 && delivery.Frame.RightTrigger == 0 &&
        synthesis.Command == ControllerFeedbackCommand.Apply &&
        synthesis.Fidelity == Switch2HdRumbleFeedbackFidelity.SdlBodyCompatibility;

    internal static Switch2HdRumbleFeedbackSynthesis FirstActiveFrame(
        in Switch2HdRumbleFeedbackSynthesis source) => new(
            source.Source, source.Command, source.Fidelity,
            FirstActiveFrame(source.Left), FirstActiveFrame(source.Right),
            source.Sequence, source.DeviceGeneration, source.TransportGeneration,
            source.OwnershipEpoch, source.TimestampMicroseconds, source.TimeToLiveMicroseconds);

    private static Switch2HdRumbleGroup FirstActiveFrame(in Switch2HdRumbleGroup group) =>
        new(group.First, WithoutAmplitude(group.Second), WithoutAmplitude(group.Third));

    private static Switch2HdRumbleSubframe WithoutAmplitude(in Switch2HdRumbleSubframe frame) =>
        new(frame.Oscillator0ControlCode, 0, frame.Oscillator1ControlCode, 0);
}
