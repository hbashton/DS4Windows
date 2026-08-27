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
using System.ComponentModel;
using System.Threading;

using System.Runtime.InteropServices;
using System.Diagnostics;

using System.Linq;
using System.Drawing;

using DS4WinWPF.DS4Control;

namespace DS4Windows
{
    public struct DS4Color : IEquatable<DS4Color>
    {
        public byte red;
        public byte green;
        public byte blue;

        public DS4Color(Color c)
        {
            red = c.R;
            green = c.G;
            blue = c.B;
        }

        public DS4Color(byte r, byte g, byte b)
        {
            red = r;
            green = g;
            blue = b;
        }

        public bool Equals(DS4Color other)
        {
            return this.red == other.red && this.green == other.green && this.blue == other.blue;
        }

        public Color ToColor => Color.FromArgb(red, green, blue);
        public Color ToColorA
        {
            get
            {
                byte alphacolor = Math.Max(red, Math.Max(green, blue));
                Color reg = Color.FromArgb(red, green, blue);
                Color full = HuetoRGB(reg.GetHue(), reg.GetBrightness(), ref reg);
                return Color.FromArgb((alphacolor > 205 ? 255 : (alphacolor + 50)), full);
            }
        }

        private Color HuetoRGB(float hue, float light, ref Color rgb)
        {
            float L = (float)Math.Max(.5, light);
            float C = (1 - Math.Abs(2 * L - 1));
            float X = (C * (1 - Math.Abs((hue / 60) % 2 - 1)));
            float m = L - C / 2;
            float R = 0, G = 0, B = 0;
            if (light == 1) return Color.White;
            else if (rgb.R == rgb.G && rgb.G == rgb.B) return Color.White;
            else if (0 <= hue && hue < 60) { R = C; G = X; }
            else if (60 <= hue && hue < 120) { R = X; G = C; }
            else if (120 <= hue && hue < 180) { G = C; B = X; }
            else if (180 <= hue && hue < 240) { G = X; B = C; }
            else if (240 <= hue && hue < 300) { R = X; B = C; }
            else if (300 <= hue && hue < 360) { R = C; B = X; }
            return Color.FromArgb((int)((R + m) * 255), (int)((G + m) * 255), (int)((B + m) * 255));
        }

        public static bool TryParse(string value, ref DS4Color ds4color)
        {
            try
            {
                string[] ss = value.Split(',');
                return byte.TryParse(ss[0], out ds4color.red) && byte.TryParse(ss[1], out ds4color.green) && byte.TryParse(ss[2], out ds4color.blue);
            }
            catch { return false; }
        }

        public override string ToString() => $"#{red:X}{green:X}{blue:X}";
    }

    public enum ConnectionType : byte { BT, SONYWA, USB }; // Prioritize Bluetooth when both BT and USB are connected.

    /**
     * The haptics engine uses a stack of these states representing the light bar and rumble motor settings.
     * It (will) handle composing them and the details of output report management.
     */
    public struct DS4ForceFeedbackState : IEquatable<DS4ForceFeedbackState>
    {
        public byte RumbleMotorStrengthLeftHeavySlow, RumbleMotorStrengthRightLightFast;
        public bool RumbleMotorsExplicitlyOff;

        public bool Equals(DS4ForceFeedbackState other)
        {
            return RumbleMotorStrengthLeftHeavySlow == other.RumbleMotorStrengthLeftHeavySlow &&
                RumbleMotorStrengthRightLightFast == other.RumbleMotorStrengthRightLightFast &&
                RumbleMotorsExplicitlyOff == other.RumbleMotorsExplicitlyOff;
        }

        public bool IsRumbleSet()
        {
            const byte zero = 0;
            return RumbleMotorsExplicitlyOff || RumbleMotorStrengthLeftHeavySlow != zero || RumbleMotorStrengthRightLightFast != zero;
        }
    }

    public struct DS4LightbarState : IEquatable<DS4LightbarState>
    {
        public DS4Color LightBarColor;
        public bool LightBarExplicitlyOff;
        public byte LightBarFlashDurationOn, LightBarFlashDurationOff;

        public bool Equals(DS4LightbarState other)
        {
            return LightBarColor.Equals(other.LightBarColor) &&
                LightBarExplicitlyOff == other.LightBarExplicitlyOff &&
                LightBarFlashDurationOn == other.LightBarFlashDurationOn &&
                LightBarFlashDurationOff == other.LightBarFlashDurationOff;
        }

        public bool IsLightBarSet()
        {
            return LightBarExplicitlyOff || LightBarColor.red != 0 || LightBarColor.green != 0 || LightBarColor.blue != 0;
        }
    }

    public struct DS4HapticState : IEquatable<DS4HapticState>
    {
        public DS4LightbarState lightbarState;
        public DS4ForceFeedbackState rumbleState;
        public bool dirty;

        public bool Equals(DS4HapticState other)
        {
            return lightbarState.Equals(other.lightbarState) &&
                rumbleState.Equals(other.rumbleState);
        }

        public bool IsLightBarSet()
        {
            return lightbarState.IsLightBarSet();
        }

        public bool IsRumbleSet()
        {
            const byte zero = 0;
            return rumbleState.RumbleMotorsExplicitlyOff || rumbleState.RumbleMotorStrengthLeftHeavySlow != zero || rumbleState.RumbleMotorStrengthRightLightFast != zero;
        }
    }

    public class DS4Device
    {
        public class GyroMouseSens
        {
            public double mouseOffset = 0.2;
            public double mouseCoefficient = 0.012;
            public double mouseSmoothOffset = 0.2;
        }

        public enum ExclusiveStatus : byte
        {
            Shared = 0,
            Exclusive = 1,
            HidGuardAffected = 2,
            HidHideAffected = 3,
        }

        // Native DS4 effects use report 0x11 and must be written at their
        // descriptor-defined 78-byte size. Audio reports 0x14/0x17 are built
        // in separate buffers by DualShock4BluetoothAudioProtocol.
        protected const int BT_OUTPUT_REPORT_LENGTH = 78;
        private const int BT_OUTPUT_REPORT_0x15_LENGTH = 334;
        private const int BT_OUTPUT_REPORT_0x11_LENGTH = 78;
        internal const int BT_INPUT_REPORT_LENGTH = 547;
        internal const int BT_OUTPUT_CHANGE_LENGTH = 13;
        internal const int USB_OUTPUT_CHANGE_LENGTH = 11;
        // Use large value for worst case scenario
        internal const int READ_STREAM_TIMEOUT = 3000;
        // the measured transport's proven Sony sole-writer path limits ordinary effect
        // reports to roughly 30 Hz. A virtual DualSense produces haptics
        // feedback at about 94 Hz; forwarding every update while the physical
        // DS4 also receives 250 SBC reports per second saturates the Bluetooth
        // HID lane and starves input reports. Coalesce those effect updates at
        // the same 30 Hz boundary while preserving the 4 ms audio clock.
        internal const int BLUETOOTH_EFFECT_INTERVAL_DURING_SPEAKER_MS = 33;
        // Isolated BT report can have latency as high as 15 ms
        // due to hardware.
        internal const int WARN_INTERVAL_BT = 500;
        internal const int WARN_INTERVAL_USB = 100;
        // Maximum values for battery level when no USB cable is connected
        // and when a USB cable is connected
        internal const int BATTERY_MAX = 8;
        internal const int BATTERY_MAX_USB = 11;
        public const string BLANK_SERIAL = "00:00:00:00:00:00";
        public const byte SERIAL_FEATURE_ID = 18;
        protected HidDevice hDevice;
        protected string Mac;
        protected DS4State cState = new DS4State();
        protected DS4State pState = new DS4State();
        protected ConnectionType conType;
        protected byte[] accel = new byte[6];
        protected byte[] gyro = new byte[6];
        protected byte[] inputReport;
        protected byte[] btInputReport = null;
        protected byte[] outReportBuffer, outputReport;
        private readonly object bluetoothOutputWriteLock = new object();
        private readonly object bluetoothAudioControlLaneLock = new object();
        private readonly object outputReportStateLock = new object();
        private readonly DualShock4BluetoothAudioState bluetoothAudioState =
            new DualShock4BluetoothAudioState();
        private readonly DualShock4ControllerClockDiscipline
            bluetoothControllerClock =
                new DualShock4ControllerClockDiscipline();
        private object bluetoothAudioControlLaneOwner;
        private Func<byte[], bool> bluetoothAudioControlLaneWriter;
        private long bluetoothEffectReportsDuringAudio;
        private long bluetoothEffectReportsDeferredDuringAudio;
        private long lastBluetoothEffectReportDuringAudioTick;
        private long lastBluetoothInputReportTick;
        private int bluetoothAudioDefaultInputIntervalOverride;
        protected int inputReportErrorCount = 0; // Num of consequtive input report errors (fex if BT device fails 5 times in crc32 and 0x11 data type check then switch over to handle incoming BT packets as those were usb PC-friendly packets. Some fake DS4 gamepads needs this)
        protected readonly DS4Touchpad touchpad = null;
        protected readonly DS4SixAxis sixAxis = null;
        protected Thread ds4Input, ds4Output;
        // Retained for device subclasses which still use the legacy watchdog.
        // The native DS4 path now relies on a bounded overlapped HID read so a
        // radio loss cancels the actual pending IRP instead of racing a second
        // timeout thread against it.
        protected Thread timeoutCheckThread;
        protected bool timeoutExecuted;
        protected bool timeoutEvent;
        protected int battery;
        public DateTime lastActive = DateTime.UtcNow;
        public DateTime firstActive = DateTime.UtcNow;
        protected bool charging;
        protected bool readyQuickChargeDisconnect;
        protected int warnInterval = WARN_INTERVAL_USB;

        public Debouncer Debouncer
        {
            get; protected set;
        }
        public int getWarnInterval()
        {
            return warnInterval;
        }

        public bool ReadyQuickChargeDisconnect
        {
            get => readyQuickChargeDisconnect;
            set => readyQuickChargeDisconnect = value;
        }

        public ControllerOptionsStore optionsStore;
        private DS4ControllerOptions nativeOptionsStore;

        public Int32 wheelPrevPhysicalAngle = 0;
        public Int32 wheelPrevFullAngle = 0;
        public Int32 wheelFullTurnCount = 0;

        public Point wheelCenterPoint;
        public Point wheel90DegPointLeft;
        public Point wheelCircleCenterPointLeft;
        public Point wheel90DegPointRight;
        public Point wheelCircleCenterPointRight;

        public DateTime wheelPrevRecalibrateTime;

        protected int wheelRecalibrateActiveState = 0;
        public int WheelRecalibrateActiveState
        {
            get { return wheelRecalibrateActiveState; }
            set
            {
                wheelRecalibrateActiveState = value;
            }
        }

        public enum WheelCalibrationPoint
        {
            None = 0,
            Center = 1,
            Right90 = 2,
            Left90 = 4,
            All = Center | Right90 | Left90
        }
        public WheelCalibrationPoint wheelCalibratedAxisBitmask;

        protected bool exitOutputThread = false;
        public bool ExitOutputThread => exitOutputThread;
        protected bool exitInputThread = false;
        protected object exitLocker = new object();
        protected ExclusiveStatus exclusiveStatus = ExclusiveStatus.Shared;

        public delegate void ReportHandler<TEventArgs>(DS4Device sender, TEventArgs args);

        //public event EventHandler<EventArgs> Report = null;
        public virtual event ReportHandler<EventArgs> Report = null;
        public virtual event EventHandler<EventArgs> Removal = null;
        public event Action<DS4Device, ushort, byte[]> BluetoothMicrophoneSbcFrameReceived;
        public event EventHandler<EventArgs> SyncChange = null;
        public event EventHandler<EventArgs> SerialChange = null;
        //public EventHandler<EventArgs> MotionEvent = null;
        public ReportHandler<EventArgs> MotionEvent = null;

