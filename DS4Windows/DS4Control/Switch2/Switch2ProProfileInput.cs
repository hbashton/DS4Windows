/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows.Switch2;

public enum Switch2ProProfileInputFailure : byte
{
    None = 0,
    InvalidCanonicalFrame,
    UnsupportedIdentity,
    UnsupportedReport,
    InvalidCalibration,
    BackwardOrOutOfOrder,
    InvalidAxis,
}

/// <summary>
/// One calibrated profile axis. <see cref="SignedValue"/> follows the common
/// gamepad convention: negative is left/up and positive is right/down. The raw
/// 12-bit value is retained so the compatibility projection cannot erase source
/// precision.
/// </summary>
public readonly struct Switch2ProfileAxis : IEquatable<Switch2ProfileAxis>
{
    internal Switch2ProfileAxis(ushort rawValue, short signedValue,
        byte legacyValue)
    {
        RawValue = rawValue;
        SignedValue = signedValue;
        LegacyValue = legacyValue;
    }

    public ushort RawValue { get; }

    public short SignedValue { get; }

    /// <summary>
    /// Explicit compatibility quantization for DS4Windows' existing 8-bit
    /// mapping fields. Center is 128 and the endpoints are 0/255.
    /// </summary>
    public byte LegacyValue { get; }

    public bool Equals(Switch2ProfileAxis other) =>
        RawValue == other.RawValue && SignedValue == other.SignedValue &&
        LegacyValue == other.LegacyValue;

    public override bool Equals(object obj) =>
        obj is Switch2ProfileAxis other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(RawValue,
        SignedValue, LegacyValue);
}

