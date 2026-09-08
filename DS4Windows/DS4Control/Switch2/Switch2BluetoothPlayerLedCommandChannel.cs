/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace DS4Windows.Switch2;

internal enum Switch2BluetoothPlayerLedChannelFailure : byte
{
    None = 0,
    InvalidPlayerNumber,
    NotPrepared,
    Busy,
    WriteRejected,
    ResponseRejected,
    Cancelled,
    DependencyThrew,
    Retired,
}

internal readonly struct Switch2BluetoothPlayerLedChannelResult
{
    private Switch2BluetoothPlayerLedChannelResult(
        Switch2BluetoothPlayerLedChannelFailure failure)
    {
        Failure = failure;
    }

    internal Switch2BluetoothPlayerLedChannelFailure Failure { get; }

    internal bool Succeeded => Failure ==
        Switch2BluetoothPlayerLedChannelFailure.None;

    internal static Switch2BluetoothPlayerLedChannelResult Success() =>
        new(Switch2BluetoothPlayerLedChannelFailure.None);

    internal static Switch2BluetoothPlayerLedChannelResult Failed(
        Switch2BluetoothPlayerLedChannelFailure failure) => new(failure);
}

internal enum Switch2BluetoothMemoryReadChannelFailure : byte
{
    None = 0,
    InvalidArgument,
    NotPrepared,
    Busy,
    WriteRejected,
    ResponseRejected,
    Cancelled,
    DependencyThrew,
    Retired,
}

internal readonly struct Switch2BluetoothMemoryReadChannelResult
{
    private Switch2BluetoothMemoryReadChannelResult(
        Switch2BluetoothMemoryReadChannelFailure failure, byte[] value)
    {
        Failure = failure;
        Value = value ?? Array.Empty<byte>();
    }

    internal Switch2BluetoothMemoryReadChannelFailure Failure { get; }

    internal ReadOnlyMemory<byte> Value { get; }

    internal bool Succeeded => Failure ==
        Switch2BluetoothMemoryReadChannelFailure.None;

    internal static Switch2BluetoothMemoryReadChannelResult Success(
        byte[] value) => new(Switch2BluetoothMemoryReadChannelFailure.None,
            value);

    internal static Switch2BluetoothMemoryReadChannelResult Failed(
        Switch2BluetoothMemoryReadChannelFailure failure) => new(failure,
            null);
}

/// <summary>
/// Sole serialized owner of the persistent BLE command/response pair used for
/// sensor startup, bounded memory reads and player LEDs. Response Notify is subscribed before
/// publication. A rejected, cancelled, or ambiguous exchange terminally fences
/// the channel; retirement waits for an admitted write before the lease may
/// dispose either endpoint.
/// </summary>
internal sealed class Switch2BluetoothPlayerLedCommandChannel
{
    private readonly object sync = new();
    private readonly ISwitch2BluetoothWindowsGattCharacteristic command;
    private readonly ISwitch2BluetoothWindowsGattCharacteristic response;
    private readonly bool writeWithoutResponse;
    private readonly Switch2BluetoothWindowsValueChangedHandler valueChanged;
    private TaskCompletionSource<byte[]> pendingResponse;
    private byte pendingCommandId;
    private byte pendingProFeatureSubcommand;
    private bool pendingLabReceiverResponse;
    private TaskCompletionSource<bool> operationsDrained = CompletedDrain();
    private bool attachAttempted;
    private bool prepared;
    private bool terminal;
    private int activeOperations;
    private Task retainedCommandWriteDrain = Task.CompletedTask;