        public HidDevice HidDevice => hDevice;
        public long DualShock4BluetoothMicrophoneFramesReceived { get; private set; }
        public long BluetoothAudioReportsWritten { get; private set; }
        public long BluetoothAudioWriteFailures { get; private set; }
        public string LastBluetoothAudioWriteStatus { get; private set; } = "inactive";
        public bool BluetoothSpeakerStreaming =>
            bluetoothAudioState.Current.SpeakerEnabled;
        public bool BluetoothMicrophoneStreaming =>
            bluetoothAudioState.Current.MicrophoneEnabled;

        internal void ReadDualShock4BluetoothAudioModeSynchronized(
            Action<bool> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            bluetoothAudioState.ReadSynchronized(state =>
                action(state.MicrophoneEnabled));
        }

        internal bool RegisterDualShock4BluetoothAudioControlLane(
            object owner, Func<byte[], bool> writer)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            lock (bluetoothAudioControlLaneLock)
            {
                if (bluetoothAudioControlLaneOwner != null &&
                    !ReferenceEquals(bluetoothAudioControlLaneOwner, owner))
                {
                    return false;
                }

                bluetoothAudioControlLaneOwner = owner;
                bluetoothAudioControlLaneWriter = writer;
                return true;
            }
        }

        internal void UnregisterDualShock4BluetoothAudioControlLane(
            object owner)
        {
            lock (bluetoothAudioControlLaneLock)
            {
                if (!ReferenceEquals(bluetoothAudioControlLaneOwner, owner))
                {
                    return;
                }

                bluetoothAudioControlLaneWriter = null;
                bluetoothAudioControlLaneOwner = null;
            }
        }
        internal long BluetoothEffectReportsDuringAudio =>
            Interlocked.Read(ref bluetoothEffectReportsDuringAudio);
        internal long BluetoothEffectReportsDeferredDuringAudio =>
            Interlocked.Read(ref bluetoothEffectReportsDeferredDuringAudio);
        internal long LastBluetoothEffectReportDuringAudioTick =>
            Interlocked.Read(ref lastBluetoothEffectReportDuringAudioTick);
        internal long LastBluetoothInputReportTick =>
            Interlocked.Read(ref lastBluetoothInputReportTick);
        internal double BluetoothControllerClockRatio =>
            bluetoothControllerClock.Ratio;
        internal double BluetoothControllerClockRawRatio =>
            bluetoothControllerClock.RawRatio;
        internal bool BluetoothControllerClockLocked =>
            bluetoothControllerClock.HasEstimate;
        internal int BluetoothControllerClockAcceptedFits =>
            bluetoothControllerClock.AcceptedFits;
        internal int BluetoothControllerClockRejectedFits =>
            bluetoothControllerClock.RejectedFits;

        internal static bool ShouldDeferBluetoothEffectDuringSpeaker(
            bool usingBluetooth, bool speakerEnabled, bool force,
            bool reportPending, long elapsedMilliseconds,
            bool audioControlRefreshPending = false)
        {
            return usingBluetooth && speakerEnabled && !force &&
                !audioControlRefreshPending &&
                reportPending && elapsedMilliseconds >= 0 &&
                elapsedMilliseconds <
                    BLUETOOTH_EFFECT_INTERVAL_DURING_SPEAKER_MS;
        }

        internal void SetBluetoothAudioDefaultInputIntervalOverride(
            bool enabled)
        {
            Interlocked.Exchange(
                ref bluetoothAudioDefaultInputIntervalOverride,
                enabled ? 1 : 0);
        }

        internal static byte GetBluetoothOutputPollRate(int profilePollRate,
            bool useDefaultAudioInterval)
        {
            return useDefaultAudioInterval ? (byte)0 :
                (byte)Math.Clamp(profilePollRate, 0, 16);
        }
        public bool IsHidExclusive => HidDevice.IsExclusive;
        public bool isHidExclusive()
        {
            return HidDevice.IsExclusive;
        }

        public bool IsExclusive
        {
            get { return exclusiveStatus > ExclusiveStatus.Shared; }
        }

        public bool isExclusive()
        {
            return exclusiveStatus > ExclusiveStatus.Shared;
        }

        public ExclusiveStatus CurrentExclusiveStatus
        {
            get => exclusiveStatus;
            set
            {
                exclusiveStatus = value;
            }
        }

        protected bool isDisconnecting = false;
        public bool IsDisconnecting
        {
            get { return isDisconnecting; }
            protected set
            {
                this.isDisconnecting = value;
            }
        }

        public bool isDisconnectingStatus()
        {
            return this.isDisconnecting;
        }

        protected bool isRemoving = false;
        public bool IsRemoving
        {
            get { return isRemoving; }
            set
            {
                this.isRemoving = value;
            }
        }

        protected bool isRemoved = false;
        public bool IsRemoved
        {
            get { return isRemoved; }
            set
            {
                this.isRemoved = value;
            }
        }

        public object removeLocker = new object();

        public string MacAddress =>  Mac;
        public event EventHandler MacAddressChanged;
        public string getMacAddress()
        {
            return this.Mac;
        }

        public ConnectionType ConnectionType => conType;
        public ConnectionType getConnectionType()
        {
            return this.conType;
        }

        // behavior only active when > 0
        protected int idleTimeout = 0;
        public int IdleTimeout
        {
            get { return idleTimeout; }
            set
            {
                idleTimeout = value;
            }
        }

        public int getIdleTimeout()
        {
            return idleTimeout;
        }

        public void setIdleTimeout(int value)
        {
            if (idleTimeout != value)
            {
                idleTimeout = value;
            }
        }

        // Feature set of gamepad (some non-official DS4 gamepads require a bit different logic than a genuine Sony DS4). 0=Default DS4 gamepad feature set.
        protected VidPidFeatureSet featureSet;
        public VidPidFeatureSet FeatureSet
        {
            get { return featureSet;  }
            set { featureSet = value; }
        }
        public VidPidFeatureSet ModifyFeatureSetFlag(VidPidFeatureSet featureBitFlag, bool flagSet)
        {
            if (flagSet) featureSet |= featureBitFlag;
            else featureSet &= ~featureBitFlag;
            return featureSet;
        }

        private const byte DEFAULT_BT_REPORT_TYPE = 0x11;
        private byte knownGoodBTOutputReportType = DEFAULT_BT_REPORT_TYPE;
        private int btOutputPayloadLen = BT_OUTPUT_REPORT_LENGTH;

        private const byte DEFAULT_OUTPUT_FEATURES = 0x07;
        //private const byte COPYCAT_OUTPUT_FEATURES = 0xF3;
        private const byte COPYCAT_OUTPUT_FEATURES = 0x03;
        private byte outputFeaturesByte = DEFAULT_OUTPUT_FEATURES;

        public int Battery => battery;
        public delegate void BatteryUpdateHandler(object sender, EventArgs e);
        public virtual event EventHandler BatteryChanged;
        public int getBattery()
        {
            return battery;
        }

        public bool Charging => charging;
        public virtual event EventHandler ChargingChanged;
        public bool isCharging()
        {
            return charging;
        }

        protected long lastTimeElapsed = 0;
        public long getLastTimeElapsed()
        {
            return lastTimeElapsed;
        }

        public double lastTimeElapsedDouble = 0.0;
        public double getLastTimeElapsedDouble()
        {
            return lastTimeElapsedDouble;
        }

        public virtual byte RightLightFastRumble
        {
            get { return currentHap.rumbleState.RumbleMotorStrengthRightLightFast; }
            set
            {
                if (currentHap.rumbleState.RumbleMotorStrengthRightLightFast != value)
                    currentHap.rumbleState.RumbleMotorStrengthRightLightFast = value;
            }
        }

        public virtual byte LeftHeavySlowRumble
        {
            get { return currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow; }
            set
            {
                if (currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow != value)
                    currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow = value;
            }
        }

        public virtual byte getLeftHeavySlowRumble()
        {
            return currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow;
        }


        private int rumbleAutostopTime = 0;
        public int RumbleAutostopTime
        {
            get { return rumbleAutostopTime; }
            set
            {
                // Value in milliseconds
                rumbleAutostopTime = value;

                // If autostop timer is disabled (value 0) then stop existing autostop timer otherwise restart it
                if (value <= 0)
                    rumbleAutostopTimer.Reset();
                else
                    rumbleAutostopTimer.Restart();
            }
        }

        public virtual DS4Color LightBarColor
        {
            get { return currentHap.lightbarState.LightBarColor; }
            set
            {
                if (currentHap.lightbarState.LightBarColor.red != value.red || currentHap.lightbarState.LightBarColor.green != value.green || currentHap.lightbarState.LightBarColor.blue != value.blue)
                {
                    currentHap.lightbarState.LightBarColor = value;
                }
            }
        }

        public byte getLightBarOnDuration()
        {
            return currentHap.lightbarState.LightBarFlashDurationOn;
        }

        // Specify the poll rate interval used for the DS4 hardware when
        // connected via Bluetooth
        protected int btPollRate = 0;
        public int BTPollRate
        {
            get { return btPollRate; }
            set
            {
                if (btPollRate != value && value >= 0 && value <= 16)
                {
                    btPollRate = value;
                }
            }
        }

        public int getBTPollRate()
        {
            return btPollRate;
        }

        public void setBTPollRate(int value)
        {
            if (btPollRate != value && value >= 0 && value <= 16)
            {
                btPollRate = value;
            }
        }

        public DS4Touchpad Touchpad { get { return touchpad; } }
        public DS4SixAxis SixAxis { get { return sixAxis; } }

        public static ConnectionType HidConnectionType(HidDevice hidDevice)
        {
            ConnectionType result = ConnectionType.USB;
            if (hidDevice.Capabilities.InputReportByteLength == 64)
            {
                if (hidDevice.Capabilities.NumberFeatureDataIndices == 22)
                {
                    result = ConnectionType.SONYWA;
                }
            }
            else
            {
                result = ConnectionType.BT;
            }

            return result;
        }

        protected Queue<Action> eventQueue = new Queue<Action>();
        protected object eventQueueLock = new object();

        protected bool runCalib;
        protected bool hasInputEvts = false;
        protected string displayName;
        public string DisplayName => displayName;
        public bool ShouldRunCalib()
        {
            return runCalib;
        }

        protected ManualResetEventSlim readWaitEv = new ManualResetEventSlim();
        public ManualResetEventSlim ReadWaitEv { get => readWaitEv; }

        public virtual byte SerialReportID { get => SERIAL_FEATURE_ID; }

        public enum BTOutputReportMethod : uint
        {
            WriteFile,
            HidD_SetOutputReport,
        }

        protected InputDevices.InputDeviceType deviceType;
        public InputDevices.InputDeviceType DeviceType { get => deviceType; }

        protected GyroMouseSens gyroMouseSensSettings;
        public virtual GyroMouseSens GyroMouseSensSettings { get => gyroMouseSensSettings; }

