/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Numerics;

namespace DS4Windows.Switch2;

public enum Switch2JoyConProfileMode : byte
{
    Invalid = 0,
    Joined = 1,
    StandaloneHorizontalLeft = 2,
    StandaloneHorizontalRight = 3,
    StandaloneVerticalLeft = 4,
    StandaloneVerticalRight = 5,
}

/// <summary>
/// Presentation orientation for one standalone Joy-Con 2. Profiles provide
/// the default and an opaque controller-specific record may override it. The
/// value is deliberately not part of Bluetooth transport authentication:
/// rotating controls never reconnects the controller, replaces its feedback
/// owner, or resets freshness baselines.
/// </summary>
public enum Switch2JoyConHoldMode : byte
{
    Vertical = 0,
    Horizontal = 1,
}

public enum Switch2JoyConProfileInputFailure : byte
{
    None = 0,
    InvalidMapperState,
    WrongMapperMode,
    PairEpochMismatch,
    InvalidCanonicalFrame,
    UnsupportedIdentity,
    UnsupportedReport,
    LifetimeMismatch,
    InvalidCalibration,
    BackwardOrOutOfOrder,
    StaleObservation,
    InvalidAxis,
}

/// <summary>
/// Logical buttons established by the pinned SDL Joy-Con 2 combined and mini
/// handlers. These are semantic flags, not Nintendo wire-bit aliases; the same
/// wire bit can intentionally mean a different control in horizontal mode.
/// </summary>
[Flags]
public enum Switch2JoyConProfileButton : uint
{
    None = 0,
    FaceWest = 1u << 0,
    FaceNorth = 1u << 1,
    FaceSouth = 1u << 2,
    FaceEast = 1u << 3,
    Back = 1u << 4,
    Start = 1u << 5,
    Guide = 1u << 6,
    Capture = 1u << 7,
    LeftStick = 1u << 8,
    RightStick = 1u << 9,
    LeftShoulder = 1u << 10,
    RightShoulder = 1u << 11,
    LeftTrigger = 1u << 12,
    RightTrigger = 1u << 13,
    DpadDown = 1u << 14,
    DpadUp = 1u << 15,
    DpadRight = 1u << 16,
    DpadLeft = 1u << 17,
    C = 1u << 18,
    LeftPaddle1 = 1u << 19,
    LeftPaddle2 = 1u << 20,
    RightPaddle1 = 1u << 21,
    RightPaddle2 = 1u << 22,
    LeftIrSensor = 1u << 23,
    RightIrSensor = 1u << 24,
    // Append-only physical rail identities. Existing paddle slots keep their
    // horizontal L/ZL/R/ZR roles in already-saved profiles.
    LeftRailSL = 1u << 25,
    LeftRailSR = 1u << 26,
    RightRailSL = 1u << 27,
    RightRailSR = 1u << 28,
}

/// <summary>
/// One unquantized Joy-Con profile axis. Raw Nintendo precision and normalized
/// signed precision remain intact until <c>TryWriteLegacyState</c> explicitly
/// projects them to the existing eight-bit DS4 compatibility surface.
/// </summary>
public readonly struct Switch2JoyConProfileAxis :
    IEquatable<Switch2JoyConProfileAxis>
{
    internal Switch2JoyConProfileAxis(ushort rawValue, short signedValue)
    {
        RawValue = rawValue;
        SignedValue = signedValue;
    }

    public ushort RawValue { get; }

    public short SignedValue { get; }

    public bool Equals(Switch2JoyConProfileAxis other) =>
        RawValue == other.RawValue && SignedValue == other.SignedValue;

    public override bool Equals(object obj) =>
        obj is Switch2JoyConProfileAxis other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(RawValue,
        SignedValue);
}

/// <summary>
/// One physical Joy-Con half copied into a profile result. The stick remains in
/// physical orientation here; standalone rotation is represented by the
/// logical axes on <see cref="Switch2JoyConProfileInputFrame"/>.
/// </summary>
public readonly struct Switch2JoyConProfileSide
{
    internal Switch2JoyConProfileSide(
        in Switch2CanonicalInputFrame canonical,
        in Switch2CalibratedStickPosition stick,
        Switch2JoyConProfileButton buttons, uint knownButtonMask)
    {
        IsPresent = true;
        Model = canonical.Model;
        DeviceGeneration = canonical.DeviceGeneration;
        TransportGeneration = canonical.TransportGeneration;
        CompletionTimestampQpc = canonical.CompletionTimestampQpc;
        QpcFrequency = canonical.QpcFrequency;
        DeviceCounterRaw = canonical.DeviceCounterRaw;
        RawButtonBits = canonical.RawButtonBits;
        Buttons = buttons;
        UnknownButtonBits = canonical.RawButtonBits & ~knownButtonMask;
        PhysicalStickXRaw = stick.Raw.X;
        PhysicalStickYRaw = stick.Raw.Y;
        CalibrationStatus = stick.CalibrationStatus;
        CalibrationFailure = stick.CalibrationFailure;
        HasLocalCalibration = stick.HasLocalCalibration;
        RawStickObservation = new Switch2RawStickObservation(canonical);
        HasCommonMotion = canonical.Report.IsCommon;
        if (HasCommonMotion)
        {
            Switch2CommonInputReport common = canonical.Report.Common;
            MotionTimestamp = common.MotionTimestamp;
            Accelerometer = common.Accelerometer;
            Gyroscope = common.Gyroscope;
            Magnetometer = common.Magnetometer;
            IrX = common.MouseX;
            IrY = common.MouseY;
            IrRoughness = common.MouseRoughness;
            IrDistance = common.MouseDistance;
            HasBatteryStatus = Switch2BatteryStatus.TryCreate(common,
                out Switch2BatteryStatus batteryStatus);
            BatteryStatus = batteryStatus;
        }
    }

    public bool IsPresent { get; }
    public Switch2ControllerModel Model { get; }
    public ulong DeviceGeneration { get; }
    public ulong TransportGeneration { get; }
    public long CompletionTimestampQpc { get; }
    public long QpcFrequency { get; }
    public uint DeviceCounterRaw { get; }
    public uint RawButtonBits { get; }
    public Switch2JoyConProfileButton Buttons { get; }
    public uint UnknownButtonBits { get; }
    public ushort PhysicalStickXRaw { get; }
    public ushort PhysicalStickYRaw { get; }
    public Switch2CalibrationAdoptionStatus CalibrationStatus { get; }
    public Switch2CalibrationAdoptionFailure CalibrationFailure { get; }
    public bool HasLocalCalibration { get; private init; }
    internal Switch2RawStickObservation RawStickObservation { get; }
    internal bool HasValidRawStickObservation => IsPresent && RawStickObservation.Matches(
        Model, Switch2Transport.BluetoothLe, DeviceGeneration, TransportGeneration,
        CompletionTimestampQpc, QpcFrequency, HasCommonMotion);

    internal Switch2JoyConProfileSide WithLocalCalibrationFlag(bool enabled) =>
        this with { HasLocalCalibration = enabled };

    public bool HasCommonMotion { get; }

    public uint MotionTimestamp { get; }

    public Switch2Vector3Raw Accelerometer { get; }

    public Switch2Vector3Raw Gyroscope { get; }

    public Switch2Vector3Raw Magnetometer { get; }

    public ushort IrX { get; }

    public ushort IrY { get; }

    public ushort IrRoughness { get; }

    public ushort IrDistance { get; }

    public bool HasBatteryStatus { get; }

    public Switch2BatteryStatus BatteryStatus { get; }

    internal Switch2JoyConMotionSample ToMotionSample(bool active = true) =>
        new(new Vector3(Gyroscope.X, Gyroscope.Y, Gyroscope.Z),
            new Vector3(Accelerometer.X, Accelerometer.Y,
                Accelerometer.Z), Vector3.Zero,
            HasCommonMotion && active);
}

