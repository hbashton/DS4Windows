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
using System.Threading;

namespace DS4Windows.Switch2;

/// <summary>
/// The closed, volatile command order required before a caller may measure the
/// input cadence of one exact Switch 2 Pro USB lifetime. Values are logical
/// transaction steps, not wire command ids.
/// </summary>
public enum Switch2ProUsbStartupStep : byte
{
    Invalid = 0,
    EnableUsbHidReports,
    SetPlayerLed,
    SetFeatureMask,
    EnableFeatures,
    SelectCommonInputReport,
    ReadFactoryPrimaryCalibration,
    ReadFactorySecondaryCalibration,
    ReadUserPrimaryCalibration,
    ReadUserSecondaryCalibration,
}

/// <summary>
/// The kind of exact response proof an admitted lease must produce.
/// </summary>
public enum Switch2ProUsbStartupResponseProofKind : byte
{
    Invalid = 0,
    InitializationResponseValidatedByCodec,
    PlayerLedResponseValidatedByCodec,
    FeatureResponseValidatedByCodec,
    CalibrationReadResponseValidatedByCodec,
}

/// <summary>
/// Causal outcome of one bounded command attempt. ProvenNotConsumed is the only
/// result that authorizes an exact retry of the same claim and bytes.
/// </summary>
public enum Switch2ProUsbStartupCommandOutcome : byte
{
    Invalid = 0,
    ExactResponseCompleted,
    ProvenNotConsumed,
    TimedOut,
    PossiblyConsumed,
}

public enum Switch2ProUsbStartupRetirementReason : byte
{
    Invalid = 0,
    Explicit,
    CommandOutcomeUncertain,
}

public enum Switch2ProUsbStartupRetirementOutcome : byte
{
    Invalid = 0,
    ExactLifetimeReleased,
    ProvenNotReleased,
    TimedOut,
    PossiblyReleased,
}

public enum Switch2ProUsbStartupTransactionState : byte
{
    Invalid = 0,
    Ready,
    CommandInFlight,
    RetryableCommand,
    Completed,
    RetirementInFlight,
    RetirementRetained,
    Retired,
    Quarantined,
}

/// <summary>
/// A successful startup transaction is permission to measure the exact input
/// lifetime. It is not evidence of a particular report rate.
/// </summary>
public enum Switch2ProUsbStartupInputRateStatus : byte
{
    Unavailable = 0,
    RequiresMeasurement,
}

public enum Switch2ProUsbStartupCreateFailure : byte
{
    None = 0,
    MissingLease,
    InvalidLifetime,
    LeaseLifetimeRejected,
    LeaseLifetimeMismatch,
}

public enum Switch2ProUsbStartupCommandFailure : byte
{
    None = 0,
    InvalidTimeout,
    OperationAlreadyInProgress,
    LifecycleClosed,
    RetirementRequired,
    RequestEncodingRejected,
    DependencyThrew,
    MalformedCompletion,
    WrongClaim,
    WrongStep,
    WrongResponseProof,
    CommandTimedOut,
    PossiblyConsumed,
    ProvenNotConsumed,
}

public enum Switch2ProUsbStartupRetirementFailure : byte
{
    None = 0,
    InvalidTimeout,
    OperationAlreadyInProgress,
    LifetimeQuarantined,
    DependencyThrew,
    MalformedCompletion,
    WrongClaim,
    WrongReason,
    TimedOut,
    PossiblyReleased,
    ProvenNotReleased,
}

