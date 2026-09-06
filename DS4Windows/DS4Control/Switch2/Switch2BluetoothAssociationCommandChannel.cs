/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

/// <summary>
/// Sole serialized owner of the BLE command-write/command-response pair used
/// during controller-side association. Prepare subscribes the response edge
/// before any request can be admitted. Any cancellation or ambiguous platform
/// failure terminally retires the channel; no request is replayed.
/// </summary>
internal sealed class Switch2BluetoothAssociationCommandChannel :
    ISwitch2BluetoothAssociationCommandChannel
{
    private readonly object sync = new();
    private readonly ISwitch2BluetoothWindowsGattCharacteristic command;
    private readonly ISwitch2BluetoothWindowsGattCharacteristic response;
    private readonly bool writeWithoutResponse;
    private readonly Switch2BluetoothWindowsValueChangedHandler valueChanged;
    private TaskCompletionSource<bool> pending;
    private bool attachAttempted;
    private bool prepared;
    private bool terminal;

    internal Switch2BluetoothAssociationCommandChannel(
        ISwitch2BluetoothWindowsGattCharacteristic command,
        ISwitch2BluetoothWindowsGattCharacteristic response)
    {
        this.command = command ?? throw new ArgumentNullException(
            nameof(command));
        this.response = response ?? throw new ArgumentNullException(
            nameof(response));

        Switch2GattProperty commandProperties = command.EvidencedProperties;
        bool hasWrite = (commandProperties & Switch2GattProperty.Write) != 0;
        bool hasWriteWithoutResponse = (commandProperties &
            Switch2GattProperty.WriteWithoutResponse) != 0;
        if (command.Uuid != Switch2BluetoothAssociationCodec.
                CommandWriteCharacteristicUuid ||
            (!hasWrite && !hasWriteWithoutResponse) ||
            (commandProperties & Switch2GattProperty.Notify) != 0)
        {
            throw new ArgumentException(
                "The command characteristic is not an evidenced write edge.",
                nameof(command));
        }
        if (response.Uuid != Switch2BluetoothAssociationCodec.
                CommandResponseCharacteristicUuid ||
            (response.EvidencedProperties & Switch2GattProperty.Notify) == 0 ||
            (response.EvidencedProperties & (Switch2GattProperty.Write |
                Switch2GattProperty.WriteWithoutResponse)) != 0)
        {
            throw new ArgumentException(
                "The response characteristic is not an evidenced notify edge.",
                nameof(response));
        }

        // Prefer an acknowledged ATT write when both forms are advertised.
        writeWithoutResponse = !hasWrite && hasWriteWithoutResponse;
        valueChanged = OnValueChanged;
    }

    internal async ValueTask<bool> PrepareAsync(
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (terminal || prepared || attachAttempted)
            {
                return false;
            }
            attachAttempted = true;
        }

        try
        {
            response.AttachValueChangedHandler(valueChanged);
            if (!await response.ConfigureNotificationsAsync(true,
                    cancellationToken).ConfigureAwait(false))
            {
                await RetireAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            lock (sync)
            {
                if (terminal)
                {
                    return false;
                }
                prepared = true;
                return true;
            }
        }
        catch
        {
            await RetireAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> ExchangeAsync(ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        lock (sync)
        {
            if (!prepared || terminal || pending != null ||
                request.Length is < Switch2BluetoothAssociationCodec.
                    CommitRequestLength or
                    > Switch2BluetoothAssociationCodec.MaximumRequestLength)
            {
                return false;
            }
            completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending = completion;
        }

        try
        {
            bool written = await command.WriteValueAsync(request,
                writeWithoutResponse, cancellationToken).ConfigureAwait(false);
            if (!written)
            {
                RetirePending(completion, accepted: false);
                return false;
            }

            return await completion.Task.WaitAsync(cancellationToken).
                ConfigureAwait(false);
        }
        catch
        {
            RetirePending(completion, accepted: false);
            throw;
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(pending, completion))
                {
                    pending = null;
                }
            }
        }
    }

    internal async ValueTask<bool> RetireAsync(
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        bool detach;
        lock (sync)
        {
            terminal = true;
            prepared = false;
            completion = pending;
            pending = null;
            detach = attachAttempted;
            attachAttempted = false;
        }
        completion?.TrySetResult(false);

        bool clean = true;
        if (detach)
        {
            try
            {
                await response.DetachValueChangedHandlerAndDrainAsync().
                    WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                clean = false;
            }
            try
            {
                clean &= await response.ConfigureNotificationsAsync(false,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                clean = false;
            }
        }
        return clean;
    }

    private void OnValueChanged(ReadOnlySpan<byte> value, long completionQpc)
    {
        TaskCompletionSource<bool> completion;
        bool accepted = Switch2BluetoothAssociationCodec.TryValidateResponse(
            value, out _);
        lock (sync)
        {
            if (!prepared || terminal || pending == null)
            {
                return;
            }
            completion = pending;
            pending = null;
            if (!accepted)
            {
                terminal = true;
                prepared = false;
            }
        }
        completion.TrySetResult(accepted);
    }

    private void RetirePending(TaskCompletionSource<bool> completion,
        bool accepted)
    {
        lock (sync)
        {
            if (ReferenceEquals(pending, completion))
            {
                pending = null;
            }
            terminal = true;
            prepared = false;
        }
        completion.TrySetResult(accepted);
    }
}
