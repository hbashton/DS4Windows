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
using System.Buffers.Binary;
using DS4Windows.Switch2;

namespace DS4Windows
{
    public class DS4State
    {
        public uint PacketCounter;
        public DateTime ReportTimeStamp;
        public bool Square, Triangle, Circle, Cross;
        public bool DpadUp, DpadDown, DpadLeft, DpadRight;
        public bool L1, L2Btn, L3, R1, R2Btn, R3;
        public bool Share, Options, PS, Mute, Touch1, Touch2, TouchButton, TouchRight,
            TouchLeft, Touch1Finger, Touch2Fingers, OutputTouchButton,
            Capture, SideL, SideR, FnL, FnR, BLP, BRP;
        public byte Touch1Identifier, Touch2Identifier;
        // Mapping-owned coordinates. Byte access remains source-compatible for
        // existing readers/writers; assigning a byte explicitly replaces any
        // fractional value. Do not recover precision from raw metadata later.
        internal DS4MappedStickAxis LXAxis, RXAxis, LYAxis, RYAxis;
        public byte LX { get => LXAxis.LegacyValue; set => LXAxis = DS4MappedStickAxis.FromLegacy(value); }
        public byte RX { get => RXAxis.LegacyValue; set => RXAxis = DS4MappedStickAxis.FromLegacy(value); }
        public byte LY { get => LYAxis.LegacyValue; set => LYAxis = DS4MappedStickAxis.FromLegacy(value); }
        public byte RY { get => RYAxis.LegacyValue; set => RYAxis = DS4MappedStickAxis.FromLegacy(value); }
        public byte L2, R2;
        public byte L2Raw, R2Raw;
        public byte FrameCounter; // 0, 1, 2...62, 63, 0....
        public byte TouchPacketCounter; // we break these out automatically
        public byte Battery; // 0 for charging, 10/20/30/40/50/60/70/80/90/100 for percentage of full
        public double LSAngle; // Calculated bearing of the LS X,Y coordinates
        public double RSAngle; // Calculated bearing of the RS X,Y coordinates
        public double LSAngleRad; // Calculated bearing of the LS X,Y coordinates (in radians)
        public double RSAngleRad; // Calculated bearing of the RS X,Y coordinates (in radians)
        public double LXUnit;
        public double LYUnit;
        public double RXUnit;
        public double RYUnit;
        public byte OutputLSOuter = 0, OutputRSOuter = 0;
        public double elapsedTime = 0.0;
        public ulong totalMicroSec = 0;
        public ushort ds4Timestamp = 0;
        // A same-report observation of the physical DualSense vendor fields.
        // This is metadata rather than mapped control state, so mapping and
        // debounce scratch copies carry it unchanged to the VIIPER boundary.
        public DualSenseRawInputStatus DualSenseRawInputStatus;
        // Source metadata from the high-resolution Switch 2 profile boundary.
        // This is never treated as already-mapped output: it preserves the
        // physical 12-bit observation (including controls the legacy DS4State
        // surface cannot represent) while the ordinary fields continue through
        // the one existing profile/mapping pipeline.
        public Switch2RawInputStatus Switch2RawInputStatus;
        // Joined/standalone Joy-Con 2 metadata has independent per-half
        // lifetime fences and four rail/paddle controls, so it remains a
        // distinct copied sidecar rather than overloading the Pro status.
        public Switch2JoyConRawInputStatus Switch2JoyConRawInputStatus;
        public SixAxis Motion = null;
        public static readonly int DEFAULT_AXISDIR_VALUE = 127;
        public Int32 SASteeringWheelEmulationUnit;

        public struct TrackPadTouch
        {
            public bool IsActive;
            public byte Id;
            public short X;
            public short Y;
            public byte RawTrackingNum;
        }

        public TrackPadTouch TrackPadTouch0;
        public TrackPadTouch TrackPadTouch1;

