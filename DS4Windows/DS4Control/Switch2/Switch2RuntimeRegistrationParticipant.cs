/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;

namespace DS4Windows.Switch2;

internal enum Switch2RuntimeRegistrationParticipantOperation : byte
{
    Invalid = 0,
    AdoptBoundSlot,
    Subscribe,
    PrepareActivation,
    CommitPrepared,
    AbortPrepared,
    AbortUnpublished,
    ArmRetirement,
    WaitForPublicationAvailability,
    StopAndQuiesce,
    Unsubscribe,
    Remove,
}

internal enum Switch2RuntimeRegistrationParticipantOutcome : byte
{
    Invalid = 0,
    Succeeded,
    ProvenRejected,
    OutcomeUncertain,
}

internal enum Switch2RuntimeRegistrationParticipantFailureKind : byte
{
    None = 0,
    InvalidArgument,
    InvalidCredential,
    StaleCredential,
    AlreadyConsumed,
    InvalidTimeout,
    InvalidState,
    OperationAlreadyInProgress,
    SubscriptionRejected,
    PrepareRejected,
    CommitRejected,
    AbortRejected,
    RetirementArmRejected,
    PublicationDrainTimedOut,
    StopRejected,
    TerminalNeutralRejected,
    RemoveRejected,
    OwnerAuthenticationLost,
    DependencyThrew,
    QuarantineRequired,
}