/// <summary>
/// An opaque, exact command credential. The private transaction fence, exact
/// lease reference, admitted registration, generations, step, and sequence
/// must all match before a completion can advance the transaction.
/// </summary>
public readonly struct Switch2ProUsbStartupCommandClaim :
    IEquatable<Switch2ProUsbStartupCommandClaim>
{
    private readonly object transactionFence;
    private readonly ISwitch2ProUsbStartupCommandLease lease;
    private readonly Switch2PhysicalInputRegistration registration;

    internal Switch2ProUsbStartupCommandClaim(object transactionFence,
        ISwitch2ProUsbStartupCommandLease lease,
        in Switch2PhysicalInputLifetime lifetime,
        Switch2ProUsbStartupStep step, ulong sequence)
    {
        this.transactionFence = transactionFence;
        this.lease = lease;
        registration = lifetime.Registration;
        DeviceGeneration = lifetime.SessionDescriptor.DeviceGeneration;
        TransportGeneration = lifetime.SessionDescriptor.TransportGeneration;
        Step = step;
        Sequence = sequence;
    }

    public ulong DeviceGeneration { get; }

    public ulong TransportGeneration { get; }

    public Switch2ProUsbStartupStep Step { get; }

    public ulong Sequence { get; }

    public bool IsValid => transactionFence != null && lease != null &&
        registration.IsValid && DeviceGeneration != 0 &&
        TransportGeneration != 0 && IsSupportedStep(Step) && Sequence != 0;

    internal bool Authenticates(object expectedFence,
        ISwitch2ProUsbStartupCommandLease expectedLease,
        in Switch2PhysicalInputLifetime expectedLifetime,
        Switch2ProUsbStartupStep expectedStep, ulong expectedSequence) =>
        ReferenceEquals(transactionFence, expectedFence) &&
        ReferenceEquals(lease, expectedLease) &&
        registration.Equals(expectedLifetime.Registration) &&
        DeviceGeneration ==
            expectedLifetime.SessionDescriptor.DeviceGeneration &&
        TransportGeneration ==
            expectedLifetime.SessionDescriptor.TransportGeneration &&
        Step == expectedStep && Sequence == expectedSequence;

    /// <summary>
    /// Authenticates only the concrete lease/lifetime boundary. The startup
    /// transaction remains the sole authority for its private fence, ordered
    /// step, and sequence; a transport lease must nevertheless reject a valid
    /// claim minted for another lease or physical registration before I/O.
    /// </summary>
    internal bool AuthenticatesLease(
        ISwitch2ProUsbStartupCommandLease expectedLease,
        in Switch2PhysicalInputLifetime expectedLifetime) => IsValid &&
        ReferenceEquals(lease, expectedLease) &&
        registration.Equals(expectedLifetime.Registration) &&
        DeviceGeneration ==
            expectedLifetime.SessionDescriptor.DeviceGeneration &&
        TransportGeneration ==
            expectedLifetime.SessionDescriptor.TransportGeneration;

    public bool Equals(Switch2ProUsbStartupCommandClaim other) =>
        ReferenceEquals(transactionFence, other.transactionFence) &&
        ReferenceEquals(lease, other.lease) &&
        registration.Equals(other.registration) &&
        DeviceGeneration == other.DeviceGeneration &&
        TransportGeneration == other.TransportGeneration &&
        Step == other.Step && Sequence == other.Sequence;

    public override bool Equals(object obj) =>
        obj is Switch2ProUsbStartupCommandClaim other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        transactionFence == null ? 0 :
            RuntimeHelpers.GetHashCode(transactionFence),
        lease == null ? 0 : RuntimeHelpers.GetHashCode(lease), registration,
        DeviceGeneration, TransportGeneration, Step, Sequence);

    private static bool IsSupportedStep(Switch2ProUsbStartupStep step) =>
        step is >= Switch2ProUsbStartupStep.EnableUsbHidReports and
            <= Switch2ProUsbStartupStep.ReadUserSecondaryCalibration;
}

/// <summary>
/// Typed completion evidence returned by the abstract lease. A completion is
/// useful only to the transaction that issued its exact claim.
/// </summary>
public readonly struct Switch2ProUsbStartupCommandCompletion
{
    private Switch2ProUsbStartupCommandCompletion(
        in Switch2ProUsbStartupCommandClaim claim,
        Switch2ProUsbStartupStep reportedStep,
        Switch2ProUsbStartupCommandOutcome outcome,
        Switch2ProUsbStartupResponseProofKind responseProof,
        ReadOnlyMemory<byte> responsePayload = default)
    {
        Claim = claim;
        ReportedStep = reportedStep;
        Outcome = outcome;
        ResponseProof = responseProof;
        ResponsePayload = responsePayload;
    }

    public Switch2ProUsbStartupCommandClaim Claim { get; }

    public Switch2ProUsbStartupStep ReportedStep { get; }

    public Switch2ProUsbStartupCommandOutcome Outcome { get; }

    public Switch2ProUsbStartupResponseProofKind ResponseProof { get; }

    internal ReadOnlyMemory<byte> ResponsePayload { get; }

    public static Switch2ProUsbStartupCommandCompletion ExactResponse(
        in Switch2ProUsbStartupCommandClaim claim,
        Switch2ProUsbStartupStep reportedStep,
        Switch2ProUsbStartupResponseProofKind responseProof) =>
        new(claim, reportedStep,
            Switch2ProUsbStartupCommandOutcome.ExactResponseCompleted,
            responseProof);

    internal static Switch2ProUsbStartupCommandCompletion ExactResponse(
        in Switch2ProUsbStartupCommandClaim claim,
        Switch2ProUsbStartupStep reportedStep,
        Switch2ProUsbStartupResponseProofKind responseProof,
        ReadOnlyMemory<byte> responsePayload) =>
        new(claim, reportedStep,
            Switch2ProUsbStartupCommandOutcome.ExactResponseCompleted,
            responseProof, responsePayload);

    public static Switch2ProUsbStartupCommandCompletion ProvenNotConsumed(
        in Switch2ProUsbStartupCommandClaim claim,
        Switch2ProUsbStartupStep reportedStep) =>
        new(claim, reportedStep,
            Switch2ProUsbStartupCommandOutcome.ProvenNotConsumed, default);

    public static Switch2ProUsbStartupCommandCompletion TimedOut(
        in Switch2ProUsbStartupCommandClaim claim,
        Switch2ProUsbStartupStep reportedStep) =>
        new(claim, reportedStep,
            Switch2ProUsbStartupCommandOutcome.TimedOut, default);

    public static Switch2ProUsbStartupCommandCompletion PossiblyConsumed(
        in Switch2ProUsbStartupCommandClaim claim,
        Switch2ProUsbStartupStep reportedStep) =>
        new(claim, reportedStep,
            Switch2ProUsbStartupCommandOutcome.PossiblyConsumed, default);
}

