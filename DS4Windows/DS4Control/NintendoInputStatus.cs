using DS4Windows.Switch2;

namespace DS4Windows;

// Logical Nintendo profile metadata, not a fabricated Switch 2 wire report.
// Values are copied with DS4State; lifecycle generations come from the owner.
internal readonly record struct NintendoInputStatus(
    ushort ContractVersion, Switch2JoyConProfileMode Mode, ulong PairEpoch,
    ulong LeftGeneration, ulong RightGeneration, bool LeftPresent, bool RightPresent,
    long CompletionTimestampQpc, long QpcFrequency,
    Switch2JoyConProfileButton Buttons, Switch2JoyConProfileButton LeftButtons,
    Switch2JoyConProfileButton RightButtons)
{
    internal const ushort CurrentVersion = 1;
    internal bool IsDeclared => ContractVersion != 0;
}

internal static class NintendoProfileInput
{
    internal static double ResolveReportIntervalMilliseconds(DS4State state, double physicalInterval) =>
        TryRead(state, out _) ? double.IsFinite(state.elapsedTime) && state.elapsedTime > 0 &&
            state.elapsedTime <= LegacyJoyConProfileProjection.MaximumSourceAgeSeconds ? state.elapsedTime * 1000.0 : 0.0 : physicalInterval;

    internal static bool TryRead(DS4State state, out NintendoInputStatus input)
    {
        input = state?.NintendoInputStatus ?? default;
        const Switch2JoyConProfileButton unsupported = Switch2JoyConProfileButton.C |
            Switch2JoyConProfileButton.LeftIrSensor | Switch2JoyConProfileButton.RightIrSensor;
        Switch2JoyConProfileButton allButtons = input.Buttons | input.LeftButtons | input.RightButtons;
        if (state == null || state.Switch2RawInputStatus.IsValid ||
            state.Switch2JoyConRawInputStatus.IsValid ||
            input.ContractVersion != NintendoInputStatus.CurrentVersion ||
            input.CompletionTimestampQpc < 0 || input.QpcFrequency <= 0 ||
            (input.LeftPresent != (input.LeftGeneration != 0)) ||
            (input.RightPresent != (input.RightGeneration != 0)) ||
            !Switch2DualGyroConfiguration.IsValidActivationButton(allButtons) ||
            (allButtons & unsupported) != 0) return false;
        return input.Mode switch
        {
            Switch2JoyConProfileMode.Joined => input.PairEpoch != 0 && input.LeftPresent && input.RightPresent,
            Switch2JoyConProfileMode.StandaloneVerticalLeft or Switch2JoyConProfileMode.StandaloneHorizontalLeft =>
                input.PairEpoch == 0 && input.LeftPresent && !input.RightPresent,
            Switch2JoyConProfileMode.StandaloneVerticalRight or Switch2JoyConProfileMode.StandaloneHorizontalRight =>
                input.PairEpoch == 0 && !input.LeftPresent && input.RightPresent,
            _ => false,
        };
    }

    internal static Switch2GyroTriggerSourceIdentity Identity(in NintendoInputStatus input) =>
        new(true, input.PairEpoch, input.LeftGeneration, input.LeftGeneration,
            input.RightGeneration, input.RightGeneration, input.Mode, originalNintendo: true);

    internal static bool TryReadButton(DS4State state, DS4Controls control)
    {
        if (!TryRead(state, out var input)) return false;
        Switch2JoyConProfileButton button = control switch
        {
            DS4Controls.Switch2JoyConLeftSL => Switch2JoyConProfileButton.LeftRailSL,
            DS4Controls.Switch2JoyConLeftSR => Switch2JoyConProfileButton.LeftRailSR,
            DS4Controls.Switch2JoyConRightSL => Switch2JoyConProfileButton.RightRailSL,
            DS4Controls.Switch2JoyConRightSR => Switch2JoyConProfileButton.RightRailSR,
            // Existing sideways profiles use paddle slots for the outer L/ZL/R/ZR.
            DS4Controls.Switch2JoyConLeftPaddle1 => Switch2JoyConProfileButton.LeftPaddle1,
            DS4Controls.Switch2JoyConLeftPaddle2 => Switch2JoyConProfileButton.LeftPaddle2,
            DS4Controls.Switch2JoyConRightPaddle1 => Switch2JoyConProfileButton.RightPaddle1,
            DS4Controls.Switch2JoyConRightPaddle2 => Switch2JoyConProfileButton.RightPaddle2,
            _ => Switch2JoyConProfileButton.None,
        };
        return button != 0 && (input.Buttons & button) != 0;
    }
}