        public DS4State()
        {
            PacketCounter = 0;
            Square = Triangle = Circle = Cross = false;
            DpadUp = DpadDown = DpadLeft = DpadRight = false;
            L1 = L2Btn = L3 = R1 = R2Btn = R3 = false;
            Share = Options = PS = Mute = Touch1 = Touch2 = TouchButton =
                OutputTouchButton = TouchRight = TouchLeft =
                Capture = SideL = SideR =
                FnL = FnR = BLP = BRP = false;
            Touch1Finger = Touch2Fingers = false;
            LX = RX = LY = RY = 128;
            L2 = R2 = 0;
            L2Raw = R2Raw = 0;
            FrameCounter = 255; // only actually has 6 bits, so this is a null indicator
            TouchPacketCounter = 255; // 8 bits, no great junk value
            Battery = 0;
            LSAngle = 0.0;
            LSAngleRad = 0.0;
            RSAngle = 0.0;
            RSAngleRad = 0.0;
            LXUnit = 0.0;
            LYUnit = 0.0;
            RXUnit = 0.0;
            RYUnit = 0.0;
            elapsedTime = 0.0;
            totalMicroSec = 0;
            ds4Timestamp = 0;
            Motion = new SixAxis(0, 0, 0, 0, 0, 0, 0.0);
            TrackPadTouch0.IsActive = false;
            TrackPadTouch1.IsActive = false;
            SASteeringWheelEmulationUnit = 0;
            OutputLSOuter = OutputRSOuter = 0;
        }

        public DS4State(DS4State state)
        {
            PacketCounter = state.PacketCounter;
            ReportTimeStamp = state.ReportTimeStamp;
            Square = state.Square;
            Triangle = state.Triangle;
            Circle = state.Circle;
            Cross = state.Cross;
            DpadUp = state.DpadUp;
            DpadDown = state.DpadDown;
            DpadLeft = state.DpadLeft;
            DpadRight = state.DpadRight;
            L1 = state.L1;
            L2 = state.L2;
            L2Raw = state.L2Raw;
            L2Btn = state.L2Btn;
            L3 = state.L3;
            R1 = state.R1;
            R2 = state.R2;
            R2Raw = state.R2Raw;
            R2Btn = state.R2Btn;
            R3 = state.R3;
            Share = state.Share;
            Options = state.Options;
            PS = state.PS;
            Mute = state.Mute;
            FnL = state.FnL;
            FnR = state.FnR;
            BLP = state.BLP;
            BRP = state.BRP;
            Capture = state.Capture;
            SideL = state.SideL;
            SideR = state.SideR;
            Touch1 = state.Touch1;
            TouchRight = state.TouchRight;
            TouchLeft = state.TouchLeft;
            Touch1Identifier = state.Touch1Identifier;
            Touch2 = state.Touch2;
            Touch2Identifier = state.Touch2Identifier;
            TouchButton = state.TouchButton;
            OutputTouchButton = state.OutputTouchButton;
            TouchPacketCounter = state.TouchPacketCounter;
            Touch1Finger = state.Touch1Finger;
            Touch2Fingers = state.Touch2Fingers;
            LXAxis = state.LXAxis;
            RXAxis = state.RXAxis;
            LYAxis = state.LYAxis;
            RYAxis = state.RYAxis;
            FrameCounter = state.FrameCounter;
            Battery = state.Battery;
            LSAngle = state.LSAngle;
            LSAngleRad = state.LSAngleRad;
            RSAngle = state.RSAngle;
            RSAngleRad = state.RSAngleRad;
            LXUnit = state.LXUnit;
            LYUnit = state.LYUnit;
            RXUnit = state.RXUnit;
            RYUnit = state.RYUnit;
            elapsedTime = state.elapsedTime;
            totalMicroSec = state.totalMicroSec;
            ds4Timestamp = state.ds4Timestamp;
            DualSenseRawInputStatus = state.DualSenseRawInputStatus;
            Switch2RawInputStatus = state.Switch2RawInputStatus;
            Switch2JoyConRawInputStatus = state.Switch2JoyConRawInputStatus;
            Motion = state.Motion;
            TrackPadTouch0 = state.TrackPadTouch0;
            TrackPadTouch1 = state.TrackPadTouch1;
            SASteeringWheelEmulationUnit = state.SASteeringWheelEmulationUnit;
            OutputLSOuter = state.OutputLSOuter;
            OutputRSOuter = state.OutputRSOuter;
        }

