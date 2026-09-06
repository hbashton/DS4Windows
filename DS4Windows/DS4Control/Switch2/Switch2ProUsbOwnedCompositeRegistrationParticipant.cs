/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;

namespace DS4Windows.Switch2;

internal enum Switch2ProUsbOwnedCompositeParticipantCreateFailureKind : byte
{
    None = 0,
    MissingDependency,
    InvalidTimeout,
    InvalidAuthority,
    CompositeAuthenticationRejected,
    StartupTransactionRejected,
    FeedbackAuthenticationRejected,
    FeedbackStateRejected,
    FeedbackDormantProofRejected,
    RuntimeAdoptionRejected,
    InputCredentialRejected,
    DependencyThrew,
    QuarantineRequired,
}

internal readonly struct
    Switch2ProUsbOwnedCompositeParticipantCreateFailure
{
    internal Switch2ProUsbOwnedCompositeParticipantCreateFailure(
        Switch2ProUsbOwnedCompositeParticipantCreateFailureKind kind,
        Switch2ProUsbStartupCreateFailure startupFailure = default,
        in Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure
            runtimeAdoptionFailure = default,
        Switch2ProUsbOwnedCompositeInputAdoptionFailure
            inputAdoptionFailure = default,
        Switch2ProUsbOwnedCompositeLeaseBundle retainedBundle = null,
        Switch2ProUsbRuntimeOwner retainedRuntimeOwner = null,
        ISwitch2ProUsbOwnedFeedbackActivationLifetime
            retainedFeedbackLifetime = null)
    {
        Kind = kind;
        StartupFailure = startupFailure;
        RuntimeAdoptionFailure = runtimeAdoptionFailure;
        InputAdoptionFailure = inputAdoptionFailure;
        RetainedBundle = retainedBundle;
        RetainedRuntimeOwner = retainedRuntimeOwner;
        RetainedFeedbackLifetime = retainedFeedbackLifetime;
    }

    internal Switch2ProUsbOwnedCompositeParticipantCreateFailureKind Kind
    {
        get;
    }

    internal Switch2ProUsbStartupCreateFailure StartupFailure { get; }

    internal Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure
        RuntimeAdoptionFailure { get; }

    internal Switch2ProUsbOwnedCompositeInputAdoptionFailure
        InputAdoptionFailure { get; }

    internal Switch2ProUsbOwnedCompositeLeaseBundle RetainedBundle { get; }

    internal Switch2ProUsbRuntimeOwner RetainedRuntimeOwner { get; }

    internal ISwitch2ProUsbOwnedFeedbackActivationLifetime
        RetainedFeedbackLifetime { get; }

    internal bool RequiresRetention => RetainedBundle != null ||
        RetainedFeedbackLifetime != null;

    internal bool IsNone => Kind ==
        Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.None;
}

internal enum Switch2ProUsbOwnedCompositeParticipantState : byte
{
    Invalid = 0,
    Dormant,
    SlotAdopted,
    Subscribed,
    Prepared,
    Committed,
    RetirementArmed,
    Stopped,
    Aborted,
    Removed,
    Quarantined,
}