/// <summary>
/// Transport-neutral result of one participant operation. The operation tag is
/// part of the value so a future shared coordinator can fail closed when an
/// adapter returns a result for the wrong phase. Construction is validated;
/// default and malformed values are never success evidence.
/// </summary>
internal readonly struct Switch2RuntimeRegistrationParticipantResult
{
    private Switch2RuntimeRegistrationParticipantResult(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantOutcome outcome,
        Switch2RuntimeRegistrationParticipantFailureKind failureKind,
        InputControllerOwnerOperationFailure ownerFailure,
        InputControllerSlotQuarantineReason quarantineReason)
    {
        Operation = operation;
        Outcome = outcome;
        FailureKind = failureKind;
        OwnerFailure = ownerFailure;
        QuarantineReason = quarantineReason;
    }

    internal Switch2RuntimeRegistrationParticipantOperation Operation
    {
        get;
    }

    internal Switch2RuntimeRegistrationParticipantOutcome Outcome { get; }

    internal Switch2RuntimeRegistrationParticipantFailureKind FailureKind
    {
        get;
    }

    internal InputControllerOwnerOperationFailure OwnerFailure { get; }

    internal InputControllerSlotQuarantineReason QuarantineReason { get; }

    internal bool IsValid => IsValidShape(Operation, Outcome, FailureKind,
        OwnerFailure, QuarantineReason);

    internal bool Succeeded => IsValid && Outcome ==
        Switch2RuntimeRegistrationParticipantOutcome.Succeeded;

    internal bool RequiresQuarantine => IsValid &&
        (Outcome == Switch2RuntimeRegistrationParticipantOutcome.
             OutcomeUncertain ||
         FailureKind == Switch2RuntimeRegistrationParticipantFailureKind.
             QuarantineRequired ||
         QuarantineReason != InputControllerSlotQuarantineReason.None);

    internal static Switch2RuntimeRegistrationParticipantResult Success(
        Switch2RuntimeRegistrationParticipantOperation operation)
    {
        if (!TryCreate(operation,
                Switch2RuntimeRegistrationParticipantOutcome.Succeeded,
                Switch2RuntimeRegistrationParticipantFailureKind.None,
                default, default, out var result))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
        return result;
    }

    internal static Switch2RuntimeRegistrationParticipantResult Reject(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantFailureKind failureKind,
        InputControllerOwnerOperationFailure ownerFailure = default,
        InputControllerSlotQuarantineReason quarantineReason = default) =>
        CreateFailure(operation,
            Switch2RuntimeRegistrationParticipantOutcome.ProvenRejected,
            failureKind, ownerFailure, quarantineReason);

    internal static Switch2RuntimeRegistrationParticipantResult Uncertain(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantFailureKind failureKind,
        InputControllerOwnerOperationFailure ownerFailure = default,
        InputControllerSlotQuarantineReason quarantineReason =
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure) =>
        CreateFailure(operation,
            Switch2RuntimeRegistrationParticipantOutcome.OutcomeUncertain,
            failureKind, ownerFailure, quarantineReason);

    internal static bool TryCreate(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantOutcome outcome,
        Switch2RuntimeRegistrationParticipantFailureKind failureKind,
        InputControllerOwnerOperationFailure ownerFailure,
        InputControllerSlotQuarantineReason quarantineReason,
        out Switch2RuntimeRegistrationParticipantResult result)
    {
        if (!IsValidShape(operation, outcome, failureKind, ownerFailure,
                quarantineReason))
        {
            result = default;
            return false;
        }

        result = new Switch2RuntimeRegistrationParticipantResult(operation,
            outcome, failureKind, ownerFailure, quarantineReason);
        return true;
    }

    private static Switch2RuntimeRegistrationParticipantResult CreateFailure(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantOutcome outcome,
        Switch2RuntimeRegistrationParticipantFailureKind failureKind,
        InputControllerOwnerOperationFailure ownerFailure,
        InputControllerSlotQuarantineReason quarantineReason)
    {
        if (!TryCreate(operation, outcome, failureKind, ownerFailure,
                quarantineReason, out var result))
        {
            throw new ArgumentException(
                "The participant result shape is invalid.");
        }
        return result;
    }

    private static bool IsValidShape(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantOutcome outcome,
        Switch2RuntimeRegistrationParticipantFailureKind failureKind,
        InputControllerOwnerOperationFailure ownerFailure,
        InputControllerSlotQuarantineReason quarantineReason)
    {
        if (!IsDefined(operation) || !IsDefined(outcome) ||
            !IsDefined(failureKind) || !IsDefined(ownerFailure) ||
            !IsDefined(quarantineReason))
        {
            return false;
        }

        if (outcome ==
            Switch2RuntimeRegistrationParticipantOutcome.Succeeded)
        {
            return failureKind ==
                    Switch2RuntimeRegistrationParticipantFailureKind.None &&
                ownerFailure == InputControllerOwnerOperationFailure.None &&
                quarantineReason ==
                    InputControllerSlotQuarantineReason.None;
        }
        if (outcome is not
                (Switch2RuntimeRegistrationParticipantOutcome.ProvenRejected or
                 Switch2RuntimeRegistrationParticipantOutcome.
                     OutcomeUncertain) ||
            failureKind ==
                Switch2RuntimeRegistrationParticipantFailureKind.None ||
            !IsFailureAllowed(operation, failureKind))
        {
            return false;
        }

        return operation switch
        {
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce =>
                ownerFailure is not
                    InputControllerOwnerOperationFailure.RemoveRejected,
            Switch2RuntimeRegistrationParticipantOperation.Remove =>
                ownerFailure is not
                    (InputControllerOwnerOperationFailure.InvalidTimeout or
                     InputControllerOwnerOperationFailure.StopRejected),
            _ => ownerFailure == InputControllerOwnerOperationFailure.None,
        };
    }

    private static bool IsFailureAllowed(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantFailureKind failureKind) =>
        failureKind switch
        {
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidArgument =>
                operation is
                    Switch2RuntimeRegistrationParticipantOperation.
                        AdoptBoundSlot or
                    Switch2RuntimeRegistrationParticipantOperation.Subscribe,
            Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential or
                Switch2RuntimeRegistrationParticipantFailureKind.
                    StaleCredential => operation is
                    Switch2RuntimeRegistrationParticipantOperation.
                        AdoptBoundSlot or
                    Switch2RuntimeRegistrationParticipantOperation.Subscribe or
                    Switch2RuntimeRegistrationParticipantOperation.
                        PrepareActivation or
                    Switch2RuntimeRegistrationParticipantOperation.
                        CommitPrepared or
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortPrepared or
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortUnpublished or
                    Switch2RuntimeRegistrationParticipantOperation.
                        ArmRetirement,
            Switch2RuntimeRegistrationParticipantFailureKind.AlreadyConsumed =>
                operation is
                    Switch2RuntimeRegistrationParticipantOperation.
                        CommitPrepared or
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortPrepared or
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortUnpublished,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout =>
                operation is
                    Switch2RuntimeRegistrationParticipantOperation.
                        PrepareActivation or
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortPrepared or
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortUnpublished or
                    Switch2RuntimeRegistrationParticipantOperation.
                        WaitForPublicationAvailability or
                    Switch2RuntimeRegistrationParticipantOperation.
                        StopAndQuiesce,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidState or
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress or
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew or
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired => true,
            Switch2RuntimeRegistrationParticipantFailureKind.
                    SubscriptionRejected => operation is
                    Switch2RuntimeRegistrationParticipantOperation.Subscribe or
                Switch2RuntimeRegistrationParticipantOperation.Unsubscribe,
            Switch2RuntimeRegistrationParticipantFailureKind.PrepareRejected =>
                operation == Switch2RuntimeRegistrationParticipantOperation.
                    PrepareActivation,
            Switch2RuntimeRegistrationParticipantFailureKind.CommitRejected =>
                operation == Switch2RuntimeRegistrationParticipantOperation.
                    CommitPrepared,
            Switch2RuntimeRegistrationParticipantFailureKind.AbortRejected =>
                operation is
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortPrepared or
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortUnpublished,
            Switch2RuntimeRegistrationParticipantFailureKind.
                    RetirementArmRejected => operation ==
                Switch2RuntimeRegistrationParticipantOperation.ArmRetirement,
            Switch2RuntimeRegistrationParticipantFailureKind.
                    PublicationDrainTimedOut => operation ==
                Switch2RuntimeRegistrationParticipantOperation.
                    WaitForPublicationAvailability,
            Switch2RuntimeRegistrationParticipantFailureKind.StopRejected or
                Switch2RuntimeRegistrationParticipantFailureKind.
                    TerminalNeutralRejected => operation ==
                Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce,
            Switch2RuntimeRegistrationParticipantFailureKind.RemoveRejected =>
                operation ==
                    Switch2RuntimeRegistrationParticipantOperation.Remove,
            Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost => operation is
                    Switch2RuntimeRegistrationParticipantOperation.
                        AdoptBoundSlot or
                    Switch2RuntimeRegistrationParticipantOperation.
                        PrepareActivation or
                    Switch2RuntimeRegistrationParticipantOperation.
                        StopAndQuiesce or
                    Switch2RuntimeRegistrationParticipantOperation.Remove,
            _ => false,
        };

    private static bool IsDefined(
        Switch2RuntimeRegistrationParticipantOperation value) => value is >=
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot and <=
            Switch2RuntimeRegistrationParticipantOperation.Remove;

    private static bool IsDefined(
        Switch2RuntimeRegistrationParticipantOutcome value) => value is
            Switch2RuntimeRegistrationParticipantOutcome.Succeeded or
            Switch2RuntimeRegistrationParticipantOutcome.ProvenRejected or
            Switch2RuntimeRegistrationParticipantOutcome.OutcomeUncertain;

    private static bool IsDefined(
        Switch2RuntimeRegistrationParticipantFailureKind value) => value is >=
            Switch2RuntimeRegistrationParticipantFailureKind.None and <=
            Switch2RuntimeRegistrationParticipantFailureKind.
                QuarantineRequired;

    private static bool IsDefined(
        InputControllerOwnerOperationFailure value) => value is >=
            InputControllerOwnerOperationFailure.None and <=
            InputControllerOwnerOperationFailure.OwnerThrew;

    private static bool IsDefined(
        InputControllerSlotQuarantineReason value) => value is >=
            InputControllerSlotQuarantineReason.None and <=
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure;
}