        protected int deviceSlotNumber = DEFAULT_JOINT_SLOT_NUMBER;
        public int DeviceSlotNumber
        {
            get => deviceSlotNumber;
            set
            {
                if (deviceSlotNumber == value) return;
                deviceSlotNumber = value;
                DeviceSlotNumberChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        protected event EventHandler DeviceSlotNumberChanged;
        protected byte deviceSlotMask = 0x00;

        protected DS4State jointState = new DS4State();
        protected DS4State jointPreviousState = new DS4State();
        public DS4State JointState
        {
            get => jointState;
            set => jointState = value;
        }

        public DS4State JointPreviousState
        {
            get => jointPreviousState;
            set => jointPreviousState = value;
        }

        protected bool performStateMerge;
        public bool PerformStateMerge
        {
            get => performStateMerge;
            set => performStateMerge = value;
        }

        protected bool primaryDevice = true;
        public bool PrimaryDevice
        {
            get => primaryDevice;
            set => primaryDevice = value;
        }

        public const int DEFAULT_JOINT_SLOT_NUMBER = -1;
        protected int jointDeviceSlotNumber = DEFAULT_JOINT_SLOT_NUMBER;
        public virtual int JointDeviceSlotNumber
        {
            get => jointDeviceSlotNumber;
            set => jointDeviceSlotNumber = value;
        }

        protected bool outputMapGyro = true;
        public bool OutputMapGyro
        {
            get => outputMapGyro;
            set => outputMapGyro = value;
        }


        public DS4Device(HidDevice hidDevice, string disName, VidPidFeatureSet featureSet = VidPidFeatureSet.DefaultDS4)
        {
            hDevice = hidDevice;
            displayName = disName;
            this.featureSet = featureSet;

            exclusiveStatus = ExclusiveStatus.Shared;
            if (hidDevice.IsExclusive)
            {
                exclusiveStatus = ExclusiveStatus.Exclusive;
            }

            if (this.FeatureSet != VidPidFeatureSet.DefaultDS4)
                AppLogger.LogToGui($"The gamepad {displayName} ({conType}) uses custom feature set ({this.FeatureSet.ToString("F")})", false);

            runCalib = (this.featureSet & VidPidFeatureSet.NoGyroCalib) == 0;

            touchpad = new DS4Touchpad();
            sixAxis = new DS4SixAxis();
        }

        public virtual void PostInit()
        {
            conType = HidConnectionType(hDevice);
            Mac = hDevice.ReadSerial(SerialReportID);

            //HidDevice hidDevice = hDevice;
            deviceType = InputDevices.InputDeviceType.DS4;
            gyroMouseSensSettings = new GyroMouseSens();
            optionsStore = nativeOptionsStore = new DS4ControllerOptions(deviceType);
            SetupOptionsEvents();

            if (conType == ConnectionType.USB || conType == ConnectionType.SONYWA)
            {
                inputReport = new byte[64];
                outputReport = new byte[hDevice.Capabilities.OutputReportByteLength];
                outReportBuffer = new byte[hDevice.Capabilities.OutputReportByteLength];
                if (conType == ConnectionType.USB)
                {
                    warnInterval = WARN_INTERVAL_USB;
                    synced = true;
                }
                else
                {
                    warnInterval = WARN_INTERVAL_BT;
                    runCalib = synced = isValidSerial();
                }
            }
            else
            {
                btInputReport = new byte[BT_INPUT_REPORT_LENGTH];
                inputReport = new byte[BT_INPUT_REPORT_LENGTH - 2];
                // If OnlyOutputData0x05 feature is not set then use the default DS4 output buffer size. However, some Razer gamepads use 32 bytes output buffer and output data type 0x05 in BT mode (writeData fails if the code tries to write too many unnecessary bytes)
                if ((this.featureSet & VidPidFeatureSet.OnlyOutputData0x05) == 0)
                {
                    // Default DS4 logic while writing data to gamepad
                    outputReport = new byte[BT_OUTPUT_REPORT_LENGTH];
                    outReportBuffer = new byte[BT_OUTPUT_REPORT_LENGTH];

                    // Buffer len and output report payload len will differ
                    btOutputPayloadLen = BT_OUTPUT_REPORT_0x11_LENGTH;
                }
                else
                {
                    // Use the gamepad specific output buffer size (but minimum of 15 bytes to avoid out-of-index errors in this app)
                    outputReport = new byte[hDevice.Capabilities.OutputReportByteLength <= 15 ? 15 : hDevice.Capabilities.OutputReportByteLength];
                    outReportBuffer = new byte[hDevice.Capabilities.OutputReportByteLength <= 15 ? 15 : hDevice.Capabilities.OutputReportByteLength];

                    // Use custom buffer len
                    btOutputPayloadLen = outputReport.Length;
                }
                warnInterval = WARN_INTERVAL_BT;
                synced = isValidSerial();
            }

            if (runCalib)
                RefreshCalibration();

            // if (!hDevice.IsFileStreamOpen())
            // {
            //     hDevice.OpenFileStream(outputReport.Length);
            // }

            // Temporarily disable this check as it does not seem to help
            // detect fake DS4 controllers
            //if (conType == ConnectionType.BT &&
            //    !featureSet.HasFlag(VidPidFeatureSet.NoOutputData) &&
            //    !featureSet.HasFlag(VidPidFeatureSet.OnlyOutputData0x05))
            //{
            //    CheckOutputReportTypes();
            //}

            sendOutputReport(true, true, false); // initialize the output report (don't force disconnect the gamepad on initialization even if writeData fails because some fake DS4 gamepads don't support writeData over BT)
        }

        // TODO: Possibly remove method
        private void CheckOutputReportTypes()
        {
            // Use Tuple here for convenience
            var reportIds = new (byte Id, int Length)[]
            {
                (0x15, BT_OUTPUT_REPORT_0x15_LENGTH),
                (0x11, BT_OUTPUT_REPORT_0x11_LENGTH),
            };

            byte finalReport = 0x00;
            foreach(var element in reportIds)
            {
                int len = element.Length;
                byte[] outputBuffer = new byte[element.Length];
                outputBuffer[0] = element.Id;
                //outputBuffer[1] = (byte)(0xC0 | 0x04);
                outputBuffer[2] = 0xA0;

                // Need to calculate and populate CRC-32 data so controller will accept the report
                uint calcCrc32 = ~Crc32Algorithm.Compute(outputBTCrc32Head);
                calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref outputBuffer, 0, len - 4);
                outputBuffer[len - 4] = (byte)calcCrc32;
                outputBuffer[len - 3] = (byte)(calcCrc32 >> 8);
                outputBuffer[len - 2] = (byte)(calcCrc32 >> 16);
                outputBuffer[len - 1] = (byte)(calcCrc32 >> 24);

                if (WriteOutput(outputBuffer))
                {
                    finalReport = element.Id;
                    knownGoodBTOutputReportType = element.Id;
                    outputReport = new byte[len];
                    outReportBuffer = new byte[len];
                    break;
                }
            }

            if (finalReport == 0x00)
            {
                ModifyFeatureSetFlag(VidPidFeatureSet.NoOutputData, true);
            }
        }

        protected const int DS4_FEATURE_REPORT_5_LEN = 41;
        protected const int DS4_FEATURE_REPORT_5_CRC32_POS = DS4_FEATURE_REPORT_5_LEN - 4;
        public virtual void RefreshCalibration()
        {
            byte[] calibration = new byte[41];
            calibration[0] = conType == ConnectionType.BT ? (byte)0x05 : (byte)0x02;

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

                sixAxis.setCalibrationData(ref calibration, conType == ConnectionType.USB);

                if (hDevice.Attributes.ProductId == 0x5C4 && hDevice.Attributes.VendorId == 0x054C &&
                    sixAxis.fixupInvertedGyroAxis())
                    AppLogger.LogToGui($"Automatically fixed inverted YAW gyro axis in DS4 v.1 BT gamepad ({Mac.ToString()})", false);
            }
            else
            {
                hDevice.readFeatureData(calibration);
                sixAxis.setCalibrationData(ref calibration, conType == ConnectionType.USB);
            }
        }

        public virtual void StartUpdate()
        {
            this.inputReportErrorCount = 0;

            if (ds4Input == null)
            {
                if (conType != ConnectionType.BT)
                {
                    ds4Output = new Thread(OutReportCopy);
                    ds4Output.Priority = ThreadPriority.Normal;
                    ds4Output.Name = "DS4 Arr Copy thread: " + Mac;
                    ds4Output.IsBackground = true;
                    ds4Output.Start();
                }

                ds4Input = new Thread(performDs4Input);
                ds4Input.Priority = ThreadPriority.AboveNormal;
                ds4Input.Name = "DS4 Input thread: " + Mac;
                ds4Input.IsBackground = true;
                ds4Input.Start();
            }
            else
            {
                Debug.WriteLine("Thread already running for DS4: " + Mac);
            }
        }