/// <summary>
/// Dormant lifecycle decorator for one admitted Pro Controller 2 USB
/// composite. It adds no report callback, queue, worker, timer, protocol byte,
/// or production call site. The exact callbacks supplied by the shared table
/// are forwarded reference-identically to the existing USB participant.
///
/// Activation is startup, sealed-feedback prepare, parked-input prepare,
/// feedback commit, then input commit. Teardown is feedback neutral/quiescent,
/// command retirement, input stop, exact mediated-facet retirement proof, and
/// finally the nonblocking whole-composite DisposeQuiesced call. A malformed,
/// thrown, or outcome-uncertain dependency result quarantines and retains the
/// full composite.
/// </summary>
internal sealed class Switch2ProUsbOwnedCompositeRegistrationParticipant :
    ISwitch2RuntimeRegistrationParticipant
{
    private readonly object gate = new();
    private readonly Switch2ProUsbOwnedCompositeLeaseBundle bundle;
    private readonly Switch2ProUsbOwnedCompositeAuthority authority;
    private readonly Switch2PhysicalInputLifetime lifetime;
    private readonly ISwitch2ProUsbOwnedCompositeLease compositeLease;
    private readonly Switch2ProUsbOwnedCompositeInputAdoptionCredential
        inputAdoptionCredential;
    private readonly Switch2ProUsbRuntimeOwner runtimeOwner;
    private readonly Switch2ProUsbRuntimeRegistrationParticipant inner;
    private readonly Switch2ProUsbStartupTransaction startup;
    private readonly ISwitch2ProUsbOwnedFeedbackActivationLifetime feedback;
    private readonly Switch2ProUsbOwnedFeedbackDormantQuiescenceProof
        feedbackDormantProof;

    private InputControllerSlotToken boundToken;
    private Switch2ProUsbOwnedFeedbackPrepareCredential feedbackCredential;
    private Switch2ProUsbOwnedCompositeInputFacetRetirementProof
        inputRetirementProof;
    private Switch2ProUsbOwnedCompositeParticipantState state =
        Switch2ProUsbOwnedCompositeParticipantState.Dormant;
    private bool operationInProgress;
    private bool subscribed;
    private bool innerPrepared;
    private bool feedbackPrepared;
    private bool retirementArmed;
    // This is initialized only from the exact authority/lifetime-bound dormant
    // quiescence proof. Successful prepare clears it because exact
    // credential-consuming abort is then required; abort or terminal neutral
    // restores it.
    private bool feedbackQuiesced;
    private bool startupRetired;
    private bool innerStopped;
    private bool compositeDisposeAttempted;
    private bool compositeDisposed;
    private bool quarantined;
    private long activationDeadline;
    private int activationTimeoutMilliseconds;

    private Switch2ProUsbStartupAdvanceResult lastStartupAdvanceResult;
    private Switch2ProUsbStartupRetirementFailure lastStartupRetirementFailure;
    private Switch2ProUsbOwnedFeedbackActivationResult
        lastFeedbackActivationResult;
    private Switch2ProUsbOwnedFeedbackQuiescenceResult
        lastFeedbackQuiescenceResult;
    private string firstInnerInvocationExceptionType;
    private Switch2RuntimeRegistrationParticipantOperation
        firstInvalidInnerExpectedOperation;
    private Switch2RuntimeRegistrationParticipantResult
        firstInvalidInnerResult;
    private string lastPreparePhase = "never-entered";
    private string lastStopPhase = "never-entered";

    private Switch2ProUsbOwnedCompositeRegistrationParticipant(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2PhysicalInputLifetime lifetime,
        ISwitch2ProUsbOwnedCompositeLease compositeLease,
        in Switch2ProUsbOwnedCompositeInputAdoptionCredential
            inputAdoptionCredential,
        Switch2ProUsbRuntimeOwner runtimeOwner,
        Switch2ProUsbRuntimeRegistrationParticipant inner,
        Switch2ProUsbStartupTransaction startup,
        ISwitch2ProUsbOwnedFeedbackActivationLifetime feedback,
        in Switch2ProUsbOwnedFeedbackDormantQuiescenceProof
            feedbackDormantProof)
    {
        this.bundle = bundle;
        this.authority = authority;
        this.lifetime = lifetime;
        this.compositeLease = compositeLease;
        this.inputAdoptionCredential = inputAdoptionCredential;
        this.runtimeOwner = runtimeOwner;
        this.inner = inner;
        this.startup = startup;
        this.feedback = feedback;
        this.feedbackDormantProof = feedbackDormantProof;
        feedbackQuiesced = true;
    }

    public InputControllerRegistration Registration => inner.Registration;

    internal Switch2ProUsbRuntimeOwner RuntimeOwner => runtimeOwner;

    internal Switch2ProUsbOwnedCompositeParticipantState State
    {
        get { lock (gate) { return state; } }
    }

    internal bool CompositeDisposed
    {
        get { lock (gate) { return compositeDisposed; } }
    }

    internal Switch2ProUsbStartupAdvanceResult LastStartupAdvanceResult
    {
        get { lock (gate) { return lastStartupAdvanceResult; } }
    }

    internal Switch2ProUsbStartupRetirementFailure
        LastStartupRetirementFailure
    {
        get { lock (gate) { return lastStartupRetirementFailure; } }
    }

    internal Switch2ProUsbRuntimePrepareFailure LastInputPrepareFailure =>
        inner.LastPrepareFailure;

    internal string LastInputPrepareExceptionType =>
        inner.LastPrepareExceptionType;

    internal string LastInputPrepareProofShape =>
        inner.LastPrepareProofShape;

    internal string LastInputOwnerPrepareDiagnostic =>
        runtimeOwner.LastPrepareDiagnostic;

    internal string LastPreparePhase
    {
        get { lock (gate) { return lastPreparePhase; } }
    }

    internal string LastStopPhase
    {
        get { lock (gate) { return lastStopPhase; } }
    }

    internal Switch2ProUsbRuntimeStopFailure LastInputStopFailure =>
        runtimeOwner.LastStopFailure;

    internal string LastCommandRetirementDiagnostic => compositeLease is
            Switch2ProUsbWindowsOwnedCompositeLease windowsLease ?
        windowsLease.LastCommandRetirementDiagnostic : "unavailable";

    internal string FirstInnerInvocationExceptionType
    {
        get { lock (gate) { return firstInnerInvocationExceptionType; } }
    }

    internal string FirstInvalidInnerResultShape
    {
        get
        {
            lock (gate)
            {
                return firstInvalidInnerExpectedOperation ==
                        Switch2RuntimeRegistrationParticipantOperation.Invalid ?
                    "none" :
                    $"expected={firstInvalidInnerExpectedOperation}," +
                    $"actual={firstInvalidInnerResult.Operation}," +
                    $"outcome={firstInvalidInnerResult.Outcome}," +
                    $"failure={firstInvalidInnerResult.FailureKind}," +
                    $"valid={firstInvalidInnerResult.IsValid}";
            }
        }
    }

    internal Switch2ProUsbOwnedFeedbackActivationResult
        LastFeedbackActivationResult
    {
        get { lock (gate) { return lastFeedbackActivationResult; } }
    }

    internal Switch2ProUsbOwnedFeedbackQuiescenceResult
        LastFeedbackQuiescenceResult
    {
        get { lock (gate) { return lastFeedbackQuiescenceResult; } }
    }

    internal Switch2ProUsbOwnedCompositeInputFacetRetirementProof
        InputRetirementProof
    {
        get { lock (gate) { return inputRetirementProof; } }
    }

    internal static bool TryCreate(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2InputCalibrationSnapshot calibration,
        ISwitch2ProUsbOwnedFeedbackActivationLifetime feedback,
        int operationTimeoutMilliseconds,
        out Switch2ProUsbOwnedCompositeRegistrationParticipant participant,
        out Switch2ProUsbOwnedCompositeParticipantCreateFailure failure) =>
        TryCreateCore(bundle, authority, calibration, feedback,
            operationTimeoutMilliseconds,
            Switch2ProUsbRuntimePumpFactory.Instance,
            Switch2ProUsbRuntimeTerminalScheduler.Instance,
            initialInputHandoffSequence: 0, out participant, out failure);

    internal static bool TryCreateCore(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2InputCalibrationSnapshot calibration,
        ISwitch2ProUsbOwnedFeedbackActivationLifetime feedback,
        int operationTimeoutMilliseconds,
        ISwitch2ProUsbRuntimePumpFactory pumpFactory,
        ISwitch2ProUsbRuntimeTerminalScheduler terminalScheduler,
        ulong initialInputHandoffSequence,
        out Switch2ProUsbOwnedCompositeRegistrationParticipant participant,
        out Switch2ProUsbOwnedCompositeParticipantCreateFailure failure) =>
        TryCreateCoreInternal(bundle, authority, calibration, feedback,
            operationTimeoutMilliseconds, pumpFactory, terminalScheduler,
            initialInputHandoffSequence, completedStartup: null,
            out participant, out failure);

    internal static bool TryCreateWithCompletedStartup(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2InputCalibrationSnapshot calibration,
        ISwitch2ProUsbOwnedFeedbackActivationLifetime feedback,
        int operationTimeoutMilliseconds,
        Switch2ProUsbStartupTransaction completedStartup,
        out Switch2ProUsbOwnedCompositeRegistrationParticipant participant,
        out Switch2ProUsbOwnedCompositeParticipantCreateFailure failure) =>
        TryCreateCoreInternal(bundle, authority, calibration, feedback,
            operationTimeoutMilliseconds,
            Switch2ProUsbRuntimePumpFactory.Instance,
            Switch2ProUsbRuntimeTerminalScheduler.Instance,
            initialInputHandoffSequence: 0,
            completedStartup: completedStartup,
            out participant, out failure);

    private static bool TryCreateCoreInternal(
        Switch2ProUsbOwnedCompositeLeaseBundle bundle,
        in Switch2ProUsbOwnedCompositeAuthority authority,
        in Switch2InputCalibrationSnapshot calibration,
        ISwitch2ProUsbOwnedFeedbackActivationLifetime feedback,
        int operationTimeoutMilliseconds,
        ISwitch2ProUsbRuntimePumpFactory pumpFactory,
        ISwitch2ProUsbRuntimeTerminalScheduler terminalScheduler,
        ulong initialInputHandoffSequence,
        Switch2ProUsbStartupTransaction completedStartup,
        out Switch2ProUsbOwnedCompositeRegistrationParticipant participant,
        out Switch2ProUsbOwnedCompositeParticipantCreateFailure failure)
    {
        participant = null;
        if (bundle == null || feedback == null || pumpFactory == null ||
            terminalScheduler == null)
        {
            failure = CreateFailure(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    MissingDependency, bundle);
            return false;
        }
        if (!IsPositiveTimeout(operationTimeoutMilliseconds))
        {
            failure = CreateFailure(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    InvalidTimeout, bundle);
            return false;
        }

        Switch2PhysicalInputLifetime lifetime = bundle.Lifetime;
        if (!authority.IsValid || !lifetime.IsValid ||
            authority.DeviceGeneration !=
                lifetime.SessionDescriptor.DeviceGeneration ||
            authority.TransportGeneration !=
                lifetime.SessionDescriptor.TransportGeneration)
        {
            failure = CreateFailure(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    InvalidAuthority, bundle);
            return false;
        }

        ISwitch2ProUsbReadOnlyCompositeLease inputLease = null;
        ISwitch2ProUsbStartupCommandLease startupLease = null;
        ISwitch2ProUsbOwnedCompositeLease outputLease = null;
        bool feedbackAuthenticated = false;
        Switch2ProUsbOwnedFeedbackActivationState feedbackState = default;
        bool dependencyThrew = false;
        try
        {
            bool inputAuthenticated = bundle.TryGetInputLease(authority,
                out inputLease);
            bool startupAuthenticated = bundle.TryGetStartupLease(authority,
                out startupLease);
            bool outputAuthenticated = bundle.TryGetBoundedOutputLease(
                authority, out outputLease);
            feedbackAuthenticated = feedback.Authenticates(authority);
            feedbackState = feedback.ActivationState;
            if (!inputAuthenticated || !startupAuthenticated ||
                !outputAuthenticated)
            {
                inputLease = null;
            }
        }
        catch
        {
            dependencyThrew = true;
        }
        if (dependencyThrew)
        {
            failure = CreateFailure(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    DependencyThrew, bundle);
            return false;
        }
        if (inputLease == null || startupLease == null ||
            outputLease == null ||
            !ReferenceEquals(inputLease, startupLease) ||
            !ReferenceEquals(inputLease, outputLease))
        {
            failure = CreateFailure(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    CompositeAuthenticationRejected, bundle);
            return false;
        }
        if (!feedbackAuthenticated)
        {
            failure = CreateFailure(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    FeedbackAuthenticationRejected, bundle);
            return false;
        }
        if (feedbackState !=
            Switch2ProUsbOwnedFeedbackActivationState.Dormant)
        {
            failure = CreateFailure(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    FeedbackStateRejected, bundle);
            return false;
        }
        Switch2ProUsbStartupTransaction startup = completedStartup;
        Switch2ProUsbStartupCreateFailure startupFailure = default;
        bool startupAccepted = startup != null ?
            startup.AuthenticatesCompleted(startupLease, lifetime) :
            Switch2ProUsbStartupTransaction.TryCreate(startupLease,
                lifetime, out startup, out startupFailure);
        if (!startupAccepted)
        {
            failure = new(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    StartupTransactionRejected,
                startupFailure, retainedBundle: bundle);
            return false;
        }

        if (!Switch2ProUsbOwnedCompositeRuntimeAdoptionFactory.TryCreateCore(
                bundle, authority, calibration,
                operationTimeoutMilliseconds, pumpFactory,
                terminalScheduler, initialInputHandoffSequence,
                out Switch2ProUsbRuntimeOwner owner,
                out InputControllerRegistration registration,
                out Switch2ProUsbOwnedCompositeInputAdoptionCredential
                    inputCredential,
                out Switch2ProUsbOwnedCompositeRuntimeAdoptionFailure
                    runtimeFailure))
        {
            failure = new(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    RuntimeAdoptionRejected,
                runtimeAdoptionFailure: runtimeFailure,
                inputAdoptionFailure: runtimeFailure.AdoptionFailure,
                retainedBundle: bundle,
                retainedRuntimeOwner: runtimeFailure.RetainedRuntimeOwner);
            return false;
        }

        if (!inputCredential.TryConsume(authority, lifetime, owner,
                registration,
                out Switch2ProUsbOwnedCompositeInputAdoptionFailure
                    inputFailure))
        {
            owner.MarkOwnedCompositeCreationQuarantined();
            inputCredential.QuarantineIssuer(inputFailure);
            failure = new(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    InputCredentialRejected,
                inputAdoptionFailure: inputFailure,
                retainedBundle: bundle, retainedRuntimeOwner: owner);
            return false;
        }

        Switch2ProUsbRuntimeRegistrationParticipant inner;
        try
        {
            inner = new Switch2ProUsbRuntimeRegistrationParticipant(owner);
        }
        catch
        {
            owner.MarkOwnedCompositeCreationQuarantined();
            inputCredential.QuarantineIssuer(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    DependencyThrew);
            failure = new(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    QuarantineRequired,
                inputAdoptionFailure:
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        DependencyThrew,
                retainedBundle: bundle, retainedRuntimeOwner: owner);
            participant = null;
            return false;
        }

        // Adopt feedback only after every other fallible prerequisite has
        // succeeded. From this point onward any rejection, malformed proof,
        // or exception retains the exact feedback lifetime alongside the
        // already-adopted composite/runtime owner; it is never reported as a
        // clean reusable failure.
        bool feedbackDormantProofTaken = false;
        Switch2ProUsbOwnedFeedbackDormantQuiescenceProof
            feedbackDormantProof = default;
        dependencyThrew = false;
        try
        {
            feedbackDormantProofTaken =
                feedback.TryTakeDormantQuiescenceProof(authority,
                    out feedbackDormantProof);
            feedbackState = feedback.ActivationState;
        }
        catch
        {
            dependencyThrew = true;
        }
        if (dependencyThrew || !feedbackDormantProofTaken ||
            feedbackState !=
                Switch2ProUsbOwnedFeedbackActivationState.Dormant ||
            !feedbackDormantProof.Authenticates(feedback, authority,
                lifetime))
        {
            owner.MarkOwnedCompositeCreationQuarantined();
            inputCredential.QuarantineIssuer(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired);
            failure = new(
                dependencyThrew ?
                    Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                        DependencyThrew :
                    Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                        FeedbackDormantProofRejected,
                inputAdoptionFailure:
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        QuarantineRequired,
                retainedBundle: bundle, retainedRuntimeOwner: owner,
                retainedFeedbackLifetime: feedback);
            return false;
        }

        // Production uses the concrete canonical USB feedback lifetime. Bind
        // that exact authority-owned object to the unpublished runtime before
        // the participant escapes, so profile staging can create its Xbox
        // receive session before input commit. Injectable test lifetimes keep
        // exercising the lifecycle interface without acquiring this optional
        // runtime-facing capability.
        if (feedback is Switch2ProUsbOwnedFeedbackActivationLifetime
                concreteFeedback &&
            !owner.RuntimeInputDevice.TryAttachUsbFeedbackLifetime(authority,
                concreteFeedback))
        {
            owner.MarkOwnedCompositeCreationQuarantined();
            inputCredential.QuarantineIssuer(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired);
            failure = new(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    QuarantineRequired,
                inputAdoptionFailure:
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        QuarantineRequired,
                retainedBundle: bundle, retainedRuntimeOwner: owner,
                retainedFeedbackLifetime: feedback);
            return false;
        }

        try
        {
            participant = new(
                bundle, authority, lifetime, outputLease, inputCredential,
                owner, inner, startup, feedback, feedbackDormantProof);
        }
        catch
        {
            owner.MarkOwnedCompositeCreationQuarantined();
            inputCredential.QuarantineIssuer(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    DependencyThrew);
            failure = new(
                Switch2ProUsbOwnedCompositeParticipantCreateFailureKind.
                    QuarantineRequired,
                inputAdoptionFailure:
                    Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                        DependencyThrew,
                retainedBundle: bundle, retainedRuntimeOwner: owner,
                retainedFeedbackLifetime: feedback);
            participant = null;
            return false;
        }

        failure = default;
        return true;
    }

    public Switch2RuntimeRegistrationParticipantResult TryAdoptBoundSlot(
        in InputControllerSlotToken token)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot;
        if (!TryBeginOperation(operation, out var rejected))
        {
            return rejected;
        }

        InputControllerSlotToken exactToken = token;
        Switch2RuntimeRegistrationParticipantResult result =
            Invoke(() => inner.TryAdoptBoundSlot(exactToken), operation);
        bool quarantine;
        lock (gate)
        {
            operationInProgress = false;
            if (result.Succeeded)
            {
                boundToken = token;
                state = Switch2ProUsbOwnedCompositeParticipantState.
                    SlotAdopted;
            }
            quarantine = ObserveFailureNoLock(result);
        }
        QuarantineIssuerIfNeeded(quarantine);
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TrySubscribe(
        in Switch2RuntimeRegistrationCallbacks callbacks)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Subscribe;
        if (!TryBeginOperation(operation, out var rejected))
        {
            return rejected;
        }

        Switch2RuntimeRegistrationCallbacks exactCallbacks = callbacks;
        // Deliberately pass the exact callback carrier. This decorator never
        // enters the warmed report path.
        Switch2RuntimeRegistrationParticipantResult result =
            Invoke(() => inner.TrySubscribe(exactCallbacks), operation);
        bool quarantine;
        lock (gate)
        {
            operationInProgress = false;
            if (result.Succeeded)
            {
                subscribed = true;
                state =
                    Switch2ProUsbOwnedCompositeParticipantState.Subscribed;
            }
            quarantine = ObserveFailureNoLock(result);
        }
        QuarantineIssuerIfNeeded(quarantine);
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TryPrepareActivation(
        int timeoutMilliseconds)
    {
        lock (gate)
        {
            lastPreparePhase = "entered";
        }
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation;
        if (!IsPositiveTimeout(timeoutMilliseconds))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }
        if (!TryBeginOperation(operation, out var rejected,
                requireSubscribed: true))
        {
            lock (gate)
            {
                lastPreparePhase = "begin-rejected";
            }
            return rejected;
        }

        long deadline = CreateDeadline(timeoutMilliseconds);
        Switch2RuntimeRegistrationParticipantResult result;
        if (!TryAuthenticateComposite())
        {
            lock (gate)
            {
                lastPreparePhase = "authentication-rejected";
            }
            result = Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost);
        }
        else if (!TryCompleteStartup(deadline, timeoutMilliseconds,
                     out result))
        {
            lock (gate)
            {
                lastPreparePhase = "startup-rejected";
            }
        }
        else if (!TryPrepareFeedback(deadline, timeoutMilliseconds,
                     out result))
        {
            lock (gate)
            {
                lastPreparePhase = "feedback-prepare-rejected";
            }
        }
        else
        {
            int remaining = RemainingMilliseconds(deadline,
                timeoutMilliseconds);
            if (remaining <= 0)
            {
                lock (gate)
                {
                    lastPreparePhase = "input-budget-expired";
                }
                result = Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidTimeout);
            }
            else
            {
                lock (gate)
                {
                    lastPreparePhase = "input-prepare-entering";
                }
                result = Invoke(
                    () => inner.TryPrepareActivation(remaining), operation);
                lock (gate)
                {
                    lastPreparePhase = result.Succeeded ?
                        "input-prepare-succeeded" :
                        "input-prepare-rejected";
                }
                if (result.Succeeded)
                {
                    innerPrepared = true;
                }
            }
        }

        bool startupCompleted = startup.State ==
            Switch2ProUsbStartupTransactionState.Completed;
        bool quarantine;
        lock (gate)
        {
            operationInProgress = false;
            if (result.Succeeded && innerPrepared && feedbackPrepared &&
                startupCompleted)
            {
                activationDeadline = deadline;
                activationTimeoutMilliseconds = timeoutMilliseconds;
                state = Switch2ProUsbOwnedCompositeParticipantState.Prepared;
            }
            quarantine = ObserveFailureNoLock(result);
        }
        QuarantineIssuerIfNeeded(quarantine);
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
        in InputControllerActivationCommitCredential activationCommit)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared;
        if (!TryBeginOperation(operation, out var rejected,
                requirePrepared: true))
        {
            return rejected;
        }

        bool commitAuthenticated;
        try
        {
            commitAuthenticated = activationCommit.IsValid &&
                activationCommit.Authenticates(boundToken);
        }
        catch
        {
            commitAuthenticated = false;
        }
        if (!commitAuthenticated || !TryAuthenticateComposite())
        {
            FinishOperation(quarantine: !commitAuthenticated ? false : true);
            return commitAuthenticated ? Uncertain(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OwnerAuthenticationLost) :
                Switch2RuntimeRegistrationParticipantResult.Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidCredential);
        }

        long deadline;
        int originalTimeout;
        lock (gate)
        {
            deadline = activationDeadline;
            originalTimeout = activationTimeoutMilliseconds;
        }
        int remaining = RemainingMilliseconds(deadline, originalTimeout);
        if (deadline == 0 || originalTimeout <= 0 || remaining <= 0)
        {
            FinishOperation(quarantine: true);
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    CommitRejected,
                quarantineReason:
                    InputControllerSlotQuarantineReason.
                        ExternalLifecycleFailure);
        }

        Switch2ProUsbOwnedFeedbackActivationResult feedbackResult = default;
        bool feedbackThrew = false;
        int feedbackCommitBudget = Math.Max(1, remaining / 2);
        try
        {
            feedbackResult = feedback.TryCommitPrepared(feedbackCredential,
                feedbackCommitBudget);
        }
        catch
        {
            feedbackThrew = true;
        }
        bool validFeedbackShape = !feedbackThrew &&
            IsValidFeedbackResult(feedbackResult,
                Switch2ProUsbOwnedFeedbackActivationOperation.Commit) &&
            IsFeedbackStateConsistent(feedbackResult);
        lock (gate)
        {
            lastFeedbackActivationResult = validFeedbackShape ?
                feedbackResult : default;
        }

        bool validFeedbackCommit = validFeedbackShape &&
            feedbackResult.Outcome ==
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded;
        if (!validFeedbackCommit)
        {
            bool uncertain = !validFeedbackShape ||
                feedbackResult.Outcome ==
                    Switch2ProUsbOwnedFeedbackActivationOutcome.
                        OutcomeUncertain;
            FinishOperation(quarantine: true);
            return uncertain ? Uncertain(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        CommitRejected) :
                Switch2RuntimeRegistrationParticipantResult.Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        CommitRejected,
                    quarantineReason:
                        InputControllerSlotQuarantineReason.
                            ExternalLifecycleFailure);
        }

        feedbackQuiesced = false;
        feedbackPrepared = false;
        remaining = RemainingMilliseconds(deadline, originalTimeout);
        if (remaining <= 0)
        {
            FinishOperation(quarantine: true);
            return Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    CommitRejected);
        }
        InputControllerActivationCommitCredential exactCommit =
            activationCommit;
        int inputCommitBudget = Math.Max(1, remaining / 2);
        Switch2RuntimeRegistrationParticipantResult inputResult = Invoke(
            () => inner.TryCommitPrepared(exactCommit, inputCommitBudget),
            operation);
        if (!inputResult.Succeeded)
        {
            // Feedback has crossed its linearization point. Seal and neutralize
            // it best-effort, but never describe this split commit as a clean
            // rollback or permit replacement ownership.
            remaining = RemainingMilliseconds(deadline, originalTimeout);
            if (remaining > 0)
            {
                TryNeutralizeFeedback(remaining);
            }
            FinishOperation(quarantine: true);
            return Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    CommitRejected);
        }

        if (RemainingMilliseconds(deadline, originalTimeout) <= 0)
        {
            FinishOperation(quarantine: true);
            return Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    CommitRejected);
        }

        lock (gate)
        {
            operationInProgress = false;
            innerPrepared = false;
            state = Switch2ProUsbOwnedCompositeParticipantState.Committed;
        }
        return Switch2RuntimeRegistrationParticipantResult.Success(operation);
    }

    public Switch2RuntimeRegistrationParticipantResult TryAbortPrepared(
        int timeoutMilliseconds) => TryAbortCore(
        Switch2RuntimeRegistrationParticipantOperation.AbortPrepared,
        timeoutMilliseconds);

    public Switch2RuntimeRegistrationParticipantResult TryAbortUnpublished(
        int timeoutMilliseconds) => TryAbortCore(
        Switch2RuntimeRegistrationParticipantOperation.AbortUnpublished,
        timeoutMilliseconds);

    public Switch2RuntimeRegistrationParticipantResult TryArmRetirement(
        in InputControllerRetirementClaim claim)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement;
        if (!TryBeginOperation(operation, out var rejected,
                requireCommitted: true))
        {
            return rejected;
        }
        InputControllerRetirementClaim exactClaim = claim;
        Switch2RuntimeRegistrationParticipantResult result = Invoke(
            () => inner.TryArmRetirement(exactClaim), operation);
        bool quarantine;
        lock (gate)
        {
            operationInProgress = false;
            if (result.Succeeded)
            {
                retirementArmed = true;
                state = Switch2ProUsbOwnedCompositeParticipantState.
                    RetirementArmed;
            }
            quarantine = ObserveFailureNoLock(result);
        }
        QuarantineIssuerIfNeeded(quarantine);
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult
        TryWaitForPublicationAvailability(int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability;
        if (!IsNonNegativeTimeout(timeoutMilliseconds))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }
        if (!TryBeginOperation(operation, out var rejected,
                requireRetirement: true))
        {
            return rejected;
        }
        Switch2RuntimeRegistrationParticipantResult result = Invoke(
            () => inner.TryWaitForPublicationAvailability(
                timeoutMilliseconds), operation);
        FinishOperation(result.RequiresQuarantine);
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TryStopAndQuiesce(
        int timeoutMilliseconds)
    {
        lock (gate)
        {
            lastStopPhase = "entered";
        }
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce;
        if (!IsNonNegativeTimeout(timeoutMilliseconds))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout,
                InputControllerOwnerOperationFailure.InvalidTimeout);
        }
        if (!TryBeginOperation(operation, out var rejected,
                requireRetirement: true))
        {
            lock (gate)
            {
                lastStopPhase = "begin-rejected";
            }
            return rejected;
        }

        long deadline = CreateDeadline(timeoutMilliseconds);
        if (!TryAuthenticateComposite())
        {
            lock (gate)
            {
                lastStopPhase = "authentication-rejected";
            }
            FinishOperation(quarantine: true);
            return Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost);
        }
        if (!feedbackQuiesced && !TryNeutralizeFeedback(
                RemainingMilliseconds(deadline, timeoutMilliseconds)))
        {
            lock (gate)
            {
                lastStopPhase = $"feedback-rejected:" +
                    $"{lastFeedbackQuiescenceResult.Outcome}";
            }
            bool uncertain = lastFeedbackQuiescenceResult.Outcome is
                Switch2ProUsbOwnedFeedbackQuiescenceOutcome.Invalid or
                Switch2ProUsbOwnedFeedbackQuiescenceOutcome.OutcomeUncertain;
            FinishOperation(quarantine: uncertain);
            return uncertain ? Uncertain(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        TerminalNeutralRejected) :
                Switch2RuntimeRegistrationParticipantResult.Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        TerminalNeutralRejected,
                    quarantineReason:
                        InputControllerSlotQuarantineReason.
                            TerminalNeutralNotObserved);
        }
        if (!startupRetired && !TryRetireStartup(
                RemainingMilliseconds(deadline, timeoutMilliseconds)))
        {
            lock (gate)
            {
                lastStopPhase = $"startup-rejected:" +
                    $"{lastStartupRetirementFailure}";
            }
            bool uncertain = IsUncertainRetirementFailure(
                lastStartupRetirementFailure);
            FinishOperation(quarantine: uncertain);
            return uncertain ? Uncertain(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        StopRejected) :
                Switch2RuntimeRegistrationParticipantResult.Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        StopRejected,
                    quarantineReason:
                        InputControllerSlotQuarantineReason.
                            ExternalLifecycleFailure);
        }

        lock (gate)
        {
            lastStopPhase = "input-stopping";
        }
        Switch2RuntimeRegistrationParticipantResult stopResult = Invoke(
            () => inner.TryStopAndQuiesce(RemainingMilliseconds(deadline,
                timeoutMilliseconds)), operation);
        if (!stopResult.Succeeded)
        {
            lock (gate)
            {
                lastStopPhase = $"input-rejected:" +
                    $"{stopResult.Outcome}/{stopResult.FailureKind}";
            }
            FinishOperation(stopResult.RequiresQuarantine);
            return stopResult;
        }
        innerStopped = true;

        if (!TryFinalizeWholeComposite())
        {
            lock (gate)
            {
                lastStopPhase = "composite-finalize-rejected";
            }
            FinishOperation(quarantine: true);
            return Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    StopRejected);
        }

        lock (gate)
        {
            operationInProgress = false;
            state = Switch2ProUsbOwnedCompositeParticipantState.Stopped;
            lastStopPhase = "succeeded";
        }
        return Switch2RuntimeRegistrationParticipantResult.Success(operation);
    }

    public Switch2RuntimeRegistrationParticipantResult TryUnsubscribe()
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe;
        if (!TryBeginOperation(operation, out var rejected,
                allowQuarantined: false))
        {
            return rejected;
        }
        lock (gate)
        {
            if (!subscribed)
            {
                operationInProgress = false;
                return Switch2RuntimeRegistrationParticipantResult.Success(
                    operation);
            }
            if (state is not
                    (Switch2ProUsbOwnedCompositeParticipantState.Stopped or
                     Switch2ProUsbOwnedCompositeParticipantState.Aborted))
            {
                operationInProgress = false;
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
        }

        Switch2RuntimeRegistrationParticipantResult result = Invoke(
            inner.TryUnsubscribe, operation);
        bool quarantine;
        lock (gate)
        {
            operationInProgress = false;
            if (result.Succeeded)
            {
                subscribed = false;
            }
            quarantine = ObserveFailureNoLock(result);
        }
        QuarantineIssuerIfNeeded(quarantine);
        return result;
    }

    public Switch2RuntimeRegistrationParticipantResult TryRemove()
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Remove;
        if (!TryBeginOperation(operation, out var rejected))
        {
            return rejected;
        }
        lock (gate)
        {
            if (state != Switch2ProUsbOwnedCompositeParticipantState.Stopped ||
                subscribed || !compositeDisposed)
            {
                operationInProgress = false;
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
        }

        Switch2RuntimeRegistrationParticipantResult result = Invoke(
            inner.TryRemove, operation);
        bool quarantine;
        lock (gate)
        {
            operationInProgress = false;
            if (result.Succeeded)
            {
                state = Switch2ProUsbOwnedCompositeParticipantState.Removed;
            }
            quarantine = ObserveFailureNoLock(result);
        }
        QuarantineIssuerIfNeeded(quarantine);
        return result;
    }

    private Switch2RuntimeRegistrationParticipantResult TryAbortCore(
        Switch2RuntimeRegistrationParticipantOperation operation,
        int timeoutMilliseconds)
    {
        if (!IsNonNegativeTimeout(timeoutMilliseconds))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }
        if (!TryBeginOperation(operation, out var rejected))
        {
            return rejected;
        }
        lock (gate)
        {
            if (state == Switch2ProUsbOwnedCompositeParticipantState.Aborted)
            {
                operationInProgress = false;
                return Switch2RuntimeRegistrationParticipantResult.Success(
                    operation);
            }
            if (state is Switch2ProUsbOwnedCompositeParticipantState.Committed
                or Switch2ProUsbOwnedCompositeParticipantState.
                    RetirementArmed or
                Switch2ProUsbOwnedCompositeParticipantState.Stopped or
                Switch2ProUsbOwnedCompositeParticipantState.Removed)
            {
                operationInProgress = false;
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
        }

        long deadline = CreateDeadline(timeoutMilliseconds);
        if (!TryAuthenticateComposite())
        {
            FinishOperation(quarantine: true);
            return Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost);
        }

        if (feedbackPrepared && !TryAbortFeedback(
                RemainingMilliseconds(deadline, timeoutMilliseconds)))
        {
            bool uncertain = lastFeedbackActivationResult.Outcome is
                Switch2ProUsbOwnedFeedbackActivationOutcome.Invalid or
                Switch2ProUsbOwnedFeedbackActivationOutcome.OutcomeUncertain;
            FinishOperation(quarantine: uncertain);
            return uncertain ? Uncertain(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        AbortRejected) :
                Switch2RuntimeRegistrationParticipantResult.Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        AbortRejected);
        }
        if (!startupRetired && !TryRetireStartup(
                RemainingMilliseconds(deadline, timeoutMilliseconds)))
        {
            bool uncertain = IsUncertainRetirementFailure(
                lastStartupRetirementFailure);
            FinishOperation(quarantine: uncertain);
            return uncertain ? Uncertain(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        AbortRejected) :
                Switch2RuntimeRegistrationParticipantResult.Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        AbortRejected);
        }

        // A clean input-prepare rejection performs its own exact unpublished
        // cleanup and leaves the owner in AbortedUnpublished. Do not issue a
        // second abort through the participant: accept that terminal owner
        // state, then consume the already-minted mediated-facet retirement
        // proof below.
        bool innerAlreadyAborted = false;
        bool ownerStateThrew = false;
        if (!innerPrepared)
        {
            try
            {
                innerAlreadyAborted = runtimeOwner.State ==
                    Switch2ProUsbRuntimeOwnerState.AbortedUnpublished;
            }
            catch
            {
                ownerStateThrew = true;
            }
        }
        if (ownerStateThrew)
        {
            FinishOperation(quarantine: true);
            return Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    AbortRejected);
        }
        if (!innerAlreadyAborted)
        {
            Switch2RuntimeRegistrationParticipantResult innerResult = Invoke(
                () => innerPrepared ?
                    inner.TryAbortPrepared(RemainingMilliseconds(deadline,
                        timeoutMilliseconds)) :
                    inner.TryAbortUnpublished(RemainingMilliseconds(deadline,
                        timeoutMilliseconds)),
                innerPrepared ?
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortPrepared :
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortUnpublished);
            if (!innerResult.Succeeded)
            {
                FinishOperation(innerResult.RequiresQuarantine);
                return Normalize(innerResult, operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        AbortRejected);
            }
        }
        innerPrepared = false;
        innerStopped = true;

        if (!TryFinalizeWholeComposite())
        {
            FinishOperation(quarantine: true);
            return Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    AbortRejected);
        }

        lock (gate)
        {
            operationInProgress = false;
            state = Switch2ProUsbOwnedCompositeParticipantState.Aborted;
        }
        return Switch2RuntimeRegistrationParticipantResult.Success(operation);
    }

    private bool TryCompleteStartup(long deadline, int originalTimeout,
        out Switch2RuntimeRegistrationParticipantResult result)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation;
        for (int index = 0;
                index < Switch2ProUsbStartupTransaction.RequiredStepCount &&
                startup.State !=
                Switch2ProUsbStartupTransactionState.Completed; index++)
        {
            int remaining = RemainingMilliseconds(deadline, originalTimeout);
            if (remaining <= 0)
            {
                result = Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidTimeout);
                return false;
            }

            // TryAdvance can invoke one command and one safety retirement.
            // Split the current remaining deadline so even that worst case
            // cannot consume two complete caller budgets.
            int maximumOperation = compositeLease.
                MaximumOutputOperationMilliseconds;
            int commandTimeout = Math.Min(maximumOperation,
                Math.Max(1, remaining / 2));
            int retirementTimeout = Math.Min(maximumOperation,
                Math.Max(0, remaining - commandTimeout));
            bool advanced;
            Switch2ProUsbStartupAdvanceResult advanceResult = default;
            bool threw = false;
            try
            {
                advanced = startup.TryAdvance(commandTimeout,
                    retirementTimeout, out advanceResult);
            }
            catch
            {
                advanced = false;
                threw = true;
            }
            lock (gate)
            {
                lastStartupAdvanceResult = advanceResult;
            }
            if (!advanced)
            {
                bool uncertain = threw || !advanceResult.State.Equals(
                        startup.State) || advanceResult.RequiresQuarantine;
                result = uncertain ? Uncertain(operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            PrepareRejected) :
                    Switch2RuntimeRegistrationParticipantResult.Reject(
                        operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            PrepareRejected);
                return false;
            }
        }

        if (startup.State != Switch2ProUsbStartupTransactionState.Completed)
        {
            result = Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    PrepareRejected);
            return false;
        }
        result = Switch2RuntimeRegistrationParticipantResult.Success(
            operation);
        return true;
    }

    private bool TryPrepareFeedback(long deadline, int originalTimeout,
        out Switch2RuntimeRegistrationParticipantResult result)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation;
        int remaining = RemainingMilliseconds(deadline, originalTimeout);
        if (remaining <= 0)
        {
            result = Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
            return false;
        }

        Switch2ProUsbOwnedFeedbackActivationResult feedbackResult = default;
        bool threw = false;
        try
        {
            feedbackResult = feedback.TryPrepareActivation(authority,
                feedbackDormantProof, remaining);
        }
        catch
        {
            threw = true;
        }
        bool valid = !threw && IsValidFeedbackResult(feedbackResult,
                Switch2ProUsbOwnedFeedbackActivationOperation.Prepare) &&
            IsFeedbackStateConsistent(feedbackResult);
        lock (gate)
        {
            lastFeedbackActivationResult = valid ? feedbackResult : default;
        }
        if (valid && feedbackResult.Outcome ==
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded)
        {
            feedbackCredential = feedbackResult.Credential;
            feedbackPrepared = true;
            // Prepared output is still sealed, but retirement now requires
            // the exact credential-consuming abort proof.
            feedbackQuiesced = false;
            result = Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
            return true;
        }

        bool uncertain = threw || !valid || feedbackResult.Outcome ==
            Switch2ProUsbOwnedFeedbackActivationOutcome.OutcomeUncertain;
        result = uncertain ? Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    PrepareRejected) :
            Switch2RuntimeRegistrationParticipantResult.Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    PrepareRejected);
        return false;
    }

    private bool TryAbortFeedback(int timeoutMilliseconds)
    {
        Switch2ProUsbOwnedFeedbackActivationResult feedbackResult = default;
        bool threw = false;
        try
        {
            feedbackResult = feedback.TryAbortPrepared(feedbackCredential,
                timeoutMilliseconds);
        }
        catch
        {
            threw = true;
        }
        bool valid = !threw && IsValidFeedbackResult(feedbackResult,
                Switch2ProUsbOwnedFeedbackActivationOperation.Abort) &&
            IsFeedbackStateConsistent(feedbackResult);
        lock (gate)
        {
            lastFeedbackActivationResult = valid ? feedbackResult : default;
        }
        bool succeeded = valid &&
            feedbackResult.Outcome ==
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded;
        if (succeeded)
        {
            feedbackPrepared = false;
            feedbackQuiesced = true;
        }
        return succeeded;
    }

    private bool TryNeutralizeFeedback(int timeoutMilliseconds)
    {
        Switch2ProUsbOwnedFeedbackQuiescenceResult quiescence = default;
        bool threw = false;
        try
        {
            quiescence = feedback.TryNeutralizeAndQuiesce(authority,
                timeoutMilliseconds);
        }
        catch
        {
            threw = true;
        }
        lock (gate)
        {
            lastFeedbackQuiescenceResult = quiescence;
        }
        bool exactAuthentication = false;
        if (!threw)
        {
            try
            {
                exactAuthentication = feedback.AuthenticatesQuiescenceResult(
                    authority, quiescence);
            }
            catch
            {
                threw = true;
            }
        }
        bool succeeded = !threw && exactAuthentication &&
            quiescence.Outcome is
                Switch2ProUsbOwnedFeedbackQuiescenceOutcome.
                    ExactNeutralAndQuiescent or
                Switch2ProUsbOwnedFeedbackQuiescenceOutcome.ExactDisconnectedAndQuiescent;
        if (succeeded)
        {
            feedbackQuiesced = true;
        }
        return succeeded;
    }

    private bool TryRetireStartup(int timeoutMilliseconds)
    {
        bool retired;
        Switch2ProUsbStartupRetirementFailure retirementFailure = default;
        try
        {
            int maximumOperation = compositeLease.
                MaximumOutputOperationMilliseconds;
            if (maximumOperation <= 0)
            {
                retired = false;
                retirementFailure =
                    Switch2ProUsbStartupRetirementFailure.InvalidTimeout;
            }
            else
            {
                // The participant owns a wider end-to-end lifecycle budget
                // than the command facet is allowed to consume in one native
                // operation. Pass only the authenticated per-operation share;
                // forwarding the whole 5 s lifecycle budget makes the 1 s
                // Windows command lease reject retirement before releasing
                // any handle.
                int boundedTimeout = Math.Min(timeoutMilliseconds,
                    maximumOperation);
                retired = startup.TryRetire(boundedTimeout,
                    out retirementFailure);
            }
        }
        catch
        {
            retired = false;
            retirementFailure =
                Switch2ProUsbStartupRetirementFailure.DependencyThrew;
        }
        lock (gate)
        {
            lastStartupRetirementFailure = retirementFailure;
        }
        startupRetired = retired && retirementFailure ==
            Switch2ProUsbStartupRetirementFailure.None && startup.State ==
            Switch2ProUsbStartupTransactionState.Retired;
        return startupRetired;
    }

    private bool TryFinalizeWholeComposite()
    {
        if (!innerStopped || !feedbackQuiesced || !startupRetired ||
            compositeDisposeAttempted)
        {
            return false;
        }
        compositeDisposeAttempted = true;

        if (!inputAdoptionCredential.TryTakeRuntimeRetirementProof(authority,
                lifetime, runtimeOwner, Registration,
                out Switch2ProUsbOwnedCompositeInputFacetRetirementProof proof))
        {
            inputAdoptionCredential.QuarantineIssuer(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired);
            return false;
        }
        inputRetirementProof = proof;

        try
        {
            compositeLease.DisposeQuiesced();
        }
        catch
        {
            inputAdoptionCredential.QuarantineIssuer(
                Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                    QuarantineRequired);
            return false;
        }
        compositeDisposed = true;
        return true;
    }

    private bool TryAuthenticateComposite()
    {
        try
        {
            return bundle.TryGetInputLease(authority, out var input) &&
                bundle.TryGetStartupLease(authority, out var startupLease) &&
                bundle.TryGetBoundedOutputLease(authority, out var output) &&
                ReferenceEquals(input, compositeLease) &&
                ReferenceEquals(startupLease, compositeLease) &&
                ReferenceEquals(output, compositeLease) &&
                feedback.Authenticates(authority);
        }
        catch
        {
            return false;
        }
    }

    private bool TryBeginOperation(
        Switch2RuntimeRegistrationParticipantOperation operation,
        out Switch2RuntimeRegistrationParticipantResult rejected,
        bool requireSubscribed = false, bool requirePrepared = false,
        bool requireCommitted = false, bool requireRetirement = false,
        bool allowQuarantined = false)
    {
        lock (gate)
        {
            if (operationInProgress)
            {
                rejected = Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
                return false;
            }
            if (quarantined && !allowQuarantined)
            {
                rejected = Uncertain(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        QuarantineRequired);
                return false;
            }
            if (state is Switch2ProUsbOwnedCompositeParticipantState.Removed ||
                requireSubscribed && (!subscribed || state !=
                    Switch2ProUsbOwnedCompositeParticipantState.Subscribed) ||
                requirePrepared && state !=
                    Switch2ProUsbOwnedCompositeParticipantState.Prepared ||
                requireCommitted && state !=
                    Switch2ProUsbOwnedCompositeParticipantState.Committed ||
                requireRetirement && (!retirementArmed || state !=
                    Switch2ProUsbOwnedCompositeParticipantState.
                        RetirementArmed))
            {
                rejected = Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
                return false;
            }
            operationInProgress = true;
            rejected = default;
            return true;
        }
    }

    private void FinishOperation(bool quarantine)
    {
        lock (gate)
        {
            operationInProgress = false;
            if (quarantine)
            {
                MarkQuarantinedNoLock();
            }
        }
        QuarantineIssuerIfNeeded(quarantine);
    }

    private bool ObserveFailureNoLock(
        in Switch2RuntimeRegistrationParticipantResult result)
    {
        if (!result.IsValid || result.RequiresQuarantine)
        {
            MarkQuarantinedNoLock();
            return true;
        }
        return false;
    }

    private void MarkQuarantinedNoLock()
    {
        quarantined = true;
        state = Switch2ProUsbOwnedCompositeParticipantState.Quarantined;
    }

    private void QuarantineIssuerIfNeeded(bool quarantine)
    {
        if (!quarantine)
        {
            return;
        }
        inputAdoptionCredential.QuarantineIssuer(
            Switch2ProUsbOwnedCompositeInputAdoptionFailure.
                QuarantineRequired);
    }

    private Switch2RuntimeRegistrationParticipantResult Invoke(
        Func<Switch2RuntimeRegistrationParticipantResult> callback,
        Switch2RuntimeRegistrationParticipantOperation expectedOperation)
    {
        Switch2RuntimeRegistrationParticipantResult result;
        try
        {
            result = callback();
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                firstInnerInvocationExceptionType ??=
                    exception.GetType().FullName;
            }
            return Uncertain(expectedOperation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        if (result.IsValid && result.Operation == expectedOperation)
        {
            return result;
        }
        lock (gate)
        {
            if (firstInvalidInnerExpectedOperation ==
                    Switch2RuntimeRegistrationParticipantOperation.Invalid)
            {
                firstInvalidInnerExpectedOperation = expectedOperation;
                firstInvalidInnerResult = result;
            }
        }
        return Uncertain(expectedOperation,
            Switch2RuntimeRegistrationParticipantFailureKind.
                DependencyThrew);
    }

    private static Switch2RuntimeRegistrationParticipantResult Normalize(
        in Switch2RuntimeRegistrationParticipantResult source,
        Switch2RuntimeRegistrationParticipantOperation targetOperation,
        Switch2RuntimeRegistrationParticipantFailureKind fallback)
    {
        if (source.Succeeded)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                targetOperation);
        }
        if (!source.IsValid || source.Outcome ==
            Switch2RuntimeRegistrationParticipantOutcome.OutcomeUncertain)
        {
            return Uncertain(targetOperation, fallback);
        }
        return Switch2RuntimeRegistrationParticipantResult.Reject(
            targetOperation, fallback);
    }

    private bool IsValidFeedbackResult(
        in Switch2ProUsbOwnedFeedbackActivationResult result,
        Switch2ProUsbOwnedFeedbackActivationOperation operation) =>
        result.Authenticates(authority) && result.Operation == operation;

    private bool IsFeedbackStateConsistent(
        in Switch2ProUsbOwnedFeedbackActivationResult result)
    {
        if (result.Outcome ==
            Switch2ProUsbOwnedFeedbackActivationOutcome.OutcomeUncertain)
        {
            return true;
        }

        Switch2ProUsbOwnedFeedbackActivationState observed;
        try
        {
            observed = feedback.ActivationState;
        }
        catch
        {
            return false;
        }
        return (result.Operation, result.Outcome, observed) switch
        {
            (Switch2ProUsbOwnedFeedbackActivationOperation.Prepare,
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
                Switch2ProUsbOwnedFeedbackActivationState.Prepared) => true,
            (Switch2ProUsbOwnedFeedbackActivationOperation.Prepare,
                Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected,
                Switch2ProUsbOwnedFeedbackActivationState.Dormant) => true,
            (Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
                Switch2ProUsbOwnedFeedbackActivationState.Committed) => true,
            (Switch2ProUsbOwnedFeedbackActivationOperation.Commit,
                Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected,
                Switch2ProUsbOwnedFeedbackActivationState.Prepared) => true,
            (Switch2ProUsbOwnedFeedbackActivationOperation.Abort,
                Switch2ProUsbOwnedFeedbackActivationOutcome.Succeeded,
                Switch2ProUsbOwnedFeedbackActivationState.Aborted) => true,
            (Switch2ProUsbOwnedFeedbackActivationOperation.Abort,
                Switch2ProUsbOwnedFeedbackActivationOutcome.ProvenRejected,
                Switch2ProUsbOwnedFeedbackActivationState.Prepared) => true,
            _ => false,
        };
    }

    private static bool IsUncertainRetirementFailure(
        Switch2ProUsbStartupRetirementFailure failure) => failure is not
        (Switch2ProUsbStartupRetirementFailure.None or
         Switch2ProUsbStartupRetirementFailure.ProvenNotReleased);

    private static Switch2RuntimeRegistrationParticipantResult Uncertain(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantFailureKind failureKind) =>
        Switch2RuntimeRegistrationParticipantResult.Uncertain(operation,
            failureKind, quarantineReason:
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure);

    private static Switch2ProUsbOwnedCompositeParticipantCreateFailure
        CreateFailure(
            Switch2ProUsbOwnedCompositeParticipantCreateFailureKind kind,
            Switch2ProUsbOwnedCompositeLeaseBundle retainedBundle) => new(
        kind, retainedBundle: retainedBundle);

    private static bool IsPositiveTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds > 0 && timeoutMilliseconds <=
            InputControllerRegistration.MaximumStopTimeoutMilliseconds;

    private static bool IsNonNegativeTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds >= 0 && timeoutMilliseconds <=
            InputControllerRegistration.MaximumStopTimeoutMilliseconds;

    private static long CreateDeadline(int timeoutMilliseconds)
    {
        long now = Stopwatch.GetTimestamp();
        long delta = (long)Math.Ceiling(timeoutMilliseconds *
            (double)Stopwatch.Frequency / 1_000d);
        return delta >= long.MaxValue - now ? long.MaxValue : now + delta;
    }

    private static int RemainingMilliseconds(long deadline,
        int originalTimeout)
    {
        if (originalTimeout == 0)
        {
            return 0;
        }
        long now = Stopwatch.GetTimestamp();
        if (now >= deadline)
        {
            return 0;
        }
        double remaining = (deadline - now) * 1_000d /
            Stopwatch.Frequency;
        return Math.Max(1, (int)Math.Min(Math.Ceiling(remaining),
            int.MaxValue));
    }
}