/// <summary>
/// Immutable ownership/freshness reducer state for one profile lane. A caller
/// creates one state per joined pair or standalone half and publishes only the
/// returned <c>next</c> state after a successful map. Rejections never advance
/// its generation, timestamp, or counter baselines.
/// </summary>
public readonly struct Switch2JoyConProfileMapperState
{
    internal Switch2JoyConProfileMapperState(Switch2JoyConProfileMode mode,
        ulong pairEpoch, in Switch2InputSessionDescriptor leftDescriptor,
        in Switch2InputSessionDescriptor rightDescriptor,
        bool hasAcceptedLeft, long lastLeftTimestampQpc,
        uint lastLeftCounter, bool hasAcceptedRight,
        long lastRightTimestampQpc, uint lastRightCounter)
    {
        Mode = mode;
        PairEpoch = pairEpoch;
        LeftDescriptor = leftDescriptor;
        RightDescriptor = rightDescriptor;
        HasAcceptedLeft = hasAcceptedLeft;
        LastLeftTimestampQpc = lastLeftTimestampQpc;
        LastLeftCounter = lastLeftCounter;
        HasAcceptedRight = hasAcceptedRight;
        LastRightTimestampQpc = lastRightTimestampQpc;
        LastRightCounter = lastRightCounter;
    }

    public Switch2JoyConProfileMode Mode { get; }
    public ulong PairEpoch { get; }
    public Switch2InputSessionDescriptor LeftDescriptor { get; }
    public Switch2InputSessionDescriptor RightDescriptor { get; }
    public bool HasAcceptedLeft { get; }
    public bool HasAcceptedRight { get; }
    public long LastLeftTimestampQpc { get; }
    public long LastRightTimestampQpc { get; }
    public uint LastLeftCounter { get; }
    public uint LastRightCounter { get; }

    public bool IsValid => Mode switch
    {
        Switch2JoyConProfileMode.Joined => PairEpoch != 0 &&
            Switch2JoyConProfileInputMapper.IsCommonJoyConDescriptor(
                LeftDescriptor, Switch2ControllerModel.JoyCon2Left) &&
            Switch2JoyConProfileInputMapper.IsCommonJoyConDescriptor(
                RightDescriptor, Switch2ControllerModel.JoyCon2Right) &&
            LeftDescriptor.QpcFrequency == RightDescriptor.QpcFrequency,
        Switch2JoyConProfileMode.StandaloneHorizontalLeft => PairEpoch == 0 &&
            Switch2JoyConProfileInputMapper.IsCommonJoyConDescriptor(
                LeftDescriptor, Switch2ControllerModel.JoyCon2Left) &&
            !RightDescriptor.IsValid,
        Switch2JoyConProfileMode.StandaloneVerticalLeft => PairEpoch == 0 &&
            Switch2JoyConProfileInputMapper.IsCommonJoyConDescriptor(
                LeftDescriptor, Switch2ControllerModel.JoyCon2Left) &&
            !RightDescriptor.IsValid,
        Switch2JoyConProfileMode.StandaloneHorizontalRight => PairEpoch == 0 &&
            !LeftDescriptor.IsValid &&
            Switch2JoyConProfileInputMapper.IsCommonJoyConDescriptor(
                RightDescriptor, Switch2ControllerModel.JoyCon2Right),
        Switch2JoyConProfileMode.StandaloneVerticalRight => PairEpoch == 0 &&
            !LeftDescriptor.IsValid &&
            Switch2JoyConProfileInputMapper.IsCommonJoyConDescriptor(
                RightDescriptor, Switch2ControllerModel.JoyCon2Right),
        _ => false,
    };
}

