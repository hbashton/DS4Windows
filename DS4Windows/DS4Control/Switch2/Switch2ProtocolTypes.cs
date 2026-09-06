using System;

namespace DS4Windows.Switch2;

/// <summary>
/// Controller models whose packet layouts are sufficiently evidenced for the
/// Phase 1, read-only codec. Values are protocol identities, not runtime device
/// registrations.
/// </summary>
public enum Switch2ControllerModel : byte
{
    Unknown = 0,
    JoyCon2Right = 1,
    JoyCon2Left = 2,
    ProController2 = 3,
}

public enum Switch2Transport : byte
{
    Usb = 1,
    BluetoothLe = 2,
}

public enum Switch2PacketDirection : byte
{
    Input = 1,
}

public enum Switch2InputReportKind : byte
{
    Common05 = 0x05,
    JoyCon2Left07 = 0x07,
    JoyCon2Right08 = 0x08,
    ProController2_09 = 0x09,
}

[Flags]
public enum Switch2GattProperty : byte
{
    None = 0,
    Read = 1 << 0,
    Notify = 1 << 1,
    Write = 1 << 2,
    WriteWithoutResponse = 1 << 3,
}

public readonly struct Switch2StickRaw : IEquatable<Switch2StickRaw>
{
    public Switch2StickRaw(ushort x, ushort y)
    {
        X = x;
        Y = y;
    }

    public ushort X { get; }

    public ushort Y { get; }

    public bool Equals(Switch2StickRaw other) => X == other.X && Y == other.Y;

    public override bool Equals(object obj) =>
        obj is Switch2StickRaw other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);
}

public readonly struct Switch2Vector3Raw : IEquatable<Switch2Vector3Raw>
{
    public Switch2Vector3Raw(short x, short y, short z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public short X { get; }

    public short Y { get; }

    public short Z { get; }

    public bool Equals(Switch2Vector3Raw other) =>
        X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object obj) =>
        obj is Switch2Vector3Raw other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
}

/// <summary>
/// A byte region in the original report. The Phase 1 codec intentionally does
/// not interpret model-specific packed motion data.
/// </summary>
public readonly struct Switch2OpaqueBodyRegion
{
    public Switch2OpaqueBodyRegion(byte bodyOffset, byte capacity,
        byte declaredLength, bool decoded)
    {
        BodyOffset = bodyOffset;
        Capacity = capacity;
        DeclaredLength = declaredLength;
        IsDecoded = decoded;
    }

    /// <summary>
    /// Offset in the 63-byte report body. A USB report's report-ID byte is not
    /// part of this coordinate system.
    /// </summary>
    public byte BodyOffset { get; }

    public byte Capacity { get; }

    public byte DeclaredLength { get; }

    public bool IsDecoded { get; }

    public bool HasData => DeclaredLength != 0;

    public bool UsesObservedLength => DeclaredLength is 0 or 30 or 40;
}

