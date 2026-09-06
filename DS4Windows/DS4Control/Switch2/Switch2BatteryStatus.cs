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
/// The three battery bands established by Switch2Connect's GPL-3.0-or-later
/// input implementation. They are deliberately not presented as an exact
/// state-of-charge estimate.
/// </summary>
public enum Switch2BatteryBand : byte
{
    Unknown = 0,
    Low,
    Medium,
    High,
}

/// <summary>
/// Validated raw Switch 2 power telemetry plus the conservative compatibility
/// projection needed by DS4Windows' legacy percent-only controller UI.
/// Validation and band thresholds are adapted from
/// TommyWabg/Switch2Connect@4487322a306f04efa27682e3f3a508635a84fd98,
/// <c>src/controller.py</c>, under GPL-3.0-or-later compatibility.
/// </summary>
public readonly struct Switch2BatteryStatus :
    IEquatable<Switch2BatteryStatus>
{
    public const ushort MinimumValidMillivolts = 2500;
    public const ushort MaximumValidMillivolts = 5000;
    public const ushort MediumThresholdMillivolts = 3125;
    public const ushort HighThresholdMillivolts = 3250;

    // DS4Windows has no categorical battery API. These stable display values
    // preserve low-battery behavior and avoid claiming that the high band is
    // fully charged. They are UI compatibility markers, not chemistry/SOC.
    public const byte LowCompatibilityPercentage = 10;
    public const byte MediumCompatibilityPercentage = 50;
    public const byte HighCompatibilityPercentage = 90;

    private Switch2BatteryStatus(ushort voltageMillivolts,
        ushort currentRaw, byte opaque23Raw, Switch2BatteryBand band,
        byte compatibilityPercentage)
    {
        IsValid = true;
        VoltageMillivolts = voltageMillivolts;
        CurrentRaw = currentRaw;
        Opaque23Raw = opaque23Raw;
        Band = band;
        CompatibilityPercentage = compatibilityPercentage;
    }

    public bool IsValid { get; }

    public ushort VoltageMillivolts { get; }

    /// <summary>
    /// Raw little-endian current field. Direction, units, and charging-state
    /// semantics remain unproven and are intentionally not inferred.
    /// </summary>
    public ushort CurrentRaw { get; }

    public byte Opaque23Raw { get; }

    public Switch2BatteryBand Band { get; }

    /// <summary>
    /// A categorical marker for DS4Windows' percent-only compatibility API;
    /// this is not an estimated physical state of charge.
    /// </summary>
    public byte CompatibilityPercentage { get; }

    public static bool TryCreate(in Switch2CommonInputReport report,
        out Switch2BatteryStatus status)
    {
        ushort voltage = report.BatteryVoltageMillivolts;
        if (voltage < MinimumValidMillivolts ||
            voltage > MaximumValidMillivolts)
        {
            status = default;
            return false;
        }

        Switch2BatteryBand band;
        byte percentage;
        if (voltage > HighThresholdMillivolts)
        {
            band = Switch2BatteryBand.High;
            percentage = HighCompatibilityPercentage;
        }
        else if (voltage > MediumThresholdMillivolts)
        {
            band = Switch2BatteryBand.Medium;
            percentage = MediumCompatibilityPercentage;
        }
        else
        {
            band = Switch2BatteryBand.Low;
            percentage = LowCompatibilityPercentage;
        }

        status = new Switch2BatteryStatus(voltage,
            report.BatteryCurrentRaw, report.Opaque23Raw, band, percentage);
        return true;
    }

    public bool Equals(Switch2BatteryStatus other) =>
        IsValid == other.IsValid &&
        VoltageMillivolts == other.VoltageMillivolts &&
        CurrentRaw == other.CurrentRaw &&
        Opaque23Raw == other.Opaque23Raw && Band == other.Band &&
        CompatibilityPercentage == other.CompatibilityPercentage;

    public override bool Equals(object obj) =>
        obj is Switch2BatteryStatus other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(IsValid,
        VoltageMillivolts, CurrentRaw, Opaque23Raw, Band,
        CompatibilityPercentage);
}
