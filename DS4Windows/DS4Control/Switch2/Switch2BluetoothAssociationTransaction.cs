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
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

internal interface ISwitch2BluetoothAssociationCommandChannel
{
    /// <summary>
    /// Sends one complete request and completes only after the response
    /// notification for that request has been admitted and copied. Calls are
    /// never concurrent. A false result proves this request was not accepted as
    /// successful; ambiguity is terminal to the channel owner.
    /// </summary>
    ValueTask<bool> ExchangeAsync(ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken);
}

internal enum Switch2BluetoothAssociationFailure : byte
{
    None = 0,
    InvalidArgument,
    Busy,
    Cancelled,
    TimedOut,
    ChannelRejected,
    ChannelFaulted,
}

internal readonly struct Switch2BluetoothAssociationResult
{
    private Switch2BluetoothAssociationResult(
        Switch2BluetoothAssociationFailure failure,
        Switch2BluetoothAssociationStep lastCompletedStep)
    {
        Failure = failure;
        LastCompletedStep = lastCompletedStep;
    }

    internal bool Succeeded => Failure ==
        Switch2BluetoothAssociationFailure.None;

    internal Switch2BluetoothAssociationFailure Failure { get; }

    internal Switch2BluetoothAssociationStep LastCompletedStep { get; }

    internal static Switch2BluetoothAssociationResult Success() => new(
        Switch2BluetoothAssociationFailure.None,
        Switch2BluetoothAssociationStep.Commit);

    internal static Switch2BluetoothAssociationResult Failed(
        Switch2BluetoothAssociationFailure failure,
        Switch2BluetoothAssociationStep lastCompletedStep = default) =>
        new(failure, lastCompletedStep);
}

/// <summary>
/// Single-flight, bounded owner for the exact four-step controller-side
/// association ceremony. Response subscription and command serialization are
/// supplied by the channel owner; this transaction never invokes Windows SMP
/// pairing APIs and never retries a possibly accepted write.
/// </summary>
internal sealed class Switch2BluetoothAssociationTransaction
{
    internal const int MinimumTimeoutMilliseconds = 100;
    internal const int MaximumTimeoutMilliseconds = 60_000;

    private static readonly Switch2BluetoothAssociationStep[] Steps =
    [
        Switch2BluetoothAssociationStep.SetHostAddress,
        Switch2BluetoothAssociationStep.WriteLongTermKeyPart1,
        Switch2BluetoothAssociationStep.WriteLongTermKeyPart2,
        Switch2BluetoothAssociationStep.Commit,
    ];

    private readonly ISwitch2BluetoothAssociationCommandChannel channel;
    private readonly TimeSpan timeout;
    private int active;

    internal Switch2BluetoothAssociationTransaction(
        ISwitch2BluetoothAssociationCommandChannel channel,
        TimeSpan timeout)
    {
        this.channel = channel ?? throw new ArgumentNullException(
            nameof(channel));
        if (timeout.TotalMilliseconds is < MinimumTimeoutMilliseconds or
            > MaximumTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        this.timeout = timeout;
    }

    internal async ValueTask<Switch2BluetoothAssociationResult> ExecuteAsync(
        ReadOnlyMemory<byte> localHostAddress,
        CancellationToken cancellationToken = default)
    {
        if (!Switch2BluetoothAssociationCodec.IsValidHostAddress(
                localHostAddress.Span))
        {
            return Switch2BluetoothAssociationResult.Failed(
                Switch2BluetoothAssociationFailure.InvalidArgument);
        }
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
        {
            return Switch2BluetoothAssociationResult.Failed(
                Switch2BluetoothAssociationFailure.Busy);
        }

        byte[] hostCopy = localHostAddress.ToArray();
        byte[] request = new byte[
            Switch2BluetoothAssociationCodec.MaximumRequestLength];
        Switch2BluetoothAssociationStep lastCompleted = default;
        bool completedAny = false;
        try
        {
            using var deadline = CancellationTokenSource.
                CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            CancellationToken boundedToken = deadline.Token;

            foreach (Switch2BluetoothAssociationStep step in Steps)
            {
                boundedToken.ThrowIfCancellationRequested();
                if (!Switch2BluetoothAssociationCodec.TryGetRequestLength(step,
                        out int requestLength) ||
                    !Switch2BluetoothAssociationCodec.TryWriteRequest(step,
                        hostCopy, request.AsSpan(0, requestLength), out _))
                {
                    return Switch2BluetoothAssociationResult.Failed(
                        Switch2BluetoothAssociationFailure.InvalidArgument,
                        completedAny ? lastCompleted : default);
                }

                bool accepted = await channel.ExchangeAsync(
                    request.AsMemory(0, requestLength), boundedToken).
                    ConfigureAwait(false);
                if (!accepted)
                {
                    return Switch2BluetoothAssociationResult.Failed(
                        Switch2BluetoothAssociationFailure.ChannelRejected,
                        completedAny ? lastCompleted : default);
                }
                lastCompleted = step;
                completedAny = true;
            }

            return Switch2BluetoothAssociationResult.Success();
        }
        catch (OperationCanceledException)
        {
            return Switch2BluetoothAssociationResult.Failed(
                cancellationToken.IsCancellationRequested ?
                    Switch2BluetoothAssociationFailure.Cancelled :
                    Switch2BluetoothAssociationFailure.TimedOut,
                completedAny ? lastCompleted : default);
        }
        catch
        {
            return Switch2BluetoothAssociationResult.Failed(
                Switch2BluetoothAssociationFailure.ChannelFaulted,
                completedAny ? lastCompleted : default);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hostCopy);
            CryptographicOperations.ZeroMemory(request);
            Volatile.Write(ref active, 0);
        }
    }
}