/// <summary>
/// Capture-backed common input report 0x05 fields. All values remain in raw
/// controller units; calibration, normalization and axis transforms are outside
/// this codec.
/// </summary>
public readonly struct Switch2CommonInputReport
{
    internal Switch2CommonInputReport(uint counter, uint buttons,
        ushort opaque08Raw,
        Switch2StickRaw leftStick, Switch2StickRaw rightStick,
        ushort mouseX, ushort mouseY, ushort mouseUnknown0Raw,
        ushort mouseUnknown1Raw, byte opaque18Raw,
        Switch2Vector3Raw magnetometer,
        ushort batteryVoltageMillivolts, ushort batteryCurrentRaw,
        byte opaque23Raw, ulong opaque24To29Raw,
        uint motionTimestamp,
        ushort temperatureRawBits, Switch2Vector3Raw accelerometer,
        Switch2Vector3Raw gyroscope, uint opaque3CTo3ERaw)
    {
        Counter = counter;
        Buttons = buttons;
        Opaque08Raw = opaque08Raw;
        LeftStick = leftStick;
        RightStick = rightStick;
        MouseX = mouseX;
        MouseY = mouseY;
        MouseUnknown0Raw = mouseUnknown0Raw;
        MouseUnknown1Raw = mouseUnknown1Raw;
        Opaque18Raw = opaque18Raw;
        Magnetometer = magnetometer;
        BatteryVoltageMillivolts = batteryVoltageMillivolts;
        BatteryCurrentRaw = batteryCurrentRaw;
        Opaque23Raw = opaque23Raw;
        Opaque24To29Raw = opaque24To29Raw;
        MotionTimestamp = motionTimestamp;
        TemperatureRawBits = temperatureRawBits;
        Accelerometer = accelerometer;
        Gyroscope = gyroscope;
        Opaque3CTo3ERaw = opaque3CTo3ERaw;
    }

    public uint Counter { get; }

    public uint Buttons { get; }

    /// <summary>
    /// Uninterpreted body bytes <c>0x08..0x09</c>, retained losslessly in
    /// little-endian order.
    /// </summary>
    public ushort Opaque08Raw { get; }

    public Switch2StickRaw LeftStick { get; }

    public Switch2StickRaw RightStick { get; }

    public ushort MouseX { get; }

    public ushort MouseY { get; }

    public ushort MouseUnknown0Raw { get; }

    public ushort MouseUnknown1Raw { get; }

    /// <summary>
    /// Optical-surface roughness at body bytes <c>0x14..0x15</c>. This name is
    /// a GPL-compatible adaptation of Switch2Connect's Common05 decoder; the
    /// original raw alias remains available for byte-exact round trips.
    /// </summary>
    public ushort MouseRoughness => MouseUnknown0Raw;

    /// <summary>
    /// Optical-surface distance at body bytes <c>0x16..0x17</c>. Zero means no
    /// active surface in the source-backed activation policy; it is not
    /// assigned a physical unit here.
    /// </summary>
    public ushort MouseDistance => MouseUnknown1Raw;

    /// <summary>
    /// Uninterpreted body byte <c>0x18</c>.
    /// </summary>
    public byte Opaque18Raw { get; }

    public Switch2Vector3Raw Magnetometer { get; }

    public ushort BatteryVoltageMillivolts { get; }

    public ushort BatteryCurrentRaw { get; }

    /// <summary>
    /// Uninterpreted body byte <c>0x23</c>. Public implementations establish
    /// the preceding two bytes as one little-endian current field; no
    /// charging-state meaning is assigned to this following byte.
    /// </summary>
    public byte Opaque23Raw { get; }

    /// <summary>
    /// Uninterpreted body bytes <c>0x24..0x29</c>, retained as a 48-bit
    /// little-endian value. The upper 16 bits are always zero.
    /// </summary>
    public ulong Opaque24To29Raw { get; }

    public uint MotionTimestamp { get; }

    /// <summary>
    /// Uninterpreted temperature field bits. Signedness and scale are not yet
    /// proven by a project-owned capture.
    /// </summary>
    public ushort TemperatureRawBits { get; }

    public Switch2Vector3Raw Accelerometer { get; }

    public Switch2Vector3Raw Gyroscope { get; }

    /// <summary>
    /// Uninterpreted body bytes <c>0x3C..0x3E</c>, retained as a 24-bit
    /// little-endian value. The upper eight bits are always zero.
    /// </summary>
    public uint Opaque3CTo3ERaw { get; }

}

/// <summary>
/// Only the evidenced basic fields of reports 0x07, 0x08 and 0x09. Their
/// packed motion payload is deliberately opaque.
/// </summary>
public readonly struct Switch2BasicInputReport
{
    internal Switch2BasicInputReport(Switch2InputReportKind kind,
        byte counter, byte powerInfo, uint buttons,
        Switch2StickRaw primaryStick, Switch2StickRaw secondaryStick,
        bool hasSecondaryStick, Switch2OpaqueBodyRegion motion)
    {
        Kind = kind;
        Counter = counter;
        PowerInfo = powerInfo;
        Buttons = buttons;
        PrimaryStick = primaryStick;
        SecondaryStick = secondaryStick;
        HasSecondaryStick = hasSecondaryStick;
        Motion = motion;
    }

    public Switch2InputReportKind Kind { get; }

    public byte Counter { get; }

    public byte PowerInfo { get; }

    public uint Buttons { get; }

    public Switch2StickRaw PrimaryStick { get; }

    public Switch2StickRaw SecondaryStick { get; }

    public bool HasSecondaryStick { get; }

    public Switch2OpaqueBodyRegion Motion { get; }
}