/// <summary>
/// High-resolution Joy-Con 2 state at DS4Windows' existing profile boundary.
/// This type performs no discovery, association, I/O, registration, output, or
/// publication. Paddle/rail controls and C stay explicit and are not aliased to
/// unrelated legacy controls.
/// </summary>
public readonly struct Switch2JoyConProfileInputFrame
{
    public const ushort CurrentVersion = 3;

    internal Switch2JoyConProfileInputFrame(Switch2JoyConProfileMode mode,
        ulong pairEpoch, Switch2JoyConProfileButton buttons,
        in Switch2JoyConProfileSide leftSource,
        in Switch2JoyConProfileSide rightSource,
        in Switch2JoyConProfileAxis leftX,
        in Switch2JoyConProfileAxis leftY,
        in Switch2JoyConProfileAxis rightX,
        in Switch2JoyConProfileAxis rightY, bool hasRightStick,
        long completionTimestampQpc, long qpcFrequency)
    {
        Version = CurrentVersion;
        Mode = mode;
        PairEpoch = pairEpoch;
        Buttons = buttons;
        LeftSource = leftSource;
        RightSource = rightSource;
        LeftX = leftX;
        LeftY = leftY;
        RightX = rightX;
        RightY = rightY;
        HasRightStick = hasRightStick;
        CompletionTimestampQpc = completionTimestampQpc;
        QpcFrequency = qpcFrequency;
    }

    public ushort Version { get; }
    public Switch2JoyConProfileMode Mode { get; }
    public ulong PairEpoch { get; }
    public Switch2JoyConProfileButton Buttons { get; }
    public Switch2JoyConProfileSide LeftSource { get; private init; }
    public Switch2JoyConProfileSide RightSource { get; private init; }
    public Switch2JoyConProfileAxis LeftX { get; private init; }
    public Switch2JoyConProfileAxis LeftY { get; private init; }
    public Switch2JoyConProfileAxis RightX { get; private init; }
    public Switch2JoyConProfileAxis RightY { get; private init; }
    public bool HasRightStick { get; }
    public long CompletionTimestampQpc { get; }
    public long QpcFrequency { get; }

    internal Switch2JoyConProfileInputFrame WithLocalStickCalibration(
        in Switch2LocalStickCalibrationOverrides leftLocal,
        in Switch2LocalStickCalibrationOverrides rightLocal)
    {
        if (!leftLocal.HasLeft && !rightLocal.HasRight &&
            !LeftSource.HasLocalCalibration && !RightSource.HasLocalCalibration) return this;

        Switch2JoyConProfileAxis leftX = default, leftY = default;
        Switch2JoyConProfileAxis rightX = default, rightY = default;
        if (LeftSource.IsPresent &&
            (!LeftSource.RawStickObservation.TryGetStick(Switch2StickSide.Left,
                leftLocal, out var left) ||
             !Switch2JoyConProfileInputMapper.TryMapPhysicalStick(Mode,
                Switch2StickSide.Left, left, out leftX, out leftY))) return this;
        if (RightSource.IsPresent)
        {
            if (!RightSource.RawStickObservation.TryGetStick(Switch2StickSide.Right,
                    rightLocal, out var right) ||
                !Switch2JoyConProfileInputMapper.TryMapPhysicalStick(Mode,
                    Switch2StickSide.Right, right, out var x, out var y)) return this;
            if (Mode == Switch2JoyConProfileMode.StandaloneHorizontalRight)
            {
                leftX = x; leftY = y;
            }
            else
            {
                rightX = x; rightY = y;
            }
        }
        return this with
        {
            LeftSource = LeftSource.WithLocalCalibrationFlag(
                LeftSource.IsPresent && leftLocal.HasLeft),
            RightSource = RightSource.WithLocalCalibrationFlag(
                RightSource.IsPresent && rightLocal.HasRight),
            LeftX = leftX, LeftY = leftY, RightX = rightX, RightY = rightY,
        };
    }

    public bool CButton => Has(Switch2JoyConProfileButton.C);
    public bool LeftPaddle1 => Has(Switch2JoyConProfileButton.LeftPaddle1);
    public bool LeftPaddle2 => Has(Switch2JoyConProfileButton.LeftPaddle2);
    public bool RightPaddle1 => Has(Switch2JoyConProfileButton.RightPaddle1);
    public bool RightPaddle2 => Has(Switch2JoyConProfileButton.RightPaddle2);

    /// <summary>
    /// Explicit compatibility projection into the one existing DS4Windows
    /// mapping state. Unsupported controls are cleared on every write. C and
    /// the four paddle/rail semantics remain only in the dedicated sidecar.
    /// </summary>
    public bool TryWriteLegacyState(DS4State destination) =>
        TryWriteLegacyState(destination, Switch2FaceButtonLayout.Xbox);

    public bool TryWriteLegacyState(DS4State destination,
        Switch2FaceButtonLayout faceButtonLayout)
    {
        if (destination == null || Version != CurrentVersion ||
            !Switch2FaceButtonLayoutProjection.IsValid(faceButtonLayout))
        {
            return false;
        }

        destination.LXAxis = DS4MappedStickAxis.FromSigned(LeftX.SignedValue);
        destination.LYAxis = DS4MappedStickAxis.FromSigned(LeftY.SignedValue);
        destination.RXAxis = DS4MappedStickAxis.FromSigned(HasRightStick ? RightX.SignedValue : (short)0);
        destination.RYAxis = DS4MappedStickAxis.FromSigned(HasRightStick ? RightY.SignedValue : (short)0);

        Switch2FaceButtonLayoutProjection.TryProject(faceButtonLayout,
            Has(Switch2JoyConProfileButton.FaceWest),
            Has(Switch2JoyConProfileButton.FaceNorth),
            Has(Switch2JoyConProfileButton.FaceSouth),
            Has(Switch2JoyConProfileButton.FaceEast),
            out destination.Square, out destination.Triangle,
            out destination.Cross, out destination.Circle);
        destination.L1 = Has(Switch2JoyConProfileButton.LeftShoulder);
        destination.R1 = Has(Switch2JoyConProfileButton.RightShoulder);
        destination.L3 = Has(Switch2JoyConProfileButton.LeftStick);
        destination.R3 = Has(Switch2JoyConProfileButton.RightStick);

        bool leftTrigger = Has(Switch2JoyConProfileButton.LeftTrigger);
        bool rightTrigger = Has(Switch2JoyConProfileButton.RightTrigger);
        destination.L2Btn = leftTrigger;
        destination.L2 = destination.L2Raw = leftTrigger ? byte.MaxValue :
            (byte)0;
        destination.R2Btn = rightTrigger;
        destination.R2 = destination.R2Raw = rightTrigger ? byte.MaxValue :
            (byte)0;

        destination.Share = Has(Switch2JoyConProfileButton.Back);
        destination.Options = Has(Switch2JoyConProfileButton.Start);
        destination.PS = Has(Switch2JoyConProfileButton.Guide);
        destination.Capture = Has(Switch2JoyConProfileButton.Capture);
        destination.DpadUp = Has(Switch2JoyConProfileButton.DpadUp);
        destination.DpadRight = Has(Switch2JoyConProfileButton.DpadRight);
        destination.DpadDown = Has(Switch2JoyConProfileButton.DpadDown);
        destination.DpadLeft = Has(Switch2JoyConProfileButton.DpadLeft);

        // There is no source-pinned, lossless legacy identity for all four
        // horizontal rail/paddle controls. Preserve them only in the sidecar.
        destination.BLP = false;
        destination.BRP = false;
        destination.Mute = false;
        destination.FnL = false;
        destination.FnR = false;
        destination.SideL = false;
        destination.SideR = false;
        destination.Touch1 = false;
        destination.Touch2 = false;
        destination.TouchButton = false;
        destination.OutputTouchButton = false;
        destination.TouchRight = false;
        destination.TouchLeft = false;
        destination.Touch1Finger = false;
        destination.Touch2Fingers = false;
        destination.TrackPadTouch0 = default;
        destination.TrackPadTouch1 = default;
        destination.OutputLSOuter = 0;
        destination.OutputRSOuter = 0;
        destination.SASteeringWheelEmulationUnit = 0;
        destination.DualSenseRawInputStatus = default;
        destination.Switch2RawInputStatus = default;
        destination.Switch2JoyConRawInputStatus = BuildRawStatus();
        return true;
    }

    private Switch2JoyConRawInputStatus BuildRawStatus() => new()
    {
        IsValid = true,
        ContractVersion = Version,
        Mode = Mode,
        PairEpoch = PairEpoch,
        CompletionTimestampQpc = CompletionTimestampQpc,
        QpcFrequency = QpcFrequency,
        LeftPresent = LeftSource.IsPresent,
        LeftDeviceGeneration = LeftSource.DeviceGeneration,
        LeftTransportGeneration = LeftSource.TransportGeneration,
        LeftDeviceCounterRaw = LeftSource.DeviceCounterRaw,
        LeftRawButtonBits = LeftSource.RawButtonBits,
        LeftUnknownButtonBits = LeftSource.UnknownButtonBits,
        LeftPhysicalStickXRaw = LeftSource.PhysicalStickXRaw,
        LeftPhysicalStickYRaw = LeftSource.PhysicalStickYRaw,
        LeftHasCommonMotion = LeftSource.HasCommonMotion,
        LeftMotionTimestamp = LeftSource.MotionTimestamp,
        LeftAccelerometer = LeftSource.Accelerometer,
        LeftGyroscope = LeftSource.Gyroscope,
        LeftMagnetometer = LeftSource.Magnetometer,
        LeftIrX = LeftSource.IrX,
        LeftIrY = LeftSource.IrY,
        LeftIrRoughness = LeftSource.IrRoughness,
        LeftIrDistance = LeftSource.IrDistance,
        RightPresent = RightSource.IsPresent,
        RightDeviceGeneration = RightSource.DeviceGeneration,
        RightTransportGeneration = RightSource.TransportGeneration,
        RightDeviceCounterRaw = RightSource.DeviceCounterRaw,
        RightRawButtonBits = RightSource.RawButtonBits,
        RightUnknownButtonBits = RightSource.UnknownButtonBits,
        RightPhysicalStickXRaw = RightSource.PhysicalStickXRaw,
        RightPhysicalStickYRaw = RightSource.PhysicalStickYRaw,
        RightHasCommonMotion = RightSource.HasCommonMotion,
        RightMotionTimestamp = RightSource.MotionTimestamp,
        RightAccelerometer = RightSource.Accelerometer,
        RightGyroscope = RightSource.Gyroscope,
        RightMagnetometer = RightSource.Magnetometer,
        RightIrX = RightSource.IrX,
        RightIrY = RightSource.IrY,
        RightIrRoughness = RightSource.IrRoughness,
        RightIrDistance = RightSource.IrDistance,
        LogicalLeftStickX = LeftX.SignedValue,
        LogicalLeftStickY = LeftY.SignedValue,
        LogicalRightStickX = HasRightStick ? RightX.SignedValue : (short)0,
        LogicalRightStickY = HasRightStick ? RightY.SignedValue : (short)0,
        CButton = CButton,
        LeftPaddle1 = LeftPaddle1,
        LeftPaddle2 = LeftPaddle2,
        RightPaddle1 = RightPaddle1,
        RightPaddle2 = RightPaddle2,
        LeftRailSL = (LeftSource.Buttons & Switch2JoyConProfileButton.LeftRailSL) != 0,
        LeftRailSR = (LeftSource.Buttons & Switch2JoyConProfileButton.LeftRailSR) != 0,
        RightRailSL = (RightSource.Buttons & Switch2JoyConProfileButton.RightRailSL) != 0,
        RightRailSR = (RightSource.Buttons & Switch2JoyConProfileButton.RightRailSR) != 0,
    };

    private bool Has(Switch2JoyConProfileButton button) =>
        (Buttons & button) != 0;
}

