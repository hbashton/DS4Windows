using System;
using System.Buffers.Binary;

namespace DS4Windows.Switch2;

public enum Switch2AdvertisedHost : byte
{
    None = 0,
    ThisHost,
    ForeignHost,
}

/// <summary>
/// Privacy-minimized result for the capture-backed version-one manufacturer
/// advertisement. Remembered host bytes are intentionally not exposed.
/// </summary>
public readonly struct Switch2Advertisement
{
    internal Switch2Advertisement(Switch2ControllerModel model,
        ushort productId, bool isWake, Switch2AdvertisedHost host)
    {
        Model = model;
        ProductId = productId;
        IsWake = isWake;
        Host = host;
    }

    public Switch2ControllerModel Model { get; }

    public ushort ProductId { get; }

    public bool IsWake { get; }

    public Switch2AdvertisedHost Host { get; }

    public bool HasRememberedHost => Host != Switch2AdvertisedHost.None;

    public bool IsReconnect => HasRememberedHost && !IsWake;

    public bool IsForThisHost => Host == Switch2AdvertisedHost.ThisHost;
}

/// <summary>
/// Strict parser for the evidenced 24-byte manufacturer value supplied by BLE
/// APIs after they have separated the two-byte company identifier.
/// </summary>
public static class Switch2AdvertisementCodec
{
    public const ushort NintendoBluetoothCompanyId = 0x0553;
    public const ushort NintendoUsbVendorId = 0x057E;
    public const int ManufacturerValueLength = 24;

    public const ushort JoyCon2RightProductId = 0x2066;
    public const ushort JoyCon2LeftProductId = 0x2067;
    public const ushort ProController2ProductId = 0x2069;

    /// <summary>
    /// The selected host address is in canonical order; the advertisement
    /// stores the same six bytes reversed.
    /// </summary>
    public static bool TryDecode(ushort companyId,
        ReadOnlySpan<byte> manufacturerValue,
        ReadOnlySpan<byte> selectedHostAddress,
        out Switch2Advertisement advertisement)
    {
        if (companyId != NintendoBluetoothCompanyId ||
            manufacturerValue.Length != ManufacturerValueLength ||
            selectedHostAddress.Length != 6 ||
            IsAllZero(selectedHostAddress) ||
            manufacturerValue[0] != 0x01 ||
            manufacturerValue[1] != 0x00 ||
            manufacturerValue[2] != 0x03 ||
            BinaryPrimitives.ReadUInt16LittleEndian(
                manufacturerValue.Slice(3, 2)) != NintendoUsbVendorId ||
            manufacturerValue[7] != 0x00 ||
            manufacturerValue[8] != 0x01 ||
            manufacturerValue[9] is not 0x00 and not 0x81 ||
            manufacturerValue[16] != 0x0F ||
            !IsAllZero(manufacturerValue.Slice(17, 7)))
        {
            advertisement = default;
            return false;
        }

        ushort productId = BinaryPrimitives.ReadUInt16LittleEndian(
            manufacturerValue.Slice(5, 2));
        if (!TryGetModel(productId, out Switch2ControllerModel model))
        {
            advertisement = default;
            return false;
        }

        ReadOnlySpan<byte> advertisedHost = manufacturerValue.Slice(10, 6);
        Switch2AdvertisedHost host = IsAllZero(advertisedHost) ?
            Switch2AdvertisedHost.None :
            EqualsReverse(advertisedHost, selectedHostAddress) ?
                Switch2AdvertisedHost.ThisHost :
                Switch2AdvertisedHost.ForeignHost;
        advertisement = new Switch2Advertisement(model, productId,
            manufacturerValue[9] == 0x81, host);
        return true;
    }

    private static bool TryGetModel(ushort productId,
        out Switch2ControllerModel model)
    {
        model = productId switch
        {
            JoyCon2RightProductId => Switch2ControllerModel.JoyCon2Right,
            JoyCon2LeftProductId => Switch2ControllerModel.JoyCon2Left,
            ProController2ProductId => Switch2ControllerModel.ProController2,
            _ => Switch2ControllerModel.Unknown,
        };
        return model != Switch2ControllerModel.Unknown;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        foreach (byte current in value)
        {
            if (current != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool EqualsReverse(ReadOnlySpan<byte> advertised,
        ReadOnlySpan<byte> hostAddress)
    {
        for (int index = 0; index < advertised.Length; index++)
        {
            if (advertised[index] != hostAddress[hostAddress.Length - 1 - index])
            {
                return false;
            }
        }

        return true;
    }
}
