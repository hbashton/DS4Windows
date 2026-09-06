/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System.Text.Json.Serialization;
using DS4Windows.Switch2;

namespace DS4Windows;

/// <summary>
/// Versioned, out-of-band VIIPER power snapshot for a virtual Switch 2 Pro
/// controller. It must never be appended to the fixed 24-byte input packet.
/// </summary>
internal readonly struct ViiperSwitch2RuntimeStatusV1
{
    internal const ushort ContractVersion = 1;

    private ViiperSwitch2RuntimeStatusV1(byte batteryLevel,
        ushort batteryVolts, bool charging, bool externalPower)
    {
        Version = ContractVersion;
        BatteryLevel = batteryLevel;
        Charging = charging;
        ExternalPower = externalPower;
        BatteryVolts = batteryVolts;
    }

    [JsonPropertyName("version")]
    public ushort Version { get; }

    [JsonPropertyName("batteryLevel")]
    public byte BatteryLevel { get; }

    [JsonPropertyName("charging")]
    public bool Charging { get; }

    [JsonPropertyName("externalPower")]
    public bool ExternalPower { get; }

    [JsonPropertyName("batteryVolts")]
    public ushort BatteryVolts { get; }

    [JsonIgnore]
    public bool IsValid => Version == ContractVersion &&
        BatteryLevel <= 9 && BatteryVolts >=
            Switch2BatteryStatus.MinimumValidMillivolts &&
        BatteryVolts <= Switch2BatteryStatus.MaximumValidMillivolts;

    internal static bool TryCreate(Switch2RuntimeInputDevice source,
        out ViiperSwitch2RuntimeStatusV1 status)
    {
        if (source == null)
        {
            status = default;
            return false;
        }

        return TryCreate(source.Switch2BatteryStatus,
            source.ConnectionType, out status);
    }

    internal static bool TryCreate(in Switch2BatteryStatus battery,
        ConnectionType connectionType,
        out ViiperSwitch2RuntimeStatusV1 status)
    {
        if (!battery.IsValid)
        {
            status = default;
            return false;
        }

        byte level = battery.Band switch
        {
            Switch2BatteryBand.Low => 1,
            Switch2BatteryBand.Medium => 5,
            Switch2BatteryBand.High => 9,
            _ => 0,
        };
        if (level == 0)
        {
            status = default;
            return false;
        }

        // Current direction and charge-state meaning remain unproven in the
        // pinned physical report. USB transport does establish external power;
        // no charging state is fabricated from the opaque current field.
        status = new ViiperSwitch2RuntimeStatusV1(level,
            battery.VoltageMillivolts, charging: false,
            externalPower: connectionType == ConnectionType.USB);
        return true;
    }

    internal ViiperSwitch2CreationMetadata ToCreationMetadata() => new()
    {
        BatteryLevel = BatteryLevel,
        Charging = Charging,
        ExternalPower = ExternalPower,
        BatteryVolts = BatteryVolts,
    };
}

internal sealed class ViiperSwitch2CreationMetadata
{
    [JsonPropertyName("battery_level")]
    public byte BatteryLevel { get; set; }

    [JsonPropertyName("charging")]
    public bool Charging { get; set; }

    [JsonPropertyName("external_power")]
    public bool ExternalPower { get; set; }

    [JsonPropertyName("battery_volts")]
    public ushort BatteryVolts { get; set; }
}