/// <summary>
/// Opaque exact-lifetime retirement credential. A ProvenNotReleased result may
/// retry only this same credential; uncertain release is quarantined instead.
/// </summary>
public readonly struct Switch2ProUsbStartupRetirementClaim :
    IEquatable<Switch2ProUsbStartupRetirementClaim>
{
    private readonly object transactionFence;
    private readonly ISwitch2ProUsbStartupCommandLease lease;
    private readonly Switch2PhysicalInputRegistration registration;

    internal Switch2ProUsbStartupRetirementClaim(object transactionFence,
        ISwitch2ProUsbStartupCommandLease lease,
        in Switch2PhysicalInputLifetime lifetime,
        Switch2ProUsbStartupRetirementReason reason, ulong sequence)
    {
        this.transactionFence = transactionFence;
        this.lease = lease;
        registration = lifetime.Registration;
        DeviceGeneration = lifetime.SessionDescriptor.DeviceGeneration;
        TransportGeneration = lifetime.SessionDescriptor.TransportGeneration;
        Reason = reason;
        Sequence = sequence;
    }

    public ulong DeviceGeneration { get; }

    public ulong TransportGeneration { get; }

    public Switch2ProUsbStartupRetirementReason Reason { get; }

    public ulong Sequence { get; }

    public bool IsValid => transactionFence != null && lease != null &&
        registration.IsValid && DeviceGeneration != 0 &&
        TransportGeneration != 0 &&
        Reason is Switch2ProUsbStartupRetirementReason.Explicit or
            Switch2ProUsbStartupRetirementReason.CommandOutcomeUncertain &&
        Sequence != 0;

    internal bool Authenticates(object expectedFence,
        ISwitch2ProUsbStartupCommandLease expectedLease,
        in Switch2PhysicalInputLifetime expectedLifetime,
        Switch2ProUsbStartupRetirementReason expectedReason,
        ulong expectedSequence) =>
        ReferenceEquals(transactionFence, expectedFence) &&
        ReferenceEquals(lease, expectedLease) &&
        registration.Equals(expectedLifetime.Registration) &&
        DeviceGeneration ==
            expectedLifetime.SessionDescriptor.DeviceGeneration &&
        TransportGeneration ==
            expectedLifetime.SessionDescriptor.TransportGeneration &&
        Reason == expectedReason && Sequence == expectedSequence;

    /// <summary>
    /// Authenticates only the concrete lease/lifetime boundary. The startup
    /// transaction remains responsible for its private retirement fence and
    /// sequence. This prevents one valid transaction claim from retiring a
    /// different physical lease that happens to use equal generation values.
    /// </summary>
    internal bool AuthenticatesLease(
        ISwitch2ProUsbStartupCommandLease expectedLease,
        in Switch2PhysicalInputLifetime expectedLifetime) => IsValid &&
        ReferenceEquals(lease, expectedLease) &&
        registration.Equals(expectedLifetime.Registration) &&
        DeviceGeneration ==
            expectedLifetime.SessionDescriptor.DeviceGeneration &&
        TransportGeneration ==
            expectedLifetime.SessionDescriptor.TransportGeneration;

    public bool Equals(Switch2ProUsbStartupRetirementClaim other) =>
        ReferenceEquals(transactionFence, other.transactionFence) &&
        ReferenceEquals(lease, other.lease) &&
        registration.Equals(other.registration) &&
        DeviceGeneration == other.DeviceGeneration &&
        TransportGeneration == other.TransportGeneration &&
        Reason == other.Reason && Sequence == other.Sequence;

    public override bool Equals(object obj) =>
        obj is Switch2ProUsbStartupRetirementClaim other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        transactionFence == null ? 0 :
            RuntimeHelpers.GetHashCode(transactionFence),
        lease == null ? 0 : RuntimeHelpers.GetHashCode(lease), registration,
        DeviceGeneration, TransportGeneration, Reason, Sequence);
}

public readonly struct Switch2ProUsbStartupRetirementCompletion
{
    private Switch2ProUsbStartupRetirementCompletion(
        in Switch2ProUsbStartupRetirementClaim claim,
        Switch2ProUsbStartupRetirementReason reportedReason,
        Switch2ProUsbStartupRetirementOutcome outcome)
    {
        Claim = claim;
        ReportedReason = reportedReason;
        Outcome = outcome;
    }

    public Switch2ProUsbStartupRetirementClaim Claim { get; }

    public Switch2ProUsbStartupRetirementReason ReportedReason { get; }

    public Switch2ProUsbStartupRetirementOutcome Outcome { get; }

    public static Switch2ProUsbStartupRetirementCompletion Released(
        in Switch2ProUsbStartupRetirementClaim claim,
        Switch2ProUsbStartupRetirementReason reportedReason) =>
        new(claim, reportedReason,
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased);

    public static Switch2ProUsbStartupRetirementCompletion ProvenNotReleased(
        in Switch2ProUsbStartupRetirementClaim claim,
        Switch2ProUsbStartupRetirementReason reportedReason) =>
        new(claim, reportedReason,
            Switch2ProUsbStartupRetirementOutcome.ProvenNotReleased);

    public static Switch2ProUsbStartupRetirementCompletion TimedOut(
        in Switch2ProUsbStartupRetirementClaim claim,
        Switch2ProUsbStartupRetirementReason reportedReason) =>
        new(claim, reportedReason,
            Switch2ProUsbStartupRetirementOutcome.TimedOut);

    public static Switch2ProUsbStartupRetirementCompletion PossiblyReleased(
        in Switch2ProUsbStartupRetirementClaim claim,
        Switch2ProUsbStartupRetirementReason reportedReason) =>
        new(claim, reportedReason,
            Switch2ProUsbStartupRetirementOutcome.PossiblyReleased);
}

