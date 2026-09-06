/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Runtime.CompilerServices;

namespace DS4Windows.Switch2;

/// <summary>
/// Opaque identity of the one exact HID output submission retained by an
/// owned-composite lease after an outcome-uncertain deadline return. Matching
/// numeric generations or sequence values from another lease are insufficient.
/// </summary>
internal readonly struct Switch2ProUsbOwnedOutputOperationClaim :
    IEquatable<Switch2ProUsbOwnedOutputOperationClaim>
{
    private readonly object leaseFence;

    internal Switch2ProUsbOwnedOutputOperationClaim(object leaseFence,
        ulong deviceGeneration, ulong transportGeneration, ulong sequence)
    {
        this.leaseFence = leaseFence;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        Sequence = sequence;
    }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal ulong Sequence { get; }

    internal bool IsValid => leaseFence != null && DeviceGeneration != 0 &&
        TransportGeneration != 0 && Sequence != 0;

    internal bool Authenticates(object expectedFence,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration,
        ulong expectedSequence) => IsValid &&
        ReferenceEquals(leaseFence, expectedFence) &&
        DeviceGeneration == expectedDeviceGeneration &&
        TransportGeneration == expectedTransportGeneration &&
        Sequence == expectedSequence;

    public bool Equals(Switch2ProUsbOwnedOutputOperationClaim other) =>
        ReferenceEquals(leaseFence, other.leaseFence) &&
        DeviceGeneration == other.DeviceGeneration &&
        TransportGeneration == other.TransportGeneration &&
        Sequence == other.Sequence;

    public override bool Equals(object obj) =>
        obj is Switch2ProUsbOwnedOutputOperationClaim other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        leaseFence == null ? 0 : RuntimeHelpers.GetHashCode(leaseFence),
        DeviceGeneration, TransportGeneration, Sequence);
}

/// <summary>
/// One bounded output attempt. A retained claim exists only when the transport
/// outcome is uncertain <em>and</em> the native submission is not yet
/// quiescent. A no-operation disposition proves only that this attempt owns no
/// native submission; it says nothing about an earlier lane operation. Whole-
/// lane quiescence requires the lane's separate terminal proof.
/// </summary>
internal enum Switch2ProUsbOwnedOutputAttemptDisposition : byte
{
    Invalid = 0,
    NoOperationOwnedByAttempt,
    RetainedOperation,
    LeaseQuarantined,
}

internal readonly struct Switch2ProUsbOwnedOutputWriteAttempt
{
    internal Switch2ProUsbOwnedOutputWriteAttempt(
        in Switch2ProUsbHdRumbleTransportWriteResult transportResult,
        in Switch2ProUsbOwnedOutputOperationClaim retainedClaim)
        : this(transportResult, retainedClaim,
            retainedClaim.IsValid ?
                Switch2ProUsbOwnedOutputAttemptDisposition.
                    RetainedOperation :
                Switch2ProUsbOwnedOutputAttemptDisposition.
                    NoOperationOwnedByAttempt)
    {
    }

    private Switch2ProUsbOwnedOutputWriteAttempt(
        in Switch2ProUsbHdRumbleTransportWriteResult transportResult,
        in Switch2ProUsbOwnedOutputOperationClaim retainedClaim,
        Switch2ProUsbOwnedOutputAttemptDisposition disposition)
    {
        TransportResult = transportResult;
        RetainedClaim = retainedClaim;
        Disposition = disposition;
    }

    internal Switch2ProUsbHdRumbleTransportWriteResult TransportResult
        { get; }

    internal Switch2ProUsbOwnedOutputOperationClaim RetainedClaim { get; }

    internal Switch2ProUsbOwnedOutputAttemptDisposition Disposition { get; }

    internal bool RequiresRetirement => TransportResult.Outcome ==
        Switch2ProUsbHdRumbleTransportWriteOutcome.OutcomeUncertain &&
        RetainedClaim.IsValid && Disposition ==
            Switch2ProUsbOwnedOutputAttemptDisposition.RetainedOperation;

    internal bool RequiresTerminalAttention => Disposition ==
        Switch2ProUsbOwnedOutputAttemptDisposition.LeaseQuarantined;

    internal bool HasValidInvariants() =>
        TransportResult.HasValidInvariants() &&
        Disposition switch
        {
            Switch2ProUsbOwnedOutputAttemptDisposition.
                NoOperationOwnedByAttempt =>
                !RetainedClaim.IsValid,
            Switch2ProUsbOwnedOutputAttemptDisposition.RetainedOperation =>
                RetainedClaim.IsValid && TransportResult.Outcome ==
                    Switch2ProUsbHdRumbleTransportWriteOutcome.
                        OutcomeUncertain,
            Switch2ProUsbOwnedOutputAttemptDisposition.LeaseQuarantined =>
                TransportResult.Outcome ==
                    Switch2ProUsbHdRumbleTransportWriteOutcome.
                        OutcomeUncertain,
            _ => false,
        };

    internal static Switch2ProUsbOwnedOutputWriteAttempt Quarantine(
        in Switch2ProUsbHdRumbleTransportWriteResult transportResult,
        in Switch2ProUsbOwnedOutputOperationClaim retainedClaim = default) =>
        new(transportResult, retainedClaim,
            Switch2ProUsbOwnedOutputAttemptDisposition.LeaseQuarantined);
}

