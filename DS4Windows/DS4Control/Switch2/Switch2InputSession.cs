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

/// <summary>
/// Exact input contract selected before a transport worker begins publishing.
/// A revision names a model/transport/framing tuple, not controller firmware.
/// </summary>
public enum Switch2InputProtocolRevision : byte
{
    Invalid = 0,
    ProUsbCommon05Bcd0201 = 1,
    BluetoothLeCommon05V1 = 2,
    BluetoothLeJoyCon2Left07V1 = 3,
    BluetoothLeJoyCon2Right08V1 = 4,
    BluetoothLeProController2_09V1 = 5,
}

/// <summary>
/// Immutable, source-pinned model and transport identity. Construction is
/// fail-closed so a live worker cannot select a parser from packet length alone.
/// </summary>
public readonly struct Switch2InputProtocolIdentity :
    IEquatable<Switch2InputProtocolIdentity>
{
    public const ushort NintendoUsbVendorId = 0x057E;
    public const ushort ProController2UsbProductId = 0x2069;
    public const ushort AuditedProController2UsbBcdDevice = 0x0201;

    private Switch2InputProtocolIdentity(Switch2ControllerModel model,
        Switch2Transport transport,
        Switch2InputProtocolRevision protocolRevision, ushort vendorId,
        ushort productId, ushort bcdDevice, Guid serviceUuid,
        Guid characteristicUuid, Switch2GattProperty gattProperties)
    {
        Model = model;
        Transport = transport;
        ProtocolRevision = protocolRevision;
        VendorId = vendorId;
        ProductId = productId;
        BcdDevice = bcdDevice;
        ServiceUuid = serviceUuid;
        CharacteristicUuid = characteristicUuid;
        GattProperties = gattProperties;
    }

    public Switch2ControllerModel Model { get; }

    public Switch2Transport Transport { get; }

    public Switch2InputProtocolRevision ProtocolRevision { get; }

    public ushort VendorId { get; }

    public ushort ProductId { get; }

    /// <summary>USB descriptor revision, not controller firmware.</summary>
    public ushort BcdDevice { get; }

    public Guid ServiceUuid { get; }

    public Guid CharacteristicUuid { get; }

    public Switch2GattProperty GattProperties { get; }

    public bool IsValid => ProtocolRevision !=
        Switch2InputProtocolRevision.Invalid;

    public static bool TryCreateProController2Usb(ushort vendorId,
        ushort productId, ushort bcdDevice,
        out Switch2InputProtocolIdentity identity)
    {
        if (vendorId != NintendoUsbVendorId ||
            productId != ProController2UsbProductId ||
            bcdDevice != AuditedProController2UsbBcdDevice)
        {
            identity = default;
            return false;
        }

        identity = new Switch2InputProtocolIdentity(
            Switch2ControllerModel.ProController2, Switch2Transport.Usb,
            Switch2InputProtocolRevision.ProUsbCommon05Bcd0201,
            vendorId, productId, bcdDevice, default, default,
            Switch2GattProperty.None);
        return true;
    }

    public static bool TryCreateBluetoothLe(Guid serviceUuid,
        Guid characteristicUuid, Switch2GattProperty gattProperties,
        Switch2ControllerModel advertisementVerifiedModel,
        out Switch2InputProtocolIdentity identity)
    {
        if (serviceUuid != Switch2InputCodec.ServiceUuid ||
            !Switch2InputCodec.TryResolveBluetoothLeInputIdentity(
                characteristicUuid,
                out Switch2GattCharacteristicIdentity characteristic) ||
            !characteristic.HasRequiredProperties(gattProperties))
        {
            identity = default;
            return false;
        }

        Switch2ControllerModel model = characteristic.FixedModel ==
            Switch2ControllerModel.Unknown ? advertisementVerifiedModel :
            characteristic.FixedModel;
        if (model is not (Switch2ControllerModel.JoyCon2Left or
                Switch2ControllerModel.JoyCon2Right or
                Switch2ControllerModel.ProController2) ||
            (characteristic.FixedModel != Switch2ControllerModel.Unknown &&
             characteristic.FixedModel != advertisementVerifiedModel))
        {
            identity = default;
            return false;
        }

        Switch2InputProtocolRevision revision = characteristic.ReportKind switch
        {
            Switch2InputReportKind.Common05 =>
                Switch2InputProtocolRevision.BluetoothLeCommon05V1,
            Switch2InputReportKind.JoyCon2Left07 when model ==
                Switch2ControllerModel.JoyCon2Left =>
                Switch2InputProtocolRevision.BluetoothLeJoyCon2Left07V1,
            Switch2InputReportKind.JoyCon2Right08 when model ==
                Switch2ControllerModel.JoyCon2Right =>
                Switch2InputProtocolRevision.BluetoothLeJoyCon2Right08V1,
            Switch2InputReportKind.ProController2_09 when model ==
                Switch2ControllerModel.ProController2 =>
                Switch2InputProtocolRevision.BluetoothLeProController2_09V1,
            _ => Switch2InputProtocolRevision.Invalid,
        };
        if (revision == Switch2InputProtocolRevision.Invalid)
        {
            identity = default;
            return false;
        }

        identity = new Switch2InputProtocolIdentity(model,
            Switch2Transport.BluetoothLe, revision, 0, 0, 0, serviceUuid,
            characteristicUuid, gattProperties);
        return true;
    }

    public bool Equals(Switch2InputProtocolIdentity other) =>
        Model == other.Model && Transport == other.Transport &&
        ProtocolRevision == other.ProtocolRevision &&
        VendorId == other.VendorId && ProductId == other.ProductId &&
        BcdDevice == other.BcdDevice && ServiceUuid == other.ServiceUuid &&
        CharacteristicUuid == other.CharacteristicUuid &&
        GattProperties == other.GattProperties;

    public override bool Equals(object obj) =>
        obj is Switch2InputProtocolIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(Model, Transport, ProtocolRevision, VendorId),
        HashCode.Combine(ProductId, BcdDevice, ServiceUuid,
            CharacteristicUuid), GattProperties);
}

