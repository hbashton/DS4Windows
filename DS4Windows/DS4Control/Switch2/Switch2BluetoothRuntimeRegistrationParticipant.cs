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
/// Narrow owner-call seam used to prove the Bluetooth participant's failure
/// normalization. The production implementation is permanently bound to one
/// exact owner; this interface does not expose discovery, pairing, transport
/// creation, or registration-table mutation.
/// </summary>
internal interface ISwitch2BluetoothRuntimeRegistrationParticipantOperations
{
    Switch2BluetoothRuntimeOwner Owner { get; }

    bool TryAdoptBoundSlot(in InputControllerSlotToken token,
        out Switch2BluetoothRuntimeSlotAdoptionCredential credential,
        out Switch2BluetoothRuntimeSlotAdoptionFailure failure);

    void AddReport(DS4Device.ReportHandler<EventArgs> handler);

    void RemoveReport(DS4Device.ReportHandler<EventArgs> handler);

    void AddAttention(EventHandler<
        Switch2BluetoothRuntimeLifecycleAttentionEventArgs> handler);

    void RemoveAttention(EventHandler<
        Switch2BluetoothRuntimeLifecycleAttentionEventArgs> handler);

    bool TryPrepareActivation(
        in Switch2BluetoothRuntimeSlotAdoptionCredential adoptionCredential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimePrepareCredential credential,
        out Switch2BluetoothRuntimePrepareFailure failure);

    bool TryCommitPrepared(
        in Switch2BluetoothRuntimePrepareCredential prepareCredential,
        in InputControllerActivationCommitCredential activationCommit,
        out Switch2BluetoothRuntimeCommitFailure failure);

    bool TryAbortPrepared(
        in Switch2BluetoothRuntimePrepareCredential prepareCredential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeAbortFailure failure);

    bool TryAbortUnpublished(
        in Switch2BluetoothRuntimeSlotAdoptionCredential adoptionCredential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeAbortFailure failure);

    bool TryArmRetirement(in InputControllerRetirementClaim claim,
        out Switch2BluetoothRuntimeRetirementArmFailure failure);

    bool TryWaitForPublicationAvailability(int timeoutMilliseconds);

    bool TryStopAndQuiesce(int timeoutMilliseconds,
        out InputControllerOwnerOperationFailure failure);

    Switch2BluetoothRuntimeStopFailure LastStopFailure { get; }

    bool TryRemove(out InputControllerOwnerOperationFailure failure);
}

internal sealed class
    Switch2BluetoothRuntimeRegistrationParticipantOperations :
    ISwitch2BluetoothRuntimeRegistrationParticipantOperations
{
    private readonly Switch2BluetoothRuntimeOwner owner;
    private readonly InputControllerRegistration registration;

    internal Switch2BluetoothRuntimeRegistrationParticipantOperations(
        Switch2BluetoothRuntimeOwner owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        registration = owner.Registration;
    }

    public Switch2BluetoothRuntimeOwner Owner => owner;

    public bool TryAdoptBoundSlot(in InputControllerSlotToken token,
        out Switch2BluetoothRuntimeSlotAdoptionCredential credential,
        out Switch2BluetoothRuntimeSlotAdoptionFailure failure) =>
        owner.TryAdoptBoundSlot(token, out credential, out failure);

    public void AddReport(DS4Device.ReportHandler<EventArgs> handler) =>
        owner.RuntimeDevice.Report += handler;

    public void RemoveReport(DS4Device.ReportHandler<EventArgs> handler) =>
        owner.RuntimeDevice.Report -= handler;

    public void AddAttention(EventHandler<
        Switch2BluetoothRuntimeLifecycleAttentionEventArgs> handler) =>
        owner.LifecycleAttention += handler;

    public void RemoveAttention(EventHandler<
        Switch2BluetoothRuntimeLifecycleAttentionEventArgs> handler) =>
        owner.LifecycleAttention -= handler;

    public bool TryPrepareActivation(
        in Switch2BluetoothRuntimeSlotAdoptionCredential adoptionCredential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimePrepareCredential credential,
        out Switch2BluetoothRuntimePrepareFailure failure) =>
        owner.TryPrepareActivation(adoptionCredential, timeoutMilliseconds,
            out credential, out failure);

    public bool TryCommitPrepared(
        in Switch2BluetoothRuntimePrepareCredential prepareCredential,
        in InputControllerActivationCommitCredential activationCommit,
        out Switch2BluetoothRuntimeCommitFailure failure) =>
        owner.TryCommitPrepared(prepareCredential, activationCommit,
            out failure);

    public bool TryAbortPrepared(
        in Switch2BluetoothRuntimePrepareCredential prepareCredential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeAbortFailure failure) =>
        owner.TryAbortPrepared(prepareCredential, timeoutMilliseconds,
            out failure);

    public bool TryAbortUnpublished(
        in Switch2BluetoothRuntimeSlotAdoptionCredential adoptionCredential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeAbortFailure failure) =>
        owner.TryAbortUnpublished(adoptionCredential, timeoutMilliseconds,
            out failure);

    public bool TryArmRetirement(in InputControllerRetirementClaim claim,
        out Switch2BluetoothRuntimeRetirementArmFailure failure) =>
        owner.TryArmRetirement(claim, out failure);

    public bool TryWaitForPublicationAvailability(int timeoutMilliseconds) =>
        owner.RuntimeDevice.TryWaitForPublicationAvailability(
            timeoutMilliseconds);

    public bool TryStopAndQuiesce(int timeoutMilliseconds,
        out InputControllerOwnerOperationFailure failure) =>
        registration.TryStopAndQuiesce(timeoutMilliseconds, out failure);

    public Switch2BluetoothRuntimeStopFailure LastStopFailure =>
        owner.LastStopFailure;

    public bool TryRemove(out InputControllerOwnerOperationFailure failure) =>
        registration.TryRemove(out failure);
}