        public DS4State Clone()
        {
            return new DS4State(this);
        }

        public void CopyTo(DS4State state)
        {
            state.PacketCounter = PacketCounter;
            state.ReportTimeStamp = ReportTimeStamp;
            state.Square = Square;
            state.Triangle = Triangle;
            state.Circle = Circle;
            state.Cross = Cross;
            state.DpadUp = DpadUp;
            state.DpadDown = DpadDown;
            state.DpadLeft = DpadLeft;
            state.DpadRight = DpadRight;
            state.L1 = L1;
            state.L2 = L2;
            state.L2Raw = L2Raw;
            state.L2Btn = L2Btn;
            state.L3 = L3;
            state.R1 = R1;
            state.R2 = R2;
            state.R2Raw = R2Raw;
            state.R2Btn = R2Btn;
            state.R3 = R3;
            state.Share = Share;
            state.Options = Options;
            state.PS = PS;
            state.Mute = Mute;
            state.FnL = FnL;
            state.FnR = FnR;
            state.BLP = BLP;
            state.BRP = BRP;
            state.Capture = Capture;
            state.SideL = SideL;
            state.SideR = SideR;
            state.Touch1 = Touch1;
            state.Touch1Identifier = Touch1Identifier;
            state.Touch2 = Touch2;
            state.Touch2Identifier = Touch2Identifier;
            state.TouchLeft = TouchLeft;
            state.TouchRight = TouchRight;
            state.TouchButton = TouchButton;
            state.OutputTouchButton = OutputTouchButton;
            state.TouchPacketCounter = TouchPacketCounter;
            state.Touch1Finger = Touch1Finger;
            state.Touch2Fingers = Touch2Fingers;
            state.LXAxis = LXAxis;
            state.RXAxis = RXAxis;
            state.LYAxis = LYAxis;
            state.RYAxis = RYAxis;
            state.FrameCounter = FrameCounter;
            state.Battery = Battery;
            state.LSAngle = LSAngle;
            state.LSAngleRad = LSAngleRad;
            state.RSAngle = RSAngle;
            state.RSAngleRad = RSAngleRad;
            state.LXUnit = LXUnit;
            state.LYUnit = LYUnit;
            state.RXUnit = RXUnit;
            state.RYUnit = RYUnit;
            state.elapsedTime = elapsedTime;
            state.totalMicroSec = totalMicroSec;
            state.ds4Timestamp = ds4Timestamp;
            state.DualSenseRawInputStatus = DualSenseRawInputStatus;
            state.Switch2RawInputStatus = Switch2RawInputStatus;
            state.Switch2JoyConRawInputStatus = Switch2JoyConRawInputStatus;
            state.Motion = Motion;
            state.TrackPadTouch0 = TrackPadTouch0;
            state.TrackPadTouch1 = TrackPadTouch1;
            state.SASteeringWheelEmulationUnit = SASteeringWheelEmulationUnit;
            state.OutputLSOuter = OutputLSOuter;
            state.OutputRSOuter = OutputRSOuter;
        }

        /// <summary>
        /// Only copy extra DS4State data that is not output directly tied
        /// to the mapper routine. Gyro motion data, Touchpad touch data,
        /// and timestamp data are copied
        /// </summary>
        /// <param name="state">State object to copy data to</param>
        public void CopyExtrasTo(DS4State state)
        {
            // Mapped stick axes are controls, not physical metadata. Copying
            // them here would undo custom mapping immediately before egress.
            state.Motion = Motion;
            state.ds4Timestamp = ds4Timestamp;
            state.FrameCounter = FrameCounter;
            state.TouchPacketCounter = TouchPacketCounter;
            state.DualSenseRawInputStatus = DualSenseRawInputStatus;
            state.Switch2RawInputStatus = Switch2RawInputStatus;
            state.Switch2JoyConRawInputStatus = Switch2JoyConRawInputStatus;
            state.TrackPadTouch0 = TrackPadTouch0;
            state.TrackPadTouch1 = TrackPadTouch1;
        }