/// <summary>
/// One immutable lifetime fence for a physical input worker. Generations are
/// caller-owned monotonic identities; this type does not discover devices.
/// </summary>
public readonly struct Switch2InputSessionDescriptor :
    IEquatable<Switch2InputSessionDescriptor>
{
    private Switch2InputSessionDescriptor(
        in Switch2InputProtocolIdentity identity, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency)
    {
        Identity = identity;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        QpcFrequency = qpcFrequency;
    }

    public Switch2InputProtocolIdentity Identity { get; }

    public ulong DeviceGeneration { get; }

    public ulong TransportGeneration { get; }

    public long QpcFrequency { get; }

    public bool IsValid => Identity.IsValid && DeviceGeneration != 0 &&
        TransportGeneration != 0 && QpcFrequency > 0;

    public static bool TryCreate(in Switch2InputProtocolIdentity identity,
        ulong deviceGeneration, ulong transportGeneration, long qpcFrequency,
        out Switch2InputSessionDescriptor descriptor)
    {
        if (!identity.IsValid || deviceGeneration == 0 ||
            transportGeneration == 0 || qpcFrequency <= 0)
        {
            descriptor = default;
            return false;
        }

        descriptor = new Switch2InputSessionDescriptor(identity,
            deviceGeneration, transportGeneration, qpcFrequency);
        return true;
    }

    public bool Equals(Switch2InputSessionDescriptor other) =>
        Identity.Equals(other.Identity) &&
        DeviceGeneration == other.DeviceGeneration &&
        TransportGeneration == other.TransportGeneration &&
        QpcFrequency == other.QpcFrequency;

    public override bool Equals(object obj) =>
        obj is Switch2InputSessionDescriptor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Identity,
        DeviceGeneration, TransportGeneration, QpcFrequency);
}

public enum Switch2CalibrationAdoptionStatus : byte
{
    Invalid = 0,
    NotApplicable,
    AdoptedFactory,
    FallbackMissing,
    FallbackMalformed,
    FallbackUnadoptable,
    AdoptedUser,
}

public enum Switch2CalibrationAdoptionFailure : byte
{
    None = 0,
    SentinelOrErased,
    NegativeEndpointOutOfRange,
    PositiveEndpointOutOfRange,
}

/// <summary>
/// Effective calibration for one logical side. Fallback is explicit and never
/// represented as factory data.
/// </summary>
public readonly struct Switch2StickCalibrationBinding :
    IEquatable<Switch2StickCalibrationBinding>
{
    internal Switch2StickCalibrationBinding(Switch2StickSide side,
        Switch2CalibrationAdoptionStatus status,
        Switch2CalibrationAdoptionFailure failure,
        in Switch2StickCalibration effectiveCalibration)
    {
        Side = side;
        Status = status;
        Failure = failure;
        EffectiveCalibration = effectiveCalibration;
    }

    public Switch2StickSide Side { get; }

    public Switch2CalibrationAdoptionStatus Status { get; }

    public Switch2CalibrationAdoptionFailure Failure { get; }

    public Switch2StickCalibration EffectiveCalibration { get; }

    public bool IsFactoryAdopted =>
        Status == Switch2CalibrationAdoptionStatus.AdoptedFactory;

    public bool IsUserAdopted =>
        Status == Switch2CalibrationAdoptionStatus.AdoptedUser;

    public bool Equals(Switch2StickCalibrationBinding other) =>
        Side == other.Side && Status == other.Status &&
        Failure == other.Failure &&
        EffectiveCalibration.Equals(other.EffectiveCalibration);

    public override bool Equals(object obj) =>
        obj is Switch2StickCalibrationBinding other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Side, Status,
        Failure, EffectiveCalibration);
}