/// <summary>
/// Exclusive abstract owner of one exact admitted command-interface lifetime.
/// timeoutMilliseconds is a maximum cumulative managed wait budget for native
/// quiescence. Concrete Windows accounting begins before submission/cancellation
/// and deducts synchronous phase time before waiting, but synchronous OS begin,
/// cancel, free, and handle-close APIs expose no hard wall-clock deadline.
/// ExactResponseCompleted means the request was fully written, an exact causally
/// matching response was validated, and no later completion remains. For
/// feature steps, the concrete lease owns the still-unimplemented response
/// validator. ProvenNotConsumed is a stronger guarantee that no byte was
/// accepted or queued and no completion can arrive, so only that result permits
/// an exact same-claim retry.
/// </summary>
public interface ISwitch2ProUsbStartupCommandLease
{
    Switch2PhysicalInputLifetime Lifetime { get; }

    Switch2ProUsbStartupCommandCompletion Execute(
        in Switch2ProUsbStartupCommandClaim claim,
        ReadOnlySpan<byte> exactRequest, int timeoutMilliseconds);

    /// <summary>
    /// Uses the supplied cumulative managed quiescence-wait budget while
    /// retiring every command operation and releasing this exact lease.
    /// Synchronous native cancel/free/close calls do not have a hard deadline.
    /// ExactLifetimeReleased proves no later native or managed activity is
    /// possible. A possibly-released or malformed result must never be retried.
    /// </summary>
    Switch2ProUsbStartupRetirementCompletion Retire(
        in Switch2ProUsbStartupRetirementClaim claim,
        int timeoutMilliseconds);
}

/// <summary>
/// Marker for an owned Pro USB command lease whose Execute implementation also
/// admits the four closed read-only calibration steps. It grants no second I/O
/// path: calibration and volatile startup still serialize through the exact
/// same MI_01 handle and command-lane state machine.
/// </summary>
internal interface ISwitch2ProUsbCalibrationCommandLease :
    ISwitch2ProUsbStartupCommandLease
{
}

public readonly struct Switch2ProUsbStartupAdvanceResult
{
    internal Switch2ProUsbStartupAdvanceResult(
        Switch2ProUsbStartupStep step,
        Switch2ProUsbStartupCommandFailure commandFailure,
        Switch2ProUsbStartupRetirementFailure retirementFailure,
        Switch2ProUsbStartupTransactionState state)
    {
        Step = step;
        CommandFailure = commandFailure;
        RetirementFailure = retirementFailure;
        State = state;
    }

    public Switch2ProUsbStartupStep Step { get; }

    public Switch2ProUsbStartupCommandFailure CommandFailure { get; }

    public Switch2ProUsbStartupRetirementFailure RetirementFailure { get; }

    public Switch2ProUsbStartupTransactionState State { get; }

    public bool StepCompleted =>
        CommandFailure == Switch2ProUsbStartupCommandFailure.None;

    public bool ExactRetryPermitted =>
        CommandFailure ==
            Switch2ProUsbStartupCommandFailure.ProvenNotConsumed &&
        State == Switch2ProUsbStartupTransactionState.RetryableCommand;

    public bool RequiresQuarantine =>
        State == Switch2ProUsbStartupTransactionState.Quarantined;
}

/// <summary>
/// Dormant, allocation-free state machine for the five closed volatile startup
/// requests. It owns ordering and lifetime uncertainty only; it performs no
/// discovery, Windows I/O, delayed retry, input read, or hardware operation.
/// </summary>
public sealed class Switch2ProUsbStartupTransaction
{
    public const int RequiredStepCount = 5;
    public const int MaximumOperationTimeoutMilliseconds = 5_000;

    private readonly object gate = new();
    private readonly object transactionFence = new();
    private readonly ISwitch2ProUsbStartupCommandLease lease;
    private readonly Switch2PhysicalInputLifetime lifetime;

    private Switch2ProUsbStartupTransactionState state =
        Switch2ProUsbStartupTransactionState.Ready;
    private Switch2ProUsbStartupStep nextStep =
        Switch2ProUsbStartupStep.EnableUsbHidReports;
    private Switch2ProUsbStartupCommandClaim activeCommandClaim;
    private Switch2ProUsbStartupRetirementClaim activeRetirementClaim;
    private ulong commandSequence;
    private ulong retirementSequence;
    private bool operationInProgress;

    private Switch2ProUsbStartupTransaction(
        ISwitch2ProUsbStartupCommandLease lease,
        in Switch2PhysicalInputLifetime lifetime)
    {
        this.lease = lease;
        this.lifetime = lifetime;
    }

    public Switch2PhysicalInputLifetime Lifetime => lifetime;

    public Switch2ProUsbStartupTransactionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public Switch2ProUsbStartupStep NextStep
    {
        get
        {
            lock (gate)
            {
                return state is Switch2ProUsbStartupTransactionState.Ready or
                        Switch2ProUsbStartupTransactionState.RetryableCommand ?
                    nextStep : Switch2ProUsbStartupStep.Invalid;
            }
        }
    }