        public void calculateStickAngles()
        {
            double lsangle = LXAxis.IsHighResolution || LYAxis.IsHighResolution ?
                Math.Atan2(-(LYAxis.ProfileCoordinate - 128), (LXAxis.ProfileCoordinate - 128)) :
                Math.Atan2(-(LY - 128), (LX - 128));
            LSAngleRad = lsangle;
            lsangle = (lsangle >= 0 ? lsangle : (2 * Math.PI + lsangle)) * 180 / Math.PI;
            LSAngle = lsangle;
            LXUnit = Math.Abs(Math.Cos(LSAngleRad));
            LYUnit = Math.Abs(Math.Sin(LSAngleRad));

            double rsangle = RXAxis.IsHighResolution || RYAxis.IsHighResolution ?
                Math.Atan2(-(RYAxis.ProfileCoordinate - 128), (RXAxis.ProfileCoordinate - 128)) :
                Math.Atan2(-(RY - 128), (RX - 128));
            RSAngleRad = rsangle;
            rsangle = (rsangle >= 0 ? rsangle : (2 * Math.PI + rsangle)) * 180 / Math.PI;
            RSAngle = rsangle;
            RXUnit = Math.Abs(Math.Cos(RSAngleRad));
            RYUnit = Math.Abs(Math.Sin(RSAngleRad));
        }

        /// <summary>
        /// Rotate LX and LY by a rotation angle (radians)
        /// </summary>
        /// <param name="rotationRad">Rotation angle in radians</param>
        public void rotateLSCoordinates(double rotationRad)
        {
            RotateStickCoordinates(ref LXAxis, ref LYAxis, rotationRad);
        }

        /// <summary>
        /// Rotate RX and RY by a rotation angle (radians)
        /// </summary>
        /// <param name="rotationRad">Rotation angle in radians</param>
        public void rotateRSCoordinates(double rotationRad)
        {
            RotateStickCoordinates(ref RXAxis, ref RYAxis, rotationRad);
        }

        private static void RotateStickCoordinates(ref DS4MappedStickAxis x,
            ref DS4MappedStickAxis y, double rotationRad)
        {
            double sinAngle = Math.Sin(rotationRad), cosAngle = Math.Cos(rotationRad);
            double tempX = x.ProfileCoordinate - 128.0, tempY = y.ProfileCoordinate - 128.0;
            double rotatedX = Global.Clamp(-128.0, tempX * cosAngle - tempY * sinAngle, 127.0) + 128.0;
            double rotatedY = Global.Clamp(-128.0, tempX * sinAngle + tempY * cosAngle, 127.0) + 128.0;
            if (x.IsHighResolution || y.IsHighResolution)
            {
                DS4MappedStickAxis.TryFromProfileCoordinate(rotatedX, out x);
                DS4MappedStickAxis.TryFromProfileCoordinate(rotatedY, out y);
            }
            else
            {
                // Preserve the historical truncation points for legacy input.
                x = DS4MappedStickAxis.FromLegacy((byte)rotatedX);
                y = DS4MappedStickAxis.FromLegacy((byte)rotatedY);
            }
        }
    }