        public virtual void StopUpdate()
        {
            if (ds4Input != null &&
                ds4Input.IsAlive && !ds4Input.ThreadState.HasFlag(System.Threading.ThreadState.Stopped) &&
                !ds4Input.ThreadState.HasFlag(System.Threading.ThreadState.AbortRequested))
            {
                try
                {
                    exitInputThread = true;
                    //ds4Input.Interrupt();
                    if (!abortInputThread)
                    {
                        hDevice.CancelIO();
                        ds4Input.Join();
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
            }

            ResetBluetoothControllerClock();
            StopOutputUpdate();
        }

        private void ResetBluetoothControllerClock()
        {
            timeStampInit = false;
            timeStampPrevious = 0;
            bluetoothControllerClock.Reset();
        }

        protected virtual void StopOutputUpdate()
        {
            lock (exitLocker)
            {
                if (ds4Output != null &&
                    ds4Output.IsAlive && !ds4Output.ThreadState.HasFlag(System.Threading.ThreadState.Stopped) &&
                    !ds4Output.ThreadState.HasFlag(System.Threading.ThreadState.AbortRequested))
                {
                    try
                    {
                        exitOutputThread = true;
                        ds4Output.Interrupt();
                        ds4Output.Join();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                }
            }
        }

        protected bool WriteOutput(byte[] outputBuffer)
        {
            lock (bluetoothOutputWriteLock)
            {
                if (conType == ConnectionType.BT)
                {
                    if (IsGenuineBluetoothDualShock4())
                        return hDevice.WriteOutputReportViaSharedOverlapped(
                            outputBuffer, READ_STREAM_TIMEOUT);

                    if (nativeOptionsStore != null && nativeOptionsStore.IsCopyCat)
                        return hDevice.WriteOutputReportViaInterrupt(outputBuffer, READ_STREAM_TIMEOUT);

                    return hDevice.WriteOutputReportViaControl(outputBuffer);
                }
                else
                {
                    return hDevice.WriteOutputReportViaInterrupt(outputBuffer, READ_STREAM_TIMEOUT);
                }
            }
        }

        protected bool writeOutput()
        {
            lock (bluetoothOutputWriteLock)
            {
                if (conType == ConnectionType.BT)
                {
                    if (IsGenuineBluetoothDualShock4())
                        return hDevice.WriteOutputReportViaSharedOverlapped(
                            outputReport, READ_STREAM_TIMEOUT);

                    if (nativeOptionsStore != null && nativeOptionsStore.IsCopyCat)
                        return hDevice.WriteOutputReportViaInterrupt(outputReport, READ_STREAM_TIMEOUT);

                    return hDevice.WriteOutputReportViaControl(outputReport);
                }
                else
                {
                    return hDevice.WriteOutputReportViaInterrupt(outReportBuffer,
                        READ_STREAM_TIMEOUT);
                }
            }
        }

        public bool SetBluetoothAudioStreaming(bool speakerEnabled, bool microphoneEnabled,
            byte speakerVolume, byte headphoneVolume, byte microphoneVolume)
        {
            return UpdateBluetoothAudioStreaming(speakerEnabled,
                microphoneEnabled, speakerVolume, headphoneVolume,
                microphoneVolume, null);
        }

        /// <summary>
        /// Applies profile audio settings without disarming an already-owned
        /// speaker lane. A newly requested speaker remains disabled until its
        /// passthrough worker has opened the dedicated realtime transport.
        /// </summary>
        internal bool ConfigureBluetoothAudioForProfile(bool speakerConfigured,
            bool microphoneEnabled, byte speakerVolume, byte headphoneVolume,
            byte microphoneVolume)
        {
            return UpdateBluetoothAudioStreaming(
                speakerConfigured ? null : false,
                microphoneEnabled, speakerVolume, headphoneVolume,
                microphoneVolume, null);
        }

        public bool SetDualShock4BluetoothMicrophoneStreaming(bool enabled)
        {
            return UpdateBluetoothAudioStreaming(null, enabled,
                null, null, null, WriteBluetoothAudioControlReport);
        }

        public bool SetDualShock4BluetoothSpeakerStreaming(bool enabled,
            byte speakerVolume, bool flushControlReport)
        {
            return UpdateBluetoothAudioStreaming(enabled, null,
                speakerVolume, null, null,
                flushControlReport ? WriteBluetoothAudioControlReport : null);
        }

        internal bool SetDualShock4BluetoothSpeakerStreaming(bool enabled,
            byte speakerVolume, Func<byte[], bool> controlReportWriter)
        {
            if (controlReportWriter == null)
            {
                throw new ArgumentNullException(nameof(controlReportWriter));
            }

            return UpdateBluetoothAudioStreaming(enabled, null,
                speakerVolume, null, null, controlReportWriter);
        }

        private bool UpdateBluetoothAudioStreaming(bool? speakerEnabled,
            bool? microphoneEnabled, byte? speakerVolume,
            byte? headphoneVolume, byte? microphoneVolume,
            Func<byte[], bool> controlReportWriter)
        {
            if (!IsGenuineBluetoothDualShock4())
            {
                LastBluetoothAudioWriteStatus =
                    "Bluetooth audio requires a physical Sony DualShock 4.";
                return false;
            }

            byte? scaledSpeakerVolume = speakerVolume.HasValue ?
                ScaleAudioVolume(speakerVolume.Value, 0x4F) : null;
            byte? scaledHeadphoneVolume = headphoneVolume.HasValue ?
                ScaleAudioVolume(headphoneVolume.Value, 0x4F) : null;
            byte? scaledMicrophoneVolume = microphoneVolume.HasValue ?
                ScaleAudioVolume(microphoneVolume.Value, 0x40) : null;

            return bluetoothAudioState.Update(speakerEnabled,
                microphoneEnabled, scaledSpeakerVolume,
                scaledHeadphoneVolume, scaledMicrophoneVolume, state =>
                {
                    if (controlReportWriter == null)
                    {
                        LastBluetoothAudioWriteStatus =
                            state.SpeakerEnabled || state.MicrophoneEnabled ?
                            "control report queued" : "disabled";
                        return true;
                    }

                    byte[] report = CreateDualShock4BluetoothAudioControlReport(
                        state);
                    bool written = controlReportWriter(report);
                    LastBluetoothAudioWriteStatus = written ?
                        "audio control report written" :
                        "audio control report write failed";
                    if (!written)
                    {
                        BluetoothAudioWriteFailures++;
                    }
                    return written;
                });
        }

        private bool WriteBluetoothAudioControlReport(byte[] report)
        {
            Func<byte[], bool> audioLaneWriter;
            lock (bluetoothAudioControlLaneLock)
            {
                audioLaneWriter = bluetoothAudioControlLaneWriter;
            }

            if (audioLaneWriter != null)
            {
                // The dedicated speaker lane drains older 0x12 reports before
                // presenting this 0x11 mode change. A late A0 report can no
                // longer undo a completed A1 microphone arm.
                return audioLaneWriter(report);
            }

            bool written;

            lock (bluetoothOutputWriteLock)
            {
                // DS4 Bluetooth audio control is an interrupt OUT report. The
                // working MeasuredTransport and SynchronousDs4Audio transports both send it
                // with overlapped WriteFile; HidD_SetOutputReport does not arm
                // the microphone lane on genuine CUH-ZCT2 hardware.
                written = hDevice.WriteOutputReportViaSharedOverlapped(
                    report, READ_STREAM_TIMEOUT);
            }
            return written;
        }

        private byte[] CreateDualShock4BluetoothAudioControlReport(
            DualShock4BluetoothAudioState.Snapshot state)
        {
            return DualShock4BluetoothAudioProtocol.BuildAudioControlReport(
                state.SpeakerEnabled, state.MicrophoneEnabled,
                state.SpeakerVolume, state.HeadphoneVolume,
                state.MicrophoneVolume,
                currentHap.rumbleState.RumbleMotorStrengthRightLightFast,
                currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow,
                currentHap.lightbarState.LightBarColor.red,
                currentHap.lightbarState.LightBarColor.green,
                currentHap.lightbarState.LightBarColor.blue,
                currentHap.lightbarState.LightBarFlashDurationOn,
                currentHap.lightbarState.LightBarFlashDurationOff,
                bluetoothPollRate: (byte)Math.Clamp(btPollRate, 0, 16));
        }

        private bool WriteDualShock4BluetoothEffectThroughAudioLane(
            DualShock4BluetoothAudioState.Snapshot state)
        {
            Func<byte[], bool> audioLaneWriter;
            lock (bluetoothAudioControlLaneLock)
            {
                audioLaneWriter = bluetoothAudioControlLaneWriter;
            }

            if (audioLaneWriter == null)
            {
                return false;
            }

            // A conventional DS4 Bluetooth effect report has no audio mode in
            // byte 2. Sending it after the A0/A1 arm report silently replaces
            // the controller's audio-plane state and makes valid SBC reports
            // disappear at the hardware decoder. Rebuild the same rumble,
            // lightbar, flash, and volume state as an A0/A1 F3 control report
            // and serialize it through the speaker lane instead.
            return audioLaneWriter(
                CreateDualShock4BluetoothAudioControlReport(state));
        }

        public bool WriteBluetoothAudioOutputReport(byte[] report)
        {
            if (!IsGenuineBluetoothDualShock4() || report == null ||
                report.Length < 8 ||
                DualShock4BluetoothAudioProtocol.GetInputReportLength(report[0]) != report.Length)
            {
                LastBluetoothAudioWriteStatus = "rejected invalid DS4 Bluetooth audio report";
                return false;
            }

            bool written;
            lock (bluetoothOutputWriteLock)
            {
                written = hDevice.WriteOutputReportViaInterrupt(report,
                    READ_STREAM_TIMEOUT);
            }

            if (written)
            {
                BluetoothAudioReportsWritten++;
                LastBluetoothAudioWriteStatus = $"report 0x{report[0]:X2} written";
            }
            else
            {
                BluetoothAudioWriteFailures++;
                LastBluetoothAudioWriteStatus =
                    $"report 0x{report[0]:X2} write failed";
            }

            return written;
        }

        private bool IsGenuineBluetoothDualShock4()
        {
            if (conType != ConnectionType.BT || hDevice?.Attributes == null ||
                hDevice.Attributes.VendorId != DS4Devices.SONY_VID ||
                nativeOptionsStore?.IsCopyCat == true)
            {
                return false;
            }

            int productId = hDevice.Attributes.ProductId;
            return productId == 0x05C4 || productId == 0x09CC;
        }

        private static byte ScaleAudioVolume(byte volume, int maximum)
        {
            return (byte)Math.Clamp((volume * maximum + 127) / 255, 0, maximum);
        }

        private readonly Stopwatch rumbleAutostopTimer = new Stopwatch(); // Autostop timer to stop rumble motors if those are stuck in a rumble state

        private byte outputPendCount = 0;
        private const int OUTPUT_MIN_COUNT_BT = 3;
        private byte[] outputBTCrc32Head = new byte[] { 0xA2 };
        protected readonly Stopwatch standbySw = new Stopwatch();

        /** Is the device alive and receiving valid sensor input reports? */
        public virtual bool IsAlive()
        {
            return priorInputReport30 != 0xff;
        }

        private byte priorInputReport30 = 0xff;

        protected bool synced = false;
        public bool Synced
        {
            get { return synced; }
            set
            {
                if (synced != value)
                {
                    synced = value;
                }
            }
        }

        public bool isSynced()
        {
            return synced;
        }

        /// <summary>
        /// Used to tell the input thread to temporarily stop firing the
        /// Report event. Keeps linked methods from being executed
        /// </summary>
        protected bool fireReport = true;
        public bool FireReport
        {
            get => fireReport;
            set
            {
                fireReport = value;
            }
        }

        private long latencyBits;
        public double Latency
        {
            get => BitConverter.Int64BitsToDouble(
                Interlocked.Read(ref latencyBits));
            protected set => Interlocked.Exchange(ref latencyBits,
                BitConverter.DoubleToInt64Bits(value));
        }
        public string error;
        public bool firstReport = true;
        public bool oldCharging = false;
        protected DateTime utcNow = DateTime.UtcNow;
        protected bool ds4InactiveFrame = true;
        protected bool idleInput = true;

        bool timeStampInit = false;
        uint timeStampPrevious = 0;
        uint deltaTimeCurrent = 0;


        protected const int BT_INPUT_REPORT_CRC32_POS = 74; //last 4 bytes of the 78-sized input report are crc32
        public const uint DefaultPolynomial = 0xedb88320u;
        private const int CRC32_NUM_ATTEMPTS = 10;
        private const int SONYWA_FEATURE_REPORT_LENGTH = 64;
        protected uint HamSeed = 2351727372;

        private void HandleBluetoothMicrophoneSbcFrame(ushort sequence,
            byte[] frame)
        {
            DualShock4BluetoothMicrophoneFramesReceived++;
            try
            {
                BluetoothMicrophoneSbcFrameReceived?.Invoke(this, sequence,
                    frame);
            }
            catch (Exception ex)
            {
                LastBluetoothAudioWriteStatus =
                    $"microphone frame handler failed: {ex.Message}";
            }
        }

        protected unsafe void performDs4Input()
        {
            unchecked
            {
                // The sensor timestamp restarts with the physical connection.
                // Never carry a clock fit or a wrap anchor across reconnects.
                ResetBluetoothControllerClock();
                Debouncer = SetupDebouncer();
                firstActive = DateTime.UtcNow;
                // Preserve the HIDCLASS queue depth used by the verified clean
                // one-frame 0x12 DS4Windows capture. This does not change the
                // controller's one-millisecond input interval; it prevents a
                // short host scheduling stall from exhausting the read queue
                // while the independent audio handle continues submitting.
                NativeMethods.HidD_SetNumInputBuffers(
                    hDevice.SafeReadHandle.DangerousGetHandle(), 64);
                Queue<double> latencyQueue = new Queue<double>(21); // Set capacity at max + 1 to avoid any resizing
                int tempLatencyCount = 0;
                long oldtime = 0;
                string currerror = string.Empty;
                long curtime = 0;
                long testelapsed = 0;
                ds4InactiveFrame = true;
                idleInput = true;
                bool syncWriteReport = true;
                bool forceWrite = false;

                int maxBatteryValue = 0;
                int tempBattery = 0;
                bool tempCharging = charging;
                uint tempStamp = 0;
                double elapsedDeltaTime = 0.0;
                uint tempDelta = 0;
                byte tempByte = 0;
                double latencySum = 0.0;

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

                    // Keep fractional milliseconds so the observed input
                    // cadence remains accurate above 500 Hz.
                    latencySum += lastTimeElapsedDouble;
                    latencyQueue.Enqueue(lastTimeElapsedDouble);
                    tempLatencyCount++;

                    //Latency = latencyQueue.Average();
                    Latency = latencySum / (double)tempLatencyCount;

                    readWaitEv.Set();

                    // Sony DS4 and compatible gamepads send data packets with 0x11 type code in BT mode.
                    // Will no longer support any third party fake DS4 that does not behave according to official DS4 specs
                    //if (conType == ConnectionType.BT)
                    if (conType == ConnectionType.BT && (this.featureSet & VidPidFeatureSet.OnlyInputData0x01) == 0)
                    {
                        //HidDevice.ReadStatus res = hDevice.ReadFile(btInputReport);
                        //HidDevice.ReadStatus res = hDevice.ReadAsyncWithFileStream(btInputReport, READ_STREAM_TIMEOUT);
                        // Match MeasuredTransport/hidapi: one outstanding shared,
                        // overlapped read on the primary input handle. The
                        // former two-deep IOCP pump stopped receiving when DS4
                        // firmware entered its Bluetooth audio report mode.
                        HidDevice.ReadStatus res = hDevice.ReadFile(
                            btInputReport, READ_STREAM_TIMEOUT);
                        if (res == HidDevice.ReadStatus.Success)
                        {
                            int bluetoothReportLength =
                                DualShock4BluetoothAudioProtocol.GetInputReportLength(
                                    btInputReport[0]);
                            bool validBluetoothReport = bluetoothReportLength > 0 &&
                                DualShock4BluetoothAudioProtocol.ValidateInputReportCrc(
                                    btInputReport, bluetoothReportLength);
                            if (!validBluetoothReport)
                            {
                                cState.PacketCounter = pState.PacketCounter + 1;
                                if (this.inputReportErrorCount >= CRC32_NUM_ATTEMPTS)
                                {
                                    AppLogger.LogToGui($"{Mac} failed CRC-32 checks {CRC32_NUM_ATTEMPTS} times. Disconnecting", false);

                                    readWaitEv.Reset();
                                    sendOutputReport(true, true);
                                    StopOutputUpdate();
                                    isDisconnecting = true;
                                    ResetBluetoothControllerClock();
                                    Removal?.Invoke(this, EventArgs.Empty);

                                    return;
                                }

                                this.inputReportErrorCount++;
                                readWaitEv.Reset();
                                continue;
                            }

                            this.inputReportErrorCount = 0;
                            Interlocked.Exchange(ref lastBluetoothInputReportTick,
                                Environment.TickCount64);
                            if (BluetoothMicrophoneStreaming)
                            {
                                DualShock4BluetoothAudioProtocol.ExtractMicrophoneSbcFrames(
                                    btInputReport, bluetoothReportLength,
                                    HandleBluetoothMicrophoneSbcFrame);
                            }

                            if (!DualShock4BluetoothAudioProtocol.HasHidState(btInputReport))
                            {
                                readWaitEv.Reset();
                                continue;
                            }

                            //Array.Copy(btInputReport, 2, inputReport, 0, inputReport.Length);
                            fixed (byte* byteP = &btInputReport[2], imp = inputReport)
                            {
                                for (int j = 0; j < BT_INPUT_REPORT_LENGTH - 2; j++)
                                {
                                    imp[j] = byteP[j];
                                }
                            }
                        }
                        else
                        {
                            if (res == HidDevice.ReadStatus.WaitTimedOut)
                            {
                                long lastInputTick = Interlocked.Read(
                                    ref lastBluetoothInputReportTick);
                                long lastInputAge = lastInputTick == 0 ? -1 :
                                    Math.Max(0, Environment.TickCount64 -
                                        lastInputTick);

                                // A visible Game Bar can briefly take the
                                // Windows gaming/HID stack through a discovery
                                // transition. The physical DS4 remains a
                                // present PnP device during that transition,
                                // but no input IRP completes for one timeout
                                // interval. Treating that recoverable drought
                                // as removal tore down both VIIPER devices and
                                // immediately rediscovered the same controller,
                                // producing an unplug/replug loop and stranding
                                // its persistent audio endpoint. Preserve the
                                // existing owner and simply issue the next read
                                // while Windows still reports the exact HID
                                // interface as present. A real disconnect still
                                // follows the original removal path as soon as
                                // the interface leaves the present-device set.
                                bool interfaceStillPresent = false;
                                try
                                {
                                    interfaceStillPresent = hDevice.IsConnected;
                                }
                                catch
                                {
                                    // Enumeration failure is not proof that a
                                    // disconnected device is still usable.
                                }

                                if (ShouldRetryBluetoothInputAfterTimeout(
                                        interfaceStillPresent))
                                {
                                    AppLogger.LogToGui(
                                        $"{Mac} Bluetooth input paused for {lastInputAge} ms while its HID interface remains present; preserving the controller and retrying the read.",
                                        true);
                                    readWaitEv.Reset();
                                    continue;
                                }

                                AppLogger.LogToGui(Mac.ToString() +
                                    " disconnected due to timeout" +
                                    $" (lastValidInputAgeMs={lastInputAge}, " +
                                    $"speaker={BluetoothSpeakerStreaming}, " +
                                    $"microphone={BluetoothMicrophoneStreaming}, " +
                                    $"effectWrites={BluetoothEffectReportsDuringAudio}, " +
                                    $"effectDeferred={BluetoothEffectReportsDeferredDuringAudio})",
                                    true);
                            }
                            else
                            {
                                int winError = Marshal.GetLastWin32Error();
                                Console.WriteLine($"{Mac} {DateTime.UtcNow.ToString("o")}> disconnect due to read failure: {winError.ToString("x8")}");
                                //Log.LogToGui(Mac.ToString() + " disconnected due to read failure: " + winError, true);
                                AppLogger.LogToGui(Mac.ToString() + " disconnected due to read failure: " + winError, true);
                            }

                            readWaitEv.Reset();
                            sendOutputReport(true, true); // Kick Windows into noticing the disconnection.
                            StopOutputUpdate();
                            isDisconnecting = true;
                            ResetBluetoothControllerClock();
                            Removal?.Invoke(this, EventArgs.Empty);

                            return;
                        }
                    }
                    else
                    {
                        //HidDevice.ReadStatus res = hDevice.ReadFile(inputReport);
                        //Array.Clear(inputReport, 0, inputReport.Length);
                        //HidDevice.ReadStatus res = hDevice.ReadAsyncWithFileStream(inputReport, READ_STREAM_TIMEOUT);
                        HidDevice.ReadStatus res = hDevice.ReadFile(inputReport,
                            conType == ConnectionType.BT ? READ_STREAM_TIMEOUT : uint.MaxValue);
                        if (res != HidDevice.ReadStatus.Success)
                        {
                            if (res == HidDevice.ReadStatus.WaitTimedOut)
                            {
                                AppLogger.LogToGui(Mac.ToString() + " disconnected due to timeout", true);
                            }
                            else
                            {
                                int winError = Marshal.GetLastWin32Error();
                                Console.WriteLine($"{Mac} {DateTime.UtcNow.ToString("o")}> disconnect due to read failure: {winError.ToString("x8")}");
                                //Log.LogToGui(Mac.ToString() + " disconnected due to read failure: " + winError, true);
                            }

                            readWaitEv.Reset();
                            StopOutputUpdate();
                            isDisconnecting = true;
                            ResetBluetoothControllerClock();
                            Removal?.Invoke(this, EventArgs.Empty);

                            return;
                        }
                    }

                    readWaitEv.Wait();
                    readWaitEv.Reset();

                    curtime = Stopwatch.GetTimestamp();
                    testelapsed = curtime - oldtime;
                    lastTimeElapsedDouble = testelapsed * (1.0 / Stopwatch.Frequency) * 1000.0;
                    lastTimeElapsed = (long)lastTimeElapsedDouble;
                    oldtime = curtime;

                    // Not going to do featureSet check anymore
                    if (conType == ConnectionType.BT &&
                        !DualShock4BluetoothAudioProtocol.HasHidState(btInputReport) &&
                        (this.featureSet & VidPidFeatureSet.OnlyInputData0x01) == 0)
                    {
                        //Received incorrect report, skip it
                        continue;
                    }

                    utcNow = DateTime.UtcNow; // timestamp with UTC in case system time zone changes

                    cState.PacketCounter = pState.PacketCounter + 1;
                    cState.ReportTimeStamp = utcNow;
                    cState.LX = inputReport[1];
                    cState.LY = inputReport[2];
                    cState.RX = inputReport[3];
                    cState.RY = inputReport[4];
                    cState.L2 = inputReport[8];
                    cState.R2 = inputReport[9];
                    cState.L2Raw = cState.L2;
                    cState.R2Raw = cState.R2;

                    tempByte = inputReport[5];
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

                    tempByte = inputReport[6];
                    cState.R3 = (tempByte & (1 << 7)) != 0;
                    cState.L3 = (tempByte & (1 << 6)) != 0;
                    cState.Options = (tempByte & (1 << 5)) != 0;
                    cState.Share = (tempByte & (1 << 4)) != 0;
                    cState.R2Btn = (inputReport[6] & (1 << 3)) != 0;
                    cState.L2Btn = (inputReport[6] & (1 << 2)) != 0;
                    cState.R1 = (tempByte & (1 << 1)) != 0;
                    cState.L1 = (tempByte & (1 << 0)) != 0;

                    tempByte = inputReport[7];
                    cState.PS = (tempByte & (1 << 0)) != 0;
                    cState.TouchButton = (tempByte & 0x02) != 0;

                    cState.OutputTouchButton = cState.TouchButton;
                    cState.FrameCounter = (byte)(tempByte >> 2);

                    if ((this.featureSet & VidPidFeatureSet.NoBatteryReading) == 0)
                    {
                        tempByte = inputReport[30];
                        tempCharging = (tempByte & 0x10) != 0;
                        if (tempCharging != charging)
                        {
                            charging = tempCharging;
                            ChargingChanged?.Invoke(this, EventArgs.Empty);
                        }

                        maxBatteryValue = charging ? BATTERY_MAX_USB : BATTERY_MAX;
                        tempBattery = (tempByte & 0x0f) * 100 / maxBatteryValue;
                        tempBattery = Math.Min(tempBattery, 100);
                        if (tempBattery != battery)
                        {
                            battery = tempBattery;
                            BatteryChanged?.Invoke(this, EventArgs.Empty);
                        }

                        cState.Battery = (byte)battery;
                        //Debug.WriteLine("CURRENT BATTERY: " + (inputReport[30] & 0x0f) + " | " + tempBattery + " | " + battery);
                        if (tempByte != priorInputReport30)
                        {
                            priorInputReport30 = tempByte;
                            //Debug.WriteLine(MacAddress.ToString() + " " + System.DateTime.UtcNow.ToString("o") + "> power subsystem octet: 0x" + inputReport[30].ToString("x02"));
                        }
                    }
                    else
                    {
                        // Some gamepads don't send battery values in DS4 compatible data fields, so use dummy 99% value to avoid constant low battery warnings
                        priorInputReport30 = 0x0F;
                        battery = 99;
                        cState.Battery = 99;
                    }

                    tempStamp = (uint)((ushort)(inputReport[11] << 8) | inputReport[10]);
                    if (IsGenuineBluetoothDualShock4())
                    {
                        bluetoothControllerClock.Observe((ushort)tempStamp,
                            curtime, Stopwatch.Frequency);
                    }
                    if (timeStampInit == false)
                    {
                        timeStampInit = true;
                        deltaTimeCurrent = tempStamp * 16u / 3u;
                    }
                    else if (timeStampPrevious > tempStamp)
                    {
                        tempDelta = ushort.MaxValue - timeStampPrevious + tempStamp + 1u;
                        deltaTimeCurrent = tempDelta * 16u / 3u;
                    }
                    else
                    {
                        tempDelta = tempStamp - timeStampPrevious;
                        deltaTimeCurrent = tempDelta * 16u / 3u;
                    }

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

                    cState.elapsedTime = elapsedDeltaTime;
                    cState.ds4Timestamp = (ushort)tempStamp;
                    timeStampPrevious = tempStamp;

                    //Simpler touch storing
                    cState.TrackPadTouch0.RawTrackingNum = inputReport[35];
                    cState.TrackPadTouch0.Id = (byte)(inputReport[35] & 0x7f);
                    cState.TrackPadTouch0.IsActive = (inputReport[35] & 0x80) == 0;
                    cState.TrackPadTouch0.X = (short)(((ushort)(inputReport[37] & 0x0f) << 8) | (ushort)(inputReport[36]));
                    cState.TrackPadTouch0.Y = (short)(((ushort)(inputReport[38]) << 4) | ((ushort)(inputReport[37] & 0xf0) >> 4));

                    cState.TrackPadTouch1.RawTrackingNum = inputReport[39];
                    cState.TrackPadTouch1.Id = (byte)(inputReport[39] & 0x7f);
                    cState.TrackPadTouch1.IsActive = (inputReport[39] & 0x80) == 0;
                    cState.TrackPadTouch1.X = (short)(((ushort)(inputReport[41] & 0x0f) << 8) | (ushort)(inputReport[40]));
                    cState.TrackPadTouch1.Y = (short)(((ushort)(inputReport[42]) << 4) | ((ushort)(inputReport[41] & 0xf0) >> 4));

                    if (conType == ConnectionType.SONYWA)
                    {
                        bool controllerSynced = inputReport[31] == 0;
                        if (controllerSynced != synced)
                        {
                            runCalib = synced = controllerSynced;
                            SyncChange?.Invoke(this, EventArgs.Empty);
                            if (synced)
                            {
                                forceWrite = true;
                                sixAxis.ResetContinuousCalibration();
                            }
                            else
                            {
                                standbySw.Reset();
                                sixAxis.StopContinuousCalibration();
                            }
                        }
                    }

                    // XXX DS4State mapping needs fixup, turn touches into an array[4] of structs.  And include the touchpad details there instead.
                    try
                    {
                        // Only care if one touch packet is detected. Other touch packets
                        // don't seem to contain relevant data. ds4drv does not use them either.
                        for (int touches = Math.Max((int)(inputReport[-1 + DS4Touchpad.DS4_TOUCHPAD_DATA_OFFSET - 1]), 1), touchOffset = 0; touches > 0; touches--, touchOffset += 9)
                        //for (int touches = inputReport[-1 + DS4Touchpad.TOUCHPAD_DATA_OFFSET - 1], touchOffset = 0; touches > 0; touches--, touchOffset += 9)
                        {
                            cState.TouchPacketCounter = inputReport[-1 + DS4Touchpad.DS4_TOUCHPAD_DATA_OFFSET + touchOffset];
                            cState.Touch1 = (inputReport[0 + DS4Touchpad.DS4_TOUCHPAD_DATA_OFFSET + touchOffset] >> 7) != 0 ? false : true; // finger 1 detected
                            cState.Touch1Identifier = (byte)(inputReport[0 + DS4Touchpad.DS4_TOUCHPAD_DATA_OFFSET + touchOffset] & 0x7f);
                            cState.Touch2 = (inputReport[4 + DS4Touchpad.DS4_TOUCHPAD_DATA_OFFSET + touchOffset] >> 7) != 0 ? false : true; // finger 2 detected
                            cState.Touch2Identifier = (byte)(inputReport[4 + DS4Touchpad.DS4_TOUCHPAD_DATA_OFFSET + touchOffset] & 0x7f);
                            cState.Touch1Finger = cState.Touch1 || cState.Touch2; // >= 1 touch detected
                            cState.Touch2Fingers = cState.Touch1 && cState.Touch2; // 2 touches detected
                            int touchX = (((inputReport[2 + DS4Touchpad.DS4_TOUCHPAD_DATA_OFFSET + touchOffset] & 0xF) << 8) | inputReport[1 + DS4Touchpad.DS4_TOUCHPAD_DATA_OFFSET + touchOffset]);
                            cState.TouchLeft = touchX >= DS4Touchpad.RESOLUTION_X_MAX * 2 / 5 ? false : true;
                            cState.TouchRight = touchX < DS4Touchpad.RESOLUTION_X_MAX * 2 / 5 ? false : true;
                            // Even when idling there is still a touch packet indicating no touch 1 or 2
                            if (synced)
                            {
                                touchpad.handleTouchpad(inputReport, cState, DS4Touchpad.DS4_TOUCHPAD_DATA_OFFSET, touchOffset);
                            }
                        }
                    }
                    catch (Exception ex) { currerror = $"Touchpad: {ex.Message}"; }

                    // Store Gyro and Accel values
                    //Array.Copy(inputReport, 13, gyro, 0, 6);
                    //Array.Copy(inputReport, 19, accel, 0, 6);

                    // Store Gyro and Accel values. Use pointers here as it seems faster than using Array.Copy
                    fixed (byte* pbInput = &inputReport[13], pbGyro = gyro, pbAccel = accel)
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
                        Debug.Write(MacAddress.ToString() + " " + System.DateTime.UtcNow.ToString("o") + ">");
                        for (int i = 0; i < inputReport.Length; i++)
                        {
                            Debug.Write(" " + inputReport[i].ToString("x2"));
                        }

                        Console.WriteLine();
                    }
                    */

                    ds4InactiveFrame = cState.FrameCounter == pState.FrameCounter;
                    if (!ds4InactiveFrame)
                    {
                        isRemoved = false;
                    }

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
                                    timeoutExecuted = true;
                                    return; // all done
                                }
                            }
                            else if (conType == ConnectionType.SONYWA)
                            {
                                DisconnectDongle();
                            }
                        }
                    }