/// <summary>
/// Source-pinned Common05 Joy-Con 2 mapper. Raw masks and rotations follow SDL
/// commit c71abd08605b8bb7078372307a93274725c99fe0 functions
/// HandleCombinedControllerStateL/R and HandleMiniControllerStateL/R. Physical
/// SL/SR identities additionally follow Switch2Connect GPL-3.0 commit
/// 61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py btn_states.
/// Pro GL/GR bits are not Joy-Con buttons. Mini-controller legacy roles are
/// retained separately from append-only physical rail controls.
/// </summary>
public static class Switch2JoyConProfileInputMapper
{
    public const uint CombinedLeftKnownButtonMask =
        (1u << 8) | (1u << 11) | (1u << 13) |
        (0xFFu << 16);
    public const uint CombinedRightKnownButtonMask =
        0xFFu | (1u << 9) | (1u << 10) | (1u << 12) |
        (1u << 14);
    public const uint MiniLeftKnownButtonMask =
        (1u << 8) | (1u << 11) | (1u << 12) | (1u << 13) |
        (0xFFu << 16);
    public const uint MiniRightKnownButtonMask =
        0xFFu | (1u << 9) | (1u << 10) | (1u << 12) | (1u << 14);

    public static bool TryCreateJoined(ulong pairEpoch,
        in Switch2InputSessionDescriptor leftDescriptor,
        in Switch2InputSessionDescriptor rightDescriptor,
        out Switch2JoyConProfileMapperState state)
    {
        if (pairEpoch == 0 ||
            !IsCommonJoyConDescriptor(leftDescriptor,
                Switch2ControllerModel.JoyCon2Left) ||
            !IsCommonJoyConDescriptor(rightDescriptor,
                Switch2ControllerModel.JoyCon2Right) ||
            leftDescriptor.QpcFrequency != rightDescriptor.QpcFrequency)
        {
            state = default;
            return false;
        }

        state = new Switch2JoyConProfileMapperState(
            Switch2JoyConProfileMode.Joined, pairEpoch, leftDescriptor,
            rightDescriptor, false, 0, 0, false, 0, 0);
        return true;
    }

