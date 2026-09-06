/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Security.Cryptography;

namespace DS4Windows.Switch2;

internal interface ISwitch2PersistentPeerIdentityDeriver
{
    bool TryDerive(ISwitch2BluetoothWindowsDevice device,
        Switch2ControllerModel model, ushort productId,
        out Switch2PersistentPeerId peerId);
}

/// <summary>
/// Trusted boundary that immediately converts Windows' stable DeviceId into an
/// install-local HMAC pseudonym. The raw OS identity exists only in stack
/// memory, is zeroed before return, and never crosses into discovery, UI, logs,
/// pair records, or runtime registrations.
/// </summary>
internal sealed class Switch2PersistentPeerIdentityDeriver :
    ISwitch2PersistentPeerIdentityDeriver, IDisposable
{
    private readonly object sync = new();
    private byte[] installKey;

    internal Switch2PersistentPeerIdentityDeriver(
        ReadOnlySpan<byte> installKey)
    {
        if (installKey.Length != Switch2PersistentPeerId.InstallKeyLength)
        {
            throw new ArgumentException("Invalid install-key length.",
                nameof(installKey));
        }
        this.installKey = installKey.ToArray();
    }

    public bool TryDerive(ISwitch2BluetoothWindowsDevice device,
        Switch2ControllerModel model, ushort productId,
        out Switch2PersistentPeerId peerId)
    {
        peerId = default;
        if (device == null)
        {
            return false;
        }

        Span<byte> identity = stackalloc byte[
            Switch2PersistentPeerId.MaximumOsIdentityLength];
        try
        {
            lock (sync)
            {
                return installKey != null &&
                    device.TryCopyStableAssociationIdentity(identity,
                        out int length) && length > 0 &&
                    Switch2PersistentPeerId.TryDerive(installKey,
                        identity[..length], model, productId, out peerId);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identity);
        }
    }

    internal bool TryDerive(
        in Switch2PhysicalContainerIdentity containerIdentity,
        Switch2ControllerModel model, ushort productId,
        out Switch2PersistentPeerId peerId)
    {
        peerId = default;
        Span<byte> identity = stackalloc byte[16];
        try
        {
            lock (sync)
            {
                return installKey != null &&
                    containerIdentity.TryCopyPseudonymInput(identity,
                        out int length) && length == identity.Length &&
                    Switch2PersistentPeerId.TryDerive(installKey, identity,
                        model, productId, out peerId);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identity);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (installKey != null)
            {
                CryptographicOperations.ZeroMemory(installKey);
                installKey = null;
            }
        }
    }
}
