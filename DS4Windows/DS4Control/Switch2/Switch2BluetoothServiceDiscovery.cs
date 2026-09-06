/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

// Startup only. Reuse the existing device and overall deadline; do not reopen
// an address, renew candidate authority or add a timer to input publication.
internal static class Switch2BluetoothServiceDiscovery
{
    internal const int MaximumAttempts = 10;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    internal static async ValueTask<Switch2BluetoothWindowsGattQuery<
        ISwitch2BluetoothWindowsGattService>> QueryAsync(
        ISwitch2BluetoothWindowsDevice device, Guid serviceUuid,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        for (int attempt = 1; ; ++attempt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await device.GetServicesForUuidUncachedAsync(serviceUuid,
                cancellationToken).ConfigureAwait(false);
            if (attempt >= MaximumAttempts || !CanRetry(result))
            {
                // Even after cancellation, return an owned final result to the
                // caller's existing late-result cleanup instead of losing it.
                return result;
            }

            // Retire this attempt's temporary services before requesting more.
            // The caller retains the device until this entire operation ends.
            Exception disposalFailure = null;
            if (result.Items != null)
            {
                foreach (var service in result.Items)
                {
                    try { service?.Dispose(); }
                    catch (Exception error) { disposalFailure ??= error; }
                }
            }
            if (disposalFailure != null)
                ExceptionDispatchInfo.Capture(disposalFailure).Throw();

            await (delay == null ? Task.Delay(RetryDelay, cancellationToken) :
                delay(RetryDelay, cancellationToken)).ConfigureAwait(false);
        }
    }

    private static bool CanRetry(Switch2BluetoothWindowsGattQuery<
        ISwitch2BluetoothWindowsGattService> result) =>
        result.Status == Switch2BluetoothWindowsGattQueryStatus.Unreachable ||
        (result.Succeeded && result.Items is { Count: 0 });
}
