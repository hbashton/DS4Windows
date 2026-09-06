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

public delegate void Switch2RuntimeMappingCallback(int slot,
    DS4Device sender, Switch2RuntimeReportEventArgs report);

public enum Switch2ProUsbRuntimeRegistrationFailureKind : byte
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

public readonly struct Switch2ProUsbRuntimeRegistrationFailure
{
    internal Switch2ProUsbRuntimeRegistrationFailure(
        Switch2ProUsbRuntimeRegistrationFailureKind kind,
        InputControllerSlotTableFailure tableFailure = default,
        Switch2ProUsbRuntimePrepareFailure prepareFailure = default,
        Switch2ProUsbRuntimeCommitFailure commitFailure = default,
        Switch2ProUsbRuntimeUnpublishedAbortFailure abortFailure = default,
        InputControllerOwnerOperationFailure ownerFailure = default,
        InputControllerSlotQuarantineReason quarantineReason = default)
    {
        Kind = kind;
        TableFailure = tableFailure;
        PrepareFailure = prepareFailure;
        CommitFailure = commitFailure;
        AbortFailure = abortFailure;
        OwnerFailure = ownerFailure;
        QuarantineReason = quarantineReason;
    }

    public Switch2ProUsbRuntimeRegistrationFailureKind Kind { get; }

    public InputControllerSlotTableFailure TableFailure { get; }

    public Switch2ProUsbRuntimePrepareFailure PrepareFailure { get; }

    public Switch2ProUsbRuntimeCommitFailure CommitFailure { get; }

    public Switch2ProUsbRuntimeUnpublishedAbortFailure AbortFailure { get; }

    public InputControllerOwnerOperationFailure OwnerFailure { get; }

    public InputControllerSlotQuarantineReason QuarantineReason { get; }

    public bool RequiresQuarantine => Kind ==
            Switch2ProUsbRuntimeRegistrationFailureKind.QuarantineRequired ||
        QuarantineReason != InputControllerSlotQuarantineReason.None;

    public bool IsNone => Kind ==
        Switch2ProUsbRuntimeRegistrationFailureKind.None;
}

/// <summary>
/// Dormant USB compatibility facade over the single transport-neutral Switch 2
/// registration transaction core. It performs no ControlService, discovery,
/// hardware, output, profile, or virtual-device registration.
/// </summary>
public sealed class Switch2ProUsbRuntimeRegistrationCoordinator
{
    private readonly Switch2RuntimeRegistrationTransactionCore core;
    // Retained as an exact alias for the existing concurrency
    // characterization. The core is the sole owner of this gate.
    private readonly object lifecycleGate;
    private readonly Func<Switch2ProUsbRuntimeOwner,
        ISwitch2RuntimeRegistrationParticipant> participantFactory;

    public Switch2ProUsbRuntimeRegistrationCoordinator(
        InputControllerRegistrationTable table,
        int lifecycleAttentionTimeoutMilliseconds = 5_000)
        : this(table, lifecycleAttentionTimeoutMilliseconds,
            static owner =>
                new Switch2ProUsbRuntimeRegistrationParticipant(owner))
    {
    }

    internal Switch2ProUsbRuntimeRegistrationCoordinator(
        InputControllerRegistrationTable table,
        int lifecycleAttentionTimeoutMilliseconds,
        Func<Switch2ProUsbRuntimeOwner,
            ISwitch2RuntimeRegistrationParticipant> participantFactory)
    {
        this.participantFactory = participantFactory ??
            throw new ArgumentNullException(nameof(participantFactory));
        core = new Switch2RuntimeRegistrationTransactionCore(table,
            lifecycleAttentionTimeoutMilliseconds);
        lifecycleGate = core.LifecycleGate;
    }

    public InputControllerRegistrationTable Table => core.Table;

    public bool TryOpen(ulong exactServiceGeneration,
        out Switch2ProUsbRuntimeRegistrationFailure failure)
    {
        bool succeeded = core.TryOpen(exactServiceGeneration,
            out Switch2RuntimeRegistrationTransactionFailure coreFailure);
        failure = Map(coreFailure);
        return succeeded;
    }

    public bool TryAttach(Switch2ProUsbRuntimeOwner owner,
        Switch2RuntimeMappingCallback mappingCallback,
        int timeoutMilliseconds, out InputControllerSlotToken token,
        out Switch2ProUsbRuntimeRegistrationFailure failure)
    {
        token = default;
        if (owner == null || mappingCallback == null)
        {
            failure = new Switch2ProUsbRuntimeRegistrationFailure(
                Switch2ProUsbRuntimeRegistrationFailureKind.InvalidArgument);
            return false;
        }

        InputControllerRegistration registration = owner.Registration;
        if (!ReferenceEquals(registration.Owner, owner) ||
            !ReferenceEquals(registration.Device,
                owner.RuntimeInputDevice) ||
            registration.Generation != owner.RuntimeInputDevice.
                RuntimeGeneration)
        {
            failure = new Switch2ProUsbRuntimeRegistrationFailure(
                Switch2ProUsbRuntimeRegistrationFailureKind.InvalidArgument);
            return false;
        }

        bool succeeded = core.TryAttach(registration,
            () => participantFactory(owner), mappingCallback,
            timeoutMilliseconds, out token,
            out Switch2RuntimeRegistrationTransactionFailure coreFailure);
        failure = Map(coreFailure);
        return succeeded;
    }