    /// <summary>
    /// Fixed-size source observation retained across DS4Windows mapping copies.
    /// The normalized axes follow the conventional gamepad sign convention
    /// (negative left/up, positive right/down). Raw axes remain in Nintendo's
    /// 12-bit wire units. The C button is retained explicitly and is never
    /// aliased to the DualSense mute button.
    /// </summary>
    public struct Switch2RawInputStatus :
        IEquatable<Switch2RawInputStatus>
    {
        public bool IsValid;
        public ushort ContractVersion;
        public Switch2Transport Transport;
        public Switch2InputProtocolRevision ProtocolRevision;
        public ulong DeviceGeneration;
        public ulong TransportGeneration;
        public long CompletionTimestampQpc;
        public long QpcFrequency;
        public uint DeviceCounterRaw;
        public uint RawButtonBits;
        public uint UnknownButtonBits;
        public ushort LeftStickXRaw;
        public ushort LeftStickYRaw;
        public ushort RightStickXRaw;
        public ushort RightStickYRaw;
        public short LeftStickX;
        public short LeftStickY;
        public short RightStickX;
        public short RightStickY;
        public bool CButton;

        public readonly bool Equals(Switch2RawInputStatus other) =>
            IsValid == other.IsValid &&
            ContractVersion == other.ContractVersion &&
            Transport == other.Transport &&
            ProtocolRevision == other.ProtocolRevision &&
            DeviceGeneration == other.DeviceGeneration &&
            TransportGeneration == other.TransportGeneration &&
            CompletionTimestampQpc == other.CompletionTimestampQpc &&
            QpcFrequency == other.QpcFrequency &&
            DeviceCounterRaw == other.DeviceCounterRaw &&
            RawButtonBits == other.RawButtonBits &&
            UnknownButtonBits == other.UnknownButtonBits &&
            LeftStickXRaw == other.LeftStickXRaw &&
            LeftStickYRaw == other.LeftStickYRaw &&
            RightStickXRaw == other.RightStickXRaw &&
            RightStickYRaw == other.RightStickYRaw &&
            LeftStickX == other.LeftStickX &&
            LeftStickY == other.LeftStickY &&
            RightStickX == other.RightStickX &&
            RightStickY == other.RightStickY &&
            CButton == other.CButton;

        public override readonly bool Equals(object obj) =>
            obj is Switch2RawInputStatus other && Equals(other);

        public override readonly int GetHashCode() => HashCode.Combine(
            HashCode.Combine(IsValid, ContractVersion, Transport,
                ProtocolRevision, DeviceGeneration, TransportGeneration,
                CompletionTimestampQpc, QpcFrequency),
            HashCode.Combine(UnknownButtonBits, LeftStickXRaw, LeftStickYRaw,
                RightStickXRaw, RightStickYRaw, LeftStickX, LeftStickY,
                RightStickX),
            DeviceCounterRaw, RawButtonBits, RightStickY, CButton);
    }

