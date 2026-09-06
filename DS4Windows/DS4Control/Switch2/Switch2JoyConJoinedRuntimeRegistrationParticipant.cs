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

internal interface ISwitch2JoyConJoinedRuntimeParticipantSubscriptions
{
    void AddReport(Switch2JoyConJoinedRuntimeOwner owner,
        DS4Device.ReportHandler<EventArgs> handler);

    void RemoveReport(Switch2JoyConJoinedRuntimeOwner owner,
        DS4Device.ReportHandler<EventArgs> handler);

    void AddAttention(Switch2JoyConJoinedRuntimeOwner owner,
        EventHandler<Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
            handler);

    void RemoveAttention(Switch2JoyConJoinedRuntimeOwner owner,
        EventHandler<Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
            handler);
}

internal sealed class Switch2JoyConJoinedRuntimeParticipantSubscriptions :
    ISwitch2JoyConJoinedRuntimeParticipantSubscriptions
{
    internal static readonly
        Switch2JoyConJoinedRuntimeParticipantSubscriptions Instance = new();

    private Switch2JoyConJoinedRuntimeParticipantSubscriptions()
    {
    }

    public void AddReport(Switch2JoyConJoinedRuntimeOwner owner,
        DS4Device.ReportHandler<EventArgs> handler) =>
        owner.RuntimeDevice.Report += handler;

    public void RemoveReport(Switch2JoyConJoinedRuntimeOwner owner,
        DS4Device.ReportHandler<EventArgs> handler) =>
        owner.RuntimeDevice.Report -= handler;

    public void AddAttention(Switch2JoyConJoinedRuntimeOwner owner,
        EventHandler<Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
            handler) => owner.LifecycleAttention += handler;

    public void RemoveAttention(Switch2JoyConJoinedRuntimeOwner owner,
        EventHandler<Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
            handler) => owner.LifecycleAttention -= handler;
}