/// <summary>
/// Allocation-free discriminated result for any Phase 1 input report.
/// </summary>
public readonly struct Switch2DecodedInputReport
{
    internal Switch2DecodedInputReport(Switch2ControllerModel model,
        Switch2CommonInputReport common)
    {
        Model = model;
        Kind = Switch2InputReportKind.Common05;
        Common = common;
        Basic = default;
        IsCommon = true;
    }

    internal Switch2DecodedInputReport(Switch2ControllerModel model,
        Switch2BasicInputReport basic)
    {
        Model = model;
        Kind = basic.Kind;
        Common = default;
        Basic = basic;
        IsCommon = false;
    }

    public Switch2ControllerModel Model { get; }

    public Switch2InputReportKind Kind { get; }

    public bool IsCommon { get; }

    public Switch2CommonInputReport Common { get; }

    public Switch2BasicInputReport Basic { get; }

    public uint Counter => IsCommon ? Common.Counter : Basic.Counter;

    public byte CounterWidthBits => IsCommon ? (byte)32 : (byte)8;

    /// <summary>
    /// Whether the decoded model has a usable left-stick slot. Report 0x05
    /// physically carries both raw slots, but the absent slot on a single
    /// Joy-Con 2 is not a supported control and may contain garbage.
    /// </summary>
    public bool HasLeftStick => Model is Switch2ControllerModel.JoyCon2Left or
        Switch2ControllerModel.ProController2;

    public bool HasRightStick => Model is Switch2ControllerModel.JoyCon2Right or
        Switch2ControllerModel.ProController2;

    /// <summary>
    /// Report 0x05 mouse fields are applicable only to Joy-Con 2 models.
    /// </summary>
    public bool HasMouseData => IsCommon &&
        Model is Switch2ControllerModel.JoyCon2Left or
            Switch2ControllerModel.JoyCon2Right;

    /// <summary>
    /// Returns the declared, still-opaque motion bytes from a caller-supplied
    /// 63-byte body. The bytes are not copied or interpreted.
    /// </summary>
    public bool TrySliceOpaqueMotionBody(ReadOnlySpan<byte> body,
        out ReadOnlySpan<byte> motionBytes)
    {
        if (IsCommon || body.Length != Switch2InputCodec.BluetoothLeBodyLength)
        {
            motionBytes = default;
            return false;
        }

        int offset = Basic.Motion.BodyOffset;
        int length = Basic.Motion.DeclaredLength;
        int lengthOffset = Kind == Switch2InputReportKind.ProController2_09 ?
            0x0E : 0x0F;
        if (body[lengthOffset] != length || offset + length > body.Length)
        {
            motionBytes = default;
            return false;
        }

        motionBytes = body.Slice(offset, length);
        return true;
    }
}

public readonly struct Switch2GattCharacteristicIdentity
{
    public Switch2GattCharacteristicIdentity(Guid serviceUuid,
        Guid characteristicUuid, Switch2InputReportKind reportKind,
        Switch2ControllerModel fixedModel,
        Switch2GattProperty requiredProperties)
    {
        ServiceUuid = serviceUuid;
        CharacteristicUuid = characteristicUuid;
        ReportKind = reportKind;
        FixedModel = fixedModel;
        RequiredProperties = requiredProperties;
    }

    public Guid ServiceUuid { get; }

    public Guid CharacteristicUuid { get; }

    public Switch2InputReportKind ReportKind { get; }

    /// <summary>
    /// Unknown means the common characteristic is valid for all supported
    /// models and the caller must supply the advertisement-verified model.
    /// </summary>
    public Switch2ControllerModel FixedModel { get; }

    public Switch2GattProperty RequiredProperties { get; }

    public bool HasRequiredProperties(Switch2GattProperty actual) =>
        (actual & RequiredProperties) == RequiredProperties;
}
