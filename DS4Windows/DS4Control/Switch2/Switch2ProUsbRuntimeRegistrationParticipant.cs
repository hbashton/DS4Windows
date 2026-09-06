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

/// <summary>
/// Dormant adapter from the exact USB runtime owner to the shared registration
/// participant contract. It retains native credentials and exact delegates but
/// performs no discovery, service registration, routing, or hardware I/O on its
/// own. Every owner, event-accessor, table-authentication, and runtime wait call
/// runs without <see cref="gate"/> held.
/// </summary>
internal sealed class Switch2ProUsbRuntimeRegistrationParticipant :
    ISwitch2RuntimeRegistrationParticipant
{
    private readonly object gate = new();
    private readonly Switch2ProUsbRuntimeOwner owner;
    private readonly InputControllerRegistration registration;
    private readonly EventHandler<
        Switch2ProUsbRuntimeLifecycleAttentionEventArgs> ownerAttentionHandler;

    private InputControllerSlotToken boundToken;
    private Switch2ProUsbRuntimeSlotAdoptionCredential adoptionCredential;
    private Switch2ProUsbRuntimePrepareCredential prepareCredential;
    private InputControllerRetirementClaim retirementClaim;
    private Switch2RuntimeRegistrationCallbacks callbacks;
    private bool adoptionOwned;
    private bool prepareOwned;
    private bool activationCommitted;
    private bool unpublishedAborted;
    private bool retirementArmed;
    private bool stopped;
    private bool removed;
    private bool lifecycleOperationInProgress;
    private bool subscriptionOperationInProgress;
    private bool reportSubscribed;
    private bool attentionSubscribed;
    private bool subscriptionUncertain;

    private Switch2ProUsbRuntimeSlotAdoptionFailure lastAdoptionFailure;
    private Switch2ProUsbRuntimePrepareFailure lastPrepareFailure;
    private string lastPrepareExceptionType;
    private bool lastPrepareReturnedSuccess;
    private bool lastPrepareCredentialValid;
    private bool lastPrepareIssuerMatched;
    private bool lastPrepareGenerationMatched;
    private Switch2ProUsbRuntimeCommitFailure lastCommitFailure;
    private Switch2ProUsbRuntimeUnpublishedAbortFailure lastAbortFailure;

    internal Switch2ProUsbRuntimeRegistrationParticipant(
        Switch2ProUsbRuntimeOwner owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        registration = owner.Registration;
        if (!ReferenceEquals(registration.Owner, owner) ||
            !ReferenceEquals(registration.Device, owner.RuntimeInputDevice) ||
            registration.Generation != owner.RuntimeInputDevice.
                RuntimeGeneration)
        {
            throw new ArgumentException(
                "The USB owner does not expose one exact registration.",
                nameof(owner));
        }
        ownerAttentionHandler = HandleOwnerLifecycleAttention;
    }

    public InputControllerRegistration Registration => registration;

    internal Switch2ProUsbRuntimeSlotAdoptionFailure LastAdoptionFailure
    {
        get { lock (gate) { return lastAdoptionFailure; } }
    }

    internal Switch2ProUsbRuntimePrepareFailure LastPrepareFailure
    {
        get { lock (gate) { return lastPrepareFailure; } }
    }

    internal string LastPrepareExceptionType
    {
        get { lock (gate) { return lastPrepareExceptionType; } }
    }

    internal string LastPrepareProofShape
    {
        get
        {
            lock (gate)
            {
                return $"returned={lastPrepareReturnedSuccess}," +
                    $"credential={lastPrepareCredentialValid}," +
                    $"issuer={lastPrepareIssuerMatched}," +
                    $"generation={lastPrepareGenerationMatched}";
            }
        }
    }

    internal Switch2ProUsbRuntimeCommitFailure LastCommitFailure
    {
        get { lock (gate) { return lastCommitFailure; } }
    }

    internal Switch2ProUsbRuntimeUnpublishedAbortFailure LastAbortFailure
    {
        get { lock (gate) { return lastAbortFailure; } }
    }

    internal bool HasAdoptedSlot
    {
        get { lock (gate) { return adoptionOwned; } }
    }

    internal bool HasPreparedCredential
    {
        get { lock (gate) { return prepareOwned; } }
    }

    internal bool IsSubscribed
    {
        get
        {
            lock (gate)
            {
                return reportSubscribed && attentionSubscribed &&
                    !subscriptionUncertain;
            }
        }
    }

    public Switch2RuntimeRegistrationParticipantResult TryAdoptBoundSlot(
        in InputControllerSlotToken token)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot;
        if (!token.IsValid ||
            !token.Registration.Equals(registration) ||
            !ReferenceEquals(token.Registration.Owner, owner))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential);
        }

        lock (gate)
        {
            if (adoptionOwned)
            {
                return token.Equals(boundToken) ?
                    Switch2RuntimeRegistrationParticipantResult.Success(
                        operation) :
                    Switch2RuntimeRegistrationParticipantResult.Reject(
                        operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            StaleCredential);
            }
            if (lifecycleOperationInProgress || removed ||
                unpublishedAborted)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation, lifecycleOperationInProgress ?
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            OperationAlreadyInProgress :
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            InvalidState);
            }
            lifecycleOperationInProgress = true;
        }

        bool adopted = false;
        bool threw = false;
        Switch2ProUsbRuntimeSlotAdoptionCredential credential = default;
        Switch2ProUsbRuntimeSlotAdoptionFailure failure = default;
        try
        {
            adopted = owner.TryAdoptBoundSlot(token, out credential,
                out failure);
        }
        catch
        {
            threw = true;
        }

        bool validSuccess = adopted && failure ==
                Switch2ProUsbRuntimeSlotAdoptionFailure.None &&
            credential.IsValid && credential.SlotToken.Equals(token);
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastAdoptionFailure = failure;
            if (validSuccess)
            {
                boundToken = token;
                adoptionCredential = credential;
                adoptionOwned = true;
            }
        }

        if (validSuccess)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        if (threw || adopted || failure ==
                Switch2ProUsbRuntimeSlotAdoptionFailure.None)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            Map(failure));
    }

    public Switch2RuntimeRegistrationParticipantResult TrySubscribe(
        in Switch2RuntimeRegistrationCallbacks exactCallbacks)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Subscribe;
        if (!exactCallbacks.IsValid)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidArgument);
        }

        lock (gate)
        {
            if (!adoptionOwned || removed || unpublishedAborted)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (subscriptionUncertain)
            {
                return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        SubscriptionRejected);
            }
            if (reportSubscribed || attentionSubscribed)
            {
                return reportSubscribed && attentionSubscribed &&
                        callbacks.IsExact(exactCallbacks) ?
                    Switch2RuntimeRegistrationParticipantResult.Success(
                        operation) :
                    Switch2RuntimeRegistrationParticipantResult.Reject(
                        operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            InvalidCredential);
            }
            if (subscriptionOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            callbacks = exactCallbacks;
            subscriptionOperationInProgress = true;
        }

        bool reportAdded = false;
        bool attentionAdded = false;
        try
        {
            owner.RuntimeInputDevice.Report += exactCallbacks.ReportHandler;
            reportAdded = true;
            owner.LifecycleAttention += ownerAttentionHandler;
            attentionAdded = true;
        }
        catch
        {
            // A custom event accessor can throw after retaining a delegate.
            // Remove both exact values best-effort, but the result remains
            // uncertain even if those compensations return normally.
            TryRemoveSubscriptions(exactCallbacks, removeReport: true,
                removeAttention: true);
            lock (gate)
            {
                subscriptionOperationInProgress = false;
                subscriptionUncertain = true;
            }
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    SubscriptionRejected);
        }

        lock (gate)
        {
            subscriptionOperationInProgress = false;
            reportSubscribed = reportAdded;
            attentionSubscribed = attentionAdded;
        }
        return Switch2RuntimeRegistrationParticipantResult.Success(operation);
    }

    public Switch2RuntimeRegistrationParticipantResult TryPrepareActivation(
        int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation;
        if (!IsPositiveTimeout(timeoutMilliseconds))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }

        Switch2ProUsbRuntimeSlotAdoptionCredential exactAdoption;
        lock (gate)
        {
            if (!adoptionOwned || !reportSubscribed || !attentionSubscribed ||
                subscriptionUncertain || prepareOwned || activationCommitted ||
                unpublishedAborted || removed)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            lifecycleOperationInProgress = true;
            exactAdoption = adoptionCredential;
        }

        bool prepared = false;
        bool threw = false;
        Switch2ProUsbRuntimePrepareCredential credential = default;
        Switch2ProUsbRuntimePrepareFailure failure = default;
        try
        {
            prepared = owner.TryPrepareActivation(exactAdoption,
                timeoutMilliseconds, out credential, out failure);
        }
        catch (Exception exception)
        {
            threw = true;
            lock (gate)
            {
                lastPrepareExceptionType = exception.GetType().FullName;
            }
        }

        bool credentialValid = credential.IsValid;
        bool issuerMatched = ReferenceEquals(credential.Issuer, owner);
        bool generationMatched = credential.RuntimeGeneration ==
            registration.Generation;
        bool validSuccess = prepared && failure ==
                Switch2ProUsbRuntimePrepareFailure.None &&
            credentialValid && issuerMatched && generationMatched;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastPrepareFailure = failure;
            lastPrepareReturnedSuccess = prepared;
            lastPrepareCredentialValid = credentialValid;
            lastPrepareIssuerMatched = issuerMatched;
            lastPrepareGenerationMatched = generationMatched;
            if (!threw)
            {
                lastPrepareExceptionType = null;
            }
            if (validSuccess)
            {
                prepareCredential = credential;
                prepareOwned = true;
            }
        }

        if (validSuccess)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        if (threw || prepared || failure ==
                Switch2ProUsbRuntimePrepareFailure.None)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            Map(failure));
    }

    public Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
        in InputControllerActivationCommitCredential activationCommit) =>
        TryCommitPreparedCore(activationCommit, cleanupTimeoutMilliseconds:
            null);

    internal Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
        in InputControllerActivationCommitCredential activationCommit,
        int cleanupTimeoutMilliseconds) => TryCommitPreparedCore(
        activationCommit, cleanupTimeoutMilliseconds);

    private Switch2RuntimeRegistrationParticipantResult TryCommitPreparedCore(
        in InputControllerActivationCommitCredential activationCommit,
        int? cleanupTimeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared;
        if (cleanupTimeoutMilliseconds is int timeout &&
            (timeout < 0 || timeout >
                Switch2ProUsbRuntimeOwner.MaximumStopTimeoutMilliseconds))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }
        Switch2ProUsbRuntimePrepareCredential exactPrepare;
        InputControllerSlotToken exactToken;
        lock (gate)
        {
            if (!prepareOwned || activationCommitted || unpublishedAborted ||
                removed)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation, activationCommitted || unpublishedAborted ?
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            AlreadyConsumed :
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            lifecycleOperationInProgress = true;
            exactPrepare = prepareCredential;
            exactToken = boundToken;
        }

        bool tableAuthenticated;
        try
        {
            // USB's native owner commit predates the table commit capability.
            // Authenticate it here, before the single native call, so a copied
            // foreign-table credential cannot release the parked worker.
            tableAuthenticated = activationCommit.IsValid &&
                activationCommit.Authenticates(exactToken);
        }
        catch
        {
            tableAuthenticated = false;
        }
        if (!tableAuthenticated)
        {
            lock (gate)
            {
                lifecycleOperationInProgress = false;
            }
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential);
        }

        bool committed = false;
        bool threw = false;
        Switch2ProUsbRuntimeCommitFailure failure = default;
        try
        {
            committed = cleanupTimeoutMilliseconds is int commitTimeout ?
                owner.TryCommitPrepared(exactPrepare, commitTimeout,
                    out failure) :
                owner.TryCommitPrepared(exactPrepare, out failure);
        }
        catch
        {
            threw = true;
        }

        bool validSuccess = committed && failure ==
            Switch2ProUsbRuntimeCommitFailure.None;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastCommitFailure = failure;
            // Once the exact table commit capability authorizes the owner call,
            // the adapter never retries or aborts that native credential. A
            // false or thrown result is an uncertain post-activation outcome.
            prepareOwned = false;
            activationCommitted = validSuccess;
        }

        if (validSuccess)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        if (threw || committed || failure ==
                Switch2ProUsbRuntimeCommitFailure.None)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            Map(failure), quarantineReason:
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure);
    }

    public Switch2RuntimeRegistrationParticipantResult TryAbortPrepared(
        int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.AbortPrepared;
        if (!IsNonNegativeTimeout(timeoutMilliseconds))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }

        Switch2ProUsbRuntimePrepareCredential exactPrepare;
        lock (gate)
        {
            if (unpublishedAborted)
            {
                return Switch2RuntimeRegistrationParticipantResult.Success(
                    operation);
            }
            if (!prepareOwned || activationCommitted || removed)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation, activationCommitted ?
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            AlreadyConsumed :
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            lifecycleOperationInProgress = true;
            exactPrepare = prepareCredential;
        }

        bool aborted = false;
        bool threw = false;
        Switch2ProUsbRuntimeUnpublishedAbortFailure failure = default;
        try
        {
            aborted = owner.TryAbortPrepared(exactPrepare,
                timeoutMilliseconds, out failure);
        }
        catch
        {
            threw = true;
        }

        bool validSuccess = aborted && failure ==
            Switch2ProUsbRuntimeUnpublishedAbortFailure.None;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastAbortFailure = failure;
            if (validSuccess)
            {
                prepareOwned = false;
                unpublishedAborted = true;
            }
        }
        return NormalizeAbort(operation, aborted, threw, failure,
            validSuccess);
    }

    public Switch2RuntimeRegistrationParticipantResult TryAbortUnpublished(
        int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.AbortUnpublished;
        if (!IsNonNegativeTimeout(timeoutMilliseconds))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }

        Switch2ProUsbRuntimeSlotAdoptionCredential exactAdoption;
        lock (gate)
        {
            if (unpublishedAborted)
            {
                return Switch2RuntimeRegistrationParticipantResult.Success(
                    operation);
            }
            if (!adoptionOwned || prepareOwned || activationCommitted ||
                removed)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            lifecycleOperationInProgress = true;
            exactAdoption = adoptionCredential;
        }

        bool aborted = false;
        bool threw = false;
        Switch2ProUsbRuntimeUnpublishedAbortFailure failure = default;
        try
        {
            aborted = owner.TryAbortUnpublished(exactAdoption,
                timeoutMilliseconds, out failure);
        }
        catch
        {
            threw = true;
        }

        bool validSuccess = aborted && failure ==
            Switch2ProUsbRuntimeUnpublishedAbortFailure.None;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastAbortFailure = failure;
            if (validSuccess)
            {
                unpublishedAborted = true;
            }
        }
        return NormalizeAbort(operation, aborted, threw, failure,
            validSuccess);
    }

    public Switch2RuntimeRegistrationParticipantResult TryArmRetirement(
        in InputControllerRetirementClaim claim)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement;
        if (!claim.IsValid)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential);
        }

        lock (gate)
        {
            if (!activationCommitted || removed)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (!claim.Token.Equals(boundToken) ||
                !claim.Token.Registration.Equals(registration) ||
                !ReferenceEquals(claim.Token.Registration.Owner, owner))
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        StaleCredential);
            }
            if (retirementArmed)
            {
                return retirementClaim.Equals(claim) ?
                    Switch2RuntimeRegistrationParticipantResult.Success(
                        operation) :
                    Switch2RuntimeRegistrationParticipantResult.Reject(
                        operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            RetirementArmRejected);
            }

            // USB stop does not currently consume the table claim. Retaining
            // and authenticating it here gives the shared core one uniform
            // pre-stop contract without changing the frozen owner.
            retirementClaim = claim;
            retirementArmed = true;
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
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
        lock (gate)
        {
            if (!retirementArmed || removed)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
        }

        bool available;
        try
        {
            available = owner.RuntimeInputDevice.
                TryWaitForPublicationAvailability(timeoutMilliseconds);
        }
        catch
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return available ?
            Switch2RuntimeRegistrationParticipantResult.Success(operation) :
            Switch2RuntimeRegistrationParticipantResult.Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    PublicationDrainTimedOut,
                quarantineReason:
                    InputControllerSlotQuarantineReason.DrainTimedOut);
    }

    public Switch2RuntimeRegistrationParticipantResult TryStopAndQuiesce(
        int timeoutMilliseconds)
    {
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
        lock (gate)
        {
            if (stopped)
            {
                return Switch2RuntimeRegistrationParticipantResult.Success(
                    operation);
            }
            if (!activationCommitted || !retirementArmed || removed)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            lifecycleOperationInProgress = true;
        }

        bool quiesced;
        InputControllerOwnerOperationFailure ownerFailure;
        try
        {
            quiesced = registration.TryStopAndQuiesce(timeoutMilliseconds,
                out ownerFailure);
        }
        catch
        {
            quiesced = false;
            ownerFailure = InputControllerOwnerOperationFailure.OwnerThrew;
        }
        Switch2ProUsbRuntimeStopFailure stopFailure = owner.LastStopFailure;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            stopped = quiesced && ownerFailure ==
                InputControllerOwnerOperationFailure.None;
        }
        if (stopped)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        return NormalizeStop(ownerFailure, stopFailure);
    }

    public Switch2RuntimeRegistrationParticipantResult TryUnsubscribe()
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe;
        Switch2RuntimeRegistrationCallbacks exactCallbacks;
        bool removeReport;
        bool removeAttention;
        lock (gate)
        {
            if (subscriptionUncertain)
            {
                return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        SubscriptionRejected);
            }
            if (subscriptionOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            removeReport = reportSubscribed;
            removeAttention = attentionSubscribed;
            if (!removeReport && !removeAttention)
            {
                return Switch2RuntimeRegistrationParticipantResult.Success(
                    operation);
            }
            subscriptionOperationInProgress = true;
            reportSubscribed = false;
            attentionSubscribed = false;
            exactCallbacks = callbacks;
        }

        bool succeeded = TryRemoveSubscriptions(exactCallbacks, removeReport,
            removeAttention);
        lock (gate)
        {
            subscriptionOperationInProgress = false;
            subscriptionUncertain = !succeeded;
        }
        return succeeded ?
            Switch2RuntimeRegistrationParticipantResult.Success(operation) :
            Switch2RuntimeRegistrationParticipantResult.Uncertain(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    SubscriptionRejected);
    }

    public Switch2RuntimeRegistrationParticipantResult TryRemove()
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Remove;
        lock (gate)
        {
            if (removed)
            {
                return Switch2RuntimeRegistrationParticipantResult.Success(
                    operation);
            }
            if (!stopped || reportSubscribed || attentionSubscribed ||
                subscriptionUncertain)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            lifecycleOperationInProgress = true;
        }

        bool didRemove;
        InputControllerOwnerOperationFailure ownerFailure;
        try
        {
            didRemove = registration.TryRemove(out ownerFailure);
        }
        catch
        {
            didRemove = false;
            ownerFailure = InputControllerOwnerOperationFailure.OwnerThrew;
        }
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            removed = didRemove && ownerFailure ==
                InputControllerOwnerOperationFailure.None;
        }
        if (removed)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }

        Switch2RuntimeRegistrationParticipantFailureKind kind = ownerFailure
            switch
            {
                InputControllerOwnerOperationFailure.
                        OwnerAuthenticationFailed =>
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OwnerAuthenticationLost,
                InputControllerOwnerOperationFailure.OwnerThrew =>
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        DependencyThrew,
                _ => Switch2RuntimeRegistrationParticipantFailureKind.
                    RemoveRejected,
            };
        InputControllerSlotQuarantineReason reason = ownerFailure switch
        {
            InputControllerOwnerOperationFailure.OwnerAuthenticationFailed =>
                InputControllerSlotQuarantineReason.OwnerAuthenticationLost,
            InputControllerOwnerOperationFailure.OwnerThrew =>
                InputControllerSlotQuarantineReason.OwnerThrew,
            _ => InputControllerSlotQuarantineReason.RemoveRejected,
        };
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            kind, ownerFailure, reason);
    }

    private void HandleOwnerLifecycleAttention(object sender,
        Switch2ProUsbRuntimeLifecycleAttentionEventArgs evidence)
    {
        if (!ReferenceEquals(sender, owner) || evidence == null ||
            evidence.RuntimeGeneration != registration.Generation)
        {
            return;
        }
        Switch2RuntimeRegistrationLifecycleAttentionKind kind = evidence.Kind
            switch
            {
                Switch2ProUsbRuntimeLifecycleAttentionKind.InputRejected =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        InputRejected,
                Switch2ProUsbRuntimeLifecycleAttentionKind.SubscriberRejected =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        SubscriberRejected,
                Switch2ProUsbRuntimeLifecycleAttentionKind.NativeReadFailure =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        ProducerFailed,
                _ => Switch2RuntimeRegistrationLifecycleAttentionKind.Invalid,
            };
        if (kind == Switch2RuntimeRegistrationLifecycleAttentionKind.Invalid)
        {
            return;
        }

        Switch2RuntimeRegistrationLifecycleAttentionCallback callback;
        lock (gate)
        {
            if (!attentionSubscribed && !subscriptionOperationInProgress)
            {
                return;
            }
            callback = callbacks.AttentionHandler;
        }
        if (callback == null)
        {
            return;
        }

        var attention = new Switch2RuntimeRegistrationLifecycleAttention(
            registration, kind);
        try
        {
            callback(attention);
        }
        catch
        {
            // Lifecycle attention is a one-shot wake-up, not teardown proof.
            // A hostile observer cannot unwind the physical producer callback.
        }
    }

    private bool TryRemoveSubscriptions(
        in Switch2RuntimeRegistrationCallbacks exactCallbacks,
        bool removeReport, bool removeAttention)
    {
        bool succeeded = true;
        if (removeReport)
        {
            try
            {
                owner.RuntimeInputDevice.Report -=
                    exactCallbacks.ReportHandler;
            }
            catch
            {
                succeeded = false;
            }
        }
        if (removeAttention)
        {
            try
            {
                owner.LifecycleAttention -= ownerAttentionHandler;
            }
            catch
            {
                succeeded = false;
            }
        }
        return succeeded;
    }

    private static Switch2RuntimeRegistrationParticipantResult NormalizeAbort(
        Switch2RuntimeRegistrationParticipantOperation operation,
        bool aborted, bool threw,
        Switch2ProUsbRuntimeUnpublishedAbortFailure failure,
        bool validSuccess)
    {
        if (validSuccess)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        if (threw || aborted || failure ==
                Switch2ProUsbRuntimeUnpublishedAbortFailure.None)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            Map(failure), quarantineReason:
                InputControllerSlotQuarantineReason.StopRejected);
    }

    private static Switch2RuntimeRegistrationParticipantResult NormalizeStop(
        InputControllerOwnerOperationFailure ownerFailure,
        in Switch2ProUsbRuntimeStopFailure stopFailure)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce;
        if (ownerFailure == InputControllerOwnerOperationFailure.
                OwnerAuthenticationFailed)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost, ownerFailure,
                InputControllerSlotQuarantineReason.OwnerAuthenticationLost);
        }
        if (ownerFailure == InputControllerOwnerOperationFailure.OwnerThrew)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew, ownerFailure,
                InputControllerSlotQuarantineReason.OwnerThrew);
        }
        if (stopFailure.Kind is
                Switch2ProUsbRuntimeStopFailureKind.
                    TerminalPublicationTimedOut or
                Switch2ProUsbRuntimeStopFailureKind.
                    TerminalPublicationRejected or
                Switch2ProUsbRuntimeStopFailureKind.TerminalDeliveryRejected)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    TerminalNeutralRejected, ownerFailure,
                InputControllerSlotQuarantineReason.
                    TerminalNeutralNotObserved);
        }
        if (stopFailure.Kind is
                Switch2ProUsbRuntimeStopFailureKind.PumpTimedOut or
                Switch2ProUsbRuntimeStopFailureKind.SinkPublicationTimedOut)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.StopRejected,
                ownerFailure,
                InputControllerSlotQuarantineReason.StopTimedOut);
        }

        Switch2RuntimeRegistrationParticipantFailureKind kind =
            stopFailure.Kind ==
                    Switch2ProUsbRuntimeStopFailureKind.DependencyThrew ?
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew :
                stopFailure.Kind ==
                    Switch2ProUsbRuntimeStopFailureKind.QuarantineRequired ?
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        QuarantineRequired :
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        StopRejected;
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            kind, ownerFailure,
            InputControllerSlotQuarantineReason.StopRejected);
    }

    private static Switch2RuntimeRegistrationParticipantFailureKind Map(
        Switch2ProUsbRuntimeSlotAdoptionFailure failure) => failure switch
        {
            Switch2ProUsbRuntimeSlotAdoptionFailure.InvalidToken =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential,
            Switch2ProUsbRuntimeSlotAdoptionFailure.InvalidState =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidState,
            Switch2ProUsbRuntimeSlotAdoptionFailure.
                    OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2ProUsbRuntimeSlotAdoptionFailure.
                    DifferentSlotAlreadyAdopted =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    StaleCredential,
            Switch2ProUsbRuntimeSlotAdoptionFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                DependencyThrew,
        };

    private static Switch2RuntimeRegistrationParticipantFailureKind Map(
        Switch2ProUsbRuntimePrepareFailure failure) => failure switch
        {
            Switch2ProUsbRuntimePrepareFailure.InvalidRegistration or
                Switch2ProUsbRuntimePrepareFailure.
                    InvalidSlotAdoptionCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential,
            Switch2ProUsbRuntimePrepareFailure.OwnerAuthenticationFailed =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost,
            Switch2ProUsbRuntimePrepareFailure.InvalidTimeout =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout,
            Switch2ProUsbRuntimePrepareFailure.InvalidState =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidState,
            Switch2ProUsbRuntimePrepareFailure.OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2ProUsbRuntimePrepareFailure.PumpPrepareTimedOut =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    PrepareRejected,
            Switch2ProUsbRuntimePrepareFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            Switch2ProUsbRuntimePrepareFailure.RuntimeArmRejected or
                Switch2ProUsbRuntimePrepareFailure.PumpPrepareRejected or
                Switch2ProUsbRuntimePrepareFailure.CleanupRejected =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    PrepareRejected,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                DependencyThrew,
        };

    private static Switch2RuntimeRegistrationParticipantFailureKind Map(
        Switch2ProUsbRuntimeCommitFailure failure) => failure switch
        {
            Switch2ProUsbRuntimeCommitFailure.InvalidCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential,
            Switch2ProUsbRuntimeCommitFailure.StaleCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    StaleCredential,
            Switch2ProUsbRuntimeCommitFailure.AlreadyConsumed =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    AlreadyConsumed,
            Switch2ProUsbRuntimeCommitFailure.InvalidState =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidState,
            Switch2ProUsbRuntimeCommitFailure.OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2ProUsbRuntimeCommitFailure.PumpCommitRejected =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    CommitRejected,
            Switch2ProUsbRuntimeCommitFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            Switch2ProUsbRuntimeCommitFailure.InvalidTimeout =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                DependencyThrew,
        };

    private static Switch2RuntimeRegistrationParticipantFailureKind Map(
        Switch2ProUsbRuntimeUnpublishedAbortFailure failure) => failure switch
        {
            Switch2ProUsbRuntimeUnpublishedAbortFailure.InvalidRegistration or
                Switch2ProUsbRuntimeUnpublishedAbortFailure.InvalidCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential,
            Switch2ProUsbRuntimeUnpublishedAbortFailure.StaleCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    StaleCredential,
            Switch2ProUsbRuntimeUnpublishedAbortFailure.AlreadyConsumed =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    AlreadyConsumed,
            Switch2ProUsbRuntimeUnpublishedAbortFailure.InvalidTimeout =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout,
            Switch2ProUsbRuntimeUnpublishedAbortFailure.InvalidState =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidState,
            Switch2ProUsbRuntimeUnpublishedAbortFailure.
                    OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2ProUsbRuntimeUnpublishedAbortFailure.DependencyThrew =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew,
            Switch2ProUsbRuntimeUnpublishedAbortFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                AbortRejected,
        };

    private static bool IsPositiveTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds > 0 && timeoutMilliseconds <=
            Switch2ProUsbRuntimeOwner.MaximumStopTimeoutMilliseconds;

    private static bool IsNonNegativeTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds >= 0 && timeoutMilliseconds <=
            Switch2ProUsbRuntimeOwner.MaximumStopTimeoutMilliseconds;
}