    /// <summary>
    /// Fixed-size Joy-Con 2 source observation retained across every DS4State
    /// copy path. Joined halves keep independent generations, counters, and raw
    /// button masks. C and rail/paddle buttons remain explicit and are never
    /// aliased to DualSense mute or to an ambiguous legacy paddle slot.
    /// </summary>
    public struct Switch2JoyConRawInputStatus :
        IEquatable<Switch2JoyConRawInputStatus>
    {
        public bool IsValid;
        public ushort ContractVersion;
        public Switch2JoyConProfileMode Mode;
        public ulong PairEpoch;
        public long CompletionTimestampQpc;
        public long QpcFrequency;
        public bool LeftPresent;
        public ulong LeftDeviceGeneration;
        public ulong LeftTransportGeneration;
        public uint LeftDeviceCounterRaw;
        public uint LeftRawButtonBits;
        public uint LeftUnknownButtonBits;
        public ushort LeftPhysicalStickXRaw;
        public ushort LeftPhysicalStickYRaw;
        public bool LeftHasCommonMotion;
        public uint LeftMotionTimestamp;
        public Switch2Vector3Raw LeftAccelerometer;
        public Switch2Vector3Raw LeftGyroscope;
        public Switch2Vector3Raw LeftMagnetometer;
        public ushort LeftIrX;
        public ushort LeftIrY;
        public ushort LeftIrRoughness;
        public ushort LeftIrDistance;
        public bool RightPresent;
        public ulong RightDeviceGeneration;
        public ulong RightTransportGeneration;
        public uint RightDeviceCounterRaw;
        public uint RightRawButtonBits;
        public uint RightUnknownButtonBits;
        public ushort RightPhysicalStickXRaw;
        public ushort RightPhysicalStickYRaw;
        public bool RightHasCommonMotion;
        public uint RightMotionTimestamp;
        public Switch2Vector3Raw RightAccelerometer;
        public Switch2Vector3Raw RightGyroscope;
        public Switch2Vector3Raw RightMagnetometer;
        public ushort RightIrX;
        public ushort RightIrY;
        public ushort RightIrRoughness;
        public ushort RightIrDistance;
        public short LogicalLeftStickX;
        public short LogicalLeftStickY;
        public short LogicalRightStickX;
        public short LogicalRightStickY;
        public bool CButton;
        public bool LeftPaddle1;
        public bool LeftPaddle2;
        public bool RightPaddle1;
        public bool RightPaddle2;
        public bool LeftRailSL;
        public bool LeftRailSR;
        public bool RightRailSL;
        public bool RightRailSR;

        public readonly bool Equals(Switch2JoyConRawInputStatus other) =>
            IsValid == other.IsValid &&
            ContractVersion == other.ContractVersion &&
            Mode == other.Mode && PairEpoch == other.PairEpoch &&
            CompletionTimestampQpc == other.CompletionTimestampQpc &&
            QpcFrequency == other.QpcFrequency &&
            LeftPresent == other.LeftPresent &&
            LeftDeviceGeneration == other.LeftDeviceGeneration &&
            LeftTransportGeneration == other.LeftTransportGeneration &&
            LeftDeviceCounterRaw == other.LeftDeviceCounterRaw &&
            LeftRawButtonBits == other.LeftRawButtonBits &&
            LeftUnknownButtonBits == other.LeftUnknownButtonBits &&
            LeftPhysicalStickXRaw == other.LeftPhysicalStickXRaw &&
            LeftPhysicalStickYRaw == other.LeftPhysicalStickYRaw &&
            LeftHasCommonMotion == other.LeftHasCommonMotion &&
            LeftMotionTimestamp == other.LeftMotionTimestamp &&
            LeftAccelerometer.Equals(other.LeftAccelerometer) &&
            LeftGyroscope.Equals(other.LeftGyroscope) &&
            LeftMagnetometer.Equals(other.LeftMagnetometer) &&
            LeftIrX == other.LeftIrX && LeftIrY == other.LeftIrY &&
            LeftIrRoughness == other.LeftIrRoughness &&
            LeftIrDistance == other.LeftIrDistance &&
            RightPresent == other.RightPresent &&
            RightDeviceGeneration == other.RightDeviceGeneration &&
            RightTransportGeneration == other.RightTransportGeneration &&
            RightDeviceCounterRaw == other.RightDeviceCounterRaw &&
            RightRawButtonBits == other.RightRawButtonBits &&
            RightUnknownButtonBits == other.RightUnknownButtonBits &&
            RightPhysicalStickXRaw == other.RightPhysicalStickXRaw &&
            RightPhysicalStickYRaw == other.RightPhysicalStickYRaw &&
            RightHasCommonMotion == other.RightHasCommonMotion &&
            RightMotionTimestamp == other.RightMotionTimestamp &&
            RightAccelerometer.Equals(other.RightAccelerometer) &&
            RightGyroscope.Equals(other.RightGyroscope) &&
            RightMagnetometer.Equals(other.RightMagnetometer) &&
            RightIrX == other.RightIrX && RightIrY == other.RightIrY &&
            RightIrRoughness == other.RightIrRoughness &&
            RightIrDistance == other.RightIrDistance &&
            LogicalLeftStickX == other.LogicalLeftStickX &&
            LogicalLeftStickY == other.LogicalLeftStickY &&
            LogicalRightStickX == other.LogicalRightStickX &&
            LogicalRightStickY == other.LogicalRightStickY &&
            CButton == other.CButton &&
            LeftPaddle1 == other.LeftPaddle1 &&
            LeftPaddle2 == other.LeftPaddle2 &&
            RightPaddle1 == other.RightPaddle1 &&
            RightPaddle2 == other.RightPaddle2 &&
            LeftRailSL == other.LeftRailSL && LeftRailSR == other.LeftRailSR &&
            RightRailSL == other.RightRailSL && RightRailSR == other.RightRailSR;

        public override readonly bool Equals(object obj) =>
            obj is Switch2JoyConRawInputStatus other && Equals(other);

        public override readonly int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(IsValid);
            hash.Add(ContractVersion);
            hash.Add(Mode);
            hash.Add(PairEpoch);
            hash.Add(CompletionTimestampQpc);
            hash.Add(QpcFrequency);
            hash.Add(LeftPresent);
            hash.Add(LeftDeviceGeneration);
            hash.Add(LeftTransportGeneration);
            hash.Add(LeftDeviceCounterRaw);
            hash.Add(LeftRawButtonBits);
            hash.Add(LeftUnknownButtonBits);
            hash.Add(LeftPhysicalStickXRaw);
            hash.Add(LeftPhysicalStickYRaw);
            hash.Add(LeftHasCommonMotion);
            hash.Add(LeftMotionTimestamp);
            hash.Add(LeftAccelerometer);
            hash.Add(LeftGyroscope);
            hash.Add(LeftMagnetometer);
            hash.Add(LeftIrX);
            hash.Add(LeftIrY);
            hash.Add(LeftIrRoughness);
            hash.Add(LeftIrDistance);
            hash.Add(RightPresent);
            hash.Add(RightDeviceGeneration);
            hash.Add(RightTransportGeneration);
            hash.Add(RightDeviceCounterRaw);
            hash.Add(RightRawButtonBits);
            hash.Add(RightUnknownButtonBits);
            hash.Add(RightPhysicalStickXRaw);
            hash.Add(RightPhysicalStickYRaw);
            hash.Add(RightHasCommonMotion);
            hash.Add(RightMotionTimestamp);
            hash.Add(RightAccelerometer);
            hash.Add(RightGyroscope);
            hash.Add(RightMagnetometer);
            hash.Add(RightIrX);
            hash.Add(RightIrY);
            hash.Add(RightIrRoughness);
            hash.Add(RightIrDistance);
            hash.Add(LogicalLeftStickX);
            hash.Add(LogicalLeftStickY);
            hash.Add(LogicalRightStickX);
            hash.Add(LogicalRightStickY);
            hash.Add(CButton);
            hash.Add(LeftPaddle1);
            hash.Add(LeftPaddle2);
            hash.Add(RightPaddle1);
            hash.Add(RightPaddle2);
            hash.Add(LeftRailSL);
            hash.Add(LeftRailSR);
            hash.Add(RightRailSL);
            hash.Add(RightRailSR);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Fixed-size physical DualSense input observation used to preserve the
    /// sensor/status relationship in an enhanced VIIPER V5 input frame. The
    /// fields are copied from one validated physical report: raw bytes 28..31
    /// and 41..55 after normalizing the USB/BT transport prefix.
    /// </summary>
    public struct DualSenseRawInputStatus :
        IEquatable<DualSenseRawInputStatus>
    {
        public const int StatusByteCount = 15;

        public bool IsValid;
        public bool IsEdgeLayout;
        public uint SensorTimestamp;
        public byte TouchTimestamp;
        public byte RightTriggerFeedback;
        public byte LeftTriggerFeedback;
        public uint HostTimestamp;
        public byte TriggerEffectModes;
        public uint DeviceTimestamp;
        public byte BatteryStatus;
        public byte ConnectionStatus;
        public byte Raw55;

        internal static bool TryRead(ReadOnlySpan<byte> report,
            int reportOffset, out DualSenseRawInputStatus status)
        {
            status = default;
            if ((uint)reportOffset > 1u ||
                report.Length < 56 + reportOffset)
            {
                return false;
            }

            status = new DualSenseRawInputStatus
            {
                IsValid = true,
                SensorTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(
                    report.Slice(28 + reportOffset, sizeof(uint))),
                TouchTimestamp = report[41 + reportOffset],
                RightTriggerFeedback = report[42 + reportOffset],
                LeftTriggerFeedback = report[43 + reportOffset],
                HostTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(
                    report.Slice(44 + reportOffset, sizeof(uint))),
                TriggerEffectModes = report[48 + reportOffset],
                DeviceTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(
                    report.Slice(49 + reportOffset, sizeof(uint))),
                BatteryStatus = report[53 + reportOffset],
                ConnectionStatus = report[54 + reportOffset],
                Raw55 = report[55 + reportOffset],
            };
            return true;
        }

        internal readonly void WriteStatusBytes(Span<byte> destination)
        {
            if (destination.Length < StatusByteCount)
            {
                throw new ArgumentException(
                    $"A DualSense raw-status block needs {StatusByteCount} bytes.",
                    nameof(destination));
            }

            destination[0] = TouchTimestamp;
            destination[1] = RightTriggerFeedback;
            destination[2] = LeftTriggerFeedback;
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice(3, sizeof(uint)), HostTimestamp);
            destination[7] = TriggerEffectModes;
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice(8, sizeof(uint)), DeviceTimestamp);
            destination[12] = BatteryStatus;
            destination[13] = ConnectionStatus;
            destination[14] = Raw55;
        }