/// <summary>
/// Device-generation calibration snapshot. Missing, malformed, and physically
/// impossible records resolve to a named symmetric fallback without changing
/// the original raw stick values retained in each frame.
/// </summary>
public readonly struct Switch2InputCalibrationSnapshot :
    IEquatable<Switch2InputCalibrationSnapshot>
{
    private static readonly Switch2StickCalibration SymmetricFallback = new(
        0x0800, 0x0800, 0x07FF, 0x07FF, 0x0800, 0x0800);
    // Switch2Connect's production wired-Pro fallback uses the observed
    // centered physical travel rather than the entire 12-bit numeric domain.
    // Keep this transport-specific so BLE factory-read failures preserve the
    // longstanding conservative full-domain fallback.
    private static readonly Switch2StickCalibration ProUsbCenteredFallback =
        new(0x0800, 0x0800, 1500, 1500, 1500, 1500);

    private Switch2InputCalibrationSnapshot(Switch2ControllerModel model,
        ulong deviceGeneration,
        in Switch2StickCalibrationBinding left,
        in Switch2StickCalibrationBinding right)
    {
        Model = model;
        DeviceGeneration = deviceGeneration;
        Left = left;
        Right = right;
    }

    public Switch2ControllerModel Model { get; }

    public ulong DeviceGeneration { get; }

    public Switch2StickCalibrationBinding Left { get; }

    public Switch2StickCalibrationBinding Right { get; }

    public bool IsValid => DeviceGeneration != 0 &&
        Model is Switch2ControllerModel.JoyCon2Left or
            Switch2ControllerModel.JoyCon2Right or
            Switch2ControllerModel.ProController2;

    public static bool TryCreate(Switch2ControllerModel model,
        ulong deviceGeneration,
        ReadOnlySpan<byte> leftFactoryRecord,
        ReadOnlySpan<byte> rightFactoryRecord,
        out Switch2InputCalibrationSnapshot snapshot)
    {
        return TryCreate(model, deviceGeneration, leftFactoryRecord,
            rightFactoryRecord, ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty, out snapshot);
    }

    public static bool TryCreate(Switch2ControllerModel model,
        ulong deviceGeneration,
        ReadOnlySpan<byte> leftFactoryRecord,
        ReadOnlySpan<byte> rightFactoryRecord,
        ReadOnlySpan<byte> leftUserRecord,
        ReadOnlySpan<byte> rightUserRecord,
        out Switch2InputCalibrationSnapshot snapshot)
    {
        if (deviceGeneration == 0 ||
            model is not (Switch2ControllerModel.JoyCon2Left or
                Switch2ControllerModel.JoyCon2Right or
                Switch2ControllerModel.ProController2))
        {
            snapshot = default;
            return false;
        }

        bool hasLeft = model is Switch2ControllerModel.JoyCon2Left or
            Switch2ControllerModel.ProController2;
        bool hasRight = model is Switch2ControllerModel.JoyCon2Right or
            Switch2ControllerModel.ProController2;
        Switch2StickCalibrationBinding left = BuildBinding(
            Switch2StickSide.Left, hasLeft, leftFactoryRecord,
            leftUserRecord);
        Switch2StickCalibrationBinding right = BuildBinding(
            Switch2StickSide.Right, hasRight, rightFactoryRecord,
            rightUserRecord);
        snapshot = new Switch2InputCalibrationSnapshot(model,
            deviceGeneration, left, right);
        return true;
    }

    public static bool TryCreateFallback(Switch2ControllerModel model,
        ulong deviceGeneration,
        out Switch2InputCalibrationSnapshot snapshot) =>
        TryCreate(model, deviceGeneration, ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty, out snapshot);

    internal static bool TryCreateProUsbCenteredFallback(
        ulong deviceGeneration,
        out Switch2InputCalibrationSnapshot snapshot)
    {
        if (deviceGeneration == 0)
        {
            snapshot = default;
            return false;
        }

        var left = new Switch2StickCalibrationBinding(
            Switch2StickSide.Left,
            Switch2CalibrationAdoptionStatus.FallbackMissing,
            Switch2CalibrationAdoptionFailure.None,
            ProUsbCenteredFallback);
        var right = new Switch2StickCalibrationBinding(
            Switch2StickSide.Right,
            Switch2CalibrationAdoptionStatus.FallbackMissing,
            Switch2CalibrationAdoptionFailure.None,
            ProUsbCenteredFallback);
        snapshot = new Switch2InputCalibrationSnapshot(
            Switch2ControllerModel.ProController2, deviceGeneration, left,
            right);
        return true;
    }

    internal static bool TryCreateProUsb(ulong deviceGeneration,
        ReadOnlySpan<byte> leftFactoryRecord,
        ReadOnlySpan<byte> rightFactoryRecord,
        ReadOnlySpan<byte> leftUserRecord,
        ReadOnlySpan<byte> rightUserRecord,
        out Switch2InputCalibrationSnapshot snapshot)
    {
        if (!TryCreate(Switch2ControllerModel.ProController2,
                deviceGeneration, leftFactoryRecord, rightFactoryRecord,
                leftUserRecord, rightUserRecord, out var decoded))
        {
            snapshot = default;
            return false;
        }

        Switch2StickCalibrationBinding left =
            ApplyProUsbFallback(decoded.Left);
        Switch2StickCalibrationBinding right =
            ApplyProUsbFallback(decoded.Right);
        snapshot = new Switch2InputCalibrationSnapshot(
            Switch2ControllerModel.ProController2, deviceGeneration, left,
            right);
        return true;
    }

    public bool Equals(Switch2InputCalibrationSnapshot other) =>
        Model == other.Model && DeviceGeneration == other.DeviceGeneration &&
        Left.Equals(other.Left) && Right.Equals(other.Right);

    public override bool Equals(object obj) =>
        obj is Switch2InputCalibrationSnapshot other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Model,
        DeviceGeneration, Left, Right);

    private static Switch2StickCalibrationBinding BuildBinding(
        Switch2StickSide side, bool applicable,
        ReadOnlySpan<byte> factoryRecord, ReadOnlySpan<byte> userRecord)
    {
        if (!applicable)
        {
            return new Switch2StickCalibrationBinding(side,
                Switch2CalibrationAdoptionStatus.NotApplicable,
                Switch2CalibrationAdoptionFailure.None, SymmetricFallback);
        }
        Switch2StickCalibrationBinding factoryBinding;
        if (factoryRecord.IsEmpty)
        {
            factoryBinding = new Switch2StickCalibrationBinding(side,
                Switch2CalibrationAdoptionStatus.FallbackMissing,
                Switch2CalibrationAdoptionFailure.None, SymmetricFallback);
        }
        else if (!Switch2CalibrationCodec.TryDecodeStick(factoryRecord,
                out Switch2StickCalibration calibration))
        {
            factoryBinding = new Switch2StickCalibrationBinding(side,
                Switch2CalibrationAdoptionStatus.FallbackMalformed,
                Switch2CalibrationAdoptionFailure.None, SymmetricFallback);
        }
        else if (!Switch2CalibrationCodec.TryValidateAdoptable(calibration,
                out Switch2CalibrationAdoptionFailure failure))
        {
            factoryBinding = new Switch2StickCalibrationBinding(side,
                Switch2CalibrationAdoptionStatus.FallbackUnadoptable,
                failure, SymmetricFallback);
        }
        else
        {
            factoryBinding = new Switch2StickCalibrationBinding(side,
                Switch2CalibrationAdoptionStatus.AdoptedFactory,
                Switch2CalibrationAdoptionFailure.None, calibration);
        }

        // User calibration is optional and read-only. A missing marker,
        // malformed record, or physically impossible span never erases a
        // usable factory binding; only a complete, marked, adoptable record
        // overrides it.
        if (!Switch2CalibrationCodec.TryDecodeUserStick(userRecord,
                out Switch2StickCalibration userCalibration) ||
            !Switch2CalibrationCodec.TryValidateAdoptable(userCalibration,
                out _))
        {
            return factoryBinding;
        }

        return new Switch2StickCalibrationBinding(side,
            Switch2CalibrationAdoptionStatus.AdoptedUser,
            Switch2CalibrationAdoptionFailure.None, userCalibration);
    }

    private static Switch2StickCalibrationBinding ApplyProUsbFallback(
        in Switch2StickCalibrationBinding binding) =>
        binding.Status is Switch2CalibrationAdoptionStatus.AdoptedFactory or
                Switch2CalibrationAdoptionStatus.AdoptedUser ? binding :
            new Switch2StickCalibrationBinding(binding.Side, binding.Status,
                binding.Failure, ProUsbCenteredFallback);
}

/// <summary>
/// Fixed, immutable ownership of all 63 body bytes without an array reference.
/// Copies of a canonical frame therefore cannot observe a reused transport
/// buffer.
/// </summary>
public readonly struct Switch2OwnedInputBody :
    IEquatable<Switch2OwnedInputBody>
{
    public const int Length = Switch2InputCodec.BluetoothLeBodyLength;

    private readonly ulong word0;
    private readonly ulong word1;
    private readonly ulong word2;
    private readonly ulong word3;
    private readonly ulong word4;
    private readonly ulong word5;
    private readonly ulong word6;
    private readonly ulong word7;

    internal Switch2OwnedInputBody(ReadOnlySpan<byte> body)
    {
        if (body.Length != Length)
        {
            throw new ArgumentException(
                $"A Switch 2 input body is exactly {Length} bytes.",
                nameof(body));
        }

        word0 = Pack(body.Slice(0, 8));
        word1 = Pack(body.Slice(8, 8));
        word2 = Pack(body.Slice(16, 8));
        word3 = Pack(body.Slice(24, 8));
        word4 = Pack(body.Slice(32, 8));
        word5 = Pack(body.Slice(40, 8));
        word6 = Pack(body.Slice(48, 8));
        word7 = Pack(body.Slice(56, 7));
    }

    public byte this[int index]
    {
        get
        {
            if ((uint)index >= Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            ulong word = (index >> 3) switch
            {
                0 => word0,
                1 => word1,
                2 => word2,
                3 => word3,
                4 => word4,
                5 => word5,
                6 => word6,
                _ => word7,
            };
            return (byte)(word >> ((index & 7) * 8));
        }
    }

    public bool TryCopyTo(Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            return false;
        }
        for (int index = 0; index < Length; index++)
        {
            destination[index] = this[index];
        }
        return true;
    }

    public bool TryCopyRange(int offset, int length, Span<byte> destination)
    {
        if (offset < 0 || length < 0 || offset > Length - length ||
            destination.Length != length)
        {
            return false;
        }
        for (int index = 0; index < length; index++)
        {
            destination[index] = this[offset + index];
        }
        return true;
    }

    public bool Equals(Switch2OwnedInputBody other) =>
        word0 == other.word0 && word1 == other.word1 &&
        word2 == other.word2 && word3 == other.word3 &&
        word4 == other.word4 && word5 == other.word5 &&
        word6 == other.word6 && word7 == other.word7;

    public override bool Equals(object obj) =>
        obj is Switch2OwnedInputBody other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(word0, word1,
        word2, word3, word4, word5, word6, word7);

    private static ulong Pack(ReadOnlySpan<byte> source)
    {
        ulong value = 0;
        for (int index = 0; index < source.Length; index++)
        {
            value |= (ulong)source[index] << (index * 8);
        }
        return value;
    }
}

