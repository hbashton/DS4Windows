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
using System.Threading;

namespace DS4Windows.Switch2;

internal enum Switch2RuntimeRegistrationTransactionFailureKind : byte
{
    None = 0,
    InvalidArgument,
    InvalidTimeout,
    TableRejected,
    StaleToken,
    SubscriptionRejected,
    PrepareRejected,
    UnpublishedAbortRejected,
    CommitRejected,
    ReentrantRemoval,
    CallbackActive,
    DrainTimedOut,
    StopRejected,
    TerminalNeutralRejected,
    RemoveRejected,
    DependencyThrew,
    QuarantineRequired,
    SlotAdoptionRejected,
}

internal readonly struct Switch2RuntimeRegistrationTransactionFailure
{
    internal Switch2RuntimeRegistrationTransactionFailure(
        Switch2RuntimeRegistrationTransactionFailureKind kind,
        InputControllerSlotTableFailure tableFailure = default,
        ISwitch2RuntimeRegistrationParticipant participant = null,
        Switch2RuntimeRegistrationParticipantResult participantResult =
            default,
        InputControllerOwnerOperationFailure ownerFailure = default,
        InputControllerSlotQuarantineReason quarantineReason = default,
        Switch2RuntimeRegistrationParticipantResult
            originalParticipantResult = default)
    {
        Kind = kind;
        TableFailure = tableFailure;
        Participant = participant;
        ParticipantResult = participantResult;
        OwnerFailure = ownerFailure;
        QuarantineReason = quarantineReason;
        OriginalParticipantResult = originalParticipantResult;
    }

    internal Switch2RuntimeRegistrationTransactionFailureKind Kind { get; }

    internal InputControllerSlotTableFailure TableFailure { get; }

    internal ISwitch2RuntimeRegistrationParticipant Participant { get; }

    internal Switch2RuntimeRegistrationParticipantResult ParticipantResult
    {
        get;
    }

    internal InputControllerOwnerOperationFailure OwnerFailure { get; }

    internal InputControllerSlotQuarantineReason QuarantineReason { get; }

    /// <summary>
    /// The participant result that triggered attach cleanup when a later abort,
    /// unsubscribe, or table rollback also failed. ParticipantResult remains
    /// the cleanup failure so ownership policy stays unchanged; this field is
    /// diagnostic provenance only.
    /// </summary>
    internal Switch2RuntimeRegistrationParticipantResult
        OriginalParticipantResult { get; }

    internal bool RequiresQuarantine => Kind ==
            Switch2RuntimeRegistrationTransactionFailureKind.
                QuarantineRequired ||
        QuarantineReason != InputControllerSlotQuarantineReason.None;
}

/// <summary>
/// Dormant transport-neutral transaction boundary for Switch 2 runtime
/// participants. It has no ControlService, discovery, hardware, output,
/// profile, or virtual-device call site. One narrow lifecycle gate serializes
/// binding visibility and activation/close intent. Fallible participant,
/// mapping, and teardown calls always run outside that gate.
/// </summary>
internal sealed class Switch2RuntimeRegistrationTransactionCore
{
    private enum RemovalOwnershipResult : byte
    {
        Acquired = 0,
        AlreadyOwned,
        CallbackActive,
        ReentrantCallback,
    }

    /// <summary>
    /// Exact retained identity and result of a service close after the table's
    /// admission boundary has irrevocably closed. All mutable fields are
    /// protected by <see cref="lifecycleGate"/>. The immutable snapshots and
    /// bindings let a later same-generation observer resume or join teardown
    /// without manufacturing claims from a newer table lifetime.
    /// </summary>
    private sealed class CloseEpoch
    {
        internal CloseEpoch(ulong serviceGeneration,
            InputControllerSlotSnapshot[] snapshots, Binding[] bindings)
        {
            ServiceGeneration = serviceGeneration;
            Snapshots = snapshots;
            Bindings = bindings;
        }

        internal ulong ServiceGeneration { get; }

        internal InputControllerSlotSnapshot[] Snapshots { get; }

        internal Binding[] Bindings { get; }

        internal bool TeardownOwned { get; set; }

        internal bool Completed { get; set; }

        internal bool Succeeded { get; set; }

        internal Switch2RuntimeRegistrationTransactionFailure Failure { get; set; }
    }

    private readonly object lifecycleGate = new();
    private readonly InputControllerRegistrationTable table;
    private readonly ControlServiceInputSlotAdmission slotAdmission;
    private readonly Binding[] bindings;
    private readonly int lifecycleAttentionTimeoutMilliseconds;

    private bool open;
    private bool serviceClosePending;
    private int pendingSetupPublishCount;
    private int pendingActivationCount;
    private ulong serviceGeneration;
    private CloseEpoch closeEpoch;
    private InputControllerSlotSnapshot[] externallyClosedSnapshots;