    public Switch2ProUsbStartupInputRateStatus InputRateStatus
    {
        get
        {
            lock (gate)
            {
                return state ==
                        Switch2ProUsbStartupTransactionState.Completed ?
                    Switch2ProUsbStartupInputRateStatus.RequiresMeasurement :
                    Switch2ProUsbStartupInputRateStatus.Unavailable;
            }
        }
    }

    public static bool TryCreate(ISwitch2ProUsbStartupCommandLease lease,
        in Switch2PhysicalInputLifetime expectedLifetime,
        out Switch2ProUsbStartupTransaction transaction,
        out Switch2ProUsbStartupCreateFailure failure)
    {
        transaction = null;
        if (lease == null)
        {
            failure = Switch2ProUsbStartupCreateFailure.MissingLease;
            return false;
        }
        if (!expectedLifetime.IsValid)
        {
            failure = Switch2ProUsbStartupCreateFailure.InvalidLifetime;
            return false;
        }

        Switch2PhysicalInputLifetime leaseLifetime;
        try
        {
            leaseLifetime = lease.Lifetime;
        }
        catch
        {
            failure =
                Switch2ProUsbStartupCreateFailure.LeaseLifetimeRejected;
            return false;
        }
        if (!leaseLifetime.Equals(expectedLifetime))
        {
            failure =
                Switch2ProUsbStartupCreateFailure.LeaseLifetimeMismatch;
            return false;
        }

        transaction = new Switch2ProUsbStartupTransaction(lease,
            expectedLifetime);
        failure = Switch2ProUsbStartupCreateFailure.None;
        return true;
    }

    internal bool AuthenticatesCompleted(
        ISwitch2ProUsbStartupCommandLease expectedLease,
        in Switch2PhysicalInputLifetime expectedLifetime)
    {
        lock (gate)
        {
            return state == Switch2ProUsbStartupTransactionState.Completed &&
                !operationInProgress && ReferenceEquals(lease,
                    expectedLease) && lifetime.Equals(expectedLifetime);
        }
    }

    /// <summary>
    /// Attempts exactly the current step once. No retry is performed inside
    /// this call. ProvenNotConsumed retains the same claim for an explicit exact
    /// retry; every uncertain outcome immediately attempts bounded retirement.
    /// </summary>
    public bool TryAdvance(int commandTimeoutMilliseconds,
        int retirementTimeoutMilliseconds,
        out Switch2ProUsbStartupAdvanceResult result)
    {
        if (!IsValidTimeout(commandTimeoutMilliseconds) ||
            !IsValidTimeout(retirementTimeoutMilliseconds))
        {
            result = SnapshotAdvanceResult(
                Switch2ProUsbStartupStep.Invalid,
                Switch2ProUsbStartupCommandFailure.InvalidTimeout,
                Switch2ProUsbStartupRetirementFailure.None);
            return false;
        }

        Switch2ProUsbStartupStep step;
        Switch2ProUsbStartupCommandClaim claim;
        lock (gate)
        {
            if (operationInProgress)
            {
                result = CreateAdvanceResultLocked(nextStep,
                    Switch2ProUsbStartupCommandFailure.
                        OperationAlreadyInProgress,
                    Switch2ProUsbStartupRetirementFailure.None);
                return false;
            }
            if (state == Switch2ProUsbStartupTransactionState.Completed)
            {
                result = CreateAdvanceResultLocked(
                    Switch2ProUsbStartupStep.Invalid,
                    Switch2ProUsbStartupCommandFailure.None,
                    Switch2ProUsbStartupRetirementFailure.None);
                return true;
            }
            if (state ==
                Switch2ProUsbStartupTransactionState.RetirementRetained)
            {
                result = CreateAdvanceResultLocked(
                    Switch2ProUsbStartupStep.Invalid,
                    Switch2ProUsbStartupCommandFailure.RetirementRequired,
                    Switch2ProUsbStartupRetirementFailure.
                        ProvenNotReleased);
                return false;
            }
            if (state is Switch2ProUsbStartupTransactionState.Retired or
                    Switch2ProUsbStartupTransactionState.Quarantined)
            {
                result = CreateAdvanceResultLocked(
                    Switch2ProUsbStartupStep.Invalid,
                    Switch2ProUsbStartupCommandFailure.LifecycleClosed,
                    state ==
                        Switch2ProUsbStartupTransactionState.Quarantined ?
                        Switch2ProUsbStartupRetirementFailure.
                            LifetimeQuarantined :
                        Switch2ProUsbStartupRetirementFailure.None);
                return false;
            }
            if (state is not (Switch2ProUsbStartupTransactionState.Ready or
                    Switch2ProUsbStartupTransactionState.RetryableCommand))
            {
                result = CreateAdvanceResultLocked(
                    Switch2ProUsbStartupStep.Invalid,
                    Switch2ProUsbStartupCommandFailure.LifecycleClosed,
                    Switch2ProUsbStartupRetirementFailure.None);
                return false;
            }

            step = nextStep;
            if (state ==
                Switch2ProUsbStartupTransactionState.RetryableCommand)
            {
                claim = activeCommandClaim;
            }
            else
            {
                commandSequence++;
                claim = new Switch2ProUsbStartupCommandClaim(
                    transactionFence, lease, lifetime, step,
                    commandSequence);
                activeCommandClaim = claim;
            }
            operationInProgress = true;
            state = Switch2ProUsbStartupTransactionState.CommandInFlight;
        }

        Span<byte> request = stackalloc byte[
            Switch2UsbCommandCodec.InitializationRequestLength];
        if (!TryWriteAndValidateRequest(step, request,
                out int requestLength))
        {
            return CompleteUnsafeCommand(step, claim,
                Switch2ProUsbStartupCommandFailure.RequestEncodingRejected,
                retirementTimeoutMilliseconds, out result);
        }

        Switch2ProUsbStartupCommandCompletion completion = default;
        bool dependencyThrew = false;
        try
        {
            completion = lease.Execute(claim,
                request.Slice(0, requestLength),
                commandTimeoutMilliseconds);
        }
        catch
        {
            dependencyThrew = true;
        }

        Switch2ProUsbStartupCommandFailure commandFailure = dependencyThrew ?
            Switch2ProUsbStartupCommandFailure.DependencyThrew :
            ClassifyCommandCompletion(step, claim, completion);
        if (commandFailure == Switch2ProUsbStartupCommandFailure.None)
        {
            lock (gate)
            {
                activeCommandClaim = default;
                nextStep = GetFollowingStep(step);
                state = nextStep == Switch2ProUsbStartupStep.Invalid ?
                    Switch2ProUsbStartupTransactionState.Completed :
                    Switch2ProUsbStartupTransactionState.Ready;
                operationInProgress = false;
                Monitor.PulseAll(gate);
                result = CreateAdvanceResultLocked(step,
                    Switch2ProUsbStartupCommandFailure.None,
                    Switch2ProUsbStartupRetirementFailure.None);
            }
            return true;
        }
        if (commandFailure ==
            Switch2ProUsbStartupCommandFailure.ProvenNotConsumed)
        {
            lock (gate)
            {
                state = Switch2ProUsbStartupTransactionState.RetryableCommand;
                operationInProgress = false;
                Monitor.PulseAll(gate);
                result = CreateAdvanceResultLocked(step, commandFailure,
                    Switch2ProUsbStartupRetirementFailure.None);
            }
            return false;
        }

        return CompleteUnsafeCommand(step, claim, commandFailure,
            retirementTimeoutMilliseconds, out result);
    }

