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
/// One lossless five-byte Switch 2 HD-rumble subframe. The two oscillator
/// indices deliberately avoid assigning physical high/low-band semantics;
/// that ordering remains a hardware basis-test gate.
/// </summary>
public readonly struct Switch2HdRumbleSubframe :
    IEquatable<Switch2HdRumbleSubframe>
{
    public const ushort MaximumCode = 0x03FF;

    public Switch2HdRumbleSubframe(ushort oscillator0ControlCode,
        ushort oscillator0AmplitudeCode, ushort oscillator1ControlCode,
        ushort oscillator1AmplitudeCode)
    {
        ThrowIfOutOfRange(oscillator0ControlCode,
            nameof(oscillator0ControlCode));
        ThrowIfOutOfRange(oscillator0AmplitudeCode,
            nameof(oscillator0AmplitudeCode));
        ThrowIfOutOfRange(oscillator1ControlCode,
            nameof(oscillator1ControlCode));
        ThrowIfOutOfRange(oscillator1AmplitudeCode,
            nameof(oscillator1AmplitudeCode));

        Oscillator0ControlCode = oscillator0ControlCode;
        Oscillator0AmplitudeCode = oscillator0AmplitudeCode;
        Oscillator1ControlCode = oscillator1ControlCode;
        Oscillator1AmplitudeCode = oscillator1AmplitudeCode;
    }

    /// <summary>
    /// Raw first 10-bit control field. Licensed SDL treats all ten bits as a
    /// frequency code; capture-backed research splits bit 9 as a tone flag.
    /// </summary>
    public ushort Oscillator0ControlCode { get; }

    public ushort Oscillator0AmplitudeCode { get; }

    /// <summary>
    /// Raw second 10-bit control field, with the same unresolved 9+1 versus
    /// 10-bit interpretation as <see cref="Oscillator0ControlCode"/>.
    /// </summary>
    public ushort Oscillator1ControlCode { get; }

    public ushort Oscillator1AmplitudeCode { get; }

    public bool HasNonzeroAmplitude => Oscillator0AmplitudeCode != 0 ||
        Oscillator1AmplitudeCode != 0;

    public bool Equals(Switch2HdRumbleSubframe other) =>
        Oscillator0ControlCode == other.Oscillator0ControlCode &&
        Oscillator0AmplitudeCode == other.Oscillator0AmplitudeCode &&
        Oscillator1ControlCode == other.Oscillator1ControlCode &&
        Oscillator1AmplitudeCode == other.Oscillator1AmplitudeCode;

    public override bool Equals(object obj) =>
        obj is Switch2HdRumbleSubframe other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        Oscillator0ControlCode, Oscillator0AmplitudeCode,
        Oscillator1ControlCode, Oscillator1AmplitudeCode);

    private static void ThrowIfOutOfRange(ushort value, string parameterName)
    {
        if (value > MaximumCode)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"A packed HD-rumble field cannot exceed {MaximumCode}.");
        }
    }
}

/// <summary>
/// Strict allocation-free packing for the corroborated 4 x 10-bit subframe.
/// Transport envelopes, counters, cadence, stop policy, and actuator routing
/// are separate contracts and intentionally absent here.
/// </summary>
public static class Switch2HdRumbleCodec
{
    public const int SubframeLength = 5;

    public static bool TryEncode(in Switch2HdRumbleSubframe subframe,
        Span<byte> destination)
    {
        if (destination.Length != SubframeLength)
        {
            return false;
        }

        ulong packed = subframe.Oscillator0ControlCode |
            ((ulong)subframe.Oscillator0AmplitudeCode << 10) |
            ((ulong)subframe.Oscillator1ControlCode << 20) |
            ((ulong)subframe.Oscillator1AmplitudeCode << 30);
        destination[0] = (byte)packed;
        destination[1] = (byte)(packed >> 8);
        destination[2] = (byte)(packed >> 16);
        destination[3] = (byte)(packed >> 24);
        destination[4] = (byte)(packed >> 32);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source,
        out Switch2HdRumbleSubframe subframe)
    {
        if (source.Length != SubframeLength)
        {
            subframe = default;
            return false;
        }

        ulong packed = source[0] |
            ((ulong)source[1] << 8) |
            ((ulong)source[2] << 16) |
            ((ulong)source[3] << 24) |
            ((ulong)source[4] << 32);
        subframe = new Switch2HdRumbleSubframe(
            (ushort)(packed & Switch2HdRumbleSubframe.MaximumCode),
            (ushort)((packed >> 10) &
                Switch2HdRumbleSubframe.MaximumCode),
            (ushort)((packed >> 20) &
                Switch2HdRumbleSubframe.MaximumCode),
            (ushort)((packed >> 30) &
                Switch2HdRumbleSubframe.MaximumCode));
        return true;
    }
}

