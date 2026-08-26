/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// One complete, value-owned DualSense compositor input. Producers only
    /// publish copies of this value. The physical output owner claims one
    /// coherent version before encoding, so no report can combine fields from
    /// two profile, trigger, lightbar, or native-ownership publications.
    /// </summary>
    internal readonly record struct DualSensePhysicalOutputSnapshot(
        byte HapticPowerLevel,
        bool UseRumble,
        bool UseAccurateRumble,
        DS4ForceFeedbackState RumbleState,
        bool PreviewLightRumbleActive,
        byte PreviewLightRumbleStrength,
        bool PreviewHeavyRumbleActive,
        byte PreviewHeavyRumbleStrength,
        long RumbleGeneration,
        byte HeadphoneVolume,
        byte SpeakerVolume,
        bool HeadsetOnlyAudio,
        byte MicrophoneVolume,
        bool EnableSpeakerOutput,
        DualSenseDevice.TriggerEffectData LeftTrigger,
        DualSenseDevice.TriggerEffectData RightTrigger,
        byte MuteLedByte,
        bool MicrophoneMuteOverride,
        bool MicrophoneMuted,
        bool MuteLedOverride,
        bool MuteLedOn,
        byte ActivePlayerLedMask,
        DS4LightbarState ProfileLightbar,
        bool NativeGameLightbarOwnershipReleased)
    {
        internal static DualSensePhysicalOutputSnapshot Default => new(
            HapticPowerLevel:
                (byte)DualSenseDevice.HapticPowerLevelFriendlyName.Str100,
            UseRumble: true,
            UseAccurateRumble: true,
            RumbleState: default,
            PreviewLightRumbleActive: false,
            PreviewLightRumbleStrength: 0,
            PreviewHeavyRumbleActive: false,
            PreviewHeavyRumbleStrength: 0,
            RumbleGeneration: 0,
            HeadphoneVolume: 128,
            SpeakerVolume: 128,
            HeadsetOnlyAudio: false,
            MicrophoneVolume: 128,
            EnableSpeakerOutput: false,
            LeftTrigger: default,
            RightTrigger: default,
            MuteLedByte: 0,
            MicrophoneMuteOverride: false,
            MicrophoneMuted: false,
            MuteLedOverride: false,
            MuteLedOn: false,
            ActivePlayerLedMask: 0,
            ProfileLightbar: default,
            NativeGameLightbarOwnershipReleased: true);
    }

    /// <summary>
    /// Fixed latest-state mailbox. The monitor protects only a value copy and
    /// version counter; callers signal the compositor after this lock is
    /// released, and the owner performs all I/O after claiming a copy.
    /// </summary>
    internal sealed class DualSensePhysicalOutputStateMailbox
    {
        private readonly object syncRoot = new();
        private DualSensePhysicalOutputSnapshot latest =
            DualSensePhysicalOutputSnapshot.Default;
        private long version = 1;

        internal DualSensePhysicalOutputSnapshot ReadLatest()
        {
            lock (syncRoot)
            {
                return latest;
            }
        }

        internal bool TryClaim(ref long claimedVersion,
            out DualSensePhysicalOutputSnapshot snapshot)
        {
            lock (syncRoot)
            {
                snapshot = latest;
                if (claimedVersion == version)
                {
                    return false;
                }

                claimedVersion = version;
                return true;
            }
        }

        internal bool Publish(in DualSensePhysicalOutputSnapshot snapshot)
        {
            lock (syncRoot)
            {
                if (latest.Equals(snapshot))
                {
                    return false;
                }

                latest = snapshot;
                version++;
                return true;
            }
        }

        internal bool SetHapticPowerLevel(byte value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    HapticPowerLevel = value,
                });
            }
        }

        internal bool SetUseRumble(bool value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with { UseRumble = value });
            }
        }

        internal bool SetUseAccurateRumble(bool value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    UseAccurateRumble = value,
                });
            }
        }

        internal bool SetHapticState(in DS4LightbarState lightbar,
            in DS4ForceFeedbackState rumble,
            out DualSensePhysicalOutputSnapshot snapshot)
        {
            lock (syncRoot)
            {
                snapshot = latest with
                {
                    ProfileLightbar = lightbar,
                    RumbleState = rumble,
                    RumbleGeneration = latest.RumbleGeneration + 1,
                };
                return PublishLocked(snapshot);
            }
        }

        internal bool SetRumbleState(in DS4ForceFeedbackState rumble,
            out DualSensePhysicalOutputSnapshot snapshot)
        {
            lock (syncRoot)
            {
                snapshot = latest with
                {
                    RumbleState = rumble,
                    RumbleGeneration = latest.RumbleGeneration + 1,
                };
                return PublishLocked(snapshot);
            }
        }

        internal bool SetRumbleChannel(bool rightLightFast, byte value,
            out DualSensePhysicalOutputSnapshot snapshot)
        {
            lock (syncRoot)
            {
                DS4ForceFeedbackState rumble = latest.RumbleState;
                if (rightLightFast)
                {
                    rumble.RumbleMotorStrengthRightLightFast = value;
                }
                else
                {
                    rumble.RumbleMotorStrengthLeftHeavySlow = value;
                }
                rumble.RumbleMotorsExplicitlyOff =
                    rumble.RumbleMotorStrengthRightLightFast == 0 &&
                    rumble.RumbleMotorStrengthLeftHeavySlow == 0;
                snapshot = latest with
                {
                    RumbleState = rumble,
                    RumbleGeneration = latest.RumbleGeneration + 1,
                };
                return PublishLocked(snapshot);
            }
        }

        internal bool SetRumblePreview(bool lightActive, byte lightStrength,
            bool heavyActive, byte heavyStrength,
            out DualSensePhysicalOutputSnapshot snapshot)
        {
            lock (syncRoot)
            {
                DS4ForceFeedbackState rumble = latest.RumbleState;
                if (!lightActive && !heavyActive)
                {
                    rumble = default;
                    rumble.RumbleMotorsExplicitlyOff = true;
                }
                snapshot = latest with
                {
                    RumbleState = rumble,
                    PreviewLightRumbleActive = lightActive,
                    PreviewLightRumbleStrength = lightStrength,
                    PreviewHeavyRumbleActive = heavyActive,
                    PreviewHeavyRumbleStrength = heavyStrength,
                    RumbleGeneration = latest.RumbleGeneration + 1,
                };
                return PublishLocked(snapshot);
            }
        }

        internal bool ClearRumblePreview(
            out DualSensePhysicalOutputSnapshot snapshot) =>
            SetRumblePreview(false, 0, false, 0, out snapshot);

        internal bool SetHeadphoneVolume(byte value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    HeadphoneVolume = value,
                });
            }
        }

        internal bool SetSpeakerVolume(byte value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with { SpeakerVolume = value });
            }
        }

        internal bool SetHeadsetOnlyAudio(bool value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    HeadsetOnlyAudio = value,
                });
            }
        }

        internal bool SetMicrophoneVolume(byte value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    MicrophoneVolume = value,
                });
            }
        }

        internal bool SetEnableSpeakerOutput(bool value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    EnableSpeakerOutput = value,
                });
            }
        }

        internal bool SetTrigger(TriggerId trigger,
            in DualSenseDevice.TriggerEffectData value)
        {
            lock (syncRoot)
            {
                DualSensePhysicalOutputSnapshot next = trigger switch
                {
                    TriggerId.LeftTrigger => latest with
                    {
                        LeftTrigger = value,
                    },
                    TriggerId.RightTrigger => latest with
                    {
                        RightTrigger = value,
                    },
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(trigger), "Invalid Trigger Id"),
                };
                return PublishLocked(next);
            }
        }

        internal bool SetMicrophoneMute(bool enabled, bool muted)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    MicrophoneMuteOverride = enabled,
                    MicrophoneMuted = enabled && muted,
                });
            }
        }

        internal bool SetMuteLedOverride(bool enabled, bool ledOn)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    MuteLedOverride = enabled,
                    MuteLedOn = ledOn,
                });
            }
        }

        internal bool SetMuteLedByte(byte value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with { MuteLedByte = value });
            }
        }

        internal bool SetActivePlayerLedMask(byte value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    ActivePlayerLedMask = value,
                });
            }
        }

        internal bool SetProfileLightbar(in DS4LightbarState value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    ProfileLightbar = value,
                });
            }
        }

        internal bool SetNativeGameLightbarOwnershipReleased(bool value)
        {
            lock (syncRoot)
            {
                return PublishLocked(latest with
                {
                    NativeGameLightbarOwnershipReleased = value,
                });
            }
        }

        private bool PublishLocked(
            in DualSensePhysicalOutputSnapshot next)
        {
            if (latest.Equals(next))
            {
                return false;
            }

            latest = next;
            version++;
            return true;
        }
    }
}
