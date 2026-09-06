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

[Flags]
public enum Switch2VirtualOutputFlags : byte
{
    None = 0,
    Rumble = 0x01,
    PlayerLed = 0x02,
}

public enum Switch2VirtualOutputDecodeFailure : byte
{
    None = 0,
    InvalidLength,
    UnknownFlags,
    UnexpectedRumblePayload,
    InvalidRumbleGroup,
    CounterMismatch,
    UnexpectedPlayerLedMask,
}

/// <summary>
/// Strict, lossless interpretation of VIIPER's 34-byte ns2pro output state.
/// Decoding proves only the transport envelope and packed fields. It does not
/// assign oscillator frequency, actuator side, cadence, or stop semantics.
/// </summary>
public readonly struct Switch2VirtualOutputState
{
    public const int WireLength = 34;
    public const int LeftRumbleOffset = 0;
    public const int RightRumbleOffset = 16;
    public const int FlagsOffset = 32;
    public const int PlayerLedMaskOffset = 33;
    private const Switch2VirtualOutputFlags KnownFlags =
        Switch2VirtualOutputFlags.Rumble |
        Switch2VirtualOutputFlags.PlayerLed;

    private Switch2VirtualOutputState(Switch2VirtualOutputFlags flags,
        byte playerLedMask, byte rumbleCounter,
        Switch2HdRumbleGroup leftRumble,
        Switch2HdRumbleGroup rightRumble)
    {
        Flags = flags;
        PlayerLedMask = playerLedMask;
        RumbleCounter = rumbleCounter;
        LeftRumble = leftRumble;
        RightRumble = rightRumble;
    }

    public Switch2VirtualOutputFlags Flags { get; }
    public byte PlayerLedMask { get; }
    public byte RumbleCounter { get; }
    public Switch2HdRumbleGroup LeftRumble { get; }
    public Switch2HdRumbleGroup RightRumble { get; }
    public bool HasRumble => (Flags & Switch2VirtualOutputFlags.Rumble) != 0;
    public bool HasPlayerLed =>
        (Flags & Switch2VirtualOutputFlags.PlayerLed) != 0;

    public static bool TryDecode(ReadOnlySpan<byte> source,
        out Switch2VirtualOutputState output,
        out Switch2VirtualOutputDecodeFailure failure)
    {
        output = default;
        if (source.Length != WireLength)
        {
            failure = Switch2VirtualOutputDecodeFailure.InvalidLength;
            return false;
        }

        var flags = (Switch2VirtualOutputFlags)source[FlagsOffset];
        if ((flags & ~KnownFlags) != 0)
        {
            failure = Switch2VirtualOutputDecodeFailure.UnknownFlags;
            return false;
        }

        byte rumbleCounter = 0;
        Switch2HdRumbleGroup left = default;
        Switch2HdRumbleGroup right = default;
        if ((flags & Switch2VirtualOutputFlags.Rumble) != 0)
        {
            if (!Switch2HdRumbleGroupCodec.TryDecode(source.Slice(
                    LeftRumbleOffset,
                    Switch2HdRumbleGroupCodec.GroupLength),
                    out byte leftCounter, out left) ||
                !Switch2HdRumbleGroupCodec.TryDecode(source.Slice(
                    RightRumbleOffset,
                    Switch2HdRumbleGroupCodec.GroupLength),
                    out byte rightCounter, out right))
            {
                failure =
                    Switch2VirtualOutputDecodeFailure.InvalidRumbleGroup;
                return false;
            }
            if (leftCounter != rightCounter)
            {
                failure = Switch2VirtualOutputDecodeFailure.CounterMismatch;
                return false;
            }
            rumbleCounter = leftCounter;
        }
        else if (!AllZero(source.Slice(LeftRumbleOffset, 32)))
        {
            failure =
                Switch2VirtualOutputDecodeFailure.UnexpectedRumblePayload;
            return false;
        }

        byte ledMask = source[PlayerLedMaskOffset];
        if ((flags & Switch2VirtualOutputFlags.PlayerLed) == 0 &&
            ledMask != 0)
        {
            failure =
                Switch2VirtualOutputDecodeFailure.UnexpectedPlayerLedMask;
            return false;
        }

        output = new Switch2VirtualOutputState(flags, ledMask,
            rumbleCounter, left, right);
        failure = Switch2VirtualOutputDecodeFailure.None;
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