        internal void CoupleTriggerFrom(
            in DualSenseRawInputStatus peakStatus, bool left)
        {
            if (!IsValid || !peakStatus.IsValid ||
                !CanCoupleTriggerFrom(peakStatus))
            {
                return;
            }

            if (left)
            {
                LeftTriggerFeedback = peakStatus.LeftTriggerFeedback;
                TriggerEffectModes = (byte)((TriggerEffectModes & 0x0F) |
                    (peakStatus.TriggerEffectModes & 0xF0));
            }
            else
            {
                RightTriggerFeedback = peakStatus.RightTriggerFeedback;
                TriggerEffectModes = (byte)((TriggerEffectModes & 0xF0) |
                    (peakStatus.TriggerEffectModes & 0x0F));
            }
        }

        internal readonly bool CanCoupleTriggerFrom(
            in DualSenseRawInputStatus other)
        {
            // Base DualSense and DualSense Edge assign different meanings to
            // raw49..52. Never anchor a synthesized trigger peak to metadata
            // from the other physical layout. Invalid observations are only
            // compatible with other invalid observations so that a report
            // with real metadata cannot be represented by one without it.
            return IsEdgeLayout == other.IsEdgeLayout &&
                IsValid == other.IsValid;
        }

        internal readonly bool TriggerCoupledEquals(
            in DualSenseRawInputStatus other, bool left)
        {
            if (IsValid != other.IsValid ||
                IsEdgeLayout != other.IsEdgeLayout)
            {
                return false;
            }

            if (!IsValid)
            {
                return true;
            }

            byte effectMask = left ? (byte)0xF0 : (byte)0x0F;
            return (left ? LeftTriggerFeedback == other.LeftTriggerFeedback :
                    RightTriggerFeedback == other.RightTriggerFeedback) &&
                (TriggerEffectModes & effectMask) ==
                    (other.TriggerEffectModes & effectMask);
        }

