/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Buffers.Binary;

namespace DS4Windows.Switch2;

public enum Switch2BluetoothMemoryReadCodecFailure : byte
{
    None = 0,
    InvalidLength,
    InvalidDestination,
    InvalidCommand,
    InvalidStatus,
    MismatchedReadLength,
    MismatchedAddress,
}

/// <summary>
/// Closed encoder/decoder for the acknowledged Switch 2 BLE memory-read
/// command. This is a direct GPL-compatible adaptation of Switch2Connect
/// commit 4487322a306f04efa27682e3f3a508635a84fd98,
/// src/controller.py lines 1173-1177, 2666-2682 and 3914-3919. Only bounded
/// reads are represented; this type exposes no arbitrary write surface.
/// </summary>
public static class Switch2BluetoothMemoryReadCodec
{
    public const byte CommandId = 0x02;
    public const byte ReadSubcommandId = 0x04;
    public const byte MaximumReadLength = 0x4F;
    public const int RequestLength = 16;
    public const int ResponsePayloadOffset = 16;

    public static bool TryWriteRequest(byte length, uint address,
        Span<byte> destination,
        out Switch2BluetoothMemoryReadCodecFailure failure)
    {
        if (length == 0 || length > MaximumReadLength)
        {
            failure = Switch2BluetoothMemoryReadCodecFailure.InvalidLength;
            return false;
        }
        if (destination.Length != RequestLength)
        {
            failure = Switch2BluetoothMemoryReadCodecFailure.
                InvalidDestination;
            return false;
        }

        destination.Clear();
        destination[0] = CommandId;
        destination[1] = 0x91;
        destination[2] = 0x01;
        destination[3] = ReadSubcommandId;
        destination[5] = 0x08;
        destination[8] = length;
        destination[9] = 0x7E;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16],
            address);
        failure = Switch2BluetoothMemoryReadCodecFailure.None;
        return true;
    }

    public static bool TryCopyResponsePayload(ReadOnlySpan<byte> response,
        byte expectedLength, uint expectedAddress, Span<byte> destination,
        out Switch2BluetoothMemoryReadCodecFailure failure)
    {
        if (expectedLength == 0 || expectedLength > MaximumReadLength)
        {
            failure = Switch2BluetoothMemoryReadCodecFailure.InvalidLength;
            return false;
        }
        if (destination.Length != expectedLength)
        {
            failure = Switch2BluetoothMemoryReadCodecFailure.
                InvalidDestination;
            return false;
        }
        if (response.Length < ResponsePayloadOffset + expectedLength)
        {
            failure = Switch2BluetoothMemoryReadCodecFailure.InvalidLength;
            return false;
        }
        if (response[0] != CommandId)
        {
            failure = Switch2BluetoothMemoryReadCodecFailure.InvalidCommand;
            return false;
        }
        if (response[1] != 0x01)
        {
            failure = Switch2BluetoothMemoryReadCodecFailure.InvalidStatus;
            return false;
        }
        if (response[8] != expectedLength)
        {
            failure = Switch2BluetoothMemoryReadCodecFailure.
                MismatchedReadLength;
            return false;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(response[12..16]) !=
                expectedAddress)
        {
            failure = Switch2BluetoothMemoryReadCodecFailure.
                MismatchedAddress;
            return false;
        }

        response.Slice(ResponsePayloadOffset, expectedLength).
            CopyTo(destination);
        failure = Switch2BluetoothMemoryReadCodecFailure.None;
        return true;
    }
}
