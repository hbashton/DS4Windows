/*
DS4Windows
Copyright (C) 2026 hbashton
This program is free software under the GNU General Public License, version 3
or (at your option) any later version. See LICENSE for details.
*/

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace DS4Windows.Switch2;

internal interface ISwitch2RawStickCalibrationStore
{
    // Cold operations only. Shared by every store for the same backing data.
    // Never acquire this gate while holding a runtime publication lock.
    object SerializationGate { get; }
    bool TryLoad(Switch2PersistentPeerId peer, Switch2ControllerModel model,
        Switch2StickSide side, out Switch2StickCalibration calibration);
    bool TryStore(Switch2PersistentPeerId peer, Switch2ControllerModel model,
        Switch2StickSide side, in Switch2StickCalibration calibration);
    bool TryRemove(Switch2PersistentPeerId peer, Switch2ControllerModel model, Switch2StickSide side);
}

/// <summary>
/// Cold PC-side storage only. Callers must perform I/O outside report callbacks
/// and publication locks, then use their exact runtime admission to apply the
/// result. Each record binds one opaque peer, model and physical stick side.
/// No controller flash or transport command is involved.
/// </summary>
internal sealed class Switch2RawStickCalibrationFileStore : ISwitch2RawStickCalibrationStore
{
    internal const int RecordLength = 51;
    private const int PeerOffset = 5, ModelOffset = 21, SideOffset = 22;
    private const int ValuesOffset = 23, DigestOffset = 35;
    private static ReadOnlySpan<byte> Magic => "S2S1"u8;
    private readonly string directory;
    private static readonly ConcurrentDictionary<string, object> DirectoryGates = new(StringComparer.OrdinalIgnoreCase);
    public object SerializationGate { get; }

    private Switch2RawStickCalibrationFileStore(string root)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, "StickCalibration")));
        SerializationGate = DirectoryGates.GetOrAdd(directory, static _ => new object());
    }

    internal static bool TryOpen(string root, out Switch2RawStickCalibrationFileStore store)
    {
        store = null;
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var candidate = new Switch2RawStickCalibrationFileStore(root);
            Directory.CreateDirectory(candidate.directory);
            store = candidate;
            return true;
        }
        catch { return false; }
    }

    public bool TryLoad(Switch2PersistentPeerId peer, Switch2ControllerModel model,
        Switch2StickSide side, out Switch2StickCalibration calibration)
    {
        calibration = default;
        if (!TryPath(peer, model, side, out string path)) return false;
        lock (SerializationGate)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != RecordLength) return false;
                Span<byte> record = stackalloc byte[RecordLength];
                stream.ReadExactly(record);
                return TryDecode(record, peer, model, side, out calibration);
            }
            catch { return false; }
        }
    }

    public bool TryStore(Switch2PersistentPeerId peer, Switch2ControllerModel model,
        Switch2StickSide side, in Switch2StickCalibration calibration)
    {
        if (!TryPath(peer, model, side, out string path)) return false;
        Span<byte> record = stackalloc byte[RecordLength];
        if (!TryEncode(peer, model, side, calibration, record)) return false;
        lock (SerializationGate)
        {
            string temporary = Path.Combine(directory,
                "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(record);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
                return true;
            }
            catch { return false; }
            finally
            {
                // This unique scratch path belongs solely to this write.
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    public bool TryRemove(Switch2PersistentPeerId peer, Switch2ControllerModel model, Switch2StickSide side)
    {
        if (!TryPath(peer, model, side, out string path)) return false;
        lock (SerializationGate)
        {
            try { File.Delete(path); return true; }
            catch { return false; }
        }
    }

    internal static bool TryEncode(Switch2PersistentPeerId peer, Switch2ControllerModel model,
        Switch2StickSide side, in Switch2StickCalibration calibration, Span<byte> record)
    {
        if (record.Length != RecordLength || !peer.IsValid ||
            !Switch2RawStickCalibrationCollector.SupportsSide(model, side) || !IsValid(calibration)) return false;
        record.Clear();
        Magic.CopyTo(record);
        record[4] = 1;
        if (!peer.TryWrite(record.Slice(PeerOffset, Switch2PersistentPeerId.EncodedLength))) return false;
        record[ModelOffset] = (byte)model;
        record[SideOffset] = (byte)side;
        Write(record, 0, calibration.NeutralX); Write(record, 1, calibration.NeutralY);
        Write(record, 2, calibration.PositiveRangeX); Write(record, 3, calibration.PositiveRangeY);
        Write(record, 4, calibration.NegativeRangeX); Write(record, 5, calibration.NegativeRangeY);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(record[..DigestOffset], digest);
        digest[..16].CopyTo(record[DigestOffset..]);
        return true;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> record, Switch2PersistentPeerId peer,
        Switch2ControllerModel model, Switch2StickSide side, out Switch2StickCalibration calibration)
    {
        calibration = default;
        if (record.Length != RecordLength || !peer.IsValid ||
            !Switch2RawStickCalibrationCollector.SupportsSide(model, side) ||
            !record[..4].SequenceEqual(Magic) || record[4] != 1 ||
            record[ModelOffset] != (byte)model || record[SideOffset] != (byte)side ||
            !Switch2PersistentPeerId.TryRead(record.Slice(PeerOffset, Switch2PersistentPeerId.EncodedLength), out var encodedPeer) ||
            encodedPeer != peer) return false;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(record[..DigestOffset], digest);
        if (!CryptographicOperations.FixedTimeEquals(digest[..16], record[DigestOffset..])) return false;
        var candidate = new Switch2StickCalibration(Read(record, 0), Read(record, 1),
            Read(record, 2), Read(record, 3), Read(record, 4), Read(record, 5));
        if (!IsValid(candidate)) return false;
        calibration = candidate;
        return true;
    }

    private bool TryPath(Switch2PersistentPeerId peer, Switch2ControllerModel model, Switch2StickSide side, out string path)
    {
        path = null;
        Span<byte> encoded = stackalloc byte[Switch2PersistentPeerId.EncodedLength];
        if (!Switch2RawStickCalibrationCollector.SupportsSide(model, side) || !peer.TryWrite(encoded)) return false;
        path = Path.Combine(directory, Convert.ToHexString(encoded) + "-" + (byte)model + "-" + (byte)side + ".stick");
        return true;
    }

    internal static bool IsValid(in Switch2StickCalibration value) =>
        Switch2CalibrationCodec.TryValidateAdoptable(value, out _) &&
        value.NegativeRangeX >= Switch2RawStickCalibrationCollector.MinimumTravel &&
        value.NegativeRangeY >= Switch2RawStickCalibrationCollector.MinimumTravel &&
        value.PositiveRangeX >= Switch2RawStickCalibrationCollector.MinimumTravel &&
        value.PositiveRangeY >= Switch2RawStickCalibrationCollector.MinimumTravel;

    private static void Write(Span<byte> record, int index, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(ValuesOffset + index * 2, 2), value);
    private static ushort Read(ReadOnlySpan<byte> record, int index) =>
        BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(ValuesOffset + index * 2, 2));
}
