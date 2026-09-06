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
/// Closed controller-side association steps used by Nintendo's Switch 2 BLE
/// application protocol. This is not Windows Bluetooth SMP pairing.
/// </summary>
public enum Switch2BluetoothAssociationStep : byte
{
    SetHostAddress = 0x01,
    WriteLongTermKeyPart1 = 0x04,
    WriteLongTermKeyPart2 = 0x02,
    Commit = 0x03,
}

public enum Switch2BluetoothAssociationCodecFailure : byte
{
    None = 0,
    InvalidStep,
    InvalidHostAddress,
    InvalidLength,
    InvalidCommand,
    InvalidDirection,
    InvalidStatus,
}

/// <summary>
/// Exact, closed encoder for the four command-0x15 requests used by the
/// Switch2Connect association ceremony. It deliberately exposes neither an
/// arbitrary command writer nor caller-selected key material.
/// </summary>
public static class Switch2BluetoothAssociationCodec
{
    public static readonly Guid ServiceUuid = new(
        "ab7de9be-89fe-49ad-828f-118f09df7fd0");
    public static readonly Guid CommandWriteCharacteristicUuid = new(
        "649d4ac9-8eb7-4e6c-af44-1ea54fe5f005");
    public static readonly Guid CommandResponseCharacteristicUuid = new(
        "c765a961-d9d8-4d36-a20a-5315b111836a");

    public const byte AssociationCommandId = 0x15;
    public const int HostAddressLength = 6;
    public const int CommandHeaderLength = 8;
    public const int MinimumResponseLength = 8;
    public const int SetHostAddressRequestLength = 22;
    public const int LongTermKeyRequestLength = 25;
    public const int CommitRequestLength = 9;
    public const int MaximumRequestLength = LongTermKeyRequestLength;

    private static ReadOnlySpan<byte> LongTermKeyPart1 =>
    [
        0x00, 0xEA, 0xBD, 0x47, 0x13, 0x89, 0x35, 0x42, 0xC6,
        0x79, 0xEE, 0x07, 0xF2, 0x53, 0x2C, 0x6C, 0x31,
    ];

    private static ReadOnlySpan<byte> LongTermKeyPart2 =>
    [
        0x00, 0x40, 0xB0, 0x8A, 0x5F, 0xCD, 0x1F, 0x9B, 0x41,
        0x12, 0x5C, 0xAC, 0xC6, 0x3F, 0x38, 0xA0, 0x73,
    ];

    public static bool TryGetRequestLength(
        Switch2BluetoothAssociationStep step, out int length)
    {
        switch (step)
        {
            case Switch2BluetoothAssociationStep.SetHostAddress:
                length = SetHostAddressRequestLength;
                return true;
            case Switch2BluetoothAssociationStep.WriteLongTermKeyPart1:
            case Switch2BluetoothAssociationStep.WriteLongTermKeyPart2:
                length = LongTermKeyRequestLength;
                return true;
            case Switch2BluetoothAssociationStep.Commit:
                length = CommitRequestLength;
                return true;
            default:
                length = 0;
                return false;
        }
    }

    public static bool TryWriteRequest(Switch2BluetoothAssociationStep step,
        ReadOnlySpan<byte> localHostAddress, Span<byte> destination,
        out Switch2BluetoothAssociationCodecFailure failure)
    {
        if (!TryGetRequestLength(step, out int requestLength))
        {
            failure = Switch2BluetoothAssociationCodecFailure.InvalidStep;
            return false;
        }
        if (!IsValidHostAddress(localHostAddress))
        {
            failure = Switch2BluetoothAssociationCodecFailure.
                InvalidHostAddress;
            return false;
        }
        if (destination.Length != requestLength)
        {
            failure = Switch2BluetoothAssociationCodecFailure.InvalidLength;
            return false;
        }

        destination.Clear();
        destination[0] = AssociationCommandId;
        destination[1] = 0x91;
        destination[2] = 0x01;
        destination[3] = (byte)step;

        Span<byte> payload = destination[CommandHeaderLength..];
        destination[5] = checked((byte)payload.Length);
        switch (step)
        {
            case Switch2BluetoothAssociationStep.SetHostAddress:
                payload[0] = 0x00;
                payload[1] = 0x02;
                // The API accepts canonical/display order (the WinRT adapter
                // converts its 48-bit integer to that order). Command 0x15
                // requires little-endian address bytes, as do reconnect ads.
                // Convert here once, without mutating the caller's address.
                for (int index = 0; index < HostAddressLength; index++)
                {
                    byte value = localHostAddress[HostAddressLength - 1 - index];
                    payload[2 + index] = value;
                    payload[8 + index] = value;
                }
                break;
            case Switch2BluetoothAssociationStep.WriteLongTermKeyPart1:
                LongTermKeyPart1.CopyTo(payload);
                break;
            case Switch2BluetoothAssociationStep.WriteLongTermKeyPart2:
                LongTermKeyPart2.CopyTo(payload);
                break;
            case Switch2BluetoothAssociationStep.Commit:
                payload[0] = 0x00;
                break;
            default:
                throw new InvalidOperationException(
                    "Validated association step became unreachable.");
        }

        failure = Switch2BluetoothAssociationCodecFailure.None;
        return true;
    }

    /// <summary>
    /// Validates only the response facts used by the proven donor flow: a
    /// complete eight-byte-or-longer response, matching command id, and success
    /// status. The donor does not establish the remaining header bytes as a
    /// stable per-subcommand echo, so this codec does not invent that contract.
    /// </summary>
    public static bool TryValidateResponse(ReadOnlySpan<byte> response,
        out Switch2BluetoothAssociationCodecFailure failure)
    {
        if (response.Length < MinimumResponseLength)
        {
            failure = Switch2BluetoothAssociationCodecFailure.InvalidLength;
            return false;
        }
        if (response[0] != AssociationCommandId)
        {
            failure = Switch2BluetoothAssociationCodecFailure.InvalidCommand;
            return false;
        }
        if (response[1] != 0x01)
        {
            failure = Switch2BluetoothAssociationCodecFailure.InvalidStatus;
            return false;
        }

        failure = Switch2BluetoothAssociationCodecFailure.None;
        return true;
    }

    public static bool IsValidHostAddress(ReadOnlySpan<byte> address)
    {
        if (address.Length != HostAddressLength)
        {
            return false;
        }

        byte any = 0;
        byte anyNotFF = 0;
        for (int index = 0; index < address.Length; index++)
        {
            any |= address[index];
            anyNotFF |= (byte)~address[index];
        }
        return any != 0 && anyNotFF != 0;
    }
}