internal enum Switch2ProUsbOwnedOutputRetirementOutcome : byte
{
    Invalid = 0,

    /// <summary>
    /// The request did not authenticate the exact retained operation or could
    /// not enter its sole retirement lane. No native call was made and this
    /// result changes no lifecycle state.
    /// </summary>
    RequestRejected,

    /// <summary>
    /// The exact operation reached native/managed quiescence and its reusable
    /// submission storage was released. No later completion can occur.
    /// </summary>
    ExactOperationQuiescent,

    /// <summary>
    /// The exact operation remains strongly owned. The same claim may be used
    /// for another bounded cancellation/drain attempt; no replacement output
    /// may start meanwhile.
    /// </summary>
    RetainedForRetry,

    /// <summary>
    /// A dependency contradiction made the operation's terminal state
    /// ambiguous. The full composite must remain quarantined and this method
    /// must not be retried as though no native effect occurred.
    /// </summary>
    Quarantined,
}

internal readonly struct Switch2ProUsbOwnedOutputRetirementResult
{
    private Switch2ProUsbOwnedOutputRetirementResult(
        in Switch2ProUsbOwnedOutputOperationClaim claim,
        Switch2ProUsbOwnedOutputRetirementOutcome outcome)
    {
        Claim = claim;
        Outcome = outcome;
    }

    internal Switch2ProUsbOwnedOutputOperationClaim Claim { get; }

    internal Switch2ProUsbOwnedOutputRetirementOutcome Outcome { get; }

    internal bool HasValidInvariants() => Outcome ==
            Switch2ProUsbOwnedOutputRetirementOutcome.RequestRejected ||
        Claim.IsValid && Outcome is >=
            Switch2ProUsbOwnedOutputRetirementOutcome.
                ExactOperationQuiescent and <=
            Switch2ProUsbOwnedOutputRetirementOutcome.Quarantined;

    internal bool ExactOperationQuiescent => Outcome ==
        Switch2ProUsbOwnedOutputRetirementOutcome.ExactOperationQuiescent;

    internal bool ExactRetryPermitted => Outcome ==
        Switch2ProUsbOwnedOutputRetirementOutcome.RetainedForRetry;

    internal static Switch2ProUsbOwnedOutputRetirementResult Reject(
        in Switch2ProUsbOwnedOutputOperationClaim claim) => new(claim,
        Switch2ProUsbOwnedOutputRetirementOutcome.RequestRejected);

    internal static Switch2ProUsbOwnedOutputRetirementResult Quiescent(
        in Switch2ProUsbOwnedOutputOperationClaim claim) => new(claim,
        Switch2ProUsbOwnedOutputRetirementOutcome.ExactOperationQuiescent);

    internal static Switch2ProUsbOwnedOutputRetirementResult Retained(
        in Switch2ProUsbOwnedOutputOperationClaim claim) => new(claim,
        Switch2ProUsbOwnedOutputRetirementOutcome.RetainedForRetry);

    internal static Switch2ProUsbOwnedOutputRetirementResult Quarantine(
        in Switch2ProUsbOwnedOutputOperationClaim claim) => new(claim,
        Switch2ProUsbOwnedOutputRetirementOutcome.Quarantined);
}
