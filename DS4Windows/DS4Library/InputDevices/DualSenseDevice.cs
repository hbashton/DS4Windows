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
        private byte[] outputBTCrc32Head = new byte[] { 0xA2 };
        //private byte outputPendCount = 0;
        private new GyroMouseSensDualSense gyroMouseSensSettings;
        public override GyroMouseSens GyroMouseSensSettings { get => gyroMouseSensSettings; }

        private byte activePlayerLEDMask = 0x00;

        private byte hapticPowerLevel = (byte)HapticPowerLevelFriendlyName.Str100;
        public byte HapticPowerLevel
        {
            get => hapticPowerLevel;
            set => hapticPowerLevel = value;
        }

        protected bool useRumble = true;
        public bool UseRumble { get => useRumble; set => useRumble = value; }

        // Accurate rumble emulation mode requires 2.24 firmware or newer. On official hardware it takes priority over normal/legacy rumble
        protected bool useAccurateRumble = true; 
        public bool UseAccurateRumble { get => useAccurateRumble; set => useAccurateRumble = value; }

        private byte headphoneVolume = 128;
        public byte HeadphoneVolume { get => headphoneVolume; set { headphoneVolume = value; outputDirty = true; } }

        private byte speakerVolume = 128;
        public byte SpeakerVolume { get => speakerVolume; set { speakerVolume = value; outputDirty = true; } }

        private bool headsetOnlyAudio;
        public bool HeadsetOnlyAudio
        {
            get => headsetOnlyAudio;
            set
            {
                if (headsetOnlyAudio == value) return;
                headsetOnlyAudio = value;
                outputDirty = true;
            }
        }

        private byte microphoneVolume = 128;
        public byte MicrophoneVolume { get => microphoneVolume; set { microphoneVolume = value; outputDirty = true; } }

        private bool enableSpeakerOutput;
        public bool EnableSpeakerOutput
        {
            get => enableSpeakerOutput;
            set
            {
                if (enableSpeakerOutput == value)
                {
                    return;
                }

                enableSpeakerOutput = value;
                if (!value)
                {
                    ClearBluetoothSpeakerAudioFrame();
                }

                outputDirty = true;
            }
        }

        private TriggerEffectData l2EffectData;
        private TriggerEffectData r2EffectData;

        private byte muteLEDByte = 0x00;
        private bool microphoneMuteOverride;
        private bool microphoneMuted;
        private int profileMicrophoneMuteState;
        private bool muteLedOverride;
        private bool muteLedOn;
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
        // PadSense's clean Windows speaker and duplex traces keep every 0x36
        // media-lane depth at 0x80. Use the same native contract for both FE
        // and FF reports; cadence remains one frame per 10.667 ms.
        private const byte BluetoothCombinedSpeakerBufferLength = 0x80;
        // The game, not a wall-clock timeout in DS4Windows, owns the end of a
        // native DualSense effect by publishing an explicit silent haptics
        // block. Expiring the newest block between otherwise valid virtual-
        // device callbacks creates audible and tactile holes in sustained
        // effects.
        private const long PersistentBluetoothHapticsExpiryQpc = long.MaxValue;
        private const int BluetoothCombinedNativeStateFreshnessMilliseconds = 100;
        // Presented Opus frames refresh this lease on every 10.667 ms tick.
        // The normal idle boundary clears it explicitly; expiry is the
        // fail-safe when a producer thread dies before reaching that boundary.
        private const int BluetoothSpeakerClockPresentedLeaseMilliseconds =
            3000;
        private const int BluetoothAudioPacerStartupRetryMilliseconds = 2000;
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
        private const byte BluetoothNormalInputBit = 0x01;
        private const byte BluetoothMicrophoneInputBit = 0x02;
        private const byte BluetoothMicrophoneControlEnable = 0x01;
        // PadSense repeats Sony's native wired-DualSense audio snapshot on
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
        private bool bluetoothCombinedSpeakerReportAvailable;
        private long latestBluetoothCombinedSpeakerReportTimestamp;
        private long latestBluetoothCombinedNativeStateTimestamp;
        private long bluetoothCombinedHapticsGeneration;
        private long bluetoothCombinedSubmittedHapticsGeneration;
        private byte bluetoothCombinedSpeakerReportSequence;
        private byte bluetoothCombinedSpeakerPacketSequence;
        private bool bluetoothCombinedSpeakerSequenceInitialized;
        private readonly object bluetoothAudioPacerLock = new object();
        private readonly object bluetoothAudioLifecycleLock = new object();
        private DualSenseBluetoothAudioPacer bluetoothAudioPacer;
        private string bluetoothAudioPacerLastError = string.Empty;
        private long bluetoothAudioPacerRetryAfterTimestamp;
        private int bluetoothAudioLifecycleTransitioning;
        private int bluetoothAudioRecoveryWorkerScheduled;
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
        private long bluetoothRejectedInputFrames;
        private int bluetoothLastRejectedInputTag = -1;

        public event Action<DualSenseDevice, byte[]> BluetoothMicrophoneOpusFrameReceived;

        public long BluetoothMicrophoneLastFrameTimestamp =>
            Interlocked.Read(ref bluetoothMicrophoneLastFrameTimestamp);

        public long BluetoothMicrophoneFramesReceived =>
            Interlocked.Read(ref bluetoothMicrophoneFramesReceived);

        public long BluetoothRejectedInputFrames =>
            Interlocked.Read(ref bluetoothRejectedInputFrames);

        public int BluetoothLastRejectedInputTag =>
            Volatile.Read(ref bluetoothLastRejectedInputTag);

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
                    usePadSensePresentationCadence: true))
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
            if (frame == null || length <= 0)
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
                    hapticsSynchronized);
                if (written)
                {
                    // The active/idle decision is serialized by this transport
                    // lock, so publish/refresh the clock lease only after the
                    // report was actually accepted. A failed later frame keeps
                    // the lease earned by the previous accepted frame; a failed
                    // first frame can never create a false active generation.
                    ClaimBluetoothSpeakerClock(
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
            if (conType != ConnectionType.BT || !enableSpeakerOutput)
            {
                return true;
            }

            lock (bluetoothCombinedTransportWriteLock)
            {
                if (!EnsureBluetoothCombinedOutputTransport())
                {
                    return false;
                }

                // Route changes are state updates on the one long-lived
                // PadSense transport. Never retire the helper or inject a
                // legacy 0x31 report between media generations.
                lock (bluetoothCombinedSpeakerReportLock)
                {
                    ApplyBluetoothSpeakerVolumeAndRoutingCore(
                        latestBluetoothCombinedSpeakerReport, speakerVolume,
                        headsetOnlyAudio, headphoneVolume);
                }

                bool published;
                if (IsBluetoothSpeakerClockActive())
                {
                    // The next already-clocked media generation applies the
                    // route atomically without inserting a control report.
                    published = RefreshBluetoothAudioPacerTemplateFromCache();
                }
                else
                {
                    published = TryWriteCachedBluetoothCombinedControlReport(
                        includeNativeHaptics: true,
                        reportDescription: headsetOnlyAudio ?
                            "AUX route" : "speaker route",
                        waitForCompletion: true);
                }
                if (!published)
                {
                    RequestUnifiedBluetoothOutputTransportRecovery();
                }

                return published;
            }
        }

        internal bool BeginBluetoothAtomicSpeakerFrame(long speakerSession)
        {
            lock (bluetoothCombinedTransportWriteLock)
            {
                if (speakerSession == 0 ||
                    bluetoothActiveSpeakerSession != speakerSession ||
                    conType != ConnectionType.BT || !enableSpeakerOutput ||
                    Volatile.Read(ref bluetoothOutputTransportStopping) != 0 ||
                    Volatile.Read(ref bluetoothAudioLifecycleTransitioning) != 0)
                {
                    return false;
                }

                // The paired haptics update follows before the PCM callback
                // releases its generation lock. Claiming here makes that update
                // template-only, so the first haptics and speaker data cannot
                // be presented as competing physical HID reports.
                return ClaimBluetoothSpeakerClock(
                    BluetoothSpeakerClockPresentedLeaseMilliseconds) != 0;
            }
        }

        internal bool EndBluetoothSpeakerGeneration(long speakerSession,
            long speakerGeneration)
        {
            lock (bluetoothCombinedTransportWriteLock)
            {
                if (speakerSession == 0 || speakerGeneration == 0 ||
                    bluetoothActiveSpeakerSession != speakerSession ||
                    bluetoothActiveSpeakerGeneration != speakerGeneration)
                {
                    return false;
                }

                bluetoothActiveSpeakerGeneration = 0;
                ClearBluetoothSpeakerAudioFrame();
                return true;
            }
        }

        internal bool ResetBluetoothSpeakerSession(long speakerSession)
        {
            lock (bluetoothCombinedTransportWriteLock)
            {
                if (speakerSession == 0 ||
                    bluetoothActiveSpeakerSession != speakerSession)
                {
                    return false;
                }

                bluetoothActiveSpeakerGeneration = 0;
                ClearBluetoothSpeakerAudioFrame();
                return true;
            }
        }

        /// <summary>
        /// Drops cached speaker data so an old Opus frame cannot be replayed
        /// after speaker output stops or its capture source changes.
        /// </summary>
        public void ClearBluetoothSpeakerAudioFrame()
        {
            lock (bluetoothCombinedTransportWriteLock)
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
                // Clear -> pending mic control is one atomic boundary. A new
                // speaker generation cannot slip reports between the helper
                // Clear and this completion-aware control commit.
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
        }

        private long ClaimBluetoothSpeakerClock(int leaseMilliseconds)
        {
            if (conType != ConnectionType.BT || !enableSpeakerOutput ||
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
            bool usePadSensePresentationCadence = false)
        {
            return TransitionBluetoothSpeakerClockTransport(
                ignoreRetryCooldown: false,
                usePadSensePresentationCadence,
                allowDuringStopping: false);
        }

        internal bool RecoverBluetoothSpeakerClockTransport(
            bool usePadSensePresentationCadence = false)
        {
            return TransitionBluetoothSpeakerClockTransport(
                ignoreRetryCooldown: true,
                usePadSensePresentationCadence,
                allowDuringStopping: false);
        }

        private bool RecoverBluetoothOutputTransportForShutdown()
        {
            return TransitionBluetoothSpeakerClockTransport(
                ignoreRetryCooldown: true,
                usePadSensePresentationCadence: true,
                allowDuringStopping: true);
        }

        private bool TransitionBluetoothSpeakerClockTransport(
            bool ignoreRetryCooldown,
            bool usePadSensePresentationCadence,
            bool allowDuringStopping)
        {
            if (conType != ConnectionType.BT ||
                (!allowDuringStopping && Volatile.Read(
                    ref bluetoothOutputTransportStopping) != 0))
            {
                return false;
            }

            lock (bluetoothAudioLifecycleLock)
            {
                byte[] initialTemplate =
                    new byte[BluetoothCombinedOutputReportLength];
                long initialHapticsExpiry;
                DualSenseBluetoothAudioPacer retiringPacer;
                lock (bluetoothCombinedTransportWriteLock)
                {
                    if ((!allowDuringStopping && Volatile.Read(
                            ref bluetoothOutputTransportStopping) != 0) ||
                        !EnsureBluetoothCombinedOutputTransport())
                    {
                        return false;
                    }

                    lock (bluetoothAudioPacerLock)
                    {
                        if (bluetoothAudioPacer?.IsRunning == true &&
                            (!usePadSensePresentationCadence ||
                                bluetoothAudioPacer.
                                    UsesPadSensePresentationCadence))
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

                    // Publish Transitioning before either old owner is detached.
                    // All report paths then return backpressure instead of
                    // inferring that a null pacer permits direct HID creation.
                    Volatile.Write(ref bluetoothAudioLifecycleTransitioning, 1);
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

                DualSenseBluetoothAudioPacer candidate = null;
                bool prepared = false;
                try
                {
                    if (retiringPacer != null)
                    {
                        bluetoothAudioPacerLastError = retiringPacer.LastError;
                        retiringPacer.Stop();
                        retiringPacer.Dispose();
                    }

                    // A stale cached speaker lane must not survive into the
                    // helper template. Speaker reports provide their own lane.
                    Array.Clear(initialTemplate,
                        BluetoothCombinedSpeakerOffset,
                        BluetoothCombinedOutputReportLength - sizeof(uint) -
                            BluetoothCombinedSpeakerOffset);
                    ApplyBluetoothSpeakerVolumeAndRoutingCore(initialTemplate,
                        speakerVolume, headsetOnlyAudio, headphoneVolume);
                    ApplyBluetoothMicrophoneStreamingRequest(initialTemplate);
                    if (!DualSenseBluetoothAudioPacer.TryStart(
                        hDevice?.DevicePath, initialTemplate,
                        initialHapticsExpiry,
                        usePadSensePresentationCadence, out candidate,
                        out string error))
                    {
                        bluetoothAudioPacerLastError = error ?? string.Empty;
                        Volatile.Write(ref bluetoothAudioPacerRetryAfterTimestamp,
                            Stopwatch.GetTimestamp() + Stopwatch.Frequency *
                                BluetoothAudioPacerStartupRetryMilliseconds / 1000);
                        return false;
                    }

                    lock (bluetoothCombinedTransportWriteLock)
                    {
                        if (!allowDuringStopping && Volatile.Read(
                                ref bluetoothOutputTransportStopping) != 0)
                        {
                            return false;
                        }

                        lock (bluetoothAudioPacerLock)
                        {
                            bluetoothAudioPacer = candidate;
                            bluetoothAudioPacer.UpdateCadenceRatio(
                                DualSenseControllerClockStable ?
                                    DualSenseControllerClockRatio : 1.0,
                                Volatile.Read(
                                    ref bluetoothLastInputArrivalQpc));
                            candidate = null;
                        }

                        bluetoothAudioPacerLastError = string.Empty;
                        Volatile.Write(ref bluetoothAudioPacerRetryAfterTimestamp, 0);
                        prepared = true;
                    }

                    return prepared;
                }
                finally
                {
                    if (candidate != null)
                    {
                        candidate.Stop();
                        candidate.Dispose();
                    }

                    Volatile.Write(ref bluetoothAudioLifecycleTransitioning, 0);
                }
            }
        }

        private void RequestUnifiedBluetoothOutputTransportRecovery()
        {
            if (!RequiresUnifiedBluetoothOutputTransport(conType) ||
                Volatile.Read(ref bluetoothOutputTransportStopping) != 0 ||
                Interlocked.CompareExchange(
                    ref bluetoothAudioRecoveryWorkerScheduled, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    while (Volatile.Read(
                        ref bluetoothOutputTransportStopping) == 0)
                    {
                        if (RecoverBluetoothSpeakerClockTransport(
                            usePadSensePresentationCadence: true))
                        {
                            // Recovery starts a fresh physical FIFO. Commit the
                            // latest coalesced state once before returning so a
                            // stale speaker lease cannot leave lightbar,
                            // trigger, rumble, or haptics changes template-only.
                            if (TryWriteCachedBluetoothCombinedControlReport(
                                    includeNativeHaptics: true,
                                    reportDescription:
                                        "recovered controller state",
                                    waitForCompletion: true))
                            {
                                return;
                            }
                        }

                        Thread.Sleep(BluetoothAudioPacerStartupRetryMilliseconds);
                    }
                }
                finally
                {
                    Interlocked.Exchange(
                        ref bluetoothAudioRecoveryWorkerScheduled, 0);
                }
            });
        }

        private void StopBluetoothAudioPacerLocked()
        {
            DualSenseBluetoothAudioPacer pacer;
            lock (bluetoothAudioPacerLock)
            {
                pacer = bluetoothAudioPacer;
                bluetoothAudioPacer = null;
                if (pacer != null)
                {
                    bluetoothAudioPacerLastError = pacer.LastError;
                }
            }

            if (pacer == null)
            {
                return;
            }

            pacer.Stop();
            pacer.Dispose();
        }

        private bool ClearBluetoothAudioPacerLocked()
        {
            lock (bluetoothAudioPacerLock)
            {
                if (bluetoothAudioPacer == null)
                {
                    return true;
                }

                if (bluetoothAudioPacer.IsRunning &&
                    bluetoothAudioPacer.Clear())
                {
                    return true;
                }

                bluetoothAudioPacerLastError =
                    bluetoothAudioPacer.LastError;
                bluetoothAudioPacer.Dispose();
                bluetoothAudioPacer = null;
                return false;
            }
        }

        private void StopBluetoothAudioPacer()
        {
            lock (bluetoothCombinedTransportWriteLock)
            {
                StopBluetoothAudioPacerLocked();
            }
        }

        private bool TryUpdateBluetoothAudioPacerTemplate(byte[] template,
            long hapticsExpiryQpc, out bool pacerOwnsTransport)
        {
            pacerOwnsTransport = false;
            lock (bluetoothAudioPacerLock)
            {
                if (bluetoothAudioPacer == null)
                {
                    return false;
                }

                // A faulted helper retains its duplicated HID handle until the
                // dedicated lifecycle worker crosses Stop/Dispose. Report that
                // ownership without blocking the controller/input caller.
                pacerOwnsTransport = true;
                if (!bluetoothAudioPacer.IsRunning)
                {
                    bluetoothAudioPacerLastError =
                        bluetoothAudioPacer.LastError;
                    return false;
                }

                // The complete mutable controller state lives in the native
                // 0x36 snapshot. Never create a second physical 0x31 lane.
                return bluetoothAudioPacer.UpdateTemplate(template,
                    hapticsExpiryQpc);
            }
        }

        private bool TryQueueBluetoothAudioPacerReport(byte[] report,
            long hapticsExpiryQpc, out bool pacerOwnsTransport)
        {
            pacerOwnsTransport = false;
            lock (bluetoothAudioPacerLock)
            {
                // A faulted/stopping helper still owns its duplicated HID
                // handle until Dispose crosses the child-process ownership
                // barrier. Never let recovery race that retained owner.
                pacerOwnsTransport =
                    PacerReferenceRetainsBluetoothTransportOwnership(
                        bluetoothAudioPacer != null);
                if (bluetoothAudioPacer?.IsRunning != true)
                {
                    return false;
                }

                return bluetoothAudioPacer.TryQueueReport(report,
                    hapticsExpiryQpc);
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
            lock (bluetoothAudioPacerLock)
            {
                return bluetoothAudioPacer?.IsRunning == true &&
                    bluetoothAudioPacer.UpdateCadenceRatio(ratio,
                        Volatile.Read(ref bluetoothLastInputArrivalQpc));
            }
        }

        private bool TryUpdateBluetoothAudioPacerMediaBuffer(byte level,
            long observationQpc)
        {
            lock (bluetoothAudioPacerLock)
            {
                if (bluetoothAudioPacer?.IsRunning != true)
                {
                    bluetoothMediaBufferServo.Reset();
                    Interlocked.Exchange(
                        ref bluetoothMediaBufferCadenceRatioBits,
                        BitConverter.DoubleToInt64Bits(1.0));
                    return false;
                }

                double cadenceRatio = bluetoothMediaBufferServo.Update(level,
                    observationQpc, observationQpc);
                Interlocked.Exchange(ref bluetoothMediaBufferCadenceRatioBits,
                    BitConverter.DoubleToInt64Bits(cadenceRatio));
                return bluetoothAudioPacer.UpdateControllerMediaBuffer(level,
                    observationQpc, cadenceRatio);
            }
        }

        private bool TryCommitBluetoothControlThroughAudioPacer(byte[] report,
            long hapticsExpiryQpc, bool waitForCompletion,
            out bool pacerOwnsTransport)
        {
            DualSenseBluetoothAudioPacer pacer;
            lock (bluetoothAudioPacerLock)
            {
                pacer = bluetoothAudioPacer;
                pacerOwnsTransport = pacer != null;
                if (pacer?.IsRunning != true)
                {
                    pacer = null;
                }
            }

            if (pacer == null)
            {
                return false;
            }

            if (!waitForCompletion)
            {
                return pacer.TryQueueReport(report, hapticsExpiryQpc);
            }

            bool presented = pacer.TryQueueControlReportAndWait(
                report, hapticsExpiryQpc,
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

        private bool RefreshBluetoothAudioPacerTemplateFromCache()
        {
            lock (bluetoothCombinedTransportWriteLock)
            {
                byte[] template = bluetoothCombinedSpeakerWorkingReport;
                long hapticsExpiryQpc;
                lock (bluetoothCombinedSpeakerReportLock)
                {
                    if (!bluetoothCombinedSpeakerReportAvailable)
                    {
                        return false;
                    }

                    Array.Copy(latestBluetoothCombinedSpeakerReport, template,
                        template.Length);
                    hapticsExpiryQpc =
                        PersistentBluetoothHapticsExpiryQpc;
                }

                ApplyBluetoothSpeakerVolumeAndRoutingCore(template,
                    speakerVolume, headsetOnlyAudio, headphoneVolume);
                ApplyBluetoothMicrophoneStreamingRequest(template);
                bool updated = TryUpdateBluetoothAudioPacerTemplate(template,
                    hapticsExpiryQpc, out bool pacerOwnsTransport);
                if (!pacerOwnsTransport || !updated)
                {
                    RequestUnifiedBluetoothOutputTransportRecovery();
                    return false;
                }

                return true;
            }
        }

        private bool QueueBluetoothAudioPacerMicrophoneTransitionFromCache(
            bool enabled)
        {
            lock (bluetoothCombinedTransportWriteLock)
            {
                byte[] template = bluetoothCombinedSpeakerWorkingReport;
                long hapticsExpiryQpc;
                lock (bluetoothCombinedSpeakerReportLock)
                {
                    if (!bluetoothCombinedSpeakerReportAvailable)
                    {
                        return false;
                    }

                    Array.Copy(latestBluetoothCombinedSpeakerReport, template,
                        template.Length);
                    hapticsExpiryQpc = PersistentBluetoothHapticsExpiryQpc;
                }

                ApplyBluetoothSpeakerVolumeAndRoutingCore(template,
                    speakerVolume, headsetOnlyAudio, headphoneVolume);
                // The requested flag was published before entering this path.
                // Keep the live template synchronized with the exact 0x32
                // transition that the isolated writer will serialize.
                ApplyBluetoothMicrophoneStreamingRequest(template);
                lock (bluetoothAudioPacerLock)
                {
                    return bluetoothAudioPacer?.IsRunning == true &&
                        bluetoothAudioPacer.UpdateMicrophoneTransition(template,
                            hapticsExpiryQpc, enabled);
                }
            }
        }

        private bool TryPublishCachedBluetoothCombinedState(
            bool includeNativeHaptics, string activeStatus,
            string idleReportDescription, out bool deferredToSpeakerClock)
        {
            lock (bluetoothCombinedTransportWriteLock)
            {
                // The active/idle decision and its matching publication are a
                // single boundary with Clear. Clear cannot invalidate the
                // clock between this check and an UpdateTemplate, leaving an
                // idle helper with state that is never physically presented.
                if (enableSpeakerOutput && IsBluetoothSpeakerClockActive())
                {
                    deferredToSpeakerClock = true;
                    bool refreshed =
                        RefreshBluetoothAudioPacerTemplateFromCache();
                    LastBluetoothHapticsWriteStatus = refreshed ? activeStatus :
                        $"Could not publish {idleReportDescription} to the active Bluetooth speaker clock.";
                    return refreshed;
                }

                deferredToSpeakerClock = false;
                return TryWriteCachedBluetoothCombinedControlReport(
                    includeNativeHaptics, idleReportDescription);
            }
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

        private bool TryTakeBluetoothSpeakerAudioFrame(byte[] destination, int destinationOffset)
        {
            lock (bluetoothSpeakerFrameLock)
            {
                if (!enableSpeakerOutput || !bluetoothSpeakerFramePending ||
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

        public void SetProfileMicrophoneMuteState(bool enabled, bool muted)
        {
            int state = enabled ? (muted ? 2 : 1) : 0;
            if (Interlocked.Exchange(ref profileMicrophoneMuteState, state) == state)
            {
                return;
            }

            queueEvent(() =>
            {
                microphoneMuteOverride = enabled;
                microphoneMuted = enabled && muted;
                outputDirty = true;
            });
        }

        public void SetMicrophoneMuteState(bool muted)
        {
            SetProfileMicrophoneMuteState(true, muted);
        }

        public void SetProfileMuteLedState(bool enabled, bool ledOn)
        {
            queueEvent(() =>
            {
                muteLedOverride = enabled;
                muteLedOn = ledOn;
                outputDirty = true;
            });
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
                    useAccurateRumble = false;
                }
            }

            if (conType == ConnectionType.BT)
            {
                // The PadSense-derived helper owns the physical Bluetooth HID
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
                Queue<double> latencyQueue = new Queue<double>(21); // Set capacity at max + 1 to avoid any resizing
                int tempLatencyCount = 0;
                long oldtime = 0;
                string currerror = string.Empty;
                long curtime = 0;
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

                    if (tempLatencyCount >= 20)
                    {
                        latencySum -= latencyQueue.Dequeue();
                        tempLatencyCount--;
                    }

                    // Preserve the fractional part of the observed HID input
                    // interval. Truncating every sample to an integer made a
                    // 1.4 ms cadence look like 1.0 ms (1000 Hz) in the UI.
                    latencySum += lastTimeElapsedDouble;
                    latencyQueue.Enqueue(lastTimeElapsedDouble);
                    tempLatencyCount++;

                    //Latency = latencyQueue.Average();
                    Latency = latencySum / (double)tempLatencyCount;

                    readWaitEv.Set();

                    if (conType == ConnectionType.BT)
                    {
                        timeoutEvent = false;
                        HidDevice.ReadStatus res = hDevice.ReadFile(inputReport);
                        if (res == HidDevice.ReadStatus.Success)
                        {
                            if (IsBluetoothMicrophoneFrame(inputReport))
                            {
                                SignalBluetoothAudioPacerMicrophoneFrame(
                                    inputReport[2]);
                                inputReportErrorCount = 0;
                                RecordBluetoothMicrophoneFrame(inputReport);
                                // PadSense treats 0x31 microphone packets as a
                                // media-only input lane. Do not let their 100 Hz
                                // cadence pump pending state or publish a
                                // competing output report; continuous 0x36
                                // media carries the latest controller state.
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
                                if (outputDirty)
                                {
                                    PrepareOutReport();
                                    FlushPreparedOutputReport();
                                }
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

                                    AppLogger.LogToGui(DS4WinWPF.Translations.Strings.CRC32Fail, true);
                                    readWaitEv.Reset();
                                    //sendOutputReport(true, true); // Kick Windows into noticing the disconnection.
                                    StopOutputUpdate();
                                    isDisconnecting = true;
                                    RunRemoval();

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
                            if (res == HidDevice.ReadStatus.WaitTimedOut)
                            {
                                AppLogger.LogToGui(Mac.ToString() + " disconnected due to timeout", true);
                            }
                            else
                            {
                                int winError = Marshal.GetLastWin32Error();
                                Console.WriteLine($"{Mac} {DateTime.UtcNow.ToString("o")} > disconnect due to read failure: {winError.ToString("x8")}");
                                //Log.LogToGui(Mac.ToString() + " disconnected due to read failure: " + winError, true);
                                AppLogger.LogToGui(Mac.ToString() + " disconnected due to read failure: " + winError, true);
                            }

                            exitInputThread = true;
                            readWaitEv.Reset();
                            //SendEmptyOutputReport();
                            //sendOutputReport(true, true); // Kick Windows into noticing the disconnection.
                            StopOutputUpdate();
                            isDisconnecting = true;
                            RunRemoval();

                            timeoutExecuted = true;
                            continue;
                        }
                    }
                    else
                    {
                        HidDevice.ReadStatus res = hDevice.ReadFile(inputReport);
                        if (res != HidDevice.ReadStatus.Success)
                        {
                            if (res == HidDevice.ReadStatus.WaitTimedOut)
                            {
                                AppLogger.LogToGui(Mac.ToString() + " disconnected due to timeout", true);
                            }
                            else
                            {
                                int winError = Marshal.GetLastWin32Error();
                                Console.WriteLine($"{Mac} {DateTime.UtcNow.ToString("o")} > disconnect due to read failure: {winError.ToString("x8")}");
                                //Log.LogToGui(Mac.ToString() + " disconnected due to read failure: " + winError, true);
                            }

                            exitInputThread = true;
                            readWaitEv.Reset();
                            StopOutputUpdate();
                            isDisconnecting = true;
                            RunRemoval();

                            timeoutExecuted = true;
                            continue;
                        }
                    }

                    readWaitEv.Wait();
                    readWaitEv.Reset();

                    curtime = Stopwatch.GetTimestamp();
                    testelapsed = curtime - oldtime;
                    lastTimeElapsedDouble = testelapsed * (1.0 / Stopwatch.Frequency) * 1000.0;
                    lastTimeElapsed = (long)lastTimeElapsedDouble;
                    oldtime = curtime;

                    if (conType == ConnectionType.BT && inputReport[0] != 0x31)
                    {
                        // Received incorrect report, skip it
                        continue;
                    }

                    utcNow = DateTime.UtcNow; // timestamp with UTC in case system time zone changes

                    cState.PacketCounter = pState.PacketCounter + 1;
                    cState.ReportTimeStamp = utcNow;
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
                            ChargingChanged?.Invoke(this, EventArgs.Empty);
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
                            BatteryChanged?.Invoke(this, EventArgs.Empty);
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
                        long previousMediaBufferPublish = Volatile.Read(
                            ref bluetoothLastMediaBufferPublishQpc);
                        bool mediaBufferPublishDue = inputArrivalQpc -
                            previousMediaBufferPublish >= Stopwatch.Frequency *
                                BluetoothMediaBufferPublishMilliseconds / 1000;
                        if (mediaBufferPublishDue &&
                            Interlocked.CompareExchange(
                                ref bluetoothLastMediaBufferPublishQpc,
                                inputArrivalQpc,
                                previousMediaBufferPublish) ==
                                    previousMediaBufferPublish)
                        {
                            // CRC validation and the normal-frame discriminator
                            // make the vendor-tail byte stable enough for trace
                            // correlation. Its semantics are undocumented, so
                            // it remains diagnostic and cannot steer cadence.
                            TryUpdateBluetoothAudioPacerMediaBuffer(
                                inputReport[65], inputArrivalQpc);
                        }
                        bool clockRatioUpdated =
                            bluetoothControllerClock.Observe(tempStamp,
                                inputArrivalQpc);
                        long previousPhasePublish = Volatile.Read(
                            ref bluetoothLastInputPhasePublishQpc);
                        bool phasePublishDue = inputArrivalQpc -
                            previousPhasePublish >= Stopwatch.Frequency *
                                BluetoothInputPhasePublishMilliseconds / 1000;
                        if ((clockRatioUpdated || phasePublishDue) &&
                            Interlocked.CompareExchange(
                                ref bluetoothLastInputPhasePublishQpc,
                                inputArrivalQpc, previousPhasePublish) ==
                                    previousPhasePublish)
                        {
                            TryUpdateBluetoothAudioPacerCadenceRatio(
                                DualSenseControllerClockStable ?
                                    bluetoothControllerClock.Ratio : 1.0);
                        }
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
                            AppLogger.LogToGui(Mac.ToString() + " disconnecting due to idle disconnect", false);

                            if (conType == ConnectionType.BT)
                            {
                                if (DisconnectBT(true))
                                {
                                    exitInputThread = true;
                                    timeoutExecuted = true;
                                    return; // all done
                                }
                            }
                        }
                    }

                    if (fireReport)
                    {
                        Report?.Invoke(this, EventArgs.Empty);
                    }

                    PrepareOutReport();
                    FlushPreparedOutputReport();
                    //forceWrite = false;

                    if (!string.IsNullOrEmpty(currerror))
                        error = currerror;
                    else if (!string.IsNullOrEmpty(error))
                        error = string.Empty;

                    cState.CopyTo(pState);

                    DrainQueuedInputEvents();
                }
            }

            timeoutExecuted = true;
        }

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

        private void RecordBluetoothMicrophoneFrame(byte[] report)
        {
            if (report == null ||
                report.Length < BluetoothMicrophonePayloadOffset +
                    BluetoothMicrophonePayloadLength)
            {
                return;
            }

            lock (bluetoothCombinedTransportWriteLock)
            {
                if (Volatile.Read(ref bluetoothMicrophoneStreamingRequested) == 0)
                {
                    return;
                }

                Interlocked.Exchange(ref bluetoothMicrophoneLastFrameTimestamp,
                    Stopwatch.GetTimestamp());
                Interlocked.Increment(ref bluetoothMicrophoneFramesReceived);
                // An inbound microphone packet is physical, completion-level
                // proof that the controller consumed an enable request. Keep
                // this check and commit serialized with SetBluetoothMicrophone-
                // Streaming so an old frame cannot clear a newer disable.
                Interlocked.Exchange(
                    ref bluetoothMicrophoneControlUpdatePending, 0);
            }

            byte[] payload = new byte[BluetoothMicrophonePayloadLength];
            Array.Copy(report, BluetoothMicrophonePayloadOffset, payload, 0,
                payload.Length);
            try
            {
                BluetoothMicrophoneOpusFrameReceived?.Invoke(this, payload);
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

        protected override void StopOutputUpdate()
        {
            // Publish the gate before waiting for transport ownership. A
            // speaker callback that was already dispatched must either finish
            // before this lock is acquired or observe the gate and abort; it
            // can never recreate the helper between Stop and the final control
            // commit.
            Interlocked.Exchange(ref bluetoothOutputTransportStopping, 1);
            lock (bluetoothCombinedTransportWriteLock)
            {
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
            MergeStates();

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
                outputReport[1] = (byte)((useRumble ? 0x0F : 0x0C) | 0x10 | 0x40 |
                    (enableSpeakerOutput ? DualSenseOutputFlag0AudioControlEnable |
                        (headsetOnlyAudio ?
                            DualSenseOutputFlag0HeadphoneVolumeEnable :
                            DualSenseOutputFlag0SpeakerVolumeEnable) : 0x00));

                // 0x01 Toggling microphone LED, 0x02 Toggling Audio/Mic Mute
                // 0x04 Toggling LED strips on the sides of the Touchpad, 0x08 Turn off all LED lights
                // 0x10 Toggle player LED lights below Touchpad, 0x20 ???
                // 0x40 Adjust overall motor/effect power, 0x80 ???
                outputReport[2] = (byte)(0x55 |
                    (enableSpeakerOutput && !headsetOnlyAudio ?
                        DualSenseOutputFlag1AudioControl2Enable : 0x00) |
                    (muteLedOverride || microphoneMuteOverride ? 0x01 : 0x00) |
                    (microphoneMuteOverride ? 0x02 : 0x00));

                if (useRumble || useAccurateRumble)
                {
                    // Right? High Freq Motor
                    outputReport[3] = currentHap.rumbleState.RumbleMotorStrengthRightLightFast;
                    // Left? Low Freq Motor
                    outputReport[4] = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow;
                }

                // Headphone volume
                // RC3's internal-speaker state carried the profile byte here.
                // Only the isolated AUX route requires Sony's 0x00-0x7F map.
                outputReport[5] = headsetOnlyAudio ?
                    MapDualSenseHeadphoneVolume(headphoneVolume) :
                    (byte)0; // Left and Right; speaker mode keeps AUX muted.
                // Internal speaker volume
                outputReport[6] = headsetOnlyAudio ? (byte)0 :
                    MapDualSenseSpeakerVolume(speakerVolume);
                // Internal microphone volume
                outputReport[7] = MapDualSenseMicrophoneVolume(
                    microphoneVolume);
                // Route the Opus stream to either the controller speaker or
                // the 3.5 mm headset DAC. This byte is an output-path field,
                // not merely an internal-speaker enable bit.
                outputReport[8] = enableSpeakerOutput ?
                    (headsetOnlyAudio ? DualSenseAudioControlOutputHeadphones :
                        DualSenseAudioControlOutputSpeaker) : (byte)0x00;

                // Mute button LED. 0x01 = Solid. 0x02 = Pulsating
                outputReport[9] = muteLedOverride ? (muteLedOn ? (byte)0x01 : (byte)0x00) :
                    microphoneMuteOverride ? (microphoneMuted ? (byte)0x01 : (byte)0x00) : muteLEDByte;

                // audio settings requiring mute toggling flags
                outputReport[10] = microphoneMuteOverride && microphoneMuted ? (byte)0x10 : (byte)0x00; // 0x10 microphone mute, 0x40 audio mute

                /* TRIGGER MOTORS  */
                // R2 Effects
                outputReport[11] = r2EffectData.triggerMotorMode; // right trigger motor mode (0 = no resistance, 1 = continuous resistance, 2 = section resistance, 0x20 and 0x04 enable additional effects together with 1 and 2 (configuration yet unknown), 252 = likely a calibration program* / PS Remote Play defaults this to 5; bit 4 only disables the motor?)
                outputReport[12] = r2EffectData.triggerStartResistance; // right trigger start of resistance section 0-255 (0 = released state; 0xb0 roughly matches trigger value 0xff); in mode 26 this field has something to do with motor re-extension after a press-release-cycle (0 = no re-extension)
                outputReport[13] = r2EffectData.triggerEffectForce; // right trigger
                                         // (mode1) amount of force exerted; 0-255
                                         // (mode2) end of resistance section (>= begin of resistance section is enforced); 0xff makes it behave like mode1
                                         // (supplemental mode 4+20) flag(s?) 0x02 = do not pause effect when fully pressed
                outputReport[14] = r2EffectData.triggerRangeForce; // right trigger force exerted in range (mode2), 0-255
                outputReport[15] = r2EffectData.triggerNearReleaseStrength; // strength of effect near release state (requires supplement modes 4 and 20)
                outputReport[16] = r2EffectData.triggerNearMiddleStrength; // strength of effect near middle (requires supplement modes 4 and 20)
                outputReport[17] = r2EffectData.triggerPressedStrength; // strength of effect at pressed state (requires supplement modes 4 and 20)
                outputReport[20] = r2EffectData.triggerActuationFrequency; // effect actuation frequency in Hz (requires supplement modes 4 and 20)


                // L2 Effects
                outputReport[22] = l2EffectData.triggerMotorMode; // left trigger motor mode (0 = no resistance, 1 = continuous resistance, 2 = section resistance, 0x20 and 0x04 enable additional effects together with 1 and 2 (configuration yet unknown), 252 = likely a calibration program* / PS Remote Play defaults this to 5; bit 4 only disables the motor?)
                outputReport[23] = l2EffectData.triggerStartResistance; // left trigger start of resistance section 0-255 (0 = released state; 0xb0 roughly matches trigger value 0xff); in mode 26 this field has something to do with motor re-extension after a press-release-cycle (0 = no re-extension)
                outputReport[24] = l2EffectData.triggerEffectForce; // left trigger
                                         // (mode1) amount of force exerted; 0-255
                                         // (mode2) end of resistance section (>= begin of resistance section is enforced); 0xff makes it behave like mode1
                                         // (supplemental mode 4+20) flag(s?) 0x02 = do not pause effect when fully pressed
                outputReport[25] = l2EffectData.triggerRangeForce; // left trigger: (mode2) amount of force exerted within range; 0-255
                outputReport[26] = l2EffectData.triggerNearReleaseStrength; // strength of effect near release state (requires supplement modes 4 and 20)
                outputReport[27] = l2EffectData.triggerNearMiddleStrength; // strength of effect near middle (requires supplement modes 4 and 20)
                outputReport[28] = l2EffectData.triggerPressedStrength; // strength of effect at pressed state (requires supplement modes 4 and 20)
                outputReport[31] = l2EffectData.triggerActuationFrequency; // effect actuation frequency in Hz (requires supplement modes 4 and 20)

                // (lower nibble: main motor; upper nibble trigger effects) 0x00 to 0x07 - reduce overall power of the respective motors/effects by 12.5% per increment (this does not affect the regular trigger motor settings, just the automatically repeating trigger effects)
                outputReport[37] = hapticPowerLevel;
                // Volume of internal speaker (0-7; ties in with index 6. The PS5 default appears to be set a 4)
                outputReport[38] = enableSpeakerOutput && !headsetOnlyAudio ?
                    DualSenseSpeakerPreGain : (byte)0x00;

                /* Player LED section (and improved rumble flag) */
                // 0x01 Enabled LED brightness (value in index 43)
                // 0x02 Uninterruptable blue LED pulse (action in index 42)
                // 0x04 Enable improved rumble emulation (Requires 2.24 firmware or newer)
                outputReport[39] = useAccurateRumble ? (byte)0x06 : (byte)0x02;

                // 0x01 Slowly (2s?) fade to blue (scheduled to when the regular LED settings are active)
                // 0x02 Slowly (2s?) fade out (scheduled after fade-in completion) with eventual switch back to configured LED color; only a fade-out can cancel the pulse (neither index 2, 0x08, nor turning this off will cancel it!)
                outputReport[42] = 0x02;
                // 0x00 High Brightness, 0x01 Medium Brightness, 0x02 Low Brightness
                outputReport[43] = 0x02;
                // 5 player LED lights below Touchpad.
                // Bitmask 0x00-0x1F from left to right with 0x04 being the center LED. Bit 0x20 sets the brightness immediately with no fade in
                outputReport[44] = activePlayerLEDMask;

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
                else if (rumbleSet && standbySw.ElapsedMilliseconds >= 4000L)
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
                outputReport[2] = (byte)((useRumble ? 0x0F : 0x0C) | 0x10 | 0x40 |
                    (enableSpeakerOutput ? DualSenseOutputFlag0AudioControlEnable |
                        (headsetOnlyAudio ?
                            DualSenseOutputFlag0HeadphoneVolumeEnable :
                            DualSenseOutputFlag0SpeakerVolumeEnable) : 0x00));

                // 0x01 Toggling microphone LED, 0x02 Toggling Audio/Mic Mute
                // 0x04 Toggling LED strips on the sides of the Touchpad, 0x08 Turn off all LED lights
                // 0x10 Toggle player LED lights below Touchpad, 0x20 ???
                // 0x40 Adjust overall motor/effect power, 0x80 ???
                outputReport[3] = (byte)(0x55 |
                    (enableSpeakerOutput && !headsetOnlyAudio ?
                        DualSenseOutputFlag1AudioControl2Enable : 0x00) |
                    (muteLedOverride || microphoneMuteOverride ? 0x01 : 0x00) |
                    (microphoneMuteOverride ? 0x02 : 0x00));

                if (useRumble || useAccurateRumble)
                {
                    // Right? High Freq Motor
                    outputReport[4] = currentHap.rumbleState.RumbleMotorStrengthRightLightFast;
                    // Left? Low Freq Motor
                    outputReport[5] = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow;
                }

                // Headphone volume
                // Keep the proven RC3 speaker report unchanged. The 0x96 AUX
                // path alone uses the controller's 0x00-0x7F gain range.
                outputReport[6] = headsetOnlyAudio ?
                    MapDualSenseHeadphoneVolume(headphoneVolume) :
                    (byte)0; // Left and Right; speaker mode keeps AUX muted.
                // Internal speaker volume
                outputReport[7] = headsetOnlyAudio ? (byte)0 :
                    MapDualSenseSpeakerVolume(speakerVolume);
                // Internal microphone volume
                outputReport[8] = MapDualSenseMicrophoneVolume(
                    microphoneVolume);
                // Select the physical speaker or AUX/headset DAC.
                outputReport[9] = enableSpeakerOutput ?
                    (headsetOnlyAudio ? DualSenseAudioControlOutputHeadphones :
                        DualSenseAudioControlOutputSpeaker) : (byte)0x00;

                // Mute button LED. 0x01 = Solid. 0x02 = Pulsating
                outputReport[10] = muteLedOverride ? (muteLedOn ? (byte)0x01 : (byte)0x00) :
                    microphoneMuteOverride ? (microphoneMuted ? (byte)0x01 : (byte)0x00) : muteLEDByte;

                // audio settings requiring mute toggling flags
                outputReport[11] = microphoneMuteOverride && microphoneMuted ? (byte)0x10 : (byte)0x00; // 0x10 microphone mute, 0x40 audio mute

                /* TRIGGER MOTORS  */
                // R2 Effects
                outputReport[12] = r2EffectData.triggerMotorMode; // right trigger motor mode (0 = no resistance, 1 = continuous resistance, 2 = section resistance, 0x20 and 0x04 enable additional effects together with 1 and 2 (configuration yet unknown), 252 = likely a calibration program* / PS Remote Play defaults this to 5; bit 4 only disables the motor?)
                outputReport[13] = r2EffectData.triggerStartResistance; // right trigger start of resistance section 0-255 (0 = released state; 0xb0 roughly matches trigger value 0xff); in mode 26 this field has something to do with motor re-extension after a press-release-cycle (0 = no re-extension)
                outputReport[14] = r2EffectData.triggerEffectForce; // right trigger
                                                                    // (mode1) amount of force exerted; 0-255
                                                                    // (mode2) end of resistance section (>= begin of resistance section is enforced); 0xff makes it behave like mode1
                                                                    // (supplemental mode 4+20) flag(s?) 0x02 = do not pause effect when fully pressed
                outputReport[15] = r2EffectData.triggerRangeForce; // right trigger force exerted in range (mode2), 0-255
                outputReport[16] = r2EffectData.triggerNearReleaseStrength; // strength of effect near release state (requires supplement modes 4 and 20)
                outputReport[17] = r2EffectData.triggerNearMiddleStrength; // strength of effect near middle (requires supplement modes 4 and 20)
                outputReport[18] = r2EffectData.triggerPressedStrength; // strength of effect at pressed state (requires supplement modes 4 and 20)
                outputReport[21] = r2EffectData.triggerActuationFrequency; // effect actuation frequency in Hz (requires supplement modes 4 and 20)


                // L2 Effects
                outputReport[23] = l2EffectData.triggerMotorMode; // left trigger motor mode (0 = no resistance, 1 = continuous resistance, 2 = section resistance, 0x20 and 0x04 enable additional effects together with 1 and 2 (configuration yet unknown), 252 = likely a calibration program* / PS Remote Play defaults this to 5; bit 4 only disables the motor?)
                outputReport[24] = l2EffectData.triggerStartResistance; // left trigger start of resistance section 0-255 (0 = released state; 0xb0 roughly matches trigger value 0xff); in mode 26 this field has something to do with motor re-extension after a press-release-cycle (0 = no re-extension)
                outputReport[25] = l2EffectData.triggerEffectForce; // left trigger
                                                                    // (mode1) amount of force exerted; 0-255
                                                                    // (mode2) end of resistance section (>= begin of resistance section is enforced); 0xff makes it behave like mode1
                                                                    // (supplemental mode 4+20) flag(s?) 0x02 = do not pause effect when fully pressed
                outputReport[26] = l2EffectData.triggerRangeForce; // left trigger: (mode2) amount of force exerted within range; 0-255
                outputReport[27] = l2EffectData.triggerNearReleaseStrength; // strength of effect near release state (requires supplement modes 4 and 20)
                outputReport[28] = l2EffectData.triggerNearMiddleStrength; // strength of effect near middle (requires supplement modes 4 and 20)
                outputReport[29] = l2EffectData.triggerPressedStrength; // strength of effect at pressed state (requires supplement modes 4 and 20)
                outputReport[32] = l2EffectData.triggerActuationFrequency; // effect actuation frequency in Hz (requires supplement modes 4 and 20)

                // (lower nibble: main motor; upper nibble trigger effects) 0x00 to 0x07 - reduce overall power of the respective motors/effects by 12.5% per increment (this does not affect the regular trigger motor settings, just the automatically repeating trigger effects)
                outputReport[38] = hapticPowerLevel;
                // Volume of internal speaker (0-7; ties in with index 6. The PS5 default appears to be set a 4)
                outputReport[39] = enableSpeakerOutput && !headsetOnlyAudio ?
                    DualSenseSpeakerPreGain : (byte)0x00;

                /* Player LED section (and improved rumble  flag) */
                // 0x01 Enabled LED brightness (value in index 43)
                // 0x02 Uninterruptable blue LED pulse (action in index 42)
                // 0x04 Enable improved rumble emulation (Requires 2.24 firmware or newer)
                outputReport[40] = useAccurateRumble ? (byte)0x06 : (byte)0x02; 

                // 0x01 Slowly (2s?) fade to blue (scheduled to when the regular LED settings are active)
                // 0x02 Slowly (2s?) fade out (scheduled after fade-in completion) with eventual switch back to configured LED color; only a fade-out can cancel the pulse (neither index 2, 0x08, nor turning this off will cancel it!)
                outputReport[43] = 0x02;
                // 0x00 High Brightness, 0x01 Medium Brightness, 0x02 Low Brightness
                outputReport[44] = 0x02;
                // 5 player LED lights below Touchpad.
                // Bitmask 0x00-0x1F from left to right with 0x04 being the center LED. Bit 0x20 sets the brightness immediately with no fade in
                outputReport[45] = activePlayerLEDMask;

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

            queueEvent(() =>
            {
                if (conType == ConnectionType.BT)
                {
                    bool published =
                        EnsureBluetoothCombinedOutputTransport() &&
                        UpdateCachedBluetoothCombinedState(report, offset) &&
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

                Array.Copy(report, offset, outputReport, 0,
                    USB_OUTPUT_CHANGE_LENGTH);
                WriteReport();
            });

            return true;
        }

        public bool WriteBluetoothHapticsOutputReport(byte[] report, int offset, int length, bool waitForWrite = false)
        {
            if (report == null || offset < 0 || length != 141 ||
                offset + length > report.Length || report[offset] != 0x32 ||
                report[offset + 11] != 0x92 ||
                report[offset + 12] != BluetoothCombinedHapticsDataLength)
            {
                LastBluetoothHapticsWriteStatus =
                    "Rejected: invalid legacy Bluetooth haptics report.";
                return false;
            }

            return WriteBluetoothHapticsSamples(report, offset + 13,
                BluetoothCombinedHapticsDataLength, waitForWrite);
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
                out bool deferredToSpeakerClock);
            if (written && !deferredToSpeakerClock)
            {
                MarkBluetoothCombinedHapticsSubmitted(hapticsGeneration);
            }

            return written;
        }

        /// <summary>
        /// Compatibility entry point for callers that still package the old
        /// 0x35 speaker lane. The Opus payload is extracted and submitted through
        /// the unified 0x36 transport; report 0x35 is never written to hardware.
        /// </summary>
        public bool WriteBluetoothSpeakerAudioOutputReport(byte[] report, int offset, int length)
        {
            if (report == null || offset < 0 || length != 334 ||
                offset + length > report.Length || report[offset] != 0x35 ||
                report[offset + 11] != 0x93 ||
                report[offset + 12] != BluetoothCombinedSpeakerFrameLength)
            {
                LastBluetoothHapticsWriteStatus =
                    "Rejected: invalid legacy Bluetooth speaker report.";
                return false;
            }

            byte[] frame = new byte[BluetoothCombinedSpeakerFrameLength];
            Array.Copy(report, offset + 13, frame, 0, frame.Length);
            return SetBluetoothSpeakerAudioFrame(frame, frame.Length);
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

            long hapticsGeneration = CacheBluetoothCombinedSpeakerReport(report,
                offset, hasNativeGameState);

            bool written = TryPublishCachedBluetoothCombinedState(
                includeNativeHaptics: true,
                activeStatus:
                    "Cached native Bluetooth haptics for the next speaker-clocked frame.",
                idleReportDescription: "combined haptics/audio",
                out bool deferredToSpeakerClock);
            if (written && !deferredToSpeakerClock)
            {
                MarkBluetoothCombinedHapticsSubmitted(hapticsGeneration);
            }

            return written;
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
                    MergeProfileStateIntoPadSenseAudioSnapshot(report,
                        offset + BluetoothCombinedStateOffset,
                        latestBluetoothCombinedSpeakerReport,
                        BluetoothCombinedStateOffset);
                    latestBluetoothCombinedNativeStateTimestamp =
                        Stopwatch.GetTimestamp();
                }
                else if (outputReport != null &&
                    outputReport.Length >= 2 +
                        BluetoothCombinedNativeStateLength &&
                    outputReport[0] == OUTPUT_REPORT_ID_BT)
                {
                    MergeProfileStateIntoPadSenseAudioSnapshot(outputReport,
                        2, latestBluetoothCombinedSpeakerReport,
                        BluetoothCombinedStateOffset);
                    latestBluetoothCombinedNativeStateTimestamp = 0;
                }
                else
                {
                    int lightbarOffset = BluetoothCombinedStateOffset + 44;
                    latestBluetoothCombinedSpeakerReport[lightbarOffset] =
                        currentHap.lightbarState.LightBarColor.red;
                    latestBluetoothCombinedSpeakerReport[lightbarOffset + 1] =
                        currentHap.lightbarState.LightBarColor.green;
                    latestBluetoothCombinedSpeakerReport[lightbarOffset + 2] =
                        currentHap.lightbarState.LightBarColor.blue;
                    latestBluetoothCombinedNativeStateTimestamp = 0;
                }
                latestBluetoothCombinedSpeakerReport[BluetoothCombinedHapticsOffset] =
                    0x92;
                latestBluetoothCombinedSpeakerReport[
                    BluetoothCombinedHapticsOffset + 1] =
                    BluetoothCombinedHapticsDataLength;
                long now = Stopwatch.GetTimestamp();
                // Audio-only sidecars can carry DS4Windows audio haptics even
                // without a native virtual-DualSense game-state report. Treat
                // the 0x92 lane as authoritative independently of state origin.
                Array.Copy(report,
                    offset + BluetoothCombinedHapticsDataOffset,
                    latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedHapticsDataOffset,
                    BluetoothCombinedHapticsDataLength);
                latestBluetoothCombinedSpeakerReportTimestamp = now;
                bluetoothCombinedHapticsGeneration++;

                bluetoothCombinedSpeakerReportAvailable = true;
                hapticsGeneration = bluetoothCombinedHapticsGeneration;
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

            lock (bluetoothCombinedSpeakerReportLock)
            {
                if (!bluetoothCombinedSpeakerReportAvailable)
                {
                    return false;
                }

                MergeProfileStateIntoPadSenseAudioSnapshot(report,
                    offset + 1, latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedStateOffset);
                latestBluetoothCombinedNativeStateTimestamp =
                    Stopwatch.GetTimestamp();
                return true;
            }
        }

        private void FlushPreparedOutputReport()
        {
            if (outputDirty)
            {
                bool published = true;
                // Every Bluetooth state uses the long-lived PadSense helper,
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
                    WriteReport();
                }

                if (!published)
                {
                    // Keep dirty state pending so a transient helper queue/fault
                    // cannot turn the latest light/rumble state into a silent
                    // permanent loss.
                    RequestUnifiedBluetoothOutputTransportRecovery();
                    return;
                }

                previousHapticState = currentHap;
            }

            outputDirty = false;
            currentHap.dirty = false;
        }

        private void DrainQueuedInputEvents()
        {
            if (!hasInputEvts)
            {
                return;
            }

            lock (eventQueueLock)
            {
                for (int index = 0, count = eventQueue.Count;
                    index < count; index++)
                {
                    eventQueue.Dequeue().Invoke();
                }

                hasInputEvts = false;
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

                long nativeStateTimestamp =
                    latestBluetoothCombinedNativeStateTimestamp;
                if (nativeStateTimestamp > 0 &&
                    Stopwatch.GetTimestamp() - nativeStateTimestamp <=
                        (Stopwatch.Frequency *
                            BluetoothCombinedNativeStateFreshnessMilliseconds) /
                        1000)
                {
                    // A virtual DualSense 0x36 contains the authoritative game
                    // trigger/light/rumble state. Do not overwrite it with the
                    // generic profile snapshot produced by PrepareOutReport.
                    return true;
                }

                MergeProfileStateIntoPadSenseAudioSnapshot(report, 2,
                    latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedStateOffset);
                return true;
            }
        }

        private static void MergeProfileStateIntoPadSenseAudioSnapshot(
            byte[] source, int sourceOffset, byte[] destination,
            int destinationOffset)
        {
            // PadSense keeps the native audio contract stable on every media
            // frame and overlays only mutable controller state. In particular,
            // generic DS4Windows output reports must not replace FD/F7, the
            // three audio gains, route 0x09, or audio-control2 0x0A with their
            // Bluetooth control-report equivalents.
            Array.Copy(source, sourceOffset + 2, destination,
                destinationOffset + 2, 2);       // rumble motors
            // PadSense keeps UseRumbleNotHaptics clear on an idle carrier.
            // A virtual report may leave its validity bit asserted while both
            // motors are zero; forwarding that bit alone switches firmware
            // out of the native tactile/audio mode on every media report.
            destination[destinationOffset] &= 0xFD;
            if (source[sourceOffset + 2] != 0 ||
                source[sourceOffset + 3] != 0)
            {
                destination[destinationOffset] |= 0x02;
            }
            // PadSense keeps mute LED and power-save/mute state in its fixed
            // media snapshot. Microphone transitions travel through the
            // ordered 0x32 control report instead of mutating each 0x36.
            Array.Copy(source, sourceOffset + 10, destination,
                destinationOffset + 10, 27);     // triggers and effect power
            Array.Copy(source, sourceOffset + 43, destination,
                destinationOffset + 43, 4);      // player LEDs and lightbar
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
            if (!allowDuringStopping &&
                Volatile.Read(ref bluetoothOutputTransportStopping) != 0)
            {
                LastBluetoothHapticsWriteStatus =
                    $"Rejected {reportDescription}: Bluetooth output is stopping.";
                return false;
            }

            lock (bluetoothCombinedTransportWriteLock)
            {
                if (!allowDuringStopping &&
                    Volatile.Read(ref bluetoothOutputTransportStopping) != 0)
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

                if (!EnsureBluetoothCombinedOutputTransport())
                {
                    return false;
                }

                bool pacerOwnedTransport =
                    BluetoothAudioPacerOwnsTransport();
                bool speakerClockActive =
                    IsBluetoothSpeakerClockActive();
                if (!pacerOwnedTransport)
                {
                    LastBluetoothHapticsWriteStatus =
                        $"Deferred {reportDescription}: unified Bluetooth helper is unavailable.";
                    RequestUnifiedBluetoothOutputTransportRecovery();
                    return false;
                }

                waitForCompletion =
                    RequiresCompletionAwareBluetoothControlWrite(
                        waitForCompletion, speakerClockActive,
                        pacerOwnedTransport);
                bool commitThroughPacer =
                    (waitForCompletion || !speakerClockActive);

                byte[] combined = bluetoothCombinedSpeakerWorkingReport;
                long hapticsGeneration;
                lock (bluetoothCombinedSpeakerReportLock)
                {
                    if (!bluetoothCombinedSpeakerReportAvailable)
                    {
                        return false;
                    }

                    Array.Copy(latestBluetoothCombinedSpeakerReport, combined,
                        combined.Length);
                    hapticsGeneration = bluetoothCombinedHapticsGeneration;
                }

                bool includeHaptics = includeNativeHaptics;
                combined[BluetoothCombinedHapticsOffset] = 0x92;
                combined[BluetoothCombinedHapticsOffset + 1] =
                    BluetoothCombinedHapticsDataLength;
                if (!includeHaptics)
                {
                    Array.Clear(combined, BluetoothCombinedHapticsDataOffset,
                        BluetoothCombinedHapticsDataLength);
                }

                for (int index = 5; index <= 9; index++)
                {
                    combined[index] = BluetoothCombinedLowLatencyBufferLength;
                }

                // The working mic/control keepalive deliberately omits packet
                // 0x13 entirely. An empty 0x93 TLV can make some firmware emit
                // an audible alert tone.
                Array.Clear(combined, BluetoothCombinedSpeakerOffset,
                    BluetoothCombinedOutputReportLength - sizeof(uint) -
                    BluetoothCombinedSpeakerOffset);
                if (enableSpeakerOutput)
                {
                    ApplyBluetoothSpeakerVolumeAndRoutingCore(combined,
                        speakerVolume, headsetOnlyAudio, headphoneVolume);
                }

                ApplyBluetoothMicrophoneStreamingRequest(combined);
                // A control-only report participates in Sony's shared output
                // report sequence but carries no media frame. DS5Dongle leaves
                // the audio packet counter untouched on this path.
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
                    advancesMediaPacketSequence: false);
                ApplyBluetoothCombinedCrc(combined);

                bool written;
                if (commitThroughPacer)
                {
                    long hapticsExpiryQpc = includeHaptics ?
                        PersistentBluetoothHapticsExpiryQpc : 0;
                    written = TryCommitBluetoothControlThroughAudioPacer(
                        combined, hapticsExpiryQpc, waitForCompletion,
                        out _);
                }
                else
                {
                    long hapticsExpiryQpc = includeHaptics ?
                        PersistentBluetoothHapticsExpiryQpc : 0;
                    written = TryUpdateBluetoothAudioPacerTemplate(combined,
                        hapticsExpiryQpc, out bool helperOwnsTransport) &&
                        helperOwnsTransport;
                }
                if (!written)
                {
                    lock (bluetoothCombinedSpeakerReportLock)
                    {
                        bluetoothCombinedSpeakerReportSequence =
                            reportSequenceBefore;
                        bluetoothCombinedSpeakerPacketSequence =
                            packetSequenceBefore;
                        bluetoothCombinedSpeakerSequenceInitialized =
                            sequenceInitializedBefore;
                    }
                    LastBluetoothHapticsWriteStatus =
                        $"Deferred {reportDescription}: unified Bluetooth helper rejected the update.";
                    RequestUnifiedBluetoothOutputTransportRecovery();
                    return false;
                }

                if (includeHaptics)
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
        }

        private bool TryWriteCachedBluetoothCombinedSpeakerReport(
            bool hapticsSynchronized)
        {
            lock (bluetoothCombinedTransportWriteLock)
            {
                return TryWriteCachedBluetoothCombinedSpeakerReportCore(
                    hapticsSynchronized);
            }
        }

        private bool TryWriteCachedBluetoothCombinedSpeakerReportCore(
            bool hapticsSynchronized)
        {
            if (conType != ConnectionType.BT || !enableSpeakerOutput)
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
                BluetoothCombinedSpeakerDataOffset))
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
                GetBluetoothCombinedSpeakerPacketType(headsetOnlyAudio);
            combined[BluetoothCombinedSpeakerOffset + 1] =
                BluetoothCombinedSpeakerFrameLength;
            ApplyBluetoothSpeakerVolumeAndRoutingCore(combined, speakerVolume,
                headsetOnlyAudio, headphoneVolume);
            ApplyBluetoothMicrophoneStreamingRequest(combined);
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

                Volatile.Write(ref bluetoothMicrophoneStreamingRequested,
                    enabled ? 1 : 0);
                Interlocked.Exchange(
                    ref bluetoothMicrophoneControlUpdatePending, 1);
                if (!enabled)
                {
                    Interlocked.Exchange(
                        ref bluetoothMicrophoneLastFrameTimestamp, 0);
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

                // Re-evaluate ownership under the transport lock. The speaker
                // producer can claim the clock after the prewarm check above;
                // in that race a completion-aware control write would drain
                // active audio IRPs and create an audible mic-toggle gap.
                if (ShouldPublishMicrophoneStateThroughSpeakerClock(
                    enableSpeakerOutput, IsBluetoothSpeakerClockActive()))
                {
                    // Enqueue Sony's native 0x32 transition and the matching
                    // live media template as one helper command group. The
                    // steady speaker/haptics carrier changes mode only at that
                    // accepted physical boundary.
                    bool published =
                        QueueBluetoothAudioPacerMicrophoneTransitionFromCache(
                            enabled);
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
                    waitForCompletion: true);
                LastBluetoothMicrophoneWriteStatus =
                    LastBluetoothHapticsWriteStatus;
                return written;
            }
        }

        internal static bool ShouldPublishMicrophoneStateThroughSpeakerClock(
            bool speakerOutputEnabled, bool speakerClockActive)
        {
            return speakerOutputEnabled && speakerClockActive;
        }

        private void ApplyBluetoothMicrophoneStreamingRequest(byte[] report)
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
                ApplyBluetoothMicrophoneVolume(report, microphoneVolume);
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
            // PadSense keeps the complete native audio snapshot armed on every
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
                // 0x93. Keep a valid gain snapshot just as the DS5 Bridge
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
            // Sony's physical output report and DS5 Bridge both use 0x40 as
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
            if (nativeOptionsStore != null)
            {
                switch (nativeOptionsStore.MuteLedMode)
                {
                    case DualSenseControllerOptions.MuteLEDMode.Off:
                        muteLEDByte = 0x00;
                        break;
                    case DualSenseControllerOptions.MuteLEDMode.On:
                        muteLEDByte = 0x01;
                        break;
                    case DualSenseControllerOptions.MuteLEDMode.Pulse:
                        muteLEDByte = 0x02;
                        break;
                    default:
                        muteLEDByte = 0x00;
                        break;
                }
            }
        }

        private void PreparePlayerLEDBarByte()
        {
            if (nativeOptionsStore != null)
            {
                if (nativeOptionsStore.LedMode == DualSenseControllerOptions.LEDBarMode.Off)
                {
                    activePlayerLEDMask = 0x00;
                }
                else if (nativeOptionsStore.LedMode == DualSenseControllerOptions.LEDBarMode.On)
                {
                    activePlayerLEDMask = deviceSlotMask;
                }
                else if (nativeOptionsStore.LedMode == DualSenseControllerOptions.LEDBarMode.BatteryPercentage)
                {
                    activePlayerLEDMask = DeviceBatteryLinearMask(battery);
                }
            }
        }

        public override void PrepareTriggerEffect(TriggerId trigger, TriggerEffects effect, TriggerEffectSettings effectSettings)
        {
            if (trigger == TriggerId.LeftTrigger)
            {
                l2EffectData.ChangeData(effect, effectSettings);
            }
            else if (trigger == TriggerId.RightTrigger)
            {
                r2EffectData.ChangeData(effect, effectSettings);
            }
            else
            {
                throw new ArgumentOutOfRangeException("Invalid Trigger Id");
            }

            queueEvent(() =>
            {
                outputDirty = true;
                currentHap.dirty = true;
                PrepareOutReport();
            });
        }

        public void PrepareRawTriggerEffect(TriggerId trigger, byte mode, byte startResistance,
            byte effectForce, byte rangeForce, byte nearReleaseStrength, byte nearMiddleStrength,
            byte pressedStrength, byte frequency)
        {
            queueEvent(() =>
            {
                if (trigger == TriggerId.LeftTrigger)
                {
                    l2EffectData.ChangeRaw(mode, startResistance, effectForce, rangeForce,
                        nearReleaseStrength, nearMiddleStrength, pressedStrength, frequency);
                }
                else if (trigger == TriggerId.RightTrigger)
                {
                    r2EffectData.ChangeRaw(mode, startResistance, effectForce, rangeForce,
                        nearReleaseStrength, nearMiddleStrength, pressedStrength, frequency);
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(trigger), "Invalid Trigger Id");
                }

                outputDirty = true;
                currentHap.dirty = true;
                PrepareOutReport();
            });
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
                    if (numControllers > 1)
                    {
                        activePlayerLEDMask = deviceSlotMask;
                    }
                    else
                    {
                        activePlayerLEDMask = 0x00;
                    }
                }
            }

            queueEvent(() =>
            {
                outputDirty = true;
                //PrepareOutReport();
            });
        }

        private void SetupOptionsEvents()
        {
            if (nativeOptionsStore != null)
            {
                nativeOptionsStore.MuteLedModeChanged += (sender, e) =>
                {
                    PrepareMuteLEDByte();
                    queueEvent(() => { outputDirty = true; });
                };

                nativeOptionsStore.LedModeChanged += (sender, e) =>
                {
                    PreparePlayerLEDBarByte();
                    queueEvent(() => { outputDirty = true; });
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