public readonly struct Switch2HdRumbleGroup :
    IEquatable<Switch2HdRumbleGroup>
{
    public Switch2HdRumbleGroup(Switch2HdRumbleSubframe first,
        Switch2HdRumbleSubframe second, Switch2HdRumbleSubframe third)
    {
        First = first;
        Second = second;
        Third = third;
    }

    public Switch2HdRumbleSubframe First { get; }

    public Switch2HdRumbleSubframe Second { get; }

    public Switch2HdRumbleSubframe Third { get; }

    public bool Equals(Switch2HdRumbleGroup other) =>
        First.Equals(other.First) && Second.Equals(other.Second) &&
        Third.Equals(other.Third);

    public override bool Equals(object obj) =>
        obj is Switch2HdRumbleGroup other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(First, Second, Third);
}

/// <summary>
/// One corroborated 16-byte actuator group: a modulo-16 header followed by
/// three explicit five-byte subframes. This codec does not choose cadence or
/// assign a physical side.
/// </summary>
public static class Switch2HdRumbleGroupCodec
{
    public const int GroupLength = 16;
    public const byte HeaderBase = 0x50;

    public static bool TryEncode(byte counter,
        in Switch2HdRumbleGroup group, Span<byte> destination)
    {
        if (counter > 0x0F || destination.Length != GroupLength)
        {
            return false;
        }

        destination.Clear();
        destination[0] = (byte)(HeaderBase | counter);
        return Switch2HdRumbleCodec.TryEncode(group.First,
                destination.Slice(1, Switch2HdRumbleCodec.SubframeLength)) &&
            Switch2HdRumbleCodec.TryEncode(group.Second,
                destination.Slice(6, Switch2HdRumbleCodec.SubframeLength)) &&
            Switch2HdRumbleCodec.TryEncode(group.Third,
                destination.Slice(11, Switch2HdRumbleCodec.SubframeLength));
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out byte counter,
        out Switch2HdRumbleGroup group)
    {
        if (source.Length != GroupLength ||
            (source[0] & 0xF0) != HeaderBase ||
            !Switch2HdRumbleCodec.TryDecode(
                source.Slice(1, Switch2HdRumbleCodec.SubframeLength),
                out var first) ||
            !Switch2HdRumbleCodec.TryDecode(
                source.Slice(6, Switch2HdRumbleCodec.SubframeLength),
                out var second) ||
            !Switch2HdRumbleCodec.TryDecode(
                source.Slice(11, Switch2HdRumbleCodec.SubframeLength),
                out var third))
        {
            counter = 0;
            group = default;
            return false;
        }

        counter = (byte)(source[0] & 0x0F);
        group = new Switch2HdRumbleGroup(first, second, third);
        return true;
    }
}

public enum Switch2BluetoothHdRumbleDecodeFailure : byte
{
    None = 0,
    InvalidLength = 1,
    InvalidEnvelope = 2,
    InvalidGroupHeader = 3,
    CounterMismatch = 4,
}

/// <summary>
/// Strict Switch 2 BLE vibration-characteristic envelopes. The corroborated
/// transport payload is one zero envelope byte followed by one actuator group
/// for a Joy-Con, or independent left/right groups for a Pro Controller. This
/// codec owns neither GATT discovery/write policy nor cadence.
/// </summary>
public static class Switch2BluetoothHdRumbleCodec
{
    public const int JoyConPayloadLength = 1 +
        Switch2HdRumbleGroupCodec.GroupLength;
    public const int ProControllerPayloadLength = 1 +
        2 * Switch2HdRumbleGroupCodec.GroupLength;
    public const byte Envelope = 0x00;

    private const int FirstGroupOffset = 1;
    private const int SecondGroupOffset = 1 +
        Switch2HdRumbleGroupCodec.GroupLength;

    public static bool TryEncodeJoyCon(byte counter,
        in Switch2HdRumbleGroup group, Span<byte> destination)
    {
        if (destination.Length != JoyConPayloadLength)
        {
            return false;
        }

        destination.Clear();
        if (!Switch2HdRumbleGroupCodec.TryEncode(counter, group,
                destination.Slice(FirstGroupOffset,
                    Switch2HdRumbleGroupCodec.GroupLength)))
        {
            destination.Clear();
            return false;
        }
        destination[0] = Envelope;
        return true;
    }

