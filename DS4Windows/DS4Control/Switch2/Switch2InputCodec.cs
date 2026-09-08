using System;
using System.Buffers.Binary;

namespace DS4Windows.Switch2;

/// <summary>
/// Strict, allocation-free parsing for the currently evidenced Switch 2 input
/// report layouts. This type performs no device discovery, I/O, registration or
/// controller-state publication.
/// </summary>
public static class Switch2InputCodec
{
    public const int UsbPacketLength = 64;
    public const int BluetoothLeBodyLength = 63;

    public static readonly Guid ServiceUuid =
        new("ab7de9be-89fe-49ad-828f-118f09df7fd0");

    public static readonly Guid Common05CharacteristicUuid =
        new("ab7de9be-89fe-49ad-828f-118f09df7fd2");

    public static readonly Guid JoyCon2Left07CharacteristicUuid =
        new("cc1bbbb5-7354-4d32-a716-a81cb241a32a");

    public static readonly Guid JoyCon2Right08CharacteristicUuid =
        new("d5a9e01e-2ffc-4cca-b20c-8b67142bf442");

    public static readonly Guid ProController2_09CharacteristicUuid =
        new("7492866c-ec3e-4619-8258-32755ffcc0f8");

    private const Switch2GattProperty InputGattProperties =
        Switch2GattProperty.Read | Switch2GattProperty.Notify;

    public static bool TryResolveBluetoothLeInputIdentity(
        Guid characteristicUuid,
        out Switch2GattCharacteristicIdentity identity)
    {
        if (characteristicUuid == Common05CharacteristicUuid)
        {
            identity = new Switch2GattCharacteristicIdentity(ServiceUuid,
                Common05CharacteristicUuid, Switch2InputReportKind.Common05,
                Switch2ControllerModel.Unknown, InputGattProperties);
            return true;
        }

        if (characteristicUuid == JoyCon2Left07CharacteristicUuid)
        {
            identity = new Switch2GattCharacteristicIdentity(ServiceUuid,
                JoyCon2Left07CharacteristicUuid,
                Switch2InputReportKind.JoyCon2Left07,
                Switch2ControllerModel.JoyCon2Left, InputGattProperties);
            return true;
        }

        if (characteristicUuid == JoyCon2Right08CharacteristicUuid)
        {
            identity = new Switch2GattCharacteristicIdentity(ServiceUuid,
                JoyCon2Right08CharacteristicUuid,
                Switch2InputReportKind.JoyCon2Right08,
                Switch2ControllerModel.JoyCon2Right, InputGattProperties);
            return true;
        }

        if (characteristicUuid == ProController2_09CharacteristicUuid)
        {
            identity = new Switch2GattCharacteristicIdentity(ServiceUuid,
                ProController2_09CharacteristicUuid,
                Switch2InputReportKind.ProController2_09,
                Switch2ControllerModel.ProController2, InputGattProperties);
            return true;
        }

        identity = default;
        return false;
    }

    public static bool TryDecodeUsb(ReadOnlySpan<byte> packet,
        Switch2ControllerModel model,
        out Switch2DecodedInputReport report)
    {
        if (packet.Length != UsbPacketLength ||
            model != Switch2ControllerModel.ProController2 ||
            !TryGetReportKind(packet[0], out Switch2InputReportKind kind) ||
            kind != Switch2InputReportKind.Common05 ||
            !IsCompatible(model, kind))
        {
            report = default;
            return false;
        }

        return TryDecodeBody(packet.Slice(1), model, kind, out report);
    }

    public static bool TryDecodeBluetoothLe(Guid serviceUuid,
        Guid characteristicUuid,
        Switch2GattProperty actualProperties, ReadOnlySpan<byte> body,
        Switch2ControllerModel advertisementVerifiedModel,
        out Switch2DecodedInputReport report)
    {
        if (body.Length != BluetoothLeBodyLength || serviceUuid != ServiceUuid ||
            !TryResolveBluetoothLeInputIdentity(characteristicUuid,
                out Switch2GattCharacteristicIdentity identity) ||
            !identity.HasRequiredProperties(actualProperties))
        {
            report = default;
            return false;
        }

        Switch2ControllerModel model = identity.FixedModel ==
            Switch2ControllerModel.Unknown ? advertisementVerifiedModel :
            identity.FixedModel;

        if (!IsSupportedModel(model) ||
            (identity.FixedModel != Switch2ControllerModel.Unknown &&
             advertisementVerifiedModel != identity.FixedModel) ||
            !IsCompatible(model, identity.ReportKind))
        {
            report = default;
            return false;
        }

        return TryDecodeBody(body, model, identity.ReportKind, out report);
    }

