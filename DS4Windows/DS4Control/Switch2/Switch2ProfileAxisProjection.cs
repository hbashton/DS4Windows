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
/// Shared, allocation-free Switch 2 stick projection. The caller selects axis
/// and inversion; this type owns the single calibration and legacy-quantization
/// implementation used by Pro Controller 2 and Joy-Con 2 profile boundaries.
/// </summary>
internal static class Switch2ProfileAxisProjection
{
    internal static bool TryMap(in Switch2CalibratedStickPosition stick,
        bool useY, bool invert, out Switch2ProfileAxis axis) =>
        TryMap(useY ? stick.Raw.Y : stick.Raw.X,
            useY ? stick.OffsetY : stick.OffsetX,
            useY ? stick.NegativeRangeY : stick.NegativeRangeX,
            useY ? stick.PositiveRangeY : stick.PositiveRangeX, invert, out axis);

    internal static bool TryMap(ushort rawValue, int offset,
        ushort negativeRange, ushort positiveRange, bool invert,
        out Switch2ProfileAxis axis)
    {
        if (!TryMapSigned(rawValue, offset, negativeRange, positiveRange,
                invert, out short signed))
        {
            axis = default;
            return false;
        }

        axis = new Switch2ProfileAxis(rawValue, signed,
            QuantizeLegacy(signed));
        return true;
    }

    internal static bool TryMapSigned(ushort rawValue, int offset,
        ushort negativeRange, ushort positiveRange, bool invert,
        out short signed)
    {
        if (rawValue > 0x0FFF || negativeRange == 0 || positiveRange == 0)
        {
            signed = default;
            return false;
        }

        int signedValue = offset < 0 ?
            -ScaleMagnitude(-(long)offset, negativeRange, 32768) :
            ScaleMagnitude(offset, positiveRange, 32767);
        if (invert)
        {
            signedValue = InvertSignedAxis(signedValue);
        }

        signed = (short)signedValue;
        return true;
    }

    internal static byte QuantizeLegacy(short value) =>
        QuantizeLegacy((int)value);

    private static int ScaleMagnitude(long magnitude, int range, int maximum)
    {
        long clamped = Math.Min(magnitude, range);
        return (int)((clamped * maximum + range / 2L) / range);
    }

    private static int InvertSignedAxis(int value)
    {
        if (value > 0)
        {
            return -ScaleMagnitude(value, 32767, 32768);
        }
        if (value < 0)
        {
            return ScaleMagnitude(-(long)value, 32768, 32767);
        }
        return 0;
    }

    private static byte QuantizeLegacy(int value)
    {
        if (value < 0)
        {
            int magnitude = ScaleMagnitude(-(long)value, 32768, 128);
            return (byte)(128 - magnitude);
        }
        int positive = ScaleMagnitude(value, 32767, 127);
        return (byte)(128 + positive);
    }
}