    public static bool TryCreateStandalone(Switch2JoyConProfileMode mode,
        in Switch2InputSessionDescriptor descriptor,
        out Switch2JoyConProfileMapperState state)
    {
        Switch2ControllerModel model = mode switch
        {
            Switch2JoyConProfileMode.StandaloneHorizontalLeft or
                Switch2JoyConProfileMode.StandaloneVerticalLeft =>
                Switch2ControllerModel.JoyCon2Left,
            Switch2JoyConProfileMode.StandaloneHorizontalRight or
                Switch2JoyConProfileMode.StandaloneVerticalRight =>
                Switch2ControllerModel.JoyCon2Right,
            _ => Switch2ControllerModel.Unknown,
        };
        if (model == Switch2ControllerModel.Unknown ||
            !IsCommonJoyConDescriptor(descriptor, model))
        {
            state = default;
            return false;
        }

        Switch2InputSessionDescriptor left = model ==
            Switch2ControllerModel.JoyCon2Left ? descriptor : default;
        Switch2InputSessionDescriptor right = model ==
            Switch2ControllerModel.JoyCon2Right ? descriptor : default;
        state = new Switch2JoyConProfileMapperState(mode, 0, left, right,
            false, 0, 0, false, 0, 0);
        return true;
    }

    /// <summary>
    /// Selects a new presentation orientation while retaining the exact
    /// descriptor, generation, timestamp, and counter fences already owned by
    /// this mapper. Cross-side changes fail closed.
    /// </summary>
    public static bool TrySelectStandaloneMode(
        in Switch2JoyConProfileMapperState state,
        Switch2JoyConProfileMode selectedMode,
        out Switch2JoyConProfileMapperState selected)
    {
        bool currentLeft = IsStandaloneLeftMode(state.Mode);
        bool currentRight = IsStandaloneRightMode(state.Mode);
        bool selectedLeft = IsStandaloneLeftMode(selectedMode);
        bool selectedRight = IsStandaloneRightMode(selectedMode);
        if (!state.IsValid || (!currentLeft && !currentRight) ||
            currentLeft != selectedLeft || currentRight != selectedRight)
        {
            selected = state;
            return false;
        }

        selected = new Switch2JoyConProfileMapperState(selectedMode, 0,
            state.LeftDescriptor, state.RightDescriptor,
            state.HasAcceptedLeft, state.LastLeftTimestampQpc,
            state.LastLeftCounter, state.HasAcceptedRight,
            state.LastRightTimestampQpc, state.LastRightCounter);
        return selected.IsValid;
    }

    public static Switch2JoyConProfileMode StandaloneModeFor(
        Switch2ControllerModel model, Switch2JoyConHoldMode holdMode) =>
        (model, holdMode) switch
        {
            (Switch2ControllerModel.JoyCon2Left,
                Switch2JoyConHoldMode.Vertical) =>
                Switch2JoyConProfileMode.StandaloneVerticalLeft,
            (Switch2ControllerModel.JoyCon2Left,
                Switch2JoyConHoldMode.Horizontal) =>
                Switch2JoyConProfileMode.StandaloneHorizontalLeft,
            (Switch2ControllerModel.JoyCon2Right,
                Switch2JoyConHoldMode.Vertical) =>
                Switch2JoyConProfileMode.StandaloneVerticalRight,
            (Switch2ControllerModel.JoyCon2Right,
                Switch2JoyConHoldMode.Horizontal) =>
                Switch2JoyConProfileMode.StandaloneHorizontalRight,
            _ => Switch2JoyConProfileMode.Invalid,
        };

    public static bool TryMapJoined(
        in Switch2JoyConProfileMapperState state,
        in Switch2JoyConPairSnapshot snapshot,
        out Switch2JoyConProfileMapperState next,
        out Switch2JoyConProfileInputFrame frame,
        out Switch2JoyConProfileInputFailure failure)
    {
        if (!state.IsValid)
        {
            return Fail(state,
                Switch2JoyConProfileInputFailure.InvalidMapperState,
                out next, out frame, out failure);
        }
        if (state.Mode != Switch2JoyConProfileMode.Joined)
        {
            return Fail(state,
                Switch2JoyConProfileInputFailure.WrongMapperMode,
                out next, out frame, out failure);
        }
        if (snapshot.PairEpoch != state.PairEpoch)
        {
            return Fail(state,
                Switch2JoyConProfileInputFailure.PairEpochMismatch,
                out next, out frame, out failure);
        }
        if (!TryAdmitJoinedHalf(state, Switch2StickSide.Left,
                snapshot.Left, out failure) ||
            !TryAdmitJoinedHalf(state, Switch2StickSide.Right,
                snapshot.Right, out failure))
        {
            next = state;
            frame = default;
            return false;
        }
        long leftTimestamp = snapshot.Left.CompletionTimestampQpc;
        long rightTimestamp = snapshot.Right.CompletionTimestampQpc;
        ulong expectedSkew = leftTimestamp >= rightTimestamp ?
            (ulong)(leftTimestamp - rightTimestamp) :
            (ulong)(rightTimestamp - leftTimestamp);
        if (snapshot.QpcFrequency != state.LeftDescriptor.QpcFrequency ||
            snapshot.CompletionTimestampQpc != Math.Max(leftTimestamp,
                rightTimestamp) || snapshot.SkewQpcTicks != expectedSkew)
        {
            return Fail(state,
                Switch2JoyConProfileInputFailure.LifetimeMismatch,
                out next, out frame, out failure);
        }
        if (!snapshot.Left.TryGetLeftStick(out var leftStick) ||
            !snapshot.Right.TryGetRightStick(out var rightStick))
        {
            return Fail(state,
                Switch2JoyConProfileInputFailure.InvalidCalibration,
                out next, out frame, out failure);
        }
        if (!TryMapPhysicalStick(state.Mode, Switch2StickSide.Left,
                leftStick, out var leftX, out var leftY) ||
            !TryMapPhysicalStick(state.Mode, Switch2StickSide.Right,
                rightStick, out var rightX, out var rightY))
        {
            return Fail(state, Switch2JoyConProfileInputFailure.InvalidAxis,
                out next, out frame, out failure);
        }

        Switch2JoyConProfileButton leftButtons = MapCombinedLeftButtons(
            snapshot.Left.RawButtonBits);
        Switch2JoyConProfileButton rightButtons = MapCombinedRightButtons(
            snapshot.Right.RawButtonBits);
        Switch2JoyConProfileButton buttons = leftButtons | rightButtons;
        var leftSource = new Switch2JoyConProfileSide(snapshot.Left,
            leftStick, leftButtons, CombinedLeftKnownButtonMask);
        var rightSource = new Switch2JoyConProfileSide(snapshot.Right,
            rightStick, rightButtons, CombinedRightKnownButtonMask);
        frame = new Switch2JoyConProfileInputFrame(state.Mode,
            state.PairEpoch, buttons, leftSource, rightSource, leftX, leftY,
            rightX, rightY, true, snapshot.CompletionTimestampQpc,
            snapshot.QpcFrequency);
        next = new Switch2JoyConProfileMapperState(state.Mode,
            state.PairEpoch, state.LeftDescriptor, state.RightDescriptor,
            true, snapshot.Left.CompletionTimestampQpc,
            snapshot.Left.DeviceCounterRaw, true,
            snapshot.Right.CompletionTimestampQpc,
            snapshot.Right.DeviceCounterRaw);
        failure = Switch2JoyConProfileInputFailure.None;
        return true;
    }

