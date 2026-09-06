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
using System.IO;
using System.Security.Cryptography;

namespace DS4Windows.Switch2;

internal interface ISwitch2MagnetometerCalibrationStore
{
    bool TryLoad(Switch2PersistentPeerId peerId,
        out Switch2MagnetometerCalibration calibration);

    bool TryStore(Switch2PersistentPeerId peerId,
        in Switch2MagnetometerCalibration calibration);
}

/// <summary>
/// Atomic install-local calibration store keyed only by the existing HMAC
/// pseudonym. No Bluetooth address, Windows identity, USB path, or serial is
/// formatted or persisted. Records are fixed-size, versioned, validated, and
/// digest-protected before their matrices can enter a motion owner.
/// </summary>
internal sealed class Switch2MagnetometerCalibrationFileStore :
    ISwitch2MagnetometerCalibrationStore
{
    private const int PeerOffset = 5;
    private const int ModelOffset = PeerOffset +
        Switch2PersistentPeerId.EncodedLength;
    private const int BiasOffset = ModelOffset + 1;
    private const int MatrixOffset = BiasOffset + 3 * sizeof(float);
    private const int ReferenceOffset = MatrixOffset + 9 * sizeof(float);
    private const int DigestOffset = ReferenceOffset + sizeof(float);
    private const int DigestLength = 16;
    private const int RecordLength = DigestOffset + DigestLength;
    private static ReadOnlySpan<byte> Magic => "S2M1"u8;

    private readonly object sync = new();
    private readonly string directory;

    private Switch2MagnetometerCalibrationFileStore(string rootDirectory)
    {
        directory = Path.GetFullPath(Path.Combine(rootDirectory,
            "Magnetometer"));
    }

    internal static bool TryOpen(string rootDirectory,
        out Switch2MagnetometerCalibrationFileStore store)
    {
        store = null;
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }
        try
        {
            var candidate = new Switch2MagnetometerCalibrationFileStore(
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
        out Switch2MagnetometerCalibration calibration)
    {
        calibration = default;
        if (!peerId.IsValid || !TryPath(peerId, out string path))
        {
            return false;
        }
        lock (sync)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }
                byte[] record = File.ReadAllBytes(path);
                return TryDecode(record, peerId, out calibration);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryStore(Switch2PersistentPeerId peerId,
        in Switch2MagnetometerCalibration calibration)
    {
        if (!peerId.IsValid || !calibration.IsValid ||
            !TryPath(peerId, out string path))
        {
            return false;
        }
        Span<byte> record = stackalloc byte[RecordLength];
        if (!TryEncode(peerId, calibration, record))
        {
            return false;
        }
        lock (sync)
        {
            string temporary = Path.Combine(directory, "." +
                Path.GetFileName(path) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary,
                           FileMode.CreateNew, FileAccess.Write,
                           FileShare.None))
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
            ".mag");
        return true;
    }

    private static bool TryEncode(Switch2PersistentPeerId peerId,
        in Switch2MagnetometerCalibration calibration,
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
        destination[ModelOffset] = (byte)calibration.Model;
        WriteSingle(destination, BiasOffset, calibration.Bias.X);
        WriteSingle(destination, BiasOffset + 4, calibration.Bias.Y);
        WriteSingle(destination, BiasOffset + 8, calibration.Bias.Z);
        Switch2MagnetometerMatrix3x3 matrix = calibration.Correction;
        WriteSingle(destination, MatrixOffset, matrix.M11);
        WriteSingle(destination, MatrixOffset + 4, matrix.M12);
        WriteSingle(destination, MatrixOffset + 8, matrix.M13);
        WriteSingle(destination, MatrixOffset + 12, matrix.M21);
        WriteSingle(destination, MatrixOffset + 16, matrix.M22);
        WriteSingle(destination, MatrixOffset + 20, matrix.M23);
        WriteSingle(destination, MatrixOffset + 24, matrix.M31);
        WriteSingle(destination, MatrixOffset + 28, matrix.M32);
        WriteSingle(destination, MatrixOffset + 32, matrix.M33);
        WriteSingle(destination, ReferenceOffset,
            calibration.ReferenceMagnitude);
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
        out Switch2MagnetometerCalibration calibration)
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

        var model = (Switch2MagnetometerCalibrationModel)
            source[ModelOffset];
        var bias = new System.Numerics.Vector3(
            ReadSingle(source, BiasOffset),
            ReadSingle(source, BiasOffset + 4),
            ReadSingle(source, BiasOffset + 8));
        if (!Switch2MagnetometerMatrix3x3.TryCreate(
                ReadSingle(source, MatrixOffset),
                ReadSingle(source, MatrixOffset + 4),
                ReadSingle(source, MatrixOffset + 8),
                ReadSingle(source, MatrixOffset + 12),
                ReadSingle(source, MatrixOffset + 16),
                ReadSingle(source, MatrixOffset + 20),
                ReadSingle(source, MatrixOffset + 24),
                ReadSingle(source, MatrixOffset + 28),
                ReadSingle(source, MatrixOffset + 32),
                out Switch2MagnetometerMatrix3x3 matrix, out _))
        {
            return false;
        }
        return Switch2MagnetometerCalibration.TryCreate(bias, matrix,
            ReadSingle(source, ReferenceOffset), model, out calibration);
    }

    private static void WriteSingle(Span<byte> destination, int offset,
        float value) => BinaryPrimitives.WriteInt32LittleEndian(
            destination.Slice(offset, sizeof(float)),
            BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
            source.Slice(offset, sizeof(float))));
}
