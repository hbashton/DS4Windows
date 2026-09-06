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
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;

namespace DS4Windows.Switch2;

internal readonly struct Switch2GyroCalibrationRecord :
    IEquatable<Switch2GyroCalibrationRecord>
{
    private Switch2GyroCalibrationRecord(in Vector3 biasDps)
    {
        version = 1;
        BiasDps = biasDps;
    }

    private readonly byte version;

    internal Vector3 BiasDps { get; }

    internal bool IsValid => version == 1 && IsFinite(BiasDps) &&
        BiasDps.Length() <=
            Switch2StationaryGyroCalibration.MaximumCommittedBiasDps;

    internal static bool TryCreate(in Vector3 biasDps,
        out Switch2GyroCalibrationRecord record)
    {
        record = new Switch2GyroCalibrationRecord(biasDps);
        if (record.IsValid)
        {
            return true;
        }
        record = default;
        return false;
    }

    public bool Equals(Switch2GyroCalibrationRecord other) =>
        version == other.version && BiasDps.Equals(other.BiasDps);

    public override bool Equals(object obj) =>
        obj is Switch2GyroCalibrationRecord other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(version, BiasDps);

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

internal interface ISwitch2GyroCalibrationStore
{
    bool TryLoad(Switch2PersistentPeerId peerId,
        out Switch2GyroCalibrationRecord calibration);

    bool TryQueueStore(Switch2PersistentPeerId peerId,
        in Switch2GyroCalibrationRecord calibration);
}

/// <summary>
/// Install-local gyro-bias store keyed only by the existing HMAC peer
/// pseudonym. Calibration commits enqueue one rare ordered background write;
/// physical input publication never waits for disk I/O.
/// </summary>
internal sealed class Switch2GyroCalibrationFileStore :
    ISwitch2GyroCalibrationStore
{
    private const int PeerOffset = 5;
    private const int BiasOffset = PeerOffset +
        Switch2PersistentPeerId.EncodedLength;
    private const int DigestOffset = BiasOffset + 3 * sizeof(float);
    private const int DigestLength = 16;
    private const int RecordLength = DigestOffset + DigestLength;
    private const int MaximumQueuedWrites = 32;
    private const int MaximumWriteAttempts = 3;
    private static ReadOnlySpan<byte> Magic => "S2G1"u8;

    private readonly object sync = new();
    private readonly string directory;
    private readonly Queue<PendingWrite> pendingWrites = new();
    private bool writerScheduled;

    private Switch2GyroCalibrationFileStore(string rootDirectory)
    {
        directory = Path.GetFullPath(Path.Combine(rootDirectory,
            "GyroCalibration"));
    }

    internal static bool TryOpen(string rootDirectory,
        out Switch2GyroCalibrationFileStore store)
    {
        store = null;
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }
        try
        {
            var candidate = new Switch2GyroCalibrationFileStore(
                rootDirectory);
            Directory.CreateDirectory(candidate.directory);
            store = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryLoad(Switch2PersistentPeerId peerId,
        out Switch2GyroCalibrationRecord calibration)
    {
        calibration = default;
        if (!peerId.IsValid || !TryPath(peerId, out string path))
        {
            return false;
        }
        try
        {
            return File.Exists(path) && TryDecode(File.ReadAllBytes(path),
                peerId, out calibration);
        }
        catch
        {
            calibration = default;
            return false;
        }
    }

    public bool TryQueueStore(Switch2PersistentPeerId peerId,
        in Switch2GyroCalibrationRecord calibration)
    {
        if (!peerId.IsValid || !calibration.IsValid)
        {
            return false;
        }
        lock (sync)
        {
            if (pendingWrites.Count >= MaximumQueuedWrites)
            {
                return false;
            }
            pendingWrites.Enqueue(new PendingWrite(peerId, calibration));
            if (writerScheduled)
            {
                return true;
            }

            writerScheduled = true;
            if (ThreadPool.QueueUserWorkItem(static state =>
                    ((Switch2GyroCalibrationFileStore)state).
                        DrainWriteQueue(), this))
            {
                return true;
            }

            pendingWrites.Clear();
            writerScheduled = false;
            return false;
        }
    }

    private void DrainWriteQueue()
    {
        while (true)
        {
            PendingWrite pending;
            lock (sync)
            {
                if (pendingWrites.Count == 0)
                {
                    writerScheduled = false;
                    return;
                }
                pending = pendingWrites.Dequeue();
            }

            for (int attempt = 0; attempt < MaximumWriteAttempts; attempt++)
            {
                if (TryStoreImmediate(pending.PeerId,
                        pending.Calibration))
                {
                    break;
                }
            }
        }
    }

    private bool TryStoreImmediate(Switch2PersistentPeerId peerId,
        in Switch2GyroCalibrationRecord calibration)
    {
        if (!TryPath(peerId, out string path))
        {
            return false;
        }
        Span<byte> record = stackalloc byte[RecordLength];
        if (!TryEncode(peerId, calibration, record))
        {
            return false;
        }

        string temporary = Path.Combine(directory, "." +
            Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") +
            ".tmp");
        try
        {
            using (var stream = new FileStream(temporary,
                       FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(record);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            return false;
        }
    }

    private bool TryPath(Switch2PersistentPeerId peerId, out string path)
    {
        Span<byte> encoded = stackalloc byte[
            Switch2PersistentPeerId.EncodedLength];
        if (!peerId.TryWrite(encoded))
        {
            path = null;
            return false;
        }
        path = Path.Combine(directory, Convert.ToHexString(encoded) +
            ".gyro");
        return true;
    }

    private static bool TryEncode(Switch2PersistentPeerId peerId,
        in Switch2GyroCalibrationRecord calibration,
        Span<byte> destination)
    {
        if (destination.Length != RecordLength || !calibration.IsValid)
        {
            return false;
        }
        destination.Clear();
        Magic.CopyTo(destination);
        destination[4] = 1;
        if (!peerId.TryWrite(destination.Slice(PeerOffset,
                Switch2PersistentPeerId.EncodedLength)))
        {
            return false;
        }
        WriteSingle(destination, BiasOffset, calibration.BiasDps.X);
        WriteSingle(destination, BiasOffset + 4, calibration.BiasDps.Y);
        WriteSingle(destination, BiasOffset + 8, calibration.BiasDps.Z);
        Span<byte> digest = stackalloc byte[32];
        try
        {
            SHA256.HashData(destination[..DigestOffset], digest);
            digest[..DigestLength].CopyTo(destination[DigestOffset..]);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool TryDecode(ReadOnlySpan<byte> source,
        Switch2PersistentPeerId expectedPeerId,
        out Switch2GyroCalibrationRecord calibration)
    {
        calibration = default;
        if (source.Length != RecordLength ||
            !source[..Magic.Length].SequenceEqual(Magic) || source[4] != 1 ||
            !Switch2PersistentPeerId.TryRead(source.Slice(PeerOffset,
                Switch2PersistentPeerId.EncodedLength),
                out Switch2PersistentPeerId encodedPeerId) ||
            encodedPeerId != expectedPeerId)
        {
            return false;
        }
        Span<byte> digest = stackalloc byte[32];
        try
        {
            SHA256.HashData(source[..DigestOffset], digest);
            if (!CryptographicOperations.FixedTimeEquals(
                    digest[..DigestLength], source[DigestOffset..]))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }

        var biasDps = new Vector3(ReadSingle(source, BiasOffset),
            ReadSingle(source, BiasOffset + 4),
            ReadSingle(source, BiasOffset + 8));
        return Switch2GyroCalibrationRecord.TryCreate(biasDps,
            out calibration);
    }

    private static void WriteSingle(Span<byte> destination, int offset,
        float value) => BinaryPrimitives.WriteInt32LittleEndian(
            destination.Slice(offset, sizeof(float)),
            BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
            source.Slice(offset, sizeof(float))));

    private readonly struct PendingWrite
    {
        internal PendingWrite(Switch2PersistentPeerId peerId,
            in Switch2GyroCalibrationRecord calibration)
        {
            PeerId = peerId;
            Calibration = calibration;
        }

        internal Switch2PersistentPeerId PeerId { get; }
        internal Switch2GyroCalibrationRecord Calibration { get; }
    }
}
