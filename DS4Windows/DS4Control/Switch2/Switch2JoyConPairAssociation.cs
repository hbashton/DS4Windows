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
using System.Security.Cryptography;
using System.Threading;

namespace DS4Windows.Switch2;

/// <summary>
/// Install-local, opaque pseudonym for one OS-associated Switch 2 peer. It is
/// derived with a protected install key and never retains or exposes the OS
/// identity, Bluetooth address, device path, bond, or key material.
/// </summary>
internal readonly struct Switch2PersistentPeerId :
    IEquatable<Switch2PersistentPeerId>
{
    internal const int InstallKeyLength = 32;
    internal const int EncodedLength = 16;
    internal const int MaximumOsIdentityLength = 256;

    private readonly ulong low;
    private readonly ulong high;

    private Switch2PersistentPeerId(ulong low, ulong high)
    {
        this.low = low;
        this.high = high;
    }

    internal bool IsValid => (low | high) != 0;

    internal static bool TryDerive(ReadOnlySpan<byte> installKey,
        ReadOnlySpan<byte> osAssociationIdentity,
        Switch2ControllerModel model, ushort productId,
        out Switch2PersistentPeerId peerId)
    {
        ReadOnlySpan<byte> domain =
            "DS4Windows/Switch2/PersistentPeer/v1"u8;
        if (installKey.Length != InstallKeyLength ||
            !ContainsNonzeroByte(installKey) ||
            osAssociationIdentity.IsEmpty ||
            osAssociationIdentity.Length > MaximumOsIdentityLength ||
            !ContainsNonzeroByte(osAssociationIdentity) ||
            !IsExactControllerIdentity(model, productId))
        {
            peerId = default;
            return false;
        }

        Span<byte> input = stackalloc byte[
            domain.Length + 1 + sizeof(ushort) + sizeof(ushort) +
            osAssociationIdentity.Length];
        Span<byte> digest = stackalloc byte[32];
        try
        {
            int offset = 0;
            domain.CopyTo(input);
            offset += domain.Length;
            input[offset++] = (byte)model;
            BinaryPrimitives.WriteUInt16LittleEndian(input.Slice(offset),
                productId);
            offset += sizeof(ushort);
            BinaryPrimitives.WriteUInt16LittleEndian(input.Slice(offset),
                checked((ushort)osAssociationIdentity.Length));
            offset += sizeof(ushort);
            osAssociationIdentity.CopyTo(input.Slice(offset));
            HMACSHA256.HashData(installKey, input, digest);

            ulong candidateLow = BinaryPrimitives.ReadUInt64LittleEndian(
                digest);
            ulong candidateHigh = BinaryPrimitives.ReadUInt64LittleEndian(
                digest.Slice(sizeof(ulong)));
            if ((candidateLow | candidateHigh) == 0)
            {
                peerId = default;
                return false;
            }
            peerId = new Switch2PersistentPeerId(candidateLow,
                candidateHigh);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal bool TryWrite(Span<byte> destination)
    {
        // The persistent format is canonical and fixed-width. Refuse a
        // larger caller-owned buffer instead of leaving an unexamined tail
        // that a store could accidentally persist beside this pseudonym.
        if (!IsValid || destination.Length != EncodedLength)
        {
            return false;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(destination, low);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination.Slice(sizeof(ulong)), high);
        return true;
    }

    internal static bool TryRead(ReadOnlySpan<byte> source,
        out Switch2PersistentPeerId peerId)
    {
        if (source.Length != EncodedLength)
        {
            peerId = default;
            return false;
        }
        ulong candidateLow = BinaryPrimitives.ReadUInt64LittleEndian(source);
        ulong candidateHigh = BinaryPrimitives.ReadUInt64LittleEndian(
            source.Slice(sizeof(ulong)));
        peerId = new Switch2PersistentPeerId(candidateLow, candidateHigh);
        if (peerId.IsValid)
        {
            return true;
        }
        peerId = default;
        return false;
    }

    public bool Equals(Switch2PersistentPeerId other) =>
        low == other.low && high == other.high;

    public override bool Equals(object obj) =>
        obj is Switch2PersistentPeerId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(low, high);

    public static bool operator ==(Switch2PersistentPeerId left,
        Switch2PersistentPeerId right) => left.Equals(right);

    public static bool operator !=(Switch2PersistentPeerId left,
        Switch2PersistentPeerId right) => !left.Equals(right);

    private static bool IsExactControllerIdentity(Switch2ControllerModel model,
        ushort productId) => (model, productId) switch
        {
            (Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId) => true,
            (Switch2ControllerModel.JoyCon2Right,
                Switch2AdvertisementCodec.JoyCon2RightProductId) => true,
            (Switch2ControllerModel.ProController2,
                Switch2AdvertisementCodec.ProController2ProductId) => true,
            _ => false,
        };

    private static bool ContainsNonzeroByte(ReadOnlySpan<byte> value)
    {
        byte aggregate = 0;
        foreach (byte current in value)
        {
            aggregate |= current;
        }
        return aggregate != 0;
    }
}

internal readonly struct Switch2JoyConPairId :
    IEquatable<Switch2JoyConPairId>
{
    internal const int EncodedLength = 16;
    private readonly ulong low;
    private readonly ulong high;

    private Switch2JoyConPairId(ulong low, ulong high)
    {
        this.low = low;
        this.high = high;
    }

    internal bool IsValid => (low | high) != 0;

    internal static Switch2JoyConPairId CreateRandom()
    {
        Span<byte> bytes = stackalloc byte[EncodedLength];
        try
        {
            Switch2JoyConPairId pairId;
            do
            {
                RandomNumberGenerator.Fill(bytes);
            }
            while (!TryRead(bytes, out pairId));
            return pairId;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal bool TryWrite(Span<byte> destination)
    {
        if (!IsValid || destination.Length != EncodedLength)
        {
            return false;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(destination, low);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination.Slice(sizeof(ulong)), high);
        return true;
    }

    internal static bool TryRead(ReadOnlySpan<byte> source,
        out Switch2JoyConPairId pairId)
    {
        if (source.Length != EncodedLength)
        {
            pairId = default;
            return false;
        }
        pairId = new Switch2JoyConPairId(
            BinaryPrimitives.ReadUInt64LittleEndian(source),
            BinaryPrimitives.ReadUInt64LittleEndian(
                source.Slice(sizeof(ulong))));
        if (pairId.IsValid)
        {
            return true;
        }
        pairId = default;
        return false;
    }

    public bool Equals(Switch2JoyConPairId other) =>
        low == other.low && high == other.high;

    public override bool Equals(object obj) =>
        obj is Switch2JoyConPairId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(low, high);

    public static bool operator ==(Switch2JoyConPairId left,
        Switch2JoyConPairId right) => left.Equals(right);

    public static bool operator !=(Switch2JoyConPairId left,
        Switch2JoyConPairId right) => !left.Equals(right);
}

internal readonly struct Switch2JoyConPairRecord
{
    internal const byte CurrentSchemaVersion = 1;
    internal const int EncodedLength = 1 + sizeof(ulong) +
        Switch2JoyConPairId.EncodedLength +
        (2 * Switch2PersistentPeerId.EncodedLength);

    private Switch2JoyConPairRecord(byte schemaVersion,
        ulong revision, Switch2JoyConPairId pairId,
        Switch2PersistentPeerId leftPeerId,
        Switch2PersistentPeerId rightPeerId)
    {
        SchemaVersion = schemaVersion;
        Revision = revision;
        PairId = pairId;
        LeftPeerId = leftPeerId;
        RightPeerId = rightPeerId;
    }

    internal byte SchemaVersion { get; }
    internal ulong Revision { get; }
    internal Switch2JoyConPairId PairId { get; }
    internal Switch2PersistentPeerId LeftPeerId { get; }
    internal Switch2PersistentPeerId RightPeerId { get; }
    internal bool IsValid => SchemaVersion == CurrentSchemaVersion &&
        Revision != 0 && PairId.IsValid && LeftPeerId.IsValid &&
        RightPeerId.IsValid && LeftPeerId != RightPeerId;

    internal static bool TryCreate(ulong revision,
        Switch2JoyConPairId pairId,
        Switch2PersistentPeerId leftPeerId,
        Switch2PersistentPeerId rightPeerId,
        out Switch2JoyConPairRecord record)
    {
        record = new Switch2JoyConPairRecord(CurrentSchemaVersion, revision,
            pairId, leftPeerId, rightPeerId);
        if (record.IsValid)
        {
            return true;
        }
        record = default;
        return false;
    }

    internal bool TryWrite(Span<byte> destination)
    {
        if (!IsValid || destination.Length != EncodedLength)
        {
            return false;
        }
        destination[0] = SchemaVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(1),
            Revision);
        int offset = 1 + sizeof(ulong);
        if (!PairId.TryWrite(destination.Slice(offset,
                Switch2JoyConPairId.EncodedLength)))
        {
            return false;
        }
        offset += Switch2JoyConPairId.EncodedLength;
        if (!LeftPeerId.TryWrite(destination.Slice(offset,
                Switch2PersistentPeerId.EncodedLength)))
        {
            return false;
        }
        offset += Switch2PersistentPeerId.EncodedLength;
        return RightPeerId.TryWrite(destination.Slice(offset,
            Switch2PersistentPeerId.EncodedLength));
    }

    internal static bool TryRead(ReadOnlySpan<byte> source,
        out Switch2JoyConPairRecord record)
    {
        record = default;
        if (source.Length != EncodedLength ||
            source[0] != CurrentSchemaVersion)
        {
            return false;
        }
        ulong revision = BinaryPrimitives.ReadUInt64LittleEndian(
            source.Slice(1));
        int offset = 1 + sizeof(ulong);
        if (!Switch2JoyConPairId.TryRead(source.Slice(offset,
                Switch2JoyConPairId.EncodedLength),
                out Switch2JoyConPairId pairId))
        {
            return false;
        }
        offset += Switch2JoyConPairId.EncodedLength;
        if (!Switch2PersistentPeerId.TryRead(source.Slice(offset,
                Switch2PersistentPeerId.EncodedLength),
                out Switch2PersistentPeerId leftPeerId))
        {
            return false;
        }
        offset += Switch2PersistentPeerId.EncodedLength;
        return Switch2PersistentPeerId.TryRead(source.Slice(offset,
                Switch2PersistentPeerId.EncodedLength),
                out Switch2PersistentPeerId rightPeerId) &&
            TryCreate(revision, pairId, leftPeerId, rightPeerId, out record);
    }
}

/// <summary>
/// Persistence belongs to service orchestration, not discovery tokens or
/// runtime registrations. Implementations must replace records atomically and
/// reject non-monotonic revisions.
/// </summary>
internal interface ISwitch2JoyConPairStore
{
    bool TryLoad(Switch2JoyConPairId pairId,
        out Switch2JoyConPairRecord record);

    bool TryReplace(in Switch2JoyConPairRecord record,
        ulong expectedPriorRevision);

    bool TryDelete(Switch2JoyConPairId pairId,
        ulong expectedRevision);
}

internal interface ISwitch2JoyConPairCatalog : ISwitch2JoyConPairStore
{
    bool TryLoadAll(out Switch2JoyConPairRecord[] records);
}

/// <summary>
/// One-shot composition authority for exactly two current-scan admissions
/// selected by an explicit persisted L/R association. Persistent peer IDs are
/// validated at construction but intentionally are not retained here.
/// </summary>
internal readonly struct Switch2JoyConPairConnectionAdmission
{
    private readonly Switch2BluetoothConnectionAdmission left;
    private readonly Switch2BluetoothConnectionAdmission right;
    private readonly Switch2JoyConPairConnectionReservation reservation;

    private Switch2JoyConPairConnectionAdmission(
        in Switch2JoyConPairRecord record,
        in Switch2BluetoothConnectionAdmission left,
        in Switch2BluetoothConnectionAdmission right)
    {
        PairId = record.PairId;
        PairRecordRevision = record.Revision;
        ScanGeneration = left.ScanGeneration;
        this.left = left;
        this.right = right;
        reservation = new Switch2JoyConPairConnectionReservation();
    }

    internal Switch2JoyConPairId PairId { get; }
    internal ulong PairRecordRevision { get; }
    internal ulong ScanGeneration { get; }
    internal bool IsValid => PairId.IsValid && PairRecordRevision != 0 &&
        ScanGeneration != 0 && reservation != null && left.IsValid &&
        right.IsValid;

    internal static bool TryCreate(in Switch2JoyConPairRecord record,
        Switch2PersistentPeerId observedLeftPeerId,
        in Switch2BluetoothConnectionAdmission left,
        Switch2PersistentPeerId observedRightPeerId,
        in Switch2BluetoothConnectionAdmission right,
        out Switch2JoyConPairConnectionAdmission admission)
    {
        if (!record.IsValid ||
            observedLeftPeerId != record.LeftPeerId ||
            observedRightPeerId != record.RightPeerId ||
            left.Model != Switch2ControllerModel.JoyCon2Left ||
            right.Model != Switch2ControllerModel.JoyCon2Right ||
            !left.IsValid || !right.IsValid ||
            left.ScanGeneration != right.ScanGeneration)
        {
            admission = default;
            return false;
        }
        admission = new Switch2JoyConPairConnectionAdmission(record, left,
            right);
        return true;
    }

    /// <summary>
    /// Validates the exact current-scan admissions without exposing either
    /// unconsumed single-side capability. Only a successful composite consume
    /// can release the two admission values to the transport preparation path.
    /// </summary>
    internal bool MatchesExactAdmissions(
        in Switch2BluetoothConnectionAdmission leftAdmission,
        in Switch2BluetoothConnectionAdmission rightAdmission,
        out bool leftMatches, out bool rightMatches)
    {
        leftMatches = IsValid && left.Equals(leftAdmission);
        rightMatches = IsValid && right.Equals(rightAdmission);
        return leftMatches && rightMatches;
    }

    internal bool TryConsume(
        out Switch2BluetoothConnectionAdmission leftAdmission,
        out Switch2BluetoothConnectionAdmission rightAdmission)
    {
        leftAdmission = default;
        rightAdmission = default;
        // Every attempt spends this composite authority. If either physical
        // admission lost to a standalone or competing-pair consumer, retrying
        // the same association cannot restore that one-shot authority and is
        // rejected fail closed. The underlying pair operation itself remains
        // atomic, so its failed attempt consumes neither remaining half.
        if (!IsValid || !reservation.TryConsume() ||
            !Switch2BluetoothConnectionAdmission.TryConsumePair(left,
                right))
        {
            return false;
        }
        leftAdmission = left;
        rightAdmission = right;
        return true;
    }
}

internal sealed class Switch2JoyConPairConnectionReservation
{
    private int consumed;

    internal bool TryConsume() => Interlocked.CompareExchange(
        ref consumed, 1, 0) == 0;
}
