/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows;

/// <summary>
/// One mapping-owned stick coordinate, distinct from immutable physical
/// metadata. Fractional 0..255 coordinates retain the existing profile's
/// center/ranges without restricting the input to 256 positions. Default is
/// neutral. Legacy writes deliberately replace precision, even when their
/// byte happens to equal the previous compatibility projection.
/// </summary>
internal readonly struct DS4MappedStickAxis : IEquatable<DS4MappedStickAxis>
{
    private readonly double offset;
    private readonly short legacyOffset;

    private DS4MappedStickAxis(double offset, short legacyOffset, bool precise)
    {
        this.offset = offset;
        this.legacyOffset = legacyOffset;
        IsHighResolution = precise;
    }

    internal double ProfileCoordinate => 128.0 + offset;
    internal byte LegacyValue => (byte)(128 + legacyOffset);
    internal bool IsHighResolution { get; }

    internal static DS4MappedStickAxis FromLegacy(byte value) =>
        new(value - 128, (short)(value - 128), false);

    internal static DS4MappedStickAxis FromSigned(short value)
    {
        double offset = value < 0 ? value * (128.0 / 32768.0) : value * (127.0 / 32767.0);
        return new(offset, QuantizeLegacyOffset(offset), true);
    }

    internal static bool TryFromProfileCoordinate(double value, out DS4MappedStickAxis axis)
    {
        axis = default;
        if (!double.IsFinite(value) || value < 0.0 || value > 255.0)
            return false;
        double offset = value - 128.0;
        axis = new(offset, QuantizeLegacyOffset(offset), true);
        return true;
    }

    // These operations use mapping-owned values, never immutable raw sidecars.
    internal DS4MappedStickAxis MapDirection(bool sourcePositive, bool destinationPositive)
    {
        if (sourcePositive ? offset <= 0.0 : offset >= 0.0)
            return new DS4MappedStickAxis(0, 0, IsHighResolution);
        if (sourcePositive == destinationPositive)
            return this;
        if (!IsHighResolution)
            return FromLegacy((byte)(255 - LegacyValue));

        // A precise reversal reflects signed magnitude, not the byte mirror
        // around 127.5 (which would leave small negative motion negative).
        double reversed = offset < 0.0 ? -offset * (127.0 / 128.0) :
            -offset * (128.0 / 127.0);
        TryFromProfileCoordinate(Math.Clamp(128.0 + reversed, 0.0, 255.0), out var result);
        return result;
    }

    internal static DS4MappedStickAxis SelectStronger(in DS4MappedStickAxis current,
        in DS4MappedStickAxis candidate) => candidate.offset != 0.0 &&
        Math.Abs(candidate.offset) > Math.Abs(current.offset) ? candidate : current;

    internal short ToSigned16(bool inverted = false)
    {
        double unit = offset < 0 ? offset / 128.0 : offset / 127.0;
        if (inverted) unit = -unit;
        return (short)Math.Round(unit * (unit < 0 ? 32768.0 : 32767.0), MidpointRounding.AwayFromZero);
    }

    internal ushort ToUnsigned12(bool inverted = false)
    {
        double unit = offset < 0 ? offset / 128.0 : offset / 127.0;
        if (inverted) unit = -unit;
        return (ushort)(2048 + (int)Math.Round(unit * (unit < 0 ? 2048.0 : 2047.0), MidpointRounding.AwayFromZero));
    }

    private static short QuantizeLegacyOffset(double value) =>
        (short)Math.Round(value, MidpointRounding.AwayFromZero);

    public bool Equals(DS4MappedStickAxis other) =>
        offset.Equals(other.offset) && legacyOffset == other.legacyOffset && IsHighResolution == other.IsHighResolution;

    public override bool Equals(object obj) => obj is DS4MappedStickAxis other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(offset, legacyOffset, IsHighResolution);
}