internal enum Switch2RuntimeRegistrationLifecycleAttentionKind : byte
{
    Invalid = 0,
    InputRejected,
    SubscriberRejected,
    ProducerFailed,
    TransportEnded,
    UserDisconnectRequested,
}

internal readonly struct Switch2RuntimeRegistrationLifecycleAttention
{
    internal Switch2RuntimeRegistrationLifecycleAttention(
        InputControllerRegistration registration,
        Switch2RuntimeRegistrationLifecycleAttentionKind kind)
    {
        Registration = registration;
        Kind = kind;
    }

    internal InputControllerRegistration Registration { get; }

    internal Switch2RuntimeRegistrationLifecycleAttentionKind Kind { get; }

    internal bool IsValid => Registration.Device != null &&
        Registration.Generation != 0 && Registration.Owner != null && Kind is
            Switch2RuntimeRegistrationLifecycleAttentionKind.InputRejected or
            Switch2RuntimeRegistrationLifecycleAttentionKind.
                SubscriberRejected or
            Switch2RuntimeRegistrationLifecycleAttentionKind.ProducerFailed or
            Switch2RuntimeRegistrationLifecycleAttentionKind.TransportEnded or
            Switch2RuntimeRegistrationLifecycleAttentionKind.
                UserDisconnectRequested;
}

