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

internal enum Switch2BluetoothWindowsAssociationFailure : byte
{
    None = 0,
    InvalidObservation,
    StaleScan,
    AddressCapabilityUnavailable,
    Cancelled,
    StartupTimedOut,
    DeviceOpenFailed,
    DeviceDisconnected,
    ServiceQueryFailed,
    ServiceIdentityMismatch,
    CommandCharacteristicQueryFailed,
    CommandCharacteristicIdentityMismatch,
    ResponseCharacteristicQueryFailed,
    ResponseCharacteristicIdentityMismatch,
    CharacteristicPropertiesMismatch,
    ResponseSubscriptionFailed,
    CeremonyRejected,
    CeremonyFaulted,
    CleanupAmbiguous,
    PostCommitPromotionRejected,
    RuntimePreparationRejected,
    SlotActivationRejected,
}

internal readonly struct Switch2BluetoothWindowsAssociationResult
{
    private Switch2BluetoothWindowsAssociationResult(
        Switch2BluetoothWindowsAssociationFailure failure,
        Switch2BluetoothAssociationStep lastCompletedStep)
    {
        Failure = failure;
        LastCompletedStep = lastCompletedStep;
    }

    internal bool Succeeded => Failure ==
        Switch2BluetoothWindowsAssociationFailure.None;
    internal Switch2BluetoothWindowsAssociationFailure Failure { get; }
    internal Switch2BluetoothAssociationStep LastCompletedStep { get; }

    internal static Switch2BluetoothWindowsAssociationResult Success() => new(
        Switch2BluetoothWindowsAssociationFailure.None,
        Switch2BluetoothAssociationStep.Commit);

    // A remembered connection does not execute or commit association commands.
    internal static Switch2BluetoothWindowsAssociationResult Reconnected() => new(
        Switch2BluetoothWindowsAssociationFailure.None, default);

    internal static Switch2BluetoothWindowsAssociationResult Failed(
        Switch2BluetoothWindowsAssociationFailure failure,
        Switch2BluetoothAssociationStep lastCompletedStep = default) =>
        new(failure, lastCompletedStep);
}