    public static bool TryMapStandalone(
        in Switch2JoyConProfileMapperState state,
        in Switch2CanonicalInputFrame canonical,
        out Switch2JoyConProfileMapperState next,
        out Switch2JoyConProfileInputFrame frame,
        out Switch2JoyConProfileInputFailure failure)
    {
        if (!state.IsValid)
        {
            return Fail(state,
                Switch2JoyConProfileInputFailure.InvalidMapperState,
                out next, out frame, out failure);
        }
        bool isLeft = IsStandaloneLeftMode(state.Mode);
        bool isRight = IsStandaloneRightMode(state.Mode);
        if (!isLeft && !isRight)
        {
            return Fail(state,
                Switch2JoyConProfileInputFailure.WrongMapperMode,
                out next, out frame, out failure);
        }

        Switch2InputSessionDescriptor expected = isLeft ?
            state.LeftDescriptor : state.RightDescriptor;
        bool hasAccepted = isLeft ? state.HasAcceptedLeft :
            state.HasAcceptedRight;
        long lastTimestamp = isLeft ? state.LastLeftTimestampQpc :
            state.LastRightTimestampQpc;
        uint lastCounter = isLeft ? state.LastLeftCounter :
            state.LastRightCounter;
        Switch2ControllerModel expectedModel = isLeft ?
            Switch2ControllerModel.JoyCon2Left :
            Switch2ControllerModel.JoyCon2Right;
        if (!TryValidateCanonical(canonical, expected, expectedModel,
                hasAccepted, lastTimestamp, lastCounter, out failure))
        {
            next = state;
            frame = default;
            return false;
        }

        bool hasStick = isLeft ?
            canonical.TryGetLeftStick(out var physicalStick) :
            canonical.TryGetRightStick(out physicalStick);
        if (!hasStick)
        {
            return Fail(state,
                Switch2JoyConProfileInputFailure.InvalidCalibration,
                out next, out frame, out failure);
        }

        bool horizontal = state.Mode is
            Switch2JoyConProfileMode.StandaloneHorizontalLeft or
            Switch2JoyConProfileMode.StandaloneHorizontalRight;
        if (!TryMapPhysicalStick(state.Mode,
                isLeft ? Switch2StickSide.Left : Switch2StickSide.Right,
                physicalStick, out var logicalX, out var logicalY))
        {
            return Fail(state, Switch2JoyConProfileInputFailure.InvalidAxis,
                out next, out frame, out failure);
        }

        uint knownMask = horizontal ?
            (isLeft ? MiniLeftKnownButtonMask : MiniRightKnownButtonMask) :
            (isLeft ? CombinedLeftKnownButtonMask :
                CombinedRightKnownButtonMask);
        Switch2JoyConProfileButton buttons = horizontal ?
            (isLeft ? MapMiniLeftButtons(canonical.RawButtonBits) :
                MapMiniRightButtons(canonical.RawButtonBits)) :
            (isLeft ? MapCombinedLeftButtons(canonical.RawButtonBits) :
                MapCombinedRightButtons(canonical.RawButtonBits));
        var source = new Switch2JoyConProfileSide(canonical, physicalStick,
            buttons | ReadPhysicalRails(canonical.RawButtonBits, isLeft),
            knownMask);
        Switch2JoyConProfileSide leftSource = isLeft ? source : default;
        Switch2JoyConProfileSide rightSource = isRight ? source : default;
        Switch2JoyConProfileAxis leftX = horizontal || isLeft ? logicalX :
            default;
        Switch2JoyConProfileAxis leftY = horizontal || isLeft ? logicalY :
            default;
        Switch2JoyConProfileAxis rightX = !horizontal && isRight ? logicalX :
            default;
        Switch2JoyConProfileAxis rightY = !horizontal && isRight ? logicalY :
            default;
        frame = new Switch2JoyConProfileInputFrame(state.Mode, 0, buttons,
            leftSource, rightSource, leftX, leftY, rightX, rightY,
            hasRightStick: !horizontal && isRight,
            canonical.CompletionTimestampQpc, canonical.QpcFrequency);
        next = new Switch2JoyConProfileMapperState(state.Mode, 0,
            state.LeftDescriptor, state.RightDescriptor,
            isLeft || state.HasAcceptedLeft,
            isLeft ? canonical.CompletionTimestampQpc :
                state.LastLeftTimestampQpc,
            isLeft ? canonical.DeviceCounterRaw : state.LastLeftCounter,
            isRight || state.HasAcceptedRight,
            isRight ? canonical.CompletionTimestampQpc :
                state.LastRightTimestampQpc,
            isRight ? canonical.DeviceCounterRaw : state.LastRightCounter);
        failure = Switch2JoyConProfileInputFailure.None;
        return true;
    }

    internal static bool IsStandaloneLeftMode(
        Switch2JoyConProfileMode mode) => mode is
            Switch2JoyConProfileMode.StandaloneHorizontalLeft or
            Switch2JoyConProfileMode.StandaloneVerticalLeft;

    internal static bool IsStandaloneRightMode(
        Switch2JoyConProfileMode mode) => mode is
            Switch2JoyConProfileMode.StandaloneHorizontalRight or
            Switch2JoyConProfileMode.StandaloneVerticalRight;

    internal static bool IsCommonJoyConDescriptor(
        in Switch2InputSessionDescriptor descriptor,
        Switch2ControllerModel expectedModel)
    {
        if (!descriptor.IsValid || descriptor.Identity.Model != expectedModel ||
            descriptor.Identity.Transport != Switch2Transport.BluetoothLe ||
            descriptor.Identity.ProtocolRevision !=
                Switch2InputProtocolRevision.BluetoothLeCommon05V1 ||
            descriptor.Identity.ServiceUuid != Switch2InputCodec.ServiceUuid ||
            descriptor.Identity.CharacteristicUuid !=
                Switch2InputCodec.Common05CharacteristicUuid)
        {
            return false;
        }
        Switch2GattProperty required = Switch2GattProperty.Read |
            Switch2GattProperty.Notify;
        return descriptor.Identity.GattProperties == required;
    }