public readonly struct Switch2CalibratedStickPosition
{
    internal Switch2CalibratedStickPosition(in Switch2StickRaw raw,
        in Switch2StickCalibrationBinding binding, Switch2StickCalibration? localCalibration = null)
    {
        Raw = raw;
        CalibrationStatus = binding.Status;
        CalibrationFailure = binding.Failure;
        HasLocalCalibration = localCalibration.HasValue;
        Switch2StickCalibration calibration = localCalibration ?? binding.EffectiveCalibration;
        OffsetX = raw.X - calibration.NeutralX;
        OffsetY = raw.Y - calibration.NeutralY;
        PositiveRangeX = calibration.PositiveRangeX;
        PositiveRangeY = calibration.PositiveRangeY;
        NegativeRangeX = calibration.NegativeRangeX;
        NegativeRangeY = calibration.NegativeRangeY;
    }

    public Switch2StickRaw Raw { get; }
    public int OffsetX { get; }
    public int OffsetY { get; }
    public ushort PositiveRangeX { get; }
    public ushort PositiveRangeY { get; }
    public ushort NegativeRangeX { get; }
    public ushort NegativeRangeY { get; }
    public Switch2CalibrationAdoptionStatus CalibrationStatus { get; }
    public Switch2CalibrationAdoptionFailure CalibrationFailure { get; }
    public bool HasLocalCalibration { get; }
}

/// <summary>
/// Version-one high-resolution input frame shared by replay and future live
/// transports. It retains the exact body and never quantizes through DS4State.
/// </summary>
public readonly struct Switch2CanonicalInputFrame
{
    public const ushort CurrentVersion = 1;
    public const uint ProController2KnownCommonButtonMask = 0x03CF7FCF;

    internal Switch2CanonicalInputFrame(
        in Switch2InputSessionDescriptor descriptor,
        long completionTimestampQpc, in Switch2DecodedInputReport report,
        in Switch2OwnedInputBody rawBody,
        in Switch2InputCalibrationSnapshot calibration,
        bool hasCounterDelta, uint counterDelta,
        Switch2CounterSequenceKind counterSequence,
        Switch2LocalStickCalibrationOverrides localStickCalibration = default)
    {
        Version = CurrentVersion;
        Descriptor = descriptor;
        CompletionTimestampQpc = completionTimestampQpc;
        Report = report;
        RawBody = rawBody;
        Calibration = calibration;
        HasCounterDelta = hasCounterDelta;
        CounterDeltaRaw = counterDelta;
        CounterSequence = counterSequence;
        LocalStickCalibration = localStickCalibration;
    }

    public ushort Version { get; }
    public Switch2InputSessionDescriptor Descriptor { get; }
    public Switch2ControllerModel Model => Descriptor.Identity.Model;
    public Switch2Transport Transport => Descriptor.Identity.Transport;
    public Switch2InputProtocolRevision ProtocolRevision =>
        Descriptor.Identity.ProtocolRevision;
    public ulong DeviceGeneration => Descriptor.DeviceGeneration;
    public ulong TransportGeneration => Descriptor.TransportGeneration;
    public long CompletionTimestampQpc { get; }
    public long QpcFrequency => Descriptor.QpcFrequency;
    public Switch2DecodedInputReport Report { get; }
    public Switch2OwnedInputBody RawBody { get; }
    public Switch2InputCalibrationSnapshot Calibration { get; }
    // Separate from immutable source factory/user-SPI calibration evidence.
    internal Switch2LocalStickCalibrationOverrides LocalStickCalibration { get; }

    internal Switch2CanonicalInputFrame WithLocalStickCalibration(in Switch2LocalStickCalibrationOverrides calibration) =>
        new(Descriptor, CompletionTimestampQpc, Report, RawBody, Calibration,
            HasCounterDelta, CounterDeltaRaw, CounterSequence, calibration);
    public uint DeviceCounterRaw => Report.Counter;
    public byte CounterWidthBits => Report.CounterWidthBits;
    public bool HasCounterDelta { get; }
    public uint CounterDeltaRaw { get; }
    public Switch2CounterSequenceKind CounterSequence { get; }
    public uint RawButtonBits => Report.IsCommon ? Report.Common.Buttons :
        Report.Basic.Buttons;
    public uint UnknownButtonBits => Report.IsCommon &&
        Model == Switch2ControllerModel.ProController2 ?
        RawButtonBits & ~ProController2KnownCommonButtonMask :
        RawButtonBits;

    public bool TryGetLeftStick(
        out Switch2CalibratedStickPosition position)
    {
        if (!Report.HasLeftStick)
        {
            position = default;
            return false;
        }
        Switch2StickRaw raw = Report.IsCommon ? Report.Common.LeftStick :
            Report.Basic.PrimaryStick;
        position = new Switch2CalibratedStickPosition(raw, Calibration.Left,
            LocalStickCalibration.HasLeft ? LocalStickCalibration.Left : null);
        return true;
    }

    public bool TryGetRightStick(
        out Switch2CalibratedStickPosition position)
    {
        if (!Report.HasRightStick)
        {
            position = default;
            return false;
        }
        Switch2StickRaw raw = Report.IsCommon ? Report.Common.RightStick :
            Model == Switch2ControllerModel.JoyCon2Right ?
                Report.Basic.PrimaryStick : Report.Basic.SecondaryStick;
        position = new Switch2CalibratedStickPosition(raw, Calibration.Right,
            LocalStickCalibration.HasRight ? LocalStickCalibration.Right : null);
        return true;
    }

    public bool TryCopyRawBody(Span<byte> destination) =>
        RawBody.TryCopyTo(destination);

    public bool TryCopyOpaqueMotion(Span<byte> destination,
        out int written)
    {
        if (Report.IsCommon)
        {
            written = 0;
            return false;
        }
        int length = Report.Basic.Motion.DeclaredLength;
        if (!RawBody.TryCopyRange(Report.Basic.Motion.BodyOffset, length,
                destination))
        {
            written = 0;
            return false;
        }
        written = length;
        return true;
    }
}

