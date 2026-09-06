/*
DS4Windows
Copyright (C) 2026 hbashton
SPDX-License-Identifier: GPL-3.0-or-later
*/

using System.Threading;

namespace DS4Windows.Switch2;

internal enum Switch2BluetoothDiscoveryState : byte
{
    Stopped = 0,
    Starting,
    Scanning,
    Unavailable,
    StartFailed,
    Interrupted,
    Stopping,
    CleanupFailed,
}

/// <summary>
/// Immutable control-plane status. No addresses, keys, or report data are
/// exposed. A fresh Starting instance identifies one host-discovery attempt.
/// </summary>
internal sealed class Switch2BluetoothDiscoveryStatus
{
    internal static readonly Switch2BluetoothDiscoveryStatus Stopped =
        new(Switch2BluetoothDiscoveryState.Stopped);

    internal Switch2BluetoothDiscoveryStatus(Switch2BluetoothDiscoveryState state,
        Switch2BluetoothWindowsScanStartFailure failure =
            Switch2BluetoothWindowsScanStartFailure.None)
    {
        State = state;
        Failure = failure;
    }

    internal Switch2BluetoothDiscoveryState State { get; }
    internal Switch2BluetoothWindowsScanStartFailure Failure { get; }
    internal bool CanAssociate => State == Switch2BluetoothDiscoveryState.Scanning;
}

/// <summary>Publishes host-lookup status without blocking the UI on WinRT.</summary>
internal sealed class Switch2BluetoothDiscoveryStartupState
{
    private Switch2BluetoothDiscoveryStatus current = Switch2BluetoothDiscoveryStatus.Stopped;

    internal Switch2BluetoothDiscoveryStatus Snapshot => Volatile.Read(ref current);

    internal Switch2BluetoothDiscoveryStatus Begin()
    {
        var starting = new Switch2BluetoothDiscoveryStatus(Switch2BluetoothDiscoveryState.Starting);
        Volatile.Write(ref current, starting);
        return starting;
    }

    internal bool TryComplete(Switch2BluetoothDiscoveryStatus exactStarting,
        Switch2BluetoothDiscoveryState state) => ReferenceEquals(
        Interlocked.CompareExchange(ref current, new(state), exactStarting), exactStarting);

    internal void Set(Switch2BluetoothDiscoveryState state) =>
        Volatile.Write(ref current, new(state));
}