/// <summary>
/// Source-owned, high-resolution Switch 2 Pro input at the existing
/// DS4Windows profile boundary. This is not a second mapping stack: callers may
/// write the compatibility fields into the ordinary <see cref="DS4State"/>,
/// while the exact source observation travels as metadata through the same
/// mapping copies.
/// </summary>
public readonly struct Switch2ProProfileInputFrame
{
    public const ushort CurrentVersion = 1;

    internal Switch2ProProfileInputFrame(
        in Switch2CanonicalInputFrame canonical, Switch2ProButton buttons,
        in Switch2ProfileAxis leftX, in Switch2ProfileAxis leftY,
        in Switch2ProfileAxis rightX, in Switch2ProfileAxis rightY)
    {
        Version = CurrentVersion;
        Transport = canonical.Transport;
        ProtocolRevision = canonical.ProtocolRevision;
        DeviceGeneration = canonical.DeviceGeneration;
        TransportGeneration = canonical.TransportGeneration;
        CompletionTimestampQpc = canonical.CompletionTimestampQpc;
        QpcFrequency = canonical.QpcFrequency;
        DeviceCounterRaw = canonical.DeviceCounterRaw;
        HasCounterDelta = canonical.HasCounterDelta;
        CounterDeltaRaw = canonical.CounterDeltaRaw;
        CounterSequence = canonical.CounterSequence;
        Buttons = buttons;
        RawButtonBits = canonical.RawButtonBits;
        UnknownButtonBits = canonical.UnknownButtonBits;
        LeftX = leftX;
        LeftY = leftY;
        RightX = rightX;
        RightY = rightY;
        HasCommonMotion = canonical.Report.IsCommon;
        if (HasCommonMotion)
        {
            Switch2CommonInputReport common = canonical.Report.Common;
            MotionTimestamp = common.MotionTimestamp;
            Accelerometer = common.Accelerometer;
            Gyroscope = common.Gyroscope;
            Magnetometer = common.Magnetometer;
            HasBatteryStatus = Switch2BatteryStatus.TryCreate(common,
                out Switch2BatteryStatus batteryStatus);
            BatteryStatus = batteryStatus;
        }
        LeftCalibrationStatus = canonical.Calibration.Left.Status;
        LeftCalibrationFailure = canonical.Calibration.Left.Failure;
        RightCalibrationStatus = canonical.Calibration.Right.Status;
        RightCalibrationFailure = canonical.Calibration.Right.Failure;
        HasLocalLeftCalibration = canonical.LocalStickCalibration.HasLeft;
        HasLocalRightCalibration = canonical.LocalStickCalibration.HasRight;
        RawStickObservation = new Switch2RawStickObservation(canonical);
    }

    public ushort Version { get; }
    public Switch2Transport Transport { get; }
    public Switch2InputProtocolRevision ProtocolRevision { get; }
    public ulong DeviceGeneration { get; }
    public ulong TransportGeneration { get; }
    public long CompletionTimestampQpc { get; }
    public long QpcFrequency { get; }
    public uint DeviceCounterRaw { get; }
    public bool HasCounterDelta { get; }
    public uint CounterDeltaRaw { get; }
    public Switch2CounterSequenceKind CounterSequence { get; }
    public Switch2ProButton Buttons { get; }
    public uint RawButtonBits { get; }
    public uint UnknownButtonBits { get; }
    public Switch2ProfileAxis LeftX { get; private init; }
    public Switch2ProfileAxis LeftY { get; private init; }
    public Switch2ProfileAxis RightX { get; private init; }
    public Switch2ProfileAxis RightY { get; private init; }
    public bool HasCommonMotion { get; }
    public uint MotionTimestamp { get; }
    public Switch2Vector3Raw Accelerometer { get; }
    public Switch2Vector3Raw Gyroscope { get; }
    public Switch2Vector3Raw Magnetometer { get; }
    public bool HasBatteryStatus { get; }
    public Switch2BatteryStatus BatteryStatus { get; }
    public Switch2CalibrationAdoptionStatus LeftCalibrationStatus { get; }
    public Switch2CalibrationAdoptionFailure LeftCalibrationFailure { get; }
    public Switch2CalibrationAdoptionStatus RightCalibrationStatus { get; }
    public Switch2CalibrationAdoptionFailure RightCalibrationFailure { get; }
    public bool HasLocalLeftCalibration { get; private init; }
    public bool HasLocalRightCalibration { get; private init; }
    internal Switch2RawStickObservation RawStickObservation { get; }
    internal bool HasValidRawStickObservation => RawStickObservation.Matches(
        Switch2ControllerModel.ProController2, Transport, DeviceGeneration,
        TransportGeneration, CompletionTimestampQpc, QpcFrequency, HasCommonMotion) &&
        RawStickObservation.HasLeft && RawStickObservation.HasRight;

    internal Switch2ProProfileInputFrame WithLocalStickCalibration(
        in Switch2LocalStickCalibrationOverrides local)
    {
        if (!local.HasLeft && !local.HasRight &&
            !HasLocalLeftCalibration && !HasLocalRightCalibration) return this;
        if (!RawStickObservation.TryGetStick(Switch2StickSide.Left, local, out var left) ||
            !RawStickObservation.TryGetStick(Switch2StickSide.Right, local, out var right) ||
            !Switch2ProfileAxisProjection.TryMap(left, false, false, out var leftX) ||
            !Switch2ProfileAxisProjection.TryMap(left, true, true, out var leftY) ||
            !Switch2ProfileAxisProjection.TryMap(right, false, false, out var rightX) ||
            !Switch2ProfileAxisProjection.TryMap(right, true, true, out var rightY)) return this;
        return this with
        {
            LeftX = leftX, LeftY = leftY, RightX = rightX, RightY = rightY,
            HasLocalLeftCalibration = local.HasLeft,
            HasLocalRightCalibration = local.HasRight,
        };
    }

    public bool CButton => (Buttons & Switch2ProButton.C) != 0;

    /// <summary>
    /// Writes the controls that the legacy DS4Windows mapping state represents
    /// using the backwards-compatible Xbox/physical-position face layout.
    /// </summary>
    public bool TryWriteLegacyState(DS4State destination) =>
        TryWriteLegacyState(destination, Switch2FaceButtonLayout.Xbox);

    /// <summary>
    /// Writes the controls using the selected profile face layout. The
    /// physical C button remains only in
    /// <see cref="DS4State.Switch2RawInputStatus"/>; it is deliberately not
    /// aliased to the DualSense mute control.
    /// </summary>
    public bool TryWriteLegacyState(DS4State destination,
        Switch2FaceButtonLayout faceButtonLayout)
    {
        if (destination == null || Version != CurrentVersion ||
            !Switch2FaceButtonLayoutProjection.IsValid(faceButtonLayout))
        {
            return false;
        }

        Switch2ProButton buttons = Buttons;
        destination.LXAxis = DS4MappedStickAxis.FromSigned(LeftX.SignedValue);
        destination.LYAxis = DS4MappedStickAxis.FromSigned(LeftY.SignedValue);
        destination.RXAxis = DS4MappedStickAxis.FromSigned(RightX.SignedValue);
        destination.RYAxis = DS4MappedStickAxis.FromSigned(RightY.SignedValue);

        Switch2FaceButtonLayoutProjection.TryProject(faceButtonLayout,
            Has(buttons, Switch2ProButton.FaceWest),
            Has(buttons, Switch2ProButton.FaceNorth),
            Has(buttons, Switch2ProButton.FaceSouth),
            Has(buttons, Switch2ProButton.FaceEast),
            out destination.Square, out destination.Triangle,
            out destination.Cross, out destination.Circle);
        destination.L1 = Has(buttons, Switch2ProButton.LeftShoulder);
        destination.R1 = Has(buttons, Switch2ProButton.RightShoulder);
        destination.L3 = Has(buttons, Switch2ProButton.LeftStick);
        destination.R3 = Has(buttons, Switch2ProButton.RightStick);

        bool leftTrigger = Has(buttons, Switch2ProButton.LeftTrigger);
        bool rightTrigger = Has(buttons, Switch2ProButton.RightTrigger);
        destination.L2Btn = leftTrigger;
        destination.L2 = destination.L2Raw = leftTrigger ? byte.MaxValue :
            (byte)0;
        destination.R2Btn = rightTrigger;
        destination.R2 = destination.R2Raw = rightTrigger ? byte.MaxValue :
            (byte)0;

        destination.Share = Has(buttons, Switch2ProButton.Back);
        destination.Options = Has(buttons, Switch2ProButton.Start);
        destination.PS = Has(buttons, Switch2ProButton.Guide);
        destination.Capture = Has(buttons, Switch2ProButton.Capture);
        destination.DpadUp = Has(buttons, Switch2ProButton.DpadUp);
        destination.DpadRight = Has(buttons, Switch2ProButton.DpadRight);
        destination.DpadDown = Has(buttons, Switch2ProButton.DpadDown);
        destination.DpadLeft = Has(buttons, Switch2ProButton.DpadLeft);
        destination.BLP = Has(buttons, Switch2ProButton.LeftPaddle);
        destination.BRP = Has(buttons, Switch2ProButton.RightPaddle);

        // Explicitly clear controls this source does not expose. This prevents a
        // reused mapping state from manufacturing sticky input.
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
        destination.Switch2JoyConRawInputStatus = default;
        destination.Switch2RawInputStatus = BuildRawStatus();
        return true;
    }

    private Switch2RawInputStatus BuildRawStatus() => new()
    {
        IsValid = true,
        ContractVersion = Version,
        Transport = Transport,
        ProtocolRevision = ProtocolRevision,
        DeviceGeneration = DeviceGeneration,
        TransportGeneration = TransportGeneration,
        CompletionTimestampQpc = CompletionTimestampQpc,
        QpcFrequency = QpcFrequency,
        DeviceCounterRaw = DeviceCounterRaw,
        RawButtonBits = RawButtonBits,
        UnknownButtonBits = UnknownButtonBits,
        LeftStickXRaw = LeftX.RawValue,
        LeftStickYRaw = LeftY.RawValue,
        RightStickXRaw = RightX.RawValue,
        RightStickYRaw = RightY.RawValue,
        LeftStickX = LeftX.SignedValue,
        LeftStickY = LeftY.SignedValue,
        RightStickX = RightX.SignedValue,
        RightStickY = RightY.SignedValue,
        CButton = CButton,
    };

    private static bool Has(Switch2ProButton buttons,
        Switch2ProButton control) => (buttons & control) != 0;
}