/// <summary>
/// Owns one consumed BLE address capability from device open through response-
/// first command association and joined cleanup. It is deliberately separate
/// from the steady Common05 lease: a successful ceremony closes this temporary
/// link, after which the controller must re-advertise the selected host and
/// enter the ordinary remembered-peer path.
/// </summary>
internal static class Switch2BluetoothWindowsAssociationOwner
{
    internal static async ValueTask<
        Switch2BluetoothWindowsAssociationResult> ExecuteAsync(
        ISwitch2BluetoothWindowsPlatform platform, ulong bluetoothAddress,
        Switch2BluetoothWindowsAddressType addressType,
        ReadOnlyMemory<byte> localHostAddress, TimeSpan timeout,
        CancellationToken scanCancellationToken,
        CancellationToken callerCancellationToken)
    {
        if (platform == null || bluetoothAddress == 0 ||
            !Switch2BluetoothAssociationCodec.IsValidHostAddress(
                localHostAddress.Span))
        {
            return Switch2BluetoothWindowsAssociationResult.Failed(
                Switch2BluetoothWindowsAssociationFailure.InvalidObservation);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            scanCancellationToken, callerCancellationToken);
        deadline.CancelAfter(timeout);
        CancellationToken boundedToken = deadline.Token;
        ISwitch2BluetoothWindowsDevice device = null;
        ISwitch2BluetoothWindowsGattService service = null;
        ISwitch2BluetoothWindowsGattCharacteristic command = null;
        ISwitch2BluetoothWindowsGattCharacteristic response = null;
        Switch2BluetoothAssociationCommandChannel channel = null;
        bool channelPrepared = false;
        try
        {
            boundedToken.ThrowIfCancellationRequested();
            device = await AwaitBoundedAsync(platform.OpenDeviceAsync(
                bluetoothAddress, addressType, boundedToken), boundedToken,
                SafeDispose).ConfigureAwait(false);
            if (device == null)
            {
                return Failed(
                    Switch2BluetoothWindowsAssociationFailure.DeviceOpenFailed);
            }
            Task<Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService>> serviceTask =
                Switch2BluetoothServiceDiscovery.QueryAsync(device,
                    Switch2BluetoothAssociationCodec.ServiceUuid,
                    boundedToken).AsTask();
            Switch2BluetoothWindowsGattQuery<
                ISwitch2BluetoothWindowsGattService> serviceQuery;
            try
            {
                serviceQuery = await serviceTask.WaitAsync(boundedToken).
                    ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!serviceTask.IsCompleted)
            {
                ISwitch2BluetoothWindowsDevice retainedDevice = device;
                device = null;
                _ = DisposeLateResultAndOwnersAsync(serviceTask,
                    DisposeServiceQuery, retainedDevice);
                throw;
            }
            catch (OperationCanceledException)
            {
                DisposeCompletedResult(serviceTask, DisposeServiceQuery);
                throw;
            }
            if (!TryTakeSingleService(serviceQuery, out service))
            {
                return Failed(serviceQuery.Succeeded ?
                    Switch2BluetoothWindowsAssociationFailure.
                        ServiceIdentityMismatch :
                    Switch2BluetoothWindowsAssociationFailure.
                        ServiceQueryFailed);
            }
            boundedToken.ThrowIfCancellationRequested();
            // Opening a Windows BLE device object does not necessarily connect
            // it. Permit uncached discovery to initiate GATT, then validate the
            // link before acquiring endpoints or sending association commands.
            if (!SafeIsConnected(device))
            {
                return Failed(Switch2BluetoothWindowsAssociationFailure.
                    DeviceDisconnected);
            }

            CharacteristicQueryResult commandQuery = await
                QuerySingleCharacteristicAsync(service, device,
                    Switch2BluetoothAssociationCodec.
                        CommandWriteCharacteristicUuid,
                    boundedToken).ConfigureAwait(false);
            if (commandQuery.OwnershipTransferred)
            {
                service = null;
                device = null;
                return Failed(ClassifyCancellation(scanCancellationToken,
                    callerCancellationToken));
            }
            if (commandQuery.Failure !=
                Switch2BluetoothWindowsAssociationFailure.None)
            {
                return Failed(commandQuery.Failure);
            }
            command = commandQuery.Characteristic;

            CharacteristicQueryResult responseQuery = await
                QuerySingleCharacteristicAsync(service,
                device, Switch2BluetoothAssociationCodec.
                    CommandResponseCharacteristicUuid,
                boundedToken,
                command).ConfigureAwait(false);
            if (responseQuery.OwnershipTransferred)
            {
                command = null;
                service = null;
                device = null;
                return Failed(ClassifyCancellation(scanCancellationToken,
                    callerCancellationToken));
            }
            if (responseQuery.Failure !=
                Switch2BluetoothWindowsAssociationFailure.None)
            {
                return Failed(responseQuery.Failure ==
                    Switch2BluetoothWindowsAssociationFailure.
                        CommandCharacteristicQueryFailed ?
                    Switch2BluetoothWindowsAssociationFailure.
                        ResponseCharacteristicQueryFailed :
                    Switch2BluetoothWindowsAssociationFailure.
                        ResponseCharacteristicIdentityMismatch);
            }
            response = responseQuery.Characteristic;
            if (!SafeIsConnected(device))
            {
                return Failed(Switch2BluetoothWindowsAssociationFailure.
                    DeviceDisconnected);
            }

            try
            {
                channel = new Switch2BluetoothAssociationCommandChannel(command,
                    response);
            }
            catch (ArgumentException)
            {
                return Failed(Switch2BluetoothWindowsAssociationFailure.
                    CharacteristicPropertiesMismatch);
            }
            if (!await channel.PrepareAsync(boundedToken).ConfigureAwait(false))
            {
                return Failed(Switch2BluetoothWindowsAssociationFailure.
                    ResponseSubscriptionFailed);
            }
            channelPrepared = true;

            var transaction = new Switch2BluetoothAssociationTransaction(
                channel, timeout);
            Switch2BluetoothAssociationResult transactionResult = await
                transaction.ExecuteAsync(localHostAddress, boundedToken).
                ConfigureAwait(false);
            if (!transactionResult.Succeeded)
            {
                return Failed(transactionResult.Failure is
                    Switch2BluetoothAssociationFailure.ChannelRejected ?
                    Switch2BluetoothWindowsAssociationFailure.
                        CeremonyRejected :
                    Switch2BluetoothWindowsAssociationFailure.CeremonyFaulted,
                    transactionResult.LastCompletedStep);
            }

            // Cleanup is part of success. An ambiguous response callback/CCCD
            // lifetime must not be reported as a clean association.
            bool clean = await channel.RetireAsync(CancellationToken.None).
                ConfigureAwait(false);
            channelPrepared = false;
            return clean ? Switch2BluetoothWindowsAssociationResult.Success() :
                Failed(Switch2BluetoothWindowsAssociationFailure.
                    CleanupAmbiguous,
                    Switch2BluetoothAssociationStep.Commit);
        }
        catch (OperationCanceledException)
        {
            return Failed(callerCancellationToken.IsCancellationRequested ?
                Switch2BluetoothWindowsAssociationFailure.Cancelled :
                scanCancellationToken.IsCancellationRequested ?
                    Switch2BluetoothWindowsAssociationFailure.StaleScan :
                    Switch2BluetoothWindowsAssociationFailure.StartupTimedOut);
        }
        catch
        {
            return Failed(Switch2BluetoothWindowsAssociationFailure.
                CeremonyFaulted);
        }
        finally
        {
            if (channelPrepared && channel != null)
            {
                try
                {
                    await channel.RetireAsync(CancellationToken.None).
                        ConfigureAwait(false);
                }
                catch
                {
                }
            }
            SafeDispose(response);
            SafeDispose(command);
            SafeDispose(service);
            SafeDispose(device);
        }

        Switch2BluetoothWindowsAssociationResult Failed(
            Switch2BluetoothWindowsAssociationFailure failure,
            Switch2BluetoothAssociationStep lastCompletedStep = default) =>
            Switch2BluetoothWindowsAssociationResult.Failed(failure,
                lastCompletedStep);
    }