    /// <summary>
    /// The single admission policy shared by joined mapping and the serialized
    /// pair/profile coordinator. This check never advances mapper state.
    /// </summary>
    internal static bool TryAdmitJoinedHalf(
        in Switch2JoyConProfileMapperState state,
        Switch2StickSide expectedSide,
        in Switch2CanonicalInputFrame canonical,
        out Switch2JoyConProfileInputFailure failure)
    {
        if (!state.IsValid)
        {
            failure = Switch2JoyConProfileInputFailure.InvalidMapperState;
            return false;
        }
        if (state.Mode != Switch2JoyConProfileMode.Joined)
        {
            failure = Switch2JoyConProfileInputFailure.WrongMapperMode;
            return false;
        }

        if (expectedSide == Switch2StickSide.Left &&
            canonical.Model == Switch2ControllerModel.JoyCon2Left)
        {
            return TryValidateCanonical(canonical, state.LeftDescriptor,
                Switch2ControllerModel.JoyCon2Left,
                state.HasAcceptedLeft, state.LastLeftTimestampQpc,
                state.LastLeftCounter, out failure);
        }
        if (expectedSide == Switch2StickSide.Right &&
            canonical.Model == Switch2ControllerModel.JoyCon2Right)
        {
            return TryValidateCanonical(canonical, state.RightDescriptor,
                Switch2ControllerModel.JoyCon2Right,
                state.HasAcceptedRight, state.LastRightTimestampQpc,
                state.LastRightCounter, out failure);
        }

        failure = Switch2JoyConProfileInputFailure.UnsupportedIdentity;
        return false;
    }

    private static bool TryValidateCanonical(
        in Switch2CanonicalInputFrame canonical,
        in Switch2InputSessionDescriptor expected,
        Switch2ControllerModel expectedModel, bool hasAccepted,
        long lastTimestampQpc, uint lastCounter,
        out Switch2JoyConProfileInputFailure failure)
    {
        if (canonical.Version != Switch2CanonicalInputFrame.CurrentVersion ||
            !canonical.Descriptor.IsValid)
        {
            failure = Switch2JoyConProfileInputFailure.InvalidCanonicalFrame;
            return false;
        }
        if (!canonical.Report.IsCommon ||
            canonical.Report.Kind != Switch2InputReportKind.Common05)
        {
            failure = Switch2JoyConProfileInputFailure.UnsupportedReport;
            return false;
        }
        if (canonical.Model != expectedModel ||
            canonical.Transport != Switch2Transport.BluetoothLe ||
            canonical.ProtocolRevision !=
                Switch2InputProtocolRevision.BluetoothLeCommon05V1)
        {
            failure = Switch2JoyConProfileInputFailure.UnsupportedIdentity;
            return false;
        }
        if (!canonical.Descriptor.Equals(expected))
        {
            failure = Switch2JoyConProfileInputFailure.LifetimeMismatch;
            return false;
        }
        if (!canonical.Calibration.IsValid ||
            canonical.Calibration.Model != expectedModel ||
            canonical.Calibration.DeviceGeneration !=
                canonical.DeviceGeneration)
        {
            failure = Switch2JoyConProfileInputFailure.InvalidCalibration;
            return false;
        }
        if (canonical.CounterWidthBits != 32 ||
            canonical.CounterSequence ==
                Switch2CounterSequenceKind.BackwardOrOutOfOrder)
        {
            failure = Switch2JoyConProfileInputFailure.BackwardOrOutOfOrder;
            return false;
        }
        if (hasAccepted)
        {
            if (canonical.CompletionTimestampQpc < lastTimestampQpc)
            {
                failure = Switch2JoyConProfileInputFailure.StaleObservation;
                return false;
            }
            Switch2CounterSequenceKind localSequence =
                Switch2CounterSequence.Classify(canonical.DeviceCounterRaw,
                    lastCounter, 32, out _);
            if (localSequence ==
                Switch2CounterSequenceKind.BackwardOrOutOfOrder)
            {
                failure =
                    Switch2JoyConProfileInputFailure.BackwardOrOutOfOrder;
                return false;
            }
        }

        failure = Switch2JoyConProfileInputFailure.None;
        return true;
    }

    internal static bool TryMapPhysicalStick(Switch2JoyConProfileMode mode,
        Switch2StickSide side, in Switch2CalibratedStickPosition stick,
        out Switch2JoyConProfileAxis x, out Switch2JoyConProfileAxis y)
    {
        x = y = default;
        bool left = side == Switch2StickSide.Left;
        if (side is not (Switch2StickSide.Left or Switch2StickSide.Right) ||
            (mode != Switch2JoyConProfileMode.Joined &&
                !(left ? IsStandaloneLeftMode(mode) : IsStandaloneRightMode(mode)))) return false;
        bool horizontal = mode is Switch2JoyConProfileMode.StandaloneHorizontalLeft or
            Switch2JoyConProfileMode.StandaloneHorizontalRight;
        // SDL mini-left: X=-physical Y, Y=-physical X; mini-right: X=Y, Y=X.
        // Vertical and joined: X=physical X, Y=-physical Y.
        return TryMapPhysicalAxis(stick, useY: horizontal,
                   invert: horizontal && left, out x) &&
               TryMapPhysicalAxis(stick, useY: !horizontal,
                   invert: !horizontal || left, out y);
    }

    private static bool TryMapPhysicalAxis(
        in Switch2CalibratedStickPosition stick, bool useY, bool invert,
        out Switch2JoyConProfileAxis axis)
    {
        ushort raw = useY ? stick.Raw.Y : stick.Raw.X;
        int offset = useY ? stick.OffsetY : stick.OffsetX;
        ushort negativeRange = useY ? stick.NegativeRangeY :
            stick.NegativeRangeX;
        ushort positiveRange = useY ? stick.PositiveRangeY :
            stick.PositiveRangeX;
        if (!Switch2ProfileAxisProjection.TryMapSigned(raw, offset,
                negativeRange, positiveRange, invert, out short signed))
        {
            axis = default;
            return false;
        }
        axis = new Switch2JoyConProfileAxis(raw, signed);
        return true;
    }