    public static bool TryEncodeProController(byte counter,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        Span<byte> destination)
    {
        if (destination.Length != ProControllerPayloadLength)
        {
            return false;
        }

        destination.Clear();
        if (!Switch2HdRumbleGroupCodec.TryEncode(counter, left,
                destination.Slice(FirstGroupOffset,
                    Switch2HdRumbleGroupCodec.GroupLength)) ||
            !Switch2HdRumbleGroupCodec.TryEncode(counter, right,
                destination.Slice(SecondGroupOffset,
                    Switch2HdRumbleGroupCodec.GroupLength)))
        {
            destination.Clear();
            return false;
        }
        destination[0] = Envelope;
        return true;
    }

    public static bool TryDecodeJoyCon(ReadOnlySpan<byte> source,
        out byte counter, out Switch2HdRumbleGroup group,
        out Switch2BluetoothHdRumbleDecodeFailure failure)
    {
        if (!ValidateEnvelope(source, JoyConPayloadLength, out failure))
        {
            counter = 0;
            group = default;
            return false;
        }
        if (!Switch2HdRumbleGroupCodec.TryDecode(source.Slice(
                FirstGroupOffset, Switch2HdRumbleGroupCodec.GroupLength),
                out counter, out group))
        {
            failure = Switch2BluetoothHdRumbleDecodeFailure.
                InvalidGroupHeader;
            return false;
        }
        return true;
    }

    public static bool TryDecodeProController(ReadOnlySpan<byte> source,
        out byte counter, out Switch2HdRumbleGroup left,
        out Switch2HdRumbleGroup right,
        out Switch2BluetoothHdRumbleDecodeFailure failure)
    {
        if (!ValidateEnvelope(source, ProControllerPayloadLength,
                out failure))
        {
            counter = 0;
            left = default;
            right = default;
            return false;
        }
        if (!Switch2HdRumbleGroupCodec.TryDecode(source.Slice(
                FirstGroupOffset, Switch2HdRumbleGroupCodec.GroupLength),
                out byte leftCounter, out left) ||
            !Switch2HdRumbleGroupCodec.TryDecode(source.Slice(
                SecondGroupOffset, Switch2HdRumbleGroupCodec.GroupLength),
                out byte rightCounter, out right))
        {
            counter = 0;
            left = default;
            right = default;
            failure = Switch2BluetoothHdRumbleDecodeFailure.
                InvalidGroupHeader;
            return false;
        }
        if (leftCounter != rightCounter)
        {
            counter = 0;
            left = default;
            right = default;
            failure = Switch2BluetoothHdRumbleDecodeFailure.CounterMismatch;
            return false;
        }

        counter = leftCounter;
        return true;
    }

    private static bool ValidateEnvelope(ReadOnlySpan<byte> source,
        int expectedLength,
        out Switch2BluetoothHdRumbleDecodeFailure failure)
    {
        if (source.Length != expectedLength)
        {
            failure = Switch2BluetoothHdRumbleDecodeFailure.InvalidLength;
            return false;
        }
        if (source[0] != Envelope)
        {
            failure = Switch2BluetoothHdRumbleDecodeFailure.InvalidEnvelope;
            return false;
        }
        failure = Switch2BluetoothHdRumbleDecodeFailure.None;
        return true;
    }
}

public enum Switch2UsbHdRumbleDecodeFailure : byte
{
    None = 0,
    InvalidLength = 1,
    InvalidReportId = 2,
    InvalidGroupHeader = 3,
    CounterMismatch = 4,
    NonzeroReservedTail = 5,
}

/// <summary>
/// Strict 64-byte USB reports. The Pro form carries independent left/right
/// groups under one counter; the Joy-Con form carries one group. BLE envelopes
/// are intentionally absent.
/// </summary>
public static class Switch2UsbHdRumbleCodec
{
    public const int ReportLength = 64;
    public const byte JoyConReportId = 0x01;
    public const byte ProControllerReportId = 0x02;
    private const int FirstGroupOffset = 1;
    private const int SecondGroupOffset = 17;
    private const int ReservedTailOffset = 33;

    public static bool TryEncodeJoyCon(byte counter,
        in Switch2HdRumbleGroup group, Span<byte> destination)
    {
        if (destination.Length != ReportLength)
        {
            return false;
        }

        destination.Clear();
        destination[0] = JoyConReportId;
        if (Switch2HdRumbleGroupCodec.TryEncode(counter, group,
                destination.Slice(FirstGroupOffset,
                    Switch2HdRumbleGroupCodec.GroupLength)))
        {
            return true;
        }
        destination.Clear();
        return false;
    }

