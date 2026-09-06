using System;

namespace DS4Windows.Switch2;

public enum Switch2StickSide : byte
{
    Invalid = 0,
    Left = 1,
    Right = 2,
}

public enum Switch2FactoryCalibrationStorageSlot : byte
{
    Primary = 1,
    Secondary = 2,
}

public readonly struct Switch2FactoryCalibrationMetadata
{
    internal Switch2FactoryCalibrationMetadata(Switch2StickSide side,
        Switch2FactoryCalibrationStorageSlot storageSlot, uint address,
        byte length)
    {
        Side = side;
        StorageSlot = storageSlot;
        Address = address;
        Length = length;
    }

    public Switch2StickSide Side { get; }

    public Switch2FactoryCalibrationStorageSlot StorageSlot { get; }

    public uint Address { get; }

    public byte Length { get; }
}

public readonly struct Switch2UserCalibrationMetadata
{
    internal Switch2UserCalibrationMetadata(Switch2StickSide side,
        uint address, byte length)
    {
        Side = side;
        Address = address;
        Length = length;
    }

    public Switch2StickSide Side { get; }

    public uint Address { get; }

    public byte Length { get; }
}

public readonly struct Switch2StickCalibration :
    IEquatable<Switch2StickCalibration>
{
    internal Switch2StickCalibration(ushort neutralX, ushort neutralY,
        ushort positiveRangeX, ushort positiveRangeY,
        ushort negativeRangeX, ushort negativeRangeY)
    {
        NeutralX = neutralX;
        NeutralY = neutralY;
        PositiveRangeX = positiveRangeX;
        PositiveRangeY = positiveRangeY;
        NegativeRangeX = negativeRangeX;
        NegativeRangeY = negativeRangeY;
    }

    public ushort NeutralX { get; }

    public ushort NeutralY { get; }

    public ushort PositiveRangeX { get; }

    public ushort PositiveRangeY { get; }

    public ushort NegativeRangeX { get; }

    public ushort NegativeRangeY { get; }

    /// <summary>
    /// Defensive applicability gate: zero and saturated 12-bit components are
    /// rejected as erased/corrupt sentinels. This does not prove that every
    /// interior calibration is physically valid.
    /// </summary>
    public bool IsUsable => IsInterior(NeutralX) && IsInterior(NeutralY) &&
        IsInterior(PositiveRangeX) && IsInterior(PositiveRangeY) &&
        IsInterior(NegativeRangeX) && IsInterior(NegativeRangeY);

    public bool Equals(Switch2StickCalibration other) =>
        NeutralX == other.NeutralX && NeutralY == other.NeutralY &&
        PositiveRangeX == other.PositiveRangeX &&
        PositiveRangeY == other.PositiveRangeY &&
        NegativeRangeX == other.NegativeRangeX &&
        NegativeRangeY == other.NegativeRangeY;

    public override bool Equals(object obj) =>
        obj is Switch2StickCalibration other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(NeutralX, NeutralY,
        PositiveRangeX, PositiveRangeY, NegativeRangeX, NegativeRangeY);

    private static bool IsInterior(ushort value) => value is > 0 and < 0x0FFF;
}

/// <summary>
/// Pure parsing and metadata for factory and read-only user stick calibration.
/// Current SDL and the independently implemented hid-nintendo2 driver both use
/// 0x1FC080 for the Pro right stick and require the little-endian 0xA1B2 marker.
/// Switch2Connect's conflicting 0x1FC060 address is deliberately not used.
/// </summary>
public static class Switch2CalibrationCodec
{
    public const int StickCalibrationLength = 9;
    public const int UserStickCalibrationLength = 11;
    public const ushort UserStickCalibrationMagic = 0xA1B2;
    public const uint PrimaryFactoryStickAddress = 0x0130A8;
    public const uint SecondaryFactoryStickAddress = 0x0130E8;
    public const uint PrimaryUserStickAddress = 0x1FC040;
    public const uint SecondaryUserStickAddress = 0x1FC080;

    public static bool SupportsLiveUserCalibration => true;