                    if (fireReport && Report != null)
                        Report(this, EventArgs.Empty);

                    sendOutputReport(syncWriteReport, forceWrite);
                    forceWrite = false;

                    if (!string.IsNullOrEmpty(currerror))
                        error = currerror;
                    else if (!string.IsNullOrEmpty(error))
                        error = string.Empty;

                    cState.CopyTo(pState);

                    if (hasInputEvts)
                    {
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
                }
            }

            timeoutExecuted = true;
        }

        protected Debouncer SetupDebouncer()
        {
            var debouncingMs = TimeSpan.FromMilliseconds(Global.DebouncingMs[deviceSlotNumber]);
            Debouncer debouncer = new(debouncingMs);
            debouncer.AddDebouncer(nameof(DS4State.Cross));
            debouncer.AddDebouncer(nameof(DS4State.Triangle));
            debouncer.AddDebouncer(nameof(DS4State.Circle));
            debouncer.AddDebouncer(nameof(DS4State.Square));
            debouncer.AddDebouncer(nameof(DS4State.R3));
            debouncer.AddDebouncer(nameof(DS4State.L3));
            debouncer.AddDebouncer(nameof(DS4State.Options));
            debouncer.AddDebouncer(nameof(DS4State.Share));
            debouncer.AddDebouncer(nameof(DS4State.R2Btn));
            debouncer.AddDebouncer(nameof(DS4State.L2Btn));
            debouncer.AddDebouncer(nameof(DS4State.R1));
            debouncer.AddDebouncer(nameof(DS4State.L1));
            debouncer.AddDebouncer(nameof(DS4State.PS));
            debouncer.AddDebouncer(nameof(DS4State.TouchButton));
            debouncer.AddDebouncer(nameof(DS4State.Capture));
            debouncer.AddDebouncer(nameof(DS4State.SideL));
            debouncer.AddDebouncer(nameof(DS4State.SideR));
            debouncer.AddDebouncer(nameof(DS4State.DpadUp));
            debouncer.AddDebouncer(nameof(DS4State.DpadDown));
            debouncer.AddDebouncer(nameof(DS4State.DpadLeft));
            debouncer.AddDebouncer(nameof(DS4State.DpadRight));
            Global.DebouncingMsChanged += (_, _) =>
            {
                debouncer.SetDuration(TimeSpan.FromMilliseconds(Global.DebouncingMs[deviceSlotNumber]));
            };
            return debouncer;
        }

        private unsafe void PrepareOutputReportInner(ref bool change,
            ref bool haptime,
            DualShock4BluetoothAudioState.Snapshot bluetoothAudio)
        {
            bool usingBT = conType == ConnectionType.BT;

            if (usingBT && (this.featureSet & VidPidFeatureSet.OnlyOutputData0x05) == 0)
            {
                outReportBuffer[0] = knownGoodBTOutputReportType;
                //outReportBuffer[0] = 0x15;
                //outReportBuffer[1] = (byte)(0x80 | btPollRate); // input report rate
                byte effectivePollRate = GetBluetoothOutputPollRate(
                    btPollRate, Volatile.Read(ref
                        bluetoothAudioDefaultInputIntervalOverride) != 0);
                outReportBuffer[1] = (byte)(0xC0 | effectivePollRate);
                // The upper transport bits are mandatory on ordinary DS4 BT
                // effects. Preserve mode 1 while microphone streaming is
                // armed; sending a later A0 effect would otherwise switch the
                // controller back to speaker-only input. Mode 2 (A2), used by
                // the speaker-only references, starves ordinary HID input on
                // genuine CUH-ZCT2 hardware.
                outReportBuffer[2] = bluetoothAudio.MicrophoneEnabled ?
                    (byte)0xA1 : (byte)0xA0;

                // Headphone volume L (0x10), Headphone volume R (0x20), Mic volume (0x40), Speaker volume (0x80)
                // enable rumble (0x01), lightbar (0x02), flash (0x04). Default: 0x07
                outReportBuffer[3] = outputFeaturesByte;
                outReportBuffer[4] = IsGenuineBluetoothDualShock4() ?
                    (byte)0x00 : (byte)0x04;

                outReportBuffer[6] = currentHap.rumbleState.RumbleMotorStrengthRightLightFast; // fast motor
                outReportBuffer[7] = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow; // slow motor
                outReportBuffer[8] = currentHap.lightbarState.LightBarColor.red; // red
                outReportBuffer[9] = currentHap.lightbarState.LightBarColor.green; // green
                outReportBuffer[10] = currentHap.lightbarState.LightBarColor.blue; // blue
                outReportBuffer[11] = currentHap.lightbarState.LightBarFlashDurationOn; // flash on duration
                outReportBuffer[12] = currentHap.lightbarState.LightBarFlashDurationOff; // flash off duration
                outReportBuffer[21] = 0;
                outReportBuffer[22] = 0;
                outReportBuffer[23] = 0;
                outReportBuffer[24] = 0;

                fixed (byte* byteR = outputReport, byteB = outReportBuffer)
                {
                    for (int i = 0, arlen = BT_OUTPUT_CHANGE_LENGTH; !change && i < arlen; i++)
                        change = byteR[i] != byteB[i];
                }

                change = change || currentHap.dirty;
                /*if (change)
                {
                    Console.WriteLine("CHANGE: {0} {1} {2} {3} {4} {5}", currentHap.LightBarColor.red, currentHap.LightBarColor.green, currentHap.LightBarColor.blue, currentHap.RumbleMotorStrengthRightLightFast, currentHap.RumbleMotorStrengthLeftHeavySlow, DateTime.Now.ToString());
                }
                */

                haptime = haptime || change;
            }
            else
            {
                outReportBuffer[0] = 0x05;
                // Headphone volume L (0x10), Headphone volume R (0x20), Mic volume (0x40), Speaker volume (0x80)
                // enable rumble (0x01), lightbar (0x02), flash (0x04). Default: 0x07
                outReportBuffer[1] = outputFeaturesByte;
                outReportBuffer[2] = nativeOptionsStore?.IsCopyCat == true ?
                    (byte)0x04 : (byte)0x00;
                outReportBuffer[4] = currentHap.rumbleState.RumbleMotorStrengthRightLightFast; // fast motor
                outReportBuffer[5] = currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow; // slow  motor
                outReportBuffer[6] = currentHap.lightbarState.LightBarColor.red; // red
                outReportBuffer[7] = currentHap.lightbarState.LightBarColor.green; // green
                outReportBuffer[8] = currentHap.lightbarState.LightBarColor.blue; // blue
                outReportBuffer[9] = currentHap.lightbarState.LightBarFlashDurationOn; // flash on duration
                outReportBuffer[10] = currentHap.lightbarState.LightBarFlashDurationOff; // flash off duration

                fixed (byte* byteR = outputReport, byteB = outReportBuffer)
                {
                    for (int i = 0, arlen = USB_OUTPUT_CHANGE_LENGTH; !change && i < arlen; i++)
                        change = byteR[i] != byteB[i];
                }

                change = change || currentHap.dirty;
                haptime = haptime || change;
            }
        }

        private void sendOutputReport(bool synchronous, bool force = false, bool quitOutputThreadOnError = true)
        {
            lock (outputReportStateLock)
            {
            bluetoothAudioState.ReadSynchronized(bluetoothAudio =>
            {
            MergeStates();
            //setTestRumble();
            //setHapticState();

            bool quitOutputThread = false;
            bool usingBT = conType == ConnectionType.BT;

            // Some gamepads don't support lightbar and rumble, so no need to write out anything (writeOut always fails, so DS4Windows would accidentally force quit the gamepad connection).
            // If noOutputData featureSet flag is set then don't try to write out anything to the gamepad device.
            if ((this.featureSet & VidPidFeatureSet.NoOutputData) != 0)
            {
                if (exitOutputThread == false && (IsRemoving || IsRemoved))
                {
                    // Gamepad disconnecting or disconnected. Signal closing of OutputUpdate thread
                    StopOutputUpdate();
                    exitOutputThread = true;
                }

                return;
            }

            //bool output = outputPendCount > 0, change = force;
            bool change = force;
            // Speaker streaming writes an audio report every 4 ms. A
            // microphone-only stream is inbound, so it still needs an A1
            // effect keepalive before the controller's roughly four-second
            // microphone-mode timeout.
            bool audioKeepsBluetoothAlive = usingBT &&
                bluetoothAudio.SpeakerEnabled;
            long keepAliveInterval = bluetoothAudio.MicrophoneEnabled ?
                1000L : 4000L;
            bool haptime = force || (!audioKeepsBluetoothAlive &&
                standbySw.ElapsedMilliseconds >= keepAliveInterval);

            PrepareOutputReportInner(ref change, ref haptime,
                bluetoothAudio);

            if (haptime && usingBT && bluetoothAudio.SpeakerEnabled && !force)
            {
                long lastEffectTick = Interlocked.Read(
                    ref lastBluetoothEffectReportDuringAudioTick);
                long elapsedMilliseconds = lastEffectTick == 0 ?
                    long.MaxValue : Math.Max(0,
                        Environment.TickCount64 - lastEffectTick);
                if (ShouldDeferBluetoothEffectDuringSpeaker(usingBT,
                    bluetoothAudio.SpeakerEnabled, force, haptime,
                    elapsedMilliseconds))
                {
                    // outReportBuffer retains the newest merged state. Because
                    // outputReport still contains the last transmitted state,
                    // the normal byte comparison keeps this update pending for
                    // the next input tick instead of dropping it.
                    haptime = false;
                    Interlocked.Increment(
                        ref bluetoothEffectReportsDeferredDuringAudio);
                }
            }

            if (rumbleAutostopTimer.IsRunning)
            {
                // Force-stop a potentially stuck virtual-output rumble motor when no new feedback has arrived within the timeout.
                if (rumbleAutostopTimer.ElapsedMilliseconds >= rumbleAutostopTime)
                    setRumble(0, 0);
            }

            if (synchronous)
            {
                if (haptime)
                {
                    if (change)
                    {
                        standbySw.Reset();
                    }
                    else
                        standbySw.Restart();
                    //standbySw.Restart();

                    if (usingBT)
                    {
                        outReportBuffer.CopyTo(outputReport, 0);

                        if ((this.featureSet & VidPidFeatureSet.OnlyOutputData0x05) == 0)
                        {
                            // Need to calculate and populate CRC-32 data so controller will accept the report
                            //int len = outputReport.Length;
                            int len = btOutputPayloadLen;
                            uint calcCrc32 = ~Crc32Algorithm.Compute(outputBTCrc32Head);
                            calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref outputReport, 0, len - 4);
                            outputReport[len - 4] = (byte)calcCrc32;
                            outputReport[len - 3] = (byte)(calcCrc32 >> 8);
                            outputReport[len - 2] = (byte)(calcCrc32 >> 16);
                            outputReport[len - 1] = (byte)(calcCrc32 >> 24);

                            //Console.WriteLine("Write CRC-32 to output report");
                        }
                    }

                    try
                    {
                        bool outputWritten = usingBT &&
                            bluetoothAudio.SpeakerEnabled ?
                            WriteDualShock4BluetoothEffectThroughAudioLane(
                                bluetoothAudio) :
                            writeOutput();
                        if (outputWritten && usingBT &&
                            (bluetoothAudio.SpeakerEnabled ||
                                bluetoothAudio.MicrophoneEnabled))
                        {
                            Interlocked.Increment(
                                ref bluetoothEffectReportsDuringAudio);
                            Interlocked.Exchange(
                                ref lastBluetoothEffectReportDuringAudioTick,
                                Environment.TickCount64);
                        }
                        if (!outputWritten)
                        {
                            if (quitOutputThreadOnError)
                            {
                                int winError = Marshal.GetLastWin32Error();

                                // Logfile notification that the gamepad is force disconnected because of writeOutput failed
                                if (quitOutputThread == false && !isDisconnecting)
                                    AppLogger.LogToGui($"Gamepad data write connection is lost. Disconnecting the gamepad. LastErrorCode={winError}", false);

                                quitOutputThread = true;
                            }
                        }
                    }
                    catch { } // If it's dead already, don't worry about it.

                    if (!usingBT)
                    {
                        lock(outReportBuffer)
                        {
                            Monitor.Pulse(outReportBuffer);
                        }
                    }
                }
            }
            else
            {
                //for (int i = 0, arlen = outputReport.Length; !change && i < arlen; i++)
                //    change = outputReport[i] != outReportBuffer[i];

                if (haptime)
                {
                    if (change)
                    {
                        standbySw.Reset();
                    }

                    Monitor.Pulse(outReportBuffer);
                }
            }

            if (quitOutputThread)
            {
                StopOutputUpdate();
                exitOutputThread = true;
            }

            currentHap.dirty = false;
            });
            }
        }

        // Perform outReportBuffer copy on a separate thread to save
        // time on main input thread
        private void OutReportCopy()
        {
            try
            {
                while (!exitOutputThread)
                {
                    lock (outReportBuffer)
                    {
                        outReportBuffer.CopyTo(outputReport, 0);
                        Monitor.Wait(outReportBuffer);
                    }
                }
            }
            catch (ThreadInterruptedException) { }
        }

        public virtual bool DisconnectWireless(bool callRemoval = false)
        {
            bool result = false;
            if (conType == ConnectionType.BT)
            {
                result = DisconnectBT(callRemoval);
            }
            else if (conType == ConnectionType.SONYWA)
            {
                result = DisconnectDongle(callRemoval);
            }

            return result;
        }

        public virtual bool DisconnectBT(bool callRemoval = false)
        {
            if (Mac != null)
            {
                // Wait for output report to be written
                StopOutputUpdate();
                Console.WriteLine("Trying to disconnect BT device " + Mac);
                IntPtr btHandle = IntPtr.Zero;
                uint IOCTL_BTH_DISCONNECT_DEVICE = 0x41000c;

                byte[] btAddr = new byte[8];
                string[] sbytes = Mac.Split(':');
                for (int i = 0; i < 6; i++)
                {
                    // parse hex byte in reverse order
                    btAddr[5 - i] = Convert.ToByte(sbytes[i], 16);
                }

                long lbtAddr = BitConverter.ToInt64(btAddr, 0);

                bool success = false;

                lock (outputReport)
                {
                    NativeMethods.BLUETOOTH_FIND_RADIO_PARAMS p = new NativeMethods.BLUETOOTH_FIND_RADIO_PARAMS();
                    p.dwSize = Marshal.SizeOf(typeof(NativeMethods.BLUETOOTH_FIND_RADIO_PARAMS));
                    IntPtr searchHandle = NativeMethods.BluetoothFindFirstRadio(ref p, ref btHandle);
                    int bytesReturned = 0;

                    while (!success && btHandle != IntPtr.Zero)
                    {
                        success = NativeMethods.DeviceIoControl(btHandle, IOCTL_BTH_DISCONNECT_DEVICE, ref lbtAddr, 8, IntPtr.Zero, 0, ref bytesReturned, IntPtr.Zero);
                        NativeMethods.CloseHandle(btHandle);
                        if (!success)
                        {
                            if (!NativeMethods.BluetoothFindNextRadio(searchHandle, ref btHandle))
                                btHandle = IntPtr.Zero;
                        }
                    }

                    NativeMethods.BluetoothFindRadioClose(searchHandle);
                    Console.WriteLine("Disconnect successful: " + success);
                }

                success = true; // XXX return value indicates failure, but it still works?
                if (success)
                {
                    IsDisconnecting = true;

                    if (callRemoval)
                    {
                        ResetBluetoothControllerClock();
                        Removal?.Invoke(this, EventArgs.Empty);

                        //System.Threading.Tasks.Task.Factory.StartNew(() => { Removal?.Invoke(this, EventArgs.Empty); });
                    }
                }

                return success;
            }

            return false;
        }

        public virtual bool DisconnectDongle(bool remove = false)
        {
            bool result = false;
            byte[] disconnectReport = new byte[SONYWA_FEATURE_REPORT_LENGTH];
            disconnectReport[0] = 0xe2;
            disconnectReport[1] = 0x02;
            Array.Clear(disconnectReport, 2, SONYWA_FEATURE_REPORT_LENGTH-2);

            if (remove)
                StopOutputUpdate();

            lock (outputReport)
            {
                result = hDevice.WriteFeatureReport(disconnectReport);
            }

            if (result && remove)
            {
                isDisconnecting = true;

                ResetBluetoothControllerClock();
                Removal?.Invoke(this, EventArgs.Empty);

                //System.Threading.Tasks.Task.Factory.StartNew(() => { Removal?.Invoke(this, EventArgs.Empty); });
                //Removal?.Invoke(this, EventArgs.Empty);
            }
            else if (result && !remove)
            {
                isRemoved = true;
            }

            return result;
        }

        protected DS4HapticState testRumble = new DS4HapticState();
        // Virtual feedback, profile-editor tests, and the physical output
        // loop run on independent threads. Treat this state as a single-slot
        // mailbox: a motor pair and its dirty marker must cross that boundary
        // atomically or the consumer can observe a torn pair or clear a newer
        // notification while consuming an older one.
        private readonly object rumbleStateLock = new object();
        private bool previewLightRumbleActive;
        private bool previewHeavyRumbleActive;
        private byte previewLightRumbleStrength;
        private byte previewHeavyRumbleStrength;
        private bool previewRumbleDirty;
        private long rumbleCommandGeneration;

        protected long RumbleCommandGeneration =>
            Interlocked.Read(ref rumbleCommandGeneration);

        protected DS4ForceFeedbackState GetLatestRumbleState()
        {
            lock (rumbleStateLock)
            {
                return testRumble.IsRumbleSet() ?
                    testRumble.rumbleState : currentHap.rumbleState;
            }
        }

        protected bool TryGetRumblePreview(out bool lightMotorActive,
            out byte lightMotorStrength, out bool heavyMotorActive,
            out byte heavyMotorStrength)
        {
            lock (rumbleStateLock)
            {
                lightMotorActive = previewLightRumbleActive;
                lightMotorStrength = previewLightRumbleStrength;
                heavyMotorActive = previewHeavyRumbleActive;
                heavyMotorStrength = previewHeavyRumbleStrength;
                return lightMotorActive || heavyMotorActive;
            }
        }

        internal static bool ShouldRetryBluetoothInputAfterTimeout(
            bool interfaceStillPresent)
        {
            return interfaceStillPresent;
        }

        public virtual void setRumble(byte rightLightFastMotor,
            byte leftHeavySlowMotor)
        {
            lock (rumbleStateLock)
            {
                testRumble.rumbleState.RumbleMotorStrengthRightLightFast = rightLightFastMotor;
                testRumble.rumbleState.RumbleMotorStrengthLeftHeavySlow = leftHeavySlowMotor;
                testRumble.rumbleState.RumbleMotorsExplicitlyOff = rightLightFastMotor == 0 && leftHeavySlowMotor == 0;
                testRumble.dirty = true;
                Interlocked.Increment(ref rumbleCommandGeneration);

                // If rumble autostop timer (msecs) is enabled for this device then restart autostop timer everytime rumble is modified (or stop the timer if rumble is set to zero)
                if (rumbleAutostopTime > 0)
                {
                    if (testRumble.rumbleState.RumbleMotorsExplicitlyOff)
                        rumbleAutostopTimer.Reset();   // A proper zero-rumble notification arrived; no safety stop is needed.
                    else if (currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow != leftHeavySlowMotor || currentHap.rumbleState.RumbleMotorStrengthRightLightFast != rightLightFastMotor)
                        rumbleAutostopTimer.Restart(); // Guard against a lost virtual-output zero-rumble notification.
                }
            }
        }

        /// <summary>
        /// Publishes the profile editor's explicit motor preview independently
        /// from virtual-controller feedback. Output feedback and the UI now run
        /// on separate threads; treating the preview as ordinary feedback let a
        /// later neutral game frame erase it before the physical writer could
        /// present it. Active preview motors override only their own channel,
        /// while the latest game state continues to update underneath.
        /// </summary>
        public virtual void SetRumblePreview(bool lightMotorActive,
            byte lightMotorStrength, bool heavyMotorActive,
            byte heavyMotorStrength)
        {
            lock (rumbleStateLock)
            {
                if (previewLightRumbleActive == lightMotorActive &&
                    previewHeavyRumbleActive == heavyMotorActive &&
                    previewLightRumbleStrength == lightMotorStrength &&
                    previewHeavyRumbleStrength == heavyMotorStrength)
                {
                    return;
                }

                previewLightRumbleActive = lightMotorActive;
                previewHeavyRumbleActive = heavyMotorActive;
                previewLightRumbleStrength = lightMotorStrength;
                previewHeavyRumbleStrength = heavyMotorStrength;
                previewRumbleDirty = true;
                if (!lightMotorActive && !heavyMotorActive)
                {
                    // Releasing the final preview motor is an explicit stop,
                    // not merely the absence of an overlay. Publish the zero
                    // pair through the ordinary ordered rumble mailbox so the
                    // physical compositor cannot retain the last non-zero
                    // template indefinitely.
                    testRumble.rumbleState.
                        RumbleMotorStrengthRightLightFast = 0;
                    testRumble.rumbleState.
                        RumbleMotorStrengthLeftHeavySlow = 0;
                    testRumble.rumbleState.RumbleMotorsExplicitlyOff = true;
                    testRumble.dirty = true;
                }
                Interlocked.Increment(ref rumbleCommandGeneration);
            }
        }

        public virtual void ClearRumblePreview()
        {
            lock (rumbleStateLock)
            {
                previewLightRumbleActive = false;
                previewHeavyRumbleActive = false;
                previewLightRumbleStrength = 0;
                previewHeavyRumbleStrength = 0;
                previewRumbleDirty = true;
                Interlocked.Increment(ref rumbleCommandGeneration);

                // Clearing a preview is also an explicit physical stop. A
                // later game frame may immediately replace it, but an idle
                // virtual output must not leave the last preview strength in
                // currentHap indefinitely.
                testRumble.rumbleState.
                    RumbleMotorStrengthRightLightFast = 0;
                testRumble.rumbleState.
                    RumbleMotorStrengthLeftHeavySlow = 0;
                testRumble.rumbleState.RumbleMotorsExplicitlyOff = true;
                testRumble.dirty = true;
            }
        }

        protected void MergeStates()
        {
            MergeStatesAndGetRumbleGeneration();
        }

        /// <summary>
        /// Atomically consumes the latest rumble mailbox and snapshots the
        /// generation represented by <see cref="currentHap"/>. Reading the
        /// generation after releasing <see cref="rumbleStateLock"/> lets a
        /// producer publish N+1 between those operations, causing the output
        /// writer to acknowledge N+1 while it actually carries N.
        /// </summary>
        protected long MergeStatesAndGetRumbleGeneration()
        {
            lock (rumbleStateLock)
            {
                if (testRumble.IsRumbleSet())
                {
                    if (testRumble.rumbleState.RumbleMotorsExplicitlyOff)
                        testRumble.rumbleState.RumbleMotorsExplicitlyOff = false;

                    //currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow = testRumble.rumbleState.RumbleMotorStrengthLeftHeavySlow;
                    //currentHap.rumbleState.RumbleMotorStrengthRightLightFast = testRumble.rumbleState.RumbleMotorStrengthRightLightFast;
                    currentHap.rumbleState = testRumble.rumbleState;
                }

                // Dirty is a multi-producer latch. Lightbar, trigger, audio
                // routing, and rumble can all publish between physical-output
                // passes; consuming an idle rumble mailbox must never clear a
                // pending update from one of those other producers.
                currentHap.dirty |= testRumble.dirty;
                testRumble.dirty = false;

                if (previewLightRumbleActive)
                {
                    currentHap.rumbleState.
                        RumbleMotorStrengthRightLightFast =
                            previewLightRumbleStrength;
                }
                if (previewHeavyRumbleActive)
                {
                    currentHap.rumbleState.
                        RumbleMotorStrengthLeftHeavySlow =
                            previewHeavyRumbleStrength;
                }
                if (previewLightRumbleActive || previewHeavyRumbleActive)
                {
                    currentHap.rumbleState.RumbleMotorsExplicitlyOff = false;
                }

                currentHap.dirty |= previewRumbleDirty;
                previewRumbleDirty = false;
                return Interlocked.Read(ref rumbleCommandGeneration);
            }
        }

        public DS4State getRawCurrentState()
        {
            return cState.Clone();
        }

        public DS4State getRawPreviousState()
        {
            return pState.Clone();
        }

        public void getRawCurrentState(DS4State state)
        {
            cState.CopyTo(state);
        }

        public void getRawPreviousState(DS4State state)
        {
            pState.CopyTo(state);
        }

        public virtual DS4State getCurrentStateRef()
        {
            return cState;
        }

        public virtual DS4State getPreviousStateRef()
        {
            return pState;
        }

        public DS4State GetRawCurrentStateRef()
        {
            return cState;
        }

        public DS4State GetRawPreviousStateRef()
        {
            return pState;
        }

        public virtual void PreserveMergedStateData()
        {
        }

        public bool isDS4Idle()
        {
            if (cState.Square || cState.Cross || cState.Circle || cState.Triangle)
                return false;
            if (cState.DpadUp || cState.DpadLeft || cState.DpadDown || cState.DpadRight)
                return false;
            if (cState.L3 || cState.R3 || cState.L1 || cState.R1 || cState.Share || cState.Options || cState.PS)
                return false;
            if (cState.L2 != 0 || cState.R2 != 0)
                return false;
            // TODO calibrate to get an accurate jitter and center-play range and centered position
            const int slop = 64;
            if (cState.LX <= 127 - slop || cState.LX >= 128 + slop || cState.LY <= 127 - slop || cState.LY >= 128 + slop)
                return false;
            if (cState.RX <= 127 - slop || cState.RX >= 128 + slop || cState.RY <= 127 - slop || cState.RY >= 128 + slop)
                return false;
            if (cState.Touch1 || cState.Touch2 || cState.TouchButton)
                return false;
            return true;
        }

        protected DS4HapticState currentHap = new DS4HapticState();
        public virtual void SetHapticState(ref DS4HapticState hs)
        {
            currentHap = hs;
            currentHap.dirty = true;
        }

        public virtual void SetLightbarState(
            ref DS4LightbarState lightState)
        {
            if (!currentHap.lightbarState.Equals(lightState))
            {
                currentHap.dirty = true;
            }

            currentHap.lightbarState = lightState;
        }

        public ref DS4LightbarState GetLightbarStateRef()
        {
            return ref currentHap.lightbarState;
        }

        public virtual void SetRumbleState(
            ref DS4ForceFeedbackState rumbleState)
        {
            currentHap.rumbleState = rumbleState;
        }

        public override string ToString()
        {
            return Mac;
        }

        protected void RunRemoval()
        {
            ResetBluetoothControllerClock();
            Removal?.Invoke(this, EventArgs.Empty);
        }

        public void removeReportHandlers()
        {
            this.Report = null;
        }

        public void queueEvent(Action act)
        {
            lock (eventQueueLock)
            {
                eventQueue.Enqueue(act);
                hasInputEvts = true;
            }
        }

        /// <summary>
        /// Must not be run from input thread. Waits for input thread to be in a wait state
        /// and then tell thread to no longer invoke the Report event. Input thread will then
        /// resume followed by invoking the action passed. Flag will be set to have
        /// Report event to resume being invoked after
        /// </summary>
        /// <param name="act">Action to execute in current thread</param>
        public void HaltReportingRunAction(Action act)
        {
            // Wait for controller to be in a wait period
            bool result = readWaitEv.Wait(millisecondsTimeout: 500);
            if (result)
            {
                readWaitEv.Reset();

                // Tell device to no longer fire reports
                fireReport = false;

                // Flag is set. Allow input thread to resume
                readWaitEv.Set();

                // Invoke main desired action
                act?.Invoke();

                // Start firing reports again
                fireReport = true;
            }
        }

        public void updateSerial()
        {
            hDevice.resetSerial();
            string tempMac = hDevice.ReadSerial(SerialReportID);
            if (tempMac != Mac)
            {
                Mac = tempMac;
                SerialChange?.Invoke(this, EventArgs.Empty);
                MacAddressChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool isValidSerial()
        {
            return !Mac.Equals(BLANK_SERIAL);
        }

        public static bool isValidSerial(string test)
        {
            return !test.Equals(BLANK_SERIAL);
        }

        private bool abortInputThread = false;
        public void PrepareAbort()
        {
            abortInputThread = true;
        }

        public virtual void MergeStateData(DS4State dState)
        {
        }

        private void PrepareOutputFeaturesByte()
        {
            if (nativeOptionsStore != null)
            {
                if (nativeOptionsStore.IsCopyCat)
                {
                    outputFeaturesByte = COPYCAT_OUTPUT_FEATURES;
                }
                else
                {
                    outputFeaturesByte = DEFAULT_OUTPUT_FEATURES;
                }
            }
        }

        private void SetupOptionsEvents()
        {
            if (nativeOptionsStore != null)
            {
                nativeOptionsStore.IsCopyCatChanged += (sender, e) =>
                {
                    PrepareOutputFeaturesByte();
                };
            }
        }

        public virtual void PrepareTriggerEffect(InputDevices.TriggerId trigger,
            InputDevices.TriggerEffects effect, InputDevices.TriggerEffectSettings effectSettings)
        {
        }

        public virtual void CheckControllerNumDeviceSettings(int numControllers)
        {
        }

        public virtual void LoadStoreSettings()
        {
            if (nativeOptionsStore != null)
            {
                PrepareOutputFeaturesByte();
            }
        }
    }
}