    internal Switch2BluetoothPlayerLedCommandChannel(
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
        if (command.Uuid != Switch2BluetoothPlayerLedCodec.
                CommandWriteCharacteristicUuid ||
            (!hasWrite && !hasWriteWithoutResponse) ||
            (commandProperties & (Switch2GattProperty.Read |
                Switch2GattProperty.Notify)) != 0)
        {
            throw new ArgumentException(
                "The LED command characteristic is not an evidenced write edge.",
                nameof(command));
        }
        Switch2GattProperty responseProperties = response.EvidencedProperties;
        if (response.Uuid != Switch2BluetoothPlayerLedCodec.
                CommandResponseCharacteristicUuid ||
            (responseProperties & Switch2GattProperty.Notify) == 0 ||
            (responseProperties & (Switch2GattProperty.Write |
                Switch2GattProperty.WriteWithoutResponse)) != 0)
        {
            throw new ArgumentException(
                "The LED response characteristic is not an evidenced notify edge.",
                nameof(response));
        }

        // Prefer the ATT-acknowledged write when the characteristic offers it.
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

    internal async ValueTask<Switch2BluetoothPlayerLedChannelResult>
        SetPlayerAsync(byte playerNumber, CancellationToken cancellationToken)
    {
        if (!Switch2BluetoothPlayerLedCodec.TryGetPattern(playerNumber,
                out byte pattern))
        {
            return Switch2BluetoothPlayerLedChannelResult.Failed(
                Switch2BluetoothPlayerLedChannelFailure.InvalidPlayerNumber);
        }
        return await SetPatternAsync(pattern, cancellationToken).
            ConfigureAwait(false);
    }

    internal async ValueTask<Switch2BluetoothPlayerLedChannelResult>
        SetPatternAsync(byte pattern, CancellationToken cancellationToken)
    {
        if ((pattern & 0xF0) != 0)
        {
            return Switch2BluetoothPlayerLedChannelResult.Failed(
                Switch2BluetoothPlayerLedChannelFailure.InvalidPlayerNumber);
        }

        TaskCompletionSource<byte[]> responseCompletion;
        lock (sync)
        {
            if (terminal)
            {
                return Switch2BluetoothPlayerLedChannelResult.Failed(
                    Switch2BluetoothPlayerLedChannelFailure.Retired);
            }
            if (!prepared)
            {
                return Switch2BluetoothPlayerLedChannelResult.Failed(
                    Switch2BluetoothPlayerLedChannelFailure.NotPrepared);
            }
            if (pendingResponse != null || activeOperations != 0)
            {
                return Switch2BluetoothPlayerLedChannelResult.Failed(
                    Switch2BluetoothPlayerLedChannelFailure.Busy);
            }

            responseCompletion = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingResponse = responseCompletion;
            pendingCommandId = Switch2BluetoothPlayerLedCodec.CommandId;
            activeOperations = 1;
            operationsDrained = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        byte[] request = new byte[Switch2BluetoothPlayerLedCodec.RequestLength];
        if (!Switch2BluetoothPlayerLedCodec.TryWritePatternRequest(pattern,
                request, out _))
        {
            CompleteOperation(responseCompletion, terminalFailure: true);
            return Switch2BluetoothPlayerLedChannelResult.Failed(
                Switch2BluetoothPlayerLedChannelFailure.InvalidPlayerNumber);
        }

        try
        {
            bool written = await command.WriteValueAsync(request,
                writeWithoutResponse, cancellationToken).ConfigureAwait(false);
            if (!written)
            {
                CompleteOperation(responseCompletion, terminalFailure: true);
                return Switch2BluetoothPlayerLedChannelResult.Failed(
                    Switch2BluetoothPlayerLedChannelFailure.WriteRejected);
            }

            byte[] responseValue = await responseCompletion.Task.
                WaitAsync(cancellationToken).ConfigureAwait(false);
            bool accepted = responseValue != null &&
                Switch2BluetoothPlayerLedCodec.TryValidateResponse(
                    responseValue, out _);
            if (!accepted)
            {
                CompleteOperation(responseCompletion, terminalFailure: true);
            }
            return accepted ? Switch2BluetoothPlayerLedChannelResult.Success() :
                Switch2BluetoothPlayerLedChannelResult.Failed(
                    Switch2BluetoothPlayerLedChannelFailure.ResponseRejected);
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(responseCompletion, terminalFailure: true);
            return Switch2BluetoothPlayerLedChannelResult.Failed(
                Switch2BluetoothPlayerLedChannelFailure.Cancelled);
        }
        catch
        {
            CompleteOperation(responseCompletion, terminalFailure: true);
            return Switch2BluetoothPlayerLedChannelResult.Failed(
                Switch2BluetoothPlayerLedChannelFailure.DependencyThrew);
        }
        finally
        {
            CompleteOperation(responseCompletion, terminalFailure: false);
        }
    }

    /// <summary>
    /// Holds the same command ownership across both acknowledged sensor
    /// startup steps. LEDs and calibration cannot interleave, and any
    /// ambiguous exchange fences the channel before a successor may write.
    /// Called before input publication, never from the report hot path.
    /// </summary>
    internal ValueTask<Switch2BluetoothSensorInitializationFailure>
        InitializeJoyConSensorsAsync(CancellationToken cancellationToken) =>
        InitializeFeaturesAsync(proFeatures: false, cancellationToken);

    internal ValueTask<Switch2BluetoothSensorInitializationFailure>
        InitializeProFeaturesAsync(CancellationToken cancellationToken) =>
        InitializeFeaturesAsync(proFeatures: true, cancellationToken);

    private async ValueTask<Switch2BluetoothSensorInitializationFailure>
        InitializeFeaturesAsync(bool proFeatures, CancellationToken cancellationToken)
    {
        TaskCompletionSource<byte[]> completion;
        lock (sync)
        {
            if (terminal)
                return Switch2BluetoothSensorInitializationFailure.Retired;
            if (!prepared)
                return Switch2BluetoothSensorInitializationFailure.NotPrepared;
            if (pendingResponse != null || activeOperations != 0)
                return Switch2BluetoothSensorInitializationFailure.Busy;

            completion = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingResponse = completion;
            pendingCommandId = Switch2BluetoothSensorCodec.CommandId;
            pendingProFeatureSubcommand = proFeatures ? (byte)0x02 : (byte)0;
            activeOperations = 1;
            operationsDrained = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        bool terminalFailure = true;
        Task<bool> retainedWrite = null;
        try
        {
            for (int step = 0; step < 2; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step != 0)
                {
                    lock (sync)
                    {
                        if (terminal)
                            return Switch2BluetoothSensorInitializationFailure.Retired;
                        completion = new TaskCompletionSource<byte[]>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        pendingResponse = completion;
                        pendingCommandId = Switch2BluetoothSensorCodec.CommandId;
                        pendingProFeatureSubcommand = proFeatures ? (byte)0x04 : (byte)0;
                    }
                }
                byte[] request = proFeatures ? Switch2BluetoothProFeatureCodec.CreateRequest(enable: step == 1) :
                    Switch2BluetoothSensorCodec.CreateRequest(enable: step == 1);
                bool written;
                if (proFeatures)
                {
                    // Cancel the wait, never the admitted Windows operation.
                    // The finally below retains ownership until actual completion.
                    retainedWrite = command.WriteValueAsync(request, writeWithoutResponse,
                        CancellationToken.None).AsTask();
                    written = await retainedWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                else
                {
                    // Preserve the existing Joy-Con transport and reply contract.
                    written = await command.WriteValueAsync(request, writeWithoutResponse,
                        cancellationToken).ConfigureAwait(false);
                }
                if (!written)
                    return Switch2BluetoothSensorInitializationFailure.WriteRejected;

                byte[] value = await completion.Task.WaitAsync(cancellationToken).
                    ConfigureAwait(false);
                if (proFeatures) cancellationToken.ThrowIfCancellationRequested();
                if (value == null || !(proFeatures ? Switch2BluetoothProFeatureCodec.IsAccepted(value, enable: step == 1) :
                        Switch2BluetoothSensorCodec.IsAccepted(value)))
                    return Switch2BluetoothSensorInitializationFailure.ResponseRejected;
            }
            terminalFailure = false;
            return Switch2BluetoothSensorInitializationFailure.None;
        }
        catch (OperationCanceledException)
        {
            return Switch2BluetoothSensorInitializationFailure.Cancelled;
        }
        catch
        {
            return Switch2BluetoothSensorInitializationFailure.DependencyThrew;
        }
        finally
        {
            await CompleteOrRetainWriteAsync(retainedWrite, completion, terminalFailure).ConfigureAwait(false);
        }
    }

    private async Task CompleteOrRetainWriteAsync(Task<bool> retainedWrite,
        TaskCompletionSource<byte[]> completion, bool terminalFailure, bool awaitDrain = false)
    {
        if (retainedWrite is { IsCompleted: false })
        {
            // Fence immediately; only actual write completion may release
            // operation ownership. Startup returns promptly, while lab windows
            // retain the inner operation through their cleanup boundary.
            lock (sync)
            {
                terminal = true;
                prepared = false;
                if (ReferenceEquals(pendingResponse, completion))
                {
                    pendingResponse = null;
                    pendingCommandId = 0;
                    pendingProFeatureSubcommand = 0;
                    pendingLabReceiverResponse = false;
                }
            }
            completion.TrySetResult(null);
            retainedCommandWriteDrain = DrainRetainedCommandWriteAsync(retainedWrite, completion);
            if (awaitDrain) await retainedCommandWriteDrain.ConfigureAwait(false);
        }
        else
        {
            if (retainedWrite != null) try { await retainedWrite.ConfigureAwait(false); } catch { }
            CompleteOperation(completion, terminalFailure);
        }
    }

    private async Task DrainRetainedCommandWriteAsync(Task<bool> write, TaskCompletionSource<byte[]> completion)
    {
        try { await write.ConfigureAwait(false); } catch { }
        finally { CompleteOperation(completion, terminalFailure: true); }
    }

    // Only the explicitly enabled portable-lab lease invokes this method.
    // Full validation and immutable request snapshots precede all writes;
    // the one existing command owner remains reserved for the entire plan.
    internal async Task<string> RunLabReceiverCommandsAsync(Switch2BluetoothLabReceiverPlan plan,
        CancellationToken cancellationToken)
    {
        var responses = new List<object>();
        bool setupAcknowledged = false;
        string Result(string error = null) => error == null
            ? JsonSerializer.Serialize(new { CommandsAcknowledged = true, SetupAcknowledged = setupAcknowledged,
                Responses = responses, BluetoothPlaybackConfirmed = false })
            : JsonSerializer.Serialize(new { CommandsAcknowledged = false, SetupAcknowledged = setupAcknowledged,
                Responses = responses, Error = error, BluetoothPlaybackConfirmed = false });

        byte[][] requests;
        try
        {
            if (plan == null) return Result("Invalid receiver plan.");
            plan.Validate();
            requests = Array.ConvertAll(plan.Commands, request => (byte[])request.Clone());
            new Switch2BluetoothLabReceiverPlan { Id = plan.Id, Generation = plan.Generation,
                HeadsetNotifications = plan.HeadsetNotifications, AllowExperimentalParameters = plan.AllowExperimentalParameters,
                Commands = requests, Audio = plan.Audio }.Validate();
        }
        catch (Exception error) { return Result("Invalid receiver plan: " + error.GetType().Name); }

        TaskCompletionSource<byte[]> completion;
        lock (sync)
        {
            if (terminal || !prepared) return Result("Command channel unavailable.");
            if (pendingResponse != null || activeOperations != 0) return Result("Command lane busy; no receiver commands sent.");
            completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingResponse = completion;
            pendingCommandId = requests[0][0];
            pendingProFeatureSubcommand = requests[0][0] == 0x0C ? requests[0][3] : (byte)0;
            pendingLabReceiverResponse = true;
            activeOperations = 1;
            operationsDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        bool terminalFailure = true;
        Task<bool> retainedWrite = null;
        byte[] currentRequest = null;
        bool currentRecorded = false;
        void RecordAttempt(byte[] reply, bool acknowledged)
        {
            responses.Add(new { Request = Convert.ToHexString(currentRequest),
                Response = reply == null ? "" : Convert.ToHexString(reply), Acknowledged = acknowledged });
            currentRecorded = true;
        }
        void RecordUncompletedAttempt()
        {
            if (currentRequest == null || currentRecorded) return;
            // Preserve an already captured bounded reply even if the Windows
            // write is still unresolved; the exchange itself is not accepted.
            RecordAttempt(completion.Task.IsCompletedSuccessfully ? completion.Task.Result : null, false);
        }
        try
        {
            for (int index = 0; index < requests.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] request = requests[index];
                if (index != 0)
                {
                    lock (sync)
                    {
                        if (terminal) return Result("Command channel retired.");
                        completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                        pendingResponse = completion;
                        pendingCommandId = request[0];
                        pendingProFeatureSubcommand = request[0] == 0x0C ? request[3] : (byte)0;
                        pendingLabReceiverResponse = true;
                    }
                }
                currentRequest = request;
                currentRecorded = false;
                retainedWrite = command.WriteValueAsync(request, writeWithoutResponse, CancellationToken.None).AsTask();
                bool written = await retainedWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!written)
                {
                    RecordUncompletedAttempt();
                    return Result("Receiver command write rejected.");
                }
                byte[] reply = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                bool acknowledged = reply != null && Switch2BluetoothLabReceiverPlan.IsAcknowledged(request, reply);
                RecordAttempt(reply, acknowledged);
                if (!acknowledged) return Result("Receiver command response rejected.");
                setupAcknowledged |= request[0] == 0x17;
            }
            terminalFailure = false;
            return Result();
        }
        catch (OperationCanceledException) { RecordUncompletedAttempt(); return Result("Receiver command deadline; command lane fenced."); }
        catch (Exception error) { RecordUncompletedAttempt(); return Result("Receiver command failed: " + error.GetType().Name); }
        finally
        {
            await CompleteOrRetainWriteAsync(retainedWrite, completion, terminalFailure, awaitDrain: true).ConfigureAwait(false);
        }
    }

    internal async ValueTask<Switch2BluetoothMemoryReadChannelResult>
        ReadMemoryAsync(byte length, uint address,
            CancellationToken cancellationToken)
    {
        byte[] request = new byte[Switch2BluetoothMemoryReadCodec.
            RequestLength];
        if (!Switch2BluetoothMemoryReadCodec.TryWriteRequest(length, address,
                request, out _))
        {
            return Switch2BluetoothMemoryReadChannelResult.Failed(
                Switch2BluetoothMemoryReadChannelFailure.InvalidArgument);
        }

        TaskCompletionSource<byte[]> responseCompletion;
        lock (sync)
        {
            if (terminal)
            {
                return Switch2BluetoothMemoryReadChannelResult.Failed(
                    Switch2BluetoothMemoryReadChannelFailure.Retired);
            }
            if (!prepared)
            {
                return Switch2BluetoothMemoryReadChannelResult.Failed(
                    Switch2BluetoothMemoryReadChannelFailure.NotPrepared);
            }
            if (pendingResponse != null || activeOperations != 0)
            {
                return Switch2BluetoothMemoryReadChannelResult.Failed(
                    Switch2BluetoothMemoryReadChannelFailure.Busy);
            }

            responseCompletion = new TaskCompletionSource<byte[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingResponse = responseCompletion;
            pendingCommandId = Switch2BluetoothMemoryReadCodec.CommandId;
            activeOperations = 1;
            operationsDrained = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            bool written = await command.WriteValueAsync(request,
                writeWithoutResponse, cancellationToken).ConfigureAwait(false);
            if (!written)
            {
                CompleteOperation(responseCompletion, terminalFailure: true);
                return Switch2BluetoothMemoryReadChannelResult.Failed(
                    Switch2BluetoothMemoryReadChannelFailure.WriteRejected);
            }

            byte[] responseValue = await responseCompletion.Task.
                WaitAsync(cancellationToken).ConfigureAwait(false);
            var value = new byte[length];
            bool accepted = responseValue != null &&
                Switch2BluetoothMemoryReadCodec.TryCopyResponsePayload(
                    responseValue, length, address, value, out _);
            if (!accepted)
            {
                CompleteOperation(responseCompletion, terminalFailure: true);
                return Switch2BluetoothMemoryReadChannelResult.Failed(
                    Switch2BluetoothMemoryReadChannelFailure.
                        ResponseRejected);
            }
            return Switch2BluetoothMemoryReadChannelResult.Success(value);
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(responseCompletion, terminalFailure: true);
            return Switch2BluetoothMemoryReadChannelResult.Failed(
                Switch2BluetoothMemoryReadChannelFailure.Cancelled);
        }
        catch
        {
            CompleteOperation(responseCompletion, terminalFailure: true);
            return Switch2BluetoothMemoryReadChannelResult.Failed(
                Switch2BluetoothMemoryReadChannelFailure.DependencyThrew);
        }
        finally
        {
            CompleteOperation(responseCompletion, terminalFailure: false);
        }
    }

    // Only called by the explicitly enabled portable-lab probe. Uses the same
    // subscription/serialization as LEDs; never admits arbitrary command bytes.
    internal async Task<string> ConfigureLabAudioAsync(CancellationToken cancellationToken, bool queryState = false)
    {
        TaskCompletionSource<byte[]> completion;
        lock (sync)
        {
            if (terminal || !prepared) return "{\"Error\":\"Command channel unavailable\"}";
            if (pendingResponse != null || activeOperations != 0)
                return "{\"Error\":\"Command lane busy; no probe sent\"}";
            completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingResponse = completion;
            pendingCommandId = queryState ? (byte)0x18 : (byte)0x17;
            activeOperations = 1;
            operationsDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        bool terminalFailure = true;
        Task<bool> write = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Retain actual Windows write completion even if the caller's
            // deadline expires. Retirement cannot detach beneath this write.
            write = command.WriteValueAsync(queryState ? Switch2BluetoothLabAudioProtocol.CreateStateRequest() : Switch2BluetoothLabAudioProtocol.CreateSetupRequest(),
                writeWithoutResponse, CancellationToken.None).AsTask();
            if (!await write.WaitAsync(cancellationToken).ConfigureAwait(false))
                return "{\"Error\":\"Audio setup write rejected\"}";
            byte[] reply = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            bool accepted = reply != null && (queryState ? Switch2BluetoothLabAudioProtocol.IsStateAcknowledged(reply) : Switch2BluetoothLabAudioProtocol.IsSetupAcknowledged(reply));
            terminalFailure = !accepted;
            return JsonSerializer.Serialize(new { SetupAcknowledged = !queryState && accepted, StateQueryAcknowledged = queryState && accepted,
                Response = reply == null ? null : Convert.ToHexString(reply), BluetoothPlaybackConfirmed = false });
        }
        catch (OperationCanceledException) { return "{\"Error\":\"Audio setup deadline; command lane fenced\"}"; }
        catch (Exception error) { return JsonSerializer.Serialize(new { Error = error.GetType().Name }); }
        finally
        {
            if (write != null) try { await write.ConfigureAwait(false); } catch { }
            CompleteOperation(completion, terminalFailure);
        }
    }

    internal async ValueTask<bool> RetireAsync(
        CancellationToken cancellationToken, Func<bool> isDisconnected = null)
    {
        TaskCompletionSource<byte[]> responseCompletion;
        Task operations;
        bool detach;
        lock (sync)
        {
            terminal = true;
            prepared = false;
            responseCompletion = pendingResponse;
            pendingResponse = null;
            operations = operationsDrained.Task;
            detach = attachAttempted;
            attachAttempted = false;
        }
        responseCompletion?.TrySetResult(null);

        bool clean = true;
        try
        {
            await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            await retainedCommandWriteDrain.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
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
                if (isDisconnected?.Invoke() != true)
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
        TaskCompletionSource<byte[]> completion;
        byte[] detachedValue;
        lock (sync)
        {
            if (!prepared || terminal || pendingResponse == null)
            {
                return;
            }
            // The command characteristic can carry responses to platform or
            // firmware work outside this closed owner. Correlate by command ID
            // exactly as the donor does and ignore unrelated notifications.
            if (value.IsEmpty || value[0] != pendingCommandId)
            {
                return;
            }
            // A duplicate fully valid Pro mask acknowledgement cannot
            // complete the enable step. Ignore only that already-accepted step;
            // malformed same-command replies reject and fence normally.
            if (pendingProFeatureSubcommand == 0x04 &&
                Switch2BluetoothProFeatureCodec.IsAccepted(value, enable: false))
                return;
            completion = pendingResponse;
            pendingResponse = null;
            pendingCommandId = 0;
            // The Pro command-only contract is exactly 12 bytes. Reject an
            // oversized same-command response without copying arbitrary data.
            detachedValue = pendingLabReceiverResponse
                ? value.Length > Switch2BluetoothLabReceiverPlan.MaximumResponseBytes ? Array.Empty<byte>() : value.ToArray()
                : pendingProFeatureSubcommand != 0 && value.Length != Switch2BluetoothProFeatureCodec.ResponseLength
                    ? Array.Empty<byte>() : value.ToArray();
            pendingProFeatureSubcommand = 0;
            pendingLabReceiverResponse = false;
        }
        completion.TrySetResult(detachedValue);
    }

    private void CompleteOperation(TaskCompletionSource<byte[]> completion,
        bool terminalFailure)
    {
        TaskCompletionSource<bool> drained = null;
        lock (sync)
        {
            if (ReferenceEquals(pendingResponse, completion))
            {
                pendingResponse = null;
                pendingCommandId = 0;
                pendingProFeatureSubcommand = 0;
                pendingLabReceiverResponse = false;
            }
            if (terminalFailure)
            {
                terminal = true;
                prepared = false;
            }
            if (activeOperations != 0)
            {
                activeOperations = 0;
                drained = operationsDrained;
            }
        }
        completion.TrySetResult(null);
        drained?.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CompletedDrain()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.TrySetResult(true);
        return completion;
    }
}