    public static bool TryGetFactoryStickMetadata(
        Switch2ControllerModel model, Switch2StickSide side,
        out Switch2FactoryCalibrationMetadata metadata)
    {
        Switch2FactoryCalibrationStorageSlot storageSlot = (model, side) switch
        {
            (Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left) =>
                Switch2FactoryCalibrationStorageSlot.Primary,
            (Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right) =>
                Switch2FactoryCalibrationStorageSlot.Primary,
            (Switch2ControllerModel.ProController2, Switch2StickSide.Left) =>
                Switch2FactoryCalibrationStorageSlot.Primary,
            (Switch2ControllerModel.ProController2, Switch2StickSide.Right) =>
                Switch2FactoryCalibrationStorageSlot.Secondary,
            _ => 0,
        };
        if (storageSlot == 0)
        {
            metadata = default;
            return false;
        }

        uint address = storageSlot ==
            Switch2FactoryCalibrationStorageSlot.Primary ?
            PrimaryFactoryStickAddress : SecondaryFactoryStickAddress;
        metadata = new Switch2FactoryCalibrationMetadata(side, storageSlot,
            address, StickCalibrationLength);
        return true;
    }

    public static bool TryGetLiveUserStickMetadata(
        Switch2ControllerModel model, Switch2StickSide side,
        out Switch2UserCalibrationMetadata metadata)
    {
        uint address = (model, side) switch
        {
            (Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left) =>
                PrimaryUserStickAddress,
            (Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right) =>
                PrimaryUserStickAddress,
            (Switch2ControllerModel.ProController2, Switch2StickSide.Left) =>
                PrimaryUserStickAddress,
            (Switch2ControllerModel.ProController2, Switch2StickSide.Right) =>
                SecondaryUserStickAddress,
            _ => 0,
        };
        if (address == 0)
        {
            metadata = default;
            return false;
        }

        metadata = new Switch2UserCalibrationMetadata(side, address,
            UserStickCalibrationLength);
        return true;
    }

    public static bool TryDecodeUserStick(ReadOnlySpan<byte> data,
        out Switch2StickCalibration calibration)
    {
        if (data.Length != UserStickCalibrationLength ||
            (ushort)(data[0] | (data[1] << 8)) !=
                UserStickCalibrationMagic)
        {
            calibration = default;
            return false;
        }

        return TryDecodeStick(data.Slice(2), out calibration);
    }

    public static bool TryDecodeStick(ReadOnlySpan<byte> data,
        out Switch2StickCalibration calibration)
    {
        if (data.Length != StickCalibrationLength)
        {
            calibration = default;
            return false;
        }

        calibration = new Switch2StickCalibration(
            DecodePacked12(data[0], data[1], lowNibble: true),
            DecodePacked12(data[1], data[2], lowNibble: false),
            DecodePacked12(data[3], data[4], lowNibble: true),
            DecodePacked12(data[4], data[5], lowNibble: false),
            DecodePacked12(data[6], data[7], lowNibble: true),
            DecodePacked12(data[7], data[8], lowNibble: false));
        return true;
    }

    /// <summary>
    /// Fail-closed adoption gate for a structurally decoded factory record.
    /// Besides erased/saturated sentinels, each signed range must terminate
    /// inside the controller's 12-bit domain. This proves bounded arithmetic,
    /// not the physical accuracy of a calibration captured from hardware.
    /// </summary>
    public static bool TryValidateAdoptable(
        in Switch2StickCalibration calibration,
        out Switch2CalibrationAdoptionFailure failure)
    {
        if (!calibration.IsUsable)
        {
            failure =
                Switch2CalibrationAdoptionFailure.SentinelOrErased;
            return false;
        }
        if (calibration.NeutralX < calibration.NegativeRangeX ||
            calibration.NeutralY < calibration.NegativeRangeY)
        {
            failure = Switch2CalibrationAdoptionFailure.
                NegativeEndpointOutOfRange;
            return false;
        }
        if ((int)calibration.NeutralX + calibration.PositiveRangeX > 0x0FFF ||
            (int)calibration.NeutralY + calibration.PositiveRangeY > 0x0FFF)
        {
            failure = Switch2CalibrationAdoptionFailure.
                PositiveEndpointOutOfRange;
            return false;
        }

        failure = Switch2CalibrationAdoptionFailure.None;
        return true;
    }

    private static ushort DecodePacked12(byte first, byte second,
        bool lowNibble) => lowNibble ?
        (ushort)(first | ((second & 0x0F) << 8)) :
        (ushort)((first >> 4) | (second << 4));
}