public enum Switch2InputSessionFailure : byte
{
    None = 0,
    InvalidDescriptor,
    InvalidCalibration,
    DescriptorMismatch,
    ClockMismatch,
    TimestampRegression,
    InvalidFramingOrReport,
    GenerationRegression,
    GenerationNotAdvanced,
    InconsistentReplaySequence,
}

/// <summary>
/// Stateful sequence boundary for one physical input lifetime. Construction is
/// a control-path allocation; successful report processing allocates nothing.
/// </summary>
public sealed class Switch2InputSession
{
    private Switch2InputSessionDescriptor descriptor;
    private Switch2InputCalibrationSnapshot calibration;
    private bool hasCompletionTimestamp;
    private long lastCompletionTimestampQpc;
    private bool hasCounter;
    private uint lastCounter;

    public Switch2InputSession(in Switch2InputSessionDescriptor descriptor,
        in Switch2InputCalibrationSnapshot calibration)
    {
        if (!descriptor.IsValid)
        {
            throw new ArgumentException("The session descriptor is invalid.",
                nameof(descriptor));
        }
        if (!calibration.IsValid ||
            calibration.Model != descriptor.Identity.Model ||
            calibration.DeviceGeneration != descriptor.DeviceGeneration)
        {
            throw new ArgumentException(
                "Calibration must match the session model and device generation.",
                nameof(calibration));
        }
        this.descriptor = descriptor;
        this.calibration = calibration;
    }

    public Switch2InputSessionDescriptor Descriptor => descriptor;

    public bool TryProcess(in Switch2InputSessionDescriptor observationLifetime,
        ReadOnlySpan<byte> packet, long completionTimestampQpc,
        out Switch2CanonicalInputFrame frame,
        out Switch2InputSessionFailure failure)
    {
        if (!observationLifetime.Equals(descriptor))
        {
            return Fail(Switch2InputSessionFailure.DescriptorMismatch,
                out frame, out failure);
        }
        if (completionTimestampQpc < 0 ||
            hasCompletionTimestamp &&
            completionTimestampQpc < lastCompletionTimestampQpc)
        {
            return Fail(Switch2InputSessionFailure.TimestampRegression,
                out frame, out failure);
        }
        if (!Switch2CanonicalInputBuilder.TryDecode(descriptor.Identity, packet,
                out Switch2DecodedInputReport report,
                out Switch2OwnedInputBody body))
        {
            return Fail(Switch2InputSessionFailure.InvalidFramingOrReport,
                out frame, out failure);
        }

        bool hasDelta = hasCounter;
        uint delta = 0;
        Switch2CounterSequenceKind sequence =
            Switch2CounterSequenceKind.First;
        if (hasDelta)
        {
            sequence = Switch2CounterSequence.Classify(report.Counter,
                lastCounter, report.CounterWidthBits, out delta);
        }
        frame = new Switch2CanonicalInputFrame(descriptor,
            completionTimestampQpc, report, body, calibration, hasDelta, delta,
            sequence);

        hasCompletionTimestamp = true;
        lastCompletionTimestampQpc = completionTimestampQpc;
        if (Switch2CounterSequence.UsesArrivalOrdering(report.Model,
                descriptor.Identity.Transport, report.Kind) ||
            sequence != Switch2CounterSequenceKind.BackwardOrOutOfOrder)
        {
            hasCounter = true;
            lastCounter = report.Counter;
        }
        failure = Switch2InputSessionFailure.None;
        return true;
    }

    /// <summary>
    /// Advances only the lifetime fence. Identity and QPC frequency are stable;
    /// a device-generation advance may restart its transport generation, while
    /// a transport-only reset must strictly advance that generation.
    /// </summary>
    public bool TryReset(in Switch2InputSessionDescriptor next,
        in Switch2InputCalibrationSnapshot nextCalibration,
        out Switch2InputSessionFailure failure)
    {
        if (!next.IsValid)
        {
            failure = Switch2InputSessionFailure.InvalidDescriptor;
            return false;
        }
        if (!next.Identity.Equals(descriptor.Identity))
        {
            failure = Switch2InputSessionFailure.DescriptorMismatch;
            return false;
        }
        if (next.QpcFrequency != descriptor.QpcFrequency)
        {
            failure = Switch2InputSessionFailure.ClockMismatch;
            return false;
        }
        if (!nextCalibration.IsValid ||
            nextCalibration.Model != next.Identity.Model ||
            nextCalibration.DeviceGeneration != next.DeviceGeneration)
        {
            failure = Switch2InputSessionFailure.InvalidCalibration;
            return false;
        }
        if (next.DeviceGeneration < descriptor.DeviceGeneration ||
            next.DeviceGeneration == descriptor.DeviceGeneration &&
            next.TransportGeneration < descriptor.TransportGeneration)
        {
            failure = Switch2InputSessionFailure.GenerationRegression;
            return false;
        }
        if (next.DeviceGeneration == descriptor.DeviceGeneration &&
            next.TransportGeneration == descriptor.TransportGeneration)
        {
            failure = Switch2InputSessionFailure.GenerationNotAdvanced;
            return false;
        }
        if (next.DeviceGeneration == descriptor.DeviceGeneration &&
            !nextCalibration.Equals(calibration))
        {
            failure = Switch2InputSessionFailure.InvalidCalibration;
            return false;
        }

        descriptor = next;
        calibration = nextCalibration;
        // QueryPerformanceCounter is one absolute monotonic host clock across
        // device and transport lifetimes. Preserve its accepted chronology;
        // only the controller-owned sequence baseline restarts.
        hasCounter = false;
        lastCounter = 0;
        failure = Switch2InputSessionFailure.None;
        return true;
    }

