/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.IO;
using System.Security.Cryptography;

namespace DS4Windows.Switch2;

internal interface ISwitch2JoyConHoldModeStore
{
    bool TryLoad(Switch2PersistentPeerId peerId,
        out Switch2JoyConHoldMode holdMode);

    bool TryStore(Switch2PersistentPeerId peerId,
        Switch2JoyConHoldMode holdMode);
}

/// <summary>
/// Atomic install-local standalone Joy-Con orientation store. Records are
/// keyed only by the existing opaque HMAC peer pseudonym; Bluetooth addresses,
/// Windows identities, bonds, and transport credentials never reach disk.
/// </summary>
internal sealed class Switch2JoyConHoldModeFileStore :
    ISwitch2JoyConHoldModeStore
{
    private const Switch2JoyConHoldMode InvalidMode =
        (Switch2JoyConHoldMode)byte.MaxValue;
    private const int PeerOffset = 5;
    private const int ModeOffset = PeerOffset +
        Switch2PersistentPeerId.EncodedLength;
    private const int DigestOffset = ModeOffset + 1;
    private const int DigestLength = 16;
    private const int RecordLength = DigestOffset + DigestLength;
    private static ReadOnlySpan<byte> Magic => "S2H1"u8;

    private readonly object sync = new();
    private readonly string directory;

    private Switch2JoyConHoldModeFileStore(string rootDirectory)
    {
        directory = Path.GetFullPath(Path.Combine(rootDirectory,
            "JoyConHoldMode"));
    }

    internal static bool TryOpen(string rootDirectory,
        out Switch2JoyConHoldModeFileStore store)
    {
        store = null;
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }
        try
        {
            var candidate = new Switch2JoyConHoldModeFileStore(
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
        out Switch2JoyConHoldMode holdMode)
    {
        holdMode = InvalidMode;
        if (!peerId.IsValid || !TryPath(peerId, out string path))
        {
            return false;
        }
        lock (sync)
        {
            try
            {
                return File.Exists(path) &&
                    TryDecode(File.ReadAllBytes(path), peerId,
                        out holdMode);
            }
            catch
            {
                holdMode = InvalidMode;
                return false;
            }
        }
    }

    public bool TryStore(Switch2PersistentPeerId peerId,
        Switch2JoyConHoldMode holdMode)
    {
        if (!peerId.IsValid || !IsValidMode(holdMode) ||
            !TryPath(peerId, out string path))
        {
            return false;
        }
        Span<byte> record = stackalloc byte[RecordLength];
        if (!TryEncode(peerId, holdMode, record))
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
            ".hold");
        return true;
    }

    private static bool TryEncode(Switch2PersistentPeerId peerId,
        Switch2JoyConHoldMode holdMode, Span<byte> destination)
    {
        if (destination.Length != RecordLength || !IsValidMode(holdMode))
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
        destination[ModeOffset] = (byte)holdMode;
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
        out Switch2JoyConHoldMode holdMode)
    {
        holdMode = InvalidMode;
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

        holdMode = (Switch2JoyConHoldMode)source[ModeOffset];
        if (IsValidMode(holdMode))
        {
            return true;
        }
        holdMode = InvalidMode;
        return false;
    }

    private static bool IsValidMode(Switch2JoyConHoldMode holdMode) =>
        holdMode is Switch2JoyConHoldMode.Vertical or
            Switch2JoyConHoldMode.Horizontal;
}