internal delegate void Switch2RuntimeRegistrationLifecycleAttentionCallback(
    in Switch2RuntimeRegistrationLifecycleAttention attention);

internal readonly struct Switch2RuntimeRegistrationCallbacks
{
    internal Switch2RuntimeRegistrationCallbacks(
        DS4Device.ReportHandler<EventArgs> reportHandler,
        Switch2RuntimeRegistrationLifecycleAttentionCallback attentionHandler)
    {
        ReportHandler = reportHandler;
        AttentionHandler = attentionHandler;
    }

    internal DS4Device.ReportHandler<EventArgs> ReportHandler { get; }

    internal Switch2RuntimeRegistrationLifecycleAttentionCallback
        AttentionHandler { get; }

    internal bool IsValid => ReportHandler != null && AttentionHandler != null;

    internal bool IsExact(in Switch2RuntimeRegistrationCallbacks other) =>
        ReferenceEquals(ReportHandler, other.ReportHandler) &&
        ReferenceEquals(AttentionHandler, other.AttentionHandler);
}

/// <summary>
/// One exact registration participant. Native credentials and transport
/// diagnostics remain inside the adapter; the future transaction core receives
/// only validated, transport-neutral results.
/// </summary>
internal interface ISwitch2RuntimeRegistrationParticipant
{
    InputControllerRegistration Registration { get; }

    Switch2RuntimeRegistrationParticipantResult TryAdoptBoundSlot(
        in InputControllerSlotToken token);

    Switch2RuntimeRegistrationParticipantResult TrySubscribe(
        in Switch2RuntimeRegistrationCallbacks callbacks);

    Switch2RuntimeRegistrationParticipantResult TryPrepareActivation(
        int timeoutMilliseconds);

    Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
        in InputControllerActivationCommitCredential activationCommit);

    Switch2RuntimeRegistrationParticipantResult TryAbortPrepared(
        int timeoutMilliseconds);

    Switch2RuntimeRegistrationParticipantResult TryAbortUnpublished(
        int timeoutMilliseconds);

    Switch2RuntimeRegistrationParticipantResult TryArmRetirement(
        in InputControllerRetirementClaim claim);

    Switch2RuntimeRegistrationParticipantResult
        TryWaitForPublicationAvailability(int timeoutMilliseconds);

    Switch2RuntimeRegistrationParticipantResult TryStopAndQuiesce(
        int timeoutMilliseconds);

    Switch2RuntimeRegistrationParticipantResult TryUnsubscribe();

    Switch2RuntimeRegistrationParticipantResult TryRemove();
}