/// <summary>
/// Dormant table participant for one exact joined Joy-Con runtime owner. The
/// adapter retains the native pair credentials and exact callbacks, but owns
/// no second mapping, transport, association, or registration lifecycle.
/// Every owner, runtime, and event-accessor call runs without <see cref="gate"/>
/// held.
/// </summary>
internal sealed class Switch2JoyConJoinedRuntimeRegistrationParticipant :
    ISwitch2RuntimeRegistrationParticipant
{
    private readonly object gate = new();
    private readonly Switch2JoyConJoinedRuntimeOwner owner;
    private readonly ISwitch2JoyConJoinedRuntimeParticipantSubscriptions
        subscriptions;
    private readonly InputControllerRegistration registration;
    private readonly ulong pairEpoch;
    private readonly ulong leftDeviceGeneration;
    private readonly ulong leftTransportGeneration;
    private readonly ulong rightDeviceGeneration;
    private readonly ulong rightTransportGeneration;
    private readonly EventHandler<
        Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
        ownerAttentionHandler;

    private InputControllerSlotToken boundToken;
    private Switch2JoyConJoinedRuntimeSlotAdoptionCredential
        adoptionCredential;
    private Switch2JoyConJoinedRuntimePrepareCredential prepareCredential;
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
    private Switch2BluetoothRuntimeRetirementArmFailure lastArmFailure;

    internal Switch2JoyConJoinedRuntimeRegistrationParticipant(
        Switch2JoyConJoinedRuntimeOwner owner) : this(owner,
            Switch2JoyConJoinedRuntimeParticipantSubscriptions.Instance)
    {
    }

    internal Switch2JoyConJoinedRuntimeRegistrationParticipant(
        Switch2JoyConJoinedRuntimeOwner owner,
        ISwitch2JoyConJoinedRuntimeParticipantSubscriptions subscriptions)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.subscriptions = subscriptions ??
            throw new ArgumentNullException(nameof(subscriptions));
        registration = owner.Registration;
        pairEpoch = owner.PairEpoch;
        Switch2BluetoothInputOwner left = owner.LeftInputOwner;
        Switch2BluetoothInputOwner right = owner.RightInputOwner;
        if (left == null || right == null ||
            !owner.DependenciesComplete ||
            !ReferenceEquals(registration.Owner, owner) ||
            !ReferenceEquals(registration.Device, owner.RuntimeDevice) ||
            registration.Generation != owner.RuntimeGeneration ||
            registration.OwnershipKind !=
                InputControllerOwnershipKind.Switch2Runtime ||
            !registration.IsOwnerAuthenticated ||
            left.Descriptor.Identity.Model !=
                Switch2ControllerModel.JoyCon2Left ||
            right.Descriptor.Identity.Model !=
                Switch2ControllerModel.JoyCon2Right)
        {
            throw new ArgumentException(
                "The joined owner does not expose one exact pair registration.",
                nameof(owner));
        }
        leftDeviceGeneration = left.Descriptor.DeviceGeneration;
        leftTransportGeneration = left.Descriptor.TransportGeneration;
        rightDeviceGeneration = right.Descriptor.DeviceGeneration;
        rightTransportGeneration = right.Descriptor.TransportGeneration;
        if (!owner.RuntimeDevice.HasExactJoinedBluetoothBinding(pairEpoch,
                leftDeviceGeneration, leftTransportGeneration,
                rightDeviceGeneration, rightTransportGeneration))
        {
            throw new ArgumentException(
                "The joined owner physical binding is inconsistent.",
                nameof(owner));
        }
        ownerAttentionHandler = HandleOwnerLifecycleAttention;
    }

    public InputControllerRegistration Registration => registration;

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

    internal Switch2BluetoothRuntimeRetirementArmFailure LastArmFailure
    {
        get { lock (gate) { return lastArmFailure; } }
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
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential);
        }

        lock (gate)
        {
            if (adoptionOwned)
            {
                return token.Equals(boundToken) ? Success(operation) :
                    Reject(operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            StaleCredential);
            }
            if (lifecycleOperationInProgress || removed ||
                unpublishedAborted)
            {
                return Reject(operation, lifecycleOperationInProgress ?
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress :
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            lifecycleOperationInProgress = true;
        }

        bool adopted = false;
        bool threw = false;
        Switch2JoyConJoinedRuntimeSlotAdoptionCredential credential = default;
        Switch2BluetoothRuntimeSlotAdoptionFailure failure = default;
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
                Switch2BluetoothRuntimeSlotAdoptionFailure.None &&
            IsExact(credential, token);
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
        if (validSuccess) { return Success(operation); }
        if (threw || adopted || failure ==
                Switch2BluetoothRuntimeSlotAdoptionFailure.None)
        {
            return Uncertain(operation);
        }
        return Reject(operation, Map(failure),
            failure == Switch2BluetoothRuntimeSlotAdoptionFailure.
                    QuarantineRequired ?
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure : default);
    }

    public Switch2RuntimeRegistrationParticipantResult TrySubscribe(
        in Switch2RuntimeRegistrationCallbacks exactCallbacks)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.Subscribe;
        if (!exactCallbacks.IsValid)
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidArgument);
        }
        lock (gate)
        {
            if (!adoptionOwned || removed || unpublishedAborted)
            {
                return Reject(operation,
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
                        callbacks.IsExact(exactCallbacks) ? Success(operation) :
                    Reject(operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            InvalidCredential);
            }
            if (subscriptionOperationInProgress)
            {
                return Reject(operation,
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
            subscriptions.AddReport(owner, exactCallbacks.ReportHandler);
            reportAdded = true;
            subscriptions.AddAttention(owner, ownerAttentionHandler);
            attentionAdded = true;
        }
        catch
        {
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
        return Success(operation);
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
        Switch2JoyConJoinedRuntimeSlotAdoptionCredential exactAdoption;
        lock (gate)
        {
            if (!adoptionOwned || !reportSubscribed || !attentionSubscribed ||
                subscriptionUncertain || prepareOwned || activationCommitted ||
                unpublishedAborted || removed)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            lifecycleOperationInProgress = true;
            exactAdoption = adoptionCredential;
        }

        bool prepared = false;
        bool threw = false;
        Switch2JoyConJoinedRuntimePrepareCredential credential = default;
        Switch2BluetoothRuntimePrepareFailure failure = default;
        try
        {
            prepared = owner.TryPrepareActivation(exactAdoption,
                timeoutMilliseconds, out credential, out failure);
        }
        catch
        {
            threw = true;
        }
        bool validSuccess = prepared && failure ==
                Switch2BluetoothRuntimePrepareFailure.None &&
            IsExact(credential);
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
        if (validSuccess) { return Success(operation); }
        if (threw || prepared || failure ==
                Switch2BluetoothRuntimePrepareFailure.None)
        {
            return Uncertain(operation);
        }
        return Reject(operation, Map(failure),
            failure == Switch2BluetoothRuntimePrepareFailure.
                    QuarantineRequired ?
                InputControllerSlotQuarantineReason.StopRejected : default);
    }

    public Switch2RuntimeRegistrationParticipantResult TryCommitPrepared(
        in InputControllerActivationCommitCredential activationCommit)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared;
        Switch2JoyConJoinedRuntimePrepareCredential exactPrepare;
        InputControllerSlotToken exactToken;
        lock (gate)
        {
            if (!prepareOwned || activationCommitted || unpublishedAborted ||
                removed)
            {
                return Reject(operation,
                    activationCommitted || unpublishedAborted ?
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            AlreadyConsumed :
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Reject(operation,
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
            lock (gate) { lifecycleOperationInProgress = false; }
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential);
        }

        bool committed = false;
        bool threw = false;
        Switch2BluetoothRuntimeCommitFailure failure = default;
        try
        {
            committed = owner.TryCommitPrepared(exactPrepare,
                activationCommit, out failure);
        }
        catch
        {
            threw = true;
        }
        bool validSuccess = committed && failure ==
            Switch2BluetoothRuntimeCommitFailure.None;
        bool provenBeforeMutation = !committed && !threw && failure is
            Switch2BluetoothRuntimeCommitFailure.InvalidCredential or
            Switch2BluetoothRuntimeCommitFailure.
                InvalidActivationCommitCredential;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastCommitFailure = failure;
            if (validSuccess)
            {
                prepareOwned = false;
                activationCommitted = true;
            }
            else if (!provenBeforeMutation)
            {
                // The native pair commit consumes its credential before the
                // external pair operation. Never retry an uncertain attempt.
                prepareOwned = false;
            }
        }
        if (validSuccess) { return Success(operation); }
        if (provenBeforeMutation)
        {
            return Reject(operation, Map(failure));
        }
        if (threw || committed || failure is
                Switch2BluetoothRuntimeCommitFailure.None or
                Switch2BluetoothRuntimeCommitFailure.DependencyThrew)
        {
            return Uncertain(operation);
        }
        return Reject(operation, Map(failure),
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure);
    }

    public Switch2RuntimeRegistrationParticipantResult TryAbortPrepared(
        int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.AbortPrepared;
        if (!IsNonNegativeTimeout(timeoutMilliseconds))
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }
        Switch2JoyConJoinedRuntimePrepareCredential exactPrepare;
        lock (gate)
        {
            if (unpublishedAborted) { return Success(operation); }
            if (!prepareOwned || activationCommitted || removed)
            {
                return Reject(operation, activationCommitted ?
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        AlreadyConsumed :
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Reject(operation,
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
            aborted = owner.TryAbortPrepared(exactPrepare,
                timeoutMilliseconds, out failure);
        }
        catch
        {
            threw = true;
        }
        bool validSuccess = aborted && failure ==
            Switch2BluetoothRuntimeAbortFailure.None;
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
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }
        Switch2JoyConJoinedRuntimeSlotAdoptionCredential exactAdoption;
        lock (gate)
        {
            if (unpublishedAborted) { return Success(operation); }
            if (!adoptionOwned || prepareOwned || activationCommitted ||
                removed)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Reject(operation,
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
            aborted = owner.TryAbortUnpublished(exactAdoption,
                timeoutMilliseconds, out failure);
        }
        catch
        {
            threw = true;
        }
        bool validSuccess = aborted && failure ==
            Switch2BluetoothRuntimeAbortFailure.None;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastAbortFailure = failure;
            if (validSuccess) { unpublishedAborted = true; }
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
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidCredential);
        }
        lock (gate)
        {
            if (!activationCommitted || removed)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (!claim.Token.Equals(boundToken) ||
                !claim.Token.Registration.Equals(registration) ||
                !ReferenceEquals(claim.Token.Registration.Owner, owner))
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        StaleCredential);
            }
            if (retirementArmed)
            {
                return retirementClaim.Equals(claim) ? Success(operation) :
                    Reject(operation,
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            RetirementArmRejected);
            }
            if (lifecycleOperationInProgress)
            {
                return Reject(operation,
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
            armed = owner.TryArmRetirement(claim, out failure);
        }
        catch
        {
            threw = true;
        }
        bool validSuccess = armed && failure ==
            Switch2BluetoothRuntimeRetirementArmFailure.None;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastArmFailure = failure;
            if (validSuccess)
            {
                retirementClaim = claim;
                retirementArmed = true;
            }
        }
        if (validSuccess) { return Success(operation); }
        if (threw || armed || failure ==
                Switch2BluetoothRuntimeRetirementArmFailure.None)
        {
            return Uncertain(operation);
        }
        return Reject(operation, Map(failure),
            failure == Switch2BluetoothRuntimeRetirementArmFailure.
                    DifferentClaimAlreadyArmed ?
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure : default);
    }

    public Switch2RuntimeRegistrationParticipantResult
        TryWaitForPublicationAvailability(int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability;
        if (!IsNonNegativeTimeout(timeoutMilliseconds))
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout);
        }
        lock (gate)
        {
            if (!retirementArmed || removed)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
        }
        bool available;
        try
        {
            available = owner.RuntimeDevice.TryWaitForPublicationAvailability(
                timeoutMilliseconds);
        }
        catch
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew);
        }
        return available ? Success(operation) : Reject(operation,
            Switch2RuntimeRegistrationParticipantFailureKind.
                PublicationDrainTimedOut,
            InputControllerSlotQuarantineReason.DrainTimedOut);
    }

    public Switch2RuntimeRegistrationParticipantResult TryStopAndQuiesce(
        int timeoutMilliseconds)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce;
        if (!IsNonNegativeTimeout(timeoutMilliseconds))
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    InvalidTimeout,
                ownerFailure: InputControllerOwnerOperationFailure.
                    InvalidTimeout);
        }
        lock (gate)
        {
            if (stopped) { return Success(operation); }
            if (!activationCommitted || !retirementArmed || removed)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Reject(operation,
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
        Switch2JoyConJoinedRuntimeStopFailure stopFailure =
            owner.LastStopFailure;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            stopped = quiesced && ownerFailure ==
                InputControllerOwnerOperationFailure.None;
        }
        return stopped ? Success(operation) :
            NormalizeStop(ownerFailure, stopFailure);
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
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        OperationAlreadyInProgress);
            }
            removeReport = reportSubscribed;
            removeAttention = attentionSubscribed;
            if (!removeReport && !removeAttention) { return Success(operation); }
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
        return succeeded ? Success(operation) :
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
            if (removed) { return Success(operation); }
            if (!stopped || reportSubscribed || attentionSubscribed ||
                subscriptionUncertain)
            {
                return Reject(operation,
                    Switch2RuntimeRegistrationParticipantFailureKind.
                        InvalidState);
            }
            if (lifecycleOperationInProgress)
            {
                return Reject(operation,
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
        if (removed) { return Success(operation); }
        if (ownerFailure == InputControllerOwnerOperationFailure.OwnerThrew)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew,
                ownerFailure, InputControllerSlotQuarantineReason.OwnerThrew);
        }
        Switch2RuntimeRegistrationParticipantFailureKind kind = ownerFailure ==
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed ?
            Switch2RuntimeRegistrationParticipantFailureKind.
                OwnerAuthenticationLost :
            Switch2RuntimeRegistrationParticipantFailureKind.RemoveRejected;
        InputControllerSlotQuarantineReason reason = ownerFailure ==
                InputControllerOwnerOperationFailure.OwnerAuthenticationFailed ?
            InputControllerSlotQuarantineReason.OwnerAuthenticationLost :
            InputControllerSlotQuarantineReason.RemoveRejected;
        return Reject(operation, kind, reason, ownerFailure);
    }

    private void HandleOwnerLifecycleAttention(object sender,
        Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs evidence)
    {
        if (!ReferenceEquals(sender, owner) || evidence == null ||
            evidence.RuntimeGeneration != registration.Generation ||
            evidence.PairEpoch != pairEpoch ||
            (!evidence.UserDisconnectRequested && !IsExactPhysical(evidence)) ||
            (evidence.UserDisconnectRequested &&
                evidence.Side != Switch2StickSide.Invalid))
        {
            return;
        }
        Switch2RuntimeRegistrationLifecycleAttentionKind kind = evidence.
                UserDisconnectRequested ?
            Switch2RuntimeRegistrationLifecycleAttentionKind.
                UserDisconnectRequested : evidence.EndReason switch
            {
                Switch2BluetoothInputEndReason.Disconnected or
                    Switch2BluetoothInputEndReason.Stopped =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        TransportEnded,
                Switch2BluetoothInputEndReason.QueueOverflow =>
                    Switch2RuntimeRegistrationLifecycleAttentionKind.
                        InputRejected,
                Switch2BluetoothInputEndReason.SinkFailure =>
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
        Switch2RuntimeRegistrationLifecycleAttentionCallback callback;
        lock (gate)
        {
            if (!attentionSubscribed && !subscriptionOperationInProgress)
            {
                return;
            }
            callback = callbacks.AttentionHandler;
        }
        if (callback == null) { return; }
        var attention = new Switch2RuntimeRegistrationLifecycleAttention(
            registration, kind);
        try { callback(attention); }
        catch
        {
            // Attention is wake-up evidence, not teardown proof. An observer
            // cannot unwind the physical producer callback.
        }
    }

    private bool IsExactPhysical(
        Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs evidence) =>
        evidence.Side switch
        {
            Switch2StickSide.Left =>
                evidence.DeviceGeneration == leftDeviceGeneration &&
                evidence.TransportGeneration == leftTransportGeneration,
            Switch2StickSide.Right =>
                evidence.DeviceGeneration == rightDeviceGeneration &&
                evidence.TransportGeneration == rightTransportGeneration,
            _ => false,
        };

    private bool TryRemoveSubscriptions(
        in Switch2RuntimeRegistrationCallbacks exactCallbacks,
        bool removeReport, bool removeAttention)
    {
        bool succeeded = true;
        if (removeReport)
        {
            try
            {
                subscriptions.RemoveReport(owner,
                    exactCallbacks.ReportHandler);
            }
            catch { succeeded = false; }
        }
        if (removeAttention)
        {
            try
            {
                subscriptions.RemoveAttention(owner, ownerAttentionHandler);
            }
            catch { succeeded = false; }
        }
        return succeeded;
    }

    private bool IsExact(
        in Switch2JoyConJoinedRuntimeSlotAdoptionCredential credential,
        in InputControllerSlotToken token) => credential.IsValid &&
        credential.SlotToken.Equals(token) &&
        credential.RuntimeGeneration == registration.Generation &&
        credential.PairEpoch == pairEpoch && credential.PairId == owner.PairId &&
        credential.PairRecordRevision == owner.PairRecordRevision &&
        credential.ScanGeneration == owner.ScanGeneration &&
        credential.LeftDeviceGeneration == leftDeviceGeneration &&
        credential.LeftTransportGeneration == leftTransportGeneration &&
        credential.RightDeviceGeneration == rightDeviceGeneration &&
        credential.RightTransportGeneration == rightTransportGeneration;

    private bool IsExact(
        in Switch2JoyConJoinedRuntimePrepareCredential credential) =>
        credential.IsValid &&
        credential.RuntimeGeneration == registration.Generation &&
        credential.PairEpoch == pairEpoch && credential.PairId == owner.PairId &&
        credential.PairRecordRevision == owner.PairRecordRevision &&
        credential.ScanGeneration == owner.ScanGeneration &&
        credential.LeftDeviceGeneration == leftDeviceGeneration &&
        credential.LeftTransportGeneration == leftTransportGeneration &&
        credential.RightDeviceGeneration == rightDeviceGeneration &&
        credential.RightTransportGeneration == rightTransportGeneration;

    private static Switch2RuntimeRegistrationParticipantResult NormalizeAbort(
        Switch2RuntimeRegistrationParticipantOperation operation,
        bool aborted, bool threw,
        Switch2BluetoothRuntimeAbortFailure failure, bool validSuccess)
    {
        if (validSuccess) { return Success(operation); }
        if (threw || aborted || failure is
                Switch2BluetoothRuntimeAbortFailure.None or
                Switch2BluetoothRuntimeAbortFailure.DependencyThrew)
        {
            return Uncertain(operation);
        }
        return Reject(operation, Map(failure),
            InputControllerSlotQuarantineReason.StopRejected);
    }

    private static Switch2RuntimeRegistrationParticipantResult NormalizeStop(
        InputControllerOwnerOperationFailure ownerFailure,
        in Switch2JoyConJoinedRuntimeStopFailure stopFailure)
    {
        const Switch2RuntimeRegistrationParticipantOperation operation =
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce;
        if (ownerFailure ==
            InputControllerOwnerOperationFailure.OwnerAuthenticationFailed)
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OwnerAuthenticationLost,
                InputControllerSlotQuarantineReason.OwnerAuthenticationLost,
                ownerFailure);
        }
        if (ownerFailure == InputControllerOwnerOperationFailure.OwnerThrew)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew,
                ownerFailure, InputControllerSlotQuarantineReason.OwnerThrew);
        }
        if (stopFailure.Kind ==
            Switch2BluetoothRuntimeStopFailureKind.DependencyThrew)
        {
            return Switch2RuntimeRegistrationParticipantResult.Uncertain(
                operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew,
                ownerFailure, InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure);
        }
        if (stopFailure.Kind is
                Switch2BluetoothRuntimeStopFailureKind.TerminalNotRequested or
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalPublicationTimedOut or
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalPublicationRejected or
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalDeliveryRejected)
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.
                    TerminalNeutralRejected,
                InputControllerSlotQuarantineReason.
                    TerminalNeutralNotObserved,
                ownerFailure);
        }
        if (stopFailure.Kind is
                Switch2BluetoothRuntimeStopFailureKind.PumpTimedOut or
                Switch2BluetoothRuntimeStopFailureKind.
                    LeaseReleaseTimedOut or
                Switch2BluetoothRuntimeStopFailureKind.
                    OperationAlreadyInProgress)
        {
            return Reject(operation,
                Switch2RuntimeRegistrationParticipantFailureKind.StopRejected,
                InputControllerSlotQuarantineReason.StopTimedOut,
                ownerFailure);
        }
        Switch2RuntimeRegistrationParticipantFailureKind kind =
            stopFailure.Kind ==
                    Switch2BluetoothRuntimeStopFailureKind.QuarantineRequired ?
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired :
                Switch2RuntimeRegistrationParticipantFailureKind.StopRejected;
        return Reject(operation, kind,
            InputControllerSlotQuarantineReason.StopRejected, ownerFailure);
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
            Switch2BluetoothRuntimePrepareFailure.
                    OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2BluetoothRuntimePrepareFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            Switch2BluetoothRuntimePrepareFailure.RuntimeStartRejected or
                Switch2BluetoothRuntimePrepareFailure.PumpStartRejected or
                Switch2BluetoothRuntimePrepareFailure.CleanupRejected =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    PrepareRejected,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                DependencyThrew,
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
            Switch2BluetoothRuntimeCommitFailure.
                    OperationAlreadyInProgress =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    OperationAlreadyInProgress,
            Switch2BluetoothRuntimeCommitFailure.QuarantineRequired =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired,
            Switch2BluetoothRuntimeCommitFailure.InputCommitRejected =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    CommitRejected,
            _ => Switch2RuntimeRegistrationParticipantFailureKind.
                DependencyThrew,
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
            Switch2BluetoothRuntimeAbortFailure.DependencyThrew =>
                Switch2RuntimeRegistrationParticipantFailureKind.
                    DependencyThrew,
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

    private static Switch2RuntimeRegistrationParticipantResult Success(
        Switch2RuntimeRegistrationParticipantOperation operation) =>
        Switch2RuntimeRegistrationParticipantResult.Success(operation);

    private static Switch2RuntimeRegistrationParticipantResult Reject(
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantFailureKind failure,
        InputControllerSlotQuarantineReason quarantineReason = default,
        InputControllerOwnerOperationFailure ownerFailure = default) =>
        Switch2RuntimeRegistrationParticipantResult.Reject(operation,
            failure, ownerFailure, quarantineReason);

    private static Switch2RuntimeRegistrationParticipantResult Uncertain(
        Switch2RuntimeRegistrationParticipantOperation operation) =>
        Switch2RuntimeRegistrationParticipantResult.Uncertain(operation,
            Switch2RuntimeRegistrationParticipantFailureKind.DependencyThrew);

    private static bool IsPositiveTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds > 0 && timeoutMilliseconds <=
            Switch2JoyConJoinedRuntimeOwner.MaximumTimeoutMilliseconds;

    private static bool IsNonNegativeTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds >= 0 && timeoutMilliseconds <=
            Switch2JoyConJoinedRuntimeOwner.MaximumTimeoutMilliseconds;
}
