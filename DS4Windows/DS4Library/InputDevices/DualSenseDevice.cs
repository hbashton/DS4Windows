/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DS4Windows;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// USB output validity bits describe fields present in an update. The
    /// Bluetooth media carrier treats several of those bits as edge-triggered
    /// commands, so replaying an unchanged USB field can restart LEDs or
    /// adaptive-trigger programs. Preserve each real state transition while
    /// consuming redundant stateful strobes. Continuous rumble is deliberately
    /// excluded because games may use repeated writes as a keepalive.
    /// </summary>
    internal sealed class DualSenseNativeStateTransitionFilter
    {
        internal sealed class Snapshot
        {
            internal readonly byte[] LatchedState = new byte[47];
            internal byte KnownFlag0;
            internal byte KnownFlag1;
            internal byte KnownFlag2;
            internal bool LedsReleased;
        }

        private readonly byte[] latchedState = new byte[47];
        private byte knownFlag0;
        private byte knownFlag1;
        private byte knownFlag2;
        private bool ledsReleased;

        internal void Reset()
        {
            Array.Clear(latchedState, 0, latchedState.Length);
            knownFlag0 = 0;
            knownFlag1 = 0;
            knownFlag2 = 0;
            ledsReleased = false;
        }

        internal void Capture(Snapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            Buffer.BlockCopy(latchedState, 0, snapshot.LatchedState, 0,
                latchedState.Length);
            snapshot.KnownFlag0 = knownFlag0;
            snapshot.KnownFlag1 = knownFlag1;
            snapshot.KnownFlag2 = knownFlag2;
            snapshot.LedsReleased = ledsReleased;
        }

        internal void Restore(Snapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            Buffer.BlockCopy(snapshot.LatchedState, 0, latchedState, 0,
                latchedState.Length);
            knownFlag0 = snapshot.KnownFlag0;
            knownFlag1 = snapshot.KnownFlag1;
            knownFlag2 = snapshot.KnownFlag2;
            ledsReleased = snapshot.LedsReleased;
        }

        internal void Filter(byte[] report, int stateOffset)
        {
            if (report == null || stateOffset < 0 ||
                stateOffset + latchedState.Length > report.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(stateOffset));
            }

            FilterField(report, stateOffset, 0, 0x04, 10, 11,
                ref knownFlag0);
            FilterField(report, stateOffset, 0, 0x08, 21, 11,
                ref knownFlag0);

            FilterField(report, stateOffset, 1, 0x01, 8, 1,
                ref knownFlag1);
            FilterField(report, stateOffset, 1, 0x02, 9, 1,
                ref knownFlag1);

            const byte releaseLedMask = 0x08;
            int flag1Offset = stateOffset + 1;
            if ((report[flag1Offset] & releaseLedMask) != 0)
            {
                if (ledsReleased)
                {
                    report[flag1Offset] &= unchecked((byte)~releaseLedMask);
                }
                else
                {
                    ledsReleased = true;
                }
            }

            bool forceLedState = ledsReleased;
            bool carriesLedState = (report[flag1Offset] & 0x14) != 0;
            FilterLedField(report, stateOffset, 0x04, 44, 3,
                forceLedState);
            FilterLedField(report, stateOffset, 0x10, 43, 1,
                forceLedState);
            if (carriesLedState)
            {
                ledsReleased = false;
            }
            FilterField(report, stateOffset, 1, 0x20, 39, 1,
                ref knownFlag1);
            FilterField(report, stateOffset, 1, 0x40, 36, 1,
                ref knownFlag1);
            FilterField(report, stateOffset, 1, 0x80, 37, 1,
                ref knownFlag1);
            FilterField(report, stateOffset, 38, 0x01, 42, 1,
                ref knownFlag2);
            FilterField(report, stateOffset, 38, 0x02, 41, 1,
                ref knownFlag2);
        }

        private void FilterLedField(byte[] report, int stateOffset,
            byte validityMask, int payloadOffset, int payloadLength,
            bool forceUpdate)
        {
            int flagOffset = stateOffset + 1;
            if ((report[flagOffset] & validityMask) == 0)
            {
                return;
            }

            bool unchanged = !forceUpdate &&
                (knownFlag1 & validityMask) != 0 &&
                PayloadEquals(report, stateOffset, payloadOffset,
                    payloadLength);
            CopyPayload(report, stateOffset, payloadOffset, payloadLength);
            knownFlag1 |= validityMask;
            if (unchanged)
            {
                report[flagOffset] &= unchecked((byte)~validityMask);
            }
        }

        private void FilterField(byte[] report, int stateOffset,
            int flagRelativeOffset, byte validityMask, int payloadOffset,
            int payloadLength, ref byte knownFlags)
        {
            int flagOffset = stateOffset + flagRelativeOffset;
            if ((report[flagOffset] & validityMask) == 0)
            {
                return;
            }

            bool unchanged = (knownFlags & validityMask) != 0 &&
                PayloadEquals(report, stateOffset, payloadOffset,
                    payloadLength);
            CopyPayload(report, stateOffset, payloadOffset, payloadLength);
            knownFlags |= validityMask;
            if (unchanged)
            {
                report[flagOffset] &= unchecked((byte)~validityMask);
            }
        }

        private bool PayloadEquals(byte[] report, int stateOffset,
            int payloadOffset, int payloadLength)
        {
            for (int index = 0; index < payloadLength; index++)
            {
                if (report[stateOffset + payloadOffset + index] !=
                    latchedState[payloadOffset + index])
                {
                    return false;
                }
            }

            return true;
        }

        private void CopyPayload(byte[] report, int stateOffset,
            int payloadOffset, int payloadLength)
        {
            Buffer.BlockCopy(report, stateOffset + payloadOffset,
                latchedState, payloadOffset, payloadLength);
        }
    }

    public class DualSenseDevice : DS4Device
    {
        public class GyroMouseSensDualSense : GyroMouseSens
        {
            private const double MOUSE_COEFFICIENT = 0.009;
            private const double MOUSE_OFFSET = 0.15;
            private const double SMOOTH_MOUSE_OFFSET = 0.15;

            public GyroMouseSensDualSense() : base()
            {
                mouseCoefficient = MOUSE_COEFFICIENT;
                mouseOffset = MOUSE_OFFSET;
                mouseSmoothOffset = SMOOTH_MOUSE_OFFSET;
            }
        }

        public abstract class InputReportDataBytes
        {
            public const int REPORT_OFFSET = 0;

            public const int REPORT_ID = 0;
            public const int LX = 1;
            public const int LY = 2;
        }

        public class InputReportDataBytesUSB : InputReportDataBytes
        {
        }

        public class InputReportDataBytesBT : InputReportDataBytesUSB
        {
            public new const int REPORT_OFFSET = 2;

            public new const int REPORT_ID = InputReportDataBytes.REPORT_ID;
            public new const int LX = InputReportDataBytes.LX + REPORT_OFFSET;
            public new const int LY = InputReportDataBytes.LY + REPORT_OFFSET;
        }

        public struct TriggerEffectData
        {
            public byte triggerMotorMode;
            public byte triggerStartResistance;
            public byte triggerEffectForce;
            public byte triggerRangeForce;
            public byte triggerNearReleaseStrength;
            public byte triggerNearMiddleStrength;
            public byte triggerPressedStrength;
            public byte triggerActuationFrequency;

            public void ChangeData(TriggerEffects effect, TriggerEffectSettings effectSettings)
            {
                byte start = effectSettings.startValue;
                byte force = effectSettings.maxValue == 0 ? (byte)255 : effectSettings.maxValue;
                byte smallForce = (byte)Math.Max(1, Math.Min(8, (force / 32) + 1));
                byte freq = (byte)Math.Max(1, Math.Min(40, force / 6));

                switch (effect)
                {
                    case TriggerEffects.None:
                        triggerMotorMode = triggerStartResistance = triggerEffectForce =
                            triggerRangeForce = triggerNearReleaseStrength = triggerNearMiddleStrength =
                            triggerPressedStrength = triggerActuationFrequency = 0;
                        break;
                    case TriggerEffects.FullClick:
                        int tempStartResValue = Math.Max((int)effectSettings.maxValue, 0);
                        //Debug.WriteLine(tempStartResValue);
                        triggerMotorMode = 0x02;
                        //triggerStartResistance = 0x94;
                        triggerStartResistance = (byte)(0x94 * (tempStartResValue / 255.0));
                        //triggerEffectForce = 0xB4;
                        triggerEffectForce = (byte)((0xB4 - triggerStartResistance) * (effectSettings.maxValue / 255.0) + triggerStartResistance);
                        //Debug.WriteLine(triggerEffectForce);
                        triggerRangeForce = 0xFF;
                        triggerNearReleaseStrength = 0x00;
                        triggerNearMiddleStrength = 0x00;
                        triggerPressedStrength = 0x00;
                        triggerActuationFrequency = 0x00;
                        break;
                    case TriggerEffects.Rigid:
                        triggerMotorMode = 0x01;
                        triggerStartResistance = 0x00;
                        triggerEffectForce = 0x00;
                        triggerRangeForce = 0x00;
                        triggerNearReleaseStrength = 0x00;
                        triggerNearMiddleStrength = 0x00;
                        triggerPressedStrength = 0x00;
                        triggerActuationFrequency = 0x00;
                        break;
                    case TriggerEffects.Pulse:
                        triggerMotorMode = 0x02;
                        triggerStartResistance = 0x00;
                        triggerEffectForce = 0x00;
                        triggerRangeForce = 0x00;
                        triggerNearReleaseStrength = 0x00;
                        triggerNearMiddleStrength = 0x00;
                        triggerPressedStrength = 0x00;
                        triggerActuationFrequency = 0x00;
                        break;
                    case TriggerEffects.Gamecube:
                        SetRaw(0x02, 144, 160, 255, 0, 0, 0, 0);
                        break;
                    case TriggerEffects.Soft:
                        SetRaw(0x21, 69, 160, 255, 0, 0, 0, 0);
                        break;
                    case TriggerEffects.Hard:
                        SetRaw(0x21, 32, 160, 255, 255, 255, 255, 0);
                        break;
                    case TriggerEffects.VeryHard:
                        SetRaw(0x21, 16, 160, 255, 255, 255, 255, 0);
                        break;
                    case TriggerEffects.Hardest:
                        SetRaw(0x02, start, 255, 255, 255, 255, 255, 0);
                        break;
                    case TriggerEffects.Vibrate:
                        SetRaw(0x26, start, force, freq, 0, 0, 0, 0);
                        break;
                    case TriggerEffects.Choppy:
                        SetRaw(0x21, 2, 39, 33, 39, 38, 2, 0);
                        break;
                    case TriggerEffects.Medium:
                        SetRaw(0x22, 2, 35, 1, 6, 6, 1, 33);
                        break;
                    case TriggerEffects.Resistance:
                        SetResistance(start, smallForce);
                        break;
                    case TriggerEffects.Bow:
                        SetRaw(0x22, BuildTwoPositionMask(start, 8), 0, smallForce, 0, 0, 0, 0);
                        break;
                    case TriggerEffects.Galloping:
                        SetRaw(35, BuildTwoPositionMask(start, 9), 0, 0x08, freq, 0, 0, 0);
                        break;
                    case TriggerEffects.SemiAutomaticGun:
                        SetRaw(0x25, BuildGunPositionMask(start), 0, smallForce, 0, 0, 0, 0);
                        break;
                    case TriggerEffects.AutomaticGun:
                        SetResistance(start, smallForce);
                        triggerMotorMode = 38;
                        triggerActuationFrequency = freq;
                        break;
                    case TriggerEffects.Machine:
                        SetRaw(39, BuildTwoPositionMask(start, 9), 0, smallForce, freq, 0, 0, 0);
                        break;
                    default:
                        break;
                }
            }

            private void SetRaw(byte mode, byte startResistance, byte effectForce, byte rangeForce,
                byte nearReleaseStrength, byte nearMiddleStrength, byte pressedStrength, byte frequency)
            {
                triggerMotorMode = mode;
                triggerStartResistance = startResistance;
                triggerEffectForce = effectForce;
                triggerRangeForce = rangeForce;
                triggerNearReleaseStrength = nearReleaseStrength;
                triggerNearMiddleStrength = nearMiddleStrength;
                triggerPressedStrength = pressedStrength;
                triggerActuationFrequency = frequency;
            }

            public void ChangeRaw(byte mode, byte startResistance, byte effectForce, byte rangeForce,
                byte nearReleaseStrength, byte nearMiddleStrength, byte pressedStrength, byte frequency)
            {
                SetRaw(mode, startResistance, effectForce, rangeForce, nearReleaseStrength,
                    nearMiddleStrength, pressedStrength, frequency);
            }

            private void SetResistance(byte start, byte force)
            {
                if (start > 9) start = 9;
                if (force > 8) force = 8;
                if (force == 0) force = 1;

                byte b = (byte)((force - 1) & 7);
                uint num = 0;
                ushort num2 = 0;
                for (int i = start; i < 10; i++)
                {
                    num |= (uint)(b << (3 * i));
                    num2 |= (ushort)(1 << i);
                }

                triggerMotorMode = 0x21;
                triggerStartResistance = (byte)(num2 & 0xFF);
                triggerEffectForce = (byte)((num2 >> 8) & 0xFF);
                triggerRangeForce = (byte)(num & 0xFF);
                triggerNearReleaseStrength = (byte)((num >> 8) & 0xFF);
                triggerNearMiddleStrength = (byte)((num >> 16) & 0xFF);
                triggerPressedStrength = (byte)((num >> 24) & 0xFF);
                triggerActuationFrequency = 0;
            }

            private byte BuildTwoPositionMask(byte start, int maxEnd)
            {
                int startPos = Math.Min((int)start, 8);
                int endPos = Math.Min(startPos + 2, maxEnd);
                return (byte)((1 << startPos) | (1 << endPos));
            }

            private byte BuildGunPositionMask(byte start)
            {
                int startPos = Math.Max(2, Math.Min((int)start, 7));
                int endPos = Math.Max(startPos + 1, Math.Min(startPos + 1, 8));
                return (byte)((1 << startPos) | (1 << endPos));
            }
        }

        public enum RumbleEmulationMode
        {
            Accurate,
            Legacy,
            Disabled,
            Passthru,
        }
   
        public enum HapticPowerLevelFriendlyName : ushort
        {
            Str100 = 0,
            Str87 = 1,
            Str75 = 2,
            Str62 = 3,
            Str50 = 4,
            Str37 = 5,
            Str25 = 6,
            Str12 = 7,
        }

        public enum DeviceSubType : ushort
        {
            DualSense,
            DSEdge,
        }
        
        private const int BT_REPORT_OFFSET = 2;
        private InputReportDataBytes dataBytes;
        protected new const int BT_OUTPUT_REPORT_LENGTH = 78;
        private new const int BT_INPUT_REPORT_LENGTH = 78;
        private const int USB_INPUT_REPORT_LENGTH = 64;
        private const byte USB_INPUT_REPORT_ID = 0x01;
        protected const int TOUCHPAD_DATA_OFFSET = 33;
        private new const int BATTERY_MAX = 8;

        public new const byte SERIAL_FEATURE_ID = 9;
        public override byte SerialReportID { get => SERIAL_FEATURE_ID; }

        private const byte OUTPUT_REPORT_ID_USB = 0x02;
        private const byte OUTPUT_REPORT_ID_BT = 0x31;
        private const byte OUTPUT_REPORT_ID_DATA = 0x02;
        private new const byte USB_OUTPUT_CHANGE_LENGTH = 48;
        private const int OUTPUT_MIN_COUNT_BT = 20;
        private const byte LED_PLAYER_BAR_TOGGLE = 0x10;
        private const int FEATURE_FIRMWARE_INFO_ID = 0x20;
        private bool timeStampInit = false;
        private uint timeStampPrevious = 0;
        private uint deltaTimeCurrent = 0;
        private readonly DualSenseControllerClockEstimator
            bluetoothControllerClock = new();
        private readonly DualSenseControllerMediaBufferServo
            bluetoothMediaBufferServo = new(Stopwatch.Frequency);
        private long bluetoothLastInputArrivalQpc;
        private long bluetoothLastInputPhasePublishQpc;
        private long bluetoothLastMediaBufferPublishQpc;
        private long bluetoothMediaBufferCadenceRatioBits =
            BitConverter.DoubleToInt64Bits(1.0);
        public double DualSenseControllerClockRatio =>
            bluetoothControllerClock.Ratio;
        public int DualSenseControllerClockCompletedWindows =>
            bluetoothControllerClock.CompletedWindows;
        public bool DualSenseControllerClockStable =>
            bluetoothControllerClock.IsStable;
        internal double DualSenseMediaBufferCadenceRatio =>
            BitConverter.Int64BitsToDouble(Interlocked.Read(
                ref bluetoothMediaBufferCadenceRatioBits));
        internal double DualSenseBluetoothPresentationClockRatio =>
            Math.Clamp(DualSenseControllerClockStable ?
                DualSenseControllerClockRatio : 1.0,
                DualSenseBluetoothAudioPacerScheduler.MinimumRateRatio,
                DualSenseBluetoothAudioPacerScheduler.MaximumRateRatio);
        private bool outputDirty = false;
        private DS4HapticState previousHapticState = new DS4HapticState();
        private long preparedLocalRumbleGeneration;
        private long submittedLocalRumbleGeneration;
        private byte[] outputBTCrc32Head = new byte[] { 0xA2 };
        //private byte outputPendCount = 0;
        private new GyroMouseSensDualSense gyroMouseSensSettings;
        public override GyroMouseSens GyroMouseSensSettings { get => gyroMouseSensSettings; }

        private readonly DualSensePhysicalOutputStateMailbox
            physicalOutputStateMailbox = new();
        private DualSensePhysicalOutputSnapshot activePhysicalOutputState =
            DualSensePhysicalOutputSnapshot.Default;
        private long claimedPhysicalOutputStateVersion;
        private int nativeSessionReleasePending;

        public byte HapticPowerLevel
        {
            get => physicalOutputStateMailbox.ReadLatest().HapticPowerLevel;
            set
            {
                if (physicalOutputStateMailbox.SetHapticPowerLevel(value))
                {
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        public bool UseRumble
        {
            get => physicalOutputStateMailbox.ReadLatest().UseRumble;
            set
            {
                if (physicalOutputStateMailbox.SetUseRumble(value))
                {
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        // Accurate rumble emulation mode requires 2.24 firmware or newer. On official hardware it takes priority over normal/legacy rumble
        public bool UseAccurateRumble
        {
            get => physicalOutputStateMailbox.ReadLatest().UseAccurateRumble;
            set
            {
                if (physicalOutputStateMailbox.SetUseAccurateRumble(value))
                {
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        public byte HeadphoneVolume
        {
            get => physicalOutputStateMailbox.ReadLatest().HeadphoneVolume;
            set
            {
                if (physicalOutputStateMailbox.SetHeadphoneVolume(value))
                {
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        public byte SpeakerVolume
        {
            get => physicalOutputStateMailbox.ReadLatest().SpeakerVolume;
            set
            {
                if (physicalOutputStateMailbox.SetSpeakerVolume(value))
                {
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        public bool HeadsetOnlyAudio
        {
            get => physicalOutputStateMailbox.ReadLatest().HeadsetOnlyAudio;
            set
            {
                if (physicalOutputStateMailbox.SetHeadsetOnlyAudio(value))
                {
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        public byte MicrophoneVolume
        {
            get => physicalOutputStateMailbox.ReadLatest().MicrophoneVolume;
            set
            {
                if (physicalOutputStateMailbox.SetMicrophoneVolume(value))
                {
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        public bool EnableSpeakerOutput
        {
            get => physicalOutputStateMailbox.ReadLatest().EnableSpeakerOutput;
            set
            {
                if (physicalOutputStateMailbox.SetEnableSpeakerOutput(value))
                {
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        private int profileMicrophoneMuteState;
        private uint hwVersion;
        private uint fwVersion;
        private uint updateVersion;
        private DeviceSubType subType = DeviceSubType.DualSense;
        public DeviceSubType SubType => subType;
        public string LastBluetoothHapticsWriteStatus { get; private set; } = "Not attempted";
        public string LastBluetoothMicrophoneWriteStatus { get; private set; } = "Not attempted";
        private const int BluetoothCombinedOutputReportLength = 398;
        private const int BluetoothCombinedStateOffset = 13;
        private const int BluetoothCombinedStateFlag0Offset = BluetoothCombinedStateOffset;
        private const int BluetoothCombinedStateFlag1Offset = BluetoothCombinedStateOffset + 1;
        private const int BluetoothCombinedStateHeadphoneVolumeOffset = BluetoothCombinedStateOffset + 4;
        private const int BluetoothCombinedStateSpeakerVolumeOffset = BluetoothCombinedStateOffset + 5;
        private const int BluetoothCombinedStateMicrophoneVolumeOffset = BluetoothCombinedStateOffset + 6;
        private const int BluetoothCombinedStateAudioControlOffset = BluetoothCombinedStateOffset + 7;
        private const int BluetoothCombinedStateMuteLedOffset = BluetoothCombinedStateOffset + 8;
        private const int BluetoothCombinedStatePowerSaveControlOffset = BluetoothCombinedStateOffset + 9;
        private const int BluetoothCombinedStateAudioControl2Offset = BluetoothCombinedStateOffset + 37;
        private const int BluetoothCombinedHapticsOffset = 76;
        private const int BluetoothCombinedHapticsDataOffset = 78;
        private const int BluetoothCombinedHapticsDataLength = 64;
        private const int BluetoothCombinedSpeakerOffset = 142;
        private const int BluetoothCombinedSpeakerDataOffset = 144;
        private const int BluetoothCombinedSpeakerFrameLength = 200;
        private const int BluetoothCombinedStateLength = 63;
        private const int BluetoothCombinedNativeStateLength = USB_OUTPUT_CHANGE_LENGTH - 1;
        private const byte BluetoothCombinedLowLatencyBufferLength = 16;
        // the native transport's clean Windows speaker and duplex traces keep every 0x36
        // media-lane depth at 0x80. Use the same native contract for both FE
        // and FF reports; cadence remains one frame per 10.667 ms.
        private const byte BluetoothCombinedSpeakerBufferLength = 0x80;
        // The game, not a wall-clock timeout in DS4Windows, owns the end of a
        // native DualSense effect by publishing an explicit silent haptics
        // block. Expiring the newest block between otherwise valid virtual-
        // device callbacks creates audible and tactile holes in sustained
        // effects.
        private const long PersistentBluetoothHapticsExpiryQpc = long.MaxValue;
        // Presented Opus frames refresh this lease on every 10.667 ms tick.
        // The normal idle boundary clears it explicitly; expiry is the
        // fail-safe when a producer thread dies before reaching that boundary.
        private const int BluetoothSpeakerClockPresentedLeaseMilliseconds =
            3000;
        private const int BluetoothAudioPacerStartupRetryMilliseconds = 2000;
        private const int PhysicalRumbleKeepaliveMilliseconds = 4000;
        private const int PhysicalOutputRetryMilliseconds = 100;
        private const int BluetoothInputPhasePublishMilliseconds = 100;
        private const int BluetoothMediaBufferPublishMilliseconds = 50;
        private const uint BluetoothFinalControlWriteTimeoutMilliseconds = 1000;
        private const byte DualSenseSpeakerVolumeMinimum = 0x3D;
        private const byte DualSenseSpeakerVolumeMaximum = 0x64;
        private const byte DualSenseHeadphoneVolumeMaximum = 0x64;
        private const byte DualSenseMicrophoneVolumeMaximum = 0xFF;
        private const byte DualSenseSpeakerPreGain = 0x0A;
        private const byte DualSenseOutputFlag0HeadphoneVolumeEnable = 0x10;
        private const byte DualSenseOutputFlag0SpeakerVolumeEnable = 0x20;
        private const byte DualSenseOutputFlag0MicrophoneVolumeEnable = 0x40;
        private const byte DualSenseOutputFlag0AudioControlEnable = 0x80;
        private const byte DualSenseOutputFlag1MicrophoneMuteLedControlEnable = 0x01;
        private const byte DualSenseOutputFlag1PowerSaveControlEnable = 0x02;
        private const byte DualSenseOutputFlag1AudioControl2Enable = 0x80;
        private const byte DualSensePowerSaveControlMicrophoneMute = 0x10;
        private const int BluetoothCombinedAudioControlFlagsOffset = 4;
        private const int BluetoothMicrophonePayloadOffset = 3;
        private const int BluetoothMicrophonePayloadLength = 71;
        private const int PhysicalOutputCommandCapacity = 64;
        private const int MaximumPhysicalCommandBurst = 8;
        private const int DeviceStatusChargingChanged = 0x01;
        private const int DeviceStatusBatteryChanged = 0x02;
        private const byte BluetoothNormalInputBit = 0x01;
        private const byte BluetoothMicrophoneInputBit = 0x02;
        private const byte BluetoothMicrophoneControlEnable = 0x01;
        // V5 repeats Sony's native wired-DualSense audio snapshot on
        // every 0x36 media report. Byte 0x09 selects the internal speaker
        // contract while retaining the controller's microphone clock.
        private const byte DualSenseAudioControlOutputSpeaker = 0x09;
        private const byte DualSenseAudioControlOutputHeadphones = 0x00;
        private const byte BluetoothCombinedSpeakerPacketType = 0x93;
        private const byte BluetoothCombinedHeadsetPacketType = 0x96;
        private static readonly byte[] DefaultBluetoothCombinedState =
        {
            0xFD, 0xF7, 0x00, 0x00, 0x64, 0x64, 0xFF, 0x09,
            0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x07, 0x00,
            0x00, 0x02, 0x01, 0x00, 0xFF, 0xD7, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        private readonly object bluetoothSpeakerFrameLock = new object();
        private readonly byte[] bluetoothSpeakerFrame =
            new byte[BluetoothCombinedSpeakerFrameLength];
        private bool bluetoothSpeakerFramePending;
        private readonly object bluetoothCombinedSpeakerReportLock = new object();
        private readonly object bluetoothCombinedTransportWriteLock = new object();
        private readonly byte[] latestBluetoothCombinedSpeakerReport =
            new byte[BluetoothCombinedOutputReportLength];
        private readonly byte[] bluetoothCombinedSpeakerWorkingReport =
            new byte[BluetoothCombinedOutputReportLength];
        private readonly byte[] bluetoothCombinedControlCommitReport =
            new byte[BluetoothCombinedOutputReportLength];
        private readonly byte[] bluetoothCombinedTemplateUpdateReport =
            new byte[BluetoothCombinedOutputReportLength];
        private int bluetoothCombinedControlCommitClaimed;
        private int bluetoothCombinedTemplateUpdateClaimed;
        private readonly byte[] bluetoothCombinedGameStateWorkingReport =
            new byte[BluetoothCombinedOutputReportLength];
        private readonly byte[] bluetoothCombinedNativeStateScratch =
            new byte[BluetoothCombinedNativeStateLength];
        private bool bluetoothCombinedSpeakerReportAvailable;
        private long latestBluetoothCombinedSpeakerReportTimestamp;
        private long latestBluetoothCombinedNativeStateTimestamp;
        private long bluetoothCombinedHapticsGeneration;
        private long bluetoothCombinedSubmittedHapticsGeneration;
        private byte bluetoothCombinedSpeakerReportSequence;
        private byte bluetoothCombinedSpeakerPacketSequence;
        private bool bluetoothCombinedSpeakerSequenceInitialized;
        private readonly object bluetoothAudioPacerLock = new object();
        private readonly ManualResetEvent bluetoothAudioPacerOperationsIdle =
            new ManualResetEvent(true);
        private int bluetoothAudioPacerActiveOperations;
        private DualSenseBluetoothAudioPacer bluetoothAudioPacer;
        private string bluetoothAudioPacerLastError = string.Empty;
        private long bluetoothAudioPacerRetryAfterTimestamp;
        private int bluetoothAudioLifecycleTransitioning;
        private readonly object bluetoothAudioRecoveryOwnershipLock = new();
        private readonly ManualResetEvent bluetoothAudioRecoveryWorkerIdle =
            new(true);
        private readonly AutoResetEvent bluetoothAudioRecoveryWake =
            new(false);
        private int bluetoothAudioRecoveryWorkerScheduled;
        private long bluetoothAudioRecoveryWorkerGeneration;
        private long bluetoothSpeakerSessionCounter;
        private long bluetoothActiveSpeakerSession;
        private long bluetoothActiveSpeakerGeneration;
        private long bluetoothSpeakerFramesDropped;
        private long bluetoothCombinedSpeakerReportsWritten;
        private long bluetoothCombinedSpeakerWriteFailures;
        private long bluetoothCombinedSpeakerStaleHapticsSilenced;
        private readonly object bluetoothSpeakerClockClaimLock = new object();
        private long bluetoothSpeakerClockLeaseExpiryTimestamp;
        private long bluetoothSpeakerClockActiveClaim;
        private long bluetoothSpeakerClockNextClaim;
        private long bluetoothCombinedHapticsPairedWrites;
        private long bluetoothCombinedSpeakerFallbackWrites;
        private int bluetoothCombinedOutputTransportEnabled;
        private int bluetoothOutputTransportStopping;
        private int bluetoothMicrophoneStreamingRequested;
        private int bluetoothMicrophoneControlUpdatePending;
        private long bluetoothMicrophoneLastFrameTimestamp;
        private long bluetoothMicrophoneFramesReceived;
        private long bluetoothMicrophoneRequestGeneration;
        private long bluetoothRejectedInputFrames;
        private int bluetoothLastRejectedInputTag = -1;
        private long usbRejectedInputFrames;
        private int usbLastRejectedInputReportId = -1;

        // Physical input publishes only fixed-size observations. These three
        // persistent workers own physical output composition/I/O, audio-clock
        // processing, and microphone callbacks respectively.
        private readonly AutoResetEvent physicalOutputSignal = new(false);
        private readonly AutoResetEvent bluetoothObservationSignal = new(false);
        private readonly AutoResetEvent bluetoothMicrophoneDispatchSignal =
            new(false);
        private readonly AutoResetEvent deviceCommandSignal = new(false);
        private readonly AutoResetEvent physicalLifecycleSignal = new(false);
        private readonly ManualResetEvent physicalLifecycleCompleted =
            new(true);
        private readonly ManualResetEvent physicalWorkerStartCompleted =
            new(true);
        private int physicalWorkerStartTransitioning;
        private int physicalWorkerStartOwnerThreadId;
        private long physicalLifecycleExternalRequestVersion;
        private readonly ViiperLatencyHistogram physicalOutputQueueLatency =
            new();
        private readonly ViiperLatencyHistogram physicalOutputWriteLatency =
            new();
        private readonly ViiperLatencyHistogram physicalMicrophoneDispatchLatency =
            new();
        private readonly ViiperLatencyHistogram physicalReadToReportLatency =
            new();
        private readonly ViiperLatencyHistogram
            physicalReportObservationIntervalLatency =
            new();
        private readonly ViiperLatencyHistogram physicalReadObservationWaitLatency =
            new();
        private readonly ViiperLatencyHistogram physicalReadRearmLatency =
            new();
        private readonly ViiperLatencyHistogram physicalReportCallbackLatency =
            new();
        private readonly ViiperLatencyHistogram physicalReadToReportReturnLatency =
            new();
        private readonly object bluetoothMicrophoneFrameLock = new();
        private readonly object physicalOutputCommandLock = new();
        private readonly byte[][] bluetoothMicrophoneFrameSlots =
            CreateFixedByteBuffers(16, BluetoothMicrophonePayloadLength);
        private readonly byte[] bluetoothMicrophoneDispatchBuffer =
            new byte[BluetoothMicrophonePayloadLength];
        private readonly long[] bluetoothMicrophoneFrameGenerations =
            new long[16];
        private readonly long[] bluetoothMicrophoneFrameArrivalTimestamps =
            new long[16];
        private readonly byte[] bluetoothMicrophoneFrameSequences =
            new byte[16];
        private readonly byte[][] physicalOutputCommandSlots =
            CreateFixedByteBuffers(PhysicalOutputCommandCapacity,
                USB_OUTPUT_CHANGE_LENGTH);
        private readonly byte[] physicalOutputCommandBuffer =
            new byte[USB_OUTPUT_CHANGE_LENGTH];
        private Thread physicalOutputThread;
        private Thread bluetoothObservationThread;
        private Thread bluetoothMicrophoneDispatchThread;
        private Thread deviceCommandThread;
        private Thread physicalLifecycleThread;
        private long physicalOutputGeneration;
        private long physicalOutputRequestedGeneration;
        private long physicalOutputQueuedTimestamp;
        private long physicalOutputKeepaliveDueQpc;
        private long physicalOutputMaximumQueueAgeTicks;
        private long physicalOutputMaximumWriteDurationTicks;
        private int physicalOutputStopRequested = 1;
        private int bluetoothObservationStopRequested = 1;
        private int bluetoothMicrophoneDispatchStopRequested = 1;
        private int deviceCommandStopRequested = 1;
        private int physicalLifecycleShutdownRequested;
        private int physicalLifecycleRemovalRequested;
        private int physicalLifecycleIdleDisconnectRequested;
        private int physicalInputFailureKind;
        private int physicalInputFailureWinError;
        private long physicalInputFailureTimestamp;
        private int deviceStatusNotificationPending;
        private long bluetoothObservationGeneration;
        private long bluetoothObservedGeneration;
        private long bluetoothObservationVersion;
        private int bluetoothObservedControllerTimestamp;
        private long bluetoothObservedInputArrivalQpc;
        private int bluetoothObservedMediaBuffer = -1;
        private int bluetoothObservedMicrophoneSequence = -1;
        private long bluetoothObservedMicrophoneArrivalQpc;
        private long bluetoothObservedMicrophoneGeneration;
        private long bluetoothMicrophoneObservationVersion;
        private int bluetoothMicrophoneFrameHead;
        private int bluetoothMicrophoneFrameCount;
        private long bluetoothMicrophoneFrameDrops;
        private long bluetoothMicrophoneMaximumQueueAgeTicks;
        private int bluetoothMicrophoneLastDispatchedSequence = -1;
        private int physicalOutputCommandHead;
        private int physicalOutputCommandCount;
        private long physicalOutputCommandOverflows;
        // Internal blocking seam used by ownership tests. Production leaves
        // this null; the physical output worker remains the sole caller.
        internal Action PhysicalOutputWriteTestHook;
        internal Action PhysicalOutputFinalizeTestHook;
        internal Func<long, bool> BluetoothOutputRecoveryIterationTestHook;
        internal Action<long> BluetoothOutputRecoveryBeforeWaitTestHook;
        internal Action BluetoothCombinedControlEnqueuedTestHook;

        private enum PhysicalInputFailureKind : byte
        {
            None,
            Crc,
            BluetoothTimeout,
            BluetoothRead,
            UsbTimeout,
            UsbRead,
        }

        public event Action<DualSenseDevice, byte[]> BluetoothMicrophoneOpusFrameReceived;

        public long BluetoothMicrophoneLastFrameTimestamp =>
            Interlocked.Read(ref bluetoothMicrophoneLastFrameTimestamp);

        public long BluetoothMicrophoneFramesReceived =>
            Interlocked.Read(ref bluetoothMicrophoneFramesReceived);

        public long BluetoothMicrophoneFrameDrops =>
            Interlocked.Read(ref bluetoothMicrophoneFrameDrops);

        public long BluetoothMicrophoneMaximumQueueAgeTicks =>
            Interlocked.Read(ref bluetoothMicrophoneMaximumQueueAgeTicks);

        public int BluetoothMicrophoneLastDispatchedSequence =>
            Volatile.Read(ref bluetoothMicrophoneLastDispatchedSequence);

        internal ViiperLatencySnapshot PhysicalOutputQueueLatencySnapshot =>
            physicalOutputQueueLatency.Snapshot();

        internal ViiperLatencySnapshot PhysicalOutputWriteLatencySnapshot =>
            physicalOutputWriteLatency.Snapshot();

        internal ViiperLatencySnapshot PhysicalMicrophoneDispatchLatencySnapshot =>
            physicalMicrophoneDispatchLatency.Snapshot();

        internal ViiperLatencySnapshot PhysicalReadToReportLatencySnapshot =>
            physicalReadToReportLatency.Snapshot();

        internal ViiperLatencySnapshot
            PhysicalReportObservationIntervalLatencySnapshot =>
                physicalReportObservationIntervalLatency.Snapshot();

        internal ViiperLatencySnapshot PhysicalReadObservationWaitLatencySnapshot =>
            physicalReadObservationWaitLatency.Snapshot();

        internal ViiperLatencySnapshot PhysicalReadRearmLatencySnapshot =>
            physicalReadRearmLatency.Snapshot();

        internal ViiperLatencySnapshot PhysicalReportCallbackLatencySnapshot =>
            physicalReportCallbackLatency.Snapshot();

        internal ViiperLatencySnapshot PhysicalReadToReportReturnLatencySnapshot =>
            physicalReadToReportReturnLatency.Snapshot();

        public long BluetoothRejectedInputFrames =>
            Interlocked.Read(ref bluetoothRejectedInputFrames);

        public int BluetoothLastRejectedInputTag =>
            Volatile.Read(ref bluetoothLastRejectedInputTag);

        public long UsbRejectedInputFrames =>
            Interlocked.Read(ref usbRejectedInputFrames);

        public int UsbLastRejectedInputReportId =>
            Volatile.Read(ref usbLastRejectedInputReportId);

        public long BluetoothSpeakerFramesDropped =>
            Interlocked.Read(ref bluetoothSpeakerFramesDropped);
        public long BluetoothCombinedSpeakerReportsWritten =>
            Interlocked.Read(ref bluetoothCombinedSpeakerReportsWritten);
        public long BluetoothCombinedSpeakerWriteFailures =>
            Interlocked.Read(ref bluetoothCombinedSpeakerWriteFailures);
        public long BluetoothRealtimeWriterDroppedReports
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.RejectedReports ?? 0;
                }
            }
        }
        public long BluetoothCombinedSpeakerStaleHapticsSilenced =>
            Interlocked.Read(ref bluetoothCombinedSpeakerStaleHapticsSilenced);
        public long BluetoothCombinedHapticsPairedWrites =>
            Interlocked.Read(ref bluetoothCombinedHapticsPairedWrites);
        public long BluetoothCombinedSpeakerFallbackWrites =>
            Interlocked.Read(ref bluetoothCombinedSpeakerFallbackWrites);
        public int PendingBluetoothSpeakerFrames
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    if (bluetoothAudioPacer?.IsRunning == true)
                    {
                        return bluetoothAudioPacer.QueuedFrames;
                    }
                }

                lock (bluetoothSpeakerFrameLock)
                {
                    return bluetoothSpeakerFramePending ? 1 : 0;
                }
            }
        }
        public bool BluetoothAudioPacerActive
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.IsRunning == true;
                }
            }
        }
        internal bool BluetoothAudioPacerRecoveryRequired
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer != null &&
                        !bluetoothAudioPacer.IsRunning;
                }
            }
        }
        internal bool BluetoothAudioLifecycleTransitioning =>
            Volatile.Read(ref bluetoothAudioLifecycleTransitioning) != 0;
        public long BluetoothAudioPacerPresentedReports
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.PresentedReports ?? 0;
                }
            }
        }

        public long BluetoothAudioPacerLatePresentations
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.LatePresentationCount ?? 0;
                }
            }
        }

        public double BluetoothAudioPacerMaximumPresentationGapMilliseconds
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        MaximumPresentationGapMilliseconds ?? 0.0;
                }
            }
        }
        public long BluetoothAudioPacerRejectedReports
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.RejectedReports ?? 0;
                }
            }
        }
        public long BluetoothAudioPacerInFlightLimitWaits
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperInFlightLimitWaitCount ?? 0;
                }
            }
        }
        public long BluetoothAudioPacerInFlightLimitEscapes
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperInFlightLimitEscapeCount ?? 0;
                }
            }
        }
        public double BluetoothAudioPacerMaximumInFlightLimitWaitMilliseconds
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperMaximumInFlightLimitWaitMilliseconds ?? 0.0;
                }
            }
        }
        public long BluetoothAudioPacerMaximumAudioPendingBeforeSubmission
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperMaximumAudioPendingBeforeSubmission ?? 0;
                }
            }
        }
        public long BluetoothAudioPacerShallowAudioSubmissions
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperShallowAudioSubmissionCount ?? 0;
                }
            }
        }
        public long BluetoothAudioPacerFullAudioSubmissions
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperFullAudioSubmissionCount ?? 0;
                }
            }
        }
        public long BluetoothAudioPacerCompletedWrites
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperCompletedWriteCount ?? 0;
                }
            }
        }
        public long BluetoothAudioPacerSlowCompletions
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperSlowCompletionCount ?? 0;
                }
            }
        }
        public double BluetoothAudioPacerMaximumCompletionMilliseconds
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperMaximumCompletionMilliseconds ?? 0.0;
                }
            }
        }
        public long BluetoothAudioPacerLateSubmissions
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperLateSubmissionCount ?? 0;
                }
            }
        }
        public double BluetoothAudioPacerMaximumSubmissionGapMilliseconds
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperMaximumSubmissionGapMilliseconds ?? 0.0;
                }
            }
        }

        public long BluetoothAudioPacerSlowNativeSubmissions
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperSlowNativeSubmissionCount ?? 0;
                }
            }
        }

        public double BluetoothAudioPacerMaximumNativeSubmissionMilliseconds
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperMaximumNativeSubmissionMilliseconds ?? 0.0;
                }
            }
        }

        public long BluetoothAudioPacerRealtimeHapticsQueueDepth
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperRealtimeHapticsQueueDepth ?? 0;
                }
            }
        }

        public long BluetoothAudioPacerRealtimeHapticsQueueHighWater
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperRealtimeHapticsQueueHighWater ?? 0;
                }
            }
        }

        public double BluetoothAudioPacerRealtimeHapticsMaximumQueueAgeMilliseconds
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperRealtimeHapticsMaximumQueueAgeMilliseconds ?? 0.0;
                }
            }
        }

        public long BluetoothAudioPacerRealtimeHapticsPresentedCount
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.
                        HelperRealtimeHapticsPresentedCount ?? 0;
                }
            }
        }
        public string BluetoothAudioPacerLastError
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.LastError ??
                        bluetoothAudioPacerLastError;
                }
            }
        }
        public long BluetoothRealtimeWriterCompletedReports
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperCompletedWriteCount ?? 0;
                }
            }
        }
        public long BluetoothRealtimeWriterSlowCompletionCount
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperSlowCompletionCount ?? 0;
                }
            }
        }
        public long BluetoothRealtimeWriterLateSubmissionCount
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperLateSubmissionCount ?? 0;
                }
            }
        }
        public double BluetoothRealtimeWriterMaximumCompletionMilliseconds
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperMaximumCompletionMilliseconds ?? 0.0;
                }
            }
        }
        public double BluetoothRealtimeWriterMaximumSubmissionGapMilliseconds
        {
            get
            {
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.HelperMaximumSubmissionGapMilliseconds ?? 0.0;
                }
            }
        }

        /// <summary>
        /// True while the physical controller's audio plane is owned by the
        /// vDS-compatible combined Bluetooth transport. DS4Windows can seed the
        /// exact same report shape even when the virtual controller is not a
        /// DualSense, so speaker, microphone, haptics, and state never compete
        /// through legacy report IDs.
        /// </summary>
        public bool BluetoothCombinedOutputTransportEnabled =>
            Volatile.Read(ref bluetoothCombinedOutputTransportEnabled) != 0;

        internal static bool RequiresUnifiedBluetoothOutputTransport(
            ConnectionType connectionType)
        {
            return connectionType == ConnectionType.BT;
        }

        public bool EnsureBluetoothCombinedOutputTransport()
        {
            if (conType != ConnectionType.BT)
            {
                LastBluetoothHapticsWriteStatus =
                    $"Rejected: controller connection type is {conType}, not Bluetooth.";
                return false;
            }

            lock (bluetoothCombinedSpeakerReportLock)
            {
                if (!bluetoothCombinedSpeakerReportAvailable)
                {
                    byte[] baseline = BuildBluetoothCombinedControlReport(
                        bluetoothCombinedSpeakerReportSequence,
                        bluetoothCombinedSpeakerPacketSequence,
                        Volatile.Read(ref bluetoothMicrophoneStreamingRequested) != 0);
                    Array.Copy(baseline, latestBluetoothCombinedSpeakerReport,
                        baseline.Length);
                    bluetoothCombinedSpeakerReportAvailable = true;
                    latestBluetoothCombinedSpeakerReportTimestamp = 0;
                    latestBluetoothCombinedNativeStateTimestamp = 0;
                    Array.Clear(bluetoothCombinedNativeStateScratch, 0,
                        bluetoothCombinedNativeStateScratch.Length);
                    bluetoothCombinedHapticsGeneration = 0;
                    bluetoothCombinedSubmittedHapticsGeneration = 0;
                    bluetoothCombinedSpeakerSequenceInitialized = true;
                }
            }

            Interlocked.Exchange(ref bluetoothCombinedOutputTransportEnabled, 1);
            return true;
        }

        private bool InitializeUnifiedBluetoothOutputTransport(
            string reportDescription)
        {
            if (!RequiresUnifiedBluetoothOutputTransport(conType) ||
                !EnsureBluetoothCombinedOutputTransport())
            {
                return false;
            }

            if (!PrepareBluetoothSpeakerClockTransport(
                    useV5PresentationCadence: true))
            {
                RequestUnifiedBluetoothOutputTransportRecovery();
                return false;
            }

            bool committed = TryWriteCachedBluetoothCombinedControlReport(
                includeNativeHaptics: false,
                reportDescription: reportDescription,
                waitForCompletion: true);
            if (!committed)
            {
                RequestUnifiedBluetoothOutputTransportRecovery();
            }

            return committed;
        }

        /// <summary>
        /// Queues one fixed-size Opus frame and submits it on the speaker
        /// clock. The newest VIIPER haptics snapshot is merged into that same
        /// packet without allowing its arrival cadence to stall speaker audio.
        /// </summary>
        public bool SetBluetoothSpeakerAudioFrame(byte[] frame, int length)
        {
            return SetBluetoothSpeakerAudioFrame(frame, length,
                speakerSession: 0, speakerGeneration: 0);
        }

        internal bool SetBluetoothSpeakerAudioFrame(byte[] frame, int length,
            long speakerSession, long speakerGeneration)
        {
            return SetBluetoothSpeakerAudioFrame(frame, length,
                speakerSession, speakerGeneration,
                synchronizedHaptics: null, synchronizedHapticsOffset: 0);
        }

        /// <summary>
        /// Queues one encoded speaker generation with the exact ordered
        /// advanced-haptics interval selected by the V5 media reframer. The
        /// two lanes are copied under the unified transport lock so a newer
        /// source callback cannot replace a brief haptics pulse before this
        /// report reaches the physical FIFO.
        /// </summary>
        internal bool SetBluetoothSpeakerAudioFrame(byte[] frame, int length,
            long speakerSession, long speakerGeneration,
            byte[] synchronizedHaptics, int synchronizedHapticsOffset)
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            if (frame == null || length <= 0)
            {
                return false;
            }

            if (synchronizedHaptics != null &&
                (synchronizedHapticsOffset < 0 ||
                    synchronizedHapticsOffset +
                        BluetoothCombinedHapticsDataLength >
                            synchronizedHaptics.Length))
            {
                return false;
            }

            // StopOutputUpdate owns the reverse handoff once this flag is set.
            // A producer callback can already be in flight when the physical
            // input thread detects removal, so checking only before the
            // transport lock would still allow it to restart the helper after
            // the final microphone-control write.
            if (Volatile.Read(ref bluetoothOutputTransportStopping) != 0)
            {
                Interlocked.Increment(ref bluetoothSpeakerFramesDropped);
                return false;
            }

            if (!EnsureBluetoothCombinedOutputTransport())
            {
                Interlocked.Increment(ref bluetoothSpeakerFramesDropped);
                return false;
            }

            lock (bluetoothCombinedTransportWriteLock)
            {
                if (Volatile.Read(ref bluetoothOutputTransportStopping) != 0)
                {
                    Interlocked.Increment(ref bluetoothSpeakerFramesDropped);
                    return false;
                }

                if (Volatile.Read(
                        ref bluetoothAudioLifecycleTransitioning) != 0)
                {
                    return false;
                }

                if (speakerSession != 0 &&
                    bluetoothActiveSpeakerSession != speakerSession)
                {
                    return false;
                }

                if (synchronizedHaptics != null)
                {
                    lock (bluetoothCombinedSpeakerReportLock)
                    {
                        latestBluetoothCombinedSpeakerReport[
                            BluetoothCombinedHapticsOffset] = 0x92;
                        latestBluetoothCombinedSpeakerReport[
                            BluetoothCombinedHapticsOffset + 1] =
                            BluetoothCombinedHapticsDataLength;
                        Buffer.BlockCopy(synchronizedHaptics,
                            synchronizedHapticsOffset,
                            latestBluetoothCombinedSpeakerReport,
                            BluetoothCombinedHapticsDataOffset,
                            BluetoothCombinedHapticsDataLength);
                        latestBluetoothCombinedSpeakerReportTimestamp =
                            Stopwatch.GetTimestamp();
                        bluetoothCombinedHapticsGeneration++;
                    }
                }

                lock (bluetoothSpeakerFrameLock)
                {
                    if (bluetoothSpeakerFramePending)
                    {
                        Interlocked.Increment(ref bluetoothSpeakerFramesDropped);
                    }

                    Array.Clear(bluetoothSpeakerFrame, 0,
                        bluetoothSpeakerFrame.Length);
                    int bytesToCopy = Math.Min(Math.Min(length, frame.Length),
                        BluetoothCombinedSpeakerFrameLength);
                    Array.Copy(frame, 0, bluetoothSpeakerFrame, 0, bytesToCopy);
                    bluetoothSpeakerFramePending = true;
                }

                bool hapticsSynchronized =
                    HasPendingBluetoothCombinedHaptics();
                bool written = TryWriteCachedBluetoothCombinedSpeakerReportCore(
                    hapticsSynchronized, outputState);
                if (written)
                {
                    // The active/idle decision is serialized by this transport
                    // lock, so publish/refresh the clock lease only after the
                    // report was actually accepted. A failed later frame keeps
                    // the lease earned by the previous accepted frame; a failed
                    // first frame can never create a false active generation.
                    ClaimBluetoothSpeakerClock(outputState,
                        BluetoothSpeakerClockPresentedLeaseMilliseconds);
                    if (speakerSession != 0)
                    {
                        bluetoothActiveSpeakerGeneration = speakerGeneration;
                    }
                }

                return written;
            }
        }

        internal long CreateBluetoothSpeakerSession()
        {
            long session = Interlocked.Increment(
                ref bluetoothSpeakerSessionCounter);
            return session == 0 ? Interlocked.Increment(
                ref bluetoothSpeakerSessionCounter) : session;
        }

        internal bool ActivateBluetoothSpeakerSession(long speakerSession)
        {
            if (speakerSession == 0)
            {
                return false;
            }

            lock (bluetoothCombinedTransportWriteLock)
            {
                if (speakerSession < bluetoothActiveSpeakerSession ||
                    Volatile.Read(ref bluetoothOutputTransportStopping) != 0)
                {
                    return false;
                }

                bluetoothActiveSpeakerSession = speakerSession;
                bluetoothActiveSpeakerGeneration = 0;
                return true;
            }
        }
        internal bool RearmBluetoothHeadsetOutputRoute()
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            if (conType != ConnectionType.BT ||
                !outputState.EnableSpeakerOutput)
            {
                return true;
            }

            if (!EnsureBluetoothCombinedOutputTransport())
            {
                return false;
            }

            // Route changes are state updates on the one long-lived V5
            // transport. Claim only the cached-state lock here; capacity and
            // completion waits occur after it is released.
            lock (bluetoothCombinedSpeakerReportLock)
            {
                ApplyBluetoothSpeakerVolumeAndRoutingCore(
                    latestBluetoothCombinedSpeakerReport,
                    outputState.SpeakerVolume,
                    outputState.HeadsetOnlyAudio,
                    outputState.HeadphoneVolume);
            }

            bool published;
            if (IsBluetoothSpeakerClockActive())
            {
                published = RefreshBluetoothAudioPacerTemplateFromCache(
                    outputState, waitForCapacity: true);
            }
            else
            {
                published = TryWriteCachedBluetoothCombinedControlReport(
                    includeNativeHaptics: true,
                    reportDescription: outputState.HeadsetOnlyAudio ?
                        "AUX route" : "speaker route",
                    waitForCompletion: true,
                    allowDuringStopping: false,
                    outputState: outputState);
            }
            if (!published)
            {
                RequestUnifiedBluetoothOutputTransportRecovery();
            }

            return published;
        }

        internal bool BeginBluetoothAtomicSpeakerFrame(long speakerSession)
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            lock (bluetoothCombinedTransportWriteLock)
            {
                if (speakerSession == 0 ||
                    bluetoothActiveSpeakerSession != speakerSession ||
                    conType != ConnectionType.BT ||
                    !outputState.EnableSpeakerOutput ||
                    Volatile.Read(ref bluetoothOutputTransportStopping) != 0 ||
                    Volatile.Read(ref bluetoothAudioLifecycleTransitioning) != 0)
                {
                    return false;
                }

                // The paired haptics update follows before the PCM callback
                // releases its generation claim. Claiming here makes that update
                // template-only, so the first haptics and speaker data cannot
                // be presented as competing physical HID reports.
                return ClaimBluetoothSpeakerClock(outputState,
                    BluetoothSpeakerClockPresentedLeaseMilliseconds) != 0;
            }
        }

        internal bool EndBluetoothSpeakerGeneration(long speakerSession,
            long speakerGeneration)
        {
            bool clear;
            lock (bluetoothCombinedTransportWriteLock)
            {
                if (speakerSession == 0 || speakerGeneration == 0 ||
                    bluetoothActiveSpeakerSession != speakerSession ||
                    bluetoothActiveSpeakerGeneration != speakerGeneration)
                {
                    return false;
                }

                bluetoothActiveSpeakerGeneration = 0;
                clear = true;
            }
            if (clear)
            {
                ClearBluetoothSpeakerAudioFrame();
            }
            return true;
        }

        internal bool ResetBluetoothSpeakerSession(long speakerSession)
        {
            bool clear;
            lock (bluetoothCombinedTransportWriteLock)
            {
                if (speakerSession == 0 ||
                    bluetoothActiveSpeakerSession != speakerSession)
                {
                    return false;
                }

                bluetoothActiveSpeakerGeneration = 0;
                clear = true;
            }
            if (clear)
            {
                ClearBluetoothSpeakerAudioFrame();
            }
            return true;
        }

        /// <summary>
        /// Drops cached speaker data so an old Opus frame cannot be replayed
        /// after speaker output stops or its capture source changes.
        /// </summary>
        public void ClearBluetoothSpeakerAudioFrame()
        {
            lock (bluetoothSpeakerFrameLock)
            {
                bluetoothSpeakerFramePending = false;
            }

            ClearBluetoothAudioPacerLocked();
            lock (bluetoothSpeakerClockClaimLock)
            {
                bluetoothSpeakerClockActiveClaim = 0;
                bluetoothSpeakerClockLeaseExpiryTimestamp = 0;
            }
            if (Volatile.Read(
                    ref bluetoothMicrophoneControlUpdatePending) != 0 &&
                BluetoothCombinedOutputTransportEnabled &&
                Volatile.Read(ref bluetoothOutputTransportStopping) == 0)
            {
                TryWriteCachedBluetoothCombinedControlReport(
                    includeNativeHaptics: true,
                    reportDescription:
                        "speaker-boundary microphone control",
                    waitForCompletion: true);
            }
        }

        private long ClaimBluetoothSpeakerClock(
            in DualSensePhysicalOutputSnapshot outputState,
            int leaseMilliseconds)
        {
            if (conType != ConnectionType.BT ||
                !outputState.EnableSpeakerOutput ||
                Volatile.Read(ref bluetoothOutputTransportStopping) != 0)
            {
                return 0;
            }

            lock (bluetoothSpeakerClockClaimLock)
            {
                long claim = Interlocked.Increment(
                    ref bluetoothSpeakerClockNextClaim);
                if (claim == 0)
                {
                    claim = Interlocked.Increment(
                        ref bluetoothSpeakerClockNextClaim);
                }

                bluetoothSpeakerClockActiveClaim = claim;
                bluetoothSpeakerClockLeaseExpiryTimestamp =
                    Stopwatch.GetTimestamp() + Math.Max(1,
                        Stopwatch.Frequency * leaseMilliseconds / 1000);
                return claim;
            }
        }

        /// <summary>
        /// Performs the isolated-writer ownership handoff without holding the
        /// combined report lock across process/OVERLAPPED waits. Callers gate
        /// speaker source consumption while this method runs on the dedicated
        /// lifecycle thread.
        /// </summary>
        internal bool PrepareBluetoothSpeakerClockTransport(
            bool useV5PresentationCadence = false)
        {
            return TransitionBluetoothSpeakerClockTransport(
                ignoreRetryCooldown: false,
                useV5PresentationCadence,
                allowDuringStopping: false);
        }

        internal bool RecoverBluetoothSpeakerClockTransport(
            bool useV5PresentationCadence = false)
        {
            return TransitionBluetoothSpeakerClockTransport(
                ignoreRetryCooldown: true,
                useV5PresentationCadence,
                allowDuringStopping: false);
        }

        private bool RecoverBluetoothOutputTransportForShutdown()
        {
            return TransitionBluetoothSpeakerClockTransport(
                ignoreRetryCooldown: true,
                useV5PresentationCadence: true,
                allowDuringStopping: true);
        }

        private bool TransitionBluetoothSpeakerClockTransport(
            bool ignoreRetryCooldown,
            bool useV5PresentationCadence,
            bool allowDuringStopping)
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            if (conType != ConnectionType.BT ||
                (!allowDuringStopping && Volatile.Read(
                    ref bluetoothOutputTransportStopping) != 0))
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref bluetoothAudioLifecycleTransitioning, 1, 0) != 0)
            {
                return false;
            }

            byte[] initialTemplate =
                new byte[BluetoothCombinedOutputReportLength];
            long initialHapticsExpiry;
            DualSenseBluetoothAudioPacer retiringPacer = null;
            DualSenseBluetoothAudioPacer candidate = null;
            try
            {
                if ((!allowDuringStopping && Volatile.Read(
                        ref bluetoothOutputTransportStopping) != 0) ||
                    !EnsureBluetoothCombinedOutputTransport())
                {
                    return false;
                }

                lock (bluetoothCombinedTransportWriteLock)
                {
                    if ((!allowDuringStopping && Volatile.Read(
                            ref bluetoothOutputTransportStopping) != 0))
                    {
                        return false;
                    }

                    lock (bluetoothAudioPacerLock)
                    {
                        if (bluetoothAudioPacer?.IsRunning == true &&
                            (!useV5PresentationCadence ||
                                bluetoothAudioPacer.
                                    UsesV5PresentationCadence))
                        {
                            return true;
                        }

                        if (!ignoreRetryCooldown &&
                            Volatile.Read(
                                ref bluetoothAudioPacerRetryAfterTimestamp) >
                                Stopwatch.GetTimestamp())
                        {
                            return false;
                        }

                        retiringPacer = bluetoothAudioPacer;
                        bluetoothAudioPacer = null;
                    }

                    lock (bluetoothCombinedSpeakerReportLock)
                    {
                        if (bluetoothCombinedSpeakerReportAvailable)
                        {
                            Array.Copy(latestBluetoothCombinedSpeakerReport,
                                initialTemplate, initialTemplate.Length);
                        }

                        initialHapticsExpiry =
                            bluetoothCombinedSpeakerReportAvailable ?
                                PersistentBluetoothHapticsExpiryQpc : 0;
                    }
                }

                if (retiringPacer != null)
                {
                    bluetoothAudioPacerOperationsIdle.WaitOne();
                    bluetoothAudioPacerLastError = retiringPacer.LastError;
                    retiringPacer.Stop();
                    retiringPacer.Dispose();
                    retiringPacer = null;
                }

                // A stale cached speaker lane must not survive into the helper
                // template. Speaker reports provide their own lane.
                Array.Clear(initialTemplate, BluetoothCombinedSpeakerOffset,
                    BluetoothCombinedOutputReportLength - sizeof(uint) -
                        BluetoothCombinedSpeakerOffset);
                ApplyBluetoothSpeakerVolumeAndRoutingCore(initialTemplate,
                    outputState.SpeakerVolume,
                    outputState.HeadsetOnlyAudio,
                    outputState.HeadphoneVolume);
                ApplyBluetoothMicrophoneStreamingRequest(initialTemplate,
                    outputState);
                if (!DualSenseBluetoothAudioPacer.TryStart(
                    hDevice?.DevicePath, initialTemplate,
                    initialHapticsExpiry, useV5PresentationCadence,
                    out candidate, out string error))
                {
                    bluetoothAudioPacerLastError = error ?? string.Empty;
                    Volatile.Write(ref bluetoothAudioPacerRetryAfterTimestamp,
                        Stopwatch.GetTimestamp() + Stopwatch.Frequency *
                            BluetoothAudioPacerStartupRetryMilliseconds / 1000);
                    return false;
                }

                candidate.UpdateCadenceRatio(
                    DualSenseControllerClockStable ?
                        DualSenseControllerClockRatio : 1.0,
                    Volatile.Read(ref bluetoothLastInputArrivalQpc));

                lock (bluetoothCombinedTransportWriteLock)
                {
                    if (!allowDuringStopping && Volatile.Read(
                            ref bluetoothOutputTransportStopping) != 0)
                    {
                        return false;
                    }

                    lock (bluetoothAudioPacerLock)
                    {
                        if (bluetoothAudioPacer != null)
                        {
                            return false;
                        }
                        bluetoothAudioPacer = candidate;
                        candidate = null;
                    }

                    bluetoothAudioPacerLastError = string.Empty;
                    Volatile.Write(ref bluetoothAudioPacerRetryAfterTimestamp, 0);
                }

                return true;
            }
            finally
            {
                if (retiringPacer != null)
                {
                    bluetoothAudioPacerOperationsIdle.WaitOne();
                    retiringPacer.Stop();
                    retiringPacer.Dispose();
                }
                if (candidate != null)
                {
                    candidate.Stop();
                    candidate.Dispose();
                }

                Volatile.Write(ref bluetoothAudioLifecycleTransitioning, 0);
            }
        }

        private void RequestUnifiedBluetoothOutputTransportRecovery()
        {
            if (!RequiresUnifiedBluetoothOutputTransport(conType))
            {
                return;
            }

            long workerGeneration = Volatile.Read(
                ref physicalOutputGeneration);
            lock (bluetoothAudioRecoveryOwnershipLock)
            {
                if (Volatile.Read(ref bluetoothOutputTransportStopping) != 0 ||
                    workerGeneration != Volatile.Read(
                        ref physicalOutputGeneration) ||
                    bluetoothAudioRecoveryWorkerScheduled != 0)
                {
                    return;
                }

                // Admission and idle publication share this short monitor with
                // retirement. Stop publishes the generation boundary before
                // taking the monitor, then waits after releasing it, so it can
                // neither miss an admitted worker nor hold a lock while that
                // worker performs recovery I/O.
                bluetoothAudioRecoveryWorkerScheduled = 1;
                bluetoothAudioRecoveryWorkerGeneration = workerGeneration;
                bluetoothAudioRecoveryWorkerIdle.Reset();
                while (bluetoothAudioRecoveryWake.WaitOne(0))
                {
                }
            }

            bool queued = ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    while (IsBluetoothOutputRecoveryGenerationActive(
                        workerGeneration))
                    {
                        Func<long, bool> testHook =
                            BluetoothOutputRecoveryIterationTestHook;
                        bool completed = testHook != null ?
                            testHook(workerGeneration) :
                            TryRecoverUnifiedBluetoothOutputTransport(
                                workerGeneration);
                        if (completed)
                        {
                            return;
                        }

                        BluetoothOutputRecoveryBeforeWaitTestHook?.Invoke(
                            workerGeneration);
                        bluetoothAudioRecoveryWake.WaitOne(
                            BluetoothAudioPacerStartupRetryMilliseconds);
                    }
                }
                finally
                {
                    CompleteBluetoothOutputRecoveryWorker(workerGeneration);
                }
            });

            if (!queued)
            {
                CompleteBluetoothOutputRecoveryWorker(workerGeneration);
            }
        }

        private bool TryRecoverUnifiedBluetoothOutputTransport(
            long workerGeneration)
        {
            if (!IsBluetoothOutputRecoveryGenerationActive(workerGeneration) ||
                !RecoverBluetoothSpeakerClockTransport(
                    useV5PresentationCadence: true) ||
                !IsBluetoothOutputRecoveryGenerationActive(workerGeneration))
            {
                return false;
            }

            // Recovery starts a fresh physical FIFO. Commit the latest
            // coalesced state once before returning so a stale speaker lease
            // cannot leave lightbar, trigger, rumble, or haptics changes
            // template-only. Recheck the captured physical generation before
            // the completion-aware commit; an old worker may never label its
            // cached state as belonging to a replacement controller owner.
            return TryWriteCachedBluetoothCombinedControlReport(
                includeNativeHaptics: true,
                reportDescription: "recovered controller state",
                waitForCompletion: true) &&
                IsBluetoothOutputRecoveryGenerationActive(workerGeneration);
        }

        private bool IsBluetoothOutputRecoveryGenerationActive(
            long workerGeneration)
        {
            return Volatile.Read(ref bluetoothOutputTransportStopping) == 0 &&
                workerGeneration == Volatile.Read(ref physicalOutputGeneration);
        }

        private void CompleteBluetoothOutputRecoveryWorker(
            long workerGeneration)
        {
            lock (bluetoothAudioRecoveryOwnershipLock)
            {
                if (bluetoothAudioRecoveryWorkerScheduled == 0 ||
                    bluetoothAudioRecoveryWorkerGeneration != workerGeneration)
                {
                    return;
                }

                bluetoothAudioRecoveryWorkerScheduled = 0;
                bluetoothAudioRecoveryWorkerGeneration = 0;
                bluetoothAudioRecoveryWorkerIdle.Set();
            }
        }

        private void RetireBluetoothOutputRecoveryWorker()
        {
            Volatile.Write(ref bluetoothOutputTransportStopping, 1);
            bluetoothAudioRecoveryWake.Set();
            // Synchronize with admission, then wait without holding ownership.
            lock (bluetoothAudioRecoveryOwnershipLock)
            {
            }
            bluetoothAudioRecoveryWorkerIdle.WaitOne();
        }

        private void StopBluetoothAudioPacerLocked()
        {
            DualSenseBluetoothAudioPacer pacer;
            lock (bluetoothAudioPacerLock)
            {
                pacer = bluetoothAudioPacer;
                bluetoothAudioPacer = null;
            }

            if (pacer == null)
            {
                return;
            }

            bluetoothAudioPacerOperationsIdle.WaitOne();
            bluetoothAudioPacerLastError = pacer.LastError;
            pacer.Stop();
            pacer.Dispose();
        }

        private bool TryClaimBluetoothAudioPacer(
            out DualSenseBluetoothAudioPacer pacer,
            out bool pacerOwnsTransport)
        {
            lock (bluetoothAudioPacerLock)
            {
                pacer = bluetoothAudioPacer;
                pacerOwnsTransport = pacer != null;
                if (pacer?.IsRunning != true)
                {
                    return false;
                }

                if (bluetoothAudioPacerActiveOperations++ == 0)
                {
                    bluetoothAudioPacerOperationsIdle.Reset();
                }
                return true;
            }
        }

        private void ReleaseBluetoothAudioPacerClaim()
        {
            lock (bluetoothAudioPacerLock)
            {
                if (--bluetoothAudioPacerActiveOperations == 0)
                {
                    bluetoothAudioPacerOperationsIdle.Set();
                }
            }
        }

        private bool ClearBluetoothAudioPacerLocked()
        {
            if (!TryClaimBluetoothAudioPacer(
                    out DualSenseBluetoothAudioPacer pacer,
                    out bool ownsTransport))
            {
                return !ownsTransport;
            }

            bool cleared;
            try
            {
                cleared = pacer.Clear();
            }
            finally
            {
                ReleaseBluetoothAudioPacerClaim();
            }
            if (cleared)
            {
                return true;
            }

            bool detached = false;
            lock (bluetoothAudioPacerLock)
            {
                if (ReferenceEquals(bluetoothAudioPacer, pacer))
                {
                    bluetoothAudioPacer = null;
                    bluetoothAudioPacerLastError = pacer.LastError;
                    detached = true;
                }
            }

            if (detached)
            {
                bluetoothAudioPacerOperationsIdle.WaitOne();
                pacer.Dispose();
            }
            return false;
        }

        private void StopBluetoothAudioPacer()
        {
            StopBluetoothAudioPacerLocked();
        }

        private bool TryUpdateBluetoothAudioPacerTemplate(byte[] template,
            long hapticsExpiryQpc, out bool pacerOwnsTransport,
            bool realtimeHaptics = false, bool waitForCapacity = false)
        {
            if (!TryClaimBluetoothAudioPacer(
                    out DualSenseBluetoothAudioPacer pacer,
                    out pacerOwnsTransport))
            {
                if (pacer != null)
                {
                    bluetoothAudioPacerLastError = pacer.LastError;
                }
                return false;
            }

            try
            {
                if (realtimeHaptics)
                {
                    return pacer.UpdateRealtimeHapticsTemplate(
                        template, hapticsExpiryQpc);
                }

                bool templateUpdated = waitForCapacity ?
                    pacer.UpdateTemplateAndWaitForCapacity(
                        template, hapticsExpiryQpc) :
                    pacer.UpdateTemplate(template,
                        hapticsExpiryQpc);
                if (!templateUpdated)
                {
                    return false;
                }

                // A steady media template must not replay regular rumble, but
                // removing either of Sony's two required validity bits also
                // prevents the command from ever reaching the controller.
                // Publish the motor pair through the compositor's ordered
                // one-shot state mailbox; it is atomically overlaid on the
                // next physical frame and consumed only after write acceptance.
                return pacer.UpdateLocalRumbleState(template);
            }
            finally
            {
                ReleaseBluetoothAudioPacerClaim();
            }
        }

        private bool TryQueueBluetoothAudioPacerReport(byte[] report,
            long hapticsExpiryQpc, out bool pacerOwnsTransport)
        {
            if (!TryClaimBluetoothAudioPacer(
                    out DualSenseBluetoothAudioPacer pacer,
                    out pacerOwnsTransport))
            {
                return false;
            }

            try
            {
                return pacer.TryQueueReport(report,
                    hapticsExpiryQpc);
            }
            finally
            {
                ReleaseBluetoothAudioPacerClaim();
            }
        }

        private void SignalBluetoothAudioPacerMicrophoneFrame(byte sequence)
        {
            // Do not take the transport lock on the physical input thread.
            // Disconnect may retire this reference concurrently; the pacer's
            // signal method treats that final race as a harmless no-op.
            Volatile.Read(ref bluetoothAudioPacer)?.SignalMicrophoneFrame(
                sequence);
        }

        private bool TryUpdateBluetoothAudioPacerCadenceRatio(double ratio)
        {
            if (!TryClaimBluetoothAudioPacer(
                    out DualSenseBluetoothAudioPacer pacer, out _))
            {
                return false;
            }
            try
            {
                return pacer.UpdateCadenceRatio(ratio,
                        Volatile.Read(ref bluetoothLastInputArrivalQpc));
            }
            finally
            {
                ReleaseBluetoothAudioPacerClaim();
            }
        }

        private bool TryUpdateBluetoothAudioPacerMediaBuffer(byte level,
            long observationQpc)
        {
            if (!TryClaimBluetoothAudioPacer(
                    out DualSenseBluetoothAudioPacer pacer, out _))
            {
                bluetoothMediaBufferServo.Reset();
                Interlocked.Exchange(ref bluetoothMediaBufferCadenceRatioBits,
                    BitConverter.DoubleToInt64Bits(1.0));
                return false;
            }

            try
            {
                double cadenceRatio = bluetoothMediaBufferServo.Update(level,
                    observationQpc, observationQpc);
                Interlocked.Exchange(ref bluetoothMediaBufferCadenceRatioBits,
                    BitConverter.DoubleToInt64Bits(cadenceRatio));
                return pacer.UpdateControllerMediaBuffer(level,
                    observationQpc, cadenceRatio);
            }
            finally
            {
                ReleaseBluetoothAudioPacerClaim();
            }
        }

        private bool TryQueueBluetoothControlThroughAudioPacer(byte[] report,
            long hapticsExpiryQpc,
            out DualSenseBluetoothAudioPacer pacer,
            out DualSenseBluetoothAudioPacer.ControlReportCompletionToken
                completionToken,
            out bool pacerOwnsTransport)
        {
            completionToken = default;
            if (!TryClaimBluetoothAudioPacer(
                    out pacer,
                    out pacerOwnsTransport))
            {
                return false;
            }

            if (pacer.TryQueueControlReport(report, hapticsExpiryQpc,
                    out completionToken))
            {
                // Retain the active-operation claim through completion wait.
                // Lifecycle retirement waits on that claim after releasing
                // every state/generation lock.
                return true;
            }

            ReleaseBluetoothAudioPacerClaim();
            pacer = null;
            return false;
        }

        private bool WaitForBluetoothControlThroughAudioPacer(
            DualSenseBluetoothAudioPacer pacer,
            DualSenseBluetoothAudioPacer.ControlReportCompletionToken token)
        {
            try
            {
                // Test synchronization is deliberately after the admission
                // monitor. A blocked completion consumer must not prevent a
                // following speaker report from entering the same FIFO.
                BluetoothCombinedControlEnqueuedTestHook?.Invoke();
                bool presented = pacer.WaitForControlReport(token,
                    (int)BluetoothFinalControlWriteTimeoutMilliseconds,
                    out DualSenseBluetoothAudioPacer.AcknowledgementDisposition
                        disposition);
                if (!presented && disposition ==
                    DualSenseBluetoothAudioPacer.AcknowledgementDisposition
                        .TransportFault)
                {
                    bluetoothAudioPacerLastError =
                        "The isolated Bluetooth control commit hit a HID transport fault.";
                }

                return presented;
            }
            finally
            {
                ReleaseBluetoothAudioPacerClaim();
            }
        }

        private bool RefreshBluetoothAudioPacerTemplateFromCache(
            bool realtimeHaptics = false, bool waitForCapacity = false)
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            return RefreshBluetoothAudioPacerTemplateFromCache(outputState,
                realtimeHaptics, waitForCapacity);
        }

        private bool RefreshBluetoothAudioPacerTemplateFromCache(
            in DualSensePhysicalOutputSnapshot outputState,
            bool realtimeHaptics = false, bool waitForCapacity = false)
        {
            if (Interlocked.CompareExchange(
                    ref bluetoothCombinedTemplateUpdateClaimed, 1, 0) != 0)
            {
                return false;
            }

            try
            {
                byte[] template = bluetoothCombinedTemplateUpdateReport;
                long hapticsExpiryQpc;
                lock (bluetoothCombinedTransportWriteLock)
                {
                    lock (bluetoothCombinedSpeakerReportLock)
                    {
                        if (!bluetoothCombinedSpeakerReportAvailable)
                        {
                            return false;
                        }

                        Array.Copy(latestBluetoothCombinedSpeakerReport,
                            template, template.Length);
                        hapticsExpiryQpc =
                            PersistentBluetoothHapticsExpiryQpc;
                    }

                    ApplyBluetoothSpeakerVolumeAndRoutingCore(template,
                        outputState.SpeakerVolume,
                        outputState.HeadsetOnlyAudio,
                        outputState.HeadphoneVolume);
                    ApplyBluetoothMicrophoneStreamingRequest(template,
                        outputState);
                }

                bool updated = TryUpdateBluetoothAudioPacerTemplate(template,
                    hapticsExpiryQpc, out bool pacerOwnsTransport,
                    realtimeHaptics, waitForCapacity);
                if (!pacerOwnsTransport || !updated)
                {
                    RequestUnifiedBluetoothOutputTransportRecovery();
                    return false;
                }

                return true;
            }
            finally
            {
                Volatile.Write(ref bluetoothCombinedTemplateUpdateClaimed, 0);
            }
        }

        private bool QueueBluetoothAudioPacerMicrophoneTransitionFromCache(
            in DualSensePhysicalOutputSnapshot outputState, bool enabled)
        {
            if (Interlocked.CompareExchange(
                    ref bluetoothCombinedTemplateUpdateClaimed, 1, 0) != 0)
            {
                return false;
            }

            try
            {
                byte[] template = bluetoothCombinedTemplateUpdateReport;
                long hapticsExpiryQpc;
                lock (bluetoothCombinedTransportWriteLock)
                {
                    lock (bluetoothCombinedSpeakerReportLock)
                    {
                        if (!bluetoothCombinedSpeakerReportAvailable)
                        {
                            return false;
                        }

                        Array.Copy(latestBluetoothCombinedSpeakerReport,
                            template, template.Length);
                        hapticsExpiryQpc =
                            PersistentBluetoothHapticsExpiryQpc;
                    }

                    ApplyBluetoothSpeakerVolumeAndRoutingCore(template,
                        outputState.SpeakerVolume,
                        outputState.HeadsetOnlyAudio,
                        outputState.HeadphoneVolume);
                    ApplyBluetoothMicrophoneStreamingRequest(template,
                        outputState);
                }

                if (!TryClaimBluetoothAudioPacer(
                        out DualSenseBluetoothAudioPacer pacer, out _))
                {
                    return false;
                }
                try
                {
                    return pacer.UpdateMicrophoneTransition(template,
                        hapticsExpiryQpc, enabled);
                }
                finally
                {
                    ReleaseBluetoothAudioPacerClaim();
                }
            }
            finally
            {
                Volatile.Write(ref bluetoothCombinedTemplateUpdateClaimed, 0);
            }
        }

        private bool TryPublishCachedBluetoothCombinedState(
            bool includeNativeHaptics, string activeStatus,
            string idleReportDescription, out bool deferredToSpeakerClock,
            bool realtimeHaptics = false)
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            // The speaker-clock claim is the authoritative media ownership
            // boundary. Downstream builders take their own short snapshot
            // locks and perform waits only after releasing them.
            if (outputState.EnableSpeakerOutput &&
                IsBluetoothSpeakerClockActive())
            {
                deferredToSpeakerClock = true;
                bool refreshed = RefreshBluetoothAudioPacerTemplateFromCache(
                    outputState, realtimeHaptics);
                LastBluetoothHapticsWriteStatus = refreshed ? activeStatus :
                    $"Could not publish {idleReportDescription} to the active Bluetooth speaker clock.";
                return refreshed;
            }

            deferredToSpeakerClock = false;
            return TryWriteCachedBluetoothCombinedControlReport(
                includeNativeHaptics, idleReportDescription,
                waitForCompletion: false,
                allowDuringStopping: false,
                outputState: outputState);
        }

        private bool IsBluetoothSpeakerClockActive()
        {
            lock (bluetoothSpeakerClockClaimLock)
            {
                if (bluetoothSpeakerClockActiveClaim == 0)
                {
                    return false;
                }

                long now = Stopwatch.GetTimestamp();
                if (bluetoothSpeakerClockLeaseExpiryTimestamp > now)
                {
                    return true;
                }

                // A producer died before its normal Clear boundary. Expiring
                // the token makes idle haptics/microphone control physically
                // commit through the retained helper instead of being cached
                // behind a speaker clock that no longer exists.
                bluetoothSpeakerClockActiveClaim = 0;
                bluetoothSpeakerClockLeaseExpiryTimestamp = 0;
                return false;
            }
        }

        private bool HasPendingBluetoothCombinedHaptics()
        {
            lock (bluetoothCombinedSpeakerReportLock)
            {
                return bluetoothCombinedSpeakerReportAvailable &&
                    bluetoothCombinedHapticsGeneration >
                        bluetoothCombinedSubmittedHapticsGeneration;
            }
        }

        private bool TryTakeBluetoothSpeakerAudioFrame(byte[] destination,
            int destinationOffset, bool speakerOutputEnabled)
        {
            lock (bluetoothSpeakerFrameLock)
            {
                if (!speakerOutputEnabled || !bluetoothSpeakerFramePending ||
                    destination == null ||
                    destinationOffset < 0 ||
                    destinationOffset + BluetoothCombinedSpeakerFrameLength > destination.Length)
                {
                    return false;
                }

                Array.Copy(bluetoothSpeakerFrame, 0, destination, destinationOffset,
                    BluetoothCombinedSpeakerFrameLength);
                bluetoothSpeakerFramePending = false;
                return true;
            }
        }

        private DualSenseControllerOptions nativeOptionsStore;
        public DualSenseControllerOptions NativeOptionsStore { get => nativeOptionsStore; }

        public bool IsProfileMicrophoneMuted =>
            Volatile.Read(ref profileMicrophoneMuteState) == 2;

        public override DS4Color LightBarColor
        {
            get => physicalOutputStateMailbox.ReadLatest().ProfileLightbar.
                LightBarColor;
            set
            {
                DualSensePhysicalOutputSnapshot latest =
                    physicalOutputStateMailbox.ReadLatest();
                DS4LightbarState lightbar = latest.ProfileLightbar;
                lightbar.LightBarColor = value;
                PublishProfileLightbar(lightbar);
            }
        }

        public override byte RightLightFastRumble
        {
            get => physicalOutputStateMailbox.ReadLatest().RumbleState.
                RumbleMotorStrengthRightLightFast;
            set
            {
                if (physicalOutputStateMailbox.SetRumbleChannel(
                        rightLightFast: true, value,
                        out DualSensePhysicalOutputSnapshot snapshot))
                {
                    base.setRumble(
                        snapshot.RumbleState.
                            RumbleMotorStrengthRightLightFast,
                        snapshot.RumbleState.
                            RumbleMotorStrengthLeftHeavySlow);
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        public override byte LeftHeavySlowRumble
        {
            get => physicalOutputStateMailbox.ReadLatest().RumbleState.
                RumbleMotorStrengthLeftHeavySlow;
            set
            {
                if (physicalOutputStateMailbox.SetRumbleChannel(
                        rightLightFast: false, value,
                        out DualSensePhysicalOutputSnapshot snapshot))
                {
                    base.setRumble(
                        snapshot.RumbleState.
                            RumbleMotorStrengthRightLightFast,
                        snapshot.RumbleState.
                            RumbleMotorStrengthLeftHeavySlow);
                    QueuePhysicalOutputUpdate();
                }
            }
        }

        public override byte getLeftHeavySlowRumble() =>
            physicalOutputStateMailbox.ReadLatest().RumbleState.
                RumbleMotorStrengthLeftHeavySlow;

        public override void SetHapticState(ref DS4HapticState state)
        {
            if (physicalOutputStateMailbox.SetHapticState(
                    state.lightbarState, state.rumbleState, out _))
            {
                base.setRumble(
                    state.rumbleState.RumbleMotorStrengthRightLightFast,
                    state.rumbleState.RumbleMotorStrengthLeftHeavySlow);
                QueuePhysicalOutputUpdate();
            }
        }

        public override void SetLightbarState(
            ref DS4LightbarState lightState)
        {
            PublishProfileLightbar(lightState);
        }

        public override void SetRumbleState(
            ref DS4ForceFeedbackState rumbleState)
        {
            if (physicalOutputStateMailbox.SetRumbleState(rumbleState, out _))
            {
                base.setRumble(
                    rumbleState.RumbleMotorStrengthRightLightFast,
                    rumbleState.RumbleMotorStrengthLeftHeavySlow);
                QueuePhysicalOutputUpdate();
            }
        }

        public override void setRumble(byte rightLightFastMotor,
            byte leftHeavySlowMotor)
        {
            DS4ForceFeedbackState rumble = new DS4ForceFeedbackState
            {
                RumbleMotorStrengthRightLightFast = rightLightFastMotor,
                RumbleMotorStrengthLeftHeavySlow = leftHeavySlowMotor,
                RumbleMotorsExplicitlyOff = rightLightFastMotor == 0 &&
                    leftHeavySlowMotor == 0,
            };
            if (physicalOutputStateMailbox.SetRumbleState(rumble, out _))
            {
                base.setRumble(rightLightFastMotor, leftHeavySlowMotor);
                QueuePhysicalOutputUpdate();
            }
        }

        public override void SetRumblePreview(bool lightMotorActive,
            byte lightMotorStrength, bool heavyMotorActive,
            byte heavyMotorStrength)
        {
            if (physicalOutputStateMailbox.SetRumblePreview(
                    lightMotorActive, lightMotorStrength, heavyMotorActive,
                    heavyMotorStrength, out _))
            {
                base.SetRumblePreview(lightMotorActive, lightMotorStrength,
                    heavyMotorActive, heavyMotorStrength);
                QueuePhysicalOutputUpdate();
            }
        }

        public override void ClearRumblePreview()
        {
            if (physicalOutputStateMailbox.ClearRumblePreview(out _))
            {
                base.ClearRumblePreview();
                QueuePhysicalOutputUpdate();
            }
        }

        private void PublishProfileLightbar(
            in DS4LightbarState lightbar)
        {
            if (physicalOutputStateMailbox.SetProfileLightbar(lightbar))
            {
                QueuePhysicalOutputUpdate();
            }
        }

        public void SetProfileMicrophoneMuteState(bool enabled, bool muted)
        {
            int state = enabled ? (muted ? 2 : 1) : 0;
            if (Interlocked.Exchange(ref profileMicrophoneMuteState, state) == state)
            {
                return;
            }

            if (physicalOutputStateMailbox.SetMicrophoneMute(enabled, muted))
            {
                QueuePhysicalOutputUpdate();
            }
        }

        public void SetMicrophoneMuteState(bool muted)
        {
            SetProfileMicrophoneMuteState(true, muted);
        }

        public void SetProfileMuteLedState(bool enabled, bool ledOn)
        {
            if (physicalOutputStateMailbox.SetMuteLedOverride(enabled, ledOn))
            {
                QueuePhysicalOutputUpdate();
            }
        }

        public override event ReportHandler<EventArgs> Report = null;
        public override event EventHandler BatteryChanged;
        public override event EventHandler ChargingChanged;

        public DualSenseDevice(HidDevice hidDevice, string disName, VidPidFeatureSet featureSet = VidPidFeatureSet.DefaultDS4) :
            base(hidDevice, disName, featureSet)
        {
            synced = true;
            DeviceSlotNumberChanged += (sender, e) => {
                CalculateDeviceSlotMask();
            };

            BatteryChanged += (sender, e) =>
            {
                PreparePlayerLEDBarByte();
            };
        }

        private static byte[][] CreateFixedByteBuffers(int count, int length)
        {
            byte[][] buffers = new byte[count][];
            for (int index = 0; index < count; index++)
            {
                buffers[index] = new byte[length];
            }
            return buffers;
        }

        private void StartPhysicalWorkers()
        {
            if (Interlocked.CompareExchange(
                    ref physicalWorkerStartTransitioning, 1, 0) != 0)
            {
                return;
            }

            physicalWorkerStartCompleted.Reset();
            Volatile.Write(ref physicalWorkerStartOwnerThreadId,
                Environment.CurrentManagedThreadId);
            long externalRequestBoundary = Interlocked.Read(
                ref physicalLifecycleExternalRequestVersion);
            try
            {
                if (physicalOutputThread?.IsAlive == true &&
                    Volatile.Read(ref physicalOutputStopRequested) == 0 &&
                    Volatile.Read(ref physicalLifecycleShutdownRequested) == 0)
                {
                    return;
                }

                // StartUpdate is not an input callback. If a prior generation
                // is incomplete or one of its owners died, retire every old
                // owner before publishing replacement storage. Merely waking
                // an unrequested lifecycle thread would let it exit without
                // joining the remaining workers.
                if (physicalLifecycleThread?.IsAlive == true)
                {
                    RequestPhysicalLifecycleShutdown(
                        waitForCompletion: true,
                        internalStartRetirement: true);
                }
                else if (physicalOutputThread?.IsAlive == true ||
                    bluetoothObservationThread?.IsAlive == true ||
                    bluetoothMicrophoneDispatchThread?.IsAlive == true ||
                    deviceCommandThread?.IsAlive == true)
                {
                    Interlocked.Exchange(ref bluetoothOutputTransportStopping,
                        1);
                    StopPhysicalWorkersCore();
                    FinalizePhysicalOutput();
                    physicalLifecycleCompleted.Set();
                }

                // A recovery retry is also a physical-transport owner. It can
                // be the only survivor when an earlier output worker faulted,
                // so retire it explicitly before clearing the stopping flag
                // and publishing reusable state for the next generation.
                RetireBluetoothOutputRecoveryWorker();

                // This is the linearization boundary between retiring an old
                // generation and publishing a replacement. A stop/removal
                // request arriving after StartPhysicalWorkers gained its
                // election wins: every old worker is already retired, so
                // finish its lifecycle notifications and do not resurrect a
                // controller generation behind the caller's back.
                // A stop issued before the first StartUpdate leaves an
                // AutoResetEvent permit without a lifecycle consumer. Retired
                // generations cannot have a waiter here, so discard that old
                // permit before publishing the replacement owner. Otherwise
                // the new lifecycle thread can consume it with request == 0
                // and exit before the first real shutdown.
                DrainPhysicalLifecycleSignal();
                Volatile.Write(ref physicalLifecycleShutdownRequested, 0);
                if (Interlocked.Read(
                        ref physicalLifecycleExternalRequestVersion) !=
                    externalRequestBoundary)
                {
                    DrainPhysicalLifecycleSignal();
                    CompletePhysicalLifecycleNotifications();
                    physicalLifecycleCompleted.Set();
                    return;
                }

                long generation = Interlocked.Increment(
                    ref physicalOutputGeneration);
                Volatile.Write(ref bluetoothOutputTransportStopping, 0);
                Volatile.Write(ref physicalOutputStopRequested, 0);
                Volatile.Write(ref bluetoothObservationStopRequested, 0);
                Volatile.Write(ref bluetoothMicrophoneDispatchStopRequested,
                    0);
                Volatile.Write(ref deviceCommandStopRequested, 0);
                Volatile.Write(ref physicalLifecycleRemovalRequested, 0);
                Volatile.Write(ref physicalLifecycleIdleDisconnectRequested,
                    0);
                Volatile.Write(ref physicalInputFailureKind,
                    (int)PhysicalInputFailureKind.None);
                Volatile.Write(ref physicalInputFailureWinError, 0);
                Interlocked.Exchange(ref physicalInputFailureTimestamp, 0);
                Volatile.Write(ref deviceStatusNotificationPending, 0);
                physicalLifecycleCompleted.Reset();
                Interlocked.Exchange(ref physicalOutputRequestedGeneration, 0);
                Interlocked.Exchange(ref physicalOutputQueuedTimestamp, 0);
                Interlocked.Exchange(ref physicalOutputKeepaliveDueQpc, 0);
                claimedPhysicalOutputStateVersion = 0;
                activePhysicalOutputState =
                    DualSensePhysicalOutputSnapshot.Default;
                Volatile.Write(ref nativeSessionReleasePending, 0);
                Interlocked.Exchange(ref bluetoothObservationVersion, 0);
                Interlocked.Exchange(
                    ref bluetoothMicrophoneObservationVersion, 0);
                Interlocked.Exchange(ref bluetoothObservedGeneration, 0);
                Interlocked.Exchange(
                    ref bluetoothObservedMicrophoneGeneration, 0);
                Interlocked.Exchange(
                    ref bluetoothObservedMicrophoneArrivalQpc, 0);
                Volatile.Write(ref bluetoothObservedMediaBuffer, -1);
                Volatile.Write(ref bluetoothObservedMicrophoneSequence, -1);
                Volatile.Write(ref bluetoothMicrophoneLastDispatchedSequence,
                    -1);
                lock (bluetoothMicrophoneFrameLock)
                {
                    bluetoothMicrophoneFrameHead = 0;
                    bluetoothMicrophoneFrameCount = 0;
                    Array.Clear(bluetoothMicrophoneFrameGenerations, 0,
                        bluetoothMicrophoneFrameGenerations.Length);
                    Array.Clear(bluetoothMicrophoneFrameArrivalTimestamps, 0,
                        bluetoothMicrophoneFrameArrivalTimestamps.Length);
                    Array.Clear(bluetoothMicrophoneFrameSequences, 0,
                        bluetoothMicrophoneFrameSequences.Length);
                }
                lock (physicalOutputCommandLock)
                {
                    physicalOutputCommandHead = 0;
                    physicalOutputCommandCount = 0;
                }

                physicalOutputThread = new Thread(() =>
                    PhysicalOutputLoop(generation))
                {
                    IsBackground = true,
                    Name = "DualSense physical output: " + Mac,
                };
                physicalOutputThread.Start();

                if (conType == ConnectionType.BT)
                {
                    Interlocked.Exchange(ref bluetoothObservationGeneration,
                        generation);
                    bluetoothObservationThread = new Thread(() =>
                        BluetoothObservationLoop(generation))
                    {
                        IsBackground = true,
                        Name = "DualSense clock observations: " + Mac,
                    };
                    bluetoothObservationThread.Start();

                    bluetoothMicrophoneDispatchThread = new Thread(() =>
                        BluetoothMicrophoneDispatchLoop(generation))
                    {
                        IsBackground = true,
                        Name = "DualSense microphone dispatch: " + Mac,
                    };
                    bluetoothMicrophoneDispatchThread.Start();
                }

                deviceCommandThread = new Thread(() =>
                    DeviceCommandLoop(generation))
                {
                    IsBackground = true,
                    Name = "DualSense device commands: " + Mac,
                };
                deviceCommandThread.Start();

                physicalLifecycleThread = new Thread(
                    PhysicalLifecycleLoop)
                {
                    IsBackground = true,
                    Name = "DualSense physical output lifecycle: " + Mac,
                };
                physicalLifecycleThread.Start();
                if (Volatile.Read(
                        ref physicalLifecycleShutdownRequested) != 0)
                {
                    // An AutoResetEvent signal can have been consumed by the
                    // retired lifecycle thread while this replacement was
                    // being constructed. Re-signal after publication.
                    physicalLifecycleSignal.Set();
                }
                else
                {
                    // Profile and device settings can be published while the
                    // old generation is stopped, in which case their normal
                    // producer signals are intentionally rejected. Admit one
                    // complete latest-state application after every new
                    // physical owner is fully published.
                    QueuePhysicalOutputUpdate();
                }
            }
            finally
            {
                Volatile.Write(ref physicalWorkerStartOwnerThreadId, 0);
                Volatile.Write(ref physicalWorkerStartTransitioning, 0);
                physicalWorkerStartCompleted.Set();
            }
        }

        private void StopPhysicalWorkersCore()
        {
            Volatile.Write(ref physicalOutputStopRequested, 1);
            Volatile.Write(ref bluetoothObservationStopRequested, 1);
            Volatile.Write(ref bluetoothMicrophoneDispatchStopRequested, 1);
            Volatile.Write(ref deviceCommandStopRequested, 1);
            Interlocked.Increment(ref physicalOutputGeneration);
            physicalOutputSignal.Set();
            bluetoothObservationSignal.Set();
            bluetoothMicrophoneDispatchSignal.Set();
            deviceCommandSignal.Set();
            RetireBluetoothOutputRecoveryWorker();
            // The old physical output writer must be completely gone before
            // the lifecycle owner presents the final neutral report. A timed
            // join here could leave two HID writers racing the same handle.
            JoinWorker(physicalOutputThread, timeoutMilliseconds: -1);
            // Observation and microphone workers also share per-device
            // buffers and pacer state. Definitive retirement is required
            // before a replacement generation can reuse that storage.
            JoinWorker(bluetoothObservationThread, timeoutMilliseconds: -1);
            JoinWorker(bluetoothMicrophoneDispatchThread,
                timeoutMilliseconds: -1);
            JoinWorker(deviceCommandThread, timeoutMilliseconds: -1);
            physicalOutputThread = null;
            bluetoothObservationThread = null;
            bluetoothMicrophoneDispatchThread = null;
            deviceCommandThread = null;
        }

        private static void JoinWorker(Thread worker, int timeoutMilliseconds)
        {
            if (worker != null && worker.IsAlive &&
                !ReferenceEquals(worker, Thread.CurrentThread))
            {
                if (timeoutMilliseconds < 0)
                {
                    worker.Join();
                }
                else
                {
                    worker.Join(timeoutMilliseconds);
                }
            }
        }

        private void PhysicalLifecycleLoop()
        {
            physicalLifecycleSignal.WaitOne();
            if (Volatile.Read(ref physicalLifecycleShutdownRequested) == 0)
            {
                return;
            }

            try
            {
                StopPhysicalWorkersCore();
                try
                {
                    FinalizePhysicalOutput();
                }
                finally
                {
                    // A failed final HID write must not suppress disconnect
                    // diagnostics or strand the controller without its
                    // removal notification.
                    CompletePhysicalLifecycleNotifications();
                }
            }
            finally
            {
                physicalLifecycleCompleted.Set();
            }
        }

        private void DrainPhysicalLifecycleSignal()
        {
            while (physicalLifecycleSignal.WaitOne(0))
            {
            }
        }

        private void CompletePhysicalLifecycleNotifications()
        {
            ReportPhysicalInputFailure();
            if (Interlocked.Exchange(
                    ref physicalLifecycleIdleDisconnectRequested, 0) != 0)
            {
                AppLogger.LogToGui(Mac.ToString() +
                    " disconnecting due to idle disconnect", false);
                base.DisconnectBT(callRemoval: true);
                Interlocked.Exchange(
                    ref physicalLifecycleRemovalRequested, 0);
            }
            else if (Interlocked.Exchange(
                         ref physicalLifecycleRemovalRequested, 0) != 0)
            {
                // Removal can destroy controller state and invokes an
                // arbitrary subscriber. It therefore belongs to this
                // lifecycle owner after physical output retirement, never to
                // the physical HID read callback that detected the failure.
                RunRemoval();
            }
        }

        private void RequestPhysicalLifecycleShutdown(bool waitForCompletion,
            bool internalStartRetirement = false)
        {
            if (!internalStartRetirement)
            {
                Interlocked.Increment(
                    ref physicalLifecycleExternalRequestVersion);
            }
            Interlocked.Exchange(ref bluetoothOutputTransportStopping, 1);
            if (Interlocked.CompareExchange(
                    ref physicalLifecycleShutdownRequested, 1, 0) == 0)
            {
                physicalLifecycleSignal.Set();
            }
            bluetoothAudioRecoveryWake.Set();

            if (waitForCompletion &&
                !ReferenceEquals(Thread.CurrentThread, physicalLifecycleThread))
            {
                if (!internalStartRetirement &&
                    Environment.CurrentManagedThreadId != Volatile.Read(
                        ref physicalWorkerStartOwnerThreadId))
                {
                    while (Volatile.Read(
                               ref physicalWorkerStartTransitioning) != 0)
                    {
                        physicalWorkerStartCompleted.WaitOne(10);
                    }
                }
                physicalLifecycleCompleted.WaitOne();
                Thread lifecycle = physicalLifecycleThread;
                if (lifecycle?.IsAlive == true &&
                    !ReferenceEquals(Thread.CurrentThread, lifecycle))
                {
                    lifecycle.Join();
                }
                // Stop-before-start has no lifecycle thread to execute
                // StopPhysicalWorkersCore, but an administrative shutdown is
                // still a definitive ownership boundary.
                bluetoothAudioRecoveryWorkerIdle.WaitOne();
            }
        }

        private void RequestPhysicalRemoval()
        {
            Interlocked.Exchange(ref physicalLifecycleRemovalRequested, 1);
            RequestPhysicalLifecycleShutdown(waitForCompletion: false);
        }

        private void RequestPhysicalRemoval(PhysicalInputFailureKind failure,
            int winError = 0)
        {
            Volatile.Write(ref physicalInputFailureWinError, winError);
            Interlocked.Exchange(ref physicalInputFailureTimestamp,
                Stopwatch.GetTimestamp());
            Volatile.Write(ref physicalInputFailureKind, (int)failure);
            RequestPhysicalRemoval();
        }

        private void RequestPhysicalIdleDisconnect()
        {
            Interlocked.Exchange(ref physicalLifecycleIdleDisconnectRequested,
                1);
            RequestPhysicalLifecycleShutdown(waitForCompletion: false);
        }

        private void ReportPhysicalInputFailure()
        {
            PhysicalInputFailureKind failure =
                (PhysicalInputFailureKind)Interlocked.Exchange(
                    ref physicalInputFailureKind,
                    (int)PhysicalInputFailureKind.None);
            int winError = Volatile.Read(ref physicalInputFailureWinError);
            _ = Interlocked.Read(ref physicalInputFailureTimestamp);
            switch (failure)
            {
                case PhysicalInputFailureKind.Crc:
                    AppLogger.LogToGui(
                        DS4WinWPF.Translations.Strings.CRC32Fail, true);
                    break;
                case PhysicalInputFailureKind.BluetoothTimeout:
                case PhysicalInputFailureKind.UsbTimeout:
                    AppLogger.LogToGui(Mac.ToString() +
                        " disconnected due to timeout", true);
                    break;
                case PhysicalInputFailureKind.BluetoothRead:
                    Console.WriteLine($"{Mac} {DateTime.UtcNow:o} > " +
                        $"disconnect due to read failure: {winError:x8}");
                    AppLogger.LogToGui(Mac.ToString() +
                        " disconnected due to read failure: " + winError,
                        true);
                    break;
                case PhysicalInputFailureKind.UsbRead:
                    Console.WriteLine($"{Mac} {DateTime.UtcNow:o} > " +
                        $"disconnect due to read failure: {winError:x8}");
                    break;
            }
        }

        private void QueuePhysicalOutputUpdate()
        {
            if (Volatile.Read(ref physicalOutputStopRequested) != 0)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            Interlocked.CompareExchange(ref physicalOutputQueuedTimestamp,
                now, 0);
            Interlocked.Increment(ref physicalOutputRequestedGeneration);
            physicalOutputSignal.Set();
        }

        private void QueuePhysicalOutputKeepaliveIfDue()
        {
            if (!TryClaimPhysicalOutputKeepalive(
                    ref physicalOutputKeepaliveDueQpc,
                    Stopwatch.GetTimestamp()))
            {
                return;
            }

            QueuePhysicalOutputUpdate();
        }

        internal static bool TryClaimPhysicalOutputKeepalive(
            ref long dueAtQpc, long nowQpc)
        {
            long dueAt = Interlocked.Read(ref dueAtQpc);
            return dueAt > 0 && nowQpc >= dueAt &&
                Interlocked.CompareExchange(ref dueAtQpc, -1, dueAt) ==
                    dueAt;
        }

        private void PhysicalOutputLoop(long generation)
        {
            long completedGeneration = 0;
            while (Volatile.Read(ref physicalOutputStopRequested) == 0 &&
                generation == Volatile.Read(ref physicalOutputGeneration))
            {
                physicalOutputSignal.WaitOne();
                while (Volatile.Read(ref physicalOutputStopRequested) == 0 &&
                    generation == Volatile.Read(ref physicalOutputGeneration))
                {
                    int commandBurst = 0;
                    while (commandBurst < MaximumPhysicalCommandBurst &&
                        TryTakePhysicalOutputCommand(
                            physicalOutputCommandBuffer))
                    {
                        try
                        {
                            ProcessRawPhysicalOutputCommand(
                                physicalOutputCommandBuffer);
                        }
                        catch (Exception ex)
                        {
                            if (Global.VerboseStartupLogging)
                            {
                                AppLogger.LogToGui(
                                    $"DualSense ordered physical output failed: {ex.GetType().Name}: {ex.Message}",
                                    true);
                            }
                        }
                        commandBurst++;
                    }

                    long requested = Volatile.Read(
                        ref physicalOutputRequestedGeneration);
                    if (requested == completedGeneration)
                    {
                        if (HasPendingPhysicalOutputCommand())
                        {
                            continue;
                        }
                        break;
                    }

                    long queuedAt = Interlocked.Exchange(
                        ref physicalOutputQueuedTimestamp, 0);
                    long startedAt = Stopwatch.GetTimestamp();
                    if (queuedAt > 0)
                    {
                        physicalOutputQueueLatency.Observe(startedAt -
                            queuedAt);
                        RecordWorkerMaximum(
                            ref physicalOutputMaximumQueueAgeTicks,
                            startedAt - queuedAt);
                    }
                    try
                    {
                        Action testHook = PhysicalOutputWriteTestHook;
                        if (testHook != null)
                        {
                            testHook();
                        }
                        else
                        {
                            PrepareOutReport();
                            FlushPreparedOutputReport(requested);
                        }
                    }
                    catch (Exception ex)
                    {
                        // A failed trigger/lightbar/audio update needs a
                        // bounded retry even when rumble is not active. The
                        // ordinary keepalive helper intentionally clears its
                        // deadline for non-rumble state, so it cannot own
                        // failure recovery.
                        SchedulePhysicalOutputRetry();
                        if (Global.VerboseStartupLogging)
                        {
                            AppLogger.LogToGui(
                                $"DualSense physical output worker failed: {ex.GetType().Name}: {ex.Message}",
                                true);
                        }
                    }
                    long completedAt = Stopwatch.GetTimestamp();
                    physicalOutputWriteLatency.Observe(completedAt -
                        startedAt);
                    RecordWorkerMaximum(
                        ref physicalOutputMaximumWriteDurationTicks,
                        completedAt - startedAt);
                    completedGeneration = requested;
                }
            }
        }

        private bool TryQueuePhysicalOutputCommand(byte[] report, int offset)
        {
            lock (physicalOutputCommandLock)
            {
                if (physicalOutputCommandCount >=
                    PhysicalOutputCommandCapacity)
                {
                    Interlocked.Increment(
                        ref physicalOutputCommandOverflows);
                    return false;
                }

                int tail = (physicalOutputCommandHead +
                    physicalOutputCommandCount) %
                        PhysicalOutputCommandCapacity;
                Buffer.BlockCopy(report, offset,
                    physicalOutputCommandSlots[tail], 0,
                    USB_OUTPUT_CHANGE_LENGTH);
                physicalOutputCommandCount++;
            }
            physicalOutputSignal.Set();
            return true;
        }

        private bool TryTakePhysicalOutputCommand(byte[] destination)
        {
            lock (physicalOutputCommandLock)
            {
                if (physicalOutputCommandCount == 0)
                {
                    return false;
                }
                Buffer.BlockCopy(
                    physicalOutputCommandSlots[physicalOutputCommandHead], 0,
                    destination, 0, USB_OUTPUT_CHANGE_LENGTH);
                physicalOutputCommandHead = (physicalOutputCommandHead + 1) %
                    PhysicalOutputCommandCapacity;
                physicalOutputCommandCount--;
                return true;
            }
        }

        private bool HasPendingPhysicalOutputCommand()
        {
            lock (physicalOutputCommandLock)
            {
                return physicalOutputCommandCount > 0;
            }
        }

        private void ClaimPhysicalOutputState()
        {
            if (physicalOutputStateMailbox.TryClaim(
                    ref claimedPhysicalOutputStateVersion,
                    out DualSensePhysicalOutputSnapshot snapshot))
            {
                bool stopSpeaker = activePhysicalOutputState.
                    EnableSpeakerOutput && !snapshot.EnableSpeakerOutput;
                activePhysicalOutputState = snapshot;

                // These are compositor copies. Only this physical owner
                // mutates them, and PrepareOutReport reads them only after the
                // complete immutable snapshot has been claimed.
                currentHap.lightbarState = snapshot.ProfileLightbar;
                currentHap.rumbleState = snapshot.RumbleState;
                if (snapshot.PreviewLightRumbleActive)
                {
                    currentHap.rumbleState.
                        RumbleMotorStrengthRightLightFast =
                            snapshot.PreviewLightRumbleStrength;
                }
                if (snapshot.PreviewHeavyRumbleActive)
                {
                    currentHap.rumbleState.
                        RumbleMotorStrengthLeftHeavySlow =
                            snapshot.PreviewHeavyRumbleStrength;
                }
                if (snapshot.PreviewLightRumbleActive ||
                    snapshot.PreviewHeavyRumbleActive)
                {
                    currentHap.rumbleState.RumbleMotorsExplicitlyOff = false;
                }
                preparedLocalRumbleGeneration = snapshot.RumbleGeneration;
                currentHap.dirty = true;
                outputDirty = true;

                if (stopSpeaker)
                {
                    ClearBluetoothSpeakerAudioFrame();
                }
            }

            if (Interlocked.Exchange(ref nativeSessionReleasePending, 0) != 0)
            {
                ApplyNativeGameOutputReleaseOnPhysicalOwner();
            }
        }

        private void ApplyNativeGameOutputReleaseOnPhysicalOwner()
        {
            lock (bluetoothCombinedSpeakerReportLock)
            {
                latestBluetoothCombinedNativeStateTimestamp = 0;
                Array.Clear(bluetoothCombinedNativeStateScratch, 0,
                    bluetoothCombinedNativeStateScratch.Length);
                if (bluetoothCombinedSpeakerReportAvailable)
                {
                    Array.Clear(latestBluetoothCombinedSpeakerReport,
                        BluetoothCombinedHapticsDataOffset,
                        BluetoothCombinedHapticsDataLength);
                    bluetoothCombinedHapticsGeneration++;
                }
            }

            if (TryClaimBluetoothAudioPacer(out DualSenseBluetoothAudioPacer
                    pacer, out _))
            {
                try
                {
                    pacer.ResetControllerStateTransitions();
                }
                finally
                {
                    ReleaseBluetoothAudioPacerClaim();
                }
            }

            currentHap.dirty = true;
            outputDirty = true;
        }

        private void ProcessRawPhysicalOutputCommand(byte[] report)
        {
            if (conType == ConnectionType.BT)
            {
                bool published =
                    EnsureBluetoothCombinedOutputTransport() &&
                    UpdateCachedBluetoothCombinedState(report, 0) &&
                    TryPublishCachedBluetoothCombinedState(
                        includeNativeHaptics: true,
                        activeStatus:
                            "Merged game output state into the unified Bluetooth stream.",
                        idleReportDescription: "native controller state",
                        out _);
                if (!published)
                {
                    RequestUnifiedBluetoothOutputTransportRecovery();
                }
                return;
            }

            Array.Clear(outputReport, 0, outputReport.Length);
            if (outputReport.Length < USB_OUTPUT_CHANGE_LENGTH)
            {
                return;
            }
            Buffer.BlockCopy(report, 0, outputReport, 0,
                USB_OUTPUT_CHANGE_LENGTH);
            WriteReport();
        }

        private void PublishBluetoothControllerObservation(
            uint controllerTimestamp, long arrivalQpc, byte mediaBuffer)
        {
            long observationGeneration = Volatile.Read(
                ref bluetoothObservationGeneration);
            if (Volatile.Read(ref bluetoothObservationStopRequested) != 0)
            {
                return;
            }

            Interlocked.Increment(ref bluetoothObservationVersion);
            Volatile.Write(ref bluetoothObservedControllerTimestamp,
                unchecked((int)controllerTimestamp));
            Volatile.Write(ref bluetoothObservedInputArrivalQpc, arrivalQpc);
            Volatile.Write(ref bluetoothObservedMediaBuffer, mediaBuffer);
            Volatile.Write(ref bluetoothObservedGeneration,
                observationGeneration);
            Interlocked.Increment(ref bluetoothObservationVersion);
            bluetoothObservationSignal.Set();
        }

        private void PublishBluetoothMicrophoneClockObservation(byte sequence,
            long arrivalQpc)
        {
            long observationGeneration = Volatile.Read(
                ref bluetoothObservationGeneration);
            if (Volatile.Read(ref bluetoothObservationStopRequested) != 0)
            {
                return;
            }
            Interlocked.Increment(ref bluetoothMicrophoneObservationVersion);
            Volatile.Write(ref bluetoothObservedMicrophoneSequence, sequence);
            Volatile.Write(ref bluetoothObservedMicrophoneArrivalQpc,
                arrivalQpc);
            Volatile.Write(ref bluetoothObservedMicrophoneGeneration,
                observationGeneration);
            Interlocked.Increment(ref bluetoothMicrophoneObservationVersion);
            bluetoothObservationSignal.Set();
        }

        private void BluetoothObservationLoop(long generation)
        {
            long consumedVersion = 0;
            long consumedMicrophoneVersion = 0;
            while (Volatile.Read(ref bluetoothObservationStopRequested) == 0 &&
                generation == Volatile.Read(ref bluetoothObservationGeneration))
            {
                bluetoothObservationSignal.WaitOne();
                if (Volatile.Read(ref bluetoothObservationStopRequested) != 0)
                {
                    break;
                }

                long microphoneFirstVersion = 0;
                long microphoneSecondVersion = 0;
                int microphoneSequence = -1;
                long microphoneArrivalQpc = 0;
                long microphoneGeneration = 0;
                do
                {
                    microphoneFirstVersion = Interlocked.Read(
                        ref bluetoothMicrophoneObservationVersion);
                    if ((microphoneFirstVersion & 1) != 0)
                    {
                        Thread.Yield();
                        continue;
                    }
                    microphoneSequence = Volatile.Read(
                        ref bluetoothObservedMicrophoneSequence);
                    microphoneArrivalQpc = Volatile.Read(
                        ref bluetoothObservedMicrophoneArrivalQpc);
                    microphoneGeneration = Volatile.Read(
                        ref bluetoothObservedMicrophoneGeneration);
                    microphoneSecondVersion = Interlocked.Read(
                        ref bluetoothMicrophoneObservationVersion);
                }
                while (microphoneFirstVersion != microphoneSecondVersion ||
                    (microphoneSecondVersion & 1) != 0);
                if (microphoneSecondVersion != consumedMicrophoneVersion &&
                    microphoneSecondVersion != 0 &&
                    microphoneGeneration == generation &&
                    microphoneArrivalQpc > 0)
                {
                    SignalBluetoothAudioPacerMicrophoneFrame(
                        (byte)microphoneSequence);
                    consumedMicrophoneVersion = microphoneSecondVersion;
                }

                long firstVersion = 0;
                long secondVersion = 0;
                uint controllerTimestamp = 0;
                long arrivalQpc = 0;
                long observationGeneration = 0;
                byte mediaBuffer = 0;
                do
                {
                    firstVersion = Interlocked.Read(
                        ref bluetoothObservationVersion);
                    if ((firstVersion & 1) != 0)
                    {
                        Thread.Yield();
                        continue;
                    }
                    controllerTimestamp = unchecked((uint)Volatile.Read(
                        ref bluetoothObservedControllerTimestamp));
                    arrivalQpc = Volatile.Read(
                        ref bluetoothObservedInputArrivalQpc);
                    mediaBuffer = (byte)Volatile.Read(
                        ref bluetoothObservedMediaBuffer);
                    observationGeneration = Volatile.Read(
                        ref bluetoothObservedGeneration);
                    secondVersion = Interlocked.Read(
                        ref bluetoothObservationVersion);
                }
                while (firstVersion != secondVersion ||
                    (secondVersion & 1) != 0);

                if (secondVersion == consumedVersion || secondVersion == 0 ||
                    arrivalQpc <= 0 || observationGeneration != generation)
                {
                    continue;
                }
                consumedVersion = secondVersion;

                bool clockRatioUpdated = bluetoothControllerClock.Observe(
                    controllerTimestamp, arrivalQpc);
                long previousPhasePublish = Volatile.Read(
                    ref bluetoothLastInputPhasePublishQpc);
                if ((clockRatioUpdated || arrivalQpc - previousPhasePublish >=
                        Stopwatch.Frequency *
                            BluetoothInputPhasePublishMilliseconds / 1000) &&
                    Interlocked.CompareExchange(
                        ref bluetoothLastInputPhasePublishQpc, arrivalQpc,
                        previousPhasePublish) == previousPhasePublish)
                {
                    TryUpdateBluetoothAudioPacerCadenceRatio(
                        DualSenseControllerClockStable ?
                            bluetoothControllerClock.Ratio : 1.0);
                }

                long previousBufferPublish = Volatile.Read(
                    ref bluetoothLastMediaBufferPublishQpc);
                if (arrivalQpc - previousBufferPublish >=
                        Stopwatch.Frequency *
                            BluetoothMediaBufferPublishMilliseconds / 1000 &&
                    Interlocked.CompareExchange(
                        ref bluetoothLastMediaBufferPublishQpc, arrivalQpc,
                        previousBufferPublish) == previousBufferPublish)
                {
                    TryUpdateBluetoothAudioPacerMediaBuffer(mediaBuffer,
                        arrivalQpc);
                }
            }
        }

        private static void RecordWorkerMaximum(ref long target,
            long candidate)
        {
            if (candidate <= 0)
            {
                return;
            }
            long current = Interlocked.Read(ref target);
            while (candidate > current)
            {
                long observed = Interlocked.CompareExchange(ref target,
                    candidate, current);
                if (observed == current)
                {
                    return;
                }
                current = observed;
            }
        }

        private void BluetoothMicrophoneDispatchLoop(long generation)
        {
            while (Volatile.Read(
                    ref bluetoothMicrophoneDispatchStopRequested) == 0 &&
                generation == Volatile.Read(ref physicalOutputGeneration))
            {
                bluetoothMicrophoneDispatchSignal.WaitOne();
                while (Volatile.Read(
                        ref bluetoothMicrophoneDispatchStopRequested) == 0)
                {
                    long requestGeneration;
                    long arrivalTimestamp;
                    byte sequence;
                    lock (bluetoothMicrophoneFrameLock)
                    {
                        if (bluetoothMicrophoneFrameCount == 0)
                        {
                            break;
                        }
                        int slot = bluetoothMicrophoneFrameHead;
                        Buffer.BlockCopy(bluetoothMicrophoneFrameSlots[slot], 0,
                            bluetoothMicrophoneDispatchBuffer, 0,
                            BluetoothMicrophonePayloadLength);
                        requestGeneration =
                            bluetoothMicrophoneFrameGenerations[slot];
                        arrivalTimestamp =
                            bluetoothMicrophoneFrameArrivalTimestamps[slot];
                        sequence = bluetoothMicrophoneFrameSequences[slot];
                        bluetoothMicrophoneFrameGenerations[slot] = 0;
                        bluetoothMicrophoneFrameArrivalTimestamps[slot] = 0;
                        bluetoothMicrophoneFrameHead =
                            (bluetoothMicrophoneFrameHead + 1) %
                                bluetoothMicrophoneFrameSlots.Length;
                        bluetoothMicrophoneFrameCount--;
                    }

                    if (requestGeneration != Interlocked.Read(
                            ref bluetoothMicrophoneRequestGeneration) ||
                        Volatile.Read(
                            ref bluetoothMicrophoneStreamingRequested) == 0)
                    {
                        continue;
                    }
                    if (arrivalTimestamp > 0)
                    {
                        long dispatchAge = Stopwatch.GetTimestamp() -
                            arrivalTimestamp;
                        physicalMicrophoneDispatchLatency.Observe(dispatchAge);
                        RecordWorkerMaximum(
                            ref bluetoothMicrophoneMaximumQueueAgeTicks,
                            dispatchAge);
                    }
                    Volatile.Write(
                        ref bluetoothMicrophoneLastDispatchedSequence,
                        sequence);
                    try
                    {
                        BluetoothMicrophoneOpusFrameReceived?.Invoke(this,
                            bluetoothMicrophoneDispatchBuffer);
                    }
                    catch (Exception ex)
                    {
                        if (Global.VerboseStartupLogging)
                        {
                            AppLogger.LogToGui(
                                $"DualSense Bluetooth microphone consumer failed: {ex.GetType().Name}: {ex.Message}",
                                true);
                        }
                    }
                }
            }
        }

        public override void PostInit()
        {
            HidDevice hidDevice = hDevice;
            deviceType = InputDeviceType.DualSense;
            DetermineSubType(hidDevice);

            gyroMouseSensSettings = new GyroMouseSensDualSense();
            optionsStore = nativeOptionsStore = new DualSenseControllerOptions(deviceType);
            SetupOptionsEvents();

            conType = DetermineConnectionType(hDevice);
            Mac = hDevice.ReadSerial(SerialReportID);

            if (conType == ConnectionType.USB)
            {
                dataBytes = new InputReportDataBytesUSB();

                inputReport = new byte[64];
                outputReport = new byte[hDevice.Capabilities.OutputReportByteLength];
                outReportBuffer = new byte[hDevice.Capabilities.OutputReportByteLength];

                warnInterval = WARN_INTERVAL_USB;
            }
            else
            {
                //btInputReport = new byte[BT_INPUT_REPORT_LENGTH];
                //inputReport = new byte[BT_INPUT_REPORT_LENGTH - 2];
                // Only plan to use one input report array. Avoid copying data
                inputReport = new byte[BT_INPUT_REPORT_LENGTH];
                // Default DS4 logic while writing data to gamepad
                outputReport = new byte[BT_OUTPUT_REPORT_LENGTH];
                outReportBuffer = new byte[BT_OUTPUT_REPORT_LENGTH];

                warnInterval = WARN_INTERVAL_BT;
                synced = isValidSerial();
            }

            if (runCalib)
                RefreshCalibration();

            // Attempt to grab hardware, firmware, and update version
            // data from DualSense controller. Referenced hid-playstation Linux
            // driver
            byte[] firmwareInfoData = new byte[64];
            firmwareInfoData[0] = FEATURE_FIRMWARE_INFO_ID;
            bool featureFirmRead = false;
            if (conType == ConnectionType.BT)
            {
                featureFirmRead = ReadBTFeatureReport(firmwareInfoData, 64);
            }
            else
            {
                featureFirmRead = hDevice.readFeatureData(firmwareInfoData);
            }

            if (featureFirmRead)
            {
                hwVersion = firmwareInfoData[24] |
                    (uint)(firmwareInfoData[25] << 8) |
                    (uint)(firmwareInfoData[26] << 16) |
                    (uint)(firmwareInfoData[27] << 24);

                fwVersion = firmwareInfoData[28] |
                    (uint)(firmwareInfoData[29] << 8) |
                    (uint)(firmwareInfoData[30] << 16) |
                    (uint)(firmwareInfoData[31] << 24);

                updateVersion = firmwareInfoData[44] | (uint)(firmwareInfoData[45] << 8);

                // Accurate rumble defaults to true. Made device default to false if
                // grabbed update version is too old
                int versionCheckAccurate = DSFeatureVersion(2, 21);
                if (updateVersion < versionCheckAccurate)
                {
                    UseAccurateRumble = false;
                }
            }

            if (conType == ConnectionType.BT)
            {
                // The V5 helper owns the physical Bluetooth HID
                // writer for this entire connection, including the idle period
                // before a virtual controller or audio endpoint is active.
                // Startup state is queued there; never inject a legacy 0x31.
                InitializeUnifiedBluetoothOutputTransport(
                    "initial controller state");
            }
        }

        private bool ReadBTFeatureReport(byte[] buffer, int size)
        {
            bool result = true;
            bool found = false;
            int crc32Pos = size - 4;
            for (int tries = 0; !found && tries < 5; tries++)
            {
                hDevice.readFeatureData(buffer);
                uint recvCrc32 = buffer[crc32Pos] |
                                (uint)(buffer[crc32Pos + 1] << 8) |
                                (uint)(buffer[crc32Pos + 2] << 16) |
                                (uint)(buffer[crc32Pos + 3] << 24);

                uint calcCrc32 = ~Crc32Algorithm.Compute(new byte[] { 0xA3 });
                calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref buffer, 0, crc32Pos);
                bool validCrc = recvCrc32 == calcCrc32;
                if (!validCrc && tries >= 5)
                {
                    AppLogger.LogToGui("Feature report read failure", true);
                    continue;
                }
                else if (validCrc)
                {
                    found = true;
                }
            }

            result = found;
            return result;
        }

        private int DSFeatureVersion(int major, int minor)
        {
            return ((major & 0xFF) << 8 | (minor & 0xFF));
        }

        private void DetermineSubType(HidDevice hidDevice)
        {
            subType = DeviceSubType.DualSense;
            if (hidDevice.Attributes.VendorId == DS4Devices.SONY_VID &&
                hidDevice.Attributes.ProductId == 0x0DF2)
            {
                subType = DeviceSubType.DSEdge;
            }
        }

        public static ConnectionType DetermineConnectionType(HidDevice hidDevice)
        {
            ConnectionType result;
            if (hidDevice.Capabilities.InputReportByteLength == 64)
            {
                result = ConnectionType.USB;
            }
            else
            {
                result = ConnectionType.BT;
            }

            return result;
        }

        public override bool DisconnectBT(bool callRemoval = false)
        {
            return base.DisconnectBT(callRemoval);
        }

        public override bool DisconnectDongle(bool remove = false)
        {
            // Do Nothing
            return true;
        }

        public override bool DisconnectWireless(bool callRemoval = false)
        {
            return base.DisconnectWireless(callRemoval);
        }

        public override bool IsAlive()
        {
            return synced;
        }

        public override void RefreshCalibration()
        {
            byte[] calibration = new byte[41];
            calibration[0] = conType == ConnectionType.BT ? (byte)0x05 : (byte)0x05;

            if (conType == ConnectionType.BT)
            {
                bool found = false;
                for (int tries = 0; !found && tries < 5; tries++)
                {
                    hDevice.readFeatureData(calibration);
                    uint recvCrc32 = calibration[DS4_FEATURE_REPORT_5_CRC32_POS] |
                                (uint)(calibration[DS4_FEATURE_REPORT_5_CRC32_POS + 1] << 8) |
                                (uint)(calibration[DS4_FEATURE_REPORT_5_CRC32_POS + 2] << 16) |
                                (uint)(calibration[DS4_FEATURE_REPORT_5_CRC32_POS + 3] << 24);

                    uint calcCrc32 = ~Crc32Algorithm.Compute(new byte[] { 0xA3 });
                    calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref calibration, 0, DS4_FEATURE_REPORT_5_LEN - 4);
                    bool validCrc = recvCrc32 == calcCrc32;
                    if (!validCrc && tries >= 5)
                    {
                        AppLogger.LogToGui("Gyro Calibration Failed", true);
                        continue;
                    }
                    else if (validCrc)
                    {
                        found = true;
                    }
                }

                sixAxis.setCalibrationData(ref calibration, true);
            }
            else
            {
                hDevice.readFeatureData(calibration);
                sixAxis.setCalibrationData(ref calibration, true);
            }
        }

        public override void StartUpdate()
        {
            this.inputReportErrorCount = 0;
            Volatile.Write(ref bluetoothOutputTransportStopping, 0);
            Volatile.Write(ref bluetoothAudioPacerRetryAfterTimestamp, 0);
            lock (bluetoothSpeakerClockClaimLock)
            {
                bluetoothSpeakerClockActiveClaim = 0;
                bluetoothSpeakerClockLeaseExpiryTimestamp = 0;
            }
            if (ds4Input == null)
            {
                StartPhysicalWorkers();
                if (conType == ConnectionType.BT)
                {
                    //ds4Output = new Thread(performDs4Output);
                    //ds4Output.Priority = ThreadPriority.Normal;
                    //ds4Output.Name = "DS4 Output thread: " + Mac;
                    //ds4Output.IsBackground = true;
                    //ds4Output.Start();

                    timeoutCheckThread = new Thread(TimeoutTestThread);
                    timeoutCheckThread.Priority = ThreadPriority.BelowNormal;
                    timeoutCheckThread.Name = "DualSense Timeout thread: " + Mac;
                    timeoutCheckThread.IsBackground = true;
                    timeoutCheckThread.Start();
                }
                //else
                //{
                //    ds4Output = new Thread(OutReportCopy);
                //    ds4Output.Priority = ThreadPriority.Normal;
                //    ds4Output.Name = "DS4 Arr Copy thread: " + Mac;
                //    ds4Output.IsBackground = true;
                //    ds4Output.Start();
                //}

                ds4Input = new Thread(ReadInput);
                ds4Input.Priority = ThreadPriority.AboveNormal;
                ds4Input.Name = "DualSense Input thread: " + Mac;
                ds4Input.IsBackground = true;
                ds4Input.Start();
            }
            else
                Console.WriteLine("Thread already running for DS4: " + Mac);
        }

        private void TimeoutTestThread()
        {
            while (!timeoutExecuted)
            {
                if (timeoutEvent)
                {
                    timeoutExecuted = true;

                    // Request serial feature report data. Causes Windows to notice the dead
                    // device.
                    byte[] tmpFeatureData = new byte[64];
                    tmpFeatureData[0] = SERIAL_FEATURE_ID;
                    hDevice.readFeatureData(tmpFeatureData); // Kick Windows into noticing the disconnection.
                }
                else
                {
                    timeoutEvent = true;
                    Thread.Sleep(READ_STREAM_TIMEOUT);
                }
            }
        }

        private unsafe void ReadInput()
        {
            using global::DS4Windows.MultimediaThreadRegistration mmcss =
                conType == ConnectionType.BT ?
                    global::DS4Windows.MultimediaThreadRegistration.EnterGames() :
                    default;
            unchecked
            {
                Debouncer = SetupDebouncer();
                firstActive = DateTime.UtcNow;
                NativeMethods.HidD_SetNumInputBuffers(hDevice.SafeReadHandle.DangerousGetHandle(),
                    conType == ConnectionType.BT ? 64 : 3);
                using PipelinedInputReportReader inputReader =
                    hDevice.CreatePipelinedInputReportReader(inputReport);
                double[] latencySamples = new double[20];
                int latencySampleIndex = 0;
                int tempLatencyCount = 0;
                Latency = 0.0;
                lastTimeElapsedDouble = 0.0;
                lastTimeElapsed = 0;
                long oldtime = 0;
                string currerror = string.Empty;
                long testelapsed = 0;
                timeoutEvent = false;
                ds4InactiveFrame = true;
                idleInput = true;
                bool syncWriteReport = conType != ConnectionType.BT;
                //bool forceWrite = false;

                int maxBatteryValue = 0;
                int tempBattery = 0;
                bool tempCharging = charging;
                bool tempFull = false;
                uint tempStamp = 0;
                long bluetoothObservationArrivalQpc = 0;
                byte bluetoothObservationMediaBuffer = 0;
                double elapsedDeltaTime = 0.0;
                uint tempDelta = 0;
                byte tempByte = 0;
                int CRC32_POS_1 = BT_INPUT_REPORT_CRC32_POS + 1,
                    CRC32_POS_2 = BT_INPUT_REPORT_CRC32_POS + 2,
                    CRC32_POS_3 = BT_INPUT_REPORT_CRC32_POS + 3;
                int crcpos = BT_INPUT_REPORT_CRC32_POS;
                int crcoffset = 0;
                double latencySum = 0.0;
                int reportOffset = conType == ConnectionType.BT ? 1 : 0;

                // Run continuous calibration on Gyro when starting input loop
                sixAxis.ResetContinuousCalibration();
                standbySw.Start();

                while (!exitInputThread)
                {
                    oldCharging = charging;
                    currerror = string.Empty;
                    bool idleDisconnectPending = false;

                    readWaitEv.Set();

                    if (conType == ConnectionType.BT)
                    {
                        timeoutEvent = false;
                    }

                    long readWaitStartedAt = Stopwatch.GetTimestamp();
                    HidDevice.ReadStatus res = inputReader.ReadNext(
                        out byte[] completedReport, out int readWinError,
                        out long physicalReadObservedAt,
                        out long readRearmDuration);
                    if (res == HidDevice.ReadStatus.Success)
                    {
                        inputReport = completedReport;
                        physicalReadObservationWaitLatency.Observe(
                            physicalReadObservedAt - readWaitStartedAt);
                        physicalReadRearmLatency.Observe(readRearmDuration);
                    }

                    if (conType == ConnectionType.BT)
                    {
                        if (res == HidDevice.ReadStatus.Success)
                        {
                            if (IsBluetoothMicrophoneFrame(inputReport))
                            {
                                long microphoneArrivalQpc =
                                    Stopwatch.GetTimestamp();
                                PublishBluetoothMicrophoneClockObservation(
                                    inputReport[2], microphoneArrivalQpc);
                                inputReportErrorCount = 0;
                                RecordBluetoothMicrophoneFrame(inputReport,
                                    microphoneArrivalQpc);
                                // V5 treats 0x31 microphone packets as a
                                // media-only input lane. Do not let their 100 Hz
                                // cadence pump pending state or publish a
                                // competing output report; continuous 0x36
                                // media carries the latest controller state.
                                DrainQueuedInputEvents();
                                readWaitEv.Reset();
                                continue;
                            }

                            if (!IsBluetoothNormalInputFrame(inputReport))
                            {
                                Interlocked.Increment(ref bluetoothRejectedInputFrames);
                                Volatile.Write(ref bluetoothLastRejectedInputTag,
                                    inputReport[1]);
                                inputReportErrorCount = 0;
                                DrainQueuedInputEvents();
                                readWaitEv.Reset();
                                continue;
                            }

                            uint recvCrc32 = inputReport[BT_INPUT_REPORT_CRC32_POS] |
                                (uint)(inputReport[CRC32_POS_1] << 8) |
                                (uint)(inputReport[CRC32_POS_2] << 16) |
                                (uint)(inputReport[CRC32_POS_3] << 24);

                            uint calcCrc32 = ~Crc32Algorithm.CalculateFasterBT78Hash(ref HamSeed, ref inputReport, ref crcoffset, ref crcpos);
                            if (recvCrc32 != calcCrc32)
                            {
                                cState.PacketCounter = pState.PacketCounter + 1; //still increase so we know there were lost packets
                                if (this.inputReportErrorCount >= 10)
                                {
                                    exitInputThread = true;

                                    readWaitEv.Reset();
                                    //sendOutputReport(true, true); // Kick Windows into noticing the disconnection.
                                    isDisconnecting = true;
                                    RequestPhysicalRemoval(
                                        PhysicalInputFailureKind.Crc);

                                    timeoutExecuted = true;
                                    continue;
                                }
                                else
                                {
                                    this.inputReportErrorCount++;
                                }

                                readWaitEv.Reset();
                                continue;
                            }
                            else
                            {
                                this.inputReportErrorCount = 0;
                            }
                        }
                        else
                        {
                            int winError = res ==
                                HidDevice.ReadStatus.WaitTimedOut ? 0 :
                                readWinError;

                            exitInputThread = true;
                            readWaitEv.Reset();
                            //SendEmptyOutputReport();
                            //sendOutputReport(true, true); // Kick Windows into noticing the disconnection.
                            isDisconnecting = true;
                            RequestPhysicalRemoval(res ==
                                HidDevice.ReadStatus.WaitTimedOut ?
                                PhysicalInputFailureKind.BluetoothTimeout :
                                PhysicalInputFailureKind.BluetoothRead,
                                winError);

                            timeoutExecuted = true;
                            continue;
                        }
                    }
                    else
                    {
                        if (res != HidDevice.ReadStatus.Success)
                        {
                            int winError = res ==
                                HidDevice.ReadStatus.WaitTimedOut ? 0 :
                                readWinError;

                            exitInputThread = true;
                            readWaitEv.Reset();
                            isDisconnecting = true;
                            RequestPhysicalRemoval(res ==
                                HidDevice.ReadStatus.WaitTimedOut ?
                                PhysicalInputFailureKind.UsbTimeout :
                                PhysicalInputFailureKind.UsbRead,
                                winError);

                            timeoutExecuted = true;
                            continue;
                        }

                        if (!TryAcceptUsbNormalInputFrame(inputReport))
                        {
                            // Unknown USB report IDs are neither controller
                            // state nor same-report status. Record only fixed
                            // telemetry here; the input thread must not log or
                            // invoke subscribers for a rejected frame.
                            inputReportErrorCount = 0;
                            DrainQueuedInputEvents();
                            readWaitEv.Reset();
                            continue;
                        }
                    }
                    readWaitEv.Wait();
                    readWaitEv.Reset();

                    if (oldtime != 0)
                    {
                        testelapsed = physicalReadObservedAt - oldtime;
                        lastTimeElapsedDouble = testelapsed *
                            (1.0 / Stopwatch.Frequency) * 1000.0;
                        lastTimeElapsed = (long)lastTimeElapsedDouble;
                        physicalReportObservationIntervalLatency.Observe(
                            testelapsed);

                        if (tempLatencyCount == latencySamples.Length)
                        {
                            latencySum -= latencySamples[latencySampleIndex];
                        }
                        else
                        {
                            tempLatencyCount++;
                        }
                        latencySamples[latencySampleIndex] =
                            lastTimeElapsedDouble;
                        latencySum += lastTimeElapsedDouble;
                        latencySampleIndex = (latencySampleIndex + 1) %
                            latencySamples.Length;
                        Latency = latencySum / tempLatencyCount;
                    }
                    oldtime = physicalReadObservedAt;

                    if (conType == ConnectionType.BT && inputReport[0] != 0x31)
                    {
                        // Received incorrect report, skip it
                        continue;
                    }

                    utcNow = DateTime.UtcNow; // timestamp with UTC in case system time zone changes

                    cState.PacketCounter = pState.PacketCounter + 1;
                    cState.ReportTimeStamp = utcNow;
                    TryExtractPhysicalInputStatus(inputReport, reportOffset,
                        subType == DeviceSubType.DSEdge,
                        out cState.DualSenseRawInputStatus);
                    cState.LX = inputReport[1 + reportOffset];
                    cState.LY = inputReport[2 + reportOffset];
                    cState.RX = inputReport[3 + reportOffset];
                    cState.RY = inputReport[4 + reportOffset];
                    cState.L2 = inputReport[5 + reportOffset];
                    cState.R2 = inputReport[6 + reportOffset];
                    cState.L2Raw = cState.L2;
                    cState.R2Raw = cState.R2;

                    // DS4 Frame Counter range is [0-127]. DS version range is [0-255]. Convert
                    cState.FrameCounter = (byte)(inputReport[7 + reportOffset] % 128);
                    tempByte = inputReport[8 + reportOffset];
                    cState.Triangle = (tempByte & (1 << 7)) != 0;
                    cState.Circle = (tempByte & (1 << 6)) != 0;
                    cState.Cross = (tempByte & (1 << 5)) != 0;
                    cState.Square = (tempByte & (1 << 4)) != 0;

                    // First 4 bits denote dpad state. Clock representation
                    // with 8 meaning centered and 0 meaning DpadUp.
                    byte dpad_state = (byte)(tempByte & 0x0F);

                    switch (dpad_state)
                    {
                        case 0: cState.DpadUp = true; cState.DpadDown = false; cState.DpadLeft = false; cState.DpadRight = false; break;
                        case 1: cState.DpadUp = true; cState.DpadDown = false; cState.DpadLeft = false; cState.DpadRight = true; break;
                        case 2: cState.DpadUp = false; cState.DpadDown = false; cState.DpadLeft = false; cState.DpadRight = true; break;
                        case 3: cState.DpadUp = false; cState.DpadDown = true; cState.DpadLeft = false; cState.DpadRight = true; break;
                        case 4: cState.DpadUp = false; cState.DpadDown = true; cState.DpadLeft = false; cState.DpadRight = false; break;
                        case 5: cState.DpadUp = false; cState.DpadDown = true; cState.DpadLeft = true; cState.DpadRight = false; break;
                        case 6: cState.DpadUp = false; cState.DpadDown = false; cState.DpadLeft = true; cState.DpadRight = false; break;
                        case 7: cState.DpadUp = true; cState.DpadDown = false; cState.DpadLeft = true; cState.DpadRight = false; break;
                        case 8:
                        default: cState.DpadUp = false; cState.DpadDown = false; cState.DpadLeft = false; cState.DpadRight = false; break;
                    }

                    tempByte = inputReport[9 + reportOffset];
                    cState.R3 = (tempByte & (1 << 7)) != 0;
                    cState.L3 = (tempByte & (1 << 6)) != 0;
                    cState.Options = (tempByte & (1 << 5)) != 0;
                    cState.Share = (tempByte & (1 << 4)) != 0;
                    cState.R2Btn = (tempByte & (1 << 3)) != 0;
                    cState.L2Btn = (tempByte & (1 << 2)) != 0;
                    cState.R1 = (tempByte & (1 << 1)) != 0;
                    cState.L1 = (tempByte & (1 << 0)) != 0;

                    tempByte = inputReport[10 + reportOffset];
                    cState.PS = (tempByte & (1 << 0)) != 0;
                    cState.TouchButton = (tempByte & 0x02) != 0;

                    cState.OutputTouchButton = cState.TouchButton;
                    cState.Mute = (tempByte & (1 << 2)) != 0;
                    cState.FnL = (tempByte & (1 << 4)) != 0;
                    cState.FnR = (tempByte & (1 << 5)) != 0;
                    cState.BLP = (tempByte & (1 << 6)) != 0;
                    cState.BRP = (tempByte & (1 << 7)) != 0;

                    if ((this.featureSet & VidPidFeatureSet.NoBatteryReading) == 0)
                    {
                        tempByte = inputReport[54 + reportOffset];
                        tempCharging = (tempByte & 0x08) != 0;
                        if (tempCharging != charging)
                        {
                            charging = tempCharging;
                            Interlocked.Or(
                                ref deviceStatusNotificationPending,
                                DeviceStatusChargingChanged);
                        }

                        tempByte = inputReport[53 + reportOffset];
                        tempFull = (tempByte & 0x20) != 0; // Check for Full status
                        maxBatteryValue = BATTERY_MAX;
                        if (tempFull)
                        {
                            // Full Charge flag found
                            tempBattery = 100;
                        }
                        else
                        {
                            // Partial charge
                            tempBattery = (tempByte & 0x0F) * 100 / maxBatteryValue;
                            tempBattery = Math.Min(tempBattery, 100);
                        }

                        if (tempBattery != battery)
                        {
                            battery = tempBattery;
                            Interlocked.Or(
                                ref deviceStatusNotificationPending,
                                DeviceStatusBatteryChanged);
                        }

                        cState.Battery = (byte)battery;
                        //System.Diagnostics.Debug.WriteLine("CURRENT BATTERY: " + (inputReport[30] & 0x0f) + " | " + tempBattery + " | " + battery);
                    }
                    else
                    {
                        // Some gamepads don't send battery values in DS4 compatible data fields, so use dummy 99% value to avoid constant low battery warnings
                        //priorInputReport30 = 0x0F;
                        battery = 99;
                        cState.Battery = 99;
                    }

                    tempStamp = inputReport[28+reportOffset] |
                                (uint)(inputReport[29+reportOffset] << 8) |
                                (uint)(inputReport[30+reportOffset] << 16) |
                                (uint)(inputReport[31+reportOffset] << 24);

                    if (conType == ConnectionType.BT)
                    {
                        long inputArrivalQpc = Stopwatch.GetTimestamp();
                        Volatile.Write(ref bluetoothLastInputArrivalQpc,
                            inputArrivalQpc);
                        bluetoothObservationArrivalQpc = inputArrivalQpc;
                        bluetoothObservationMediaBuffer = inputReport[65];
                    }

                    if (timeStampInit == false)
                    {
                        timeStampInit = true;
                        deltaTimeCurrent = tempStamp * 1u / 3u;
                    }
                    else if (timeStampPrevious > tempStamp)
                    {
                        tempDelta = uint.MaxValue - timeStampPrevious + tempStamp + 1u;
                        deltaTimeCurrent = tempDelta * 1u / 3u;
                    }
                    else
                    {
                        tempDelta = tempStamp - timeStampPrevious;
                        deltaTimeCurrent = tempDelta * 1u / 3u;
                    }

                    //if (tempStamp == timeStampPrevious)
                    //{
                    //    Console.WriteLine("PINEAPPLES");
                    //}

                    // Make sure timestamps don't match
                    if (deltaTimeCurrent != 0)
                    {
                        elapsedDeltaTime = 0.000001 * deltaTimeCurrent; // Convert from microseconds to seconds
                        cState.totalMicroSec = pState.totalMicroSec + deltaTimeCurrent;
                    }
                    else
                    {
                        // Duplicate timestamp. Use system clock for elapsed time instead
                        elapsedDeltaTime = lastTimeElapsedDouble * .001;
                        cState.totalMicroSec = pState.totalMicroSec + (uint)(elapsedDeltaTime * 1000000);
                    }

                    //Console.WriteLine("{0} {1} {2} {3} {4} Diff({5}) TSms({6}) Sys({7})", tempStamp, inputReport[31 + reportOffset], inputReport[30 + reportOffset], inputReport[29 + reportOffset], inputReport[28 + reportOffset], tempStamp - timeStampPrevious, elapsedDeltaTime, lastTimeElapsedDouble * 0.001);

                    cState.elapsedTime = elapsedDeltaTime;
                    cState.ds4Timestamp = (ushort)((tempStamp / 16) % ushort.MaxValue);
                    timeStampPrevious = tempStamp;

                    //elapsedDeltaTime = lastTimeElapsedDouble * .001;
                    //cState.elapsedTime = elapsedDeltaTime;
                    //cState.totalMicroSec = pState.totalMicroSec + (uint)(elapsedDeltaTime * 1000000);

                    // Simpler touch storing
                    cState.TrackPadTouch0.RawTrackingNum = inputReport[33+reportOffset];
                    cState.TrackPadTouch0.Id = (byte)(inputReport[33+reportOffset] & 0x7f);
                    cState.TrackPadTouch0.IsActive = (inputReport[33+reportOffset] & 0x80) == 0;
                    cState.TrackPadTouch0.X = (short)(((ushort)(inputReport[35+reportOffset] & 0x0f) << 8) | (ushort)(inputReport[34+reportOffset]));
                    cState.TrackPadTouch0.Y = (short)(((ushort)(inputReport[36+reportOffset]) << 4) | ((ushort)(inputReport[35+reportOffset] & 0xf0) >> 4));

                    cState.TrackPadTouch1.RawTrackingNum = inputReport[37+reportOffset];
                    cState.TrackPadTouch1.Id = (byte)(inputReport[37+reportOffset] & 0x7f);
                    cState.TrackPadTouch1.IsActive = (inputReport[37+reportOffset] & 0x80) == 0;
                    cState.TrackPadTouch1.X = (short)(((ushort)(inputReport[39+reportOffset] & 0x0f) << 8) | (ushort)(inputReport[38+reportOffset]));
                    cState.TrackPadTouch1.Y = (short)(((ushort)(inputReport[40+reportOffset]) << 4) | ((ushort)(inputReport[39+reportOffset] & 0xf0) >> 4));

                    // XXX DS4State mapping needs fixup, turn touches into an array[4] of structs.  And include the touchpad details there instead.
                    try
                    {
                        // Only care if one touch packet is detected. Other touch packets
                        // don't seem to contain relevant data. ds4drv does not use them either.
                        int touchOffset = 0;

                        // TouchPacketCounter is at the end of the Touchpad payload with the DualSense
                        cState.TouchPacketCounter = inputReport[8 + TOUCHPAD_DATA_OFFSET + reportOffset + touchOffset];
                        cState.Touch1 = (inputReport[0 + TOUCHPAD_DATA_OFFSET + reportOffset + touchOffset] >> 7) != 0 ? false : true; // finger 1 detected
                        cState.Touch1Identifier = (byte)(inputReport[0 + TOUCHPAD_DATA_OFFSET + reportOffset + touchOffset] & 0x7f);
                        cState.Touch2 = (inputReport[4 + TOUCHPAD_DATA_OFFSET + reportOffset + touchOffset] >> 7) != 0 ? false : true; // finger 2 detected
                        cState.Touch2Identifier = (byte)(inputReport[4 + TOUCHPAD_DATA_OFFSET + reportOffset + touchOffset] & 0x7f);
                        cState.Touch1Finger = cState.Touch1 || cState.Touch2; // >= 1 touch detected
                        cState.Touch2Fingers = cState.Touch1 && cState.Touch2; // 2 touches detected
                        int touchX = (((inputReport[2 + TOUCHPAD_DATA_OFFSET + reportOffset + touchOffset] & 0xF) << 8) | inputReport[1 + TOUCHPAD_DATA_OFFSET + reportOffset + touchOffset]);
                        cState.TouchLeft = touchX >= DS4Touchpad.RESOLUTION_X_MAX * 2 / 5 ? false : true;
                        cState.TouchRight = touchX < DS4Touchpad.RESOLUTION_X_MAX * 2 / 5 ? false : true;
                        // Even when idling there is still a touch packet indicating no touch 1 or 2
                        if (synced)
                        {
                            touchpad.handleTouchpad(inputReport, cState, TOUCHPAD_DATA_OFFSET + reportOffset, touchOffset);
                        }
                    }
                    catch (Exception ex) { currerror = $"Touchpad: {ex.Message}"; }

                    fixed (byte* pbInput = &inputReport[16+reportOffset], pbGyro = gyro, pbAccel = accel)
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            pbGyro[i] = pbInput[i];
                        }

                        for (int i = 6; i < 12; i++)
                        {
                            pbAccel[i - 6] = pbInput[i];
                        }

                        if (synced)
                        {
                            sixAxis.handleSixaxis(pbGyro, pbAccel, cState, elapsedDeltaTime);
                        }
                    }

                    /* Debug output of incoming HID data:
                    if (cState.L2 == 0xff && cState.R2 == 0xff)
                    {
                        Console.Write(MacAddress.ToString() + " " + System.DateTime.UtcNow.ToString("o") + ">");
                        for (int i = 0; i < inputReport.Length; i++)
                            Console.Write(" " + inputReport[i].ToString("x2"));
                        Console.WriteLine();
                    }
                    ///*/

                    if (conType == ConnectionType.USB)
                    {
                        if (idleTimeout == 0)
                        {
                            lastActive = utcNow;
                        }
                        else
                        {
                            idleInput = isDS4Idle();
                            if (!idleInput)
                            {
                                lastActive = utcNow;
                            }
                        }
                    }
                    else
                    {
                        bool shouldDisconnect = false;
                        if (!isRemoved && idleTimeout > 0)
                        {
                            idleInput = isDS4Idle();
                            if (idleInput)
                            {
                                DateTime timeout = lastActive + TimeSpan.FromSeconds(idleTimeout);
                                if (!charging)
                                    shouldDisconnect = utcNow >= timeout;
                            }
                            else
                            {
                                lastActive = utcNow;
                            }
                        }
                        else
                        {
                            lastActive = utcNow;
                        }

                        if (shouldDisconnect)
                        {
                            idleDisconnectPending =
                                conType == ConnectionType.BT;
                        }
                    }

                    if (fireReport)
                    {
                        long reportStartedAt = Stopwatch.GetTimestamp();
                        physicalReadToReportLatency.Observe(
                            reportStartedAt - physicalReadObservedAt);
                        Report?.Invoke(this, EventArgs.Empty);
                        long publicationCompletedAt =
                            Stopwatch.GetTimestamp();
                        physicalReportCallbackLatency.Observe(
                            publicationCompletedAt - reportStartedAt);
                        physicalReadToReportReturnLatency.Observe(
                            publicationCompletedAt -
                                physicalReadObservedAt);
                    }

                    // Mapping and virtual scheduler publication complete in
                    // Report before slower physical-output or audio-clock
                    // owners are signaled.
                    if (conType == ConnectionType.BT &&
                        bluetoothObservationArrivalQpc > 0)
                    {
                        PublishBluetoothControllerObservation(tempStamp,
                            bluetoothObservationArrivalQpc,
                            bluetoothObservationMediaBuffer);
                    }
                    QueuePhysicalOutputKeepaliveIfDue();
                    //forceWrite = false;

                    if (!string.IsNullOrEmpty(currerror))
                        error = currerror;
                    else if (!string.IsNullOrEmpty(error))
                        error = string.Empty;

                    cState.CopyTo(pState);

                    DrainQueuedInputEvents();
                    if (idleDisconnectPending)
                    {
                        exitInputThread = true;
                        isDisconnecting = true;
                        RequestPhysicalIdleDisconnect();
                        timeoutExecuted = true;
                    }
                }
            }

            timeoutExecuted = true;
        }

        internal static bool TryExtractPhysicalInputStatus(
            ReadOnlySpan<byte> report, int reportOffset,
            out DualSenseRawInputStatus status) =>
            DualSenseRawInputStatus.TryRead(report, reportOffset, out status);

        internal static bool TryExtractPhysicalInputStatus(
            ReadOnlySpan<byte> report, int reportOffset, bool isEdgeLayout,
            out DualSenseRawInputStatus status)
        {
            bool valid = DualSenseRawInputStatus.TryRead(report, reportOffset,
                out status);
            status.IsEdgeLayout = valid && isEdgeLayout;
            return valid;
        }

        internal bool TryAcceptUsbNormalInputFrame(
            ReadOnlySpan<byte> report)
        {
            if (IsUsbNormalInputFrame(report))
            {
                return true;
            }

            Interlocked.Increment(ref usbRejectedInputFrames);
            Volatile.Write(ref usbLastRejectedInputReportId,
                report.IsEmpty ? -1 : report[0]);
            return false;
        }

        internal static bool IsUsbNormalInputFrame(
            ReadOnlySpan<byte> report) =>
            report.Length == USB_INPUT_REPORT_LENGTH &&
            report[0] == USB_INPUT_REPORT_ID;

        private static bool IsBluetoothMicrophoneFrame(byte[] report)
        {
            return report != null &&
                report.Length == BT_INPUT_REPORT_LENGTH &&
                report[0] == 0x31 &&
                (report[1] & BluetoothMicrophoneInputBit) != 0;
        }

        private static bool IsBluetoothNormalInputFrame(byte[] report)
        {
            return report != null &&
                report.Length == BT_INPUT_REPORT_LENGTH &&
                report[0] == 0x31 &&
                (report[1] & BluetoothMicrophoneInputBit) == 0 &&
                (report[1] & BluetoothNormalInputBit) != 0;
        }

        private void RecordBluetoothMicrophoneFrame(byte[] report,
            long arrivedAt)
        {
            if (report == null ||
                report.Length < BluetoothMicrophonePayloadOffset +
                    BluetoothMicrophonePayloadLength)
            {
                return;
            }

            long requestGeneration = Interlocked.Read(
                ref bluetoothMicrophoneRequestGeneration);
            if (Volatile.Read(ref bluetoothMicrophoneStreamingRequested) == 0)
            {
                return;
            }

            if (arrivedAt <= 0)
            {
                arrivedAt = Stopwatch.GetTimestamp();
            }
            lock (bluetoothMicrophoneFrameLock)
            {
                if (bluetoothMicrophoneFrameCount ==
                    bluetoothMicrophoneFrameSlots.Length)
                {
                    bluetoothMicrophoneFrameGenerations[
                        bluetoothMicrophoneFrameHead] = 0;
                    bluetoothMicrophoneFrameArrivalTimestamps[
                        bluetoothMicrophoneFrameHead] = 0;
                    bluetoothMicrophoneFrameHead =
                        (bluetoothMicrophoneFrameHead + 1) %
                            bluetoothMicrophoneFrameSlots.Length;
                    bluetoothMicrophoneFrameCount--;
                    Interlocked.Increment(ref bluetoothMicrophoneFrameDrops);
                }

                int tail = (bluetoothMicrophoneFrameHead +
                    bluetoothMicrophoneFrameCount) %
                        bluetoothMicrophoneFrameSlots.Length;
                Buffer.BlockCopy(report, BluetoothMicrophonePayloadOffset,
                    bluetoothMicrophoneFrameSlots[tail], 0,
                    BluetoothMicrophonePayloadLength);
                bluetoothMicrophoneFrameGenerations[tail] = requestGeneration;
                bluetoothMicrophoneFrameArrivalTimestamps[tail] = arrivedAt;
                bluetoothMicrophoneFrameSequences[tail] = report[2];
                bluetoothMicrophoneFrameCount++;
            }

            // The generation recheck replaces the old physical-output lock. A
            // disable racing this packet invalidates its ring slot and cannot
            // be cleared by stale completion evidence.
            if (requestGeneration == Interlocked.Read(
                    ref bluetoothMicrophoneRequestGeneration) &&
                Volatile.Read(ref bluetoothMicrophoneStreamingRequested) != 0)
            {
                Interlocked.Exchange(ref bluetoothMicrophoneLastFrameTimestamp,
                    arrivedAt);
                Interlocked.Increment(ref bluetoothMicrophoneFramesReceived);
                Interlocked.Exchange(
                    ref bluetoothMicrophoneControlUpdatePending, 0);
            }
            bluetoothMicrophoneDispatchSignal.Set();
        }

        protected override void StopOutputUpdate()
        {
            // Read/CRC failures invoke this method on the physical input
            // thread. That caller may only publish the lifecycle request; it
            // must resume teardown without waiting for output locks or HID
            // completion. Administrative StopUpdate callers wait on the same
            // dedicated owner so final neutral/release delivery remains
            // synchronous at the public lifecycle boundary.
            RequestPhysicalLifecycleShutdown(
                waitForCompletion: !ReferenceEquals(Thread.CurrentThread,
                    ds4Input));
        }

        private void FinalizePhysicalOutput()
        {
            Action testHook = PhysicalOutputFinalizeTestHook;
            if (testHook != null)
            {
                testHook();
                return;
            }

            // The ordinary output owner has exited before this method begins.
            // This lifecycle owner is now the only path allowed to touch the
            // physical HID transport.
            lock (bluetoothSpeakerClockClaimLock)
            {
                bluetoothSpeakerClockActiveClaim = 0;
                bluetoothSpeakerClockLeaseExpiryTimestamp = 0;
            }

            if (conType == ConnectionType.BT)
            {
                bool helperRunning;
                lock (bluetoothAudioPacerLock)
                {
                    helperRunning = bluetoothAudioPacer?.IsRunning == true;
                }

                if (!helperRunning)
                {
                    RecoverBluetoothOutputTransportForShutdown();
                }
            }

            if (conType == ConnectionType.BT &&
                (Volatile.Read(ref bluetoothMicrophoneStreamingRequested) != 0 ||
                    Volatile.Read(ref bluetoothMicrophoneControlUpdatePending) != 0))
            {
                DisableBluetoothMicrophoneStreamingForShutdown();
            }

            if (conType == ConnectionType.BT)
            {
                QueueUnifiedBluetoothShutdownState();
            }
            else
            {
                SendEmptyOutputReport();
            }

            StopBluetoothAudioPacerLocked();
            Volatile.Write(ref bluetoothAudioPacerRetryAfterTimestamp, 0);
        }

        private bool QueueUnifiedBluetoothShutdownState()
        {
            if (!EnsureBluetoothCombinedOutputTransport())
            {
                return false;
            }

            lock (bluetoothCombinedSpeakerReportLock)
            {
                // Preserve the visible lightbar but neutralize every actuator
                // and media lane before the sole helper owner is retired.
                int stateOffset = BluetoothCombinedStateOffset;
                latestBluetoothCombinedSpeakerReport[stateOffset + 2] = 0;
                latestBluetoothCombinedSpeakerReport[stateOffset + 3] = 0;
                Array.Clear(latestBluetoothCombinedSpeakerReport,
                    stateOffset + 10, 27);
                Array.Clear(latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedHapticsDataOffset,
                    BluetoothCombinedHapticsDataLength);
                latestBluetoothCombinedSpeakerReportTimestamp = 0;
                ApplyBluetoothMicrophoneStreamingRequest(
                    latestBluetoothCombinedSpeakerReport, enabled: false);
            }

            return TryWriteCachedBluetoothCombinedControlReport(
                includeNativeHaptics: false,
                reportDescription: "final neutral controller state",
                waitForCompletion: true,
                allowDuringStopping: true);
        }

        private bool DisableBluetoothMicrophoneStreamingForShutdown()
        {
            // A normal microphone toggle may defer its control bit to the next
            // speaker-clocked report. Shutdown has no next frame, so compose an
            // explicit FE control report and wait for this exact OVERLAPPED
            // write before the realtime writer is disposed.
            Interlocked.Increment(ref bluetoothMicrophoneRequestGeneration);
            Volatile.Write(ref bluetoothMicrophoneStreamingRequested, 0);
            Interlocked.Exchange(ref bluetoothMicrophoneControlUpdatePending, 1);
            Interlocked.Exchange(ref bluetoothMicrophoneLastFrameTimestamp, 0);

            if (!EnsureBluetoothCombinedOutputTransport())
            {
                LastBluetoothMicrophoneWriteStatus =
                    LastBluetoothHapticsWriteStatus;
                return false;
            }

            lock (bluetoothCombinedSpeakerReportLock)
            {
                ApplyBluetoothMicrophoneStreamingRequest(
                    latestBluetoothCombinedSpeakerReport, enabled: false);
            }

            bool written = TryWriteCachedBluetoothCombinedControlReport(
                includeNativeHaptics: false,
                reportDescription: "final microphone disable",
                waitForCompletion: true,
                allowDuringStopping: true);
            LastBluetoothMicrophoneWriteStatus = LastBluetoothHapticsWriteStatus;
            return written;
        }

        private void SendEmptyOutputReport()
        {
            if (conType != ConnectionType.USB)
            {
                return;
            }

            const int reportOffset = 0;
            bool useRumble = physicalOutputStateMailbox.ReadLatest().UseRumble;
            Array.Clear(outputReport, 0, outputReport.Length);

            outputReport[0] = OUTPUT_REPORT_ID_USB;

            // Disable haptics and trigger motors
            outputReport[1 + reportOffset] = useRumble ? (byte)0x0F : (byte)0x0C;
            outputReport[2 + reportOffset] = 0x15; // Toggle all LED lights. 0x01 | 0x04 | 0x10

            // Set Lightbar to white
            outputReport[45 + reportOffset] = 0xFF;
            outputReport[46 + reportOffset] = 0xFF;
            outputReport[47 + reportOffset] = 0xFF;

            WriteReport();
            //hDevice.fileStream.Flush();
        }

        private unsafe void PrepareOutReport()
        {
            ClaimPhysicalOutputState();
            DualSensePhysicalOutputSnapshot outputState =
                activePhysicalOutputState;

            bool change = false;
            bool rumbleSet = currentHap.IsRumbleSet();

            if (conType == ConnectionType.USB)
            {
                outputReport[0] = OUTPUT_REPORT_ID_USB; // Report ID
                // 0x01 Set the main motors (also requires flag 0x02)
                // 0x02 Set the main motors (also requires flag 0x01)
                // 0x04 Set the right trigger motor
                // 0x08 Set the left trigger motor
                // 0x10 Enable modification of audio volume
                // 0x20 Enable internal speaker (even while headset is connected)
                // 0x40 Enable modification of microphone volume
                // 0x80 Enable internal mic (even while headset is connected)
                outputReport[1] = (byte)((outputState.UseRumble ? 0x0F : 0x0C) | 0x10 | 0x40 |
                    (outputState.EnableSpeakerOutput ? DualSenseOutputFlag0AudioControlEnable |
                        (outputState.HeadsetOnlyAudio ?
                            DualSenseOutputFlag0HeadphoneVolumeEnable :
                            DualSenseOutputFlag0SpeakerVolumeEnable) : 0x00));

                // 0x01 Toggling microphone LED, 0x02 Toggling Audio/Mic Mute
                // 0x04 Toggling LED strips on the sides of the Touchpad, 0x08 Turn off all LED lights
                // 0x10 Toggle player LED lights below Touchpad, 0x20 ???
                // 0x40 Adjust overall motor/effect power, 0x80 ???
                outputReport[2] = (byte)(0x55 |
                    (outputState.EnableSpeakerOutput && !outputState.HeadsetOnlyAudio ?
                        DualSenseOutputFlag1AudioControl2Enable : 0x00) |
                    (outputState.MuteLedOverride || outputState.MicrophoneMuteOverride ? 0x01 : 0x00) |
                    (outputState.MicrophoneMuteOverride ? 0x02 : 0x00));

                if (outputState.UseRumble || outputState.UseAccurateRumble)
                {
                    // Right? High Freq Motor
                    outputReport[3] = currentHap.rumbleState.RumbleMotorStrengthRightLightFast;
                    // Left? Low Freq Motor
                    outputReport[4] = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow;
                }

                // Headphone volume
                // RC3's internal-speaker state carried the profile byte here.
                // Only the isolated AUX route requires Sony's 0x00-0x7F map.
                outputReport[5] = outputState.HeadsetOnlyAudio ?
                    MapDualSenseHeadphoneVolume(outputState.HeadphoneVolume) :
                    (byte)0; // Left and Right; speaker mode keeps AUX muted.
                // Internal speaker volume
                outputReport[6] = outputState.HeadsetOnlyAudio ? (byte)0 :
                    MapDualSenseSpeakerVolume(outputState.SpeakerVolume);
                // Internal microphone volume
                outputReport[7] = MapDualSenseMicrophoneVolume(
                    outputState.MicrophoneVolume);
                // Route the Opus stream to either the controller speaker or
                // the 3.5 mm headset DAC. This byte is an output-path field,
                // not merely an internal-speaker enable bit.
                outputReport[8] = outputState.EnableSpeakerOutput ?
                    (outputState.HeadsetOnlyAudio ? DualSenseAudioControlOutputHeadphones :
                        DualSenseAudioControlOutputSpeaker) : (byte)0x00;

                // Mute button LED. 0x01 = Solid. 0x02 = Pulsating
                outputReport[9] = outputState.MuteLedOverride ? (outputState.MuteLedOn ? (byte)0x01 : (byte)0x00) :
                    outputState.MicrophoneMuteOverride ? (outputState.MicrophoneMuted ? (byte)0x01 : (byte)0x00) : outputState.MuteLedByte;

                // audio settings requiring mute toggling flags
                outputReport[10] = outputState.MicrophoneMuteOverride && outputState.MicrophoneMuted ? (byte)0x10 : (byte)0x00; // 0x10 microphone mute, 0x40 audio mute

                /* TRIGGER MOTORS  */
                // R2 Effects
                outputReport[11] = outputState.RightTrigger.triggerMotorMode; // right trigger motor mode (0 = no resistance, 1 = continuous resistance, 2 = section resistance, 0x20 and 0x04 enable additional effects together with 1 and 2 (configuration yet unknown), 252 = likely a calibration program* / PS Remote Play defaults this to 5; bit 4 only disables the motor?)
                outputReport[12] = outputState.RightTrigger.triggerStartResistance; // right trigger start of resistance section 0-255 (0 = released state; 0xb0 roughly matches trigger value 0xff); in mode 26 this field has something to do with motor re-extension after a press-release-cycle (0 = no re-extension)
                outputReport[13] = outputState.RightTrigger.triggerEffectForce; // right trigger
                                         // (mode1) amount of force exerted; 0-255
                                         // (mode2) end of resistance section (>= begin of resistance section is enforced); 0xff makes it behave like mode1
                                         // (supplemental mode 4+20) flag(s?) 0x02 = do not pause effect when fully pressed
                outputReport[14] = outputState.RightTrigger.triggerRangeForce; // right trigger force exerted in range (mode2), 0-255
                outputReport[15] = outputState.RightTrigger.triggerNearReleaseStrength; // strength of effect near release state (requires supplement modes 4 and 20)
                outputReport[16] = outputState.RightTrigger.triggerNearMiddleStrength; // strength of effect near middle (requires supplement modes 4 and 20)
                outputReport[17] = outputState.RightTrigger.triggerPressedStrength; // strength of effect at pressed state (requires supplement modes 4 and 20)
                outputReport[20] = outputState.RightTrigger.triggerActuationFrequency; // effect actuation frequency in Hz (requires supplement modes 4 and 20)


                // L2 Effects
                outputReport[22] = outputState.LeftTrigger.triggerMotorMode; // left trigger motor mode (0 = no resistance, 1 = continuous resistance, 2 = section resistance, 0x20 and 0x04 enable additional effects together with 1 and 2 (configuration yet unknown), 252 = likely a calibration program* / PS Remote Play defaults this to 5; bit 4 only disables the motor?)
                outputReport[23] = outputState.LeftTrigger.triggerStartResistance; // left trigger start of resistance section 0-255 (0 = released state; 0xb0 roughly matches trigger value 0xff); in mode 26 this field has something to do with motor re-extension after a press-release-cycle (0 = no re-extension)
                outputReport[24] = outputState.LeftTrigger.triggerEffectForce; // left trigger
                                         // (mode1) amount of force exerted; 0-255
                                         // (mode2) end of resistance section (>= begin of resistance section is enforced); 0xff makes it behave like mode1
                                         // (supplemental mode 4+20) flag(s?) 0x02 = do not pause effect when fully pressed
                outputReport[25] = outputState.LeftTrigger.triggerRangeForce; // left trigger: (mode2) amount of force exerted within range; 0-255
                outputReport[26] = outputState.LeftTrigger.triggerNearReleaseStrength; // strength of effect near release state (requires supplement modes 4 and 20)
                outputReport[27] = outputState.LeftTrigger.triggerNearMiddleStrength; // strength of effect near middle (requires supplement modes 4 and 20)
                outputReport[28] = outputState.LeftTrigger.triggerPressedStrength; // strength of effect at pressed state (requires supplement modes 4 and 20)
                outputReport[31] = outputState.LeftTrigger.triggerActuationFrequency; // effect actuation frequency in Hz (requires supplement modes 4 and 20)

                // (lower nibble: main motor; upper nibble trigger effects) 0x00 to 0x07 - reduce overall power of the respective motors/effects by 12.5% per increment (this does not affect the regular trigger motor settings, just the automatically repeating trigger effects)
                outputReport[37] = outputState.HapticPowerLevel;
                // Volume of internal speaker (0-7; ties in with index 6. The PS5 default appears to be set a 4)
                outputReport[38] = outputState.EnableSpeakerOutput && !outputState.HeadsetOnlyAudio ?
                    DualSenseSpeakerPreGain : (byte)0x00;

                /* Player LED section (and improved rumble flag) */
                // 0x01 Enabled LED brightness (value in index 43)
                // 0x02 Uninterruptable blue LED pulse (action in index 42)
                // 0x04 Enable improved rumble emulation (Requires 2.24 firmware or newer)
                outputReport[39] = outputState.UseAccurateRumble ? (byte)0x06 : (byte)0x02;

                // 0x01 Slowly (2s?) fade to blue (scheduled to when the regular LED settings are active)
                // 0x02 Slowly (2s?) fade out (scheduled after fade-in completion) with eventual switch back to configured LED color; only a fade-out can cancel the pulse (neither index 2, 0x08, nor turning this off will cancel it!)
                outputReport[42] = 0x02;
                // 0x00 High Brightness, 0x01 Medium Brightness, 0x02 Low Brightness
                outputReport[43] = 0x02;
                // 5 player LED lights below Touchpad.
                // Bitmask 0x00-0x1F from left to right with 0x04 being the center LED. Bit 0x20 sets the brightness immediately with no fade in
                outputReport[44] = outputState.ActivePlayerLedMask;

                /* Lightbar colors */
                outputReport[45] = currentHap.lightbarState.LightBarColor.red;
                outputReport[46] = currentHap.lightbarState.LightBarColor.green;
                outputReport[47] = currentHap.lightbarState.LightBarColor.blue;

                if (currentHap.dirty || !previousHapticState.Equals(currentHap))
                {
                    change = true;
                }
                /*fixed (byte* bytePrevBuff = outputReport, byteTmpBuff = outReportBuffer)
                {
                    for (int i = 0, arlen = USB_OUTPUT_CHANGE_LENGTH; !change && i < arlen; i++)
                        change = bytePrevBuff[i] != byteTmpBuff[i];
                }
                */

                if (change)
                {
                    //Console.WriteLine("DIRTY");
                    outputDirty = true;
                    if (rumbleSet)
                    {
                        standbySw.Restart();
                    }
                    else
                    {
                        standbySw.Reset();
                    }

                    //outReportBuffer.CopyTo(outputReport, 0);
                }
                else if (rumbleSet && standbySw.ElapsedMilliseconds >=
                    PhysicalRumbleKeepaliveMilliseconds)
                {
                    outputDirty = true;
                    standbySw.Restart();
                }
                //bool res = hDevice.WriteOutputReportViaInterrupt(outputReport, READ_STREAM_TIMEOUT);
                //Console.WriteLine("STAUTS: {0}", res);
            }
            else
            {
                //outReportBuffer[0] = OUTPUT_REPORT_ID_BT; // Report ID
                outputReport[0] = OUTPUT_REPORT_ID_BT; // Report ID
                outputReport[1] = OUTPUT_REPORT_ID_DATA;

                // 0x01 Set the main motors (also requires flag 0x02)
                // 0x02 Set the main motors (also requires flag 0x01)
                // 0x04 Set the right trigger motor
                // 0x08 Set the left trigger motor
                // 0x10 Enable modification of audio volume
                // 0x20 Enable internal speaker (even while headset is connected)
                // 0x40 Enable modification of microphone volume
                // 0x80 Enable internal mic (even while headset is connected)
                outputReport[2] = (byte)((outputState.UseRumble ? 0x0F : 0x0C) | 0x10 | 0x40 |
                    (outputState.EnableSpeakerOutput ? DualSenseOutputFlag0AudioControlEnable |
                        (outputState.HeadsetOnlyAudio ?
                            DualSenseOutputFlag0HeadphoneVolumeEnable :
                            DualSenseOutputFlag0SpeakerVolumeEnable) : 0x00));

                // 0x01 Toggling microphone LED, 0x02 Toggling Audio/Mic Mute
                // 0x04 Toggling LED strips on the sides of the Touchpad, 0x08 Turn off all LED lights
                // 0x10 Toggle player LED lights below Touchpad, 0x20 ???
                // 0x40 Adjust overall motor/effect power, 0x80 ???
                outputReport[3] = (byte)(0x55 |
                    (outputState.EnableSpeakerOutput && !outputState.HeadsetOnlyAudio ?
                        DualSenseOutputFlag1AudioControl2Enable : 0x00) |
                    (outputState.MuteLedOverride || outputState.MicrophoneMuteOverride ? 0x01 : 0x00) |
                    (outputState.MicrophoneMuteOverride ? 0x02 : 0x00));

                if (outputState.UseRumble || outputState.UseAccurateRumble)
                {
                    // Right? High Freq Motor
                    outputReport[4] = currentHap.rumbleState.RumbleMotorStrengthRightLightFast;
                    // Left? Low Freq Motor
                    outputReport[5] = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow;
                }

                // Headphone volume
                // Keep the proven RC3 speaker report unchanged. The 0x96 AUX
                // path alone uses the controller's 0x00-0x7F gain range.
                outputReport[6] = outputState.HeadsetOnlyAudio ?
                    MapDualSenseHeadphoneVolume(outputState.HeadphoneVolume) :
                    (byte)0; // Left and Right; speaker mode keeps AUX muted.
                // Internal speaker volume
                outputReport[7] = outputState.HeadsetOnlyAudio ? (byte)0 :
                    MapDualSenseSpeakerVolume(outputState.SpeakerVolume);
                // Internal microphone volume
                outputReport[8] = MapDualSenseMicrophoneVolume(
                    outputState.MicrophoneVolume);
                // Select the physical speaker or AUX/headset DAC.
                outputReport[9] = outputState.EnableSpeakerOutput ?
                    (outputState.HeadsetOnlyAudio ? DualSenseAudioControlOutputHeadphones :
                        DualSenseAudioControlOutputSpeaker) : (byte)0x00;

                // Mute button LED. 0x01 = Solid. 0x02 = Pulsating
                outputReport[10] = outputState.MuteLedOverride ? (outputState.MuteLedOn ? (byte)0x01 : (byte)0x00) :
                    outputState.MicrophoneMuteOverride ? (outputState.MicrophoneMuted ? (byte)0x01 : (byte)0x00) : outputState.MuteLedByte;

                // audio settings requiring mute toggling flags
                outputReport[11] = outputState.MicrophoneMuteOverride && outputState.MicrophoneMuted ? (byte)0x10 : (byte)0x00; // 0x10 microphone mute, 0x40 audio mute

                /* TRIGGER MOTORS  */
                // R2 Effects
                outputReport[12] = outputState.RightTrigger.triggerMotorMode; // right trigger motor mode (0 = no resistance, 1 = continuous resistance, 2 = section resistance, 0x20 and 0x04 enable additional effects together with 1 and 2 (configuration yet unknown), 252 = likely a calibration program* / PS Remote Play defaults this to 5; bit 4 only disables the motor?)
                outputReport[13] = outputState.RightTrigger.triggerStartResistance; // right trigger start of resistance section 0-255 (0 = released state; 0xb0 roughly matches trigger value 0xff); in mode 26 this field has something to do with motor re-extension after a press-release-cycle (0 = no re-extension)
                outputReport[14] = outputState.RightTrigger.triggerEffectForce; // right trigger
                                                                    // (mode1) amount of force exerted; 0-255
                                                                    // (mode2) end of resistance section (>= begin of resistance section is enforced); 0xff makes it behave like mode1
                                                                    // (supplemental mode 4+20) flag(s?) 0x02 = do not pause effect when fully pressed
                outputReport[15] = outputState.RightTrigger.triggerRangeForce; // right trigger force exerted in range (mode2), 0-255
                outputReport[16] = outputState.RightTrigger.triggerNearReleaseStrength; // strength of effect near release state (requires supplement modes 4 and 20)
                outputReport[17] = outputState.RightTrigger.triggerNearMiddleStrength; // strength of effect near middle (requires supplement modes 4 and 20)
                outputReport[18] = outputState.RightTrigger.triggerPressedStrength; // strength of effect at pressed state (requires supplement modes 4 and 20)
                outputReport[21] = outputState.RightTrigger.triggerActuationFrequency; // effect actuation frequency in Hz (requires supplement modes 4 and 20)


                // L2 Effects
                outputReport[23] = outputState.LeftTrigger.triggerMotorMode; // left trigger motor mode (0 = no resistance, 1 = continuous resistance, 2 = section resistance, 0x20 and 0x04 enable additional effects together with 1 and 2 (configuration yet unknown), 252 = likely a calibration program* / PS Remote Play defaults this to 5; bit 4 only disables the motor?)
                outputReport[24] = outputState.LeftTrigger.triggerStartResistance; // left trigger start of resistance section 0-255 (0 = released state; 0xb0 roughly matches trigger value 0xff); in mode 26 this field has something to do with motor re-extension after a press-release-cycle (0 = no re-extension)
                outputReport[25] = outputState.LeftTrigger.triggerEffectForce; // left trigger
                                                                    // (mode1) amount of force exerted; 0-255
                                                                    // (mode2) end of resistance section (>= begin of resistance section is enforced); 0xff makes it behave like mode1
                                                                    // (supplemental mode 4+20) flag(s?) 0x02 = do not pause effect when fully pressed
                outputReport[26] = outputState.LeftTrigger.triggerRangeForce; // left trigger: (mode2) amount of force exerted within range; 0-255
                outputReport[27] = outputState.LeftTrigger.triggerNearReleaseStrength; // strength of effect near release state (requires supplement modes 4 and 20)
                outputReport[28] = outputState.LeftTrigger.triggerNearMiddleStrength; // strength of effect near middle (requires supplement modes 4 and 20)
                outputReport[29] = outputState.LeftTrigger.triggerPressedStrength; // strength of effect at pressed state (requires supplement modes 4 and 20)
                outputReport[32] = outputState.LeftTrigger.triggerActuationFrequency; // effect actuation frequency in Hz (requires supplement modes 4 and 20)

                // (lower nibble: main motor; upper nibble trigger effects) 0x00 to 0x07 - reduce overall power of the respective motors/effects by 12.5% per increment (this does not affect the regular trigger motor settings, just the automatically repeating trigger effects)
                outputReport[38] = outputState.HapticPowerLevel;
                // Volume of internal speaker (0-7; ties in with index 6. The PS5 default appears to be set a 4)
                outputReport[39] = outputState.EnableSpeakerOutput && !outputState.HeadsetOnlyAudio ?
                    DualSenseSpeakerPreGain : (byte)0x00;

                /* Player LED section (and improved rumble  flag) */
                // 0x01 Enabled LED brightness (value in index 43)
                // 0x02 Uninterruptable blue LED pulse (action in index 42)
                // 0x04 Enable improved rumble emulation (Requires 2.24 firmware or newer)
                outputReport[40] = outputState.UseAccurateRumble ? (byte)0x06 : (byte)0x02;

                // 0x01 Slowly (2s?) fade to blue (scheduled to when the regular LED settings are active)
                // 0x02 Slowly (2s?) fade out (scheduled after fade-in completion) with eventual switch back to configured LED color; only a fade-out can cancel the pulse (neither index 2, 0x08, nor turning this off will cancel it!)
                outputReport[43] = 0x02;
                // 0x00 High Brightness, 0x01 Medium Brightness, 0x02 Low Brightness
                outputReport[44] = 0x02;
                // 5 player LED lights below Touchpad.
                // Bitmask 0x00-0x1F from left to right with 0x04 being the center LED. Bit 0x20 sets the brightness immediately with no fade in
                outputReport[45] = outputState.ActivePlayerLedMask;

                /* Lightbar colors */
                outputReport[46] = currentHap.lightbarState.LightBarColor.red;
                outputReport[47] = currentHap.lightbarState.LightBarColor.green;
                outputReport[48] = currentHap.lightbarState.LightBarColor.blue;

                change = currentHap.dirty || !previousHapticState.Equals(currentHap);

                // Need to calculate and populate CRC32 data so controller will accept the report
                uint calcCrc32 = 0;
                if (change)
                //if (outputPendCount >= 1 || change)
                //if (!previousHapticState.Equals(currentHap))
                {
                    //change = true;
                    outputDirty = true;

                    if (rumbleSet)
                    {
                        standbySw.Restart();
                    }
                    else
                    {
                        standbySw.Reset();
                    }
                }
                else if (rumbleSet && standbySw.ElapsedMilliseconds >= 4000L)
                {
                    outputDirty = true;
                    standbySw.Restart();
                }

                if (outputDirty)
                {
                    int crcOffset = 0;
                    int crcpos = BT_OUTPUT_REPORT_LENGTH - 4;
                    calcCrc32 = ~Crc32Algorithm.Compute(outputBTCrc32Head);
                    //calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref outputReport, 0, BT_OUTPUT_REPORT_LENGTH-4);
                    calcCrc32 = ~Crc32Algorithm.CalculateFasterBT78Hash(ref calcCrc32, ref outputReport, ref crcOffset, ref crcpos);
                }

                outputReport[74] = (byte)calcCrc32;
                outputReport[75] = (byte)(calcCrc32 >> 8);
                outputReport[76] = (byte)(calcCrc32 >> 16);
                outputReport[77] = (byte)(calcCrc32 >> 24);

                /*fixed (byte* bytePrevBuff = outputReport, byteTmpBuff = outReportBuffer)
                {
                    for (int i = 0, arlen = BT_OUTPUT_CHANGE_LENGTH; !change && i < arlen; i++)
                        change = bytePrevBuff[i] != byteTmpBuff[i];
                }
                */

                /*if (change)
                {
                    outputPendCount = OUTPUT_MIN_COUNT_BT;
                    //Console.WriteLine("DIRTY");
                    outputDirty = true;
                    
                    //outReportBuffer.CopyTo(outputReport, 0);
                }
                else if (outputPendCount >= 1)
                {
                    Console.WriteLine("CURRENT: {0}", outputPendCount);
                    outputPendCount--;
                    outputDirty = outputPendCount >= 1;
                }
                */

                //outputDirty = true;

                //bool res = hDevice.WriteOutputReportViaControl(outputReport);
                //Console.WriteLine("STAUTS: {0}", res);
            }
        }

        private bool WriteReport()
        {
            if (conType == ConnectionType.BT)
            {
                bool published = EnsureBluetoothCombinedOutputTransport() &&
                    UpdateCachedBluetoothCombinedStateFromBluetoothOutput(
                        outputReport) &&
                    TryPublishCachedBluetoothCombinedState(
                        includeNativeHaptics: true,
                        activeStatus:
                            "Queued controller state on the unified Bluetooth transport.",
                        idleReportDescription: "controller state",
                        out _);
                if (!published)
                {
                    RequestUnifiedBluetoothOutputTransportRecovery();
                }

                return published;
            }

            return hDevice.WriteOutputReportViaInterrupt(outputReport,
                READ_STREAM_TIMEOUT);
        }

        public bool WriteRawOutputReportFromGame(byte[] report, int offset, int length)
        {
            if (report == null ||
                length < USB_OUTPUT_CHANGE_LENGTH ||
                offset < 0 ||
                offset + USB_OUTPUT_CHANGE_LENGTH > report.Length ||
                report[offset] != OUTPUT_REPORT_ID_USB)
            {
                return false;
            }

            return TryQueuePhysicalOutputCommand(report, offset);
        }

        internal void ReleaseNativeGameOutputOwnership()
        {
            physicalOutputStateMailbox.
                SetNativeGameLightbarOwnershipReleased(true);
            Interlocked.Exchange(ref nativeSessionReleasePending, 1);
            // Clearing the native transport state, resetting the audio pacer,
            // and dirtying the compositor copy now occur on the physical
            // owner. The caller only publishes this ordered lifecycle event.
            QueuePhysicalOutputUpdate();
        }

        /// <summary>
        /// Publishes one native 3 kHz stereo haptics packet through the same
        /// combined Bluetooth transport used by controller speaker audio,
        /// microphone control, and game feedback. This keeps a single owner of
        /// the physical HID handle and avoids competing report streams.
        /// </summary>
        public bool WriteBluetoothHapticsSamples(byte[] samples, int offset,
            int length, bool waitForWrite = false)
        {
            if (samples == null || offset < 0 ||
                length != BluetoothCombinedHapticsDataLength ||
                offset + length > samples.Length)
            {
                LastBluetoothHapticsWriteStatus =
                    "Rejected: invalid Bluetooth haptics sample block.";
                return false;
            }

            if (!EnsureBluetoothCombinedOutputTransport())
            {
                return false;
            }

            long hapticsGeneration;
            lock (bluetoothCombinedSpeakerReportLock)
            {
                latestBluetoothCombinedSpeakerReport[BluetoothCombinedHapticsOffset] =
                    0x92;
                latestBluetoothCombinedSpeakerReport[
                    BluetoothCombinedHapticsOffset + 1] =
                    BluetoothCombinedHapticsDataLength;
                Array.Copy(samples, offset,
                    latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedHapticsDataOffset,
                    BluetoothCombinedHapticsDataLength);
                latestBluetoothCombinedSpeakerReportTimestamp =
                    Stopwatch.GetTimestamp();
                bluetoothCombinedHapticsGeneration++;
                hapticsGeneration = bluetoothCombinedHapticsGeneration;
            }

            bool written = TryPublishCachedBluetoothCombinedState(
                includeNativeHaptics: true,
                activeStatus:
                    "Converted Bluetooth haptics to the next combined speaker-clocked report.",
                idleReportDescription: "converted haptics",
                out bool deferredToSpeakerClock,
                realtimeHaptics: true);
            if (written && !deferredToSpeakerClock)
            {
                MarkBluetoothCombinedHapticsSubmitted(hapticsGeneration);
            }

            return written;
        }

        /// <summary>
        /// Receives VIIPER's vDS-compatible Bluetooth report 0x36. While
        /// speaker audio is active, this refreshes the newest native state and
        /// haptics block; the fixed-cadence speaker clock owns physical writes.
        /// </summary>
        public bool WriteBluetoothCombinedHapticsAudioOutputReport(byte[] report,
            int offset, int length, bool hasNativeGameState = true)
        {
            if (report == null || offset < 0 || length != BluetoothCombinedOutputReportLength ||
                offset + length > report.Length || report[offset] != 0x36 ||
                report[offset + 11] != 0x90 ||
                report[offset + 12] != BluetoothCombinedStateLength ||
                report[offset + BluetoothCombinedHapticsOffset] != 0x92 ||
                report[offset + BluetoothCombinedHapticsOffset + 1] !=
                    BluetoothCombinedHapticsDataLength)
            {
                LastBluetoothHapticsWriteStatus =
                    "Rejected: invalid combined Bluetooth haptics/audio report.";
                return false;
            }

            if (!EnsureBluetoothCombinedOutputTransport())
            {
                return false;
            }

            lock (bluetoothCombinedTransportWriteLock)
            {
                long hapticsGeneration =
                    CacheBluetoothCombinedSpeakerReport(report, offset,
                        hasNativeGameState);

                if (hasNativeGameState)
                {
                    bool published =
                        TryPublishAtomicNativeGameStateTransition();
                    if (published)
                    {
                        MarkBluetoothCombinedHapticsSubmitted(
                            hapticsGeneration);
                    }
                    return published;
                }

                bool written = TryPublishCachedBluetoothCombinedState(
                    includeNativeHaptics: true,
                    activeStatus:
                        "Cached native Bluetooth haptics for the next speaker-clocked frame.",
                    idleReportDescription: "combined haptics/audio",
                    out bool deferredToSpeakerClock,
                    realtimeHaptics: true);
                if (written && !deferredToSpeakerClock)
                {
                    MarkBluetoothCombinedHapticsSubmitted(hapticsGeneration);
                }

                return written;
            }
        }

        private bool TryPublishAtomicNativeGameStateTransition()
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            byte[] exactState = bluetoothCombinedGameStateWorkingReport;
            byte[] quiescentTemplate =
                bluetoothCombinedSpeakerWorkingReport;
            byte originalFlag0;
            byte originalFlag1;
            byte originalFlag2;
            lock (bluetoothCombinedSpeakerReportLock)
            {
                if (!bluetoothCombinedSpeakerReportAvailable)
                {
                    return false;
                }

                Array.Copy(latestBluetoothCombinedSpeakerReport, exactState,
                    exactState.Length);
                originalFlag0 = latestBluetoothCombinedSpeakerReport[
                    BluetoothCombinedStateOffset];
                originalFlag1 = latestBluetoothCombinedSpeakerReport[
                    BluetoothCombinedStateOffset + 1];
                originalFlag2 = latestBluetoothCombinedSpeakerReport[
                    BluetoothCombinedStateOffset + 38];
                ConsumeNativeGameStateValidity(
                    latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedStateOffset);
                Array.Copy(latestBluetoothCombinedSpeakerReport,
                    quiescentTemplate, quiescentTemplate.Length);
            }

            ApplyBluetoothSpeakerVolumeAndRoutingCore(exactState,
                outputState.SpeakerVolume, outputState.HeadsetOnlyAudio,
                outputState.HeadphoneVolume);
            ApplyBluetoothMicrophoneStreamingRequest(exactState,
                outputState);
            ApplyBluetoothSpeakerVolumeAndRoutingCore(quiescentTemplate,
                outputState.SpeakerVolume, outputState.HeadsetOnlyAudio,
                outputState.HeadphoneVolume);
            ApplyBluetoothMicrophoneStreamingRequest(quiescentTemplate,
                outputState);

            bool published = false;
            if (TryClaimBluetoothAudioPacer(out DualSenseBluetoothAudioPacer
                    pacer, out _))
            {
                try
                {
                    published = pacer.UpdateGameStateAndTemplate(
                        exactState, quiescentTemplate,
                        PersistentBluetoothHapticsExpiryQpc);
                }
                finally
                {
                    ReleaseBluetoothAudioPacerClaim();
                }
            }

            if (!published)
            {
                lock (bluetoothCombinedSpeakerReportLock)
                {
                    latestBluetoothCombinedSpeakerReport[
                        BluetoothCombinedStateOffset] = originalFlag0;
                    latestBluetoothCombinedSpeakerReport[
                        BluetoothCombinedStateOffset + 1] = originalFlag1;
                    latestBluetoothCombinedSpeakerReport[
                        BluetoothCombinedStateOffset + 38] = originalFlag2;
                }
                LastBluetoothHapticsWriteStatus =
                    "Could not atomically publish native game state to the unified Bluetooth compositor.";
                RequestUnifiedBluetoothOutputTransportRecovery();
                return false;
            }

            LastBluetoothHapticsWriteStatus =
                "Merged exact native game state into the next unified Bluetooth frame.";
            return true;
        }

        internal static void ConsumeNativeGameStateValidity(byte[] report,
            int stateOffset)
        {
            if (report == null || stateOffset < 0 ||
                stateOffset + BluetoothCombinedNativeStateLength >
                    report.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(stateOffset));
            }

            // Bits outside these masks are game-authored update strobes. The
            // exact transition is already queued in the compositor; retaining
            // them in its steady media template would retrigger adaptive
            // effects, rumble, or LED release on every 10.667 ms carrier.
            report[stateOffset] &= 0xF0;
            report[stateOffset + 1] &= 0x83;
            report[stateOffset + 38] = 0;
        }

        private static byte[] BuildBluetoothCombinedControlReport(byte sequence,
            byte packetSequence, bool microphoneEnabled)
        {
            byte[] report = new byte[BluetoothCombinedOutputReportLength];
            report[0] = 0x36;
            report[1] = (byte)((sequence & 0x0F) << 4);
            report[2] = 0x91;
            report[3] = 0x07;
            report[4] = (byte)(0xFE |
                (microphoneEnabled ? BluetoothMicrophoneControlEnable : 0));
            for (int index = 5; index <= 9; index++)
            {
                report[index] = BluetoothCombinedLowLatencyBufferLength;
            }

            report[10] = packetSequence;
            report[11] = 0x90;
            report[12] = BluetoothCombinedStateLength;
            Array.Copy(DefaultBluetoothCombinedState, 0, report,
                BluetoothCombinedStateOffset, DefaultBluetoothCombinedState.Length);
            report[BluetoothCombinedHapticsOffset] = 0x92;
            report[BluetoothCombinedHapticsOffset + 1] =
                BluetoothCombinedHapticsDataLength;
            // A control-only report omits packet 0x13. Some controller firmware
            // turns an empty 0x93 TLV into an audible notification chirp.

            uint crc = DualSenseBluetoothCrc32(report, report.Length - 4);
            report[report.Length - 4] = (byte)crc;
            report[report.Length - 3] = (byte)(crc >> 8);
            report[report.Length - 2] = (byte)(crc >> 16);
            report[report.Length - 1] = (byte)(crc >> 24);
            return report;
        }

        private long CacheBluetoothCombinedSpeakerReport(byte[] report,
            int offset, bool hasNativeGameState)
        {
            int ledOwnershipUpdate = hasNativeGameState ?
                GetNativeGameLedOwnershipUpdate(report,
                    offset + BluetoothCombinedStateOffset) : 0;
            bool releasedOwnership = ledOwnershipUpdate < 0;
            if (ledOwnershipUpdate != 0)
            {
                PublishNativeGameLightbarOwnership(
                    released: releasedOwnership);
            }
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            long hapticsGeneration;
            lock (bluetoothCombinedSpeakerReportLock)
            {
                // VIIPER owns this state only after a game has actually sent a
                // native USB output report. Audio-only sidecars and a newly
                // enumerated virtual pad otherwise repeat vDS's green default
                // carrier on every PCM packet. Preserve DS4Windows' current
                // profile/custom lightbar and audio routing in that case.
                if (hasNativeGameState)
                {
                    MergeControllerStateDeltaIntoV5AudioSnapshot(report,
                        offset + BluetoothCombinedStateOffset,
                        latestBluetoothCombinedSpeakerReport,
                        BluetoothCombinedStateOffset,
                        bluetoothCombinedNativeStateScratch);
                    latestBluetoothCombinedNativeStateTimestamp =
                        Stopwatch.GetTimestamp();
                    if (outputState.NativeGameLightbarOwnershipReleased)
                    {
                        // Sony's release bit is an immediate ownership
                        // boundary. Publish the profile lightbar and player
                        // LEDs in this same atomic carrier instead of waiting
                        // for an unrelated later state/media callback.
                        MergeProfileLightbarIntoV5AudioSnapshot(outputState,
                            latestBluetoothCombinedSpeakerReport,
                            BluetoothCombinedStateOffset);
                    }
                }
                else
                {
                    if (latestBluetoothCombinedNativeStateTimestamp == 0)
                    {
                        MergeProfileLightbarIntoV5AudioSnapshot(outputState,
                            latestBluetoothCombinedSpeakerReport,
                            BluetoothCombinedStateOffset);
                    }
                    else if (outputState.
                        NativeGameLightbarOwnershipReleased)
                    {
                        MergeProfileLightbarIntoV5AudioSnapshot(outputState,
                            latestBluetoothCombinedSpeakerReport,
                            BluetoothCombinedStateOffset);
                    }
                }
                // A native HID SET_REPORT is state-only. VIIPER attaches the
                // current media snapshot so state and media can share one
                // carrier, but that snapshot is not a new rear-channel audio
                // interval. Recaching it here lets a high-rate game HID stream
                // repeatedly replace the independently clocked haptics lane
                // with stale (often silent) data. Only the atomic audio
                // callback is allowed to advance media ownership.
                if (!hasNativeGameState)
                {
                    latestBluetoothCombinedSpeakerReport[
                        BluetoothCombinedHapticsOffset] = 0x92;
                    latestBluetoothCombinedSpeakerReport[
                        BluetoothCombinedHapticsOffset + 1] =
                        BluetoothCombinedHapticsDataLength;
                    Array.Copy(report,
                        offset + BluetoothCombinedHapticsDataOffset,
                        latestBluetoothCombinedSpeakerReport,
                        BluetoothCombinedHapticsDataOffset,
                        BluetoothCombinedHapticsDataLength);
                    latestBluetoothCombinedSpeakerReportTimestamp =
                        Stopwatch.GetTimestamp();
                    bluetoothCombinedHapticsGeneration++;
                }

                bluetoothCombinedSpeakerReportAvailable = true;
                hapticsGeneration = bluetoothCombinedHapticsGeneration;
            }

            if (releasedOwnership)
            {
                AppLogger.LogToGui(
                    "DualSense game released lightbar/player LED ownership; restoring the active profile state atomically.",
                    false);
            }

            return hapticsGeneration;
        }

        private void ApplyNextBluetoothCombinedSequence(byte[] report,
            bool advancesMediaPacketSequence)
        {
            lock (bluetoothCombinedSpeakerReportLock)
            {
                if (!bluetoothCombinedSpeakerSequenceInitialized)
                {
                    bluetoothCombinedSpeakerReportSequence =
                        (byte)(report[1] >> 4);
                    bluetoothCombinedSpeakerPacketSequence = report[10];
                    bluetoothCombinedSpeakerSequenceInitialized = true;
                }

                report[1] =
                    (byte)((bluetoothCombinedSpeakerReportSequence & 0x0F) << 4);
                report[10] = bluetoothCombinedSpeakerPacketSequence;
                bluetoothCombinedSpeakerReportSequence =
                    (byte)((bluetoothCombinedSpeakerReportSequence + 1) & 0x0F);
                if (advancesMediaPacketSequence)
                {
                    bluetoothCombinedSpeakerPacketSequence++;
                }
            }
        }

        private void MarkBluetoothCombinedHapticsSubmitted(long hapticsGeneration)
        {
            lock (bluetoothCombinedSpeakerReportLock)
            {
                if (hapticsGeneration >
                    bluetoothCombinedSubmittedHapticsGeneration)
                {
                    bluetoothCombinedSubmittedHapticsGeneration =
                        hapticsGeneration;
                }
            }
        }

        private bool UpdateCachedBluetoothCombinedState(byte[] report, int offset)
        {
            if (report == null || offset < 0 ||
                offset + USB_OUTPUT_CHANGE_LENGTH > report.Length ||
                report[offset] != OUTPUT_REPORT_ID_USB)
            {
                return false;
            }

            int ledOwnershipUpdate =
                GetNativeGameLedOwnershipUpdate(report, offset + 1);
            bool releasedOwnership = ledOwnershipUpdate < 0;
            if (ledOwnershipUpdate != 0)
            {
                PublishNativeGameLightbarOwnership(releasedOwnership);
            }
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            bool updated;
            lock (bluetoothCombinedSpeakerReportLock)
            {
                if (!bluetoothCombinedSpeakerReportAvailable)
                {
                    return false;
                }

                MergeControllerStateDeltaIntoV5AudioSnapshot(report,
                    offset + 1, latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedStateOffset,
                    bluetoothCombinedNativeStateScratch);
                latestBluetoothCombinedNativeStateTimestamp =
                    Stopwatch.GetTimestamp();
                if (outputState.NativeGameLightbarOwnershipReleased)
                {
                    MergeProfileLightbarIntoV5AudioSnapshot(outputState,
                        latestBluetoothCombinedSpeakerReport,
                        BluetoothCombinedStateOffset);
                }
                ApplyActiveRumblePreviewToV5AudioSnapshot(
                    latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedStateOffset, outputState);
                updated = true;
            }

            if (releasedOwnership)
            {
                AppLogger.LogToGui(
                    "DualSense game released lightbar/player LED ownership; restoring the active profile state atomically.",
                    false);
            }
            return updated;
        }

        private void FlushPreparedOutputReport(
            long ownerRequestGeneration = 0)
        {
            if (outputDirty)
            {
                bool published = true;
                // Every Bluetooth state uses the long-lived V5 helper,
                // even while speaker and microphone media are idle.
                if (conType == ConnectionType.BT)
                {
                    published =
                        EnsureBluetoothCombinedOutputTransport() &&
                        UpdateCachedBluetoothCombinedStateFromBluetoothOutput(
                            outputReport) &&
                        TryPublishCachedBluetoothCombinedState(
                            includeNativeHaptics: true,
                            activeStatus:
                                "Merged controller state into the next speaker-clocked combined Bluetooth report.",
                            idleReportDescription: "controller state",
                            out _);
                }
                else
                {
                    published = WriteReport();
                }

                if (!published)
                {
                    // Keep dirty state pending so a transient helper queue/fault
                    // cannot turn the latest light/rumble state into a silent
                    // permanent loss.
                    RequestUnifiedBluetoothOutputTransportRecovery();
                    SchedulePhysicalOutputRetry();
                    return;
                }

                previousHapticState = currentHap;
                submittedLocalRumbleGeneration =
                    preparedLocalRumbleGeneration;
                SchedulePhysicalOutputKeepalive(
                    PhysicalRumbleKeepaliveMilliseconds);
            }

            if (ownerRequestGeneration == 0 ||
                ownerRequestGeneration == Volatile.Read(
                    ref physicalOutputRequestedGeneration))
            {
                outputDirty = false;
                currentHap.dirty = false;
            }
        }

        private void SchedulePhysicalOutputKeepalive(int delayMilliseconds)
        {
            if (!currentHap.IsRumbleSet())
            {
                Interlocked.Exchange(ref physicalOutputKeepaliveDueQpc, 0);
                return;
            }

            SchedulePhysicalOutputDue(ref physicalOutputKeepaliveDueQpc,
                Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                delayMilliseconds);
        }

        private void SchedulePhysicalOutputRetry()
        {
            SchedulePhysicalOutputDue(ref physicalOutputKeepaliveDueQpc,
                Stopwatch.GetTimestamp(), Stopwatch.Frequency,
                PhysicalOutputRetryMilliseconds);
        }

        internal static void SchedulePhysicalOutputDue(ref long dueAtQpc,
            long nowQpc, long frequency, int delayMilliseconds)
        {
            long delayTicks = Math.Max(1,
                frequency * delayMilliseconds / 1000);
            Interlocked.Exchange(ref dueAtQpc, nowQpc + delayTicks);
        }

        private void DrainQueuedInputEvents()
        {
            if (!Volatile.Read(ref hasInputEvts) &&
                Volatile.Read(ref deviceStatusNotificationPending) == 0)
            {
                return;
            }

            // Preserve the established report-boundary admission point while
            // keeping arbitrary device-configuration Actions off the physical
            // HID callback. The dedicated command owner claims and invokes
            // them after this signal.
            deviceCommandSignal.Set();
        }

        private void DeviceCommandLoop(long generation)
        {
            while (Volatile.Read(ref deviceCommandStopRequested) == 0 &&
                generation == Volatile.Read(ref physicalOutputGeneration))
            {
                deviceCommandSignal.WaitOne();
                if (Volatile.Read(ref deviceCommandStopRequested) != 0 ||
                    generation != Volatile.Read(ref physicalOutputGeneration))
                {
                    break;
                }

                DrainQueuedDeviceCommandsOnOwner();
            }
        }

        private void DrainQueuedDeviceCommandsOnOwner()
        {
            int notifications = Interlocked.Exchange(
                ref deviceStatusNotificationPending, 0);
            if ((notifications & DeviceStatusChargingChanged) != 0)
            {
                InvokeDeviceStatusNotification(ChargingChanged);
            }
            if ((notifications & DeviceStatusBatteryChanged) != 0)
            {
                InvokeDeviceStatusNotification(BatteryChanged);
            }

            while (true)
            {
                Action action;
                lock (eventQueueLock)
                {
                    if (eventQueue.Count == 0)
                    {
                        Volatile.Write(ref hasInputEvts, false);
                        return;
                    }

                    action = eventQueue.Dequeue();
                }

                // These existing device-configuration commands deliberately
                // observe a completed input-report boundary. Never invoke one
                // while owning the collection lock: subscribers may enqueue a
                // follow-up command or acquire unrelated subsystem locks.
                try
                {
                    action.Invoke();
                }
                catch (Exception exception)
                {
                    AppLogger.LogToGui($"{Mac} device command failed: " +
                        exception.Message, true);
                }
            }
        }

        private void InvokeDeviceStatusNotification(
            EventHandler notification)
        {
            try
            {
                notification?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                AppLogger.LogToGui($"{Mac} status subscriber failed: " +
                    exception.Message, true);
            }
        }

        private bool UpdateCachedBluetoothCombinedStateFromBluetoothOutput(
            byte[] report)
        {
            if (report == null || report.Length < 2 +
                    BluetoothCombinedNativeStateLength ||
                report[0] != OUTPUT_REPORT_ID_BT)
            {
                return false;
            }

            lock (bluetoothCombinedSpeakerReportLock)
            {
                if (!bluetoothCombinedSpeakerReportAvailable)
                {
                    return false;
                }

                if (latestBluetoothCombinedNativeStateTimestamp > 0)
                {
                    // Native DualSense output is stateful, not a pulse with a
                    // 100 ms lease. The game owns trigger, rumble, haptics, and
                    // LED state until it sends another native report or the
                    // virtual DualSense is detached. Replacing that state with
                    // PrepareOutReport after an arbitrary timeout made the two
                    // writers alternate ownership and caused triggers and
                    // vibration to oscillate in PS5 games.
                    if (activePhysicalOutputState.
                        NativeGameLightbarOwnershipReleased)
                    {
                        MergeProfileLightbarIntoV5AudioSnapshot(
                            activePhysicalOutputState,
                            latestBluetoothCombinedSpeakerReport,
                            BluetoothCombinedStateOffset);
                    }

                    // Native game ownership is state ownership, not a veto on
                    // later local feedback. XInput/compact HID rumble and the
                    // profile editor arrive through DS4Device rather than as
                    // native USB output reports. Previously this early return
                    // silently discarded those motor transitions forever
                    // after the first native game report. Overlay only a new
                    // local motor generation; unrelated profile/light/audio
                    // writes continue to leave the game's state untouched.
                    if (preparedLocalRumbleGeneration !=
                        submittedLocalRumbleGeneration)
                    {
                        MergeLocalRumbleIntoV5AudioSnapshot(report, 2,
                            latestBluetoothCombinedSpeakerReport,
                            BluetoothCombinedStateOffset);
                    }

                    ApplyActiveRumblePreviewToV5AudioSnapshot(
                        latestBluetoothCombinedSpeakerReport,
                        BluetoothCombinedStateOffset,
                        activePhysicalOutputState);
                    return true;
                }

                MergeControllerStateIntoV5AudioSnapshot(report, 2,
                    latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedStateOffset);
                ApplyActiveRumblePreviewToV5AudioSnapshot(
                    latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedStateOffset,
                    activePhysicalOutputState);
                return true;
            }
        }

        internal static void MergeLocalRumbleIntoV5AudioSnapshot(
            byte[] source, int sourceOffset, byte[] destination,
            int destinationOffset)
        {
            const byte mainMotorValidity = 0x03;
            destination[destinationOffset] = (byte)(
                destination[destinationOffset] | mainMotorValidity);
            destination[destinationOffset + 2] = source[sourceOffset + 2];
            destination[destinationOffset + 3] = source[sourceOffset + 3];

            // Improved-rumble mode is part of the motor contract. Copy only
            // that bit; the game retains ownership of the other fields in
            // this byte.
            destination[destinationOffset + 38] = (byte)(
                (destination[destinationOffset + 38] & ~0x04) |
                (source[sourceOffset + 38] & 0x04));
        }

        private void ApplyActiveRumblePreviewToV5AudioSnapshot(
            byte[] destination, int destinationOffset,
            in DualSensePhysicalOutputSnapshot outputState)
        {
            if (!outputState.PreviewLightRumbleActive &&
                !outputState.PreviewHeavyRumbleActive)
            {
                return;
            }

            destination[destinationOffset] |= 0x03;
            destination[destinationOffset + 2] =
                outputState.PreviewLightRumbleActive ?
                    outputState.PreviewLightRumbleStrength : (byte)0;
            destination[destinationOffset + 3] =
                outputState.PreviewHeavyRumbleActive ?
                    outputState.PreviewHeavyRumbleStrength : (byte)0;
        }

        private static void MergeControllerStateIntoV5AudioSnapshot(
            byte[] source, int sourceOffset, byte[] destination,
            int destinationOffset)
        {
            // Keep the complete game/controller state contract. Validity bits
            // are part of that contract: retaining the default FD/F7 flags
            // while copying changing trigger blocks makes stale fields valid
            // and causes the physical triggers to thrash. Only the seven
            // fields owned locally by the PlayStation audio route survive the
            // copy; they are refreshed again at the physical write boundary.
            byte audioFlag0 = (byte)(destination[destinationOffset] & 0xF0);
            byte audioFlag1 = (byte)(destination[destinationOffset + 1] & 0x83);
            byte headphoneVolume = destination[destinationOffset + 4];
            byte speakerVolumeSnapshot = destination[destinationOffset + 5];
            byte microphoneVolumeSnapshot = destination[destinationOffset + 6];
            byte audioControl = destination[destinationOffset + 7];
            byte muteLed = destination[destinationOffset + 8];
            byte powerSaveControl = destination[destinationOffset + 9];
            byte audioControl2 = destination[destinationOffset + 37];

            Array.Copy(source, sourceOffset, destination, destinationOffset,
                BluetoothCombinedNativeStateLength);

            destination[destinationOffset] = (byte)(
                (source[sourceOffset] & 0x0F) | audioFlag0);
            destination[destinationOffset + 1] = (byte)(
                (source[sourceOffset + 1] & 0x7C) | audioFlag1);
            destination[destinationOffset + 4] = headphoneVolume;
            destination[destinationOffset + 5] = speakerVolumeSnapshot;
            destination[destinationOffset + 6] = microphoneVolumeSnapshot;
            destination[destinationOffset + 7] = audioControl;
            destination[destinationOffset + 8] = muteLed;
            destination[destinationOffset + 9] = powerSaveControl;
            destination[destinationOffset + 37] = audioControl2;
        }

        internal static void MergeControllerStateDeltaIntoV5AudioSnapshot(
            byte[] source, int sourceOffset, byte[] destination,
            int destinationOffset, byte[] scratch)
        {
            if (source == null || sourceOffset < 0 ||
                sourceOffset + BluetoothCombinedNativeStateLength >
                    source.Length || destination == null ||
                destinationOffset < 0 || destinationOffset +
                    BluetoothCombinedNativeStateLength > destination.Length ||
                scratch == null || scratch.Length !=
                    BluetoothCombinedNativeStateLength)
            {
                throw new ArgumentOutOfRangeException();
            }

            // Native USB output reports are validity-masked deltas, but only
            // the visible LED state is persistent here. Rumble and adaptive
            // trigger validity bits are commands: retaining them across an
            // unrelated report replays an old effect and can make the
            // physical controls feel stuck. Preserve the last game-authored
            // lightbar/player LEDs while every other field remains the exact
            // current game report.
            byte previousFlag1 = scratch[1];
            byte previousPlayerLeds = scratch[43];
            byte previousRed = scratch[44];
            byte previousGreen = scratch[45];
            byte previousBlue = scratch[46];
            Buffer.BlockCopy(source, sourceOffset, scratch, 0,
                BluetoothCombinedNativeStateLength);

            bool incomingRelease = (scratch[1] & 0x08) != 0;
            bool incomingLightbar = (scratch[1] & 0x04) != 0;
            bool incomingPlayerLeds = (scratch[1] & 0x10) != 0;
            if (incomingRelease)
            {
                scratch[1] &= unchecked((byte)~0x14);
            }
            else
            {
                if (!incomingLightbar && (previousFlag1 & 0x04) != 0)
                {
                    scratch[1] |= 0x04;
                    scratch[44] = previousRed;
                    scratch[45] = previousGreen;
                    scratch[46] = previousBlue;
                }

                if (!incomingPlayerLeds && (previousFlag1 & 0x10) != 0)
                {
                    scratch[1] |= 0x10;
                    scratch[43] = previousPlayerLeds;
                }

                if (incomingLightbar || incomingPlayerLeds)
                {
                    scratch[1] &= unchecked((byte)~0x08);
                }
                else if ((previousFlag1 & 0x08) != 0)
                {
                    scratch[1] |= 0x08;
                }
            }

            // The physical PlayStation audio route remains locally owned even
            // while every game-authored controller field is accumulated.
            byte audioFlag0 = (byte)(destination[destinationOffset] & 0xF0);
            byte audioFlag1 = (byte)(destination[destinationOffset + 1] & 0x83);
            byte headphoneVolume = destination[destinationOffset + 4];
            byte speakerVolumeSnapshot = destination[destinationOffset + 5];
            byte microphoneVolumeSnapshot = destination[destinationOffset + 6];
            byte audioControl = destination[destinationOffset + 7];
            byte muteLed = destination[destinationOffset + 8];
            byte powerSaveControl = destination[destinationOffset + 9];
            byte audioControl2 = destination[destinationOffset + 37];

            Buffer.BlockCopy(scratch, 0, destination, destinationOffset,
                BluetoothCombinedNativeStateLength);
            destination[destinationOffset] = (byte)(
                (destination[destinationOffset] & 0x0F) | audioFlag0);
            destination[destinationOffset + 1] = (byte)(
                (destination[destinationOffset + 1] & 0x7C) | audioFlag1);
            destination[destinationOffset + 4] = headphoneVolume;
            destination[destinationOffset + 5] = speakerVolumeSnapshot;
            destination[destinationOffset + 6] = microphoneVolumeSnapshot;
            destination[destinationOffset + 7] = audioControl;
            destination[destinationOffset + 8] = muteLed;
            destination[destinationOffset + 9] = powerSaveControl;
            destination[destinationOffset + 37] = audioControl2;
        }

        internal static int GetNativeGameLedOwnershipUpdate(byte[] state,
            int stateOffset)
        {
            const byte lightbarControl = 0x04;
            const byte releaseLeds = 0x08;
            const byte playerLedControl = 0x10;
            const int flag1Offset = 1;
            if (state == null || stateOffset < 0 ||
                stateOffset + flag1Offset >= state.Length)
            {
                return 0;
            }

            byte flags = state[stateOffset + flag1Offset];
            bool controlsPlayer = (flags & playerLedControl) != 0;
            bool controlsLightbar = (flags & lightbarControl) != 0;
            bool explicitlyReleased = (flags & releaseLeds) != 0;
            if (explicitlyReleased)
            {
                return -1;
            }

            return controlsPlayer || controlsLightbar ? 1 : 0;
        }

        private void PublishNativeGameLightbarOwnership(bool released)
        {
            if (physicalOutputStateMailbox.
                SetNativeGameLightbarOwnershipReleased(released))
            {
                QueuePhysicalOutputUpdate();
            }
        }

        private static void MergeProfileLightbarIntoV5AudioSnapshot(
            in DualSensePhysicalOutputSnapshot source, byte[] destination,
            int destinationOffset)
        {
            const byte ledValidityMask = 0x14;
            const byte releaseLedMask = 0x08;
            const byte lightbarSetupValidityMask = 0x02;
            destination[destinationOffset + 1] = (byte)(
                (destination[destinationOffset + 1] &
                    ~(ledValidityMask | releaseLedMask)) |
                ledValidityMask);
            destination[destinationOffset + 38] = (byte)(
                (destination[destinationOffset + 38] &
                    ~lightbarSetupValidityMask) |
                lightbarSetupValidityMask);
            destination[destinationOffset + 41] = 0x02;
            destination[destinationOffset + 42] = 0x02;
            destination[destinationOffset + 43] =
                source.ActivePlayerLedMask;
            destination[destinationOffset + 44] =
                source.ProfileLightbar.LightBarColor.red;
            destination[destinationOffset + 45] =
                source.ProfileLightbar.LightBarColor.green;
            destination[destinationOffset + 46] =
                source.ProfileLightbar.LightBarColor.blue;
        }

        private bool BluetoothAudioPacerOwnsTransport()
        {
            lock (bluetoothAudioPacerLock)
            {
                // A faulted or stopping helper still owns its dedicated HID
                // handle until its process/writer retirement barrier completes.
                // Test the owner reference, not IsRunning.
                return PacerReferenceRetainsBluetoothTransportOwnership(
                    bluetoothAudioPacer != null);
            }
        }

        private static bool PacerReferenceRetainsBluetoothTransportOwnership(
            bool pacerReferencePresent)
        {
            return pacerReferencePresent;
        }

        private static bool RequiresCompletionAwareBluetoothControlWrite(
            bool completionRequested, bool speakerClockActive,
            bool pacerOwnsTransport)
        {
            // Ordinary idle state/haptics are physically queued to the helper
            // but must not make the controller input/gyro thread wait on IPC +
            // HID completion. Only mic transitions and shutdown barriers need
            // an exact completion acknowledgement.
            return completionRequested;
        }

        private bool TryWriteCachedBluetoothCombinedControlReport(
            bool includeNativeHaptics, string reportDescription,
            bool waitForCompletion = false,
            bool allowDuringStopping = false)
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            return TryWriteCachedBluetoothCombinedControlReport(
                includeNativeHaptics, reportDescription, waitForCompletion,
                allowDuringStopping, outputState);
        }

        private bool TryWriteCachedBluetoothCombinedControlReport(
            bool includeNativeHaptics, string reportDescription,
            bool waitForCompletion, bool allowDuringStopping,
            in DualSensePhysicalOutputSnapshot outputState)
        {
            if (!allowDuringStopping &&
                Volatile.Read(ref bluetoothOutputTransportStopping) != 0)
            {
                LastBluetoothHapticsWriteStatus =
                    $"Rejected {reportDescription}: Bluetooth output is stopping.";
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref bluetoothCombinedControlCommitClaimed, 1, 0) != 0)
            {
                LastBluetoothHapticsWriteStatus =
                    $"Deferred {reportDescription}: another ordered control commit owns the fixed dispatch slot.";
                return false;
            }

            byte reportSequenceBefore = 0;
            byte packetSequenceBefore = 0;
            bool sequenceInitializedBefore = false;
            byte reportSequenceAfter = 0;
            byte packetSequenceAfter = 0;
            bool sequenceInitializedAfter = false;
            long hapticsGeneration = 0;
            DualSenseBluetoothAudioPacer completionPacer = null;
            DualSenseBluetoothAudioPacer.ControlReportCompletionToken
                completionToken = default;
            bool helperOwnsTransport = false;
            bool admitted = false;
            try
            {
                if (!EnsureBluetoothCombinedOutputTransport())
                {
                    return false;
                }

                lock (bluetoothCombinedTransportWriteLock)
                {
                    if (!allowDuringStopping && Volatile.Read(
                            ref bluetoothOutputTransportStopping) != 0)
                    {
                        LastBluetoothHapticsWriteStatus =
                            $"Rejected {reportDescription}: Bluetooth output is stopping.";
                        return false;
                    }

                    if (Volatile.Read(
                            ref bluetoothAudioLifecycleTransitioning) != 0)
                    {
                        LastBluetoothHapticsWriteStatus =
                            $"Deferred {reportDescription}: Bluetooth audio ownership is transitioning.";
                        return false;
                    }

                    byte[] combined = bluetoothCombinedControlCommitReport;
                    lock (bluetoothCombinedSpeakerReportLock)
                    {
                        if (!bluetoothCombinedSpeakerReportAvailable)
                        {
                            return false;
                        }

                        Array.Copy(latestBluetoothCombinedSpeakerReport,
                            combined, combined.Length);
                        hapticsGeneration =
                            bluetoothCombinedHapticsGeneration;
                    }

                    combined[BluetoothCombinedHapticsOffset] = 0x92;
                    combined[BluetoothCombinedHapticsOffset + 1] =
                        BluetoothCombinedHapticsDataLength;
                    if (!includeNativeHaptics)
                    {
                        Array.Clear(combined,
                            BluetoothCombinedHapticsDataOffset,
                            BluetoothCombinedHapticsDataLength);
                    }

                    for (int index = 5; index <= 9; index++)
                    {
                        combined[index] =
                            BluetoothCombinedLowLatencyBufferLength;
                    }

                    // A control keepalive deliberately omits packet 0x13.
                    Array.Clear(combined, BluetoothCombinedSpeakerOffset,
                        BluetoothCombinedOutputReportLength - sizeof(uint) -
                            BluetoothCombinedSpeakerOffset);
                    if (outputState.EnableSpeakerOutput)
                    {
                        ApplyBluetoothSpeakerVolumeAndRoutingCore(combined,
                            outputState.SpeakerVolume,
                            outputState.HeadsetOnlyAudio,
                            outputState.HeadphoneVolume);
                    }

                    ApplyBluetoothMicrophoneStreamingRequest(combined,
                        outputState);
                    lock (bluetoothCombinedSpeakerReportLock)
                    {
                        reportSequenceBefore =
                            bluetoothCombinedSpeakerReportSequence;
                        packetSequenceBefore =
                            bluetoothCombinedSpeakerPacketSequence;
                        sequenceInitializedBefore =
                            bluetoothCombinedSpeakerSequenceInitialized;
                    }
                    ApplyNextBluetoothCombinedSequence(combined,
                        advancesMediaPacketSequence: false);
                    lock (bluetoothCombinedSpeakerReportLock)
                    {
                        reportSequenceAfter =
                            bluetoothCombinedSpeakerReportSequence;
                        packetSequenceAfter =
                            bluetoothCombinedSpeakerPacketSequence;
                        sequenceInitializedAfter =
                            bluetoothCombinedSpeakerSequenceInitialized;
                    }
                    ApplyBluetoothCombinedCrc(combined);

                    long hapticsExpiryQpc = includeNativeHaptics ?
                        PersistentBluetoothHapticsExpiryQpc : 0;
                    if (waitForCompletion)
                    {
                        // The sole permitted nested admission order is:
                        // combined transport -> pacer reference claim ->
                        // pacer fixed-copy/ring state. Pacer code has no
                        // callback into this device and releases its state
                        // before IPC, so no reverse edge exists.
                        admitted =
                            TryQueueBluetoothControlThroughAudioPacer(
                                bluetoothCombinedControlCommitReport,
                                hapticsExpiryQpc, out completionPacer,
                                out completionToken,
                                out helperOwnsTransport);
                    }
                    else
                    {
                        // Template publication is nonblocking local pacer
                        // admission. Keep it in the same short ordering
                        // boundary as speaker queue admission so its sequence
                        // reservation cannot be passed by a later report.
                        admitted = TryUpdateBluetoothAudioPacerTemplate(
                            bluetoothCombinedControlCommitReport,
                            hapticsExpiryQpc, out helperOwnsTransport) &&
                            helperOwnsTransport;
                    }

                    if (!admitted)
                    {
                        lock (bluetoothCombinedSpeakerReportLock)
                        {
                            if (bluetoothCombinedSpeakerReportSequence ==
                                    reportSequenceAfter &&
                                bluetoothCombinedSpeakerPacketSequence ==
                                    packetSequenceAfter &&
                                bluetoothCombinedSpeakerSequenceInitialized ==
                                    sequenceInitializedAfter)
                            {
                                bluetoothCombinedSpeakerReportSequence =
                                    reportSequenceBefore;
                                bluetoothCombinedSpeakerPacketSequence =
                                    packetSequenceBefore;
                                bluetoothCombinedSpeakerSequenceInitialized =
                                    sequenceInitializedBefore;
                            }
                        }
                    }
                }

                if (!admitted)
                {
                    LastBluetoothHapticsWriteStatus =
                        $"Deferred {reportDescription}: unified Bluetooth helper rejected the update.";
                    if (!helperOwnsTransport)
                    {
                        RequestUnifiedBluetoothOutputTransportRecovery();
                    }
                    return false;
                }

                bool written = !waitForCompletion ||
                    WaitForBluetoothControlThroughAudioPacer(
                        completionPacer, completionToken);
                if (!written)
                {
                    // Admission irrevocably consumed this sequence. The
                    // helper can still acknowledge after a timeout, and a
                    // following speaker report may already own the next
                    // sequence, so completion failure must never roll back.
                    LastBluetoothHapticsWriteStatus =
                        $"Deferred {reportDescription}: unified Bluetooth control completion was not presented.";
                    return false;
                }

                if (includeNativeHaptics)
                {
                    MarkBluetoothCombinedHapticsSubmitted(hapticsGeneration);
                }

                if (waitForCompletion)
                {
                    // The helper acknowledged the exact ordered control IRP.
                    Interlocked.Exchange(
                        ref bluetoothMicrophoneControlUpdatePending, 0);
                }
                LastBluetoothHapticsWriteStatus =
                    waitForCompletion ?
                        $"Unified Bluetooth {reportDescription} write completed." :
                        $"Queued unified Bluetooth {reportDescription}.";
                return true;
            }
            finally
            {
                Volatile.Write(ref bluetoothCombinedControlCommitClaimed, 0);
            }
        }

        private bool TryWriteCachedBluetoothCombinedSpeakerReport(
            bool hapticsSynchronized)
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            lock (bluetoothCombinedTransportWriteLock)
            {
                return TryWriteCachedBluetoothCombinedSpeakerReportCore(
                    hapticsSynchronized, outputState);
            }
        }

        private bool TryWriteCachedBluetoothCombinedSpeakerReportCore(
            bool hapticsSynchronized,
            in DualSensePhysicalOutputSnapshot outputState)
        {
            if (conType != ConnectionType.BT ||
                !outputState.EnableSpeakerOutput)
            {
                return false;
            }

            byte[] combined = bluetoothCombinedSpeakerWorkingReport;
            long hapticsGeneration;
            lock (bluetoothCombinedSpeakerReportLock)
            {
                if (!bluetoothCombinedSpeakerReportAvailable)
                {
                    return false;
                }

                Array.Copy(latestBluetoothCombinedSpeakerReport, combined, combined.Length);
                hapticsGeneration = bluetoothCombinedHapticsGeneration;
            }

            // Empty speaker TLVs can make the controller emit an alert tone.
            // Add the lane only when this tick has one fresh Opus frame.
            combined[BluetoothCombinedSpeakerOffset] = 0;
            combined[BluetoothCombinedSpeakerOffset + 1] = 0;
            Array.Clear(combined, BluetoothCombinedSpeakerDataOffset,
                BluetoothCombinedSpeakerFrameLength);
            if (!TryTakeBluetoothSpeakerAudioFrame(combined,
                BluetoothCombinedSpeakerDataOffset,
                outputState.EnableSpeakerOutput))
            {
                return false;
            }

            // Preserve the proven controller-side speaker configuration on
            // every audio report. This is protocol state, not a host-side
            // startup prefill; presentation still starts immediately.
            combined[5] = BluetoothCombinedSpeakerBufferLength;
            combined[6] = BluetoothCombinedSpeakerBufferLength;
            combined[7] = BluetoothCombinedSpeakerBufferLength;
            combined[8] = BluetoothCombinedSpeakerBufferLength;
            combined[9] = BluetoothCombinedSpeakerBufferLength;
            combined[BluetoothCombinedSpeakerOffset] =
                GetBluetoothCombinedSpeakerPacketType(
                    outputState.HeadsetOnlyAudio);
            combined[BluetoothCombinedSpeakerOffset + 1] =
                BluetoothCombinedSpeakerFrameLength;
            ApplyBluetoothSpeakerVolumeAndRoutingCore(combined,
                outputState.SpeakerVolume, outputState.HeadsetOnlyAudio,
                outputState.HeadphoneVolume);
            ApplyBluetoothMicrophoneStreamingRequest(combined, outputState);
            byte reportSequenceBefore;
            byte packetSequenceBefore;
            bool sequenceInitializedBefore;
            lock (bluetoothCombinedSpeakerReportLock)
            {
                reportSequenceBefore = bluetoothCombinedSpeakerReportSequence;
                packetSequenceBefore = bluetoothCombinedSpeakerPacketSequence;
                sequenceInitializedBefore =
                    bluetoothCombinedSpeakerSequenceInitialized;
            }
            ApplyNextBluetoothCombinedSequence(combined,
                advancesMediaPacketSequence: true);
            ApplyBluetoothCombinedCrc(combined);

            long hapticsExpiryQpc = PersistentBluetoothHapticsExpiryQpc;
            bool written = TryQueueBluetoothAudioPacerReport(combined,
                hapticsExpiryQpc, out bool pacerOwnsTransport);
            if (!pacerOwnsTransport)
            {
                RequestUnifiedBluetoothOutputTransportRecovery();
            }

            if (!written)
            {
                // Queue rejection is backpressure, not presentation. Roll the
                // sequence reservation back while the combined transport lock
                // still excludes every other output producer. The passthrough
                // retains and retries the exact encoded packet; its retry then
                // receives the sequence that was never physically accepted.
                lock (bluetoothCombinedSpeakerReportLock)
                {
                    bluetoothCombinedSpeakerReportSequence =
                        reportSequenceBefore;
                    bluetoothCombinedSpeakerPacketSequence =
                        packetSequenceBefore;
                    bluetoothCombinedSpeakerSequenceInitialized =
                        sequenceInitializedBefore;
                }
                Interlocked.Increment(ref bluetoothCombinedSpeakerWriteFailures);
                RequestUnifiedBluetoothOutputTransportRecovery();
                return false;
            }

            MarkBluetoothCombinedHapticsSubmitted(hapticsGeneration);

            Interlocked.Increment(ref bluetoothCombinedSpeakerReportsWritten);
            if (hapticsSynchronized)
            {
                Interlocked.Increment(ref bluetoothCombinedHapticsPairedWrites);
                LastBluetoothHapticsWriteStatus =
                    "Haptics-synchronized combined Bluetooth write accepted.";
            }
            else
            {
                Interlocked.Increment(ref bluetoothCombinedSpeakerFallbackWrites);
                LastBluetoothHapticsWriteStatus =
                    "Speaker fallback combined Bluetooth write accepted.";
            }

            return true;
        }

        private static void ApplyBluetoothCombinedCrc(byte[] combined)
        {
            uint crc = DualSenseBluetoothCrc32(combined,
                combined.Length - sizeof(uint));
            combined[combined.Length - 4] = (byte)crc;
            combined[combined.Length - 3] = (byte)(crc >> 8);
            combined[combined.Length - 2] = (byte)(crc >> 16);
            combined[combined.Length - 1] = (byte)(crc >> 24);
        }

        public bool SetBluetoothMicrophoneStreaming(bool enabled)
        {
            DualSensePhysicalOutputSnapshot outputState =
                physicalOutputStateMailbox.ReadLatest();
            if (conType != ConnectionType.BT)
            {
                LastBluetoothMicrophoneWriteStatus =
                    $"Rejected: controller connection type is {conType}, not Bluetooth.";
                return false;
            }

            // Do not hold the combined transport lock across helper/process or
            // OVERLAPPED retirement waits. Speaker-session traffic performs
            // this transition on its dedicated lifecycle worker; this control
            // path is never the real-time speaker producer.
            if (enabled && !IsBluetoothSpeakerClockActive())
            {
                PrepareBluetoothSpeakerClockTransport();
            }

            lock (bluetoothCombinedTransportWriteLock)
            {
                // StopOutputUpdate publishes this gate before it waits for this
                // lock. A VIIPER re-arm already in flight either completes
                // first (and is undone by the final shutdown disable) or sees
                // the gate here; it can never recreate transport afterwards.
                if (Volatile.Read(ref bluetoothOutputTransportStopping) != 0)
                {
                    LastBluetoothMicrophoneWriteStatus =
                        "Rejected: Bluetooth output is stopping.";
                    return false;
                }

                Interlocked.Increment(ref bluetoothMicrophoneRequestGeneration);
                Volatile.Write(ref bluetoothMicrophoneStreamingRequested,
                    enabled ? 1 : 0);
                Interlocked.Exchange(
                    ref bluetoothMicrophoneControlUpdatePending, 1);
                if (!enabled)
                {
                    Interlocked.Exchange(
                        ref bluetoothMicrophoneLastFrameTimestamp, 0);
                }
            }

            if (!EnsureBluetoothCombinedOutputTransport())
            {
                LastBluetoothMicrophoneWriteStatus =
                    LastBluetoothHapticsWriteStatus;
                return false;
            }

            lock (bluetoothCombinedSpeakerReportLock)
            {
                ApplyBluetoothMicrophoneStreamingRequest(
                    latestBluetoothCombinedSpeakerReport, enabled);
            }

            if (ShouldPublishMicrophoneStateThroughSpeakerClock(
                outputState.EnableSpeakerOutput,
                IsBluetoothSpeakerClockActive()))
            {
                bool published =
                    QueueBluetoothAudioPacerMicrophoneTransitionFromCache(
                        outputState, enabled);
                if (!published)
                {
                    RequestUnifiedBluetoothOutputTransportRecovery();
                }
                LastBluetoothMicrophoneWriteStatus = published ?
                    (enabled ?
                        "Microphone enable is pending physical commit on the combined speaker stream." :
                        "Microphone disable is pending physical commit on the combined speaker stream.") :
                    "Microphone control could not be published to the combined speaker stream.";
                return published;
            }

            bool written = TryWriteCachedBluetoothCombinedControlReport(
                includeNativeHaptics: true,
                reportDescription: enabled ?
                    "microphone enable" : "microphone disable",
                waitForCompletion: true,
                allowDuringStopping: false,
                outputState: outputState);
            LastBluetoothMicrophoneWriteStatus =
                LastBluetoothHapticsWriteStatus;
            return written;
        }

        internal static bool ShouldPublishMicrophoneStateThroughSpeakerClock(
            bool speakerOutputEnabled, bool speakerClockActive)
        {
            return speakerOutputEnabled && speakerClockActive;
        }

        private void ApplyBluetoothMicrophoneStreamingRequest(byte[] report,
            in DualSensePhysicalOutputSnapshot outputState)
        {
            bool enabled =
                Volatile.Read(ref bluetoothMicrophoneStreamingRequested) != 0;
            ApplyBluetoothMicrophoneStreamingRequest(report, enabled);

            // Apply microphone gain as a dedicated controller state transition.
            // The profile keeps its full 0x00-0xFF software-gain range, while
            // the physical controller receives its documented 0x00-0x40 ADC
            // range. Sending 0xFF here clips the ADC before the decoded PCM can
            // reach the shared limiter.
            // Speaker snapshots intentionally strip that state, so restore it
            // only while an enable transition is awaiting physical proof. This
            // prevents a mic enabled after the speaker clock started from
            // inheriting the controller's quiet/default ADC level without
            // replaying mic control on every 10.667 ms audio frame forever.
            if (enabled && Volatile.Read(
                    ref bluetoothMicrophoneControlUpdatePending) != 0)
            {
                ApplyBluetoothMicrophoneVolume(report,
                    outputState.MicrophoneVolume);
            }
        }

        private static void ApplyBluetoothMicrophoneStreamingRequest(
            byte[] report, bool enabled)
        {
            if (report == null ||
                report.Length <= BluetoothCombinedAudioControlFlagsOffset)
            {
                return;
            }

            if (enabled)
            {
                report[BluetoothCombinedAudioControlFlagsOffset] |=
                    BluetoothMicrophoneControlEnable;
            }
            else
            {
                report[BluetoothCombinedAudioControlFlagsOffset] &=
                    unchecked((byte)~BluetoothMicrophoneControlEnable);
            }
        }

        private static byte MapDualSenseSpeakerVolume(byte profileVolume)
        {
            if (profileVolume == 0)
            {
                return 0;
            }

            int firmwareRange = DualSenseSpeakerVolumeMaximum -
                DualSenseSpeakerVolumeMinimum;
            return (byte)(DualSenseSpeakerVolumeMinimum +
                (profileVolume * firmwareRange + byte.MaxValue / 2) /
                byte.MaxValue);
        }

        private static byte MapDualSenseHeadphoneVolume(byte profileVolume)
        {
            return (byte)((profileVolume * DualSenseHeadphoneVolumeMaximum +
                byte.MaxValue / 2) / byte.MaxValue);
        }

        private static byte GetBluetoothCombinedSpeakerPacketType(
            bool headsetOnlyAudio) => headsetOnlyAudio ?
                BluetoothCombinedHeadsetPacketType :
                BluetoothCombinedSpeakerPacketType;

        // Retain the two-argument protocol helper for callers and regression
        // tests that only need the standard speaker route.
        private static void ApplyBluetoothSpeakerVolumeAndRouting(
            byte[] combined, byte profileVolume)
        {
            ApplyBluetoothSpeakerVolumeAndRoutingCore(combined, profileVolume,
                false, 128);
        }

        private static void ApplyBluetoothSpeakerVolumeAndRoutingCore(
            byte[] combined, byte profileVolume, bool headsetOnlyAudio,
            byte headphoneVolume)
        {
            // V5 keeps the complete native audio snapshot armed on every
            // 0x36, independent of the header's FE/FF microphone mode. Keep
            // both volume fields valid and preserve the microphone volume and
            // audio-clock fields instead of alternating state shapes.
            combined[BluetoothCombinedStateFlag0Offset] |= (byte)(
                DualSenseOutputFlag0HeadphoneVolumeEnable |
                DualSenseOutputFlag0SpeakerVolumeEnable |
                DualSenseOutputFlag0MicrophoneVolumeEnable |
                DualSenseOutputFlag0AudioControlEnable);
            combined[BluetoothCombinedStateFlag1Offset] |= (byte)(
                DualSenseOutputFlag1MicrophoneMuteLedControlEnable |
                DualSenseOutputFlag1PowerSaveControlEnable |
                DualSenseOutputFlag1AudioControl2Enable);
            byte mappedSpeakerVolume = MapDualSenseSpeakerVolume(
                profileVolume);
            combined[BluetoothCombinedStateHeadphoneVolumeOffset] =
                headsetOnlyAudio ?
                    MapDualSenseHeadphoneVolume(headphoneVolume) :
                    mappedSpeakerVolume;
            combined[BluetoothCombinedStateSpeakerVolumeOffset] =
                headsetOnlyAudio ? (byte)0 : mappedSpeakerVolume;
            combined[BluetoothCombinedStateMicrophoneVolumeOffset] =
                DualSenseMicrophoneVolumeMaximum;
            if (headsetOnlyAudio)
            {
                // A 0x96 headset frame is the same fixed-cadence audio lane as
                // 0x93. Keep a valid gain snapshot just as the the validated implementation
                // combined transport does; clearing it can leave the AUX DAC
                // quiet or inconsistently armed on controller firmware.
                combined[BluetoothCombinedStateFlag1Offset] |=
                    DualSenseOutputFlag1AudioControl2Enable;
                combined[BluetoothCombinedStateAudioControlOffset] =
                    DualSenseAudioControlOutputHeadphones;
                combined[BluetoothCombinedStateAudioControl2Offset] =
                    DualSenseSpeakerPreGain;
            }
            else
            {
                combined[BluetoothCombinedStateAudioControlOffset] =
                    DualSenseAudioControlOutputSpeaker;
                combined[BluetoothCombinedStateAudioControl2Offset] =
                    DualSenseSpeakerPreGain;
            }
        }

        private static byte MapDualSenseMicrophoneVolume(byte profileVolume)
        {
            // Sony's physical output report and the validated implementation both use 0x40 as
            // the maximum microphone level. Keep the profile/UI byte range and
            // map it once at the hardware protocol boundary.
            return (byte)((profileVolume * DualSenseMicrophoneVolumeMaximum +
                byte.MaxValue / 2) / byte.MaxValue);
        }

        private static void ApplyBluetoothMicrophoneVolume(byte[] combined,
            byte profileVolume)
        {
            if (combined == null ||
                combined.Length <= BluetoothCombinedStateMicrophoneVolumeOffset)
            {
                return;
            }

            combined[BluetoothCombinedStateFlag0Offset] |=
                DualSenseOutputFlag0MicrophoneVolumeEnable;
            combined[BluetoothCombinedStateMicrophoneVolumeOffset] =
                MapDualSenseMicrophoneVolume(profileVolume);
        }

        private static uint DualSenseBluetoothCrc32(byte[] data, int length)
        {
            uint crc = ~0xEADA2D49u;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
                }
            }

            return ~crc;
        }

        private void CalculateDeviceSlotMask()
        {
            // Map 1-8 to a symmetrical LED array from a set of
            // 5 LED lights
            switch (deviceSlotNumber)
            {
                case 0:
                    deviceSlotMask = 0x04;
                    break;
                case 1:
                    deviceSlotMask = 0x02 | 0x08;
                    break;
                case 2:
                    deviceSlotMask = 0x01 | 0x04 | 0x10;
                    break;
                case 3:
                    deviceSlotMask = 0x01 | 0x02 | 0x08 | 0x10;
                    break;
                case 4:
                    deviceSlotMask = 0x01 | 0x10;
                    break;
                case 5:
                    deviceSlotMask = 0x02 | 0x04 | 0x08;
                    break;
                case 6:
                    deviceSlotMask = 0x01 | 0x02 | 0x04 | 0x08 | 0x10;
                    break;
                case 7:
                default:
                    deviceSlotMask = 0x00;
                    break;
            }
        }

        private void PrepareMuteLEDByte()
        {
            byte value = 0;
            if (nativeOptionsStore != null)
            {
                switch (nativeOptionsStore.MuteLedMode)
                {
                    case DualSenseControllerOptions.MuteLEDMode.Off:
                        value = 0x00;
                        break;
                    case DualSenseControllerOptions.MuteLEDMode.On:
                        value = 0x01;
                        break;
                    case DualSenseControllerOptions.MuteLEDMode.Pulse:
                        value = 0x02;
                        break;
                    default:
                        value = 0x00;
                        break;
                }
            }

            if (physicalOutputStateMailbox.SetMuteLedByte(value))
            {
                QueuePhysicalOutputUpdate();
            }
        }

        private void PreparePlayerLEDBarByte()
        {
            byte value = physicalOutputStateMailbox.ReadLatest().
                ActivePlayerLedMask;
            if (nativeOptionsStore != null)
            {
                if (nativeOptionsStore.LedMode == DualSenseControllerOptions.LEDBarMode.Off)
                {
                    value = 0x00;
                }
                else if (nativeOptionsStore.LedMode == DualSenseControllerOptions.LEDBarMode.On)
                {
                    value = deviceSlotMask;
                }
                else if (nativeOptionsStore.LedMode == DualSenseControllerOptions.LEDBarMode.BatteryPercentage)
                {
                    value = DeviceBatteryLinearMask(battery);
                }
            }

            if (physicalOutputStateMailbox.SetActivePlayerLedMask(value))
            {
                QueuePhysicalOutputUpdate();
            }
        }

        public override void PrepareTriggerEffect(TriggerId trigger, TriggerEffects effect, TriggerEffectSettings effectSettings)
        {
            if (trigger != TriggerId.LeftTrigger &&
                trigger != TriggerId.RightTrigger)
            {
                throw new ArgumentOutOfRangeException(nameof(trigger),
                    "Invalid Trigger Id");
            }

            TriggerEffectData triggerState = default;
            triggerState.ChangeData(effect, effectSettings);
            if (physicalOutputStateMailbox.SetTrigger(trigger, triggerState))
            {
                QueuePhysicalOutputUpdate();
            }
        }

        public void PrepareRawTriggerEffect(TriggerId trigger, byte mode, byte startResistance,
            byte effectForce, byte rangeForce, byte nearReleaseStrength, byte nearMiddleStrength,
            byte pressedStrength, byte frequency)
        {
            if (trigger != TriggerId.LeftTrigger &&
                trigger != TriggerId.RightTrigger)
            {
                throw new ArgumentOutOfRangeException(nameof(trigger),
                    "Invalid Trigger Id");
            }

            TriggerEffectData triggerState = default;
            triggerState.ChangeRaw(mode, startResistance, effectForce,
                rangeForce, nearReleaseStrength, nearMiddleStrength,
                pressedStrength, frequency);
            if (physicalOutputStateMailbox.SetTrigger(trigger, triggerState))
            {
                QueuePhysicalOutputUpdate();
            }
        }

        private byte DeviceBatteryLinearMask(int deviceBattery)
        {
            byte batteryMask;
            if (deviceBattery >= 95)
                batteryMask = 0x01 | 0x02 | 0x08 | 0x10;
            else if (deviceBattery >= 70)
                batteryMask = 0x01 | 0x02 | 0x08;
            else if (deviceBattery >= 50)
                batteryMask = 0x01 | 0x02;
            else if (deviceBattery >= 20)
                batteryMask = 0x01;
            else if (deviceBattery >= 5)
                batteryMask = 0x01 | 0x02 | 0x04;
            else
                batteryMask = 0x00;

            return batteryMask;
        }

        public override void CheckControllerNumDeviceSettings(int numControllers)
        {
            if (nativeOptionsStore != null)
            {
                if (nativeOptionsStore.LedMode ==
                    DualSenseControllerOptions.LEDBarMode.MultipleControllers)
                {
                    byte value;
                    if (numControllers > 1)
                    {
                        value = deviceSlotMask;
                    }
                    else
                    {
                        value = 0x00;
                    }

                    if (physicalOutputStateMailbox.
                        SetActivePlayerLedMask(value))
                    {
                        QueuePhysicalOutputUpdate();
                    }
                }
            }
        }

        private void SetupOptionsEvents()
        {
            if (nativeOptionsStore != null)
            {
                nativeOptionsStore.MuteLedModeChanged += (sender, e) =>
                {
                    PrepareMuteLEDByte();
                };

                nativeOptionsStore.LedModeChanged += (sender, e) =>
                {
                    PreparePlayerLEDBarByte();
                };
            }
        }

        public override void LoadStoreSettings()
        {
            if (nativeOptionsStore != null)
            {
                PrepareMuteLEDByte();
                PreparePlayerLEDBarByte();
            }
        }
    }
}