    internal Switch2RuntimeRegistrationTransactionCore(
        InputControllerRegistrationTable table,
        int lifecycleAttentionTimeoutMilliseconds = 5_000,
        ControlServiceInputSlotAdmission slotAdmission = null)
    {
        this.table = table ?? throw new ArgumentNullException(nameof(table));
        if (slotAdmission != null && !ReferenceEquals(slotAdmission.Table, table))
            throw new ArgumentException("Slot admission must use the same registration table.", nameof(slotAdmission));
        this.slotAdmission = slotAdmission;
        if (lifecycleAttentionTimeoutMilliseconds <= 0 ||
            lifecycleAttentionTimeoutMilliseconds >
                InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifecycleAttentionTimeoutMilliseconds));
        }

        this.lifecycleAttentionTimeoutMilliseconds =
            lifecycleAttentionTimeoutMilliseconds;
        bindings = new Binding[table.SlotCount];
    }

    internal InputControllerRegistrationTable Table => table;

    internal object LifecycleGate => lifecycleGate;

    // Informational only: emitted after exact terminal removal, never from
    // input callbacks or while holding the core/table gates.
    internal event Action<InputControllerSlotToken> RuntimeRemoved;

    internal bool TryOpen(ulong exactServiceGeneration,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        lock (lifecycleGate)
        {
            if (closeEpoch != null && !closeEpoch.Completed)
            {
                failure = Fail(
                    Switch2RuntimeRegistrationTransactionFailureKind.TableRejected,
                    InputControllerSlotTableFailure.Busy);
                return false;
            }
            if (!table.TryOpen(exactServiceGeneration,
                    out InputControllerSlotTableFailure tableFailure))
            {
                failure = Fail(
                    Switch2RuntimeRegistrationTransactionFailureKind.TableRejected,
                    tableFailure);
                return false;
            }

            closeEpoch = null;
            open = true;
            serviceClosePending = false;
            serviceGeneration = exactServiceGeneration;
            failure = default;
            return true;
        }
    }

    /// <summary>
    /// Adopts the exact table lifetime already opened by the ControlService
    /// owner. It never calls <see cref="InputControllerRegistrationTable.TryOpen"/>
    /// and therefore cannot create a competing service generation.
    /// </summary>
    internal bool TryAdoptOpen(ulong exactServiceGeneration,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        lock (lifecycleGate)
        {
            if (exactServiceGeneration == 0 || open ||
                externallyClosedSnapshots != null ||
                closeEpoch != null && !closeEpoch.Completed)
            {
                failure = Fail(
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        TableRejected,
                    InputControllerSlotTableFailure.AlreadyOpen);
                return false;
            }
            if (!table.IsOpenForServiceGeneration(exactServiceGeneration))
            {
                failure = Fail(
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        TableRejected,
                    InputControllerSlotTableFailure.Closed);
                return false;
            }

            closeEpoch = null;
            open = true;
            serviceClosePending = false;
            serviceGeneration = exactServiceGeneration;
            failure = default;
            return true;
        }
    }

    /// <summary>
    /// Supplies the immutable snapshots from the one ControlService-owned
    /// table close. The next <see cref="TryClose"/> consumes this exact image
    /// instead of attempting a second table close.
    /// </summary>
    internal bool TryObserveExternalTableClose(
        ulong exactServiceGeneration,
        InputControllerSlotSnapshot[] snapshots,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        if (snapshots == null)
        {
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.
                    InvalidArgument);
            return false;
        }

        lock (lifecycleGate)
        {
            if (!open || serviceGeneration != exactServiceGeneration ||
                table.IsOpen || closeEpoch != null ||
                externallyClosedSnapshots != null)
            {
                failure = Fail(
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        TableRejected,
                    InputControllerSlotTableFailure.Closed);
                return false;
            }

            var seenSlots = new bool[table.SlotCount];
            foreach (InputControllerSlotSnapshot snapshot in snapshots)
            {
                bool requiresExactToken = snapshot.State is
                    InputControllerSlotState.Bound or
                    InputControllerSlotState.Retiring or
                    InputControllerSlotState.Quiesced;
                if (snapshot.Slot < 0 || snapshot.Slot >= seenSlots.Length ||
                    seenSlots[snapshot.Slot] ||
                    requiresExactToken &&
                    (snapshot.ServiceGeneration != exactServiceGeneration ||
                     snapshot.Token.ServiceGeneration !=
                         exactServiceGeneration ||
                     snapshot.Token.Slot != snapshot.Slot) ||
                    snapshot.Token.IsValid &&
                    snapshot.Token.Slot != snapshot.Slot)
                {
                    failure = Fail(
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            StaleToken,
                        InputControllerSlotTableFailure.StaleCredential);
                    return false;
                }
                seenSlots[snapshot.Slot] = true;
            }

            externallyClosedSnapshots = snapshots.Length == 0 ?
                Array.Empty<InputControllerSlotSnapshot>() :
                (InputControllerSlotSnapshot[])snapshots.Clone();
            failure = default;
            return true;
        }
    }

    internal bool TryAttach(InputControllerRegistration registration,
        Func<ISwitch2RuntimeRegistrationParticipant> participantFactory,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        return TryAttachCore(exactSlot: -1, registration,
            participantFactory, mappingCallback, timeoutMilliseconds,
            out token, out failure);
    }

    /// <summary>
    /// Attaches only to the table slot whose vacancy the external slot owner
    /// has already proven under its own gate. No fallback slot is selected.
    /// Configured slot admission atomically checks external vacancy and binds
    /// the table before releasing its short-lived gate. Without that admission
    /// bridge, the caller must retain its external gate across setup when
    /// atomicity with a separate slot array is required.
    /// </summary>
    internal bool TryAttachExactSlot(int exactSlot,
        InputControllerRegistration registration,
        Func<ISwitch2RuntimeRegistrationParticipant> participantFactory,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (exactSlot < 0 || exactSlot >= table.SlotCount)
        {
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.
                    InvalidArgument,
                InputControllerSlotTableFailure.InvalidArgument);
            return false;
        }

        return TryAttachCore(exactSlot, registration, participantFactory,
            mappingCallback, timeoutMilliseconds, out token, out failure);
    }

    private bool TryAttachCore(int exactSlot,
        InputControllerRegistration registration,
        Func<ISwitch2RuntimeRegistrationParticipant> participantFactory,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        token = default;
        if (participantFactory == null || mappingCallback == null ||
            registration.Device == null || registration.Owner == null ||
            registration.Generation == 0)
        {
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.InvalidArgument);
            return false;
        }
        if (!IsPositiveTimeout(timeoutMilliseconds))
        {
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.InvalidTimeout);
            return false;
        }

        long deadline = CreateDeadline(timeoutMilliseconds);
        Binding binding = null;
        bool boundAdmissionOwned = false;
        bool slotAdoptionOwned = false;
        bool boundAdmissionCleaned = false;
        bool bindingConstructionThrew = false;
        bool slotAdoptionThrew = false;
        bool slotAdoptionResultInvalid = false;
        bool setupPublishAdmissionOwned = false;
        ulong admittedServiceGeneration = 0;
        InputControllerSetupRollbackClaim rollbackClaim = default;
        ISwitch2RuntimeRegistrationParticipant participant = null;
        Switch2RuntimeRegistrationParticipantResult slotAdoptionResult =
            default;
        Switch2RuntimeRegistrationParticipantResult earlyAbortResult =
            default;
        InputControllerSlotTableFailure earlyRollbackFailure = default;
        InputControllerSlotTableFailure tableFailure = default;
        lock (lifecycleGate)
        {
            if (!open || serviceClosePending)
            {
                tableFailure = InputControllerSlotTableFailure.Closed;
            }
            else
            {
                setupPublishAdmissionOwned = true;
                admittedServiceGeneration = serviceGeneration;
                pendingSetupPublishCount++;
            }
        }
        if (setupPublishAdmissionOwned)
        {
            Binding candidate = null;
            try
            {
                bool tableBound = slotAdmission != null ?
                    slotAdmission.TryReserveAndBind(exactSlot, registration,
                        out token, out rollbackClaim, out tableFailure) :
                    exactSlot >= 0 ?
                    table.TryReserveAndBindExactSlot(exactSlot,
                        registration, out token, out rollbackClaim,
                        out tableFailure) :
                    table.TryReserveAndBind(registration, out token,
                        out rollbackClaim, out tableFailure);
                if (tableBound)
                {
                    boundAdmissionOwned = true;
                    try
                    {
                        participant = participantFactory();
                        if (participant == null ||
                            !participant.Registration.Equals(registration))
                        {
                            bindingConstructionThrew = true;
                        }
                        else
                        {
                            slotAdoptionResult =
                                participant.TryAdoptBoundSlot(token);
                            slotAdoptionResultInvalid =
                                !IsExpectedParticipantResult(
                                    slotAdoptionResult,
                                    Switch2RuntimeRegistrationParticipantOperation.
                                        AdoptBoundSlot);
                            slotAdoptionOwned = !slotAdoptionResultInvalid &&
                                slotAdoptionResult.Succeeded;
                        }
                    }
                    catch
                    {
                        slotAdoptionThrew = true;
                    }
                    if (slotAdoptionOwned)
                    {
                        try
                        {
                            candidate = new Binding(this, participant, token,
                                rollbackClaim, mappingCallback);
                        }
                        catch
                        {
                            bindingConstructionThrew = true;
                        }
                    }
                }

                lock (lifecycleGate)
                {
                    // A close that linearizes after setup admission waits for
                    // either this exact binding publication or exact local
                    // rollback. No Bound slot is exposed without a retained
                    // cleanup owner.
                    if (candidate != null &&
                        admittedServiceGeneration == token.ServiceGeneration &&
                        serviceGeneration == admittedServiceGeneration &&
                        bindings[token.Slot] == null)
                    {
                        binding = candidate;
                        bindings[token.Slot] = binding;
                    }
                }

                if (boundAdmissionOwned && binding == null)
                {
                    bool ownerClean = !slotAdoptionOwned &&
                        !slotAdoptionThrew && !slotAdoptionResultInvalid &&
                        !bindingConstructionThrew &&
                        !slotAdoptionResult.RequiresQuarantine;
                    if (slotAdoptionOwned)
                    {
                        try
                        {
                            earlyAbortResult =
                                participant.TryAbortUnpublished(
                                    RemainingMilliseconds(deadline,
                                        timeoutMilliseconds));
                            ownerClean = IsSuccessfulParticipantResult(
                                earlyAbortResult,
                                Switch2RuntimeRegistrationParticipantOperation.
                                    AbortUnpublished);
                        }
                        catch
                        {
                            ownerClean = false;
                        }
                    }

                    if (ownerClean)
                    {
                        try
                        {
                            boundAdmissionCleaned = table.TryRollback(
                                rollbackClaim, out earlyRollbackFailure);
                        }
                        catch
                        {
                            boundAdmissionCleaned = false;
                            earlyRollbackFailure =
                                InputControllerSlotTableFailure.Busy;
                        }
                    }
                    if (!boundAdmissionCleaned)
                    {
                        table.TryQuarantine(rollbackClaim,
                            slotAdoptionOwned && !ownerClean ?
                                InputControllerSlotQuarantineReason.
                                    StopRejected :
                                InputControllerSlotQuarantineReason.
                                    ExternalLifecycleFailure,
                            out _);
                    }
                    if (tableFailure == InputControllerSlotTableFailure.None)
                    {
                        tableFailure = InputControllerSlotTableFailure.Busy;
                    }
                }
            }
            finally
            {
                lock (lifecycleGate)
                {
                    pendingSetupPublishCount--;
                    Monitor.PulseAll(lifecycleGate);
                }
            }
        }
        if (binding == null)
        {
            if (boundAdmissionOwned)
            {
                if (boundAdmissionCleaned)
                {
                    failure = new Switch2RuntimeRegistrationTransactionFailure(
                        slotAdoptionThrew || bindingConstructionThrew ||
                                slotAdoptionResultInvalid ?
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                DependencyThrew :
                        !slotAdoptionOwned &&
                                slotAdoptionResult.RequiresQuarantine ?
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                QuarantineRequired :
                        !slotAdoptionOwned ?
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                SlotAdoptionRejected :
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                TableRejected,
                        tableFailure: tableFailure,
                        participant: participant,
                        participantResult: earlyAbortResult.IsValid ?
                            earlyAbortResult : slotAdoptionResult);
                    return false;
                }
                failure = new Switch2RuntimeRegistrationTransactionFailure(
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        QuarantineRequired,
                    tableFailure: earlyRollbackFailure == default ?
                        tableFailure : earlyRollbackFailure,
                    participant: participant,
                    participantResult: earlyAbortResult.IsValid ?
                        earlyAbortResult : slotAdoptionResult,
                    quarantineReason: slotAdoptionOwned &&
                            (!earlyAbortResult.IsValid ||
                             !earlyAbortResult.Succeeded) ?
                        InputControllerSlotQuarantineReason.StopRejected :
                        InputControllerSlotQuarantineReason.
                            ExternalLifecycleFailure);
                return false;
            }
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.TableRejected,
                tableFailure);
            return false;
        }

        Switch2RuntimeRegistrationParticipantResult subscribeResult =
            binding.TrySubscribeExact();
        if (!IsSuccessfulParticipantResult(subscribeResult,
                Switch2RuntimeRegistrationParticipantOperation.Subscribe))
        {
            return CleanupBoundAttach(binding, credentialPrepared: false,
                deadline, timeoutMilliseconds,
                MapParticipantFailure(subscribeResult,
                    Switch2RuntimeRegistrationParticipantOperation.Subscribe,
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        SubscriptionRejected),
                default, subscribeResult, out failure);
        }

        if (binding.CloseRequested)
        {
            return CleanupBoundAttach(binding, credentialPrepared: false,
                deadline, timeoutMilliseconds,
                Switch2RuntimeRegistrationTransactionFailureKind.TableRejected,
                InputControllerSlotTableFailure.Closed, default,
                out failure);
        }

        Switch2RuntimeRegistrationParticipantResult prepareResult = default;
        try
        {
            prepareResult = binding.Participant.TryPrepareActivation(
                RemainingMilliseconds(deadline, timeoutMilliseconds));
        }
        catch
        {
        }
        if (!IsSuccessfulParticipantResult(prepareResult,
                Switch2RuntimeRegistrationParticipantOperation.
                    PrepareActivation))
        {
            return CleanupBoundAttach(binding, credentialPrepared: false,
                deadline, timeoutMilliseconds,
                MapParticipantFailure(prepareResult,
                    Switch2RuntimeRegistrationParticipantOperation.
                        PrepareActivation,
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        PrepareRejected),
                default, prepareResult, out failure);
        }

        bool tableActivated = false;
        bool committed = false;
        bool commitThrew = false;
        bool postCommitQuarantined = false;
        bool activationCompleted = false;
        bool activationAdmissionOwned = false;
        bool ownerCommitAttempted = false;
        InputControllerActivationClaim activationClaim = default;
        InputControllerActivationCommitCredential activationCommit = default;
        lock (lifecycleGate)
        {
            if (open && !serviceClosePending && !binding.CloseRequested &&
                serviceGeneration == token.ServiceGeneration &&
                ReferenceEquals(bindings[token.Slot], binding))
            {
                activationAdmissionOwned = true;
                pendingActivationCount++;
            }
            else if (tableFailure == InputControllerSlotTableFailure.None)
            {
                tableFailure = InputControllerSlotTableFailure.Closed;
            }
        }

        if (activationAdmissionOwned)
        {
            try
            {
                tableActivated = table.TryBeginActivate(token,
                    out activationClaim, out tableFailure);
                if (!tableActivated)
                {
                    return CleanupBoundAttach(binding,
                        credentialPrepared: true, deadline,
                        timeoutMilliseconds,
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            TableRejected,
                        tableFailure, default, out failure);
                }
                if (!table.TryAcquireActivationCommit(activationClaim,
                        out activationCommit, out tableFailure))
                {
                    table.TryCompleteActivate(activationClaim,
                        externalCommitSucceeded: false, out _);
                    Switch2RuntimeRegistrationParticipantResult abortResult =
                        binding.Participant.TryAbortPrepared(
                            RemainingMilliseconds(deadline,
                                timeoutMilliseconds));
                    bool aborted = IsSuccessfulParticipantResult(abortResult,
                        Switch2RuntimeRegistrationParticipantOperation.
                            AbortPrepared);
                    table.TryQuarantine(token,
                        InputControllerSlotQuarantineReason.
                            ExternalLifecycleFailure, out _);
                    failure = new Switch2RuntimeRegistrationTransactionFailure(
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            QuarantineRequired,
                        tableFailure: tableFailure,
                        participant: binding.Participant,
                        participantResult: abortResult,
                        quarantineReason: aborted ?
                            InputControllerSlotQuarantineReason.
                                ExternalLifecycleFailure :
                            InputControllerSlotQuarantineReason.StopRejected);
                    binding.CompleteSetup(success: false, failure);
                    return false;
                }

                // Commit can wake the worker immediately. Make the exact
                // binding eligible for its first report and lifecycle wake-up
                // before releasing that parked gate. The table's activation
                // claim keeps close, retirement, and actions fenced while the
                // fallible participant commit runs without either core or
                // table locks.
                binding.MarkAttached();
                ownerCommitAttempted = true;
                Switch2RuntimeRegistrationParticipantResult commitResult =
                    binding.Participant.TryCommitPrepared(activationCommit);
                committed = IsSuccessfulParticipantResult(commitResult,
                    Switch2RuntimeRegistrationParticipantOperation.
                        CommitPrepared);
                commitThrew = !IsExpectedParticipantResult(commitResult,
                        Switch2RuntimeRegistrationParticipantOperation.
                            CommitPrepared) ||
                    commitResult.FailureKind ==
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            DependencyThrew;
                activationCompleted = table.TryCompleteActivate(
                    activationCommit, committed,
                    out InputControllerSlotTableFailure completionFailure);
                if (!activationCompleted)
                {
                    postCommitQuarantined = completionFailure ==
                        InputControllerSlotTableFailure.
                            ActivationCommitRejected;
                    if (!postCommitQuarantined)
                    {
                        bool quarantined = table.TryQuarantine(token,
                            InputControllerSlotQuarantineReason.
                                ExternalLifecycleFailure,
                            out InputControllerSlotTableFailure
                                quarantineFailure);
                        postCommitQuarantined = quarantined ||
                            quarantineFailure ==
                                InputControllerSlotTableFailure.Quarantined;
                    }
                }

                if (committed && activationCompleted)
                {
                    failure = default;
                    binding.CompleteSetup(success: true, default);
                }
                else
                {
                    failure = new Switch2RuntimeRegistrationTransactionFailure(
                        commitThrew ?
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                DependencyThrew :
                        postCommitQuarantined ?
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                CommitRejected :
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                DependencyThrew,
                        participant: binding.Participant,
                        participantResult: commitResult,
                        quarantineReason: postCommitQuarantined ?
                            InputControllerSlotQuarantineReason.
                                ExternalLifecycleFailure : default);
                    binding.CompleteSetup(success: false, failure);
                }
                return committed && activationCompleted;
            }
            catch
            {
                committed = false;
                commitThrew = true;
                if (tableActivated)
                {
                    if (activationCommit.IsValid)
                    {
                        table.TryCompleteActivate(activationCommit,
                            externalCommitSucceeded: false, out _);
                    }
                    else
                    {
                        table.TryCompleteActivate(activationClaim,
                            externalCommitSucceeded: false, out _);
                    }
                    if (!ownerCommitAttempted)
                    {
                        binding.Participant.TryAbortPrepared(
                            RemainingMilliseconds(deadline,
                                timeoutMilliseconds));
                    }
                    table.TryQuarantine(token,
                        InputControllerSlotQuarantineReason.
                            ExternalLifecycleFailure, out _);
                    failure = new Switch2RuntimeRegistrationTransactionFailure(
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            DependencyThrew,
                        participant: binding.Participant,
                        quarantineReason: InputControllerSlotQuarantineReason.
                            ExternalLifecycleFailure);
                    binding.CompleteSetup(success: false, failure);
                    return false;
                }
                throw;
            }
            finally
            {
                lock (lifecycleGate)
                {
                    pendingActivationCount--;
                    Monitor.PulseAll(lifecycleGate);
                }
            }
        }

        return CleanupBoundAttach(binding, credentialPrepared: true,
            deadline, timeoutMilliseconds,
            Switch2RuntimeRegistrationTransactionFailureKind.TableRejected,
            tableFailure, default, out failure);
    }

    internal bool TryRemove(InputControllerSlotToken token,
        int timeoutMilliseconds,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        if (!IsPositiveTimeout(timeoutMilliseconds))
        {
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.InvalidTimeout);
            return false;
        }

        Binding binding;
        lock (lifecycleGate)
        {
            binding = GetExactBindingNoLock(token);
        }
        if (binding == null)
        {
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.StaleToken,
                InputControllerSlotTableFailure.StaleCredential);
            return false;
        }
        if (binding.IsCurrentCallbackThread)
        {
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.
                    ReentrantRemoval);
            return false;
        }
        if (binding.HasActiveCallback)
        {
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.CallbackActive);
            return false;
        }

        return TryRemoveBinding(binding, preexistingClaim: default,
            CreateDeadline(timeoutMilliseconds), timeoutMilliseconds,
            out failure);
    }

    internal bool TryClose(ulong exactServiceGeneration,
        int timeoutMilliseconds,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        if (!IsPositiveTimeout(timeoutMilliseconds))
        {
            failure = Fail(
                Switch2RuntimeRegistrationTransactionFailureKind.InvalidTimeout);
            return false;
        }

        long deadline = CreateDeadline(timeoutMilliseconds);
        CloseEpoch epoch = null;
        while (epoch == null)
        {
            lock (lifecycleGate)
            {
                if (closeEpoch != null)
                {
                    if (closeEpoch.ServiceGeneration !=
                        exactServiceGeneration)
                    {
                        failure = Fail(
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                TableRejected,
                            InputControllerSlotTableFailure.Closed);
                        return false;
                    }
                    if (closeEpoch.Completed)
                    {
                        failure = closeEpoch.Failure;
                        return closeEpoch.Succeeded;
                    }
                    if (closeEpoch.TeardownOwned)
                    {
                        int remaining = RemainingMilliseconds(deadline,
                            timeoutMilliseconds);
                        if (remaining == 0 ||
                            !Monitor.Wait(lifecycleGate, remaining))
                        {
                            failure = new(
                                Switch2RuntimeRegistrationTransactionFailureKind.
                                    DrainTimedOut);
                            return false;
                        }
                        continue;
                    }

                    closeEpoch.TeardownOwned = true;
                    epoch = closeEpoch;
                    continue;
                }

                if (!open || serviceGeneration != exactServiceGeneration)
                {
                    failure = Fail(
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            TableRejected,
                        InputControllerSlotTableFailure.Closed);
                    return false;
                }
                serviceClosePending = true;
                while (pendingSetupPublishCount != 0 ||
                    pendingActivationCount != 0)
                {
                    int remaining = RemainingMilliseconds(deadline,
                        timeoutMilliseconds);
                    if (remaining == 0 ||
                        !Monitor.Wait(lifecycleGate, remaining))
                    {
                        failure = Fail(
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                TableRejected,
                            InputControllerSlotTableFailure.TimedOut);
                        return false;
                    }
                    if (closeEpoch != null)
                    {
                        break;
                    }
                    if (!open || serviceGeneration != exactServiceGeneration)
                    {
                        failure = Fail(
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                TableRejected,
                            InputControllerSlotTableFailure.Closed);
                        return false;
                    }
                }
                if (closeEpoch != null)
                {
                    continue;
                }

                var closeArmed = new Binding[bindings.Length];
                int closeArmedCount = 0;
                for (int index = 0; index < bindings.Length; index++)
                {
                    Binding candidate = bindings[index];
                    if (candidate == null ||
                        candidate.Token.ServiceGeneration !=
                            exactServiceGeneration)
                    {
                        continue;
                    }
                    if (!candidate.TryBeginCloseAdmission(
                            out bool reentrantCallback))
                    {
                        for (int armedIndex = 0;
                            armedIndex < closeArmedCount; armedIndex++)
                        {
                            closeArmed[armedIndex].CancelCloseAdmission();
                        }
                        failure = Fail(
                            reentrantCallback ?
                                Switch2RuntimeRegistrationTransactionFailureKind.
                                    ReentrantRemoval :
                                Switch2RuntimeRegistrationTransactionFailureKind.
                                    CallbackActive);
                        return false;
                    }
                    closeArmed[closeArmedCount++] = candidate;
                }

                InputControllerSlotSnapshot[] snapshots;
                InputControllerSlotTableFailure tableFailure;
                if (externallyClosedSnapshots != null)
                {
                    snapshots = externallyClosedSnapshots;
                    externallyClosedSnapshots = null;
                    tableFailure = InputControllerSlotTableFailure.None;
                }
                else if (!table.TryClose(exactServiceGeneration,
                             out snapshots, out tableFailure))
                {
                    for (int index = 0; index < closeArmedCount; index++)
                    {
                        closeArmed[index].CancelCloseAdmission();
                    }
                    failure = Fail(
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            TableRejected,
                        tableFailure);
                    return false;
                }

                open = false;
                serviceClosePending = false;
                serviceGeneration = 0;
                var exactBindings = new Binding[snapshots.Length];
                for (int index = 0; index < snapshots.Length; index++)
                {
                    InputControllerSlotSnapshot snapshot = snapshots[index];
                    Binding binding = GetExactBindingNoLock(snapshot.Token);
                    exactBindings[index] = binding;
                    if (binding == null)
                    {
                        continue;
                    }
                    if (snapshot.State == InputControllerSlotState.Bound)
                    {
                        binding.RequestClose(snapshot.SetupRollbackClaim);
                    }
                    else if (snapshot.State is
                        InputControllerSlotState.Retiring or
                        InputControllerSlotState.Quiesced)
                    {
                        binding.TryAdoptRetirementClaim(
                            snapshot.RetirementClaim);
                    }
                }

                epoch = new CloseEpoch(exactServiceGeneration, snapshots,
                    exactBindings)
                {
                    TeardownOwned = true,
                };
                closeEpoch = epoch;
                Monitor.PulseAll(lifecycleGate);
            }
        }

        return TryRunCloseEpoch(epoch, deadline, timeoutMilliseconds,
            out failure);
    }

    private bool TryRunCloseEpoch(CloseEpoch epoch, long deadline,
        int originalTimeout,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        bool succeeded = false;
        bool terminal = false;
        Switch2RuntimeRegistrationTransactionFailure attemptFailure = default;
        try
        {
            succeeded = TryAdvanceCloseEpoch(epoch, deadline, originalTimeout,
                out terminal, out attemptFailure);
        }
        catch
        {
            // The table is already closed, so an unexpected observer failure
            // cannot restore admission or be cached as a completed teardown.
            // Release only the attempt owner and leave the exact epoch
            // retryable; TryOpen remains fenced until a later attempt resolves
            // every captured lifetime.
            succeeded = false;
            terminal = false;
            attemptFailure = new(
                Switch2RuntimeRegistrationTransactionFailureKind.DependencyThrew);
        }

        lock (lifecycleGate)
        {
            if (!ReferenceEquals(closeEpoch, epoch))
            {
                failure = Fail(
                    Switch2RuntimeRegistrationTransactionFailureKind.TableRejected,
                    InputControllerSlotTableFailure.StaleCredential);
                return false;
            }

            epoch.TeardownOwned = false;
            if (terminal)
            {
                epoch.Completed = true;
                epoch.Succeeded = succeeded;
                epoch.Failure = attemptFailure;
            }
            Monitor.PulseAll(lifecycleGate);
        }

        failure = attemptFailure;
        return succeeded;
    }

    private bool TryAdvanceCloseEpoch(CloseEpoch epoch, long deadline,
        int originalTimeout, out bool terminal,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        Switch2RuntimeRegistrationTransactionFailure firstFailure = default;
        bool succeeded = true;
        bool retryPending = false;
        for (int index = 0; index < epoch.Snapshots.Length; index++)
        {
            InputControllerSlotSnapshot snapshot = epoch.Snapshots[index];
            Binding binding = epoch.Bindings[index];
            bool entrySucceeded = true;
            bool entryRetryPending = false;
            Switch2RuntimeRegistrationTransactionFailure entryFailure = default;
            if (snapshot.State == InputControllerSlotState.Bound)
            {
                if (binding == null || !binding.WaitForSetupCompletion(
                        RemainingMilliseconds(deadline,
                            originalTimeout)) ||
                    !binding.SetupCleanupSucceeded)
                {
                    table.TryQuarantine(snapshot.SetupRollbackClaim,
                        InputControllerSlotQuarantineReason.DrainTimedOut,
                        out _);
                    entrySucceeded = false;
                    entryFailure = new(
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            QuarantineRequired,
                        quarantineReason:
                            InputControllerSlotQuarantineReason.DrainTimedOut);
                }
            }
            else if (snapshot.State is InputControllerSlotState.Retiring or
                InputControllerSlotState.Quiesced)
            {
                Switch2RuntimeRegistrationTransactionFailure removalFailure =
                    default;
                if (binding == null || !TryRemoveBinding(binding,
                        snapshot.RetirementClaim, deadline,
                        originalTimeout,
                        out removalFailure))
                {
                    if (binding == null)
                    {
                        table.TryQuarantine(snapshot.RetirementClaim,
                            InputControllerSlotQuarantineReason.
                                ExternalLifecycleFailure, out _);
                        removalFailure = new(
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                QuarantineRequired,
                            quarantineReason:
                                InputControllerSlotQuarantineReason.
                                    ExternalLifecycleFailure);
                    }
                    entrySucceeded = false;
                    entryFailure = removalFailure;
                    entryRetryPending = binding != null &&
                        IsRetryableCloseObserverFailure(removalFailure);
                }
            }
            else if (snapshot.State == InputControllerSlotState.Quarantined)
            {
                entrySucceeded = false;
                entryFailure = new(
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        QuarantineRequired,
                    quarantineReason: snapshot.QuarantineReason);
            }

            if (!entrySucceeded)
            {
                if (succeeded)
                {
                    firstFailure = entryFailure;
                }
                succeeded = false;
                retryPending |= entryRetryPending;
            }
        }

        terminal = !retryPending;
        failure = succeeded ? default : firstFailure;
        return succeeded;
    }

    private static bool IsRetryableCloseObserverFailure(
        in Switch2RuntimeRegistrationTransactionFailure failure) =>
        !failure.RequiresQuarantine && failure.Kind is
            (Switch2RuntimeRegistrationTransactionFailureKind.DrainTimedOut or
             Switch2RuntimeRegistrationTransactionFailureKind.CallbackActive or
             Switch2RuntimeRegistrationTransactionFailureKind.ReentrantRemoval);

    private bool CleanupBoundAttach(Binding binding,
        bool credentialPrepared, long deadline, int originalTimeout,
        Switch2RuntimeRegistrationTransactionFailureKind originalKind,
        InputControllerSlotTableFailure originalTableFailure,
        Switch2RuntimeRegistrationParticipantResult originalParticipantResult,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        bool aborted;
        Switch2RuntimeRegistrationParticipantResult abortResult = default;
        try
        {
            int remaining = RemainingMilliseconds(deadline, originalTimeout);
            Switch2RuntimeRegistrationParticipantOperation operation =
                credentialPrepared ?
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortPrepared :
                    Switch2RuntimeRegistrationParticipantOperation.
                        AbortUnpublished;
            abortResult = credentialPrepared ?
                    binding.Participant.TryAbortPrepared(remaining) :
                    binding.Participant.TryAbortUnpublished(remaining);
            aborted = IsSuccessfulParticipantResult(abortResult, operation);
        }
        catch
        {
            aborted = false;
        }

        Switch2RuntimeRegistrationParticipantResult unsubscribeResult =
            binding.TryUnsubscribeExact();
        bool unsubscribed = IsSuccessfulParticipantResult(unsubscribeResult,
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        bool rolledBack = false;
        InputControllerSlotTableFailure rollbackFailure = default;
        if (aborted && unsubscribed)
        {
            rolledBack = TryRollbackAndClearExactBinding(binding,
                out rollbackFailure);
        }

        if (rolledBack)
        {
            failure = new Switch2RuntimeRegistrationTransactionFailure(
                originalKind, originalTableFailure,
                participant: binding.Participant,
                participantResult: originalParticipantResult);
            binding.CompleteSetup(success: false, failure,
                cleanupSucceeded: true);
            return false;
        }

        table.TryQuarantine(binding.SetupRollbackClaim,
            aborted && unsubscribed ?
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure :
                InputControllerSlotQuarantineReason.StopRejected,
            out _);
        failure = new Switch2RuntimeRegistrationTransactionFailure(
            Switch2RuntimeRegistrationTransactionFailureKind.QuarantineRequired,
            tableFailure: rollbackFailure,
            participant: binding.Participant,
            participantResult: aborted ? unsubscribeResult : abortResult,
            quarantineReason: aborted && unsubscribed ?
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure :
                InputControllerSlotQuarantineReason.StopRejected,
            originalParticipantResult: originalParticipantResult);
        binding.CompleteSetup(success: false, failure,
            cleanupSucceeded: false);
        return false;
    }

    private bool QuarantinePostCommitFailureNoLock(Binding binding)
    {
        if (table.TryBeginRetire(binding.Token,
                out InputControllerRetirementClaim claim, out _))
        {
            binding.TryAdoptRetirementClaim(claim);
            bool quarantined = table.TryQuarantine(claim,
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure, out _);
            return quarantined;
        }
        return table.TryQuarantine(binding.Token,
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
            out _);
    }

    private bool TryRemoveBinding(Binding binding,
        InputControllerRetirementClaim preexistingClaim, long deadline,
        int originalTimeout,
        out Switch2RuntimeRegistrationTransactionFailure failure)
    {
        RemovalOwnershipResult ownership = binding.TryOwnRemoval();
        if (ownership is RemovalOwnershipResult.CallbackActive or
            RemovalOwnershipResult.ReentrantCallback)
        {
            failure = Fail(ownership ==
                    RemovalOwnershipResult.ReentrantCallback ?
                Switch2RuntimeRegistrationTransactionFailureKind.
                    ReentrantRemoval :
                Switch2RuntimeRegistrationTransactionFailureKind.CallbackActive);
            return false;
        }
        if (ownership == RemovalOwnershipResult.AlreadyOwned)
        {
            if (!binding.WaitForRemovalCompletion(RemainingMilliseconds(
                    deadline, originalTimeout)))
            {
                failure = new(
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        DrainTimedOut);
                return false;
            }
            failure = binding.RemovalFailure;
            return binding.RemovalSucceeded;
        }

        bool succeeded = false;
        failure = default;
        try
        {
            if (!TryResolveRetirementClaim(binding, preexistingClaim,
                    out InputControllerRetirementClaim claim,
                    out InputControllerSlotTableFailure tableFailure))
            {
                failure = Fail(
                    Switch2RuntimeRegistrationTransactionFailureKind.TableRejected,
                    tableFailure);
                return false;
            }

            Switch2RuntimeRegistrationParticipantResult armResult = default;
            try
            {
                armResult = binding.Participant.TryArmRetirement(claim);
            }
            catch
            {
            }
            if (!IsSuccessfulParticipantResult(armResult,
                    Switch2RuntimeRegistrationParticipantOperation.
                        ArmRetirement))
            {
                failure = Quarantine(binding, claim,
                    ParticipantQuarantineReason(armResult,
                        InputControllerSlotQuarantineReason.
                            ExternalLifecycleFailure),
                    MapParticipantFailure(armResult,
                        Switch2RuntimeRegistrationParticipantOperation.
                            ArmRetirement,
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            DependencyThrew));
                return false;
            }

            int remaining = RemainingMilliseconds(deadline, originalTimeout);
            if (!table.TryWaitForDrain(claim, remaining, out tableFailure))
            {
                failure = Quarantine(binding, claim,
                    InputControllerSlotQuarantineReason.DrainTimedOut,
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        DrainTimedOut,
                    tableFailure);
                return false;
            }

            remaining = RemainingMilliseconds(deadline, originalTimeout);
            Switch2RuntimeRegistrationParticipantResult publicationResult =
                default;
            try
            {
                publicationResult = binding.Participant.
                    TryWaitForPublicationAvailability(remaining);
            }
            catch
            {
            }
            if (!IsSuccessfulParticipantResult(publicationResult,
                    Switch2RuntimeRegistrationParticipantOperation.
                        WaitForPublicationAvailability))
            {
                failure = Quarantine(binding, claim,
                    ParticipantQuarantineReason(publicationResult,
                        InputControllerSlotQuarantineReason.DrainTimedOut),
                    MapParticipantFailure(publicationResult,
                        Switch2RuntimeRegistrationParticipantOperation.
                            WaitForPublicationAvailability,
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            DrainTimedOut));
                return false;
            }

            remaining = RemainingMilliseconds(deadline, originalTimeout);
            Switch2RuntimeRegistrationParticipantResult stopResult = default;
            try
            {
                stopResult = binding.Participant.TryStopAndQuiesce(remaining);
            }
            catch
            {
            }
            if (!IsSuccessfulParticipantResult(stopResult,
                    Switch2RuntimeRegistrationParticipantOperation.
                        StopAndQuiesce))
            {
                InputControllerOwnerOperationFailure ownerFailure =
                    stopResult.IsValid ? stopResult.OwnerFailure :
                        InputControllerOwnerOperationFailure.OwnerThrew;
                InputControllerSlotQuarantineReason reason =
                    ReasonForStopFailure(binding, stopResult, ownerFailure);
                failure = Quarantine(binding, claim, reason,
                    reason == InputControllerSlotQuarantineReason.
                            TerminalNeutralNotObserved ?
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            TerminalNeutralRejected :
                        MapParticipantFailure(stopResult,
                            Switch2RuntimeRegistrationParticipantOperation.
                                StopAndQuiesce,
                            Switch2RuntimeRegistrationTransactionFailureKind.
                                StopRejected),
                    ownerFailure: ownerFailure,
                    participantResult: stopResult);
                return false;
            }

            remaining = RemainingMilliseconds(deadline, originalTimeout);
            if (!table.TryWaitForDrain(claim, remaining, out tableFailure))
            {
                failure = Quarantine(binding, claim,
                    InputControllerSlotQuarantineReason.DrainTimedOut,
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        DrainTimedOut,
                    tableFailure);
                return false;
            }
            if (!table.TryMarkQuiesced(claim, out tableFailure))
            {
                failure = Quarantine(binding, claim,
                    tableFailure == InputControllerSlotTableFailure.
                            TerminalNeutralRequired ?
                        InputControllerSlotQuarantineReason.
                            TerminalNeutralNotObserved :
                        InputControllerSlotQuarantineReason.
                            ExternalLifecycleFailure,
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        TerminalNeutralRejected,
                    tableFailure);
                return false;
            }

            Switch2RuntimeRegistrationParticipantResult unsubscribeResult =
                binding.TryUnsubscribeExact();
            if (!IsSuccessfulParticipantResult(unsubscribeResult,
                    Switch2RuntimeRegistrationParticipantOperation.
                        Unsubscribe))
            {
                failure = Quarantine(binding, claim,
                    ParticipantQuarantineReason(unsubscribeResult,
                        InputControllerSlotQuarantineReason.
                            ExternalLifecycleFailure),
                    MapParticipantFailure(unsubscribeResult,
                        Switch2RuntimeRegistrationParticipantOperation.
                            Unsubscribe,
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            DependencyThrew));
                return false;
            }
            Switch2RuntimeRegistrationParticipantResult removeResult =
                default;
            try
            {
                removeResult = binding.Participant.TryRemove();
            }
            catch
            {
            }
            if (!IsSuccessfulParticipantResult(removeResult,
                    Switch2RuntimeRegistrationParticipantOperation.Remove))
            {
                InputControllerOwnerOperationFailure ownerFailure =
                    removeResult.IsValid ? removeResult.OwnerFailure :
                        InputControllerOwnerOperationFailure.OwnerThrew;
                failure = Quarantine(binding, claim,
                    ParticipantQuarantineReason(removeResult,
                        ownerFailure == InputControllerOwnerOperationFailure.
                                OwnerAuthenticationFailed ?
                            InputControllerSlotQuarantineReason.
                                OwnerAuthenticationLost :
                        ownerFailure == InputControllerOwnerOperationFailure.
                                OwnerThrew ?
                            InputControllerSlotQuarantineReason.OwnerThrew :
                            InputControllerSlotQuarantineReason.
                                RemoveRejected),
                    MapParticipantFailure(removeResult,
                        Switch2RuntimeRegistrationParticipantOperation.Remove,
                        Switch2RuntimeRegistrationTransactionFailureKind.
                            RemoveRejected),
                    ownerFailure: ownerFailure);
                return false;
            }
            if (!TryCompleteRemovalAndClearExactBinding(binding, claim,
                    out tableFailure))
            {
                failure = Quarantine(binding, claim,
                    InputControllerSlotQuarantineReason.
                        ExternalLifecycleFailure,
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        TableRejected,
                    tableFailure);
                return false;
            }

            succeeded = true;
            failure = default;
            return true;
        }
        finally
        {
            binding.CompleteRemoval(succeeded, failure);
            if (succeeded)
            {
                PublishRuntimeRemoved(binding.Token);
            }
        }
    }

    private void PublishRuntimeRemoved(InputControllerSlotToken token)
    {
        Action<InputControllerSlotToken> observers = RuntimeRemoved;
        if (observers == null) return;
        foreach (Action<InputControllerSlotToken> observer in observers.GetInvocationList())
        {
            try
            {
                observer(token);
            }
            catch
            {
                // A presentation observer cannot undo completed ownership
                // retirement or prevent the other observers being notified.
            }
        }
    }

    private bool TryResolveRetirementClaim(Binding binding,
        InputControllerRetirementClaim preexistingClaim,
        out InputControllerRetirementClaim claim,
        out InputControllerSlotTableFailure failure)
    {
        if (preexistingClaim.IsValid)
        {
            if (!binding.TryAdoptRetirementClaim(preexistingClaim))
            {
                table.TryQuarantine(preexistingClaim,
                    InputControllerSlotQuarantineReason.
                        ExternalLifecycleFailure, out _);
                claim = default;
                failure = InputControllerSlotTableFailure.Quarantined;
                return false;
            }
            claim = preexistingClaim;
            failure = default;
            return true;
        }
        if (binding.TryGetRetirementClaim(out claim))
        {
            failure = default;
            return true;
        }
        if (table.TryBeginRetire(binding.Token, out claim, out failure))
        {
            if (binding.TryAdoptRetirementClaim(claim))
            {
                return true;
            }
            table.TryQuarantine(claim,
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure, out _);
            failure = InputControllerSlotTableFailure.Quarantined;
            claim = default;
            return false;
        }

        InputControllerSlotSnapshot[] snapshots = table.GetSnapshot();
        if (binding.Token.Slot >= 0 &&
            binding.Token.Slot < snapshots.Length)
        {
            InputControllerSlotSnapshot snapshot =
                snapshots[binding.Token.Slot];
            if (snapshot.Token == binding.Token &&
                snapshot.RetirementClaim.IsValid)
            {
                if (binding.TryAdoptRetirementClaim(
                        snapshot.RetirementClaim))
                {
                    claim = snapshot.RetirementClaim;
                    failure = default;
                    return true;
                }
                table.TryQuarantine(snapshot.RetirementClaim,
                    InputControllerSlotQuarantineReason.
                        ExternalLifecycleFailure, out _);
                failure = InputControllerSlotTableFailure.Quarantined;
            }
        }

        claim = default;
        return false;
    }

    private Switch2RuntimeRegistrationTransactionFailure Quarantine(
        Binding binding, InputControllerRetirementClaim claim,
        InputControllerSlotQuarantineReason reason,
        Switch2RuntimeRegistrationTransactionFailureKind kind,
        InputControllerSlotTableFailure tableFailure = default,
        InputControllerOwnerOperationFailure ownerFailure = default,
        Switch2RuntimeRegistrationParticipantResult participantResult =
            default)
    {
        table.TryQuarantine(claim, reason, out _);
        return new Switch2RuntimeRegistrationTransactionFailure(kind,
            tableFailure: tableFailure, ownerFailure: ownerFailure,
            participant: binding.Participant,
            participantResult: participantResult,
            quarantineReason: reason);
    }

    private static InputControllerSlotQuarantineReason ReasonForStopFailure(
        Binding binding,
        in Switch2RuntimeRegistrationParticipantResult participantResult,
        InputControllerOwnerOperationFailure ownerFailure)
    {
        if (binding.TerminalReportRejected ||
            participantResult.IsValid && participantResult.FailureKind ==
                Switch2RuntimeRegistrationParticipantFailureKind.
                    TerminalNeutralRejected)
        {
            return InputControllerSlotQuarantineReason.
                TerminalNeutralNotObserved;
        }
        if (ownerFailure == InputControllerOwnerOperationFailure.
                OwnerAuthenticationFailed)
        {
            return InputControllerSlotQuarantineReason.
                OwnerAuthenticationLost;
        }
        if (ownerFailure == InputControllerOwnerOperationFailure.OwnerThrew)
        {
            return InputControllerSlotQuarantineReason.OwnerThrew;
        }
        return ParticipantQuarantineReason(participantResult,
            InputControllerSlotQuarantineReason.StopRejected);
    }

    private void RetireFromLifecycleAttention(Binding binding)
    {
        long deadline = CreateDeadline(
            lifecycleAttentionTimeoutMilliseconds);
        bool resolved = false;
        try
        {
            if (!binding.WaitForSetupCompletion(RemainingMilliseconds(
                    deadline, lifecycleAttentionTimeoutMilliseconds)))
            {
                // Setup completion is the durable retry signal. Attention is
                // a retained service intent, so a short policy timeout cannot
                // consume the owner's one-shot lifecycle wake-up.
                return;
            }
            if (!binding.SetupSucceeded)
            {
                resolved = true;
                return;
            }
            if (TryRemoveBinding(binding, preexistingClaim: default,
                    deadline, lifecycleAttentionTimeoutMilliseconds,
                    out Switch2RuntimeRegistrationTransactionFailure failure))
            {
                resolved = true;
                return;
            }
            if (failure.Kind is
                Switch2RuntimeRegistrationTransactionFailureKind.CallbackActive or
                Switch2RuntimeRegistrationTransactionFailureKind.ReentrantRemoval)
            {
                // ExitCallback is the durable retry signal. Never spin a
                // worker while the exact mapping lease is still held.
                return;
            }

            // All non-transient failures are terminal for this exact owner.
            // Preserve fail-closed state even if the failing path could not
            // manufacture a retirement claim.
            QuarantineUnscheduledAttention(binding);
            resolved = true;
        }
        finally
        {
            binding.CompleteAttentionWorker(resolved);
        }
    }

    private void QuarantineUnscheduledAttention(Binding binding)
    {
        if (TryResolveRetirementClaim(binding, default,
                out InputControllerRetirementClaim claim, out _))
        {
            table.TryQuarantine(claim,
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure, out _);
            return;
        }
        table.TryQuarantine(binding.Token,
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
            out _);
    }

    private Binding GetExactBindingNoLock(InputControllerSlotToken token)
    {
        if (!token.IsValid || token.Slot < 0 || token.Slot >= bindings.Length)
        {
            return null;
        }
        Binding binding = bindings[token.Slot];
        return binding != null && binding.Token == token ? binding : null;
    }

    private bool TryRollbackAndClearExactBinding(Binding binding,
        out InputControllerSlotTableFailure failure)
    {
        lock (lifecycleGate)
        {
            if (!ReferenceEquals(GetExactBindingNoLock(binding.Token),
                    binding))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (!table.TryRollback(binding.SetupRollbackClaim, out failure))
            {
                return false;
            }
            bindings[binding.Token.Slot] = null;
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    private bool TryCompleteRemovalAndClearExactBinding(Binding binding,
        InputControllerRetirementClaim claim,
        out InputControllerSlotTableFailure failure)
    {
        if (!claim.IsValid || claim.Token != binding.Token)
        {
            failure = InputControllerSlotTableFailure.StaleCredential;
            return false;
        }
        lock (lifecycleGate)
        {
            if (!ReferenceEquals(GetExactBindingNoLock(binding.Token),
                    binding))
            {
                failure = InputControllerSlotTableFailure.StaleCredential;
                return false;
            }
            if (!table.TryCompleteRemoval(claim, out failure))
            {
                return false;
            }
            bindings[binding.Token.Slot] = null;
            failure = InputControllerSlotTableFailure.None;
            return true;
        }
    }

    private static bool IsExpectedParticipantResult(
        in Switch2RuntimeRegistrationParticipantResult result,
        Switch2RuntimeRegistrationParticipantOperation operation) =>
        result.IsValid && result.Operation == operation;

    private static bool IsSuccessfulParticipantResult(
        in Switch2RuntimeRegistrationParticipantResult result,
        Switch2RuntimeRegistrationParticipantOperation operation) =>
        IsExpectedParticipantResult(result, operation) && result.Succeeded;

    private static Switch2RuntimeRegistrationTransactionFailureKind
        MapParticipantFailure(
            in Switch2RuntimeRegistrationParticipantResult result,
            Switch2RuntimeRegistrationParticipantOperation operation,
            Switch2RuntimeRegistrationTransactionFailureKind fallback)
    {
        if (!IsExpectedParticipantResult(result, operation))
        {
            return Switch2RuntimeRegistrationTransactionFailureKind.
                DependencyThrew;
        }
        if (operation ==
                Switch2RuntimeRegistrationParticipantOperation.
                    ArmRetirement &&
            result.FailureKind ==
                Switch2RuntimeRegistrationParticipantFailureKind.
                    QuarantineRequired)
        {
            return Switch2RuntimeRegistrationTransactionFailureKind.
                QuarantineRequired;
        }

        return operation switch
        {
            Switch2RuntimeRegistrationParticipantOperation.Subscribe =>
                Switch2RuntimeRegistrationTransactionFailureKind.
                    SubscriptionRejected,
            Switch2RuntimeRegistrationParticipantOperation.
                    PrepareActivation when result.FailureKind ==
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            DependencyThrew =>
                Switch2RuntimeRegistrationTransactionFailureKind.DependencyThrew,
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared
                    when result.FailureKind ==
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            DependencyThrew ||
                    result.Outcome ==
                        Switch2RuntimeRegistrationParticipantOutcome.
                            OutcomeUncertain =>
                Switch2RuntimeRegistrationTransactionFailureKind.DependencyThrew,
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared =>
                Switch2RuntimeRegistrationTransactionFailureKind.CommitRejected,
            Switch2RuntimeRegistrationParticipantOperation.
                    WaitForPublicationAvailability =>
                result.FailureKind ==
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            PublicationDrainTimedOut ?
                    Switch2RuntimeRegistrationTransactionFailureKind.DrainTimedOut :
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        DependencyThrew,
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce =>
                result.FailureKind ==
                        Switch2RuntimeRegistrationParticipantFailureKind.
                            TerminalNeutralRejected ?
                    Switch2RuntimeRegistrationTransactionFailureKind.
                        TerminalNeutralRejected :
                    Switch2RuntimeRegistrationTransactionFailureKind.StopRejected,
            Switch2RuntimeRegistrationParticipantOperation.Remove =>
                Switch2RuntimeRegistrationTransactionFailureKind.RemoveRejected,
            _ => fallback,
        };
    }

    private static InputControllerSlotQuarantineReason
        ParticipantQuarantineReason(
            in Switch2RuntimeRegistrationParticipantResult result,
            InputControllerSlotQuarantineReason fallback) =>
        result.IsValid && result.QuarantineReason !=
                InputControllerSlotQuarantineReason.None ?
            result.QuarantineReason : fallback;

    private static bool IsPositiveTimeout(int timeoutMilliseconds) =>
        timeoutMilliseconds > 0 && timeoutMilliseconds <=
            InputControllerRegistration.MaximumStopTimeoutMilliseconds;

    private static long CreateDeadline(int timeoutMilliseconds)
    {
        long now = Stopwatch.GetTimestamp();
        if (timeoutMilliseconds == 0)
        {
            return now;
        }
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

    private static Switch2RuntimeRegistrationTransactionFailure Fail(
        Switch2RuntimeRegistrationTransactionFailureKind kind,
        InputControllerSlotTableFailure tableFailure = default) =>
        new(kind, tableFailure);

    private sealed class Binding
    {
        private readonly object gate = new();
        private readonly Switch2RuntimeRegistrationTransactionCore core;
        private readonly ISwitch2RuntimeRegistrationParticipant participant;
        private readonly Switch2RuntimeMappingCallback mappingCallback;
        private readonly ManualResetEventSlim setupCompleted = new(false);
        private readonly ManualResetEventSlim removalCompleted = new(false);

        private InputControllerRetirementClaim retirementClaim;
        private InputControllerSetupRollbackClaim closeRollbackClaim;
        private int attached;
        private bool setupCleanupSucceeded;
        private bool setupSucceeded;
        private bool closeAdmission;
        private bool removalOwned;
        private bool callbackActive;
        private int closeRequested;
        private int attentionQueued;
        private int attentionWorkerScheduled;
        private int callbackThreadId;
        private int terminalReportRejected;
        private bool removalSucceeded;
        private Switch2RuntimeRegistrationTransactionFailure removalFailure;

        internal Binding(
            Switch2RuntimeRegistrationTransactionCore core,
            ISwitch2RuntimeRegistrationParticipant participant,
            InputControllerSlotToken token,
            InputControllerSetupRollbackClaim setupRollbackClaim,
            Switch2RuntimeMappingCallback mappingCallback)
        {
            this.core = core;
            this.participant = participant ??
                throw new ArgumentNullException(nameof(participant));
            if (!participant.Registration.Equals(token.Registration))
            {
                throw new ArgumentException(
                    "The participant registration does not match the slot.",
                    nameof(participant));
            }
            Token = token;
            SetupRollbackClaim = setupRollbackClaim;
            this.mappingCallback = mappingCallback;
            ReportHandler = HandleReport;
            AttentionHandler = HandleLifecycleAttention;
            Callbacks = new Switch2RuntimeRegistrationCallbacks(
                ReportHandler, AttentionHandler);
        }

        internal ISwitch2RuntimeRegistrationParticipant Participant =>
            participant;

        internal InputControllerSlotToken Token { get; }

        internal InputControllerSetupRollbackClaim SetupRollbackClaim
        {
            get;
        }

        internal DS4Device.ReportHandler<EventArgs> ReportHandler { get; }

        internal Switch2RuntimeRegistrationLifecycleAttentionCallback
            AttentionHandler { get; }

        internal Switch2RuntimeRegistrationCallbacks Callbacks { get; }

        internal bool CloseRequested => Volatile.Read(ref closeRequested) != 0;

        internal bool IsCurrentCallbackThread => Volatile.Read(
            ref callbackThreadId) == Environment.CurrentManagedThreadId;

        internal bool HasActiveCallback => Volatile.Read(
            ref callbackThreadId) != 0;

        internal bool TerminalReportRejected => Volatile.Read(
            ref terminalReportRejected) != 0;

        internal bool SetupCleanupSucceeded
        {
            get
            {
                lock (gate)
                {
                    return setupCleanupSucceeded || setupSucceeded;
                }
            }
        }

        internal bool SetupSucceeded
        {
            get
            {
                lock (gate)
                {
                    return setupSucceeded;
                }
            }
        }

        internal bool RemovalSucceeded
        {
            get
            {
                lock (gate)
                {
                    return removalSucceeded;
                }
            }
        }

        internal Switch2RuntimeRegistrationTransactionFailure RemovalFailure
        {
            get
            {
                lock (gate)
                {
                    return removalFailure;
                }
            }
        }

        internal Switch2RuntimeRegistrationParticipantResult
            TrySubscribeExact()
        {
            try
            {
                return participant.TrySubscribe(Callbacks);
            }
            catch
            {
                return default;
            }
        }

        internal void MarkAttached()
        {
            Volatile.Write(ref attached, 1);
        }

        internal void CompleteSetup(bool success,
            Switch2RuntimeRegistrationTransactionFailure failure,
            bool cleanupSucceeded = false)
        {
            lock (gate)
            {
                setupSucceeded = success;
                setupCleanupSucceeded = cleanupSucceeded;
            }
            setupCompleted.Set();
            TryScheduleAttentionRetirement();
        }

        internal void RequestClose(InputControllerSetupRollbackClaim claim)
        {
            if (claim != SetupRollbackClaim)
            {
                return;
            }
            lock (gate)
            {
                closeRollbackClaim = claim;
                Volatile.Write(ref closeRequested, 1);
            }
        }

        internal bool TryBeginCloseAdmission(out bool reentrantCallback)
        {
            lock (gate)
            {
                if (callbackActive)
                {
                    reentrantCallback = callbackThreadId ==
                        Environment.CurrentManagedThreadId;
                    return false;
                }
                closeAdmission = true;
                reentrantCallback = false;
                return true;
            }
        }

        internal void CancelCloseAdmission()
        {
            lock (gate)
            {
                closeAdmission = false;
            }
        }

        internal bool WaitForSetupCompletion(int timeoutMilliseconds) =>
            setupCompleted.Wait(timeoutMilliseconds);

        internal bool TryAdoptRetirementClaim(
            InputControllerRetirementClaim claim)
        {
            if (!claim.IsValid || claim.Token != Token)
            {
                return false;
            }
            lock (gate)
            {
                if (retirementClaim.IsValid && retirementClaim != claim)
                {
                    return false;
                }
                retirementClaim = claim;
                return true;
            }
        }

        internal bool TryGetRetirementClaim(
            out InputControllerRetirementClaim claim)
        {
            lock (gate)
            {
                claim = retirementClaim;
                return claim.IsValid;
            }
        }

        internal RemovalOwnershipResult TryOwnRemoval()
        {
            lock (gate)
            {
                if (removalOwned)
                {
                    return RemovalOwnershipResult.AlreadyOwned;
                }
                if (callbackActive)
                {
                    return callbackThreadId ==
                            Environment.CurrentManagedThreadId ?
                        RemovalOwnershipResult.ReentrantCallback :
                        RemovalOwnershipResult.CallbackActive;
                }
                removalOwned = true;
                return RemovalOwnershipResult.Acquired;
            }
        }

        internal bool WaitForRemovalCompletion(int timeoutMilliseconds) =>
            removalCompleted.Wait(timeoutMilliseconds);

        internal void CompleteRemoval(bool succeeded,
            Switch2RuntimeRegistrationTransactionFailure failure)
        {
            lock (gate)
            {
                removalSucceeded = succeeded;
                removalFailure = failure;
            }
            Interlocked.Exchange(ref attentionQueued, 0);
            removalCompleted.Set();
        }

        internal void CompleteAttentionWorker(bool resolved)
        {
            if (resolved)
            {
                Interlocked.Exchange(ref attentionQueued, 0);
            }
            Interlocked.Exchange(ref attentionWorkerScheduled, 0);
            if (!resolved && CanRunAttentionRetirement())
            {
                TryScheduleAttentionRetirement();
            }
        }

        private bool CanRunAttentionRetirement()
        {
            if (Volatile.Read(ref attentionQueued) == 0 ||
                !setupCompleted.IsSet || removalCompleted.IsSet)
            {
                return false;
            }
            lock (gate)
            {
                return !callbackActive;
            }
        }

        private void TryScheduleAttentionRetirement()
        {
            if (!CanRunAttentionRetirement() ||
                Interlocked.CompareExchange(ref attentionWorkerScheduled,
                    1, 0) != 0)
            {
                return;
            }

            bool queued = ThreadPool.UnsafeQueueUserWorkItem(static state =>
                    state.core.RetireFromLifecycleAttention(state),
                this, preferLocal: false);
            if (queued)
            {
                return;
            }

            Interlocked.Exchange(ref attentionWorkerScheduled, 0);
            core.QuarantineUnscheduledAttention(this);
            Interlocked.Exchange(ref attentionQueued, 0);
        }

        internal Switch2RuntimeRegistrationParticipantResult
            TryUnsubscribeExact()
        {
            try
            {
                return participant.TryUnsubscribe();
            }
            catch
            {
                return default;
            }
        }

        private void HandleReport(DS4Device sender, EventArgs args)
        {
            if (!ReferenceEquals(sender, Token.Registration.Device) ||
                args is not Switch2RuntimeReportEventArgs report ||
                report.RuntimeGeneration != Token.Registration.Generation)
            {
                Interlocked.Exchange(ref terminalReportRejected, 1);
                throw new InvalidOperationException(
                    "Switch 2 runtime report identity was rejected.");
            }

            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                if (!core.table.TryAcquireReportLease(Token, sender,
                        out InputControllerReportLease lease, out _))
                {
                    throw new InvalidOperationException(
                        "Switch 2 regular report admission was rejected.");
                }
                if (!TryEnterCallback(terminal: false))
                {
                    lease.Dispose();
                    throw new InvalidOperationException(
                        "Switch 2 regular callback admission was rejected.");
                }
                try
                {
                    InvokeMapping(sender, report);
                }
                finally
                {
                    ExitCallback();
                    lease.Dispose();
                }
                return;
            }
            if (report.Kind != Switch2RuntimeReportKind.TerminalNeutral ||
                !TryGetRetirementClaim(out InputControllerRetirementClaim
                    claim) ||
                !core.table.TryAcquireTerminalReportLease(claim,
                    sender, out InputControllerReportLease terminalLease,
                    out _))
            {
                Interlocked.Exchange(ref terminalReportRejected, 1);
                throw new InvalidOperationException(
                    "Switch 2 terminal report admission was rejected.");
            }

            if (!TryEnterCallback(terminal: true))
            {
                terminalLease.Dispose();
                Interlocked.Exchange(ref terminalReportRejected, 1);
                throw new InvalidOperationException(
                    "Switch 2 terminal callback admission was rejected.");
            }
            try
            {
                InvokeMapping(sender, report);
                if (!terminalLease.TryAcknowledgeTerminalNeutral(out _))
                {
                    Interlocked.Exchange(ref terminalReportRejected, 1);
                    throw new InvalidOperationException(
                        "Switch 2 terminal acknowledgement was rejected.");
                }
            }
            catch
            {
                Interlocked.Exchange(ref terminalReportRejected, 1);
                throw;
            }
            finally
            {
                ExitCallback();
                terminalLease.Dispose();
            }
        }

        private bool TryEnterCallback(bool terminal)
        {
            lock (gate)
            {
                if (callbackActive || !terminal &&
                        (removalOwned || closeAdmission))
                {
                    return false;
                }
                if (terminal && !removalOwned)
                {
                    return false;
                }
                callbackActive = true;
                Volatile.Write(ref callbackThreadId,
                    Environment.CurrentManagedThreadId);
                return true;
            }
        }

        private void ExitCallback()
        {
            lock (gate)
            {
                callbackActive = false;
                Volatile.Write(ref callbackThreadId, 0);
            }
            TryScheduleAttentionRetirement();
        }

        private void InvokeMapping(DS4Device sender,
            Switch2RuntimeReportEventArgs report)
        {
            mappingCallback(Token.Slot, sender, report);
        }

        private void HandleLifecycleAttention(
            in Switch2RuntimeRegistrationLifecycleAttention attention)
        {
            if (!attention.IsValid ||
                !attention.Registration.Equals(Token.Registration) ||
                Volatile.Read(ref attached) == 0)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref attentionQueued, 1, 0) != 0)
            {
                return;
            }
            TryScheduleAttentionRetirement();
        }
    }
}