    internal static bool TryBuildReplayFrame(
        in Switch2InputSessionDescriptor descriptor,
        in Switch2InputCalibrationSnapshot calibration,
        ReadOnlySpan<byte> packet, long completionTimestampQpc,
        bool hasCounterDelta, uint counterDelta,
        Switch2CounterSequenceKind counterSequence,
        out Switch2CanonicalInputFrame frame,
        out Switch2InputSessionFailure failure)
    {
        if (!descriptor.IsValid)
        {
            return Fail(Switch2InputSessionFailure.InvalidDescriptor,
                out frame, out failure);
        }
        if (!calibration.IsValid ||
            calibration.Model != descriptor.Identity.Model ||
            calibration.DeviceGeneration != descriptor.DeviceGeneration)
        {
            return Fail(Switch2InputSessionFailure.InvalidCalibration,
                out frame, out failure);
        }
        if (completionTimestampQpc < 0)
        {
            return Fail(Switch2InputSessionFailure.TimestampRegression,
                out frame, out failure);
        }
        if (!Switch2CounterSequence.IsConsistent(hasCounterDelta, counterDelta,
                counterSequence))
        {
            return Fail(Switch2InputSessionFailure.InconsistentReplaySequence,
                out frame, out failure);
        }
        if (!Switch2CanonicalInputBuilder.TryDecode(descriptor.Identity, packet,
                out Switch2DecodedInputReport report,
                out Switch2OwnedInputBody body))
        {
            return Fail(Switch2InputSessionFailure.InvalidFramingOrReport,
                out frame, out failure);
        }

        frame = new Switch2CanonicalInputFrame(descriptor,
            completionTimestampQpc, report, body, calibration,
            hasCounterDelta, counterDelta, counterSequence);
        failure = Switch2InputSessionFailure.None;
        return true;
    }

    private static bool Fail(Switch2InputSessionFailure reason,
        out Switch2CanonicalInputFrame frame,
        out Switch2InputSessionFailure failure)
    {
        frame = default;
        failure = reason;
        return false;
    }
}

public enum Switch2CounterSequenceKind : byte
{
    First = 0,
    Forward,
    Duplicate,
    BackwardOrOutOfOrder,
}

internal static class Switch2CounterSequence
{
    // The evidenced Pro USB and BLE Common05 counter resets below uint.MaxValue
    // while reports continue. Its modular classification is diagnostic only:
    // exact read/notification leases and host QPC establish delivery order. Keep
    // the discontinuity visible and compare the next arrival against it.
    // Live callers first validate the exact USB or GATT protocol identity;
    // the offline fixture format carries only this model/transport/report tuple.
    internal static bool UsesArrivalOrdering(Switch2ControllerModel model,
        Switch2Transport transport, Switch2InputReportKind kind) =>
        model == Switch2ControllerModel.ProController2 &&
        (transport == Switch2Transport.Usb || transport == Switch2Transport.BluetoothLe) &&
        kind == Switch2InputReportKind.Common05;

    internal static Switch2CounterSequenceKind Classify(uint current,
        uint previous, byte widthBits, out uint delta)
    {
        delta = widthBits == 8 ? (byte)(current - previous) :
            unchecked(current - previous);
        uint forwardLimit = widthBits == 8 ? 0x7Fu : 0x7FFFFFFFu;
        return delta == 0 ? Switch2CounterSequenceKind.Duplicate :
            delta <= forwardLimit ? Switch2CounterSequenceKind.Forward :
            Switch2CounterSequenceKind.BackwardOrOutOfOrder;
    }

    internal static bool IsConsistent(bool hasDelta, uint delta,
        Switch2CounterSequenceKind sequence)
    {
        if (!hasDelta)
        {
            return delta == 0 && sequence == Switch2CounterSequenceKind.First;
        }
        return sequence switch
        {
            Switch2CounterSequenceKind.Forward => delta != 0,
            Switch2CounterSequenceKind.Duplicate => delta == 0,
            Switch2CounterSequenceKind.BackwardOrOutOfOrder => delta != 0,
            _ => false,
        };
    }
}

internal static class Switch2CanonicalInputBuilder
{
    internal static bool TryDecode(
        in Switch2InputProtocolIdentity identity, ReadOnlySpan<byte> packet,
        out Switch2DecodedInputReport report,
        out Switch2OwnedInputBody body)
    {
        bool decoded;
        ReadOnlySpan<byte> rawBody;
        if (identity.Transport == Switch2Transport.Usb &&
            identity.ProtocolRevision ==
                Switch2InputProtocolRevision.ProUsbCommon05Bcd0201)
        {
            decoded = Switch2InputCodec.TryDecodeUsb(packet, identity.Model,
                out report);
            rawBody = decoded ? packet.Slice(1) : default;
        }
        else if (identity.Transport == Switch2Transport.BluetoothLe)
        {
            decoded = Switch2InputCodec.TryDecodeBluetoothLe(
                identity.ServiceUuid, identity.CharacteristicUuid,
                identity.GattProperties, packet, identity.Model, out report);
            rawBody = decoded ? packet : default;
        }
        else
        {
            decoded = false;
            report = default;
            rawBody = default;
        }

        if (!decoded)
        {
            body = default;
            return false;
        }
        body = new Switch2OwnedInputBody(rawBody);
        return true;
    }
}

public enum Switch2JoyConPairEventKind : byte
{
    Invalid = 0,
    Input,
    HalfLost,
    Split,
}

public enum Switch2JoyConStaleSide : byte
{
    None = 0,
    Left,
    Right,
}

public enum Switch2JoyConPairDisposition : byte
{
    Invalid = 0,
    WaitingForOtherHalf,
    JoinedSnapshot,
    StaleHalf,
    HalfLost,
    Split,
}

public enum Switch2JoyConPairRejection : byte
{
    None = 0,
    InvalidState,
    PairEpochMismatch,
    AlreadySplit,
    InvalidEvent,
    WrongModelOrTransport,
    ClockMismatch,
    StaleGeneration,
    StaleTimestamp,
    HalfNotPresent,
}

public readonly struct Switch2JoyConPairPolicy
{
    public Switch2JoyConPairPolicy(ulong maximumSkewMicroseconds)
    {
        MaximumSkewMicroseconds = maximumSkewMicroseconds;
    }

    public ulong MaximumSkewMicroseconds { get; }
}

