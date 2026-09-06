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

/// <summary>
/// Synchronous completion evidence returned by the abstract USB output
/// transport. A proven rejection means no byte from this operation was
/// accepted. An uncertain outcome may have applied any prefix, including the
/// complete report, and therefore cannot authorize counter reuse for a
/// different logical submission.
/// </summary>
internal enum Switch2ProUsbHdRumbleTransportWriteOutcome : byte
{
    Invalid = 0,
    Completed,
    ProvenRejected,
    OutcomeUncertain,
}

internal enum Switch2ProUsbHdRumbleTransportWriteFailure : byte
{
    None = 0,
    InvalidReport,
    StaleLifetime,
    Busy,
    TransportRejected,
    TransportEnded,
    DependencyThrew,
}

/// <summary>
/// Normalized synchronous transport result. Model and both generations bind
/// completion evidence to the exact lease lifetime which performed the call.
/// Factories intentionally retain the raw byte count so the owner can reject
/// malformed or partial "Completed" results instead of promoting them.
/// </summary>
internal readonly struct Switch2ProUsbHdRumbleTransportWriteResult
{
    private Switch2ProUsbHdRumbleTransportWriteResult(
        Switch2ProUsbHdRumbleTransportWriteOutcome outcome,
        Switch2ProUsbHdRumbleTransportWriteFailure failure,
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

    internal Switch2ProUsbHdRumbleTransportWriteOutcome Outcome { get; }

    internal Switch2ProUsbHdRumbleTransportWriteFailure Failure { get; }

    internal Switch2ControllerModel Model { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal int BytesTransferred { get; }

    internal bool HasValidInvariants()
    {
        if (Model is < Switch2ControllerModel.JoyCon2Right or >
                Switch2ControllerModel.ProController2 ||
            DeviceGeneration == 0 || TransportGeneration == 0)
        {
            return false;
        }

        return Outcome switch
        {
            Switch2ProUsbHdRumbleTransportWriteOutcome.Completed =>
                Failure ==
                    Switch2ProUsbHdRumbleTransportWriteFailure.None &&
                BytesTransferred == Switch2UsbHdRumbleCodec.ReportLength,
            Switch2ProUsbHdRumbleTransportWriteOutcome.ProvenRejected =>
                IsFailure(Failure) && BytesTransferred == 0,
            Switch2ProUsbHdRumbleTransportWriteOutcome.OutcomeUncertain =>
                IsFailure(Failure) && BytesTransferred >= 0 &&
                BytesTransferred <= Switch2UsbHdRumbleCodec.ReportLength,
            _ => false,
        };
    }

    internal bool Authenticates(Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration) =>
        Model == expectedModel &&
        DeviceGeneration == expectedDeviceGeneration &&
        TransportGeneration == expectedTransportGeneration;

    internal static Switch2ProUsbHdRumbleTransportWriteResult Complete(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, int bytesTransferred =
            Switch2UsbHdRumbleCodec.ReportLength) => new(
        Switch2ProUsbHdRumbleTransportWriteOutcome.Completed,
        Switch2ProUsbHdRumbleTransportWriteFailure.None, model,
        deviceGeneration, transportGeneration, bytesTransferred);

    internal static Switch2ProUsbHdRumbleTransportWriteResult Reject(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration,
        Switch2ProUsbHdRumbleTransportWriteFailure failure,
        int bytesTransferred = 0) => new(
        Switch2ProUsbHdRumbleTransportWriteOutcome.ProvenRejected, failure,
        model, deviceGeneration, transportGeneration, bytesTransferred);

    internal static Switch2ProUsbHdRumbleTransportWriteResult Uncertain(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration,
        Switch2ProUsbHdRumbleTransportWriteFailure failure,
        int bytesTransferred = 0) => new(
        Switch2ProUsbHdRumbleTransportWriteOutcome.OutcomeUncertain, failure,
        model, deviceGeneration, transportGeneration, bytesTransferred);

    private static bool IsFailure(
        Switch2ProUsbHdRumbleTransportWriteFailure failure) =>
        failure is >=
            Switch2ProUsbHdRumbleTransportWriteFailure.InvalidReport and <=
            Switch2ProUsbHdRumbleTransportWriteFailure.DependencyThrew;
}

/// <summary>
/// Exact synchronous, transport-owned USB output lifetime. Authentication is
/// pure and performs no I/O. TryWriteReport must consume the span before it
/// returns and must not retain it. Its result must carry this operation's exact
/// model and generations. The dormant owned-composite compatibility bridge is
/// an internal implementation, but no production path constructs it and this
/// contract itself cannot open or mutate a physical device.
/// </summary>
internal interface ISwitch2ProUsbHdRumbleTransportLease
{
    bool Authenticates(Switch2ControllerModel model,
        ulong deviceGeneration, ulong transportGeneration);

    Switch2ProUsbHdRumbleTransportWriteResult TryWriteReport(
        ReadOnlySpan<byte> report, Switch2ControllerModel expectedModel,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration);
}

/// <summary>
/// Dormant direct USB Pro Controller 2 writer. It owns one 64-byte buffer and
/// the modulo-16 output sequence. It adds no pacing or background work; a
/// future transport owner must first establish cadence, neutralization, and
/// teardown behavior from hardware evidence.
/// </summary>
internal sealed class Switch2ProUsbHdRumblePhysicalWriter :
    ISwitch2HdRumblePhysicalWriter
{
    private const Switch2ControllerModel Model =
        Switch2ControllerModel.ProController2;

    private readonly ISwitch2ProUsbHdRumbleTransportLease lease;
    private readonly ulong deviceGeneration;
    private readonly ulong transportGeneration;
    private readonly byte[] pendingReport =
        new byte[Switch2UsbHdRumbleCodec.ReportLength];

    private Switch2HdRumblePhysicalSubmission pendingSubmission;
    private byte nextCounter;
    private bool hasPendingReport;
    private int writeActive;

    internal Switch2ProUsbHdRumblePhysicalWriter(
        ISwitch2ProUsbHdRumbleTransportLease lease,
        ulong deviceGeneration, ulong transportGeneration,
        byte initialCounter = 0)
    {
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
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
        if (!TryAuthenticate(lease, deviceGeneration, transportGeneration,
                out bool dependencyThrew))
        {
            throw new ArgumentException(dependencyThrew ?
                    "The transport lease threw while authenticating the USB " +
                    "Pro Controller 2 lifetime." :
                    "The transport lease does not authenticate the USB Pro " +
                    "Controller 2 lifetime.",
                nameof(lease));
        }

        this.deviceGeneration = deviceGeneration;
        this.transportGeneration = transportGeneration;
        nextCounter = initialCounter;
    }

    public bool Authenticates(ulong candidateDeviceGeneration,
        ulong candidateTransportGeneration) =>
        candidateDeviceGeneration != 0 &&
        candidateTransportGeneration != 0 &&
        candidateDeviceGeneration == deviceGeneration &&
        candidateTransportGeneration == transportGeneration &&
        lease.Authenticates(Model, candidateDeviceGeneration,
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
            if (!TryAuthenticate(lease, deviceGeneration,
                    transportGeneration, out bool authenticationThrew))
            {
                return Switch2HdRumblePhysicalWriteResult.Reject(
                    authenticationThrew ?
                        Switch2HdRumblePhysicalWriteFailure.DependencyThrew :
                        Switch2HdRumblePhysicalWriteFailure.StaleLifetime);
            }

            if (!hasPendingReport ||
                !SubmissionEquals(pendingSubmission, submission))
            {
                if (hasPendingReport && lease is
                        ISwitch2ProUsbHdRumblePendingReportFence fence &&
                    fence.MustRetainPendingReport)
                {
                    return Switch2HdRumblePhysicalWriteResult.Reject(
                        Switch2HdRumblePhysicalWriteFailure.Busy);
                }

                Span<byte> encoded = stackalloc byte[
                    Switch2UsbHdRumbleCodec.ReportLength];
                byte counter = nextCounter;
                if (!Switch2UsbHdRumbleCodec.TryEncodeProController(counter,
                        submission.Left, submission.Right, encoded))
                {
                    return Switch2HdRumblePhysicalWriteResult.Reject(
                        Switch2HdRumblePhysicalWriteFailure.
                            InvalidSubmission);
                }

                encoded.CopyTo(pendingReport);
                pendingSubmission = submission;
                hasPendingReport = true;
                nextCounter = (byte)((counter + 1) & 0x0F);
            }

            Switch2ProUsbHdRumbleTransportWriteResult transportResult;
            try
            {
                transportResult = lease.TryWriteReport(pendingReport, Model,
                    deviceGeneration, transportGeneration);
            }
            catch
            {
                return Switch2HdRumblePhysicalWriteResult.Uncertain(
                    Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
            }

            if (!transportResult.HasValidInvariants())
            {
                return Switch2HdRumblePhysicalWriteResult.Uncertain(
                    Switch2HdRumblePhysicalWriteFailure.DependencyThrew);
            }
            if (!transportResult.Authenticates(Model, deviceGeneration,
                    transportGeneration))
            {
                return Switch2HdRumblePhysicalWriteResult.Uncertain(
                    Switch2HdRumblePhysicalWriteFailure.StaleLifetime);
            }

            if (transportResult.Outcome ==
                Switch2ProUsbHdRumbleTransportWriteOutcome.Completed)
            {
                hasPendingReport = false;
                pendingSubmission = default;
                return Switch2HdRumblePhysicalWriteResult.Success();
            }

            Switch2HdRumblePhysicalWriteFailure failure = MapFailure(
                transportResult.Failure);
            return transportResult.Outcome ==
                    Switch2ProUsbHdRumbleTransportWriteOutcome.ProvenRejected ?
                Switch2HdRumblePhysicalWriteResult.Reject(failure) :
                Switch2HdRumblePhysicalWriteResult.Uncertain(failure);
        }
        finally
        {
            Volatile.Write(ref writeActive, 0);
        }
    }

    private static bool TryAuthenticate(
        ISwitch2ProUsbHdRumbleTransportLease candidate,
        ulong deviceGeneration, ulong transportGeneration,
        out bool dependencyThrew)
    {
        try
        {
            dependencyThrew = false;
            return candidate.Authenticates(Model, deviceGeneration,
                transportGeneration);
        }
        catch
        {
            dependencyThrew = true;
            return false;
        }
    }

    private static Switch2HdRumblePhysicalWriteFailure MapFailure(
        Switch2ProUsbHdRumbleTransportWriteFailure failure) =>
        failure switch
        {
            Switch2ProUsbHdRumbleTransportWriteFailure.InvalidReport =>
                Switch2HdRumblePhysicalWriteFailure.InvalidSubmission,
            Switch2ProUsbHdRumbleTransportWriteFailure.StaleLifetime =>
                Switch2HdRumblePhysicalWriteFailure.StaleLifetime,
            Switch2ProUsbHdRumbleTransportWriteFailure.Busy =>
                Switch2HdRumblePhysicalWriteFailure.Busy,
            Switch2ProUsbHdRumbleTransportWriteFailure.
                    TransportRejected =>
                Switch2HdRumblePhysicalWriteFailure.TransportRejected,
            Switch2ProUsbHdRumbleTransportWriteFailure.TransportEnded =>
                Switch2HdRumblePhysicalWriteFailure.TransportEnded,
            _ => Switch2HdRumblePhysicalWriteFailure.DependencyThrew,
        };

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

    private static bool IsFreshAt(
        in Switch2HdRumblePhysicalSubmission submission,
        ulong nowMicroseconds)
    {
        if (submission.TimestampMicroseconds > nowMicroseconds)
        {
            return submission.TimestampMicroseconds - nowMicroseconds <=
                ControllerFeedbackFrame.MaxFutureSkewMicroseconds;
        }

        return nowMicroseconds - submission.TimestampMicroseconds <
            submission.TimeToLiveMicroseconds;
    }
}