    public static bool TryDecodeCommon05(ReadOnlySpan<byte> body,
        out Switch2CommonInputReport report)
    {
        if (body.Length != BluetoothLeBodyLength)
        {
            report = default;
            return false;
        }

        report = new Switch2CommonInputReport(
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0x00, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0x04, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0x08, 2)),
            DecodePackedStick(body.Slice(0x0A, 3)),
            DecodePackedStick(body.Slice(0x0D, 3)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0x10, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0x12, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0x14, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0x16, 2)),
            body[0x18],
            DecodeVector3(body.Slice(0x19, 6)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0x1F, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0x21, 2)),
            body[0x23],
            ReadUInt48LittleEndian(body.Slice(0x24, 6)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0x2A, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0x2E, 2)),
            DecodeVector3(body.Slice(0x30, 6)),
            DecodeVector3(body.Slice(0x36, 6)),
            ReadUInt24LittleEndian(body.Slice(0x3C, 3)));
        return true;
    }

    /// <summary>
    /// Re-encodes every byte retained by <see cref="TryDecodeCommon05"/>. This
    /// is an offline/replay invariant helper, not a controller output report.
    /// </summary>
    internal static bool TryEncodeCommon05(in Switch2CommonInputReport report,
        Span<byte> body)
    {
        if (body.Length != BluetoothLeBodyLength)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0x00, 4),
            report.Counter);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0x04, 4),
            report.Buttons);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(0x08, 2),
            report.Opaque08Raw);
        EncodePackedStick(report.LeftStick, body.Slice(0x0A, 3));
        EncodePackedStick(report.RightStick, body.Slice(0x0D, 3));
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(0x10, 2),
            report.MouseX);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(0x12, 2),
            report.MouseY);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(0x14, 2),
            report.MouseUnknown0Raw);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(0x16, 2),
            report.MouseUnknown1Raw);
        body[0x18] = report.Opaque18Raw;
        EncodeVector3(report.Magnetometer, body.Slice(0x19, 6));
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(0x1F, 2),
            report.BatteryVoltageMillivolts);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(0x21, 2),
            report.BatteryCurrentRaw);
        body[0x23] = report.Opaque23Raw;
        WriteUInt48LittleEndian(report.Opaque24To29Raw, body.Slice(0x24, 6));
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0x2A, 4),
            report.MotionTimestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(0x2E, 2),
            report.TemperatureRawBits);
        EncodeVector3(report.Accelerometer, body.Slice(0x30, 6));
        EncodeVector3(report.Gyroscope, body.Slice(0x36, 6));
        WriteUInt24LittleEndian(report.Opaque3CTo3ERaw,
            body.Slice(0x3C, 3));
        return true;
    }

    public static bool TryDecodeJoyCon2Left07(ReadOnlySpan<byte> body,
        out Switch2BasicInputReport report) =>
        TryDecodeJoyCon(body, Switch2InputReportKind.JoyCon2Left07,
            out report);

    public static bool TryDecodeJoyCon2Right08(ReadOnlySpan<byte> body,
        out Switch2BasicInputReport report) =>
        TryDecodeJoyCon(body, Switch2InputReportKind.JoyCon2Right08,
            out report);

    public static bool TryDecodeProController2_09(ReadOnlySpan<byte> body,
        out Switch2BasicInputReport report)
    {
        if (body.Length != BluetoothLeBodyLength || body[0x0E] > 40)
        {
            report = default;
            return false;
        }

        report = new Switch2BasicInputReport(
            Switch2InputReportKind.ProController2_09,
            body[0x00], body[0x01], ReadUInt24LittleEndian(body.Slice(0x02, 3)),
            DecodePackedStick(body.Slice(0x05, 3)),
            DecodePackedStick(body.Slice(0x08, 3)), true,
            new Switch2OpaqueBodyRegion(0x0F, 40, body[0x0E], false));
        return true;
    }

    public static Switch2StickRaw DecodePackedStick(ReadOnlySpan<byte> data)
    {
        if (data.Length != 3)
        {
            throw new ArgumentException(
                "A packed Switch 2 stick must be exactly three bytes.",
                nameof(data));
        }

        ushort x = (ushort)(data[0] | ((data[1] & 0x0F) << 8));
        ushort y = (ushort)((data[1] >> 4) | (data[2] << 4));
        return new Switch2StickRaw(x, y);
    }

    private static bool TryDecodeBody(ReadOnlySpan<byte> body,
        Switch2ControllerModel model, Switch2InputReportKind kind,
        out Switch2DecodedInputReport report)
    {
        switch (kind)
        {
            case Switch2InputReportKind.Common05:
                if (TryDecodeCommon05(body,
                    out Switch2CommonInputReport common))
                {
                    report = new Switch2DecodedInputReport(model, common);
                    return true;
                }
                break;
            case Switch2InputReportKind.JoyCon2Left07:
                if (TryDecodeJoyCon2Left07(body,
                    out Switch2BasicInputReport left))
                {
                    report = new Switch2DecodedInputReport(model, left);
                    return true;
                }
                break;
            case Switch2InputReportKind.JoyCon2Right08:
                if (TryDecodeJoyCon2Right08(body,
                    out Switch2BasicInputReport right))
                {
                    report = new Switch2DecodedInputReport(model, right);
                    return true;
                }
                break;
            case Switch2InputReportKind.ProController2_09:
                if (TryDecodeProController2_09(body,
                    out Switch2BasicInputReport pro))
                {
                    report = new Switch2DecodedInputReport(model, pro);
                    return true;
                }
                break;
        }

        report = default;
        return false;
    }

    private static bool TryDecodeJoyCon(ReadOnlySpan<byte> body,
        Switch2InputReportKind kind, out Switch2BasicInputReport report)
    {
        if (body.Length != BluetoothLeBodyLength || body[0x0F] > 40 ||
            kind is not Switch2InputReportKind.JoyCon2Left07 and
                not Switch2InputReportKind.JoyCon2Right08)
        {
            report = default;
            return false;
        }

        report = new Switch2BasicInputReport(kind, body[0x00], body[0x01],
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0x02, 2)),
            DecodePackedStick(body.Slice(0x05, 3)), default, false,
            new Switch2OpaqueBodyRegion(0x10, 40, body[0x0F], false));
        return true;
    }

    private static Switch2Vector3Raw DecodeVector3(ReadOnlySpan<byte> data) =>
        new(BinaryPrimitives.ReadInt16LittleEndian(data.Slice(0, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(data.Slice(2, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(data.Slice(4, 2)));

    private static void EncodeVector3(in Switch2Vector3Raw value,
        Span<byte> data)
    {
        BinaryPrimitives.WriteInt16LittleEndian(data.Slice(0, 2), value.X);
        BinaryPrimitives.WriteInt16LittleEndian(data.Slice(2, 2), value.Y);
        BinaryPrimitives.WriteInt16LittleEndian(data.Slice(4, 2), value.Z);
    }

    private static void EncodePackedStick(in Switch2StickRaw value,
        Span<byte> data)
    {
        data[0] = (byte)value.X;
        data[1] = (byte)(((value.X >> 8) & 0x0F) |
            ((value.Y & 0x0F) << 4));
        data[2] = (byte)(value.Y >> 4);
    }

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> data) =>
        (uint)(data[0] | (data[1] << 8) | (data[2] << 16));

    private static ulong ReadUInt48LittleEndian(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4)) |
        ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2)) << 32);

    private static void WriteUInt24LittleEndian(uint value, Span<byte> data)
    {
        data[0] = (byte)value;
        data[1] = (byte)(value >> 8);
        data[2] = (byte)(value >> 16);
    }

    private static void WriteUInt48LittleEndian(ulong value, Span<byte> data)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(0, 4),
            (uint)value);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(4, 2),
            (ushort)(value >> 32));
    }

    private static bool TryGetReportKind(byte reportId,
        out Switch2InputReportKind kind)
    {
        kind = (Switch2InputReportKind)reportId;
        return kind is Switch2InputReportKind.Common05 or
            Switch2InputReportKind.JoyCon2Left07 or
            Switch2InputReportKind.JoyCon2Right08 or
            Switch2InputReportKind.ProController2_09;
    }

    private static bool IsSupportedModel(Switch2ControllerModel model) =>
        model is Switch2ControllerModel.JoyCon2Left or
            Switch2ControllerModel.JoyCon2Right or
            Switch2ControllerModel.ProController2;

    private static bool IsCompatible(Switch2ControllerModel model,
        Switch2InputReportKind kind) =>
        kind == Switch2InputReportKind.Common05 ||
        (kind == Switch2InputReportKind.JoyCon2Left07 &&
         model == Switch2ControllerModel.JoyCon2Left) ||
        (kind == Switch2InputReportKind.JoyCon2Right08 &&
         model == Switch2ControllerModel.JoyCon2Right) ||
        (kind == Switch2InputReportKind.ProController2_09 &&
         model == Switch2ControllerModel.ProController2);
}