    public static bool TryEncodeProController(byte counter,
        in Switch2HdRumbleGroup left, in Switch2HdRumbleGroup right,
        Span<byte> destination)
    {
        if (destination.Length != ReportLength)
        {
            return false;
        }

        destination.Clear();
        destination[0] = ProControllerReportId;
        if (Switch2HdRumbleGroupCodec.TryEncode(counter, left,
                destination.Slice(FirstGroupOffset,
                    Switch2HdRumbleGroupCodec.GroupLength)) &&
            Switch2HdRumbleGroupCodec.TryEncode(counter, right,
                destination.Slice(SecondGroupOffset,
                    Switch2HdRumbleGroupCodec.GroupLength)))
        {
            return true;
        }
        destination.Clear();
        return false;
    }

    /// <summary>
    /// Builds upstream SDL's one-subframe compatibility form, including Pro's
    /// six-byte left-to-right mirror and zeroed unused slots.
    /// </summary>
    public static bool TryEncodeSdlCompatibility(byte reportId, byte counter,
        in Switch2HdRumbleSubframe subframe, Span<byte> destination)
    {
        if (reportId is not (JoyConReportId or ProControllerReportId))
        {
            return false;
        }

        var group = new Switch2HdRumbleGroup(subframe, default, default);
        return reportId == JoyConReportId ?
            TryEncodeJoyCon(counter, group, destination) :
            TryEncodeProController(counter, group, group, destination);
    }

    public static bool TryDecodeJoyCon(ReadOnlySpan<byte> source,
        out byte counter, out Switch2HdRumbleGroup group,
        out Switch2UsbHdRumbleDecodeFailure failure)
    {
        if (!ValidateEnvelope(source, JoyConReportId, out failure))
        {
            counter = 0;
            group = default;
            return false;
        }
        if (!Switch2HdRumbleGroupCodec.TryDecode(source.Slice(
                FirstGroupOffset, Switch2HdRumbleGroupCodec.GroupLength),
                out counter, out group))
        {
            failure = Switch2UsbHdRumbleDecodeFailure.InvalidGroupHeader;
            return false;
        }
        if (!AllZero(source.Slice(SecondGroupOffset)))
        {
            counter = 0;
            group = default;
            failure = Switch2UsbHdRumbleDecodeFailure.NonzeroReservedTail;
            return false;
        }
        return true;
    }

    public static bool TryDecodeProController(ReadOnlySpan<byte> source,
        out byte counter, out Switch2HdRumbleGroup left,
        out Switch2HdRumbleGroup right,
        out Switch2UsbHdRumbleDecodeFailure failure)
    {
        if (!ValidateEnvelope(source, ProControllerReportId, out failure))
        {
            counter = 0;
            left = default;
            right = default;
            return false;
        }
        if (!Switch2HdRumbleGroupCodec.TryDecode(source.Slice(
                FirstGroupOffset, Switch2HdRumbleGroupCodec.GroupLength),
                out byte leftCounter, out left) ||
            !Switch2HdRumbleGroupCodec.TryDecode(source.Slice(
                SecondGroupOffset, Switch2HdRumbleGroupCodec.GroupLength),
                out byte rightCounter, out right))
        {
            counter = 0;
            left = default;
            right = default;
            failure = Switch2UsbHdRumbleDecodeFailure.InvalidGroupHeader;
            return false;
        }
        if (leftCounter != rightCounter)
        {
            counter = 0;
            left = default;
            right = default;
            failure = Switch2UsbHdRumbleDecodeFailure.CounterMismatch;
            return false;
        }
        if (!AllZero(source.Slice(ReservedTailOffset)))
        {
            counter = 0;
            left = default;
            right = default;
            failure = Switch2UsbHdRumbleDecodeFailure.NonzeroReservedTail;
            return false;
        }

        counter = leftCounter;
        return true;
    }

    private static bool ValidateEnvelope(ReadOnlySpan<byte> source,
        byte expectedReportId, out Switch2UsbHdRumbleDecodeFailure failure)
    {
        if (source.Length != ReportLength)
        {
            failure = Switch2UsbHdRumbleDecodeFailure.InvalidLength;
            return false;
        }
        if (source[0] != expectedReportId)
        {
            failure = Switch2UsbHdRumbleDecodeFailure.InvalidReportId;
            return false;
        }
        failure = Switch2UsbHdRumbleDecodeFailure.None;
        return true;
    }

    private static bool AllZero(ReadOnlySpan<byte> source)
    {
        foreach (byte value in source)
        {
            if (value != 0)
            {
                return false;
            }
        }
        return true;
    }
}