public readonly struct Switch2JoyConPairEvent
{
    private Switch2JoyConPairEvent(Switch2JoyConPairEventKind kind,
        ulong pairEpoch, Switch2StickSide side,
        in Switch2CanonicalInputFrame frame, ulong deviceGeneration,
        ulong transportGeneration)
    {
        Kind = kind;
        PairEpoch = pairEpoch;
        Side = side;
        Frame = frame;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
    }

    public Switch2JoyConPairEventKind Kind { get; }
    public ulong PairEpoch { get; }
    public Switch2StickSide Side { get; }
    public Switch2CanonicalInputFrame Frame { get; }
    public ulong DeviceGeneration { get; }
    public ulong TransportGeneration { get; }

    public static Switch2JoyConPairEvent Input(ulong pairEpoch,
        in Switch2CanonicalInputFrame frame)
    {
        Switch2StickSide side = frame.Model ==
            Switch2ControllerModel.JoyCon2Right ? Switch2StickSide.Right :
            Switch2StickSide.Left;
        return new Switch2JoyConPairEvent(Switch2JoyConPairEventKind.Input,
            pairEpoch, side, frame, frame.DeviceGeneration,
            frame.TransportGeneration);
    }

    public static Switch2JoyConPairEvent HalfLost(ulong pairEpoch,
        Switch2StickSide side, ulong deviceGeneration,
        ulong transportGeneration) => new(
        Switch2JoyConPairEventKind.HalfLost, pairEpoch, side, default,
        deviceGeneration, transportGeneration);

    public static Switch2JoyConPairEvent Split(ulong pairEpoch) => new(
        Switch2JoyConPairEventKind.Split, pairEpoch, default, default, 0, 0);
}

public readonly struct Switch2JoyConPairSnapshot
{
    internal Switch2JoyConPairSnapshot(ulong pairEpoch,
        in Switch2CanonicalInputFrame left,
        in Switch2CanonicalInputFrame right, ulong skewQpcTicks)
    {
        PairEpoch = pairEpoch;
        Left = left;
        Right = right;
        CompletionTimestampQpc = Math.Max(left.CompletionTimestampQpc,
            right.CompletionTimestampQpc);
        QpcFrequency = left.QpcFrequency;
        SkewQpcTicks = skewQpcTicks;
    }

    public ulong PairEpoch { get; }
    public Switch2CanonicalInputFrame Left { get; }
    public Switch2CanonicalInputFrame Right { get; }
    public long CompletionTimestampQpc { get; }
    public long QpcFrequency { get; }
    public ulong SkewQpcTicks { get; }
    public ulong SkewMicroseconds => QpcFrequency <= 0 ? 0 :
        (ulong)((UInt128)SkewQpcTicks * 1_000_000u /
            (ulong)QpcFrequency);
}

public readonly struct Switch2JoyConPairResult
{
    internal Switch2JoyConPairResult(
        Switch2JoyConPairDisposition disposition,
        Switch2JoyConPairRejection rejection,
        Switch2JoyConStaleSide staleSide,
        in Switch2JoyConPairSnapshot snapshot)
    {
        Disposition = disposition;
        Rejection = rejection;
        StaleSide = staleSide;
        Snapshot = snapshot;
    }

    public Switch2JoyConPairDisposition Disposition { get; }
    public Switch2JoyConPairRejection Rejection { get; }
    public Switch2JoyConStaleSide StaleSide { get; }
    public Switch2JoyConPairSnapshot Snapshot { get; }
    public bool HasSnapshot =>
        Disposition == Switch2JoyConPairDisposition.JoinedSnapshot;
}

/// <summary>
/// Value-owned pair state. Reducer calls are deterministic and perform no I/O,
/// discovery, persistence, scheduling, or allocation.
/// </summary>
public readonly struct Switch2JoyConPairState
{
    private Switch2JoyConPairState(ulong pairEpoch, bool isSplit,
        bool hasLeft, bool hasLeftLifetimeFence,
        in Switch2CanonicalInputFrame left, bool hasRight,
        bool hasRightLifetimeFence, in Switch2CanonicalInputFrame right)
    {
        PairEpoch = pairEpoch;
        IsSplit = isSplit;
        HasLeft = hasLeft;
        HasLeftLifetimeFence = hasLeftLifetimeFence;
        Left = left;
        HasRight = hasRight;
        HasRightLifetimeFence = hasRightLifetimeFence;
        Right = right;
    }

    public ulong PairEpoch { get; }
    public bool IsSplit { get; }
    public bool HasLeft { get; }
    public bool HasLeftLifetimeFence { get; }
    public Switch2CanonicalInputFrame Left { get; }
    public bool HasRight { get; }
    public bool HasRightLifetimeFence { get; }
    public Switch2CanonicalInputFrame Right { get; }
    public bool IsValid => PairEpoch != 0;

    public static bool TryCreate(ulong pairEpoch,
        out Switch2JoyConPairState state)
    {
        if (pairEpoch == 0)
        {
            state = default;
            return false;
        }
        state = new Switch2JoyConPairState(pairEpoch, false, false, false,
            default, false, false, default);
        return true;
    }

    internal Switch2JoyConPairState WithLeft(
        in Switch2CanonicalInputFrame frame) => new(PairEpoch, IsSplit, true,
        true, frame, HasRight, HasRightLifetimeFence, Right);

    internal Switch2JoyConPairState WithRight(
        in Switch2CanonicalInputFrame frame) => new(PairEpoch, IsSplit,
        HasLeft, HasLeftLifetimeFence, Left, true, true, frame);

    internal Switch2JoyConPairState Without(Switch2StickSide side) =>
        side == Switch2StickSide.Left ? new Switch2JoyConPairState(PairEpoch,
            IsSplit, false, HasLeftLifetimeFence, Left, HasRight,
            HasRightLifetimeFence, Right) :
        new Switch2JoyConPairState(PairEpoch, IsSplit, HasLeft,
            HasLeftLifetimeFence, Left, false, HasRightLifetimeFence, Right);

    internal Switch2JoyConPairState AsSplit() => new(PairEpoch, true, false,
        false, default, false, false, default);
}

public static class Switch2JoyConPairReducer
{
    public static bool TryReduce(in Switch2JoyConPairState state,
        in Switch2JoyConPairEvent pairEvent,
        in Switch2JoyConPairPolicy policy,
        out Switch2JoyConPairState next,
        out Switch2JoyConPairResult result)
    {
        if (!state.IsValid)
        {
            return Reject(state, Switch2JoyConPairRejection.InvalidState,
                out next, out result);
        }
        if (pairEvent.PairEpoch != state.PairEpoch)
        {
            return Reject(state,
                Switch2JoyConPairRejection.PairEpochMismatch,
                out next, out result);
        }
        if (state.IsSplit)
        {
            return Reject(state, Switch2JoyConPairRejection.AlreadySplit,
                out next, out result);
        }

        switch (pairEvent.Kind)
        {
            case Switch2JoyConPairEventKind.Input:
                return ReduceInput(state, pairEvent, policy, out next,
                    out result);
            case Switch2JoyConPairEventKind.HalfLost:
                return ReduceLoss(state, pairEvent, out next, out result);
            case Switch2JoyConPairEventKind.Split:
                next = state.AsSplit();
                result = new Switch2JoyConPairResult(
                    Switch2JoyConPairDisposition.Split,
                    Switch2JoyConPairRejection.None,
                    Switch2JoyConStaleSide.None, default);
                return true;
            default:
                return Reject(state, Switch2JoyConPairRejection.InvalidEvent,
                    out next, out result);
        }
    }

