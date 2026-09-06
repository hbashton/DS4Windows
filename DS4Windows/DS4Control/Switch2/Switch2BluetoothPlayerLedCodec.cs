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

public enum Switch2BluetoothPlayerLedCodecFailure : byte
{
    None = 0,
    InvalidPlayerNumber,
    InvalidLength,
    InvalidCommand,
    InvalidStatus,
}

/// <summary>
/// Closed encoder for the acknowledged BLE player-LED command. This is a
/// direct GPL-compatible adaptation of Switch2Connect commit
/// 4487322a306f04efa27682e3f3a508635a84fd98, src/controller.py lines
/// 1166-1171, 1198-1201, 2666-2682 and 3904-3909. It deliberately exposes no
/// arbitrary command, subcommand, payload, memory, or firmware write surface.
/// </summary>
public static class Switch2BluetoothPlayerLedCodec
{
    public static readonly Guid CommandWriteCharacteristicUuid =
        Switch2BluetoothAssociationCodec.CommandWriteCharacteristicUuid;

    public static readonly Guid CommandResponseCharacteristicUuid =
        Switch2BluetoothAssociationCodec.CommandResponseCharacteristicUuid;

    public const byte CommandId = 0x09;
    public const byte SetPlayerSubcommandId = 0x07;
    public const int RequestLength = 12;
    public const int MinimumResponseLength = 8;

    public static bool TryWriteRequest(byte playerNumber,
        Span<byte> destination,
        out Switch2BluetoothPlayerLedCodecFailure failure)
    {
        if (!TryGetPattern(playerNumber, out byte pattern))
        {
            failure = Switch2BluetoothPlayerLedCodecFailure.
                InvalidPlayerNumber;
            return false;
        }
        return TryWritePatternRequest(pattern, destination, out failure);
    }

    /// <summary>
    /// Writes the exact four-segment mask authored by a native virtual
    /// controller. This remains a closed LED-only operation: bits outside the
    /// four physical player indicators are rejected.
    /// </summary>
    public static bool TryWritePatternRequest(byte pattern,
        Span<byte> destination,
        out Switch2BluetoothPlayerLedCodecFailure failure)
    {
        if ((pattern & 0xF0) != 0)
        {
            failure = Switch2BluetoothPlayerLedCodecFailure.
                InvalidPlayerNumber;
            return false;
        }
        if (destination.Length != RequestLength)
        {
            failure = Switch2BluetoothPlayerLedCodecFailure.InvalidLength;
            return false;
        }

        destination.Clear();
        destination[0] = CommandId;
        destination[1] = 0x91;
        destination[2] = 0x01;
        destination[3] = SetPlayerSubcommandId;
        destination[5] = 0x04;
        destination[8] = pattern;
        failure = Switch2BluetoothPlayerLedCodecFailure.None;
        return true;
    }

    /// <summary>
    /// The donor protocol establishes response length, command identity, and
    /// status. It does not establish a stable subcommand echo in the remaining
    /// header bytes, so validation must not invent one.
    /// </summary>
    public static bool TryValidateResponse(ReadOnlySpan<byte> response,
        out Switch2BluetoothPlayerLedCodecFailure failure)
    {
        if (response.Length < MinimumResponseLength)
        {
            failure = Switch2BluetoothPlayerLedCodecFailure.InvalidLength;
            return false;
        }
        if (response[0] != CommandId)
        {
            failure = Switch2BluetoothPlayerLedCodecFailure.InvalidCommand;
            return false;
        }
        if (response[1] != 0x01)
        {
            failure = Switch2BluetoothPlayerLedCodecFailure.InvalidStatus;
            return false;
        }

        failure = Switch2BluetoothPlayerLedCodecFailure.None;
        return true;
    }

    public static bool TryGetPattern(byte playerNumber, out byte pattern)
    {
        pattern = playerNumber switch
        {
            1 => 0x01,
            2 => 0x03,
            3 => 0x07,
            4 => 0x0F,
            5 => 0x09,
            6 => 0x05,
            7 => 0x0D,
            8 => 0x06,
            _ => 0x00,
        };
        return pattern != 0;
    }

    /// <summary>
    /// Resolves only the eight patterns established by the controller
    /// protocol. An arbitrary virtual-device bit mask is not approximated to
    /// another player's indication.
    /// </summary>
    public static bool TryGetPlayerNumber(byte pattern,
        out byte playerNumber)
    {
        playerNumber = pattern switch
        {
            0x01 => 1,
            0x03 => 2,
            0x07 => 3,
            0x0F => 4,
            0x09 => 5,
            0x05 => 6,
            0x0D => 7,
            0x06 => 8,
            _ => 0,
        };
        return playerNumber != 0;
    }
}