    public bool TryRemove(InputControllerSlotToken token,
        int timeoutMilliseconds,
        out Switch2ProUsbRuntimeRegistrationFailure failure)
    {
        bool succeeded = core.TryRemove(token, timeoutMilliseconds,
            out Switch2RuntimeRegistrationTransactionFailure coreFailure);
        failure = Map(coreFailure);
        return succeeded;
    }

    public bool TryClose(ulong exactServiceGeneration,
        int timeoutMilliseconds,
        out Switch2ProUsbRuntimeRegistrationFailure failure)
    {
        bool succeeded = core.TryClose(exactServiceGeneration,
            timeoutMilliseconds,
            out Switch2RuntimeRegistrationTransactionFailure coreFailure);
        failure = Map(coreFailure);
        return succeeded;
    }

    private static Switch2ProUsbRuntimeRegistrationFailure Map(
        in Switch2RuntimeRegistrationTransactionFailure failure)
    {
        var usb = failure.Participant as
            Switch2ProUsbRuntimeRegistrationParticipant;
        return new Switch2ProUsbRuntimeRegistrationFailure(
            MapKind(failure.Kind),
            failure.TableFailure,
            usb?.LastPrepareFailure ?? default,
            usb?.LastCommitFailure ?? default,
            usb?.LastAbortFailure ?? default,
            failure.OwnerFailure, failure.QuarantineReason);
    }

    private static Switch2ProUsbRuntimeRegistrationFailureKind MapKind(
        Switch2RuntimeRegistrationTransactionFailureKind kind) => kind switch
    {
        Switch2RuntimeRegistrationTransactionFailureKind.None =>
            Switch2ProUsbRuntimeRegistrationFailureKind.None,
        Switch2RuntimeRegistrationTransactionFailureKind.InvalidArgument =>
            Switch2ProUsbRuntimeRegistrationFailureKind.InvalidArgument,
        Switch2RuntimeRegistrationTransactionFailureKind.InvalidTimeout =>
            Switch2ProUsbRuntimeRegistrationFailureKind.InvalidTimeout,
        Switch2RuntimeRegistrationTransactionFailureKind.TableRejected =>
            Switch2ProUsbRuntimeRegistrationFailureKind.TableRejected,
        Switch2RuntimeRegistrationTransactionFailureKind.StaleToken =>
            Switch2ProUsbRuntimeRegistrationFailureKind.StaleToken,
        Switch2RuntimeRegistrationTransactionFailureKind.
                SubscriptionRejected =>
            Switch2ProUsbRuntimeRegistrationFailureKind.SubscriptionRejected,
        Switch2RuntimeRegistrationTransactionFailureKind.PrepareRejected =>
            Switch2ProUsbRuntimeRegistrationFailureKind.PrepareRejected,
        Switch2RuntimeRegistrationTransactionFailureKind.
                UnpublishedAbortRejected =>
            Switch2ProUsbRuntimeRegistrationFailureKind.
                UnpublishedAbortRejected,
        Switch2RuntimeRegistrationTransactionFailureKind.CommitRejected =>
            Switch2ProUsbRuntimeRegistrationFailureKind.CommitRejected,
        Switch2RuntimeRegistrationTransactionFailureKind.ReentrantRemoval =>
            Switch2ProUsbRuntimeRegistrationFailureKind.ReentrantRemoval,
        Switch2RuntimeRegistrationTransactionFailureKind.CallbackActive =>
            Switch2ProUsbRuntimeRegistrationFailureKind.CallbackActive,
        Switch2RuntimeRegistrationTransactionFailureKind.DrainTimedOut =>
            Switch2ProUsbRuntimeRegistrationFailureKind.DrainTimedOut,
        Switch2RuntimeRegistrationTransactionFailureKind.StopRejected =>
            Switch2ProUsbRuntimeRegistrationFailureKind.StopRejected,
        Switch2RuntimeRegistrationTransactionFailureKind.
                TerminalNeutralRejected =>
            Switch2ProUsbRuntimeRegistrationFailureKind.
                TerminalNeutralRejected,
        Switch2RuntimeRegistrationTransactionFailureKind.RemoveRejected =>
            Switch2ProUsbRuntimeRegistrationFailureKind.RemoveRejected,
        Switch2RuntimeRegistrationTransactionFailureKind.DependencyThrew =>
            Switch2ProUsbRuntimeRegistrationFailureKind.DependencyThrew,
        Switch2RuntimeRegistrationTransactionFailureKind.
                QuarantineRequired =>
            Switch2ProUsbRuntimeRegistrationFailureKind.QuarantineRequired,
        Switch2RuntimeRegistrationTransactionFailureKind.
                SlotAdoptionRejected =>
            Switch2ProUsbRuntimeRegistrationFailureKind.SlotAdoptionRejected,
        _ => Switch2ProUsbRuntimeRegistrationFailureKind.DependencyThrew,
    };
}