/// <summary>
/// Exact Common05 Pro projection into the DS4Windows profile boundary for the
/// independently pinned USB and BLE identities. Axis orientation follows the
/// pinned SDL Switch 2 driver: X is not inverted and Y is inverted.
/// Calibration is the generation-bound snapshot already selected by
/// <see cref="Switch2InputSession"/>.
/// </summary>
public static class Switch2ProProfileInputMapper
{
    public static bool TryMap(in Switch2CanonicalInputFrame canonical,
        out Switch2ProProfileInputFrame frame,
        out Switch2ProProfileInputFailure failure)
    {
        if (canonical.Version != Switch2CanonicalInputFrame.CurrentVersion ||
            !canonical.Descriptor.IsValid)
        {
            return Fail(Switch2ProProfileInputFailure.InvalidCanonicalFrame,
                out frame, out failure);
        }

        Switch2InputProtocolIdentity identity = canonical.Descriptor.Identity;
        if (!IsSupportedProIdentity(identity))
        {
            return Fail(Switch2ProProfileInputFailure.UnsupportedIdentity,
                out frame, out failure);
        }
        if (!canonical.Report.IsCommon ||
            canonical.Report.Kind != Switch2InputReportKind.Common05)
        {
            return Fail(Switch2ProProfileInputFailure.UnsupportedReport,
                out frame, out failure);
        }
        if (!canonical.Calibration.IsValid ||
            canonical.Calibration.Model != canonical.Model ||
            canonical.Calibration.DeviceGeneration !=
                canonical.DeviceGeneration ||
            !canonical.TryGetLeftStick(out var left) ||
            !canonical.TryGetRightStick(out var right))
        {
            return Fail(Switch2ProProfileInputFailure.InvalidCalibration,
                out frame, out failure);
        }
        if (canonical.CounterSequence ==
                Switch2CounterSequenceKind.BackwardOrOutOfOrder &&
            !Switch2CounterSequence.UsesArrivalOrdering(canonical.Model,
                canonical.Transport, canonical.Report.Kind))
        {
            return Fail(Switch2ProProfileInputFailure.BackwardOrOutOfOrder,
                out frame, out failure);
        }

        if (!Switch2ProfileAxisProjection.TryMap(left.Raw.X, left.OffsetX,
                left.NegativeRangeX,
                left.PositiveRangeX, invert: false, out var leftX) ||
            !Switch2ProfileAxisProjection.TryMap(left.Raw.Y, left.OffsetY,
                left.NegativeRangeY,
                left.PositiveRangeY, invert: true, out var leftY) ||
            !Switch2ProfileAxisProjection.TryMap(right.Raw.X, right.OffsetX,
                right.NegativeRangeX,
                right.PositiveRangeX, invert: false, out var rightX) ||
            !Switch2ProfileAxisProjection.TryMap(right.Raw.Y, right.OffsetY,
                right.NegativeRangeY,
                right.PositiveRangeY, invert: true, out var rightY))
        {
            return Fail(Switch2ProProfileInputFailure.InvalidAxis,
                out frame, out failure);
        }

        var buttons = (Switch2ProButton)(canonical.RawButtonBits &
            Switch2ProUsbInputProjection.KnownButtonMask);
        frame = new Switch2ProProfileInputFrame(canonical, buttons, leftX,
            leftY, rightX, rightY);
        failure = Switch2ProProfileInputFailure.None;
        return true;
    }