    /// <summary>
    /// Applies the supplied cumulative managed quiescence-wait budget while
    /// retiring the exact lease. An exact ProvenNotReleased result retains the
    /// same retirement claim for retry. Any uncertain release is quarantined
    /// and cannot be retried. Synchronous native cancellation/free/close is not
    /// covered by a hard wall-clock bound.
    /// </summary>
    public bool TryRetire(int timeoutMilliseconds,
        out Switch2ProUsbStartupRetirementFailure failure)
    {
        if (!IsValidTimeout(timeoutMilliseconds))
        {
            failure =
                Switch2ProUsbStartupRetirementFailure.InvalidTimeout;
            return false;
        }

        Switch2ProUsbStartupRetirementClaim claim;
        lock (gate)
        {
            if (operationInProgress)
            {
                failure = Switch2ProUsbStartupRetirementFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            if (state == Switch2ProUsbStartupTransactionState.Retired)
            {
                failure = Switch2ProUsbStartupRetirementFailure.None;
                return true;
            }
            if (state == Switch2ProUsbStartupTransactionState.Quarantined)
            {
                failure = Switch2ProUsbStartupRetirementFailure.
                    LifetimeQuarantined;
                return false;
            }

            if (state ==
                Switch2ProUsbStartupTransactionState.RetirementRetained)
            {
                claim = activeRetirementClaim;
            }
            else
            {
                retirementSequence++;
                claim = new Switch2ProUsbStartupRetirementClaim(
                    transactionFence, lease, lifetime,
                    Switch2ProUsbStartupRetirementReason.Explicit,
                    retirementSequence);
                activeRetirementClaim = claim;
                activeCommandClaim = default;
            }
            operationInProgress = true;
            state = Switch2ProUsbStartupTransactionState.RetirementInFlight;
        }

        failure = InvokeAndCompleteRetirement(claim, timeoutMilliseconds);
        return failure == Switch2ProUsbStartupRetirementFailure.None;
    }

    private bool CompleteUnsafeCommand(Switch2ProUsbStartupStep step,
        in Switch2ProUsbStartupCommandClaim commandClaim,
        Switch2ProUsbStartupCommandFailure commandFailure,
        int retirementTimeoutMilliseconds,
        out Switch2ProUsbStartupAdvanceResult result)
    {
        Switch2ProUsbStartupRetirementClaim retirementClaim;
        lock (gate)
        {
            activeCommandClaim = default;
            retirementSequence++;
            retirementClaim = new Switch2ProUsbStartupRetirementClaim(
                transactionFence, lease, lifetime,
                Switch2ProUsbStartupRetirementReason.CommandOutcomeUncertain,
                retirementSequence);
            activeRetirementClaim = retirementClaim;
            state = Switch2ProUsbStartupTransactionState.RetirementInFlight;
        }

        Switch2ProUsbStartupRetirementFailure retirementFailure =
            InvokeAndCompleteRetirement(retirementClaim,
                retirementTimeoutMilliseconds);
        lock (gate)
        {
            result = CreateAdvanceResultLocked(step, commandFailure,
                retirementFailure);
        }
        return false;
    }

    private Switch2ProUsbStartupRetirementFailure
        InvokeAndCompleteRetirement(
            in Switch2ProUsbStartupRetirementClaim claim,
            int timeoutMilliseconds)
    {
        Switch2ProUsbStartupRetirementCompletion completion = default;
        bool dependencyThrew = false;
        try
        {
            completion = lease.Retire(claim, timeoutMilliseconds);
        }
        catch
        {
            dependencyThrew = true;
        }

        Switch2ProUsbStartupRetirementFailure failure = dependencyThrew ?
            Switch2ProUsbStartupRetirementFailure.DependencyThrew :
            ClassifyRetirementCompletion(claim, completion);
        lock (gate)
        {
            switch (failure)
            {
                case Switch2ProUsbStartupRetirementFailure.None:
                    activeRetirementClaim = default;
                    state = Switch2ProUsbStartupTransactionState.Retired;
                    break;
                case Switch2ProUsbStartupRetirementFailure.ProvenNotReleased:
                    state = Switch2ProUsbStartupTransactionState.
                        RetirementRetained;
                    break;
                default:
                    state = Switch2ProUsbStartupTransactionState.Quarantined;
                    break;
            }
            operationInProgress = false;
            Monitor.PulseAll(gate);
        }
        return failure;
    }

    private Switch2ProUsbStartupCommandFailure ClassifyCommandCompletion(
        Switch2ProUsbStartupStep step,
        in Switch2ProUsbStartupCommandClaim claim,
        in Switch2ProUsbStartupCommandCompletion completion)
    {
        if (completion.Outcome ==
            Switch2ProUsbStartupCommandOutcome.Invalid)
        {
            return Switch2ProUsbStartupCommandFailure.MalformedCompletion;
        }
        if (!claim.Authenticates(transactionFence, lease, lifetime, step,
                claim.Sequence) || !completion.Claim.Equals(claim))
        {
            return Switch2ProUsbStartupCommandFailure.WrongClaim;
        }
        if (completion.ReportedStep != step)
        {
            return Switch2ProUsbStartupCommandFailure.WrongStep;
        }

        switch (completion.Outcome)
        {
            case Switch2ProUsbStartupCommandOutcome.ExactResponseCompleted:
                return completion.ResponseProof == GetRequiredProof(step) &&
                        completion.ResponsePayload.IsEmpty ?
                    Switch2ProUsbStartupCommandFailure.None :
                    Switch2ProUsbStartupCommandFailure.WrongResponseProof;
            case Switch2ProUsbStartupCommandOutcome.ProvenNotConsumed:
                return completion.ResponseProof == default &&
                        completion.ResponsePayload.IsEmpty ?
                    Switch2ProUsbStartupCommandFailure.ProvenNotConsumed :
                    Switch2ProUsbStartupCommandFailure.MalformedCompletion;
            case Switch2ProUsbStartupCommandOutcome.TimedOut:
                return completion.ResponseProof == default &&
                        completion.ResponsePayload.IsEmpty ?
                    Switch2ProUsbStartupCommandFailure.CommandTimedOut :
                    Switch2ProUsbStartupCommandFailure.MalformedCompletion;
            case Switch2ProUsbStartupCommandOutcome.PossiblyConsumed:
                return completion.ResponseProof == default &&
                        completion.ResponsePayload.IsEmpty ?
                    Switch2ProUsbStartupCommandFailure.PossiblyConsumed :
                    Switch2ProUsbStartupCommandFailure.MalformedCompletion;
            default:
                return Switch2ProUsbStartupCommandFailure.
                    MalformedCompletion;
        }
    }

    private Switch2ProUsbStartupRetirementFailure
        ClassifyRetirementCompletion(
            in Switch2ProUsbStartupRetirementClaim claim,
            in Switch2ProUsbStartupRetirementCompletion completion)
    {
        if (completion.Outcome ==
            Switch2ProUsbStartupRetirementOutcome.Invalid)
        {
            return Switch2ProUsbStartupRetirementFailure.
                MalformedCompletion;
        }
        if (!claim.Authenticates(transactionFence, lease, lifetime,
                claim.Reason, claim.Sequence) ||
            !completion.Claim.Equals(claim))
        {
            return Switch2ProUsbStartupRetirementFailure.WrongClaim;
        }
        if (completion.ReportedReason != claim.Reason)
        {
            return Switch2ProUsbStartupRetirementFailure.WrongReason;
        }

        return completion.Outcome switch
        {
            Switch2ProUsbStartupRetirementOutcome.ExactLifetimeReleased =>
                Switch2ProUsbStartupRetirementFailure.None,
            Switch2ProUsbStartupRetirementOutcome.ProvenNotReleased =>
                Switch2ProUsbStartupRetirementFailure.ProvenNotReleased,
            Switch2ProUsbStartupRetirementOutcome.TimedOut =>
                Switch2ProUsbStartupRetirementFailure.TimedOut,
            Switch2ProUsbStartupRetirementOutcome.PossiblyReleased =>
                Switch2ProUsbStartupRetirementFailure.PossiblyReleased,
            _ => Switch2ProUsbStartupRetirementFailure.MalformedCompletion,
        };
    }

    private Switch2ProUsbStartupAdvanceResult SnapshotAdvanceResult(
        Switch2ProUsbStartupStep step,
        Switch2ProUsbStartupCommandFailure commandFailure,
        Switch2ProUsbStartupRetirementFailure retirementFailure)
    {
        lock (gate)
        {
            return CreateAdvanceResultLocked(step, commandFailure,
                retirementFailure);
        }
    }

    private Switch2ProUsbStartupAdvanceResult CreateAdvanceResultLocked(
        Switch2ProUsbStartupStep step,
        Switch2ProUsbStartupCommandFailure commandFailure,
        Switch2ProUsbStartupRetirementFailure retirementFailure) =>
        new(step, commandFailure, retirementFailure, state);

    private static bool TryWriteAndValidateRequest(
        Switch2ProUsbStartupStep step, Span<byte> request,
        out int requestLength)
    {
        const Switch2UsbFeatureMask Mask =
            Switch2UsbFeatureMask.ButtonsSticksImuAndRumble;
        requestLength = Switch2UsbCommandCodec.InitializationRequestLength;
        switch (step)
        {
            case Switch2ProUsbStartupStep.EnableUsbHidReports:
                return Switch2UsbCommandCodec.TryWriteInitializationRequest(
                        Switch2UsbInitializationStep.EnableUsbHidReports,
                        request, out _) &&
                    Switch2UsbCommandCodec.TryValidateInitializationRequest(
                        request,
                        Switch2UsbInitializationStep.EnableUsbHidReports,
                        out _);
            case Switch2ProUsbStartupStep.SetPlayerLed:
                requestLength = Switch2UsbCommandCodec.RequestLength;
                Span<byte> ledRequest = request.Slice(0, requestLength);
                return Switch2UsbCommandCodec.TryWritePlayerLedRequest(
                        Switch2PlayerLedCommand.Player1Only, ledRequest,
                        out _) &&
                    Switch2UsbCommandCodec.TryValidatePlayerLedRequest(
                        ledRequest, Switch2PlayerLedCommand.Player1Only,
                        out _);
            case Switch2ProUsbStartupStep.SetFeatureMask:
                return Switch2UsbCommandCodec.TryWriteFeatureRequest(
                        Switch2UsbFeatureStep.SetFeatureMask, Mask, request,
                        out _) &&
                    Switch2UsbCommandCodec.TryValidateFeatureRequest(request,
                        Switch2UsbFeatureStep.SetFeatureMask, Mask, out _);
            case Switch2ProUsbStartupStep.EnableFeatures:
                return Switch2UsbCommandCodec.TryWriteFeatureRequest(
                        Switch2UsbFeatureStep.EnableFeatures, Mask, request,
                        out _) &&
                    Switch2UsbCommandCodec.TryValidateFeatureRequest(request,
                        Switch2UsbFeatureStep.EnableFeatures, Mask, out _);
            case Switch2ProUsbStartupStep.SelectCommonInputReport:
                return Switch2UsbCommandCodec.TryWriteInitializationRequest(
                        Switch2UsbInitializationStep.SelectCommonInputReport,
                        request, out _) &&
                    Switch2UsbCommandCodec.TryValidateInitializationRequest(
                        request,
                        Switch2UsbInitializationStep.SelectCommonInputReport,
                        out _);
            default:
                return false;
        }
    }

    private static Switch2ProUsbStartupResponseProofKind GetRequiredProof(
        Switch2ProUsbStartupStep step) => step switch
        {
            Switch2ProUsbStartupStep.EnableUsbHidReports or
                Switch2ProUsbStartupStep.SelectCommonInputReport =>
                Switch2ProUsbStartupResponseProofKind.
                    InitializationResponseValidatedByCodec,
            Switch2ProUsbStartupStep.SetPlayerLed =>
                Switch2ProUsbStartupResponseProofKind.
                    PlayerLedResponseValidatedByCodec,
            Switch2ProUsbStartupStep.SetFeatureMask or
                Switch2ProUsbStartupStep.EnableFeatures =>
                Switch2ProUsbStartupResponseProofKind.
                    FeatureResponseValidatedByCodec,
            _ => Switch2ProUsbStartupResponseProofKind.Invalid,
        };

    private static Switch2ProUsbStartupStep GetFollowingStep(
        Switch2ProUsbStartupStep step) => step switch
        {
            Switch2ProUsbStartupStep.EnableUsbHidReports =>
                Switch2ProUsbStartupStep.SetPlayerLed,
            Switch2ProUsbStartupStep.SetPlayerLed =>
                Switch2ProUsbStartupStep.SetFeatureMask,
            Switch2ProUsbStartupStep.SetFeatureMask =>
                Switch2ProUsbStartupStep.EnableFeatures,
            Switch2ProUsbStartupStep.EnableFeatures =>
                Switch2ProUsbStartupStep.SelectCommonInputReport,
            _ => Switch2ProUsbStartupStep.Invalid,
        };

    private static bool IsValidTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds is >= 0 and
            <= MaximumOperationTimeoutMilliseconds;
}