    private static bool ReduceInput(in Switch2JoyConPairState state,
        in Switch2JoyConPairEvent pairEvent,
        in Switch2JoyConPairPolicy policy,
        out Switch2JoyConPairState next,
        out Switch2JoyConPairResult result)
    {
        Switch2CanonicalInputFrame incoming = pairEvent.Frame;
        bool isLeft = incoming.Model == Switch2ControllerModel.JoyCon2Left;
        bool isRight = incoming.Model == Switch2ControllerModel.JoyCon2Right;
        if ((!isLeft && !isRight) ||
            incoming.Transport != Switch2Transport.BluetoothLe ||
            pairEvent.Side != (isLeft ? Switch2StickSide.Left :
                Switch2StickSide.Right))
        {
            return Reject(state,
                Switch2JoyConPairRejection.WrongModelOrTransport,
                out next, out result);
        }

        bool hasExisting = isLeft ? state.HasLeft : state.HasRight;
        bool hasLifetimeFence = isLeft ? state.HasLeftLifetimeFence :
            state.HasRightLifetimeFence;
        Switch2CanonicalInputFrame existing = isLeft ? state.Left :
            state.Right;
        if (hasLifetimeFence)
        {
            if (incoming.QpcFrequency != existing.QpcFrequency)
            {
                return Reject(state,
                    Switch2JoyConPairRejection.ClockMismatch,
                    out next, out result);
            }
            int generationOrder = CompareGeneration(in incoming, in existing);
            if (generationOrder < 0 || !hasExisting && generationOrder == 0)
            {
                return Reject(state,
                    Switch2JoyConPairRejection.StaleGeneration,
                    out next, out result);
            }
            if (incoming.CompletionTimestampQpc <
                    existing.CompletionTimestampQpc)
            {
                return Reject(state,
                    Switch2JoyConPairRejection.StaleTimestamp,
                    out next, out result);
            }
        }

        Switch2JoyConPairState candidate = isLeft ? state.WithLeft(incoming) :
            state.WithRight(incoming);
        if (!candidate.HasLeft || !candidate.HasRight)
        {
            next = candidate;
            result = new Switch2JoyConPairResult(
                Switch2JoyConPairDisposition.WaitingForOtherHalf,
                Switch2JoyConPairRejection.None,
                Switch2JoyConStaleSide.None, default);
            return true;
        }
        if (candidate.Left.QpcFrequency != candidate.Right.QpcFrequency)
        {
            return Reject(state, Switch2JoyConPairRejection.ClockMismatch,
                out next, out result);
        }

        long leftTimestamp = candidate.Left.CompletionTimestampQpc;
        long rightTimestamp = candidate.Right.CompletionTimestampQpc;
        ulong skew = leftTimestamp >= rightTimestamp ?
            (ulong)(leftTimestamp - rightTimestamp) :
            (ulong)(rightTimestamp - leftTimestamp);
        bool withinBudget = (UInt128)skew * 1_000_000u <=
            (UInt128)policy.MaximumSkewMicroseconds *
            (ulong)candidate.Left.QpcFrequency;
        if (!withinBudget)
        {
            next = candidate;
            result = new Switch2JoyConPairResult(
                Switch2JoyConPairDisposition.StaleHalf,
                Switch2JoyConPairRejection.None,
                leftTimestamp < rightTimestamp ?
                    Switch2JoyConStaleSide.Left :
                    Switch2JoyConStaleSide.Right, default);
            return true;
        }

        var snapshot = new Switch2JoyConPairSnapshot(state.PairEpoch,
            candidate.Left, candidate.Right, skew);
        next = candidate;
        result = new Switch2JoyConPairResult(
            Switch2JoyConPairDisposition.JoinedSnapshot,
            Switch2JoyConPairRejection.None,
            Switch2JoyConStaleSide.None, snapshot);
        return true;
    }

    private static bool ReduceLoss(in Switch2JoyConPairState state,
        in Switch2JoyConPairEvent pairEvent,
        out Switch2JoyConPairState next,
        out Switch2JoyConPairResult result)
    {
        if (pairEvent.Side is not (Switch2StickSide.Left or
                Switch2StickSide.Right) || pairEvent.DeviceGeneration == 0 ||
            pairEvent.TransportGeneration == 0)
        {
            return Reject(state, Switch2JoyConPairRejection.InvalidEvent,
                out next, out result);
        }
        bool hasHalf = pairEvent.Side == Switch2StickSide.Left ?
            state.HasLeft : state.HasRight;
        Switch2CanonicalInputFrame half = pairEvent.Side ==
            Switch2StickSide.Left ? state.Left : state.Right;
        if (!hasHalf)
        {
            return Reject(state, Switch2JoyConPairRejection.HalfNotPresent,
                out next, out result);
        }
        if (half.DeviceGeneration != pairEvent.DeviceGeneration ||
            half.TransportGeneration != pairEvent.TransportGeneration)
        {
            return Reject(state, Switch2JoyConPairRejection.StaleGeneration,
                out next, out result);
        }

        next = state.Without(pairEvent.Side);
        result = new Switch2JoyConPairResult(
            Switch2JoyConPairDisposition.HalfLost,
            Switch2JoyConPairRejection.None,
            pairEvent.Side == Switch2StickSide.Left ?
                Switch2JoyConStaleSide.Left :
                Switch2JoyConStaleSide.Right, default);
        return true;
    }

    private static int CompareGeneration(
        in Switch2CanonicalInputFrame left,
        in Switch2CanonicalInputFrame right)
    {
        int device = left.DeviceGeneration.CompareTo(right.DeviceGeneration);
        return device != 0 ? device :
            left.TransportGeneration.CompareTo(right.TransportGeneration);
    }

    private static bool Reject(in Switch2JoyConPairState state,
        Switch2JoyConPairRejection rejection,
        out Switch2JoyConPairState next,
        out Switch2JoyConPairResult result)
    {
        next = state;
        result = new Switch2JoyConPairResult(
            Switch2JoyConPairDisposition.Invalid, rejection,
            Switch2JoyConStaleSide.None, default);
        return false;
    }
}