    private static bool IsSupportedProIdentity(
        in Switch2InputProtocolIdentity identity)
    {
        if (identity.Model != Switch2ControllerModel.ProController2)
        {
            return false;
        }

        if (identity.Transport == Switch2Transport.Usb)
        {
            return identity.ProtocolRevision ==
                    Switch2InputProtocolRevision.ProUsbCommon05Bcd0201 &&
                identity.VendorId ==
                    Switch2InputProtocolIdentity.NintendoUsbVendorId &&
                identity.ProductId ==
                    Switch2InputProtocolIdentity.ProController2UsbProductId &&
                identity.BcdDevice ==
                    Switch2InputProtocolIdentity.
                        AuditedProController2UsbBcdDevice;
        }

        if (identity.Transport != Switch2Transport.BluetoothLe ||
            identity.ProtocolRevision !=
                Switch2InputProtocolRevision.BluetoothLeCommon05V1 ||
            identity.ServiceUuid != Switch2InputCodec.ServiceUuid ||
            identity.CharacteristicUuid !=
                Switch2InputCodec.Common05CharacteristicUuid)
        {
            return false;
        }

        const Switch2GattProperty required = Switch2GattProperty.Read |
            Switch2GattProperty.Notify;
        return identity.GattProperties == required;
    }

    private static bool Fail(Switch2ProProfileInputFailure reason,
        out Switch2ProProfileInputFrame frame,
        out Switch2ProProfileInputFailure failure)
    {
        frame = default;
        failure = reason;
        return false;
    }
}
