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
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DS4Windows.Switch2;

/// <summary>
/// Versioned, atomic install-local pair catalog. Pair files contain only opaque
/// HMAC peer IDs. The HMAC install key is separately protected by Windows DPAPI
/// for the current user and is never returned to discovery, UI, logging, or a
/// pair record.
/// </summary>
internal sealed class Switch2JoyConPairFileStore :
    ISwitch2JoyConPairCatalog
{
    private const uint CryptProtectUiForbidden = 0x01;
    private const int KeyHeaderLength = 8;
    private static ReadOnlySpan<byte> KeyMagic => "S2K1"u8;

    private readonly object sync = new();
    private readonly string rootDirectory;
    private readonly string pairDirectory;
    private readonly string keyPath;

    private Switch2JoyConPairFileStore(string rootDirectory)
    {
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        pairDirectory = Path.Combine(this.rootDirectory, "Pairs");
        keyPath = Path.Combine(this.rootDirectory, "peer-identity.key");
    }

    internal static bool TryOpen(string rootDirectory,
        out Switch2JoyConPairFileStore store,
        out Switch2PersistentPeerIdentityDeriver identityDeriver)
    {
        store = null;
        identityDeriver = null;
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }

        byte[] installKey = null;
        try
        {
            var candidate = new Switch2JoyConPairFileStore(rootDirectory);
            Directory.CreateDirectory(candidate.rootDirectory);
            Directory.CreateDirectory(candidate.pairDirectory);
            if (!candidate.TryLoadOrCreateInstallKey(out installKey))
            {
                return false;
            }
            identityDeriver = new Switch2PersistentPeerIdentityDeriver(
                installKey);
            store = candidate;
            return true;
        }
        catch
        {
            identityDeriver?.Dispose();
            identityDeriver = null;
            store = null;
            return false;
        }
        finally
        {
            if (installKey != null)
            {
                CryptographicOperations.ZeroMemory(installKey);
            }
        }
    }

    public bool TryLoad(Switch2JoyConPairId pairId,
        out Switch2JoyConPairRecord record)
    {
        lock (sync)
        {
            return TryLoadNoLock(pairId, out record);
        }
    }

    public bool TryReplace(in Switch2JoyConPairRecord record,
        ulong expectedPriorRevision)
    {
        if (!record.IsValid || expectedPriorRevision == ulong.MaxValue ||
            (expectedPriorRevision == 0 ? record.Revision != 1 :
                record.Revision != expectedPriorRevision + 1))
        {
            return false;
        }

        lock (sync)
        {
            string path = PathFor(record.PairId);
            bool exists = File.Exists(path);
            if (expectedPriorRevision == 0)
            {
                if (exists)
                {
                    return false;
                }
            }
            else if (!exists || !TryLoadNoLock(record.PairId,
                         out Switch2JoyConPairRecord prior) ||
                     prior.Revision != expectedPriorRevision)
            {
                return false;
            }

            Span<byte> wire = stackalloc byte[
                Switch2JoyConPairRecord.EncodedLength];
            if (!record.TryWrite(wire))
            {
                return false;
            }
            return TryAtomicWrite(path, wire, overwrite: exists);
        }
    }

    public bool TryDelete(Switch2JoyConPairId pairId,
        ulong expectedRevision)
    {
        if (!pairId.IsValid || expectedRevision == 0)
        {
            return false;
        }
        lock (sync)
        {
            if (!TryLoadNoLock(pairId, out Switch2JoyConPairRecord record) ||
                record.Revision != expectedRevision)
            {
                return false;
            }
            try
            {
                File.Delete(PathFor(pairId));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryLoadAll(out Switch2JoyConPairRecord[] records)
    {
        lock (sync)
        {
            try
            {
                string[] paths = Directory.GetFiles(pairDirectory, "*.pair",
                    SearchOption.TopDirectoryOnly);
                Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
                var result = new List<Switch2JoyConPairRecord>(paths.Length);
                foreach (string path in paths)
                {
                    byte[] wire = File.ReadAllBytes(path);
                    if (!Switch2JoyConPairRecord.TryRead(wire,
                            out Switch2JoyConPairRecord record) ||
                        !string.Equals(path, PathFor(record.PairId),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        records = Array.Empty<Switch2JoyConPairRecord>();
                        return false;
                    }
                    result.Add(record);
                }
                records = result.ToArray();
                return true;
            }
            catch
            {
                records = Array.Empty<Switch2JoyConPairRecord>();
                return false;
            }
        }
    }

    private bool TryLoadNoLock(Switch2JoyConPairId pairId,
        out Switch2JoyConPairRecord record)
    {
        record = default;
        if (!pairId.IsValid)
        {
            return false;
        }
        try
        {
            string path = PathFor(pairId);
            if (!File.Exists(path))
            {
                return false;
            }
            byte[] wire = File.ReadAllBytes(path);
            return Switch2JoyConPairRecord.TryRead(wire, out record) &&
                record.PairId == pairId;
        }
        catch
        {
            record = default;
            return false;
        }
    }

    private string PathFor(Switch2JoyConPairId pairId)
    {
        Span<byte> id = stackalloc byte[Switch2JoyConPairId.EncodedLength];
        if (!pairId.TryWrite(id))
        {
            throw new ArgumentException("Invalid Joy-Con pair ID.",
                nameof(pairId));
        }
        return Path.Combine(pairDirectory, Convert.ToHexString(id) + ".pair");
    }

    private bool TryLoadOrCreateInstallKey(out byte[] installKey)
    {
        lock (sync)
        {
            if (File.Exists(keyPath))
            {
                return TryLoadProtectedInstallKey(out installKey);
            }

            installKey = new byte[Switch2PersistentPeerId.InstallKeyLength];
            RandomNumberGenerator.Fill(installKey);
            if (!TryProtect(installKey, out byte[] protectedKey))
            {
                CryptographicOperations.ZeroMemory(installKey);
                installKey = null;
                return false;
            }
            try
            {
                byte[] wire = new byte[KeyHeaderLength + protectedKey.Length];
                KeyMagic.CopyTo(wire);
                BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(4),
                    checked((uint)protectedKey.Length));
                protectedKey.CopyTo(wire, KeyHeaderLength);
                if (TryAtomicWrite(keyPath, wire, overwrite: false))
                {
                    return true;
                }

                // Another instance may have won creation. Never continue with
                // an unpersisted key that would make pair IDs unrecoverable.
                CryptographicOperations.ZeroMemory(installKey);
                installKey = null;
                return TryLoadProtectedInstallKey(out installKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }
    }

    private bool TryLoadProtectedInstallKey(out byte[] installKey)
    {
        installKey = null;
        try
        {
            byte[] wire = File.ReadAllBytes(keyPath);
            if (wire.Length <= KeyHeaderLength ||
                !wire.AsSpan(0, 4).SequenceEqual(KeyMagic))
            {
                return false;
            }
            uint protectedLength = BinaryPrimitives.ReadUInt32LittleEndian(
                wire.AsSpan(4, 4));
            if (protectedLength != wire.Length - KeyHeaderLength)
            {
                return false;
            }
            if (!TryUnprotect(wire.AsSpan(KeyHeaderLength), out installKey))
            {
                return false;
            }
            if (installKey.Length ==
                    Switch2PersistentPeerId.InstallKeyLength)
            {
                return true;
            }
            CryptographicOperations.ZeroMemory(installKey);
            installKey = null;
            return false;
        }
        catch
        {
            if (installKey != null)
            {
                CryptographicOperations.ZeroMemory(installKey);
                installKey = null;
            }
            return false;
        }
    }

    private static bool TryAtomicWrite(string path, ReadOnlySpan<byte> value,
        bool overwrite)
    {
        string directory = Path.GetDirectoryName(path);
        string temporary = Path.Combine(directory,
            "." + Path.GetFileName(path) + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
            }
        }
    }

    private static bool TryProtect(ReadOnlySpan<byte> clear,
        out byte[] protectedValue) => TryCrypt(clear, protect: true,
        out protectedValue);

    private static bool TryUnprotect(ReadOnlySpan<byte> protectedSource,
        out byte[] clear) => TryCrypt(protectedSource, protect: false,
        out clear);

    private static bool TryCrypt(ReadOnlySpan<byte> source, bool protect,
        out byte[] result)
    {
        result = null;
        IntPtr inputMemory = IntPtr.Zero;
        DataBlob output = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            byte[] detached = source.ToArray();
            try
            {
                inputMemory = Marshal.AllocHGlobal(detached.Length);
                Marshal.Copy(detached, 0, inputMemory, detached.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(detached);
            }
            var input = new DataBlob
            {
                ByteCount = source.Length,
                Data = inputMemory,
            };
            bool succeeded = protect ? CryptProtectData(ref input,
                    "DS4Windows Switch 2 peer identity", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden,
                    out output) :
                CryptUnprotectData(ref input, out description, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden,
                    out output);
            if (!succeeded || output.Data == IntPtr.Zero ||
                output.ByteCount <= 0 || output.ByteCount > 4096)
            {
                return false;
            }
            result = new byte[output.ByteCount];
            Marshal.Copy(output.Data, result, 0, output.ByteCount);
            return true;
        }
        catch
        {
            if (result != null)
            {
                CryptographicOperations.ZeroMemory(result);
                result = null;
            }
            return false;
        }
        finally
        {
            if (inputMemory != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputMemory);
            }
            if (output.Data != IntPtr.Zero)
            {
                _ = LocalFree(output.Data);
            }
            if (description != IntPtr.Zero)
            {
                _ = LocalFree(description);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int ByteCount;
        internal IntPtr Data;
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn,
        string description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn,
        out IntPtr description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
