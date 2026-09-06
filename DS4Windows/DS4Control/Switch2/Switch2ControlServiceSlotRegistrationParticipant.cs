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

internal enum Switch2ControlServiceSlotHostOperation : byte
{
    Invalid = 0,
    Prepare,
    DispatchRegular,
    DispatchTerminalNeutral,
    Abort,
    Remove,
}

internal enum Switch2ControlServiceSlotHostOutcome : byte
{
    Invalid = 0,
    Succeeded,
    ProvenRejected,
    OutcomeUncertain,
}

internal enum Switch2ControlServiceSlotHostFailureKind : byte
{
    None = 0,
    InvalidCredential,
    SlotOccupied,
    SlotChanged,
    ProfileSetupRejected,
    CallbackRejected,
    TerminalNeutralRejected,
    CleanupRejected,
    DependencyThrew,
}

/// <summary>
/// Strict result returned by the future ControlService slot host. A proven
/// Prepare rejection is evidence that the host made no mutation. Abort and
/// Remove are successful only when the exact slot lifetime is no longer
/// installed; every other cleanup result keeps the table slot quarantinable.
/// </summary>
internal readonly struct Switch2ControlServiceSlotHostResult
{
    private Switch2ControlServiceSlotHostResult(
        Switch2ControlServiceSlotHostOperation operation,
        Switch2ControlServiceSlotHostOutcome outcome,
        Switch2ControlServiceSlotHostFailureKind failureKind)
    {
        Operation = operation;
        Outcome = outcome;
        FailureKind = failureKind;
    }

    internal Switch2ControlServiceSlotHostOperation Operation { get; }

    internal Switch2ControlServiceSlotHostOutcome Outcome { get; }

    internal Switch2ControlServiceSlotHostFailureKind FailureKind { get; }

    internal bool IsValid => IsDefined(Operation) && IsDefined(Outcome) &&
        IsDefined(FailureKind) &&
        (Outcome == Switch2ControlServiceSlotHostOutcome.Succeeded ?
            FailureKind == Switch2ControlServiceSlotHostFailureKind.None :
            Outcome is Switch2ControlServiceSlotHostOutcome.ProvenRejected or
                Switch2ControlServiceSlotHostOutcome.OutcomeUncertain &&
            FailureKind != Switch2ControlServiceSlotHostFailureKind.None);

    internal bool Succeeded => IsValid && Outcome ==
        Switch2ControlServiceSlotHostOutcome.Succeeded;

    internal static Switch2ControlServiceSlotHostResult Success(
        Switch2ControlServiceSlotHostOperation operation)
    {
        if (!IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return new Switch2ControlServiceSlotHostResult(operation,
            Switch2ControlServiceSlotHostOutcome.Succeeded,
            Switch2ControlServiceSlotHostFailureKind.None);
    }

    internal static Switch2ControlServiceSlotHostResult Reject(
        Switch2ControlServiceSlotHostOperation operation,
        Switch2ControlServiceSlotHostFailureKind failureKind) =>
        Failure(operation,
            Switch2ControlServiceSlotHostOutcome.ProvenRejected, failureKind);

    internal static Switch2ControlServiceSlotHostResult Uncertain(
        Switch2ControlServiceSlotHostOperation operation,
        Switch2ControlServiceSlotHostFailureKind failureKind) =>
        Failure(operation,
            Switch2ControlServiceSlotHostOutcome.OutcomeUncertain,
            failureKind);

    private static Switch2ControlServiceSlotHostResult Failure(
        Switch2ControlServiceSlotHostOperation operation,
        Switch2ControlServiceSlotHostOutcome outcome,
        Switch2ControlServiceSlotHostFailureKind failureKind)
    {
        if (!IsDefined(operation) || !IsDefined(outcome) ||
            !IsDefined(failureKind) ||
            failureKind == Switch2ControlServiceSlotHostFailureKind.None)
        {
            throw new ArgumentException("The slot-host result is malformed.");
        }

        return new Switch2ControlServiceSlotHostResult(operation, outcome,
            failureKind);
    }

    private static bool IsDefined(
        Switch2ControlServiceSlotHostOperation value) => value is >=
            Switch2ControlServiceSlotHostOperation.Prepare and <=
            Switch2ControlServiceSlotHostOperation.Remove;

    private static bool IsDefined(
        Switch2ControlServiceSlotHostOutcome value) => value is
            Switch2ControlServiceSlotHostOutcome.Succeeded or
            Switch2ControlServiceSlotHostOutcome.ProvenRejected or
            Switch2ControlServiceSlotHostOutcome.OutcomeUncertain;

    private static bool IsDefined(
        Switch2ControlServiceSlotHostFailureKind value) => value is >=
            Switch2ControlServiceSlotHostFailureKind.None and <=
            Switch2ControlServiceSlotHostFailureKind.DependencyThrew;
}

/// <summary>
/// Exact table-issued slot lifetime handed to the ControlService host. It is
/// deliberately not a discovery identity and cannot be recreated from a slot
/// number, MAC address, or runtime generation.
/// </summary>
internal readonly struct Switch2ControlServiceSlotLease
{
    private readonly object issuer;

    internal Switch2ControlServiceSlotLease(object issuer,
        in InputControllerSlotToken token)
    {
        this.issuer = issuer;
        Token = token;
    }

    internal InputControllerSlotToken Token { get; }

    internal int Slot => Token.Slot;

    internal DS4Device Device => Token.Registration.Device;

    internal ulong RuntimeGeneration => Token.Registration.Generation;

    internal bool IsValid => issuer != null && Token.IsValid &&
        Token.Registration.OwnershipKind ==
            InputControllerOwnershipKind.Switch2Runtime;

    internal bool Authenticates(object expectedIssuer,
        in InputControllerRegistration expectedRegistration) =>
        IsValid && ReferenceEquals(issuer, expectedIssuer) &&
        Token.Registration.Equals(expectedRegistration);
}

/// <summary>
/// Future ControlService-side implementation boundary. Prepare must atomically
/// verify that the table-selected slot is empty in the legacy slot array and
/// install all profile/mapping state needed by Dispatch, without subscribing
/// DS4Device.Report and without starting or owning the transport. No method may
/// retain a report argument beyond the synchronous call.
/// </summary>
internal interface ISwitch2ControlServiceSlotHost
{
    Switch2ControlServiceSlotHostResult TryPrepare(
        in Switch2ControlServiceSlotLease lease);

    Switch2ControlServiceSlotHostResult TryDispatch(
        in Switch2ControlServiceSlotLease lease, DS4Device sender,
        Switch2RuntimeReportEventArgs report);

    Switch2ControlServiceSlotHostResult TryAbort(
        in Switch2ControlServiceSlotLease lease);

    Switch2ControlServiceSlotHostResult TryRemove(
        in Switch2ControlServiceSlotLease lease);
}

/// <summary>
/// Dormant participant decorator that stages one exact ControlService slot
/// after table binding and before transport commit. The wrapped participant
/// remains the sole owner of the DS4Device.Report subscription. Its callback is
/// routed through <see cref="MappingCallback"/> into the existing profile and
/// mapping host; this class contains no second mapper, queue, or report handler.
/// </summary>
internal sealed class Switch2ControlServiceSlotRegistrationParticipant :
    ISwitch2RuntimeRegistrationParticipant
{
    private readonly object gate = new();
    private readonly object leaseIssuer = new();
    private readonly ISwitch2RuntimeRegistrationParticipant inner;
    private readonly ISwitch2ControlServiceSlotHost host;
    private readonly InputControllerRegistration registration;
    private readonly Switch2RuntimeMappingCallback mappingCallback;

    private InputControllerSlotToken token;
    private Switch2ControlServiceSlotLease lease;
    private Switch2RuntimeRegistrationCallbacks subscribedCallbacks;
    private bool adopted;
    private bool subscribed;
    private bool hostCleanupRequired;
    private bool hostPrepared;
    private bool prepared;
    private bool commitAttempted;
    private bool committed;
    private bool retirementArmed;
    private bool stopInProgress;
    private bool stopped;
    private bool terminalAccepted;
    private bool abortCompleted;
    private bool innerRemoved;
    private bool hostRemoved;
    private bool lifecycleOperationActive;
    private bool callbackActive;
    private Switch2ControlServiceSlotHostResult lastHostResult;

    internal Switch2ControlServiceSlotRegistrationParticipant(
        ISwitch2RuntimeRegistrationParticipant inner,
        ISwitch2ControlServiceSlotHost host)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        try
        {
            registration = inner.Registration;
        }
        catch (Exception exception)
        {
            throw new ArgumentException(
                "The wrapped participant did not expose a registration.",
                nameof(inner), exception);
        }

        if (registration.Device == null || registration.Owner == null ||
            registration.Generation == 0 ||
            registration.OwnershipKind !=
                InputControllerOwnershipKind.Switch2Runtime)
        {
            throw new ArgumentException(
                "The wrapped participant registration is invalid.",
                nameof(inner));
        }

        mappingCallback = DispatchMappedReport;
    }

    public InputControllerRegistration Registration => registration;

    internal Switch2RuntimeMappingCallback MappingCallback => mappingCallback;

    internal ISwitch2RuntimeRegistrationParticipant InnerParticipant => inner;

    /// <summary>Test-visible identity of the decorator's only gate.</summary>
    internal object LifecycleGate => gate;

    internal Switch2ControlServiceSlotHostResult LastHostResult
    {
        get { lock (gate) { return lastHostResult; } }
    }

    public Switch2RuntimeRegistrationParticipantResult TryAdoptBoundSlot(
        in InputControllerSlotToken candidate)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot;
        if (!candidate.IsValid ||
            !candidate.Registration.Equals(registration))
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential);
        }

        lock (gate)
        {
            if (adopted)
            {
                return candidate.Equals(token) ? Success(operation) :
                    Reject(operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            StaleCredential);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }
        }

        InputControllerSlotToken exactCandidate = candidate;
        Switch2RuntimeRegistrationParticipantResult result = InvokeInner(
            operation, () => inner.TryAdoptBoundSlot(exactCandidate));
        lock (gate)
        {
            lifecycleOperationActive = false;
            if (result.Succeeded)
            {
                token = candidate;
                lease = new Switch2ControlServiceSlotLease(leaseIssuer,
                    candidate);
                adopted = true;
            }
        }
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TrySubscribe(
        in Switch2RuntimeRegistrationCallbacks callbacks)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Subscribe;
        lock (gate)
        {
            if (!adopted || abortCompleted || commitAttempted)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (subscribed)
            {
                return subscribedCallbacks.IsExact(callbacks) ?
                    Success(operation) :
                    Reject(operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            InvalidCredential);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }
        }

        Switch2RuntimeRegistrationCallbacks exactCallbacks = callbacks;
        Switch2RuntimeRegistrationParticipantResult result = InvokeInner(
            operation, () => inner.TrySubscribe(exactCallbacks));
        lock (gate)
        {
            lifecycleOperationActive = false;
            subscribed = result.Succeeded;
            if (result.Succeeded)
            {
                subscribedCallbacks = exactCallbacks;
            }
        }
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TryPrepareActivation(
        int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation;
        if (!IsPositiveTimeout(timeoutMilliseconds))
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }

        Switch2ControlServiceSlotLease exactLease;
        lock (gate)
        {
            if (!adopted || !subscribed || prepared || commitAttempted ||
                abortCompleted)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }
            exactLease = lease;
        }

        Switch2ControlServiceSlotHostResult hostResult = InvokeHost(
            Switch2ControlServiceSlotHostOperation.Prepare,
            () => host.TryPrepare(exactLease));
        if (!hostResult.Succeeded)
        {
            lock (gate)
            {
                lifecycleOperationActive = false;
                lastHostResult = hostResult;
                hostCleanupRequired = hostResult.IsValid &&
                    hostResult.Outcome ==
                        Switch2ControlServiceSlotHostOutcome.OutcomeUncertain;
            }
            return HostPrepareFailure(hostResult);
        }

        lock (gate)
        {
            lastHostResult = hostResult;
            hostPrepared = true;
            hostCleanupRequired = true;
        }

        Switch2RuntimeRegistrationParticipantResult innerResult = InvokeInner(
            operation,
            () => inner.TryPrepareActivation(timeoutMilliseconds));
        lock (gate)
        {
            lifecycleOperationActive = false;
            prepared = innerResult.Succeeded;
        }
        return innerResult;
    }

    public Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
        in InputControllerActivationCommitCredential activationCommit)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared;
        lock (gate)
        {
            if (!prepared || !hostPrepared || !hostCleanupRequired ||
                abortCompleted || commitAttempted)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }

            // The worker may publish synchronously from the inner commit. Host
            // staging is already complete and mapping admission must therefore
            // be visible before this external call begins.
            commitAttempted = true;
        }

        InputControllerActivationCommitCredential exactCommit =
            activationCommit;
        Switch2RuntimeRegistrationParticipantResult result = InvokeInner(
            operation,
            () => inner.TryCommitPrepared(exactCommit));
        lock (gate)
        {
            lifecycleOperationActive = false;
            prepared = false;
            committed = result.Succeeded;
        }
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TryAbortPrepared(
        int timeoutMilliseconds) => Abort(
            Switch2RuntimeRegistrationParticipantOperation.AbortPrepared,
            timeoutMilliseconds,
            () => inner.TryAbortPrepared(timeoutMilliseconds));

    public Switch2RuntimeRegistrationParticipantResult TryAbortUnpublished(
        int timeoutMilliseconds) => Abort(
            Switch2RuntimeRegistrationParticipantOperation.AbortUnpublished,
            timeoutMilliseconds,
            () => inner.TryAbortUnpublished(timeoutMilliseconds));

    public Switch2RuntimeRegistrationParticipantResult TryArmRetirement(
        in InputControllerRetirementClaim claim)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement;
        lock (gate)
        {
            if (!committed || stopped || abortCompleted || hostRemoved)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (retirementArmed)
            {
                return claim.IsValid && claim.Token.Equals(token) ?
                    Success(operation) : Reject(operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            StaleCredential);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }
        }

        InputControllerRetirementClaim exactClaim = claim;
        Switch2RuntimeRegistrationParticipantResult result = InvokeInner(
            operation, () => inner.TryArmRetirement(exactClaim));
        lock (gate)
        {
            lifecycleOperationActive = false;
            retirementArmed = result.Succeeded;
        }
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult
        TryWaitForPublicationAvailability(int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability;
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }

        lock (gate)
        {
            if (!retirementArmed || stopped || abortCompleted)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }
        }

        Switch2RuntimeRegistrationParticipantResult result = InvokeInner(
            operation,
            () => inner.TryWaitForPublicationAvailability(
                timeoutMilliseconds));
        lock (gate)
        {
            lifecycleOperationActive = false;
        }
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TryStopAndQuiesce(
        int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce;
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout,
                InputControllerOwnerOperationFailure.InvalidTimeout);
        }

        lock (gate)
        {
            if (stopped)
            {
                return Success(operation);
            }
            if (!committed || !retirementArmed || !hostPrepared ||
                abortCompleted)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }
            stopInProgress = true;
        }

        Switch2RuntimeRegistrationParticipantResult result = InvokeInner(
            operation,
            () => inner.TryStopAndQuiesce(timeoutMilliseconds));
        bool exactTerminal;
        lock (gate)
        {
            exactTerminal = terminalAccepted;
            stopInProgress = false;
            lifecycleOperationActive = false;
            stopped = result.Succeeded && exactTerminal;
        }
        if (result.Succeeded && !exactTerminal)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    TerminalNeutralRejected,
                quarantineReason: InputControllerSlotQuarantineReason.
                    TerminalNeutralNotObserved);
        }
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TryUnsubscribe()
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe;
        lock (gate)
        {
            if (!subscribed)
            {
                return Success(operation);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }
        }

        Switch2RuntimeRegistrationParticipantResult result = InvokeInner(
            operation, inner.TryUnsubscribe);
        lock (gate)
        {
            lifecycleOperationActive = false;
            if (result.Succeeded)
            {
                subscribed = false;
            }
        }
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TryRemove()
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Remove;
        Switch2ControlServiceSlotLease exactLease;
        bool removeInner;
        lock (gate)
        {
            if (hostRemoved)
            {
                return Success(operation);
            }
            if (!stopped || !terminalAccepted || subscribed || abortCompleted)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }
            exactLease = lease;
            removeInner = !innerRemoved;
        }

        if (removeInner)
        {
            Switch2RuntimeRegistrationParticipantResult innerResult =
                InvokeInner(operation, inner.TryRemove);
            if (!innerResult.Succeeded)
            {
                lock (gate)
                {
                    lifecycleOperationActive = false;
                }
                return innerResult;
            }
            lock (gate)
            {
                innerRemoved = true;
            }
        }

        Switch2ControlServiceSlotHostResult hostResult = InvokeHost(
            Switch2ControlServiceSlotHostOperation.Remove,
            () => host.TryRemove(exactLease));
        lock (gate)
        {
            lifecycleOperationActive = false;
            lastHostResult = hostResult;
            if (hostResult.Succeeded)
            {
                hostRemoved = true;
                hostCleanupRequired = false;
            }
        }
        return hostResult.Succeeded ? Success(operation) :
            HostCleanupFailure(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    RemoveRejected, hostResult);
    }

    private Switch2RuntimeRegistrationParticipantResult Abort(
        Switch2RuntimeRegistrationParticipantOperation operation,
        int timeoutMilliseconds,
        Func<Switch2RuntimeRegistrationParticipantResult> innerAbort)
    {
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }

        Switch2ControlServiceSlotLease exactLease;
        bool cleanupHost;
        lock (gate)
        {
            if (abortCompleted)
            {
                return Success(operation);
            }
            if (!adopted || commitAttempted || stopped || innerRemoved)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (!TryBeginLifecycleNoLock())
            {
                return Busy(operation);
            }
            exactLease = lease;
            cleanupHost = hostCleanupRequired;
        }

        Switch2RuntimeRegistrationParticipantResult innerResult = InvokeInner(
            operation, innerAbort);
        if (!innerResult.Succeeded)
        {
            lock (gate)
            {
                lifecycleOperationActive = false;
            }
            return innerResult;
        }

        if (cleanupHost)
        {
            Switch2ControlServiceSlotHostResult hostResult = InvokeHost(
                Switch2ControlServiceSlotHostOperation.Abort,
                () => host.TryAbort(exactLease));
            lock (gate)
            {
                lastHostResult = hostResult;
                lifecycleOperationActive = false;
                if (hostResult.Succeeded)
                {
                    hostCleanupRequired = false;
                    hostPrepared = false;
                    abortCompleted = true;
                }
            }
            return hostResult.Succeeded ? Success(operation) :
                HostCleanupFailure(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        AbortRejected, hostResult);
        }

        lock (gate)
        {
            lifecycleOperationActive = false;
            abortCompleted = true;
        }
        return Success(operation);
    }

    private void DispatchMappedReport(int slot, DS4Device sender,
        Switch2RuntimeReportEventArgs report)
    {
        Switch2ControlServiceSlotLease exactLease;
        Switch2ControlServiceSlotHostOperation operation;
        lock (gate)
        {
            bool exactIdentity = lease.IsValid && slot == token.Slot &&
                ReferenceEquals(sender, registration.Device) &&
                report != null &&
                report.RuntimeGeneration == registration.Generation;
            bool regularAdmitted = report?.Kind ==
                    Switch2RuntimeReportKind.Regular && commitAttempted &&
                !stopInProgress && !stopped;
            bool terminalAdmitted = report?.Kind ==
                    Switch2RuntimeReportKind.TerminalNeutral &&
                stopInProgress && retirementArmed && !terminalAccepted;
            if (!exactIdentity || !hostPrepared || !hostCleanupRequired ||
                (!regularAdmitted && !terminalAdmitted) || callbackActive)
            {
                throw new InvalidOperationException(
                    "The ControlService slot callback rejected report identity or lifecycle state.");
            }

            callbackActive = true;
            exactLease = lease;
            operation = terminalAdmitted ?
                Switch2ControlServiceSlotHostOperation.
                    DispatchTerminalNeutral :
                Switch2ControlServiceSlotHostOperation.DispatchRegular;
        }

        Switch2ControlServiceSlotHostResult result = InvokeHostDispatch(
            operation, exactLease, sender, report);
        lock (gate)
        {
            lastHostResult = result;
            if (result.Succeeded && operation ==
                    Switch2ControlServiceSlotHostOperation.
                        DispatchTerminalNeutral)
            {
                terminalAccepted = true;
            }
            callbackActive = false;
        }

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "The ControlService slot host rejected the exact report.");
        }
    }

    private bool TryBeginLifecycleNoLock()
    {
        if (lifecycleOperationActive || callbackActive)
        {
            return false;
        }
        lifecycleOperationActive = true;
        return true;
    }

    private static Switch2RuntimeRegistrationParticipantResult InvokeInner(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Func<Switch2RuntimeRegistrationParticipantResult> call)
    {
        Switch2RuntimeRegistrationParticipantResult result;
        try
        {
            result = call();
        }
        catch
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }

        return result.IsValid && result.Operation == operation ? result :
            Switch2RuntimeRegistrationParticipantResult.Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
    }

    private static Switch2ControlServiceSlotHostResult InvokeHost(
        Switch2ControlServiceSlotHostOperation operation,
        Func<Switch2ControlServiceSlotHostResult> call)
    {
        Switch2ControlServiceSlotHostResult result;
        try
        {
            result = call();
        }
        catch
        {
            return Switch2ControlServiceSlotHostResult.Uncertain(operation,
                Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
        }

        return result.IsValid && result.Operation == operation ? result :
            Switch2ControlServiceSlotHostResult.Uncertain(operation,
                Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
    }

    private Switch2ControlServiceSlotHostResult InvokeHostDispatch(
        Switch2ControlServiceSlotHostOperation operation,
        in Switch2ControlServiceSlotLease exactLease, DS4Device sender,
        Switch2RuntimeReportEventArgs report)
    {
        Switch2ControlServiceSlotHostResult result;
        try
        {
            result = host.TryDispatch(exactLease, sender, report);
        }
        catch
        {
            return Switch2ControlServiceSlotHostResult.Uncertain(operation,
                Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
        }

        return result.IsValid && result.Operation == operation ? result :
            Switch2ControlServiceSlotHostResult.Uncertain(operation,
                Switch2ControlServiceSlotHostFailureKind.DependencyThrew);
    }

    private static Switch2RuntimeRegistrationParticipantResult
        HostPrepareFailure(in Switch2ControlServiceSlotHostResult result)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation;
        return result.IsValid && result.Outcome ==
                Switch2ControlServiceSlotHostOutcome.ProvenRejected ?
            Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    PrepareRejected) :
            Switch2RuntimeRegistrationParticipantResult.Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
    }

    private static Switch2RuntimeRegistrationParticipantResult
        HostCleanupFailure(
            Switch2RuntimeRegistrationParticipantOperation operation,
            Switch2RuntimeRegistrationParticipantFailureKind failureKind,
            in Switch2ControlServiceSlotHostResult result) =>
        result.IsValid && result.Outcome ==
                Switch2ControlServiceSlotHostOutcome.ProvenRejected ?
            Reject(operation, failureKind,
                quarantineReason: InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure) :
            Switch2RuntimeRegistrationParticipantResult.Uncertain(operation,
                failureKind,
                quarantineReason: InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure);

    private static Switch2RuntimeRegistrationParticipantResult Success(
        Switch2RuntimeRegistrationParticipantOperation operation) =>
        Switch2RuntimeRegistrationParticipantResult.Success(operation);

    private static Switch2RuntimeRegistrationParticipantResult Reject(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantFailureKind failureKind,
        InputControllerOwnerOperationFailure ownerFailure = default,
        InputControllerSlotQuarantineReason quarantineReason = default) =>
        Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            failureKind, ownerFailure, quarantineReason);

    private static Switch2RuntimeRegistrationParticipantResult Busy(
        Switch2RuntimeRegistrationParticipantOperation operation) =>
        Reject(operation,
            Switch2RuntimeRegistrationParticipantFailureKind.
                OperationAlreadyInProgress);

    private static bool IsPositiveTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds > 0 && timeoutMilliseconds <=
            InputControllerRegistration.MaximumStopTimeoutMilliseconds;
}
