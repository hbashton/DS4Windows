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

namespace DS4Windows.Switch2;

internal enum Switch2BluetoothHdRumbleTransportWriteOutcome : byte
{
    Invalid = 0,
    Completed,
    ProvenRejected,
    OutcomeUncertain,
}

internal enum Switch2BluetoothHdRumbleTransportWriteFailure : byte
{
    None = 0,
    InvalidPayload,
    StaleLifetime,
    Busy,
    TransportRejected,
    TransportEnded,
    TimedOut,
    DependencyThrew,
}

internal readonly struct Switch2BluetoothHdRumbleTransportWriteResult
{
    private Switch2BluetoothHdRumbleTransportWriteResult(
        Switch2BluetoothHdRumbleTransportWriteOutcome outcome,
        Switch2BluetoothHdRumbleTransportWriteFailure failure,
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, int bytesTransferred)
    {
        Outcome = outcome;
        Failure = failure;
        Model = model;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        BytesTransferred = bytesTransferred;
    }

    internal Switch2BluetoothHdRumbleTransportWriteOutcome Outcome { get; }

    internal Switch2BluetoothHdRumbleTransportWriteFailure Failure { get; }

    internal Switch2ControllerModel Model { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal int BytesTransferred { get; }

    internal bool HasValidInvariants()
    {
        int expectedLength = Switch2BluetoothHdRumblePhysicalWriter.
            PayloadLengthFor(Model);
        if (expectedLength == 0 || DeviceGeneration == 0 ||
            TransportGeneration == 0)
        {
            return false;
        }

        return Outcome switch
        {
            Switch2BluetoothHdRumbleTransportWriteOutcome.Completed =>
                Failure == Switch2BluetoothHdRumbleTransportWriteFailure.None &&
                BytesTransferred == expectedLength,
            Switch2BluetoothHdRumbleTransportWriteOutcome.ProvenRejected =>
                IsFailure(Failure) && BytesTransferred == 0,
            Switch2BluetoothHdRumbleTransportWriteOutcome.OutcomeUncertain =>
                IsFailure(Failure) && BytesTransferred >= 0 &&
                BytesTransferred <= expectedLength,
            _ => false,
        };
    }

    internal bool Authenticates(Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration) =>
        Model == expectedModel &&
        DeviceGeneration == expectedDeviceGeneration &&
        TransportGeneration == expectedTransportGeneration;

    internal static Switch2BluetoothHdRumbleTransportWriteResult Complete(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, int bytesTransferred) => new(
        Switch2BluetoothHdRumbleTransportWriteOutcome.Completed,
        Switch2BluetoothHdRumbleTransportWriteFailure.None, model,
        deviceGeneration, transportGeneration, bytesTransferred);

    internal static Switch2BluetoothHdRumbleTransportWriteResult Reject(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration,
        Switch2BluetoothHdRumbleTransportWriteFailure failure) => new(
        Switch2BluetoothHdRumbleTransportWriteOutcome.ProvenRejected,
        failure, model, deviceGeneration, transportGeneration, 0);

    internal static Switch2BluetoothHdRumbleTransportWriteResult Uncertain(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration,
        Switch2BluetoothHdRumbleTransportWriteFailure failure,
        int bytesTransferred = 0) => new(
        Switch2BluetoothHdRumbleTransportWriteOutcome.OutcomeUncertain,
        failure, model, deviceGeneration, transportGeneration,
        bytesTransferred);

    private static bool IsFailure(
        Switch2BluetoothHdRumbleTransportWriteFailure failure) => failure is >=
            Switch2BluetoothHdRumbleTransportWriteFailure.InvalidPayload and <=
            Switch2BluetoothHdRumbleTransportWriteFailure.DependencyThrew;
}

/// <summary>
/// Exact generation-bound BLE vibration-characteristic lifetime. The payload
/// must be consumed before TryWritePayload returns. Implementations own the
/// bounded write-without-response operation and must not retain caller memory.
/// </summary>
internal interface ISwitch2BluetoothHdRumbleTransportLease
{
    bool Authenticates(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration);

    Switch2BluetoothHdRumbleTransportWriteResult TryWritePayload(
        ReadOnlySpan<byte> payload, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration);
}

/// <summary>
/// Cold-path binding used by the runtime owner after it has minted the exact
/// physical generations. Binding is one-shot and performs no GATT operation.
/// </summary>
internal interface ISwitch2BluetoothHdRumbleBindableTransportLease :
    ISwitch2BluetoothHdRumbleTransportLease
{
    bool HasHdRumbleOutput { get; }

    bool TryBindHdRumbleLifetime(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration);
}

/// <summary>
/// Proof from the same bound lease that Windows reported a definite disconnect,
/// output admission is sealed, and every callback, output operation and native
/// resource has drained. A failed write or timeout alone is not this proof.
/// </summary>
internal interface ISwitch2BluetoothDisconnectedOutputProof
{
    bool IsDisconnectedAndReleased(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration);
}

/// <summary>
/// Sole BLE HD-rumble framer for one Pro Controller 2 or Joy-Con 2 lifetime.
///
/// Transport constants and envelope layout are adapted from Switch2Connect
/// GPL-3.0 commit 4487322a306f04efa27682e3f3a508635a84fd98,
/// src/controller.py lines 1160-1164 and 3759-3795: a zero envelope byte,
/// rolling 0x50-0x5f actuator-group headers, one group for a Joy-Con, and
/// left-then-right groups for a Pro Controller. No donor scheduling or mapping
/// stack is copied into this owner.
/// </summary>
internal sealed class Switch2BluetoothHdRumblePhysicalWriter :
    ISwitch2HdRumblePhysicalWriter
{
    internal static readonly Guid JoyCon2RightCharacteristicUuid =
        new("fa19b0fb-cd1f-46a7-84a1-bbb09e00c149");
    internal static readonly Guid JoyCon2LeftCharacteristicUuid =
        new("289326cb-a471-485d-a8f4-240c14f18241");
    internal static readonly Guid ProController2CharacteristicUuid =
        new("cc483f51-9258-427d-a939-630c31f72b05");

    private readonly ISwitch2BluetoothHdRumbleTransportLease lease;
    private readonly Switch2ControllerModel model;
    private readonly ulong deviceGeneration;
    private readonly ulong transportGeneration;
    private readonly byte[] pendingPayload = new byte[
        Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength];

    private Switch2HdRumblePhysicalSubmission pendingSubmission;
    private byte nextCounter;
    private int pendingLength;
    private bool hasPendingPayload;
    private int writeActive;

    internal Switch2BluetoothHdRumblePhysicalWriter(
        ISwitch2BluetoothHdRumbleTransportLease lease,
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, byte initialCounter = 0)
    {
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
        if (PayloadLengthFor(model) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(model));
        }
        if (deviceGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceGeneration));
        }
        if (transportGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transportGeneration));
        }
        if (initialCounter > 0x0F)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCounter));
        }
        if (!TryAuthenticate(lease, model, deviceGeneration,
                transportGeneration, out bool dependencyThrew))
        {
            throw new ArgumentException(dependencyThrew ?
                    "The Bluetooth transport threw while authenticating the " +
                    "Switch 2 output lifetime." :
                    "The Bluetooth transport does not authenticate the " +
                    "Switch 2 output lifetime.",
                nameof(lease));
        }

        this.model = model;
        this.deviceGeneration = deviceGeneration;
        this.transportGeneration = transportGeneration;
        nextCounter = initialCounter;
    }

    public bool Authenticates(ulong candidateDeviceGeneration,
        ulong candidateTransportGeneration) =>
        candidateDeviceGeneration != 0 && candidateTransportGeneration != 0 &&
        candidateDeviceGeneration == deviceGeneration &&
        candidateTransportGeneration == transportGeneration &&
        lease.Authenticates(model, candidateDeviceGeneration,
            candidateTransportGeneration);

    public Switch2HdRumblePhysicalWriteResult TryWrite(
        in Switch2HdRumblePhysicalSubmission submission)
    {
        if (!submission.HasValidInvariants())
        {
            return Switch2HdRumblePhysicalWriteResult.Reject(
                Switch2HdRumblePhysicalWriteFailure.InvalidSubmission);
        }
        if (submission.DeviceGeneration != deviceGeneration ||
            submission.TransportGeneration != transportGeneration)
        {
            return Switch2HdRumblePhysicalWriteResult.Reject(
                Switch2HdRumblePhysicalWriteFailure.StaleLifetime);
        }
        if (!submission.IsStop &&
            (!ControllerFeedbackClock.TryGetTimestampMicroseconds(
                    out ulong nowMicroseconds) ||
                !IsFreshAt(submission, nowMicroseconds)))
        {
            return Switch2HdRumblePhysicalWriteResult.Reject(
                Switch2HdRumblePhysicalWriteFailure.InvalidSubmission);
        }
        if (Interlocked.CompareExchange(ref writeActive, 1, 0) != 0)
        {
            return Switch2HdRumblePhysicalWriteResult.Reject(
                Switch2HdRumblePhysicalWriteFailure.Busy);
        }

        try
        {
            if (!TryAuthenticate(lease, model, deviceGeneration,
                    transportGeneration, out bool authenticationThrew))
            {
                return Switch2HdRumblePhysicalWriteResult.Reject(
                    authenticationThrew ?
                        Switch2HdRumblePhysicalWriteFailure.DependencyThrew :
                        Switch2HdRumblePhysicalWriteFailure.StaleLifetime);
            }

            if (!hasPendingPayload ||
                !SubmissionEquals(pendingSubmission, submission))
            {
                Span<byte> encoded = stackalloc byte[
                    Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength];
                byte counter = nextCounter;
                bool encodedOk;
                int length = PayloadLengthFor(model);
                if (model == Switch2ControllerModel.ProController2)
                {
                    encodedOk = Switch2BluetoothHdRumbleCodec.
                        TryEncodeProController(counter, submission.Left,
                            submission.Right, encoded.Slice(0, length));
                }
                else
                {
                    Switch2HdRumbleGroup side = model ==
                            Switch2ControllerModel.JoyCon2Left ?
                        submission.Left : submission.Right;
                    encodedOk = Switch2BluetoothHdRumbleCodec.TryEncodeJoyCon(
                        counter, side, encoded.Slice(0, length));
                }
                if (!encodedOk)
                {
                    return Switch2HdRumblePhysicalWriteResult.Reject(
                        Switch2HdRumblePhysicalWriteFailure.InvalidSubmission);
                }

                encoded.Slice(0, length).CopyTo(pendingPayload);
                pendingLength = length;
                pendingSubmission = submission;
                hasPendingPayload = true;
                nextCounter = (byte)((counter + 1) & 0x0F);
            }

            Switch2BluetoothHdRumbleTransportWriteResult result;
            try
            {
                result = lease.TryWritePayload(
                    pendingPayload.AsSpan(0, pendingLength), model,
                    deviceGeneration, transportGeneration);
            }
            catch
            {
                return Switch2HdRumblePhysicalWriteResult.Uncertain(
                    Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
            }

            if (!result.HasValidInvariants() ||
                !result.Authenticates(model, deviceGeneration,
                    transportGeneration))
            {
                return Switch2HdRumblePhysicalWriteResult.Uncertain(
                    Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
            }
            if (result.Outcome ==
                Switch2BluetoothHdRumbleTransportWriteOutcome.Completed)
            {
                hasPendingPayload = false;
                pendingLength = 0;
                pendingSubmission = default;
                return Switch2HdRumblePhysicalWriteResult.Success();
            }

            Switch2HdRumblePhysicalWriteFailure failure = MapFailure(
                result.Failure);
            return result.Outcome ==
                    Switch2BluetoothHdRumbleTransportWriteOutcome.
                        OutcomeUncertain ?
                Switch2HdRumblePhysicalWriteResult.Uncertain(failure) :
                Switch2HdRumblePhysicalWriteResult.Reject(failure);
        }
        finally
        {
            Volatile.Write(ref writeActive, 0);
        }
    }

    internal static int PayloadLengthFor(Switch2ControllerModel model) =>
        model switch
        {
            Switch2ControllerModel.ProController2 =>
                Switch2BluetoothHdRumbleCodec.ProControllerPayloadLength,
            Switch2ControllerModel.JoyCon2Left or
                Switch2ControllerModel.JoyCon2Right =>
                Switch2BluetoothHdRumbleCodec.JoyConPayloadLength,
            _ => 0,
        };

    internal static Guid CharacteristicUuidFor(
        Switch2ControllerModel model) => model switch
        {
            Switch2ControllerModel.ProController2 =>
                ProController2CharacteristicUuid,
            Switch2ControllerModel.JoyCon2Left =>
                JoyCon2LeftCharacteristicUuid,
            Switch2ControllerModel.JoyCon2Right =>
                JoyCon2RightCharacteristicUuid,
            _ => Guid.Empty,
        };

    private static bool IsFreshAt(
        in Switch2HdRumblePhysicalSubmission submission,
        ulong nowMicroseconds) =>
        nowMicroseconds >= submission.TimestampMicroseconds &&
        nowMicroseconds - submission.TimestampMicroseconds <=
            submission.TimeToLiveMicroseconds;

    private static bool SubmissionEquals(
        in Switch2HdRumblePhysicalSubmission left,
        in Switch2HdRumblePhysicalSubmission right) =>
        left.Command == right.Command && left.Fidelity == right.Fidelity &&
        left.Left.Equals(right.Left) && left.Right.Equals(right.Right) &&
        left.DeviceGeneration == right.DeviceGeneration &&
        left.TransportGeneration == right.TransportGeneration &&
        left.DeliveryEpoch == right.DeliveryEpoch &&
        left.Source == right.Source && left.Sequence == right.Sequence &&
        left.OwnershipEpoch == right.OwnershipEpoch &&
        left.TimestampMicroseconds == right.TimestampMicroseconds &&
        left.TimeToLiveMicroseconds == right.TimeToLiveMicroseconds;

    private static Switch2HdRumblePhysicalWriteFailure MapFailure(
        Switch2BluetoothHdRumbleTransportWriteFailure failure) =>
        failure switch
        {
            Switch2BluetoothHdRumbleTransportWriteFailure.InvalidPayload =>
                Switch2HdRumblePhysicalWriteFailure.InvalidSubmission,
            Switch2BluetoothHdRumbleTransportWriteFailure.StaleLifetime =>
                Switch2HdRumblePhysicalWriteFailure.StaleLifetime,
            Switch2BluetoothHdRumbleTransportWriteFailure.Busy =>
                Switch2HdRumblePhysicalWriteFailure.Busy,
            Switch2BluetoothHdRumbleTransportWriteFailure.TransportRejected =>
                Switch2HdRumblePhysicalWriteFailure.TransportRejected,
            Switch2BluetoothHdRumbleTransportWriteFailure.TransportEnded or
                Switch2BluetoothHdRumbleTransportWriteFailure.TimedOut =>
                Switch2HdRumblePhysicalWriteFailure.TransportEnded,
            _ => Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
        };

    private static bool TryAuthenticate(
        ISwitch2BluetoothHdRumbleTransportLease candidate,
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, out bool dependencyThrew)
    {
        try
        {
            dependencyThrew = false;
            return candidate.Authenticates(model, deviceGeneration,
                transportGeneration);
        }
        catch
        {
            dependencyThrew = true;
            return false;
        }
    }
}
