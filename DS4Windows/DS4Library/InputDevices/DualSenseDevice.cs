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
using System.Threading.Tasks;
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
        private const int BluetoothCombinedSpeakerOffset = 142;
        private const int BluetoothCombinedSpeakerDataOffset = 144;
        private const int BluetoothCombinedSpeakerFrameLength = 200;
        private const int BluetoothCombinedStateOffset = 13;
        private const int BluetoothCombinedStateFlag0Offset = BluetoothCombinedStateOffset;
        private const int BluetoothCombinedStateFlag1Offset = BluetoothCombinedStateOffset + 1;
        private const int BluetoothCombinedStateSpeakerVolumeOffset = BluetoothCombinedStateOffset + 5;
        private const int BluetoothCombinedStateAudioControlOffset = BluetoothCombinedStateOffset + 7;
        private const int BluetoothCombinedStateAudioControl2Offset = BluetoothCombinedStateOffset + 37;
        private const int MaxBluetoothSpeakerFrames = 4;
        private readonly object bluetoothSpeakerFrameLock = new object();
        private readonly Queue<byte[]> bluetoothSpeakerFrames = new Queue<byte[]>(MaxBluetoothSpeakerFrames);
        private long bluetoothSpeakerFramesDropped;
        private long bluetoothSpeakerFramesUnderrun;
        private readonly object bluetoothCombinedSpeakerReportLock = new object();
        private byte[] latestBluetoothCombinedSpeakerReport;
        private byte bluetoothCombinedSpeakerReportSequence;
        private byte bluetoothCombinedSpeakerPacketSequence;
        private bool bluetoothCombinedSpeakerSequenceInitialized;
        private long bluetoothCombinedSpeakerReportsWritten;
        private long bluetoothCombinedSpeakerWriteFailures;
        private readonly object bluetoothRealtimeWriterLock = new object();
        private DualSenseBluetoothRealtimeWriter bluetoothRealtimeWriter;
        private long bluetoothRealtimeWriterLastOpenAttemptUtcTicks;
        private long bluetoothRealtimeWriterDroppedReports;
        private readonly object bluetoothCombinedOutputWriteLock = new object();
        private byte[] pendingBluetoothCombinedOutputReport;
        private bool bluetoothCombinedOutputWriteScheduled;
        private long bluetoothCombinedOutputReportsCoalesced;
        private long bluetoothCombinedOutputReportCount;
        private long bluetoothCombinedOutputLastTimestamp;
        private long bluetoothCombinedOutputMaxGapTicks;
        private long bluetoothCombinedOutputLateReportCount;
        private long bluetoothNormalOutputWritesSuppressed;
        private int bluetoothCombinedOutputTransportEnabled;
        private long bluetoothMicrophoneFrameCount;
        private long bluetoothMicrophoneLastFrameUtcTicks;

        public long BluetoothMicrophoneFrameCount => Interlocked.Read(ref bluetoothMicrophoneFrameCount);
        public long BluetoothCombinedOutputReportsCoalesced =>
            Interlocked.Read(ref bluetoothCombinedOutputReportsCoalesced);
        public long BluetoothCombinedOutputReportCount =>
            Interlocked.Read(ref bluetoothCombinedOutputReportCount);
        public long BluetoothCombinedOutputLateReportCount =>
            Interlocked.Read(ref bluetoothCombinedOutputLateReportCount);
        public long BluetoothNormalOutputWritesSuppressed =>
            Interlocked.Read(ref bluetoothNormalOutputWritesSuppressed);
        public double BluetoothCombinedOutputMaxGapMilliseconds =>
            Interlocked.Read(ref bluetoothCombinedOutputMaxGapTicks) * 1000.0 / Stopwatch.Frequency;
        public long BluetoothRealtimeWriterDroppedReports =>
            Interlocked.Read(ref bluetoothRealtimeWriterDroppedReports);
        public long BluetoothSpeakerFramesDropped => Interlocked.Read(ref bluetoothSpeakerFramesDropped);
        public long BluetoothSpeakerFramesUnderrun => Interlocked.Read(ref bluetoothSpeakerFramesUnderrun);
        public long BluetoothCombinedSpeakerReportsWritten =>
            Interlocked.Read(ref bluetoothCombinedSpeakerReportsWritten);
        public long BluetoothCombinedSpeakerWriteFailures =>
            Interlocked.Read(ref bluetoothCombinedSpeakerWriteFailures);
        public int PendingBluetoothSpeakerFrames
        {
            get
            {
                lock (bluetoothSpeakerFrameLock)
                {
                    return bluetoothSpeakerFrames.Count;
                }
            }
        }
        public DateTime BluetoothMicrophoneLastFrameUtc
        {
            get
            {
                long ticks = Interlocked.Read(ref bluetoothMicrophoneLastFrameUtcTicks);
                return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// True after VIIPER has delivered a vDS-compatible combined Bluetooth
        /// haptics report. Speaker frames are then embedded in that single
        /// output stream rather than competing with it through report 0x35.
        /// </summary>
        public bool BluetoothCombinedOutputTransportEnabled =>
            Volatile.Read(ref bluetoothCombinedOutputTransportEnabled) != 0;

        /// <summary>
        /// Queues one fixed-size Opus frame for the next combined Bluetooth
        /// report. The small bounded queue aligns the capture and virtual-audio
        /// clocks without allowing latency to grow under backpressure.
        /// </summary>
        public void SetBluetoothSpeakerAudioFrame(byte[] frame, int length)
        {
            if (frame == null || length <= 0)
            {
                return;
            }

            lock (bluetoothSpeakerFrameLock)
            {
                byte[] speakerFrame = new byte[BluetoothCombinedSpeakerFrameLength];
                int bytesToCopy = Math.Min(Math.Min(length, frame.Length), BluetoothCombinedSpeakerFrameLength);
                Array.Copy(frame, 0, speakerFrame, 0, bytesToCopy);

                while (bluetoothSpeakerFrames.Count >= MaxBluetoothSpeakerFrames)
                {
                    bluetoothSpeakerFrames.Dequeue();
                    Interlocked.Increment(ref bluetoothSpeakerFramesDropped);
                }

                bluetoothSpeakerFrames.Enqueue(speakerFrame);
            }

            // VIIPER's combined haptics cadence can be slower and burstier
            // than the controller's speaker cadence. The speaker worker owns
            // the final 0x36 write so every Opus frame gets one timely slot.
            TryWriteCachedBluetoothCombinedSpeakerReport();
        }

        /// <summary>
        /// Drops any cached speaker data. A Bluetooth 0x36 report must never
        /// replay an old Opus frame after speaker output is disabled or its
        /// capture source has changed.
        /// </summary>
        public void ClearBluetoothSpeakerAudioFrame()
        {
            lock (bluetoothSpeakerFrameLock)
            {
                bluetoothSpeakerFrames.Clear();
            }

            lock (bluetoothCombinedSpeakerReportLock)
            {
                latestBluetoothCombinedSpeakerReport = null;
                bluetoothCombinedSpeakerSequenceInitialized = false;
            }

            Interlocked.Exchange(ref bluetoothCombinedOutputTransportEnabled, 0);
        }

        private bool TryTakeBluetoothSpeakerAudioFrame(byte[] destination, int destinationOffset)
        {
            lock (bluetoothSpeakerFrameLock)
            {
                if (!enableSpeakerOutput || bluetoothSpeakerFrames.Count == 0 || destination == null ||
                    destinationOffset < 0 || destinationOffset + BluetoothCombinedSpeakerFrameLength > destination.Length)
                {
                    if (enableSpeakerOutput)
                    {
                        Interlocked.Increment(ref bluetoothSpeakerFramesUnderrun);
                    }

                    return false;
                }

                byte[] speakerFrame = bluetoothSpeakerFrames.Dequeue();
                Array.Copy(speakerFrame, 0, destination, destinationOffset,
                    BluetoothCombinedSpeakerFrameLength);
                return true;
            }
        }

        private DualSenseControllerOptions nativeOptionsStore;
        public DualSenseControllerOptions NativeOptionsStore { get => nativeOptionsStore; }

        public void SetMicrophoneMuteState(bool muted)
        {
            queueEvent(() =>
            {
                microphoneMuteOverride = true;
                microphoneMuted = muted;
                outputDirty = true;
            });
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

            // Need to blank LED lights so lightbar will change colors
            // as requested
            if (conType == ConnectionType.BT)
            {
                SendInitialBTOutputReport();
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
            unchecked
            {
                Debouncer = SetupDebouncer();
                firstActive = DateTime.UtcNow;
                NativeMethods.HidD_SetNumInputBuffers(hDevice.SafeReadHandle.DangerousGetHandle(), 3);
                Queue<long> latencyQueue = new Queue<long>(21); // Set capacity at max + 1 to avoid any resizing
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
                long latencySum = 0;
                int reportOffset = conType == ConnectionType.BT ? 1 : 0;

                // Run continuous calibration on Gyro when starting input loop
                sixAxis.ResetContinuousCalibration();
                standbySw.Start();

                while (!exitInputThread)
                {
                    oldCharging = charging;
                    currerror = string.Empty;
                    bool bluetoothMicrophoneFrame = false;

                    if (tempLatencyCount >= 20)
                    {
                        latencySum -= latencyQueue.Dequeue();
                        tempLatencyCount--;
                    }

                    latencySum += this.lastTimeElapsed;
                    latencyQueue.Enqueue(this.lastTimeElapsed);
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
                            bluetoothMicrophoneFrame = IsBluetoothMicrophoneFrame(inputReport);
                            if (bluetoothMicrophoneFrame)
                            {
                                // A valid mic packet is not a malformed normal
                                // input packet, so it must clear any previous
                                // normal-input CRC error streak.
                                this.inputReportErrorCount = 0;
                                RecordBluetoothMicrophoneFrame();
                            }
                            else
                            {
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

                    if (bluetoothMicrophoneFrame)
                    {
                        // Mic packets share report ID 0x31 with normal Bluetooth
                        // input but do not use the normal gamepad packet layout.
                        // Keep output actions flowing without feeding voice data
                        // into controller-state parsing.
                        ProcessQueuedEvents();
                        continue;
                    }

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
                    if (outputDirty)
                    {
                        // A combined 0x36 report already carries the current
                        // Bluetooth state, haptics, and speaker packet. A
                        // separate 0x31 keepalive can interrupt that stream;
                        // in particular, the normal four-second rumble timer
                        // creates audible speaker dropouts.
                        if (!UsesCombinedBluetoothSpeakerTransport())
                        {
                            WriteReport();
                        }
                        else
                        {
                            Interlocked.Increment(ref bluetoothNormalOutputWritesSuppressed);
                        }

                        currentHap.dirty = false;
                        previousHapticState = currentHap;
                    }

                    outputDirty = false;
                    currentHap.dirty = false;
                    //forceWrite = false;

                    if (!string.IsNullOrEmpty(currerror))
                        error = currerror;
                    else if (!string.IsNullOrEmpty(error))
                        error = string.Empty;

                    cState.CopyTo(pState);

                    ProcessQueuedEvents();
                }
            }

            timeoutExecuted = true;
        }

        private bool UsesCombinedBluetoothSpeakerTransport()
        {
            return conType == ConnectionType.BT && enableSpeakerOutput &&
                BluetoothCombinedOutputTransportEnabled;
        }

        private static bool IsBluetoothMicrophoneFrame(byte[] report)
        {
            // HidBth strips the HIDP 0xA1 transaction prefix. Direct-L2CAP
            // references therefore see A1 31 flags, while this handle exposes
            // 31 flags. Bit 1 in flags marks a 71-byte Opus mic frame.
            return report != null && report.Length >= 2 && report[0] == 0x31 &&
                (report[1] & 0x02) != 0;
        }

        private void RecordBluetoothMicrophoneFrame()
        {
            Interlocked.Increment(ref bluetoothMicrophoneFrameCount);
            Interlocked.Exchange(ref bluetoothMicrophoneLastFrameUtcTicks, DateTime.UtcNow.Ticks);
        }

        private void ProcessQueuedEvents()
        {
            if (!hasInputEvts)
            {
                return;
            }

            lock (eventQueueLock)
            {
                Action tempAct = null;
                for (int actInd = 0, actLen = eventQueue.Count; actInd < actLen; actInd++)
                {
                    tempAct = eventQueue.Dequeue();
                    tempAct.Invoke();
                }

                hasInputEvts = false;
            }
        }

        protected override void StopOutputUpdate()
        {
            DisposeBluetoothRealtimeWriter();
            SendEmptyOutputReport();
        }

        private void DisposeBluetoothRealtimeWriter()
        {
            lock (bluetoothRealtimeWriterLock)
            {
                bluetoothRealtimeWriter?.Dispose();
                bluetoothRealtimeWriter = null;
            }
        }

        private void SendEmptyOutputReport()
        {
            int reportOffset = conType == ConnectionType.BT ? 1 : 0;
            Array.Clear(outputReport, 0, outputReport.Length);

            outputReport[0] = conType == ConnectionType.USB ? OUTPUT_REPORT_ID_USB :
                OUTPUT_REPORT_ID_BT;

            // Disable haptics and trigger motors
            outputReport[1 + reportOffset] = useRumble ? (byte)0x0F : (byte)0x0C;
            outputReport[2 + reportOffset] = 0x15; // Toggle all LED lights. 0x01 | 0x04 | 0x10

            // Set Lightbar to white
            outputReport[45 + reportOffset] = 0xFF; 
            outputReport[46 + reportOffset] = 0xFF;
            outputReport[47 + reportOffset] = 0xFF;

            if (conType == ConnectionType.BT)
            {
                outputReport[1] = OUTPUT_REPORT_ID_DATA;

                // Need to calculate and populate CRC32 data so controller will accept the report
                uint calcCrc32 = ~Crc32Algorithm.Compute(outputBTCrc32Head);
                calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref outputReport, 0, BT_OUTPUT_REPORT_LENGTH - 4);
                outputReport[74] = (byte)calcCrc32;
                outputReport[75] = (byte)(calcCrc32 >> 8);
                outputReport[76] = (byte)(calcCrc32 >> 16);
                outputReport[77] = (byte)(calcCrc32 >> 24);
            }

            WriteReport();
            //hDevice.fileStream.Flush();
        }

        private void SendInitialBTOutputReport()
        {
            Array.Clear(outputReport, 0, outputReport.Length);

            outputReport[0] = OUTPUT_REPORT_ID_BT; // Report ID
            outputReport[1] = OUTPUT_REPORT_ID_DATA;
            outputReport[3] = 0x15; // Toggle all LED lights. 0x01 | 0x04 | 0x10

            // Need to calculate and populate CRC32 data so controller will accept the report
            uint calcCrc32 = ~Crc32Algorithm.Compute(outputBTCrc32Head);
            calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref outputReport, 0, BT_OUTPUT_REPORT_LENGTH - 4);
            outputReport[74] = (byte)calcCrc32;
            outputReport[75] = (byte)(calcCrc32 >> 8);
            outputReport[76] = (byte)(calcCrc32 >> 16);
            outputReport[77] = (byte)(calcCrc32 >> 24);

            WriteReport();
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
                outputReport[1] = (byte)((useRumble ? 0x0F : 0x0C) | 0x10 | 0x40 | (enableSpeakerOutput ? 0x20 : 0x00)); // 0x02 | 0x01 | 0x04 | 0x08;

                // 0x01 Toggling microphone LED, 0x02 Toggling Audio/Mic Mute
                // 0x04 Toggling LED strips on the sides of the Touchpad, 0x08 Turn off all LED lights
                // 0x10 Toggle player LED lights below Touchpad, 0x20 ???
                // 0x40 Adjust overall motor/effect power, 0x80 ???
                outputReport[2] = (byte)(0x55 |
                    (muteLedOverride || microphoneMuteOverride ? 0x01 : 0x00) |
                    (microphoneMuteOverride ? 0x02 : 0x00)); // 0x04 | 0x01 | 0x10 | 0x40

                if (useRumble || useAccurateRumble)
                {
                    // Right? High Freq Motor
                    outputReport[3] = currentHap.rumbleState.RumbleMotorStrengthRightLightFast;
                    // Left? Low Freq Motor
                    outputReport[4] = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow;
                }

                // Headphone volume
                outputReport[5] = headphoneVolume; // Left and Right
                // Internal speaker volume
                outputReport[6] = speakerVolume;
                // Internal microphone volume
                outputReport[7] = microphoneVolume;
                // 0x01 Enable internal microphone, 0x10 Disable attached headphones (must set 0x20 as well)
                // 0x20 Enable internal speaker
                outputReport[8] = enableSpeakerOutput ? (byte)0x20 : (byte)0x00;

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
                outputReport[38] = (byte)Math.Min(7, speakerVolume / 32);

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
                outputReport[2] = (byte)((useRumble ? 0x0F : 0x0C) | 0x10 | 0x40 | (enableSpeakerOutput ? 0x20 : 0x00)); // 0x02 | 0x01 | 0x04 | 0x08;

                // 0x01 Toggling microphone LED, 0x02 Toggling Audio/Mic Mute
                // 0x04 Toggling LED strips on the sides of the Touchpad, 0x08 Turn off all LED lights
                // 0x10 Toggle player LED lights below Touchpad, 0x20 ???
                // 0x40 Adjust overall motor/effect power, 0x80 ???
                outputReport[3] = (byte)(0x55 |
                    (muteLedOverride || microphoneMuteOverride ? 0x01 : 0x00) |
                    (microphoneMuteOverride ? 0x02 : 0x00)); // 0x04 | 0x01 | 0x10 | 0x40

                if (useRumble || useAccurateRumble)
                {
                    // Right? High Freq Motor
                    outputReport[4] = currentHap.rumbleState.RumbleMotorStrengthRightLightFast;
                    // Left? Low Freq Motor
                    outputReport[5] = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow;
                }

                // Headphone volume
                outputReport[6] = headphoneVolume; // Left and Right
                // Internal speaker volume
                outputReport[7] = speakerVolume;
                // Internal microphone volume
                outputReport[8] = microphoneVolume;
                // 0x01 Enable internal microphone, 0x10 Disable attached headphones (must set 0x20 as well)
                // 0x20 Enable internal speaker
                outputReport[9] = enableSpeakerOutput ? (byte)0x20 : (byte)0x00;

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
                outputReport[39] = (byte)Math.Min(7, speakerVolume / 32);

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
            bool result;
            if (conType == ConnectionType.BT)
            {
                // DualSense seems to only accept output data via the Interrupt endpoint
                result = hDevice.WriteOutputReportViaInterrupt(outputReport, READ_STREAM_TIMEOUT);
                //result = hDevice.WriteOutputReportViaControl(outputReport);
            }
            else
            {
                result = hDevice.WriteOutputReportViaInterrupt(outputReport, READ_STREAM_TIMEOUT);
            }

            //Console.WriteLine("STAUTS: {0}", result);
            return result;
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
                if (UsesCombinedBluetoothSpeakerTransport() &&
                    UpdateCachedBluetoothCombinedState(report, offset))
                {
                    LastBluetoothHapticsWriteStatus =
                        "Merged game output state into the speaker-clocked combined Bluetooth report.";
                    return;
                }

                Array.Clear(outputReport, 0, outputReport.Length);

                if (conType == ConnectionType.BT)
                {
                    if (outputReport.Length < BT_OUTPUT_REPORT_LENGTH)
                    {
                        return;
                    }

                    outputReport[0] = OUTPUT_REPORT_ID_BT;
                    outputReport[1] = OUTPUT_REPORT_ID_DATA;
                    Array.Copy(report, offset + 1, outputReport, 2, USB_OUTPUT_CHANGE_LENGTH - 1);

                    uint calcCrc32 = ~Crc32Algorithm.Compute(outputBTCrc32Head);
                    calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref outputReport, 0, BT_OUTPUT_REPORT_LENGTH - 4);
                    outputReport[74] = (byte)calcCrc32;
                    outputReport[75] = (byte)(calcCrc32 >> 8);
                    outputReport[76] = (byte)(calcCrc32 >> 16);
                    outputReport[77] = (byte)(calcCrc32 >> 24);
                }
                else
                {
                    if (outputReport.Length < USB_OUTPUT_CHANGE_LENGTH)
                    {
                        return;
                    }

                    Array.Copy(report, offset, outputReport, 0, USB_OUTPUT_CHANGE_LENGTH);
                }

                WriteReport();
            });

            return true;
        }

        public bool WriteBluetoothHapticsOutputReport(byte[] report, int offset, int length, bool waitForWrite = false)
        {
            return WriteBluetoothAudioOutputReport(report, offset, length, 0x32, 141,
                "haptics", waitForWrite);
        }

        /// <summary>
        /// Queues a DualSense Bluetooth speaker stream report. This is the 334-byte
        /// report 0x35 packet 0x13 lane used by the controller's internal speaker.
        /// It is deliberately separate from the 0x32 haptics PCM lane so a game
        /// audio mirror cannot overwrite adaptive-trigger or haptics state.
        /// </summary>
        public bool WriteBluetoothSpeakerAudioOutputReport(byte[] report, int offset, int length)
        {
            return WriteBluetoothAudioOutputReport(report, offset, length, 0x35, 334,
                "speaker audio", waitForWrite: false);
        }

        /// <summary>
        /// Receives VIIPER's vDS-compatible Bluetooth report 0x36. With
        /// speaker streaming enabled, this supplies the latest native state
        /// and haptics block while the speaker worker owns the final physical
        /// write cadence.
        /// </summary>
        public bool WriteBluetoothCombinedHapticsAudioOutputReport(byte[] report, int offset, int length)
        {
            if (report == null || offset < 0 || length != BluetoothCombinedOutputReportLength ||
                offset + length > report.Length || report[offset] != 0x36)
            {
                return WriteBluetoothAudioOutputReport(report, offset, length, 0x36,
                    BluetoothCombinedOutputReportLength, "combined haptics/audio", waitForWrite: false);
            }

            byte[] combined = new byte[BluetoothCombinedOutputReportLength];
            Array.Copy(report, offset, combined, 0, combined.Length);
            RecordBluetoothCombinedOutputTiming();

            Interlocked.Exchange(ref bluetoothCombinedOutputTransportEnabled, 1);
            if (enableSpeakerOutput)
            {
                CacheBluetoothCombinedSpeakerReport(combined);
                LastBluetoothHapticsWriteStatus =
                    "Cached Bluetooth combined haptics report for the speaker clock.";
                return true;
            }

            return QueueLatestBluetoothCombinedOutputReport(combined);
        }

        private void RecordBluetoothCombinedOutputTiming()
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref bluetoothCombinedOutputLastTimestamp, now);
            Interlocked.Increment(ref bluetoothCombinedOutputReportCount);
            if (previous == 0)
            {
                return;
            }

            long gap = now - previous;
            long maximum;
            do
            {
                maximum = Interlocked.Read(ref bluetoothCombinedOutputMaxGapTicks);
                if (gap <= maximum)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref bluetoothCombinedOutputMaxGapTicks, gap, maximum) != maximum);

            if (gap > Stopwatch.Frequency / 67)
            {
                // The transport normally supplies an audio/haptics update
                // about every 10-11 ms. Fifteen milliseconds is a useful
                // conservative boundary for a real missed speaker slot.
                Interlocked.Increment(ref bluetoothCombinedOutputLateReportCount);
            }
        }

        private void CacheBluetoothCombinedSpeakerReport(byte[] report)
        {
            lock (bluetoothCombinedSpeakerReportLock)
            {
                latestBluetoothCombinedSpeakerReport = report;
                if (!bluetoothCombinedSpeakerSequenceInitialized)
                {
                    bluetoothCombinedSpeakerReportSequence = (byte)(report[1] >> 4);
                    bluetoothCombinedSpeakerPacketSequence = report[10];
                    bluetoothCombinedSpeakerSequenceInitialized = true;
                }
            }
        }

        /// <summary>
        /// A game can send an ordinary USB output report while its UAC haptics
        /// stream is active. The current 0x36 report owns the physical
        /// Bluetooth transport in that state, so fold the USB effect state
        /// into its 0x10 state block instead of issuing a competing 0x31
        /// write. VIIPER uses this exact raw-report-to-state mapping when it
        /// creates the combined report.
        /// </summary>
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
                if (latestBluetoothCombinedSpeakerReport == null)
                {
                    return false;
                }

                Array.Copy(report, offset + 1, latestBluetoothCombinedSpeakerReport,
                    BluetoothCombinedStateOffset, USB_OUTPUT_CHANGE_LENGTH - 1);
                return true;
            }
        }

        private bool TryWriteCachedBluetoothCombinedSpeakerReport()
        {
            if (conType != ConnectionType.BT || !enableSpeakerOutput)
            {
                return false;
            }

            byte[] combined;
            lock (bluetoothCombinedSpeakerReportLock)
            {
                if (latestBluetoothCombinedSpeakerReport == null)
                {
                    return false;
                }

                combined = new byte[BluetoothCombinedOutputReportLength];
                Array.Copy(latestBluetoothCombinedSpeakerReport, combined, combined.Length);
                combined[1] = (byte)((bluetoothCombinedSpeakerReportSequence & 0x0F) << 4);
                combined[10] = bluetoothCombinedSpeakerPacketSequence;
            }

            ApplyBluetoothSpeakerRouting(combined);

            // A 0x93 packet with a zero payload is not a valid silent Opus
            // frame. Omit the speaker TLV until an encoded frame is ready,
            // otherwise some controller firmware emits an audible alert tone.
            combined[BluetoothCombinedSpeakerOffset] = 0;
            combined[BluetoothCombinedSpeakerOffset + 1] = 0;
            Array.Clear(combined, BluetoothCombinedSpeakerDataOffset,
                BluetoothCombinedSpeakerFrameLength);
            if (!TryTakeBluetoothSpeakerAudioFrame(combined, BluetoothCombinedSpeakerDataOffset))
            {
                return false;
            }

            combined[BluetoothCombinedSpeakerOffset] = 0x93;
            combined[BluetoothCombinedSpeakerOffset + 1] = BluetoothCombinedSpeakerFrameLength;
            uint crc = DualSenseBluetoothCrc32(combined, combined.Length - 4);
            combined[combined.Length - 4] = (byte)crc;
            combined[combined.Length - 3] = (byte)(crc >> 8);
            combined[combined.Length - 2] = (byte)(crc >> 16);
            combined[combined.Length - 1] = (byte)(crc >> 24);

            bool written;
            try
            {
                written = TryWriteBluetoothCombinedSpeakerReport(combined,
                    out bool realtimeWriterActive);
                if (!realtimeWriterActive)
                {
                    // A controller can be transitioning between profiles while
                    // its HID handle is recreated. Preserve the synchronous
                    // fallback for that narrow window; normal streaming uses
                    // the bounded asynchronous pool above.
                    lock (bluetoothRealtimeWriterLock)
                    {
                        written = hDevice.WriteOutputReportViaInterrupt(combined, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref bluetoothCombinedSpeakerWriteFailures);
                LastBluetoothHapticsWriteStatus =
                    $"Speaker-clocked combined Bluetooth write threw {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            if (!written)
            {
                Interlocked.Increment(ref bluetoothCombinedSpeakerWriteFailures);
                LastBluetoothHapticsWriteStatus =
                    $"Speaker-clocked combined Bluetooth write returned false. LastWin32Error={Marshal.GetLastWin32Error()}.";
                return false;
            }

            lock (bluetoothCombinedSpeakerReportLock)
            {
                bluetoothCombinedSpeakerReportSequence =
                    (byte)((bluetoothCombinedSpeakerReportSequence + 1) & 0x0F);
                bluetoothCombinedSpeakerPacketSequence++;
            }

            Interlocked.Increment(ref bluetoothCombinedSpeakerReportsWritten);
            LastBluetoothHapticsWriteStatus = "Speaker-clocked combined Bluetooth write completed successfully.";
            return true;
        }

        private bool TryWriteBluetoothCombinedSpeakerReport(byte[] report, out bool realtimeWriterActive)
        {
            realtimeWriterActive = false;
            lock (bluetoothRealtimeWriterLock)
            {
                if (bluetoothRealtimeWriter == null)
                {
                    int error = 0;
                    if (hDevice?.SafeReadHandle == null ||
                        !DualSenseBluetoothRealtimeWriter.TryCreate(hDevice.SafeReadHandle,
                            BluetoothCombinedOutputReportLength, out bluetoothRealtimeWriter, out error))
                    {
                        bluetoothRealtimeWriter = null;
                        LastBluetoothHapticsWriteStatus =
                            $"Realtime speaker writer unavailable on the active HID handle. LastWin32Error={error}.";
                        return false;
                    }
                }

                realtimeWriterActive = true;
                bool accepted = bluetoothRealtimeWriter.TryWrite(report, out bool transportFault);
                if (accepted)
                {
                    return true;
                }

                Interlocked.Increment(ref bluetoothRealtimeWriterDroppedReports);
                if (transportFault)
                {
                    bluetoothRealtimeWriter.Dispose();
                    bluetoothRealtimeWriter = null;
                    realtimeWriterActive = false;
                    LastBluetoothHapticsWriteStatus =
                        "Realtime speaker writer encountered a transport fault; using the synchronous fallback.";
                }
                else
                {
                    LastBluetoothHapticsWriteStatus =
                        "Realtime speaker writer was saturated; dropped one stale speaker frame to protect latency.";
                }

                return false;
            }
        }

        private void ApplyBluetoothSpeakerRouting(byte[] combined)
        {
            // The firmware route is sticky. Keep the internal speaker path,
            // volume, and pre-gain asserted on every combined packet while
            // audio is active so a game's feedback cannot flip the route
            // mid-stream and corrupt an otherwise valid Opus frame.
            combined[BluetoothCombinedStateFlag0Offset] |= 0xA0;
            combined[BluetoothCombinedStateFlag1Offset] |= 0x80;
            // vDS' hardware-derived state uses a 0x64 ceiling and preamp 2
            // for the internal Bluetooth speaker. DS4Windows' normal output
            // report permits a wider UI range, but applying that raw value to
            // the Opus path can overdrive the controller's tiny amplifier.
            combined[BluetoothCombinedStateSpeakerVolumeOffset] =
                (byte)Math.Min(0x64, speakerVolume);
            combined[BluetoothCombinedStateAudioControlOffset] = 0x30;
            combined[BluetoothCombinedStateAudioControl2Offset] = 0x02;
        }

        /// <summary>
        /// Keeps Bluetooth haptics responsive when Windows delivers several USB
        /// isochronous packets in a burst. Advanced-haptics samples are time
        /// sensitive, so the newest packet is more useful than a delayed queue
        /// of old packets. This is deliberately disabled while speaker audio is
        /// active because Opus frames must be delivered in order.
        /// </summary>
        private bool QueueLatestBluetoothCombinedOutputReport(byte[] report)
        {
            bool scheduleWrite = false;
            lock (bluetoothCombinedOutputWriteLock)
            {
                if (pendingBluetoothCombinedOutputReport != null)
                {
                    Interlocked.Increment(ref bluetoothCombinedOutputReportsCoalesced);
                }

                pendingBluetoothCombinedOutputReport = report;
                if (!bluetoothCombinedOutputWriteScheduled)
                {
                    bluetoothCombinedOutputWriteScheduled = true;
                    scheduleWrite = true;
                }
            }

            if (scheduleWrite)
            {
                queueEvent(WriteLatestBluetoothCombinedOutputReport);
            }

            LastBluetoothHapticsWriteStatus = "Queued latest Bluetooth combined haptics report for asynchronous HID write.";
            return true;
        }

        private void WriteLatestBluetoothCombinedOutputReport()
        {
            byte[] report;
            lock (bluetoothCombinedOutputWriteLock)
            {
                report = pendingBluetoothCombinedOutputReport;
                pendingBluetoothCombinedOutputReport = null;
            }

            if (report != null)
            {
                try
                {
                    bool result = hDevice.WriteOutputReportViaInterrupt(report, READ_STREAM_TIMEOUT);
                    LastBluetoothHapticsWriteStatus = result ?
                        "Coalesced combined haptics write completed successfully." :
                        $"Coalesced combined haptics write returned false. LastWin32Error={Marshal.GetLastWin32Error()}.";
                }
                catch (Exception ex)
                {
                    LastBluetoothHapticsWriteStatus =
                        $"Coalesced combined haptics write threw {ex.GetType().Name}: {ex.Message}";
                }
            }

            lock (bluetoothCombinedOutputWriteLock)
            {
                if (pendingBluetoothCombinedOutputReport != null)
                {
                    // A newer haptics packet arrived during the HID write.
                    // Do not let it form a second queue behind this one: the
                    // next USB audio packet will schedule a fresh, current
                    // write on the next controller poll.
                    pendingBluetoothCombinedOutputReport = null;
                    Interlocked.Increment(ref bluetoothCombinedOutputReportsCoalesced);
                }

                bluetoothCombinedOutputWriteScheduled = false;
            }
        }

        /// <summary>
        /// Sends the controller's Bluetooth microphone stream-control packet. This
        /// only controls microphone streaming; it does not replace the normal
        /// state, trigger, haptics, or speaker output report.
        /// </summary>
        public bool SetBluetoothMicrophoneStreaming(bool enabled, bool waitForWrite = false)
        {
            byte[] report = BuildBluetoothMicrophoneControlReport(enabled);
            bool result = WriteBluetoothAudioOutputReport(report, 0, report.Length, 0x36, 398,
                enabled ? "microphone enable" : "microphone disable", waitForWrite);
            LastBluetoothMicrophoneWriteStatus = LastBluetoothHapticsWriteStatus;
            return result;
        }

        public void ResetBluetoothMicrophoneProbeStatistics()
        {
            Interlocked.Exchange(ref bluetoothMicrophoneFrameCount, 0);
            Interlocked.Exchange(ref bluetoothMicrophoneLastFrameUtcTicks, 0);
        }

        private bool WriteBluetoothAudioOutputReport(byte[] report, int offset, int length,
            byte expectedReportId, int expectedLength, string reportDescription, bool waitForWrite)
        {
            if (report == null)
            {
                LastBluetoothHapticsWriteStatus = $"Rejected: {reportDescription} report was null.";
                return false;
            }

            if (conType != ConnectionType.BT)
            {
                LastBluetoothHapticsWriteStatus = $"Rejected: controller connection type is {conType}, not Bluetooth.";
                return false;
            }

            if (length != expectedLength)
            {
                LastBluetoothHapticsWriteStatus = $"Rejected: {reportDescription} report length was {length}, expected {expectedLength}.";
                return false;
            }

            if (offset < 0 || offset + length > report.Length)
            {
                LastBluetoothHapticsWriteStatus = $"Rejected: invalid report slice offset={offset} length={length} bufferLength={report.Length}.";
                return false;
            }

            if (report[offset] != expectedReportId)
            {
                LastBluetoothHapticsWriteStatus = $"Rejected: report ID was 0x{report[offset]:X2}, expected 0x{expectedReportId:X2}.";
                return false;
            }

            byte[] audioReport = new byte[length];
            Array.Copy(report, offset, audioReport, 0, length);

            if (!waitForWrite)
            {
                queueEvent(() =>
                {
                    bool result = hDevice.WriteOutputReportViaInterrupt(audioReport, READ_STREAM_TIMEOUT);
                    LastBluetoothHapticsWriteStatus = result ?
                        "Queued write completed successfully." :
                        $"Queued write returned false. LastWin32Error={Marshal.GetLastWin32Error()}.";
                });

                LastBluetoothHapticsWriteStatus = $"Queued Bluetooth {reportDescription} report for asynchronous HID write.";
                return true;
            }

            TaskCompletionSource<bool> writeCompletion = new TaskCompletionSource<bool>();
            queueEvent(() =>
            {
                try
                {
                    bool result = hDevice.WriteOutputReportViaInterrupt(audioReport, READ_STREAM_TIMEOUT);
                    if (!result)
                    {
                        LastBluetoothHapticsWriteStatus = $"HID interrupt write returned false. LastWin32Error={Marshal.GetLastWin32Error()}.";
                    }

                    writeCompletion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    LastBluetoothHapticsWriteStatus = $"HID interrupt write threw {ex.GetType().Name}: {ex.Message}";
                    writeCompletion.TrySetResult(false);
                }
            });

            if (!writeCompletion.Task.Wait(READ_STREAM_TIMEOUT + 500))
            {
                LastBluetoothHapticsWriteStatus = "Timed out waiting for input thread to perform HID interrupt write.";
                return false;
            }

            if (!writeCompletion.Task.Result)
            {
                if (string.IsNullOrWhiteSpace(LastBluetoothHapticsWriteStatus))
                {
                    LastBluetoothHapticsWriteStatus = "HID interrupt write returned false.";
                }

                return false;
            }

            LastBluetoothHapticsWriteStatus = "HID interrupt write completed successfully.";
            return true;
        }

        public bool PlayBluetoothHapticsTestTone(int durationMs = 900, int frequencyHz = 85, byte amplitude = 72)
        {
            if (conType != ConnectionType.BT)
            {
                LastBluetoothHapticsWriteStatus = $"Rejected: controller connection type is {conType}, not Bluetooth.";
                return false;
            }

            durationMs = Math.Max(100, Math.Min(durationMs, 3000));
            frequencyHz = Math.Max(20, Math.Min(frequencyHz, 900));
            amplitude = (byte)Math.Min(amplitude, (byte)120);

            const int sampleRate = 3000;
            const int sampleBytes = 64;
            const int framesPerPacket = sampleBytes / 2;
            int packetCount = Math.Max(1, (durationMs * sampleRate) / (1000 * framesPerPacket));

            for (int packet = 0; packet < packetCount; packet++)
            {
                byte[] sample = new byte[sampleBytes];
                for (int frame = 0; frame < framesPerPacket; frame++)
                {
                    int sampleIndex = packet * framesPerPacket + frame;
                    double phase = 2.0 * Math.PI * frequencyHz * sampleIndex / sampleRate;
                    sbyte value = (sbyte)Math.Round(Math.Sin(phase) * amplitude);
                    sample[frame * 2] = unchecked((byte)value);
                    sample[(frame * 2) + 1] = unchecked((byte)value);
                }

                byte[] report = BuildBluetoothHapticsOutputReport((byte)packet, (byte)packet, sample);
                if (!WriteBluetoothHapticsOutputReport(report, 0, report.Length, true))
                {
                    return false;
                }

                Thread.Sleep(11);
            }

            return true;
        }

        private static byte[] BuildBluetoothHapticsOutputReport(byte sequence, byte intervalIndex, byte[] sample)
        {
            const int reportSize = 141;
            const int sampleSize = 64;
            byte[] report = new byte[reportSize];
            report[0] = 0x32;
            report[1] = (byte)((sequence & 0x0F) << 4);

            report[2] = 0x91;
            report[3] = 0x07;
            report[4] = 0xFE;
            report[9] = 0xFF;
            report[10] = intervalIndex;

            report[11] = 0x92;
            report[12] = sampleSize;
            Array.Copy(sample, 0, report, 13, sampleSize);

            uint crc = DualSenseBluetoothCrc32(report, reportSize - 4);
            report[reportSize - 4] = (byte)crc;
            report[reportSize - 3] = (byte)(crc >> 8);
            report[reportSize - 2] = (byte)(crc >> 16);
            report[reportSize - 1] = (byte)(crc >> 24);
            return report;
        }

        private static byte[] BuildBluetoothMicrophoneControlReport(bool enabled)
        {
            const int reportSize = 398;
            byte[] report = new byte[reportSize];
            report[0] = 0x36;
            report[1] = 0x10;
            report[2] = 0x91;
            report[3] = 0x07;
            report[4] = enabled ? (byte)0xFF : (byte)0xFE;
            report[5] = 64;
            report[6] = 64;
            report[7] = 64;
            report[8] = 64;
            report[9] = 64;

            uint crc = DualSenseBluetoothCrc32(report, reportSize - 4);
            report[reportSize - 4] = (byte)crc;
            report[reportSize - 3] = (byte)(crc >> 8);
            report[reportSize - 2] = (byte)(crc >> 16);
            report[reportSize - 1] = (byte)(crc >> 24);
            return report;
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

        private void Detach()
        {
            SendEmptyOutputReport();
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