    private static async ValueTask<CharacteristicQueryResult>
        QuerySingleCharacteristicAsync(
            ISwitch2BluetoothWindowsGattService service,
            ISwitch2BluetoothWindowsDevice device, Guid uuid,
            CancellationToken cancellationToken,
            IDisposable additionalOwner = null)
    {
        Task<Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic>> task = service.
            GetCharacteristicsForUuidUncachedAsync(uuid, cancellationToken).
            AsTask();
        Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic> query;
        try
        {
            query = await task.WaitAsync(cancellationToken).
                ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            _ = DisposeLateResultAndOwnersAsync(task,
                DisposeCharacteristicQuery, additionalOwner, service, device);
            return CharacteristicQueryResult.Transferred();
        }
        catch (OperationCanceledException)
        {
            DisposeCompletedResult(task, DisposeCharacteristicQuery);
            throw;
        }
        if (!query.Succeeded || query.Items == null)
        {
            DisposeCharacteristicQuery(query);
            return CharacteristicQueryResult.Failed(
                Switch2BluetoothWindowsAssociationFailure.
                    CommandCharacteristicQueryFailed);
        }
        if (query.Items.Count != 1 || query.Items[0] == null ||
            query.Items[0].Uuid != uuid)
        {
            DisposeCharacteristicQuery(query);
            return CharacteristicQueryResult.Failed(
                Switch2BluetoothWindowsAssociationFailure.
                    CommandCharacteristicIdentityMismatch);
        }
        return CharacteristicQueryResult.Success(query.Items[0]);
    }

    private static Switch2BluetoothWindowsAssociationFailure
        ClassifyCancellation(CancellationToken scanCancellationToken,
            CancellationToken callerCancellationToken) =>
        callerCancellationToken.IsCancellationRequested ?
            Switch2BluetoothWindowsAssociationFailure.Cancelled :
            scanCancellationToken.IsCancellationRequested ?
                Switch2BluetoothWindowsAssociationFailure.StaleScan :
                Switch2BluetoothWindowsAssociationFailure.StartupTimedOut;

