/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows
{
    internal readonly record struct DualSenseSpeakerTransportState(
        byte TransportVolume,
        byte PhysicalSpeakerVolume)
    {
        internal static DualSenseSpeakerTransportState Resolve(
            bool speakerEnabled, bool headsetOnly,
            byte configuredSpeakerVolume, bool muteLatched,
            in DualSenseMuteButtonRuntimePolicy muteButtonPolicy)
        {
            // Capture/encoder gain belongs to the continuous media carrier.
            // Muting the physical built-in speaker must not bake silence into
            // that shared stream because USB AUX and Bluetooth replacement
            // sessions consume the same carrier.
            byte physicalSpeakerVolume = !speakerEnabled ? (byte)0 :
                headsetOnly ? configuredSpeakerVolume :
                muteButtonPolicy.ResolveSpeakerVolume(
                    configuredSpeakerVolume, muteLatched);
            return new DualSenseSpeakerTransportState(
                TransportVolume: configuredSpeakerVolume,
                PhysicalSpeakerVolume: physicalSpeakerVolume);
        }
    }

    /// <summary>
    /// Resolves the mutually-exclusive DualSense mute-button modes before
    /// the input report path changes any controller output. Keeping this as
    /// a value makes stale or hand-edited profiles deterministic: the master
    /// input/output mode always wins over profile switching, and target flags
    /// cannot mute anything on their own.
    /// </summary>
    internal readonly record struct DualSenseMuteButtonRuntimePolicy(
        bool InputOutputModeEnabled,
        bool MutesMicrophone,
        bool MutesSpeaker,
        bool SwitchesProfiles,
        bool OverridesMuteLed)
    {
        internal bool HandlesButton => InputOutputModeEnabled ||
            SwitchesProfiles || OverridesMuteLed;

        internal static DualSenseMuteButtonRuntimePolicy Resolve(
            bool inputOutputModeEnabled,
            bool microphoneTargetEnabled,
            bool speakerTargetEnabled,
            bool profileSwitchingEnabled,
            bool legacyMuteLedEnabled)
        {
            return new DualSenseMuteButtonRuntimePolicy(
                InputOutputModeEnabled: inputOutputModeEnabled,
                MutesMicrophone: inputOutputModeEnabled &&
                    microphoneTargetEnabled,
                MutesSpeaker: inputOutputModeEnabled &&
                    speakerTargetEnabled,
                SwitchesProfiles: !inputOutputModeEnabled &&
                    profileSwitchingEnabled,
                // Input/output mode owns the physical indication even when a
                // stale profile has the legacy light checkbox turned off.
                OverridesMuteLed: inputOutputModeEnabled ||
                    legacyMuteLedEnabled);
        }

        internal byte ResolveSpeakerVolume(byte configuredVolume,
            bool muteLatched)
        {
            return MutesSpeaker && muteLatched ? (byte)0 : configuredVolume;
        }

        internal bool CanMuteBuiltInSpeaker(bool controllerAudioEnabled,
            bool headsetOnly)
        {
            return MutesSpeaker && controllerAudioEnabled && !headsetOnly;
        }
    }
}