/// <summary>
/// Dormant adapter from one exact Bluetooth runtime owner to the shared
/// registration transaction contract. It keeps all owner-issued credentials
/// private, subscribes the table's report delegate directly, and performs no
/// discovery, association, reconnect, output, or service registration.
/// Every owner/event/runtime call occurs without <see cref="gate"/> held.
/// </summary>
internal sealed class Switch2BluetoothRuntimeRegistrationParticipant :
    ISwitch2RuntimeRegistrationParticipant
{
    private readonly object gate = new();
    private readonly Switch2BluetoothRuntimeOwner owner;
    private readonly InputControllerRegistration registration;
    private readonly
        ISwitch2BluetoothRuntimeRegistrationParticipantOperations operations;
    private readonly EventHandler<
        Switch2BluetoothRuntimeLifecycleAttentionEventArgs>
        ownerAttentionHandler;

    private InputControllerSlotToken boundToken;
    private Switch2BluetoothRuntimeSlotAdoptionCredential adoptionCredential;
    private Switch2BluetoothRuntimePrepareCredential prepareCredential;
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

    private Switch2BluetoothRuntimeSlotAdoptionFailure lastAdoptionFailure;
    private Switch2BluetoothRuntimePrepareFailure lastPrepareFailure;
    private Switch2BluetoothRuntimeCommitFailure lastCommitFailure;
    private Switch2BluetoothRuntimeAbortFailure lastAbortFailure;
    private Switch2BluetoothRuntimeRetirementArmFailure
        lastRetirementArmFailure;

    internal Switch2BluetoothRuntimeRegistrationParticipant(
        Switch2BluetoothRuntimeOwner owner) : this(owner,
        new Switch2BluetoothRuntimeRegistrationParticipantOperations(owner))
    {
    }

    internal Switch2BluetoothRuntimeRegistrationParticipant(
        Switch2BluetoothRuntimeOwner owner,
        ISwitch2BluetoothRuntimeRegistrationParticipantOperations operations)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.operations = operations ??
            throw new ArgumentNullException(nameof(operations));
        if (!ReferenceEquals(operations.Owner, owner))
        {
            throw new ArgumentException(
                "The Bluetooth operation seam is not bound to the exact owner.",
                nameof(operations));
        }

        registration = owner.Registration;
        if (!ReferenceEquals(registration.Owner, owner) ||
            !ReferenceEquals(registration.Device, owner.RuntimeDevice) ||
            registration.Generation != owner.RuntimeDevice.RuntimeGeneration ||
            registration.OwnershipKind !=
                InputControllerOwnershipKind.Switch2Runtime)
        {
            throw new ArgumentException(
                "The Bluetooth owner does not expose one exact registration.",
                nameof(owner));
        }
        ownerAttentionHandler = HandleOwnerLifecycleAttention;
    }

    public InputControllerRegistration Registration => registration;

    internal Switch2BluetoothRuntimeSlotAdoptionFailure LastAdoptionFailure
    {
        get { lock (gate) { return lastAdoptionFailure; } }
    }

    internal Switch2BluetoothRuntimePrepareFailure LastPrepareFailure
    {
        get { lock (gate) { return lastPrepareFailure; } }
    }

    internal Switch2BluetoothRuntimeCommitFailure LastCommitFailure
    {
        get { lock (gate) { return lastCommitFailure; } }
    }

    internal Switch2BluetoothRuntimeAbortFailure LastAbortFailure
    {
        get { lock (gate) { return lastAbortFailure; } }
    }

    internal Switch2BluetoothRuntimeRetirementArmFailure
        LastRetirementArmFailure
    {
        get { lock (gate) { return lastRetirementArmFailure; } }
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
        if (!token.IsValid || !token.Registration.Equals(registration) ||
            !ReferenceEquals(token.Registration.Owner, owner))
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential);
        }
        if (!registration.IsOwnerAuthenticated)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost,
                quarantineReason: InputControllerSlotQuarantineReason.
                    OwnerAuthenticationLost);
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
        Switch2BluetoothRuntimeSlotAdoptionCredential credential = default;
        Switch2BluetoothRuntimeSlotAdoptionFailure failure = default;
        try
        {
            adopted = operations.TryAdoptBoundSlot(token, out credential,
                out failure);
        }
        catch
        {
            threw = true;
        }

        bool structurallyValid = adopted && failure ==
                Switch2BluetoothRuntimeSlotAdoptionFailure.None &&
            credential.IsValid && credential.SlotToken.Equals(token) &&
            credential.Model == owner.Model && credential.DeviceGeneration ==
                registration.Generation;
        bool attested = false;
        bool attestationThrew = false;
        if (structurallyValid)
        {
            try
            {
                // Adoption credentials intentionally hide their issuer fence.
                // An exact idempotent owner retry is the reflection-free
                // attestation that a test seam (or later platform wrapper)
                // did not substitute a structurally similar foreign value.
                attested = owner.TryAdoptBoundSlot(token,
                        out var attestedCredential,
                        out var attestationFailure) &&
                    attestationFailure ==
                        Switch2BluetoothRuntimeSlotAdoptionFailure.None &&
                    attestedCredential.Equals(credential);
            }
            catch
            {
                attestationThrew = true;
            }
        }
        bool validSuccess = structurallyValid && attested;
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
        if (threw || attestationThrew || adopted || failure ==
                Switch2BluetoothRuntimeSlotAdoptionFailure.None ||
            !IsDefined(failure))
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
        if (!registration.IsOwnerAuthenticated)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    SubscriptionRejected,
                quarantineReason: InputControllerSlotQuarantineReason.
                    OwnerAuthenticationLost);
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

        try
        {
            // The exact report delegate is installed directly. There is no
            // participant callback wrapper on the steady report path.
            operations.AddReport(exactCallbacks.ReportHandler);
            operations.AddAttention(ownerAttentionHandler);
        }
        catch
        {
            // A hostile accessor may throw after retaining its delegate.
            // Exact removal is best-effort, but cannot turn uncertainty into
            // proof that no callback remains reachable.
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
            reportSubscribed = true;
            attentionSubscribed = true;
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

        Switch2BluetoothRuntimeSlotAdoptionCredential exactAdoption;
        InputControllerSlotToken exactToken;
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
            exactToken = boundToken;
        }

        if (!registration.IsOwnerAuthenticated)
        {
            lock (gate)
            {
                lifecycleOperationInProgress = false;
            }
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost,
                quarantineReason: InputControllerSlotQuarantineReason.
                    OwnerAuthenticationLost);
        }

        bool prepared = false;
        bool threw = false;
        Switch2BluetoothRuntimePrepareCredential credential = default;
        Switch2BluetoothRuntimePrepareFailure failure = default;
        try
        {
            prepared = operations.TryPrepareActivation(exactAdoption,
                timeoutMilliseconds, out credential, out failure);
        }
        catch
        {
            threw = true;
        }

        bool exactCredential = credential.IsValid &&
            credential.Authenticates(owner, credential.Fence, exactToken,
                owner.Model, exactAdoption.ScanGeneration,
                registration.Generation, exactAdoption.TransportGeneration);
        bool validSuccess = prepared && failure ==
                Switch2BluetoothRuntimePrepareFailure.None &&
            exactCredential && owner.State ==
                Switch2BluetoothRuntimeOwnerState.Prepared;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastPrepareFailure = failure;
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
                Switch2BluetoothRuntimePrepareFailure.None ||
            !IsDefined(failure))
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            Map(failure), quarantineReason: PrepareQuarantineReason(failure));
    }

    public Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
        in InputControllerActivationCommitCredential activationCommit)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared;
        Switch2BluetoothRuntimePrepareCredential exactPrepare;
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
        Switch2BluetoothRuntimeCommitFailure failure = default;
        try
        {
            committed = operations.TryCommitPrepared(exactPrepare,
                activationCommit, out failure);
        }
        catch
        {
            threw = true;
        }

        bool validSuccess = committed && failure ==
                Switch2BluetoothRuntimeCommitFailure.None && owner.State ==
                Switch2BluetoothRuntimeOwnerState.Active;
        bool credentialPossiblyConsumed = threw || committed ||
            failure is Switch2BluetoothRuntimeCommitFailure.
                    InputCommitRejected or
                Switch2BluetoothRuntimeCommitFailure.DependencyThrew or
                Switch2BluetoothRuntimeCommitFailure.QuarantineRequired ||
            owner.State is Switch2BluetoothRuntimeOwnerState.Active or
                Switch2BluetoothRuntimeOwnerState.Quarantined;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastCommitFailure = failure;
            if (validSuccess || credentialPossiblyConsumed)
            {
                prepareOwned = false;
            }
            activationCommitted = validSuccess;
        }

        if (validSuccess)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        if (threw || committed || failure ==
                Switch2BluetoothRuntimeCommitFailure.None ||
            !IsDefined(failure) || failure ==
                Switch2BluetoothRuntimeCommitFailure.DependencyThrew)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            Map(failure), quarantineReason: CommitQuarantineReason(failure));
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

        Switch2BluetoothRuntimePrepareCredential exactPrepare;
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
        Switch2BluetoothRuntimeAbortFailure failure = default;
        try
        {
            aborted = operations.TryAbortPrepared(exactPrepare,
                timeoutMilliseconds, out failure);
        }
        catch
        {
            threw = true;
        }
        return CompleteAbort(operation, aborted, threw, failure,
            wasPrepared: true);
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

        Switch2BluetoothRuntimeSlotAdoptionCredential exactAdoption;
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
        Switch2BluetoothRuntimeAbortFailure failure = default;
        try
        {
            aborted = operations.TryAbortUnpublished(exactAdoption,
                timeoutMilliseconds, out failure);
        }
        catch
        {
            threw = true;
        }
        return CompleteAbort(operation, aborted, threw, failure,
            wasPrepared: false);
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
            if (lifecycleOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            lifecycleOperationInProgress = true;
        }

        bool armed = false;
        bool threw = false;
        Switch2BluetoothRuntimeRetirementArmFailure failure = default;
        try
        {
            armed = operations.TryArmRetirement(claim, out failure);
        }
        catch
        {
            threw = true;
        }

        bool structurallyValid = armed && failure ==
            Switch2BluetoothRuntimeRetirementArmFailure.None;
        bool attested = false;
        bool attestationThrew = false;
        if (structurallyValid)
        {
            try
            {
                // The claim is retained by the Bluetooth owner itself. Its
                // exact idempotent retry proves that the owner, rather than
                // only an adapter seam, accepted this table capability.
                attested = owner.TryArmRetirement(claim,
                        out var attestationFailure) &&
                    attestationFailure ==
                        Switch2BluetoothRuntimeRetirementArmFailure.None;
            }
            catch
            {
                attestationThrew = true;
            }
        }
        bool validSuccess = structurallyValid && attested;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastRetirementArmFailure = failure;
            if (validSuccess)
            {
                retirementClaim = claim;
                retirementArmed = true;
            }
        }
        if (validSuccess)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        if (threw || attestationThrew || armed || failure ==
                Switch2BluetoothRuntimeRetirementArmFailure.None ||
            !IsDefined(failure))
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            Map(failure), quarantineReason:
                RetirementQuarantineReason(failure));
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
            if (lifecycleOperationInProgress)
            {
                return Switch2RuntimeRegistrationParticipantResult.Reject(
                    operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            lifecycleOperationInProgress = true;
        }

        bool available = false;
        bool threw = false;
        try
        {
            available = operations.TryWaitForPublicationAvailability(
                timeoutMilliseconds);
        }
        catch
        {
            threw = true;
        }
        lock (gate)
        {
            lifecycleOperationInProgress = false;
        }
        if (threw)
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
                quarantineReason: InputControllerSlotQuarantineReason.
                    DrainTimedOut);
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

        bool quiesced = false;
        bool threw = false;
        InputControllerOwnerOperationFailure ownerFailure = default;
        try
        {
            quiesced = operations.TryStopAndQuiesce(timeoutMilliseconds,
                out ownerFailure);
        }
        catch
        {
            threw = true;
            ownerFailure = InputControllerOwnerOperationFailure.OwnerThrew;
        }

        bool exactProof = !threw && quiesced && ownerFailure ==
                InputControllerOwnerOperationFailure.None &&
            HasExactStopProof();
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            stopped = exactProof;
        }
        if (exactProof)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        if (threw || quiesced || ownerFailure ==
                InputControllerOwnerOperationFailure.None ||
            !IsDefined(ownerFailure))
        {
            InputControllerOwnerOperationFailure normalizedOwnerFailure =
                IsDefined(ownerFailure) ? ownerFailure : default;
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation, quiesced ?
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        TerminalNeutralRejected :
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        DependencyThrew,
                normalizedOwnerFailure,
                quiesced ? InputControllerSlotQuarantineReason.
                        TerminalNeutralNotObserved :
                    InputControllerSlotQuarantineReason.
                        ExternalLifecycleFailure);
        }

        Switch2BluetoothRuntimeStopFailure stopFailure;
        try
        {
            stopFailure = operations.LastStopFailure;
        }
        catch
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew,
                InputControllerOwnerOperationFailure.OwnerThrew,
                InputControllerSlotQuarantineReason.OwnerThrew);
        }
        if (!IsDefined(stopFailure.Kind) || stopFailure.Kind ==
                Switch2BluetoothRuntimeStopFailureKind.None)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew, ownerFailure,
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure);
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

        bool didRemove = false;
        bool threw = false;
        InputControllerOwnerOperationFailure ownerFailure = default;
        try
        {
            didRemove = operations.TryRemove(out ownerFailure);
        }
        catch
        {
            threw = true;
            ownerFailure = InputControllerOwnerOperationFailure.OwnerThrew;
        }
        bool exactProof = !threw && didRemove && ownerFailure ==
                InputControllerOwnerOperationFailure.None && owner.State ==
                Switch2BluetoothRuntimeOwnerState.Removed;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            removed = exactProof;
        }
        if (exactProof)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        if (threw || didRemove || ownerFailure ==
                InputControllerOwnerOperationFailure.None ||
            !IsDefined(ownerFailure))
        {
            InputControllerOwnerOperationFailure normalizedOwnerFailure =
                IsDefined(ownerFailure) ? ownerFailure : default;
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew,
                normalizedOwnerFailure,
                InputControllerSlotQuarantineReason.ExternalLifecycleFailure);
        }

        Switch2RuntimeRegistrationParticipantFailureKind kind = ownerFailure
            switch
            {
                InputControllerOwnerOperationFailure.
                        OwnerAuthenticationFailed or
                    InputControllerOwnerOperationFailure.InvalidRegistration =>
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
            InputControllerOwnerOperationFailure.OwnerAuthenticationFailed or
                InputControllerOwnerOperationFailure.InvalidRegistration =>
                InputControllerSlotQuarantineReason.OwnerAuthenticationLost,
            InputControllerOwnerOperationFailure.OwnerThrew =>
                InputControllerSlotQuarantineReason.OwnerThrew,
            _ => InputControllerSlotQuarantineReason.RemoveRejected,
        };
        return ownerFailure == InputControllerOwnerOperationFailure.OwnerThrew ?
            Switch2RuntimeRegistrationParticipantResult.Uncertain(operation,
                kind, ownerFailure, reason) :
            Switch2RuntimeRegistrationParticipantResult.Reject(operation,
                kind, ownerFailure, reason);
    }

    private Switch2RuntimeRegistrationParticipantResult CompleteAbort(
        Switch2RuntimeRegistrationParticipantOperation operation,
        bool aborted, bool threw, Switch2BluetoothRuntimeAbortFailure failure,
        bool wasPrepared)
    {
        bool exactProof = !threw && aborted && failure ==
                Switch2BluetoothRuntimeAbortFailure.None &&
            HasExactAbortProof();
        bool credentialPossiblyConsumed = wasPrepared &&
            (threw || aborted || failure is
                Switch2BluetoothRuntimeAbortFailure.AlreadyConsumed or
                Switch2BluetoothRuntimeAbortFailure.InputAbortRejected or
                Switch2BluetoothRuntimeAbortFailure.PumpRejected or
                Switch2BluetoothRuntimeAbortFailure.PumpTimedOut or
                Switch2BluetoothRuntimeAbortFailure.LeaseReleaseRejected or
                Switch2BluetoothRuntimeAbortFailure.LeaseReleaseTimedOut or
                Switch2BluetoothRuntimeAbortFailure.RuntimeAbortRejected or
                Switch2BluetoothRuntimeAbortFailure.DependencyThrew or
                Switch2BluetoothRuntimeAbortFailure.QuarantineRequired ||
                owner.State is Switch2BluetoothRuntimeOwnerState.
                    AbortedUnpublished or
                    Switch2BluetoothRuntimeOwnerState.Quarantined);
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastAbortFailure = failure;
            if (exactProof || credentialPossiblyConsumed)
            {
                prepareOwned = false;
            }
            if (exactProof)
            {
                unpublishedAborted = true;
            }
        }
        if (exactProof)
        {
            return Switch2RuntimeRegistrationParticipantResult.Success(
                operation);
        }
        if (threw || aborted || failure ==
                Switch2BluetoothRuntimeAbortFailure.None ||
            !IsDefined(failure) || failure ==
                Switch2BluetoothRuntimeAbortFailure.DependencyThrew)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            Map(failure), quarantineReason: AbortQuarantineReason(failure));
    }

    private bool HasExactAbortProof() =>
        owner.State == Switch2BluetoothRuntimeOwnerState.AbortedUnpublished &&
        owner.LeaseReleaseProven && owner.DrainPump.State ==
            Switch2BluetoothInputDrainPumpState.Stopped &&
        owner.RuntimeDevice.RuntimeState ==
            Switch2RuntimeInputDeviceState.AbortedUnpublished;

    private bool HasExactStopProof() =>
        owner.State == Switch2BluetoothRuntimeOwnerState.Stopped &&
        owner.LeaseReleaseProven && owner.DrainPump.State ==
            Switch2BluetoothInputDrainPumpState.Stopped &&
        owner.Sink.TerminalState ==
            Switch2BluetoothRuntimeTerminalState.Delivered &&
        owner.RuntimeDevice.TerminalNeutralCompleted &&
        owner.RuntimeDevice.TerminalNeutralReported;

    private void HandleOwnerLifecycleAttention(object sender,
        Switch2BluetoothRuntimeLifecycleAttentionEventArgs evidence)
    {
        if (!ReferenceEquals(sender, owner) || evidence == null ||
            evidence.Model != owner.Model || evidence.DeviceGeneration !=
                registration.Generation)
        {
            return;
        }

        Switch2RuntimeRegistrationLifecycleAttentionCallback callback;
        ulong exactTransportGeneration;
        lock (gate)
        {
            if ((!attentionSubscribed && !subscriptionOperationInProgress) ||
                !adoptionOwned)
            {
                return;
            }
            callback = callbacks.AttentionHandler;
            exactTransportGeneration = adoptionCredential.
                TransportGeneration;
        }
        if (callback == null || evidence.TransportGeneration !=
                exactTransportGeneration)
        {
            return;
        }

        Switch2RuntimeRegistrationLifecycleAttentionKind kind = evidence.
                UserDisconnectRequested ?
            Switch2RuntimeRegistrationLifecycleAttentionKind.
                UserDisconnectRequested : evidence.EndReason switch
            {
                Switch2BluetoothInputEndReason.Disconnected =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        TransportEnded,
                Switch2BluetoothInputEndReason.QueueOverflow =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        ProducerFailed,
                Switch2BluetoothInputEndReason.SinkFailure =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        SubscriberRejected,
                _ when evidence.PumpFailure ==
                        Switch2BluetoothInputDrainPumpFailure.SinkRejected =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        SubscriberRejected,
                _ when evidence.PumpFailure !=
                        Switch2BluetoothInputDrainPumpFailure.None =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        ProducerFailed,
                _ => Switch2RuntimeRegistrationLifecycleAttentionKind.Invalid,
            };
        if (kind == Switch2RuntimeRegistrationLifecycleAttentionKind.Invalid)
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
            // Attention wakes service teardown. Observer failure is not proof
            // that the physical producer or table slot has quiesced.
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
                operations.RemoveReport(exactCallbacks.ReportHandler);
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
                operations.RemoveAttention(ownerAttentionHandler);
            }
            catch
            {
                succeeded = false;
            }
        }
        return succeeded;
    }

    private static Switch2RuntimeRegistrationParticipantResult NormalizeStop(
        InputControllerOwnerOperationFailure ownerFailure,
        in Switch2BluetoothRuntimeStopFailure stopFailure)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce;
        if (ownerFailure is
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed
                or InputControllerOwnerOperationFailure.InvalidRegistration ||
            stopFailure.Kind ==
                Switch2BluetoothRuntimeStopFailureKind.InvalidOwner)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost, ownerFailure,
                InputControllerSlotQuarantineReason.OwnerAuthenticationLost);
        }
        if (ownerFailure == InputControllerOwnerOperationFailure.OwnerThrew ||
            stopFailure.Kind ==
                Switch2BluetoothRuntimeStopFailureKind.DependencyThrew)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew,
                InputControllerOwnerOperationFailure.OwnerThrew,
                InputControllerSlotQuarantineReason.OwnerThrew);
        }
        if (stopFailure.Kind is
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalPublicationTimedOut or
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalPublicationRejected or
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalDeliveryRejected or
                Switch2BluetoothRuntimeStopFailureKind.TerminalNotRequested)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    TerminalNeutralRejected, ownerFailure,
                InputControllerSlotQuarantineReason.
                    TerminalNeutralNotObserved);
        }
        if (stopFailure.Kind is
                Switch2BluetoothRuntimeStopFailureKind.PumpTimedOut or
                Switch2BluetoothRuntimeStopFailureKind.
                    LeaseReleaseTimedOut)
        {
            return Switch2RuntimeRegistrationParticipantResult.Reject(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.StopRejected,
                ownerFailure,
                InputControllerSlotQuarantineReason.StopTimedOut);
        }

        Switch2RuntimeRegistrationParticipantFailureKind kind =
            stopFailure.Kind ==
                    Switch2BluetoothRuntimeStopFailureKind.QuarantineRequired ?
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired :
                Switch2RuntimeRegistrationParticipantFailureKind.StopRejected;
        return Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            kind, ownerFailure,
            InputControllerSlotQuarantineReason.StopRejected);
    }

    private static Switch2RuntimeRegistrationParticipantFailureKind Map(
        Switch2BluetoothRuntimeSlotAdoptionFailure failure) => failure switch
        {
            Switch2BluetoothRuntimeSlotAdoptionFailure.InvalidToken =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential,
            Switch2BluetoothRuntimeSlotAdoptionFailure.InvalidState =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidState,
            Switch2BluetoothRuntimeSlotAdoptionFailure.
                    OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2BluetoothRuntimeSlotAdoptionFailure.
                    DifferentSlotAlreadyAdopted =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    StaleCredential,
            Switch2BluetoothRuntimeSlotAdoptionFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                DependencyThrew,
        };

    private static Switch2RuntimeRegistrationParticipantFailureKind Map(
        Switch2BluetoothRuntimePrepareFailure failure) => failure switch
        {
            Switch2BluetoothRuntimePrepareFailure.InvalidRegistration or
                Switch2BluetoothRuntimePrepareFailure.
                    InvalidSlotAdoptionCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential,
            Switch2BluetoothRuntimePrepareFailure.InvalidTimeout =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout,
            Switch2BluetoothRuntimePrepareFailure.InvalidState =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidState,
            Switch2BluetoothRuntimePrepareFailure.OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2BluetoothRuntimePrepareFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                PrepareRejected,
        };

    private static Switch2RuntimeRegistrationParticipantFailureKind Map(
        Switch2BluetoothRuntimeCommitFailure failure) => failure switch
        {
            Switch2BluetoothRuntimeCommitFailure.InvalidCredential or
                Switch2BluetoothRuntimeCommitFailure.
                    InvalidActivationCommitCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential,
            Switch2BluetoothRuntimeCommitFailure.StaleCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    StaleCredential,
            Switch2BluetoothRuntimeCommitFailure.AlreadyConsumed =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    AlreadyConsumed,
            Switch2BluetoothRuntimeCommitFailure.InvalidState =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidState,
            Switch2BluetoothRuntimeCommitFailure.OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2BluetoothRuntimeCommitFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                CommitRejected,
        };

    private static Switch2RuntimeRegistrationParticipantFailureKind Map(
        Switch2BluetoothRuntimeAbortFailure failure) => failure switch
        {
            Switch2BluetoothRuntimeAbortFailure.InvalidRegistration or
                Switch2BluetoothRuntimeAbortFailure.InvalidCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential,
            Switch2BluetoothRuntimeAbortFailure.StaleCredential =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    StaleCredential,
            Switch2BluetoothRuntimeAbortFailure.AlreadyConsumed =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    AlreadyConsumed,
            Switch2BluetoothRuntimeAbortFailure.InvalidTimeout =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout,
            Switch2BluetoothRuntimeAbortFailure.InvalidState =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidState,
            Switch2BluetoothRuntimeAbortFailure.OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2BluetoothRuntimeAbortFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                AbortRejected,
        };

    private static Switch2RuntimeRegistrationParticipantFailureKind Map(
        Switch2BluetoothRuntimeRetirementArmFailure failure) => failure switch
        {
            Switch2BluetoothRuntimeRetirementArmFailure.InvalidClaim =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential,
            Switch2BluetoothRuntimeRetirementArmFailure.StaleClaim =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    StaleCredential,
            Switch2BluetoothRuntimeRetirementArmFailure.InvalidState =>
                Switch2RuntimeRegistrationParticipantFailureKind.InvalidState,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                RetirementArmRejected,
        };

    private static InputControllerSlotQuarantineReason
        PrepareQuarantineReason(Switch2BluetoothRuntimePrepareFailure failure)
        => failure is Switch2BluetoothRuntimePrepareFailure.CleanupRejected or
                Switch2BluetoothRuntimePrepareFailure.QuarantineRequired ?
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure :
            InputControllerSlotQuarantineReason.None;

    private static InputControllerSlotQuarantineReason
        CommitQuarantineReason(Switch2BluetoothRuntimeCommitFailure failure) =>
        InputControllerSlotQuarantineReason.ExternalLifecycleFailure;

    private static InputControllerSlotQuarantineReason
        AbortQuarantineReason(Switch2BluetoothRuntimeAbortFailure failure) =>
        failure switch
        {
            Switch2BluetoothRuntimeAbortFailure.PumpTimedOut or
                Switch2BluetoothRuntimeAbortFailure.
                    LeaseReleaseTimedOut =>
                InputControllerSlotQuarantineReason.StopTimedOut,
            Switch2BluetoothRuntimeAbortFailure.InvalidCredential or
                Switch2BluetoothRuntimeAbortFailure.StaleCredential or
                Switch2BluetoothRuntimeAbortFailure.InvalidTimeout or
                Switch2BluetoothRuntimeAbortFailure.InvalidState or
                Switch2BluetoothRuntimeAbortFailure.
                    OperationAlreadyInProgress =>
                InputControllerSlotQuarantineReason.None,
            _ => InputControllerSlotQuarantineReason.StopRejected,
        };

    private static InputControllerSlotQuarantineReason
        RetirementQuarantineReason(
            Switch2BluetoothRuntimeRetirementArmFailure failure) =>
        InputControllerSlotQuarantineReason.ExternalLifecycleFailure;

    private static bool IsDefined(
        Switch2BluetoothRuntimeSlotAdoptionFailure value) => value is >=
            Switch2BluetoothRuntimeSlotAdoptionFailure.None and <=
            Switch2BluetoothRuntimeSlotAdoptionFailure.QuarantineRequired;

    private static bool IsDefined(
        Switch2BluetoothRuntimePrepareFailure value) => value is >=
            Switch2BluetoothRuntimePrepareFailure.None and <=
            Switch2BluetoothRuntimePrepareFailure.QuarantineRequired;

    private static bool IsDefined(
        Switch2BluetoothRuntimeCommitFailure value) => value is >=
            Switch2BluetoothRuntimeCommitFailure.None and <=
            Switch2BluetoothRuntimeCommitFailure.QuarantineRequired;

    private static bool IsDefined(
        Switch2BluetoothRuntimeAbortFailure value) => value is >=
            Switch2BluetoothRuntimeAbortFailure.None and <=
            Switch2BluetoothRuntimeAbortFailure.QuarantineRequired;

    private static bool IsDefined(
        Switch2BluetoothRuntimeRetirementArmFailure value) => value is >=
            Switch2BluetoothRuntimeRetirementArmFailure.None and <=
            Switch2BluetoothRuntimeRetirementArmFailure.
                DifferentClaimAlreadyArmed;

    private static bool IsDefined(
        Switch2BluetoothRuntimeStopFailureKind value) => value is >=
            Switch2BluetoothRuntimeStopFailureKind.None and <=
            Switch2BluetoothRuntimeStopFailureKind.QuarantineRequired;

    private static bool IsDefined(InputControllerOwnerOperationFailure value)
        => value is >= InputControllerOwnerOperationFailure.None and <=
            InputControllerOwnerOperationFailure.OwnerThrew;

    private static bool IsPositiveTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds > 0 && timeoutMilliseconds <=
            Switch2BluetoothRuntimeOwner.MaximumTimeoutMilliseconds;

    private static bool IsNonNegativeTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds >= 0 && timeoutMilliseconds <=
            Switch2BluetoothRuntimeOwner.MaximumTimeoutMilliseconds;
}