    private readonly struct CharacteristicQueryResult
    {
        private CharacteristicQueryResult(
            Switch2BluetoothWindowsAssociationFailure failure,
            ISwitch2BluetoothWindowsGattCharacteristic characteristic,
            bool ownershipTransferred)
        {
            Failure = failure;
            Characteristic = characteristic;
            OwnershipTransferred = ownershipTransferred;
        }

        internal Switch2BluetoothWindowsAssociationFailure Failure { get; }
        internal ISwitch2BluetoothWindowsGattCharacteristic Characteristic
            { get; }
        internal bool OwnershipTransferred { get; }

        internal static CharacteristicQueryResult Success(
            ISwitch2BluetoothWindowsGattCharacteristic characteristic) =>
            new(Switch2BluetoothWindowsAssociationFailure.None,
                characteristic, false);
        internal static CharacteristicQueryResult Failed(
            Switch2BluetoothWindowsAssociationFailure failure) =>
            new(failure, null, false);
        internal static CharacteristicQueryResult Transferred() => new(
            Switch2BluetoothWindowsAssociationFailure.StartupTimedOut, null,
            true);
    }

    private static bool TryTakeSingleService(
        Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService> query,
        out ISwitch2BluetoothWindowsGattService service)
    {
        if (!query.Succeeded || query.Items == null ||
            query.Items.Count != 1 || query.Items[0] == null ||
            query.Items[0].Uuid !=
                Switch2BluetoothAssociationCodec.ServiceUuid)
        {
            DisposeServiceQuery(query);
            service = null;
            return false;
        }
        service = query.Items[0];
        return true;
    }

    private static async ValueTask<T> AwaitBoundedAsync<T>(
        ValueTask<T> operation, CancellationToken cancellationToken,
        Action<T> disposeLateResult)
    {
        Task<T> task = operation.AsTask();
        try
        {
            return await task.WaitAsync(cancellationToken).
                ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            _ = DisposeLateResultAsync(task, disposeLateResult);
            throw;
        }
        catch (OperationCanceledException)
        {
            DisposeCompletedResult(task, disposeLateResult);
            throw;
        }
    }

    private static async Task DisposeLateResultAsync<T>(Task<T> task,
        Action<T> disposeResult)
    {
        try
        {
            disposeResult(await task.ConfigureAwait(false));
        }
        catch
        {
        }
    }

    private static async Task DisposeLateResultAndOwnersAsync<T>(Task<T> task,
        Action<T> disposeResult, params IDisposable[] owners)
    {
        try
        {
            disposeResult(await task.ConfigureAwait(false));
        }
        catch
        {
        }
        finally
        {
            foreach (IDisposable owner in owners)
            {
                SafeDispose(owner);
            }
        }
    }

    private static void DisposeCompletedResult<T>(Task<T> task,
        Action<T> disposeResult)
    {
        if (task.Status == TaskStatus.RanToCompletion)
        {
            try
            {
                disposeResult(task.Result);
            }
            catch
            {
            }
        }
    }

    private static void DisposeServiceQuery(
        Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattService> query)
    {
        if (query.Items == null)
        {
            return;
        }
        foreach (ISwitch2BluetoothWindowsGattService item in query.Items)
        {
            SafeDispose(item);
        }
    }

    private static void DisposeCharacteristicQuery(
        Switch2BluetoothWindowsGattQuery<
            ISwitch2BluetoothWindowsGattCharacteristic> query)
    {
        if (query.Items == null)
        {
            return;
        }
        foreach (ISwitch2BluetoothWindowsGattCharacteristic item in query.Items)
        {
            SafeDispose(item);
        }
    }

    private static bool SafeIsConnected(
        ISwitch2BluetoothWindowsDevice device)
    {
        try
        {
            return device.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    private static void SafeDispose(IDisposable value)
    {
        try
        {
            value?.Dispose();
        }
        catch
        {
        }
    }
}