        public readonly bool Equals(DualSenseRawInputStatus other) =>
            IsValid == other.IsValid &&
            IsEdgeLayout == other.IsEdgeLayout &&
            SensorTimestamp == other.SensorTimestamp &&
            TouchTimestamp == other.TouchTimestamp &&
            RightTriggerFeedback == other.RightTriggerFeedback &&
            LeftTriggerFeedback == other.LeftTriggerFeedback &&
            HostTimestamp == other.HostTimestamp &&
            TriggerEffectModes == other.TriggerEffectModes &&
            DeviceTimestamp == other.DeviceTimestamp &&
            BatteryStatus == other.BatteryStatus &&
            ConnectionStatus == other.ConnectionStatus &&
            Raw55 == other.Raw55;

        public override readonly bool Equals(object obj) =>
            obj is DualSenseRawInputStatus other && Equals(other);

        public override readonly int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(IsValid);
            hash.Add(IsEdgeLayout);
            hash.Add(SensorTimestamp);
            hash.Add(TouchTimestamp);
            hash.Add(RightTriggerFeedback);
            hash.Add(LeftTriggerFeedback);
            hash.Add(HostTimestamp);
            hash.Add(TriggerEffectModes);
            hash.Add(DeviceTimestamp);
            hash.Add(BatteryStatus);
            hash.Add(ConnectionStatus);
            hash.Add(Raw55);
            return hash.ToHashCode();
        }
    }
}