    private static Switch2JoyConProfileButton MapCombinedLeftButtons(uint raw)
    {
        Switch2JoyConProfileButton buttons = 0;
        Add(ref buttons, raw, 1u << 8, Switch2JoyConProfileButton.Back);
        Add(ref buttons, raw, 1u << 11,
            Switch2JoyConProfileButton.LeftStick);
        Add(ref buttons, raw, 1u << 13,
            Switch2JoyConProfileButton.Capture);
        Add(ref buttons, raw, 1u << 16,
            Switch2JoyConProfileButton.DpadDown);
        Add(ref buttons, raw, 1u << 17,
            Switch2JoyConProfileButton.DpadUp);
        Add(ref buttons, raw, 1u << 18,
            Switch2JoyConProfileButton.DpadRight);
        Add(ref buttons, raw, 1u << 19,
            Switch2JoyConProfileButton.DpadLeft);
        Add(ref buttons, raw, 1u << 22,
            Switch2JoyConProfileButton.LeftShoulder);
        Add(ref buttons, raw, 1u << 23,
            Switch2JoyConProfileButton.LeftTrigger);
        return buttons | ReadPhysicalRails(raw, left: true);
    }

    private static Switch2JoyConProfileButton MapCombinedRightButtons(uint raw)
    {
        Switch2JoyConProfileButton buttons = 0;
        Add(ref buttons, raw, 1u << 0,
            Switch2JoyConProfileButton.FaceWest);
        Add(ref buttons, raw, 1u << 1,
            Switch2JoyConProfileButton.FaceNorth);
        Add(ref buttons, raw, 1u << 2,
            Switch2JoyConProfileButton.FaceSouth);
        Add(ref buttons, raw, 1u << 3,
            Switch2JoyConProfileButton.FaceEast);
        Add(ref buttons, raw, 1u << 6,
            Switch2JoyConProfileButton.RightShoulder);
        Add(ref buttons, raw, 1u << 7,
            Switch2JoyConProfileButton.RightTrigger);
        Add(ref buttons, raw, 1u << 9, Switch2JoyConProfileButton.Start);
        Add(ref buttons, raw, 1u << 10,
            Switch2JoyConProfileButton.RightStick);
        Add(ref buttons, raw, 1u << 12, Switch2JoyConProfileButton.Guide);
        Add(ref buttons, raw, 1u << 14, Switch2JoyConProfileButton.C);
        return buttons | ReadPhysicalRails(raw, left: false);
    }

    private static Switch2JoyConProfileButton MapMiniLeftButtons(uint raw)
    {
        Switch2JoyConProfileButton buttons = 0;
        Add(ref buttons, raw, 1u << 8, Switch2JoyConProfileButton.Start);
        Add(ref buttons, raw, 1u << 11,
            Switch2JoyConProfileButton.LeftStick);
        Add(ref buttons, raw, 1u << 13, Switch2JoyConProfileButton.Guide);
        Add(ref buttons, raw, 1u << 12,
            Switch2JoyConProfileButton.Capture);
        Add(ref buttons, raw, 1u << 16,
            Switch2JoyConProfileButton.FaceWest);
        Add(ref buttons, raw, 1u << 17,
            Switch2JoyConProfileButton.FaceNorth);
        Add(ref buttons, raw, 1u << 18,
            Switch2JoyConProfileButton.FaceSouth);
        Add(ref buttons, raw, 1u << 19,
            Switch2JoyConProfileButton.FaceEast);
        Add(ref buttons, raw, 1u << 20,
            Switch2JoyConProfileButton.RightShoulder);
        Add(ref buttons, raw, 1u << 21,
            Switch2JoyConProfileButton.LeftShoulder);
        Add(ref buttons, raw, 1u << 22,
            Switch2JoyConProfileButton.LeftPaddle1);
        Add(ref buttons, raw, 1u << 23,
            Switch2JoyConProfileButton.LeftPaddle2);
        return buttons;
    }

    private static Switch2JoyConProfileButton MapMiniRightButtons(uint raw)
    {
        Switch2JoyConProfileButton buttons = 0;
        Add(ref buttons, raw, 1u << 0,
            Switch2JoyConProfileButton.FaceWest);
        Add(ref buttons, raw, 1u << 1,
            Switch2JoyConProfileButton.FaceNorth);
        Add(ref buttons, raw, 1u << 2,
            Switch2JoyConProfileButton.FaceSouth);
        Add(ref buttons, raw, 1u << 3,
            Switch2JoyConProfileButton.FaceEast);
        Add(ref buttons, raw, 1u << 4,
            Switch2JoyConProfileButton.RightShoulder);
        Add(ref buttons, raw, 1u << 5,
            Switch2JoyConProfileButton.LeftShoulder);
        Add(ref buttons, raw, 1u << 6,
            Switch2JoyConProfileButton.RightPaddle1);
        Add(ref buttons, raw, 1u << 7,
            Switch2JoyConProfileButton.RightPaddle2);
        Add(ref buttons, raw, 1u << 9, Switch2JoyConProfileButton.Start);
        Add(ref buttons, raw, 1u << 10,
            Switch2JoyConProfileButton.LeftStick);
        Add(ref buttons, raw, 1u << 12, Switch2JoyConProfileButton.Guide);
        Add(ref buttons, raw, 1u << 14, Switch2JoyConProfileButton.C);
        return buttons;
    }

    private static Switch2JoyConProfileButton ReadPhysicalRails(uint raw, bool left)
    {
        Switch2JoyConProfileButton buttons = 0;
        Add(ref buttons, raw, 1u << (left ? 21 : 5), left ?
            Switch2JoyConProfileButton.LeftRailSL : Switch2JoyConProfileButton.RightRailSL);
        Add(ref buttons, raw, 1u << (left ? 20 : 4), left ?
            Switch2JoyConProfileButton.LeftRailSR : Switch2JoyConProfileButton.RightRailSR);
        return buttons;
    }

    private static void Add(ref Switch2JoyConProfileButton buttons, uint raw,
        uint rawMask, Switch2JoyConProfileButton semantic)
    {
        if ((raw & rawMask) != 0)
        {
            buttons |= semantic;
        }
    }

    private static bool Fail(in Switch2JoyConProfileMapperState state,
        Switch2JoyConProfileInputFailure reason,
        out Switch2JoyConProfileMapperState next,
        out Switch2JoyConProfileInputFrame frame,
        out Switch2JoyConProfileInputFailure failure)
    {
        next = state;
        frame = default;
        failure = reason;
        return false;
    }
}
