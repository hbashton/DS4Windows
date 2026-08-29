using System;

namespace DS4Windows.Switch2;

public enum Switch2StickSide : byte
{
    Left = 1,
    Right = 2,
}

public readonly struct Switch2FactoryCalibrationMetadata
{
    internal Switch2FactoryCalibrationMetadata(Switch2StickSide side,
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

public readonly struct Switch2StickCalibration
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

    private static bool IsInterior(ushort value) => value is > 0 and < 0x0FFF;
}

/// <summary>
/// Pure parsing and metadata for factory stick calibration. User calibration is
/// intentionally unavailable because current sources conflict on the right-stick
/// user address (0x1FC060 versus 0x1FC080).
/// </summary>
public static class Switch2CalibrationCodec
{
    public const int StickCalibrationLength = 9;
    public const uint LeftFactoryStickAddress = 0x0130A8;
    public const uint RightFactoryStickAddress = 0x0130E8;

    public static bool SupportsLiveUserCalibration => false;

    public static bool TryGetFactoryStickMetadata(
        Switch2ControllerModel model, Switch2StickSide side,
        out Switch2FactoryCalibrationMetadata metadata)
    {
        bool applicable = (model, side) switch
        {
            (Switch2ControllerModel.JoyCon2Left, Switch2StickSide.Left) => true,
            (Switch2ControllerModel.JoyCon2Right, Switch2StickSide.Right) => true,
            (Switch2ControllerModel.ProController2, Switch2StickSide.Left) => true,
            (Switch2ControllerModel.ProController2, Switch2StickSide.Right) => true,
            _ => false,
        };
        if (!applicable)
        {
            metadata = default;
            return false;
        }

        uint address = side == Switch2StickSide.Left ?
            LeftFactoryStickAddress : RightFactoryStickAddress;
        metadata = new Switch2FactoryCalibrationMetadata(side, address,
            StickCalibrationLength);
        return true;
    }

    /// <summary>
    /// Always returns false in Phase 1. This explicit API prevents a caller from
    /// silently choosing one of the conflicting user-calibration addresses.
    /// </summary>
    public static bool TryGetLiveUserStickAddress(
        Switch2ControllerModel model, Switch2StickSide side, out uint address)
    {
        address = 0;
        return false;
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

    private static ushort DecodePacked12(byte first, byte second,
        bool lowNibble) => lowNibble ?
        (ushort)(first | ((second & 0x0F) << 8)) :
        (ushort)((first >> 4) | (second << 4));
}
