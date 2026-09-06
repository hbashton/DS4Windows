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

internal enum Switch2BluetoothRuntimeOwnerState : byte
{
    Invalid = 0,
    Created,
    Preparing,
    Prepared,
    Active,
    StopRequested,
    Stopped,
    Quarantined,
    Removed,
    AbortedUnpublished,
}

internal enum Switch2BluetoothRuntimeCreateFailureKind : byte
{
    None = 0,
    MissingDependency,
    InvalidArgument,
    MissingReleaseProof,
    RuntimeDeviceRejected,
    RegistrationRejected,
    SinkRejected,
    InputOwnerRejected,
    DescriptorBindRejected,
    PumpRejected,
    AttentionRejected,
    DependencyThrew,
    RollbackRejected,
    RollbackTimedOut,
}

internal readonly struct Switch2BluetoothRuntimeCreateFailure
{
    internal Switch2BluetoothRuntimeCreateFailure(
        Switch2BluetoothRuntimeCreateFailureKind kind,
        Switch2RuntimeInputDeviceCreateFailure runtimeFailure,
        InputControllerRegistrationFailure registrationFailure,
        Switch2BluetoothRuntimeSinkFailure sinkFailure,
        Switch2BluetoothInputStartFailure inputFailure,
        Switch2BluetoothInputDrainPumpFailure pumpFailure,
        Switch2BluetoothInputLeaseReleaseResult releaseResult,
        Switch2BluetoothRuntimeOwner quarantinedOwner)
    {
        Kind = kind;
        RuntimeFailure = runtimeFailure;
        RegistrationFailure = registrationFailure;
        SinkFailure = sinkFailure;
        InputFailure = inputFailure;
        PumpFailure = pumpFailure;
        ReleaseResult = releaseResult;
        QuarantinedOwner = quarantinedOwner;
    }

    internal Switch2BluetoothRuntimeCreateFailureKind Kind { get; }

    internal Switch2RuntimeInputDeviceCreateFailure RuntimeFailure { get; }

    internal InputControllerRegistrationFailure RegistrationFailure { get; }

    internal Switch2BluetoothRuntimeSinkFailure SinkFailure { get; }

    internal Switch2BluetoothInputStartFailure InputFailure { get; }

    internal Switch2BluetoothInputDrainPumpFailure PumpFailure { get; }

    internal Switch2BluetoothInputLeaseReleaseResult ReleaseResult { get; }

    internal Switch2BluetoothRuntimeOwner QuarantinedOwner { get; }

    internal bool RequiresQuarantine => QuarantinedOwner != null;
}

internal enum Switch2BluetoothRuntimePrepareFailure : byte
{
    None = 0,
    InvalidRegistration,
    InvalidSlotAdoptionCredential,
    InvalidTimeout,
    InvalidState,
    OperationAlreadyInProgress,
    RuntimeStartRejected,
    PumpStartRejected,
    CleanupRejected,
    QuarantineRequired,
}

internal enum Switch2BluetoothRuntimeSlotAdoptionFailure : byte
{
    None = 0,
    InvalidToken,
    InvalidState,
    OperationAlreadyInProgress,
    DifferentSlotAlreadyAdopted,
    QuarantineRequired,
}

internal enum Switch2BluetoothRuntimeCommitFailure : byte
{
    None = 0,
    InvalidCredential,
    InvalidActivationCommitCredential,
    StaleCredential,
    AlreadyConsumed,
    InvalidState,
    OperationAlreadyInProgress,
    InputCommitRejected,
    DependencyThrew,
    QuarantineRequired,
}

internal enum Switch2BluetoothRuntimeAbortFailure : byte
{
    None = 0,
    InvalidRegistration,
    InvalidCredential,
    StaleCredential,
    AlreadyConsumed,
    InvalidTimeout,
    InvalidState,
    OperationAlreadyInProgress,
    InputAbortRejected,
    PumpRejected,
    PumpTimedOut,
    LeaseReleaseRejected,
    LeaseReleaseTimedOut,
    RuntimeAbortRejected,
    DependencyThrew,
    QuarantineRequired,
}

internal enum Switch2BluetoothRuntimeRetirementArmFailure : byte
{
    None = 0,
    InvalidClaim,
    StaleClaim,
    InvalidState,
    DifferentClaimAlreadyArmed,
}

internal enum Switch2BluetoothRuntimeStopFailureKind : byte
{
    None = 0,
    InvalidOwner,
    InvalidTimeout,
    RetirementNotArmed,
    InvalidState,
    OperationAlreadyInProgress,
    CallbackActive,
    PumpRejected,
    PumpTimedOut,
    LeaseReleaseRejected,
    LeaseReleaseTimedOut,
    TerminalNotRequested,
    TerminalPublicationTimedOut,
    TerminalPublicationRejected,
    TerminalDeliveryRejected,
    DependencyThrew,
    QuarantineRequired,
}

internal readonly struct Switch2BluetoothRuntimeStopFailure
{
    internal Switch2BluetoothRuntimeStopFailure(
        Switch2BluetoothRuntimeStopFailureKind kind,
        Switch2BluetoothInputDrainPumpFailure pumpFailure,
        Switch2BluetoothInputLeaseReleaseResult releaseResult,
        Switch2BluetoothRuntimeSinkFailure sinkFailure)
    {
        Kind = kind;
        PumpFailure = pumpFailure;
        ReleaseResult = releaseResult;
        SinkFailure = sinkFailure;
    }

    internal Switch2BluetoothRuntimeStopFailureKind Kind { get; }

    internal Switch2BluetoothInputDrainPumpFailure PumpFailure { get; }

    internal Switch2BluetoothInputLeaseReleaseResult ReleaseResult { get; }

    internal Switch2BluetoothRuntimeSinkFailure SinkFailure { get; }
}

internal readonly struct Switch2BluetoothRuntimePrepareCredential :
    IEquatable<Switch2BluetoothRuntimePrepareCredential>
{
    private readonly Switch2BluetoothRuntimeOwner issuer;
    private readonly object fence;
    private readonly InputControllerSlotToken slotToken;

    internal Switch2BluetoothRuntimePrepareCredential(
        Switch2BluetoothRuntimeOwner issuer, object fence,
        in InputControllerSlotToken slotToken,
        Switch2ControllerModel model, ulong scanGeneration,
        ulong deviceGeneration, ulong transportGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        this.slotToken = slotToken;
        Model = model;
        ScanGeneration = scanGeneration;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
    }

    internal Switch2ControllerModel Model { get; }

    internal ulong ScanGeneration { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal object Fence => fence;

    internal bool IsValid => issuer != null && fence != null &&
        slotToken.IsValid && ScanGeneration != 0 && DeviceGeneration != 0 &&
        TransportGeneration != 0;

    internal bool Authenticates(Switch2BluetoothRuntimeOwner candidate,
        object expectedFence, in InputControllerSlotToken expectedSlotToken,
        Switch2ControllerModel expectedModel, ulong expectedScanGeneration,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration) =>
        ReferenceEquals(issuer, candidate) &&
        ReferenceEquals(fence, expectedFence) &&
        slotToken.Equals(expectedSlotToken) && Model == expectedModel &&
        ScanGeneration == expectedScanGeneration &&
        DeviceGeneration == expectedDeviceGeneration &&
        TransportGeneration == expectedTransportGeneration;

    public bool Equals(Switch2BluetoothRuntimePrepareCredential other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(fence, other.fence) &&
        slotToken.Equals(other.slotToken) && Model == other.Model &&
        ScanGeneration == other.ScanGeneration &&
        DeviceGeneration == other.DeviceGeneration &&
        TransportGeneration == other.TransportGeneration;

    public override bool Equals(object obj) => obj is
        Switch2BluetoothRuntimePrepareCredential other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : RuntimeHelpers.GetHashCode(issuer),
        fence == null ? 0 : RuntimeHelpers.GetHashCode(fence), slotToken,
        Model, ScanGeneration, DeviceGeneration, TransportGeneration);
}

/// <summary>
/// Owner-issued proof that this exact dormant composition owner adopted one
/// exact table slot. The private fence makes the first successful adoption the
/// sole authority for prepare or unpublished cleanup; a token from another
/// table cannot mutate the winning owner even when it carries the same
/// registration and colliding public generations.
/// </summary>
internal readonly struct Switch2BluetoothRuntimeSlotAdoptionCredential :
    IEquatable<Switch2BluetoothRuntimeSlotAdoptionCredential>
{
    private readonly Switch2BluetoothRuntimeOwner issuer;
    private readonly object fence;
    private readonly InputControllerSlotToken slotToken;

    internal Switch2BluetoothRuntimeSlotAdoptionCredential(
        Switch2BluetoothRuntimeOwner issuer, object fence,
        in InputControllerSlotToken slotToken,
        Switch2ControllerModel model, ulong scanGeneration,
        ulong deviceGeneration, ulong transportGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        this.slotToken = slotToken;
        Model = model;
        ScanGeneration = scanGeneration;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
    }

    internal Switch2ControllerModel Model { get; }

    internal ulong ScanGeneration { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal InputControllerSlotToken SlotToken => slotToken;

    internal bool IsValid => issuer != null && fence != null &&
        slotToken.IsValid && ScanGeneration != 0 && DeviceGeneration != 0 &&
        TransportGeneration != 0;

    internal bool Authenticates(Switch2BluetoothRuntimeOwner candidate,
        object expectedFence, in InputControllerSlotToken expectedSlotToken,
        Switch2ControllerModel expectedModel, ulong expectedScanGeneration,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration) =>
        ReferenceEquals(issuer, candidate) &&
        ReferenceEquals(fence, expectedFence) &&
        slotToken.Equals(expectedSlotToken) && Model == expectedModel &&
        ScanGeneration == expectedScanGeneration &&
        DeviceGeneration == expectedDeviceGeneration &&
        TransportGeneration == expectedTransportGeneration;

    public bool Equals(Switch2BluetoothRuntimeSlotAdoptionCredential other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(fence, other.fence) &&
        slotToken.Equals(other.slotToken) && Model == other.Model &&
        ScanGeneration == other.ScanGeneration &&
        DeviceGeneration == other.DeviceGeneration &&
        TransportGeneration == other.TransportGeneration;

    public override bool Equals(object obj) => obj is
        Switch2BluetoothRuntimeSlotAdoptionCredential other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : RuntimeHelpers.GetHashCode(issuer),
        fence == null ? 0 : RuntimeHelpers.GetHashCode(fence), slotToken,
        Model, ScanGeneration, DeviceGeneration, TransportGeneration);
}

internal sealed class Switch2BluetoothRuntimeLifecycleAttentionEventArgs :
    EventArgs
{
    internal Switch2BluetoothRuntimeLifecycleAttentionEventArgs(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, Switch2BluetoothInputEndReason endReason,
        Switch2BluetoothInputDrainPumpFailure pumpFailure,
        bool userDisconnectRequested = false)
    {
        Model = model;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        EndReason = endReason;
        PumpFailure = pumpFailure;
        UserDisconnectRequested = userDisconnectRequested;
    }

    internal Switch2ControllerModel Model { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal Switch2BluetoothInputEndReason EndReason { get; }

    internal Switch2BluetoothInputDrainPumpFailure PumpFailure { get; }

    internal bool UserDisconnectRequested { get; }
}

internal interface ISwitch2BluetoothRuntimeDrainPump
{
    Switch2BluetoothInputDrainPumpState State { get; }

    Switch2BluetoothInputDrainPumpFailure TerminalFailure { get; }

    bool RequiresQuarantine { get; }

    bool IsCurrentWorkerThread { get; }

    long PublishedCount { get; }

    bool TrySetLifecycleAttentionHandler(
        Action<Switch2BluetoothInputDrainPumpAttention> handler);

    bool TryStartParked(int timeoutMilliseconds,
        out Switch2BluetoothInputDrainPumpFailure failure);

    bool TryStopAndJoin(int timeoutMilliseconds,
        out Switch2BluetoothInputDrainPumpFailure failure);
}

internal sealed class Switch2BluetoothRuntimeDrainPump :
    ISwitch2BluetoothRuntimeDrainPump
{
    private readonly Switch2BluetoothInputDrainPump pump;

    internal Switch2BluetoothRuntimeDrainPump(
        Switch2BluetoothInputDrainPump pump)
    {
        this.pump = pump ?? throw new ArgumentNullException(nameof(pump));
    }

    public Switch2BluetoothInputDrainPumpState State => pump.State;

    public Switch2BluetoothInputDrainPumpFailure TerminalFailure =>
        pump.TerminalFailure;

    public bool RequiresQuarantine => pump.RequiresQuarantine;

    public bool IsCurrentWorkerThread => pump.IsCurrentWorkerThread;

    public long PublishedCount => pump.PublishedCount;

    public bool TrySetLifecycleAttentionHandler(
        Action<Switch2BluetoothInputDrainPumpAttention> handler) =>
        pump.TrySetLifecycleAttentionHandler(handler);

    public bool TryStartParked(int timeoutMilliseconds,
        out Switch2BluetoothInputDrainPumpFailure failure) =>
        pump.TryStartParked(timeoutMilliseconds, out failure);

    public bool TryStopAndJoin(int timeoutMilliseconds,
        out Switch2BluetoothInputDrainPumpFailure failure) =>
        pump.TryStopAndJoin(timeoutMilliseconds, out failure);
}

internal interface ISwitch2BluetoothRuntimeDrainPumpFactory
{
    bool TryCreate(Switch2BluetoothInputOwner inputOwner,
        out ISwitch2BluetoothRuntimeDrainPump pump,
        out Switch2BluetoothInputDrainPumpFailure failure);
}

internal sealed class Switch2BluetoothRuntimeDrainPumpFactory :
    ISwitch2BluetoothRuntimeDrainPumpFactory
{
    internal static readonly Switch2BluetoothRuntimeDrainPumpFactory Instance =
        new();

    private Switch2BluetoothRuntimeDrainPumpFactory()
    {
    }

    public bool TryCreate(Switch2BluetoothInputOwner inputOwner,
        out ISwitch2BluetoothRuntimeDrainPump pump,
        out Switch2BluetoothInputDrainPumpFailure failure)
    {
        pump = null;
        if (!Switch2BluetoothInputDrainPump.TryCreate(inputOwner,
                out Switch2BluetoothInputDrainPump concrete, out failure))
        {
            return false;
        }
        pump = new Switch2BluetoothRuntimeDrainPump(concrete);
        return true;
    }
}

/// <summary>
/// Dormant composition owner for one admitted Switch 2 Bluetooth Pro or
/// standalone Joy-Con lifetime. It creates no discovery, pairing, reconnect,
/// joined-pair, hardware-output, or production registration call site.
/// </summary>
internal sealed class Switch2BluetoothRuntimeOwner :
    IInputControllerRegistrationOwner
{
    internal const int MaximumTimeoutMilliseconds =
        InputControllerRegistration.MaximumStopTimeoutMilliseconds;

    private readonly object gate = new();
    private readonly object attentionGate = new();
    private readonly Switch2ControllerModel model;
    private readonly ulong scanGeneration;
    private readonly ulong deviceGeneration;
    private readonly ulong transportGeneration;
    private readonly ISwitch2BluetoothInputLease lease;
    private readonly ISwitch2BluetoothInputLeaseReleaseProof releaseProof;
    private readonly Switch2RuntimeInputDevice runtimeDevice;
    private readonly InputControllerRegistration registration;
    // Factory-staged dependencies. They are published only after the exact
    // registration, sink descriptor, input credential, pump, and attention
    // handler all exist. A failed setup can retain this partial graph solely
    // as a quarantined cleanup handle.
    private Switch2BluetoothRuntimeInputSink sink;
    private Switch2BluetoothInputOwner inputOwner;
    private Switch2BluetoothInputPrepareCredential inputCredential;
    private Switch2BluetoothRuntimeTerminalCredential terminalCredential;
    private ISwitch2BluetoothRuntimeDrainPump pump;
    private Switch2BluetoothFeedbackLifetime feedbackLifetime;
    private readonly Switch2BluetoothRuntimeLifecycleAttentionEventArgs
        disconnectedAttention;
    private readonly Switch2BluetoothRuntimeLifecycleAttentionEventArgs
        overflowAttention;
    private readonly Switch2BluetoothRuntimeLifecycleAttentionEventArgs
        sinkAttention;
    private readonly Switch2BluetoothRuntimeLifecycleAttentionEventArgs
        userDisconnectAttention;
    private readonly Switch2BluetoothRuntimeLifecycleAttentionEventArgs[]
        failureAttentions;

    private Switch2BluetoothRuntimeOwnerState state =
        Switch2BluetoothRuntimeOwnerState.Created;
    private Switch2BluetoothRuntimeStopFailure lastStopFailure;
    private Switch2BluetoothRuntimeAbortFailure lastAbortFailure;
    private object preparedFence;
    private bool preparedCredentialConsumed;
    private bool lifecycleOperationInProgress;
    private bool dependenciesComplete;
    private bool creationFailed;
    private bool requiresQuarantine;
    private bool leaseReleaseProven;
    private bool retirementArmed;
    private object slotAdoptionFence;
    private InputControllerSlotToken boundSlotToken;
    private InputControllerRetirementClaim retirementClaim;
    private int attentionRaised;
    private EventHandler<Switch2BluetoothRuntimeLifecycleAttentionEventArgs>
        attentionHandlers;

    // Construction before subscription needs an owner for registration. The
    // factory finalizes the staged dependency graph exactly once before escape.
    private Switch2BluetoothRuntimeOwner(Switch2ControllerModel model,
        ulong scanGeneration, ulong deviceGeneration,
        ulong transportGeneration, ISwitch2BluetoothInputLease lease,
        ISwitch2BluetoothInputLeaseReleaseProof releaseProof,
        Switch2RuntimeInputDevice runtimeDevice)
    {
        this.model = model;
        this.scanGeneration = scanGeneration;
        this.deviceGeneration = deviceGeneration;
        this.transportGeneration = transportGeneration;
        this.lease = lease;
        this.releaseProof = releaseProof;
        this.runtimeDevice = runtimeDevice;
        disconnectedAttention = new(model, deviceGeneration,
            transportGeneration, Switch2BluetoothInputEndReason.Disconnected,
            default);
        overflowAttention = new(model, deviceGeneration, transportGeneration,
            Switch2BluetoothInputEndReason.QueueOverflow, default);
        sinkAttention = new(model, deviceGeneration, transportGeneration,
            Switch2BluetoothInputEndReason.SinkFailure,
            Switch2BluetoothInputDrainPumpFailure.SinkRejected);
        userDisconnectAttention = new(model, deviceGeneration,
            transportGeneration, Switch2BluetoothInputEndReason.Stopped,
            default, userDisconnectRequested: true);
        failureAttentions = new Switch2BluetoothRuntimeLifecycleAttentionEventArgs[
            (int)Switch2BluetoothInputDrainPumpFailure.
                UnexpectedWorkerFailure + 1];
        for (int index = 1; index < failureAttentions.Length; index++)
        {
            failureAttentions[index] = new(model, deviceGeneration,
                transportGeneration, Switch2BluetoothInputEndReason.Stopped,
                (Switch2BluetoothInputDrainPumpFailure)index);
        }
        if (!InputControllerRegistration.TryCreate(runtimeDevice,
                deviceGeneration, InputControllerOwnershipKind.Switch2Runtime,
                hasHidInterface: false, hasPersistentIdentity: false, this,
                out registration, out InputControllerRegistrationFailure failure))
        {
            throw new InvalidOperationException(
                $"Bluetooth runtime registration rejected: {failure}.");
        }
        if (!runtimeDevice.TryBindBluetoothDisconnectRequest(
                TryRequestUserDisconnect))
        {
            throw new InvalidOperationException(
                "Bluetooth runtime disconnect lifecycle binding was rejected.");
        }
    }

    private Switch2BluetoothRuntimeInputSink ExactSink => sink;
    private Switch2BluetoothInputOwner ExactInputOwner => inputOwner;
    private Switch2BluetoothInputPrepareCredential ExactInputCredential =>
        inputCredential;
    private Switch2BluetoothRuntimeTerminalCredential ExactTerminalCredential =>
        terminalCredential;
    private ISwitch2BluetoothRuntimeDrainPump ExactPump => pump;

    internal InputControllerOwnershipKind Kind =>
        InputControllerOwnershipKind.Switch2Runtime;

    InputControllerOwnershipKind IInputControllerRegistrationOwner.Kind => Kind;

    internal Switch2ControllerModel Model => model;

    internal Switch2RuntimeInputDevice RuntimeDevice => runtimeDevice;

    internal InputControllerRegistration Registration => registration;

    internal Switch2BluetoothRuntimeInputSink Sink => ExactSink;

    internal Switch2BluetoothInputOwner InputOwner => ExactInputOwner;

    internal ISwitch2BluetoothRuntimeDrainPump DrainPump => ExactPump;

    internal Switch2BluetoothFeedbackLifetime FeedbackLifetime =>
        feedbackLifetime;

    internal Switch2BluetoothRuntimeOwnerState State
    {
        get { lock (gate) { return state; } }
    }

    internal bool RequiresQuarantine
    {
        get { lock (gate) { return requiresQuarantine; } }
    }

    internal bool LeaseReleaseProven
    {
        get { lock (gate) { return leaseReleaseProven; } }
    }

    internal bool DependenciesComplete
    {
        get { lock (gate) { return dependenciesComplete; } }
    }

    internal Switch2BluetoothRuntimeStopFailure LastStopFailure
    {
        get { lock (gate) { return lastStopFailure; } }
    }

    internal Switch2BluetoothRuntimeAbortFailure LastAbortFailure
    {
        get { lock (gate) { return lastAbortFailure; } }
    }

    internal event EventHandler<
        Switch2BluetoothRuntimeLifecycleAttentionEventArgs> LifecycleAttention
    {
        add { lock (attentionGate) { attentionHandlers += value; } }
        remove { lock (attentionGate) { attentionHandlers -= value; } }
    }

    internal static bool TryCreate(
        in Switch2BluetoothConnectionAdmission admission,
        ISwitch2BluetoothInputLease lease, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration, int queueCapacity,
        int lifecycleTimeoutMilliseconds,
        out Switch2BluetoothRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2BluetoothRuntimeCreateFailure failure) => TryCreateCore(
            admission, lease, deviceGeneration, transportGeneration,
            qpcFrequency, calibration, queueCapacity,
            lifecycleTimeoutMilliseconds,
            Switch2BluetoothRuntimeDrainPumpFactory.Instance,
            Switch2RuntimeTerminalScheduler.Instance, out owner,
            out registration, out failure);

    internal static bool TryCreateCore(
        in Switch2BluetoothConnectionAdmission admission,
        ISwitch2BluetoothInputLease lease, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration, int queueCapacity,
        int lifecycleTimeoutMilliseconds,
        ISwitch2BluetoothRuntimeDrainPumpFactory pumpFactory,
        ISwitch2RuntimeTerminalScheduler terminalScheduler,
        out Switch2BluetoothRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2BluetoothRuntimeCreateFailure failure)
    {
        owner = null;
        registration = default;
        if (lease == null || pumpFactory == null || terminalScheduler == null)
        {
            failure = CreateFailure(
                Switch2BluetoothRuntimeCreateFailureKind.MissingDependency);
            return false;
        }
        if (lease is not ISwitch2BluetoothInputLeaseReleaseProof releaseProof)
        {
            failure = CreateFailure(
                Switch2BluetoothRuntimeCreateFailureKind.MissingReleaseProof);
            return false;
        }
        if (!admission.IsValid || !lease.Admission.Equals(admission) ||
            deviceGeneration == 0 || transportGeneration == 0 ||
            qpcFrequency <= 0 || queueCapacity <= 0 ||
            queueCapacity > Switch2BluetoothInputOwner.MaximumQueueCapacity ||
            lifecycleTimeoutMilliseconds <= 0 ||
            lifecycleTimeoutMilliseconds > MaximumTimeoutMilliseconds ||
            !calibration.IsValid || calibration.Model != admission.Model ||
            calibration.DeviceGeneration != deviceGeneration)
        {
            failure = CreateFailure(
                Switch2BluetoothRuntimeCreateFailureKind.InvalidArgument);
            return false;
        }

        bool runtimeCreated = admission.Model ==
                Switch2ControllerModel.ProController2 ?
            Switch2RuntimeInputDevice.TryCreatePro(deviceGeneration,
                transportGeneration, Switch2Transport.BluetoothLe,
                out Switch2RuntimeInputDevice runtime,
                out Switch2RuntimeInputDeviceCreateFailure runtimeFailure) :
            Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(admission.Model,
                deviceGeneration, transportGeneration, out runtime,
                out runtimeFailure);
        if (!runtimeCreated)
        {
            failure = new Switch2BluetoothRuntimeCreateFailure(
                Switch2BluetoothRuntimeCreateFailureKind.RuntimeDeviceRejected,
                runtimeFailure, default, default, default, default, default,
                null);
            return false;
        }

        Switch2BluetoothRuntimeOwner candidate;
        try
        {
            candidate = new Switch2BluetoothRuntimeOwner(admission.Model,
                admission.ScanGeneration, deviceGeneration,
                transportGeneration, lease, releaseProof, runtime);
        }
        catch
        {
            failure = new Switch2BluetoothRuntimeCreateFailure(
                Switch2BluetoothRuntimeCreateFailureKind.RegistrationRejected,
                default, InputControllerRegistrationFailure.
                    OwnerAuthenticationFailed, default, default, default,
                default, null);
            return false;
        }

        if (!Switch2BluetoothRuntimeInputSink.TryCreateUnbound(admission.Model,
                deviceGeneration, transportGeneration, runtime,
                lifecycleTimeoutMilliseconds, terminalScheduler,
                out Switch2BluetoothRuntimeInputSink sink,
                out Switch2BluetoothRuntimeSinkBindingCredential bindCredential,
                out Switch2BluetoothRuntimeTerminalCredential terminalCredential,
                out Switch2BluetoothRuntimeSinkFailure sinkFailure))
        {
            failure = new Switch2BluetoothRuntimeCreateFailure(
                Switch2BluetoothRuntimeCreateFailureKind.SinkRejected,
                default, default, sinkFailure, default, default, default, null);
            return false;
        }
        candidate.sink = sink;
        candidate.terminalCredential = terminalCredential;

        bool inputPrepared;
        Switch2BluetoothInputOwner inputOwner;
        Switch2BluetoothInputPrepareCredential inputCredential;
        Switch2BluetoothInputStartFailure inputFailure;
        bool inputPrepareThrew = false;
        try
        {
            inputPrepared = Switch2BluetoothInputOwner.TryPrepare(admission,
                lease, sink, deviceGeneration, transportGeneration,
                qpcFrequency, calibration, queueCapacity, out inputOwner,
                out inputCredential, out inputFailure);
        }
        catch
        {
            inputPrepareThrew = true;
            inputPrepared = false;
            inputOwner = null;
            inputCredential = default;
            inputFailure = Switch2BluetoothInputStartFailure.
                SubscriptionInterrupted;
        }
        if (!inputPrepared)
        {
            bool consumed = inputFailure is
                Switch2BluetoothInputStartFailure.SubscriptionFailed or
                Switch2BluetoothInputStartFailure.SubscriptionInterrupted;
            if (consumed && !TryRollbackCreation(candidate,
                    lifecycleTimeoutMilliseconds,
                    requestUnsubscribe: inputPrepareThrew,
                    out Switch2BluetoothInputLeaseReleaseResult releaseResult))
            {
                candidate.MarkCreationQuarantined();
                owner = candidate;
                failure = new Switch2BluetoothRuntimeCreateFailure(
                    releaseResult ==
                            Switch2BluetoothInputLeaseReleaseResult.TimedOut ?
                        Switch2BluetoothRuntimeCreateFailureKind.RollbackTimedOut :
                        Switch2BluetoothRuntimeCreateFailureKind.RollbackRejected,
                    default, default, default, inputFailure, default,
                    releaseResult, candidate);
                return false;
            }
            runtime.TryAbortUnpublishedActivation();
            failure = new Switch2BluetoothRuntimeCreateFailure(
                Switch2BluetoothRuntimeCreateFailureKind.InputOwnerRejected,
                default, default, default, inputFailure, default, default,
                null);
            return false;
        }
        candidate.inputOwner = inputOwner;
        candidate.inputCredential = inputCredential;

        if (!sink.TryBindDescriptor(bindCredential, inputOwner.Descriptor,
                out sinkFailure))
        {
            return FailPostPrepareCreation(candidate, lifecycleTimeoutMilliseconds,
                Switch2BluetoothRuntimeCreateFailureKind.DescriptorBindRejected,
                sinkFailure, default, out owner, out registration, out failure);
        }

        ISwitch2BluetoothRuntimeDrainPump pump = null;
        bool pumpCreated;
        Switch2BluetoothInputDrainPumpFailure pumpFailure;
        bool pumpThrew = false;
        try
        {
            pumpCreated = pumpFactory.TryCreate(inputOwner, out pump,
                out pumpFailure);
        }
        catch
        {
            pumpCreated = false;
            pumpThrew = true;
            pumpFailure = Switch2BluetoothInputDrainPumpFailure.
                UnexpectedWorkerFailure;
        }
        // A rejecting factory may still return an attached cleanup handle.
        // Retain it before deciding whether normal construction succeeded.
        candidate.pump = pump;
        if (pumpThrew)
        {
            return RetainUncertainPumpCreation(candidate,
                lifecycleTimeoutMilliseconds, pumpFailure, out owner,
                out registration, out failure);
        }
        if (!pumpCreated || pump == null)
        {
            return FailPostPrepareCreation(candidate, lifecycleTimeoutMilliseconds,
                Switch2BluetoothRuntimeCreateFailureKind.PumpRejected,
                default, pumpFailure, out owner, out registration, out failure);
        }

        bool attentionInstalled;
        try
        {
            attentionInstalled = pump.TrySetLifecycleAttentionHandler(
                candidate.OnPumpAttention);
        }
        catch
        {
            attentionInstalled = false;
        }
        if (!attentionInstalled)
        {
            return FailPostPrepareCreation(candidate, lifecycleTimeoutMilliseconds,
                Switch2BluetoothRuntimeCreateFailureKind.AttentionRejected,
                default, Switch2BluetoothInputDrainPumpFailure.OwnerRejected,
                out owner, out registration, out failure);
        }

        if (lease is ISwitch2BluetoothHdRumbleBindableTransportLease
                outputLease && outputLease.HasHdRumbleOutput)
        {
            if (!Switch2BluetoothFeedbackLifetime.TryCreate(outputLease,
                    admission.Model, deviceGeneration, transportGeneration,
                    out Switch2BluetoothFeedbackLifetime feedback))
            {
                return FailPostPrepareCreation(candidate,
                    lifecycleTimeoutMilliseconds,
                    Switch2BluetoothRuntimeCreateFailureKind.DependencyThrew,
                    default,
                    Switch2BluetoothInputDrainPumpFailure.OwnerRejected,
                    out owner, out registration, out failure);
            }
            candidate.feedbackLifetime = feedback;
            if (!runtime.TryAttachBluetoothFeedbackLifetime(admission.Model,
                    deviceGeneration, transportGeneration, feedback))
            {
                return FailPostPrepareCreation(candidate,
                    lifecycleTimeoutMilliseconds,
                    Switch2BluetoothRuntimeCreateFailureKind.DependencyThrew,
                    default,
                    Switch2BluetoothInputDrainPumpFailure.OwnerRejected,
                    out owner, out registration, out failure);
            }
        }

        lock (candidate.gate)
        {
            candidate.dependenciesComplete = candidate.sink != null &&
                candidate.inputOwner != null && candidate.pump != null;
        }
        if (!candidate.dependenciesComplete)
        {
            return FailPostPrepareCreation(candidate,
                lifecycleTimeoutMilliseconds,
                Switch2BluetoothRuntimeCreateFailureKind.DependencyThrew,
                default, Switch2BluetoothInputDrainPumpFailure.OwnerRejected,
                out owner, out registration, out failure);
        }

        owner = candidate;
        registration = candidate.registration;
        failure = default;
        return true;
    }

    /// <summary>
    /// Atomically adopts the exact slot returned by a completed table bind.
    /// The first distinct token wins. Repeating that exact token before setup
    /// begins is idempotent and returns the same private-fenced credential.
    /// </summary>
    internal bool TryAdoptBoundSlot(
        in InputControllerSlotToken exactBoundSlotToken,
        out Switch2BluetoothRuntimeSlotAdoptionCredential credential,
        out Switch2BluetoothRuntimeSlotAdoptionFailure failure)
    {
        credential = default;
        if (!exactBoundSlotToken.IsValid ||
            !exactBoundSlotToken.Registration.Equals(registration) ||
            !ReferenceEquals(exactBoundSlotToken.Registration.Owner, this) ||
            !exactBoundSlotToken.Registration.IsOwnerAuthenticated)
        {
            failure = Switch2BluetoothRuntimeSlotAdoptionFailure.InvalidToken;
            return false;
        }

        lock (gate)
        {
            if (requiresQuarantine || creationFailed || !dependenciesComplete)
            {
                failure = Switch2BluetoothRuntimeSlotAdoptionFailure.
                    QuarantineRequired;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2BluetoothRuntimeSlotAdoptionFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            if (state != Switch2BluetoothRuntimeOwnerState.Created)
            {
                failure = Switch2BluetoothRuntimeSlotAdoptionFailure.
                    InvalidState;
                return false;
            }
            if (slotAdoptionFence != null)
            {
                if (!exactBoundSlotToken.Equals(boundSlotToken))
                {
                    failure = Switch2BluetoothRuntimeSlotAdoptionFailure.
                        DifferentSlotAlreadyAdopted;
                    return false;
                }
            }
            else
            {
                boundSlotToken = exactBoundSlotToken;
                slotAdoptionFence = new object();
            }

            credential = new Switch2BluetoothRuntimeSlotAdoptionCredential(
                this, slotAdoptionFence, boundSlotToken, model,
                scanGeneration, deviceGeneration, transportGeneration);
        }
        failure = Switch2BluetoothRuntimeSlotAdoptionFailure.None;
        return true;
    }

    internal bool TryPrepareActivation(
        in Switch2BluetoothRuntimeSlotAdoptionCredential adoptionCredential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimePrepareCredential credential,
        out Switch2BluetoothRuntimePrepareFailure failure)
    {
        credential = default;
        if (!adoptionCredential.IsValid ||
            !adoptionCredential.SlotToken.Registration.IsOwnerAuthenticated)
        {
            failure = Switch2BluetoothRuntimePrepareFailure.
                InvalidSlotAdoptionCredential;
            return false;
        }
        if (timeoutMilliseconds <= 0 ||
            timeoutMilliseconds > MaximumTimeoutMilliseconds)
        {
            failure = Switch2BluetoothRuntimePrepareFailure.InvalidTimeout;
            return false;
        }
        lock (gate)
        {
            if (!adoptionCredential.Authenticates(this, slotAdoptionFence,
                    boundSlotToken, model, scanGeneration, deviceGeneration,
                    transportGeneration))
            {
                failure = Switch2BluetoothRuntimePrepareFailure.
                    InvalidSlotAdoptionCredential;
                return false;
            }
            if (requiresQuarantine || creationFailed || !dependenciesComplete)
            {
                failure = Switch2BluetoothRuntimePrepareFailure.
                    QuarantineRequired;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2BluetoothRuntimePrepareFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            if (state != Switch2BluetoothRuntimeOwnerState.Created)
            {
                failure = Switch2BluetoothRuntimePrepareFailure.InvalidState;
                return false;
            }
            lifecycleOperationInProgress = true;
            state = Switch2BluetoothRuntimeOwnerState.Preparing;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        bool runtimeStarted;
        try
        {
            runtimeDevice.StartUpdate();
            runtimeStarted = runtimeDevice.RuntimeState ==
                Switch2RuntimeInputDeviceState.Active;
        }
        catch
        {
            runtimeStarted = false;
        }
        Switch2BluetoothInputDrainPumpFailure pumpFailure = default;
        bool pumpStarted = false;
        if (runtimeStarted)
        {
            try
            {
                int remaining = RemainingMilliseconds(deadline);
                pumpStarted = remaining != 0 && ExactPump.TryStartParked(
                    remaining, out pumpFailure);
            }
            catch
            {
                pumpFailure = Switch2BluetoothInputDrainPumpFailure.
                    UnexpectedWorkerFailure;
            }
        }
        if (!runtimeStarted || !pumpStarted)
        {
            bool cleaned = CleanupUnpublished(deadline,
                out Switch2BluetoothRuntimeAbortFailure cleanupFailure);
            lock (gate)
            {
                lifecycleOperationInProgress = false;
                if (cleaned)
                {
                    state = Switch2BluetoothRuntimeOwnerState.
                        AbortedUnpublished;
                }
                else
                {
                    requiresQuarantine = true;
                    state = Switch2BluetoothRuntimeOwnerState.Quarantined;
                    lastAbortFailure = cleanupFailure;
                }
                Monitor.PulseAll(gate);
            }
            failure = !cleaned ?
                Switch2BluetoothRuntimePrepareFailure.QuarantineRequired :
                !runtimeStarted ?
                    Switch2BluetoothRuntimePrepareFailure.RuntimeStartRejected :
                    Switch2BluetoothRuntimePrepareFailure.PumpStartRejected;
            return false;
        }

        object fence = new();
        lock (gate)
        {
            preparedFence = fence;
            preparedCredentialConsumed = false;
            lifecycleOperationInProgress = false;
            state = Switch2BluetoothRuntimeOwnerState.Prepared;
            Monitor.PulseAll(gate);
        }
        credential = new Switch2BluetoothRuntimePrepareCredential(this, fence,
            boundSlotToken, model, scanGeneration, deviceGeneration,
            transportGeneration);
        failure = Switch2BluetoothRuntimePrepareFailure.None;
        return true;
    }

    internal bool TryCommitPrepared(
        in Switch2BluetoothRuntimePrepareCredential credential,
        in InputControllerActivationCommitCredential activationCommit,
        out Switch2BluetoothRuntimeCommitFailure failure)
    {
        if (!credential.IsValid || !credential.Authenticates(this,
                preparedFence, boundSlotToken, model, scanGeneration,
                deviceGeneration, transportGeneration))
        {
            failure = Switch2BluetoothRuntimeCommitFailure.InvalidCredential;
            return false;
        }
        if (!activationCommit.Authenticates(boundSlotToken))
        {
            failure = Switch2BluetoothRuntimeCommitFailure.
                InvalidActivationCommitCredential;
            return false;
        }
        lock (gate)
        {
            if (!ReferenceEquals(preparedFence,
                    GetCredentialFence(credential)))
            {
                failure = Switch2BluetoothRuntimeCommitFailure.StaleCredential;
                return false;
            }
            if (preparedCredentialConsumed)
            {
                failure = Switch2BluetoothRuntimeCommitFailure.AlreadyConsumed;
                return false;
            }
            if (requiresQuarantine)
            {
                failure = Switch2BluetoothRuntimeCommitFailure.
                    QuarantineRequired;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2BluetoothRuntimeCommitFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            if (state != Switch2BluetoothRuntimeOwnerState.Prepared)
            {
                failure = Switch2BluetoothRuntimeCommitFailure.InvalidState;
                return false;
            }
            preparedCredentialConsumed = true;
            lifecycleOperationInProgress = true;
            state = Switch2BluetoothRuntimeOwnerState.Active;
        }

        bool committed;
        bool threw = false;
        try
        {
            committed = ExactInputOwner.TryCommitPrepared(ExactInputCredential,
                out _);
        }
        catch
        {
            committed = false;
            threw = true;
        }
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            if (!committed)
            {
                requiresQuarantine = true;
                state = Switch2BluetoothRuntimeOwnerState.Quarantined;
            }
            Monitor.PulseAll(gate);
        }
        failure = committed ? Switch2BluetoothRuntimeCommitFailure.None :
            threw ? Switch2BluetoothRuntimeCommitFailure.DependencyThrew :
            Switch2BluetoothRuntimeCommitFailure.QuarantineRequired;
        if (committed)
        {
            _ = runtimeDevice.TryStartConnectionHaptic();
        }
        return committed;
    }

    internal bool TryAbortPrepared(
        in Switch2BluetoothRuntimePrepareCredential credential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeAbortFailure failure)
    {
        if (!credential.IsValid || !credential.Authenticates(this,
                preparedFence, boundSlotToken, model, scanGeneration,
                deviceGeneration, transportGeneration))
        {
            failure = Switch2BluetoothRuntimeAbortFailure.InvalidCredential;
            return false;
        }
        return TryAbortCore(requireCredential: true,
            GetCredentialFence(credential), timeoutMilliseconds, out failure);
    }

    internal bool TryAbortUnpublished(
        in Switch2BluetoothRuntimeSlotAdoptionCredential adoptionCredential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeAbortFailure failure)
    {
        if (!adoptionCredential.IsValid)
        {
            failure = Switch2BluetoothRuntimeAbortFailure.InvalidCredential;
            return false;
        }
        lock (gate)
        {
            if (!adoptionCredential.Authenticates(this, slotAdoptionFence,
                    boundSlotToken, model, scanGeneration, deviceGeneration,
                    transportGeneration))
            {
                failure = Switch2BluetoothRuntimeAbortFailure.
                    InvalidCredential;
                return false;
            }
            if (state == Switch2BluetoothRuntimeOwnerState.AbortedUnpublished)
            {
                bool proof = dependenciesComplete && leaseReleaseProven &&
                    ExactPump.State ==
                        Switch2BluetoothInputDrainPumpState.Stopped &&
                    runtimeDevice.RuntimeState ==
                        Switch2RuntimeInputDeviceState.AbortedUnpublished;
                failure = proof ? Switch2BluetoothRuntimeAbortFailure.None :
                    Switch2BluetoothRuntimeAbortFailure.QuarantineRequired;
                return proof;
            }
        }
        if (!adoptionCredential.SlotToken.Registration.IsOwnerAuthenticated)
        {
            failure = Switch2BluetoothRuntimeAbortFailure.InvalidRegistration;
            return false;
        }
        return TryAbortCore(requireCredential: false, credentialFence: null,
            timeoutMilliseconds, out failure);
    }

    /// <summary>
    /// Releases a successfully-created owner when the registration table
    /// rejects it before a slot-adoption credential can exist (for example,
    /// because every slot became occupied). The exact owner registration is
    /// the sole authority; a copied generation or foreign owner is rejected.
    /// </summary>
    internal bool TryAbortCreated(
        in InputControllerRegistration exactRegistration,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeAbortFailure failure)
    {
        if (!exactRegistration.IsOwnerAuthenticated ||
            exactRegistration.OwnershipKind !=
                InputControllerOwnershipKind.Switch2Runtime ||
            !ReferenceEquals(exactRegistration.Owner, this) ||
            !ReferenceEquals(exactRegistration.Device, runtimeDevice) ||
            exactRegistration.Generation != runtimeDevice.RuntimeGeneration)
        {
            failure = Switch2BluetoothRuntimeAbortFailure.InvalidRegistration;
            return false;
        }

        return TryAbortCore(requireCredential: false, credentialFence: null,
            timeoutMilliseconds, out failure);
    }

    internal bool TryArmRetirement(in InputControllerRetirementClaim claim,
        out Switch2BluetoothRuntimeRetirementArmFailure failure)
    {
        if (!claim.IsValid || !claim.Token.Equals(boundSlotToken) ||
            !claim.Token.Registration.Equals(registration) ||
            !ReferenceEquals(claim.Token.Registration.Owner, this))
        {
            failure = Switch2BluetoothRuntimeRetirementArmFailure.InvalidClaim;
            return false;
        }
        lock (gate)
        {
            if (retirementArmed)
            {
                if (retirementClaim.Equals(claim))
                {
                    failure = Switch2BluetoothRuntimeRetirementArmFailure.None;
                    return true;
                }
                failure = Switch2BluetoothRuntimeRetirementArmFailure.
                    DifferentClaimAlreadyArmed;
                return false;
            }
            if (state is not (Switch2BluetoothRuntimeOwnerState.Active or
                    Switch2BluetoothRuntimeOwnerState.StopRequested or
                    Switch2BluetoothRuntimeOwnerState.Quarantined))
            {
                failure = Switch2BluetoothRuntimeRetirementArmFailure.
                    InvalidState;
                return false;
            }
            retirementArmed = true;
            retirementClaim = claim;
        }
        failure = Switch2BluetoothRuntimeRetirementArmFailure.None;
        return true;
    }

    public bool Authenticates(DS4Device device, ulong generation)
    {
        lock (gate)
        {
            return !creationFailed &&
                state is not (Switch2BluetoothRuntimeOwnerState.Removed or
                    Switch2BluetoothRuntimeOwnerState.AbortedUnpublished) &&
                ReferenceEquals(device, runtimeDevice) &&
                generation == deviceGeneration;
        }
    }

    public bool TryStopAndQuiesce(DS4Device device, ulong generation,
        int timeoutMilliseconds,
        out InputControllerOwnerOperationFailure failure)
    {
        if (!ReferenceEquals(device, runtimeDevice) ||
            generation != deviceGeneration)
        {
            SetStopFailure(new Switch2BluetoothRuntimeStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.InvalidOwner,
                default, default, default));
            failure = InputControllerOwnerOperationFailure.
                OwnerAuthenticationFailed;
            return false;
        }
        if (timeoutMilliseconds < 0 ||
            timeoutMilliseconds > MaximumTimeoutMilliseconds)
        {
            SetStopFailure(new Switch2BluetoothRuntimeStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.InvalidTimeout,
                default, default, default));
            failure = InputControllerOwnerOperationFailure.StopRejected;
            return false;
        }
        // A callback cannot join the stop that is waiting for that callback.
        // Independent callers can join the in-flight operation, including
        // while its terminal publication is running on another thread.
        if (pump?.IsCurrentWorkerThread == true ||
            sink?.IsCurrentPublicationThread == true ||
            sink?.IsCurrentTerminalPublicationThread == true)
        {
            SetStopFailure(new Switch2BluetoothRuntimeStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.CallbackActive,
                default, default, default));
            failure = InputControllerOwnerOperationFailure.StopRejected;
            return false;
        }
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        bool wasQuarantined;
        lock (gate)
        {
            if (creationFailed || !dependenciesComplete)
            {
                lastStopFailure = new Switch2BluetoothRuntimeStopFailure(
                    Switch2BluetoothRuntimeStopFailureKind.QuarantineRequired,
                    default, default, default);
                failure = InputControllerOwnerOperationFailure.StopRejected;
                return false;
            }
            if (!retirementArmed)
            {
                lastStopFailure = new Switch2BluetoothRuntimeStopFailure(
                    Switch2BluetoothRuntimeStopFailureKind.RetirementNotArmed,
                    default, default, default);
                failure = InputControllerOwnerOperationFailure.StopRejected;
                return false;
            }
            while (lifecycleOperationInProgress)
            {
                int remaining = RemainingMilliseconds(deadline);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    lastStopFailure = new Switch2BluetoothRuntimeStopFailure(
                        Switch2BluetoothRuntimeStopFailureKind.
                            OperationAlreadyInProgress,
                        default, default, default);
                    failure = InputControllerOwnerOperationFailure.StopRejected;
                    return false;
                }
            }
            if (state == Switch2BluetoothRuntimeOwnerState.Stopped)
            {
                failure = InputControllerOwnerOperationFailure.None;
                return true;
            }
            if (state is Switch2BluetoothRuntimeOwnerState.Prepared or
                Switch2BluetoothRuntimeOwnerState.Created or
                Switch2BluetoothRuntimeOwnerState.Preparing or
                Switch2BluetoothRuntimeOwnerState.Removed or
                Switch2BluetoothRuntimeOwnerState.AbortedUnpublished)
            {
                lastStopFailure = new Switch2BluetoothRuntimeStopFailure(
                    Switch2BluetoothRuntimeStopFailureKind.InvalidState,
                    default, default, default);
                failure = InputControllerOwnerOperationFailure.StopRejected;
                return false;
            }
            wasQuarantined = requiresQuarantine;
            lifecycleOperationInProgress = true;
            state = Switch2BluetoothRuntimeOwnerState.StopRequested;
        }

        if (ExactPump.IsCurrentWorkerThread ||
            ExactSink.PublicationInProgress ||
            ExactSink.TerminalPublicationInProgress)
        {
            lock (gate)
            {
                lifecycleOperationInProgress = false;
                state = Switch2BluetoothRuntimeOwnerState.StopRequested;
                lastStopFailure = new Switch2BluetoothRuntimeStopFailure(
                    Switch2BluetoothRuntimeStopFailureKind.CallbackActive,
                    default, default, default);
                Monitor.PulseAll(gate);
            }
            failure = InputControllerOwnerOperationFailure.StopRejected;
            return false;
        }

        bool stopped = TryStopCore(deadline,
            out Switch2BluetoothRuntimeStopFailure stopFailure);
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            if (!stopped || wasQuarantined)
            {
                requiresQuarantine = true;
                state = Switch2BluetoothRuntimeOwnerState.Quarantined;
                if (stopFailure.Kind !=
                    Switch2BluetoothRuntimeStopFailureKind.None)
                {
                    lastStopFailure = stopFailure;
                }
                else if (lastStopFailure.Kind ==
                    Switch2BluetoothRuntimeStopFailureKind.None)
                {
                    lastStopFailure = new Switch2BluetoothRuntimeStopFailure(
                        Switch2BluetoothRuntimeStopFailureKind.
                            QuarantineRequired,
                        default, default, default);
                }
            }
            else
            {
                state = Switch2BluetoothRuntimeOwnerState.Stopped;
            }
            Monitor.PulseAll(gate);
        }

        failure = stopped && !wasQuarantined ?
            InputControllerOwnerOperationFailure.None :
            InputControllerOwnerOperationFailure.StopRejected;
        return stopped && !wasQuarantined;
    }

    public bool TryRemove(DS4Device device, ulong generation,
        out InputControllerOwnerOperationFailure failure)
    {
        lock (gate)
        {
            if (!ReferenceEquals(device, runtimeDevice) ||
                generation != deviceGeneration)
            {
                failure = InputControllerOwnerOperationFailure.
                    OwnerAuthenticationFailed;
                return false;
            }
            if (lifecycleOperationInProgress || requiresQuarantine ||
                !retirementArmed ||
                state != Switch2BluetoothRuntimeOwnerState.Stopped ||
                !leaseReleaseProven || ExactSink.TerminalState !=
                    Switch2BluetoothRuntimeTerminalState.Delivered ||
                (feedbackLifetime != null && !feedbackLifetime.IsRetired) ||
                !runtimeDevice.TerminalNeutralCompleted ||
                !runtimeDevice.TerminalNeutralReported)
            {
                failure = InputControllerOwnerOperationFailure.RemoveRejected;
                return false;
            }
            state = Switch2BluetoothRuntimeOwnerState.Removed;
        }
        failure = InputControllerOwnerOperationFailure.None;
        return true;
    }

    private bool TryAbortCore(bool requireCredential, object credentialFence,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeAbortFailure failure)
    {
        if (timeoutMilliseconds < 0 ||
            timeoutMilliseconds > MaximumTimeoutMilliseconds)
        {
            failure = Switch2BluetoothRuntimeAbortFailure.InvalidTimeout;
            return false;
        }
        lock (gate)
        {
            if (requiresQuarantine)
            {
                failure = Switch2BluetoothRuntimeAbortFailure.
                    QuarantineRequired;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2BluetoothRuntimeAbortFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            if (requireCredential)
            {
                if (!ReferenceEquals(credentialFence, preparedFence))
                {
                    failure = Switch2BluetoothRuntimeAbortFailure.
                        StaleCredential;
                    return false;
                }
                if (preparedCredentialConsumed)
                {
                    failure = Switch2BluetoothRuntimeAbortFailure.
                        AlreadyConsumed;
                    return false;
                }
                if (state != Switch2BluetoothRuntimeOwnerState.Prepared)
                {
                    failure = Switch2BluetoothRuntimeAbortFailure.InvalidState;
                    return false;
                }
                preparedCredentialConsumed = true;
            }
            else if (state != Switch2BluetoothRuntimeOwnerState.Created)
            {
                failure = Switch2BluetoothRuntimeAbortFailure.InvalidState;
                return false;
            }
            lifecycleOperationInProgress = true;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        bool cleaned = CleanupUnpublished(deadline, out failure);
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastAbortFailure = failure;
            if (cleaned)
            {
                state = Switch2BluetoothRuntimeOwnerState.AbortedUnpublished;
            }
            else
            {
                requiresQuarantine = true;
                state = Switch2BluetoothRuntimeOwnerState.Quarantined;
            }
            Monitor.PulseAll(gate);
        }
        return cleaned;
    }

    private bool CleanupUnpublished(long deadline,
        out Switch2BluetoothRuntimeAbortFailure failure)
    {
        try
        {
            if (ExactInputOwner.IsPrepared &&
                !ExactInputOwner.TryAbortPrepared(ExactInputCredential, out _))
            {
                failure = Switch2BluetoothRuntimeAbortFailure.
                    InputAbortRejected;
                return false;
            }
            if (ExactInputOwner.ActivationCommitted)
            {
                failure = Switch2BluetoothRuntimeAbortFailure.InvalidState;
                return false;
            }

            int remaining = RemainingMilliseconds(deadline);
            if (!ExactPump.TryStopAndJoin(remaining,
                    out Switch2BluetoothInputDrainPumpFailure pumpFailure))
            {
                failure = pumpFailure ==
                        Switch2BluetoothInputDrainPumpFailure.WorkerExitTimedOut ?
                    Switch2BluetoothRuntimeAbortFailure.PumpTimedOut :
                    Switch2BluetoothRuntimeAbortFailure.PumpRejected;
                return false;
            }

            if (feedbackLifetime != null &&
                !feedbackLifetime.TryAbortUnpublished())
            {
                failure = Switch2BluetoothRuntimeAbortFailure.
                    RuntimeAbortRejected;
                return false;
            }

            remaining = RemainingMilliseconds(deadline);
            Switch2BluetoothInputLeaseReleaseResult release = releaseProof.
                WaitForRelease(transportGeneration, remaining);
            if (release != Switch2BluetoothInputLeaseReleaseResult.Released)
            {
                failure = release ==
                        Switch2BluetoothInputLeaseReleaseResult.TimedOut ?
                    Switch2BluetoothRuntimeAbortFailure.LeaseReleaseTimedOut :
                    Switch2BluetoothRuntimeAbortFailure.LeaseReleaseRejected;
                return false;
            }
            leaseReleaseProven = true;
            if (!runtimeDevice.TryAbortUnpublishedActivation())
            {
                failure = Switch2BluetoothRuntimeAbortFailure.
                    RuntimeAbortRejected;
                return false;
            }
        }
        catch
        {
            failure = Switch2BluetoothRuntimeAbortFailure.DependencyThrew;
            return false;
        }
        failure = Switch2BluetoothRuntimeAbortFailure.None;
        return true;
    }

    private bool TryStopCore(long deadline,
        out Switch2BluetoothRuntimeStopFailure failure)
    {
        bool disconnected = ExactInputOwner.EndReason ==
            Switch2BluetoothInputEndReason.Disconnected;
        int remaining = RemainingMilliseconds(deadline);
        if (!disconnected && feedbackLifetime != null &&
            (remaining <= 0 || !feedbackLifetime.TryStopAndRetire(
                maxAttempts: Math.Min(3, Math.Max(1, remaining / 100)))))
        {
            failure = new Switch2BluetoothRuntimeStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalDeliveryRejected,
                default, default,
                Switch2BluetoothRuntimeSinkFailure.
                    TerminalDeliveryRejected);
            return false;
        }

        try
        {
            ExactInputOwner.Stop();
        }
        catch
        {
            failure = new Switch2BluetoothRuntimeStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.DependencyThrew,
                default, default, default);
            return false;
        }

        remaining = RemainingMilliseconds(deadline);
        Switch2BluetoothInputDrainPumpFailure pumpFailure;
        try
        {
            if (!ExactPump.TryStopAndJoin(remaining, out pumpFailure))
            {
                failure = new Switch2BluetoothRuntimeStopFailure(
                    pumpFailure == Switch2BluetoothInputDrainPumpFailure.
                            WorkerExitTimedOut ?
                        Switch2BluetoothRuntimeStopFailureKind.PumpTimedOut :
                        Switch2BluetoothRuntimeStopFailureKind.PumpRejected,
                    pumpFailure, default, default);
                return false;
            }
        }
        catch
        {
            failure = new Switch2BluetoothRuntimeStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.DependencyThrew,
                default, default, default);
            return false;
        }

        remaining = RemainingMilliseconds(deadline);
        Switch2BluetoothInputLeaseReleaseResult releaseResult;
        try
        {
            releaseResult = releaseProof.WaitForRelease(transportGeneration,
                remaining);
        }
        catch
        {
            releaseResult = Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }
        if (releaseResult != Switch2BluetoothInputLeaseReleaseResult.Released)
        {
            failure = new Switch2BluetoothRuntimeStopFailure(
                releaseResult ==
                        Switch2BluetoothInputLeaseReleaseResult.TimedOut ?
                    Switch2BluetoothRuntimeStopFailureKind.
                        LeaseReleaseTimedOut :
                    Switch2BluetoothRuntimeStopFailureKind.
                        LeaseReleaseRejected,
                default, releaseResult, default);
            return false;
        }
        lock (gate)
        {
            leaseReleaseProven = true;
        }

        // Physical removal cannot deliver a new rumble Stop. Require the exact
        // lease's complete native release first, then retire locally without an
        // ACK. Virtual input still must publish/commit its terminal neutral.
        if (disconnected && feedbackLifetime != null &&
            !feedbackLifetime.TryRetireDisconnectedTarget())
        {
            failure = new Switch2BluetoothRuntimeStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.TerminalDeliveryRejected,
                default, releaseResult,
                Switch2BluetoothRuntimeSinkFailure.TerminalDeliveryRejected);
            return false;
        }

        if (!ExactSink.TerminalRequested)
        {
            failure = new Switch2BluetoothRuntimeStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.TerminalNotRequested,
                default, releaseResult,
                Switch2BluetoothRuntimeSinkFailure.TerminalNotRequested);
            return false;
        }
        remaining = RemainingMilliseconds(deadline);
        bool terminal;
        Switch2BluetoothRuntimeSinkFailure sinkFailure;
        try
        {
            terminal = ExactSink.TryCompleteTerminalNeutral(
                ExactTerminalCredential, remaining, out sinkFailure);
        }
        catch
        {
            terminal = false;
            sinkFailure = Switch2BluetoothRuntimeSinkFailure.DependencyThrew;
        }
        if (!terminal)
        {
            Switch2BluetoothRuntimeStopFailureKind kind = sinkFailure switch
            {
                Switch2BluetoothRuntimeSinkFailure.TerminalDeliveryTimedOut =>
                    Switch2BluetoothRuntimeStopFailureKind.
                        TerminalPublicationTimedOut,
                Switch2BluetoothRuntimeSinkFailure.TerminalDeliveryRejected =>
                    Switch2BluetoothRuntimeStopFailureKind.
                        TerminalDeliveryRejected,
                Switch2BluetoothRuntimeSinkFailure.TerminalNotRequested =>
                    Switch2BluetoothRuntimeStopFailureKind.TerminalNotRequested,
                Switch2BluetoothRuntimeSinkFailure.DependencyThrew =>
                    Switch2BluetoothRuntimeStopFailureKind.DependencyThrew,
                _ => Switch2BluetoothRuntimeStopFailureKind.
                    TerminalPublicationRejected,
            };
            failure = new Switch2BluetoothRuntimeStopFailure(kind, default,
                releaseResult, sinkFailure);
            return false;
        }
        failure = default;
        return true;
    }

    private void OnPumpAttention(
        Switch2BluetoothInputDrainPumpAttention evidence)
    {
        if (evidence == null || evidence.DeviceGeneration != deviceGeneration ||
            evidence.TransportGeneration != transportGeneration)
        {
            return;
        }
        Switch2BluetoothRuntimeLifecycleAttentionEventArgs exact =
            evidence.EndReason switch
            {
                Switch2BluetoothInputEndReason.Disconnected =>
                    disconnectedAttention,
                Switch2BluetoothInputEndReason.QueueOverflow =>
                    overflowAttention,
                Switch2BluetoothInputEndReason.SinkFailure => sinkAttention,
                _ => (int)evidence.PumpFailure > 0 &&
                    (int)evidence.PumpFailure < failureAttentions.Length ?
                    failureAttentions[(int)evidence.PumpFailure] : null,
            };
        if (exact == null || Interlocked.CompareExchange(ref attentionRaised,
                1, 0) != 0)
        {
            return;
        }
        lock (gate)
        {
            if (state == Switch2BluetoothRuntimeOwnerState.Active)
            {
                state = Switch2BluetoothRuntimeOwnerState.StopRequested;
            }
        }

        EventHandler<Switch2BluetoothRuntimeLifecycleAttentionEventArgs>
            handlers;
        lock (attentionGate)
        {
            handlers = attentionHandlers;
        }
        if (handlers == null)
        {
            return;
        }
        Delegate[] invocationList = handlers.GetInvocationList();
        for (int index = 0; index < invocationList.Length; index++)
        {
            try
            {
                ((EventHandler<
                    Switch2BluetoothRuntimeLifecycleAttentionEventArgs>)
                    invocationList[index])(this, exact);
            }
            catch
            {
            }
        }
    }

    private bool TryRequestUserDisconnect(ulong exactRuntimeGeneration)
    {
        if (exactRuntimeGeneration != registration.Generation)
        {
            return false;
        }

        EventHandler<Switch2BluetoothRuntimeLifecycleAttentionEventArgs>
            handlers;
        lock (attentionGate)
        {
            handlers = attentionHandlers;
        }
        if (handlers == null)
        {
            return false;
        }

        lock (gate)
        {
            if (state == Switch2BluetoothRuntimeOwnerState.StopRequested)
            {
                return true;
            }
            if (state != Switch2BluetoothRuntimeOwnerState.Active)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref attentionRaised, 1, 0) != 0)
            {
                return true;
            }
            state = Switch2BluetoothRuntimeOwnerState.StopRequested;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<
                    Switch2BluetoothRuntimeLifecycleAttentionEventArgs>)
                    handler)(this, userDisconnectAttention);
            }
            catch
            {
            }
        }
        return true;
    }

    private static bool FailPostPrepareCreation(
        Switch2BluetoothRuntimeOwner candidate, int timeoutMilliseconds,
        Switch2BluetoothRuntimeCreateFailureKind kind,
        Switch2BluetoothRuntimeSinkFailure sinkFailure,
        Switch2BluetoothInputDrainPumpFailure pumpFailure,
        out Switch2BluetoothRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2BluetoothRuntimeCreateFailure failure)
    {
        bool cleaned = TryRollbackCreation(candidate, timeoutMilliseconds,
            requestUnsubscribe: false,
            out Switch2BluetoothInputLeaseReleaseResult releaseResult);
        if (!cleaned)
        {
            candidate.MarkCreationQuarantined();
            owner = candidate;
            registration = default;
            failure = new Switch2BluetoothRuntimeCreateFailure(
                releaseResult ==
                        Switch2BluetoothInputLeaseReleaseResult.TimedOut ?
                    Switch2BluetoothRuntimeCreateFailureKind.RollbackTimedOut :
                    Switch2BluetoothRuntimeCreateFailureKind.RollbackRejected,
                default, default, sinkFailure, default, pumpFailure,
                releaseResult, candidate);
            return false;
        }
        owner = null;
        registration = default;
        failure = new Switch2BluetoothRuntimeCreateFailure(kind, default,
            default, sinkFailure, default, pumpFailure, releaseResult, null);
        return false;
    }

    private static bool RetainUncertainPumpCreation(
        Switch2BluetoothRuntimeOwner candidate, int timeoutMilliseconds,
        Switch2BluetoothInputDrainPumpFailure pumpFailure,
        out Switch2BluetoothRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2BluetoothRuntimeCreateFailure failure)
    {
        // A throwing factory may have attached or started work before throwing
        // without returning its cleanup handle. Perform every cleanup action we
        // can still authenticate, but never convert that ambiguity into proof
        // or let the retained graph escape as an operable owner.
        Switch2BluetoothInputLeaseReleaseResult releaseResult =
            Switch2BluetoothInputLeaseReleaseResult.Rejected;
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        try
        {
            if (candidate.inputOwner?.IsPrepared == true)
            {
                candidate.inputOwner.TryAbortPrepared(
                    candidate.inputCredential, out _);
            }
            releaseResult = candidate.releaseProof.WaitForRelease(
                candidate.transportGeneration,
                RemainingMilliseconds(deadline));
            candidate.runtimeDevice.TryAbortUnpublishedActivation();
        }
        catch
        {
            releaseResult = Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }

        candidate.MarkCreationQuarantined();
        owner = candidate;
        registration = default;
        failure = new Switch2BluetoothRuntimeCreateFailure(
            Switch2BluetoothRuntimeCreateFailureKind.DependencyThrew,
            default, default, default, default, pumpFailure, releaseResult,
            candidate);
        return false;
    }

    private static bool TryRollbackCreation(
        Switch2BluetoothRuntimeOwner candidate, int timeoutMilliseconds,
        bool requestUnsubscribe,
        out Switch2BluetoothInputLeaseReleaseResult releaseResult)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        try
        {
            if (candidate.ExactInputOwner != null)
            {
                if (candidate.ExactInputOwner.IsPrepared &&
                    !candidate.ExactInputOwner.TryAbortPrepared(
                        candidate.ExactInputCredential, out _))
                {
                    releaseResult =
                        Switch2BluetoothInputLeaseReleaseResult.Rejected;
                    return false;
                }
                if (candidate.ExactPump != null &&
                    !candidate.ExactPump.TryStopAndJoin(
                        RemainingMilliseconds(deadline), out _))
                {
                    releaseResult =
                        Switch2BluetoothInputLeaseReleaseResult.TimedOut;
                    return false;
                }
            }
            else if (requestUnsubscribe)
            {
                candidate.lease.TryUnsubscribeCccdNone(
                    candidate.transportGeneration);
            }

            if (candidate.feedbackLifetime != null &&
                !candidate.feedbackLifetime.TryAbortUnpublished())
            {
                releaseResult =
                    Switch2BluetoothInputLeaseReleaseResult.Rejected;
                return false;
            }

            releaseResult = candidate.releaseProof.WaitForRelease(
                candidate.transportGeneration, RemainingMilliseconds(deadline));
            if (releaseResult != Switch2BluetoothInputLeaseReleaseResult.Released)
            {
                return false;
            }
            candidate.leaseReleaseProven = true;
            return candidate.runtimeDevice.TryAbortUnpublishedActivation();
        }
        catch
        {
            releaseResult = Switch2BluetoothInputLeaseReleaseResult.Rejected;
            return false;
        }
    }

    private void MarkCreationQuarantined()
    {
        lock (gate)
        {
            creationFailed = true;
            requiresQuarantine = true;
            state = Switch2BluetoothRuntimeOwnerState.Quarantined;
        }
    }

    private void SetStopFailure(in Switch2BluetoothRuntimeStopFailure failure)
    {
        lock (gate)
        {
            lastStopFailure = failure;
        }
    }

    private static object GetCredentialFence(
        in Switch2BluetoothRuntimePrepareCredential credential)
    {
        // Equality/authentication above is reference-fenced. This reflection-free
        // helper is replaced by the credential's private internal accessor below.
        return credential.Fence;
    }

    private static Switch2BluetoothRuntimeCreateFailure CreateFailure(
        Switch2BluetoothRuntimeCreateFailureKind kind) => new(kind, default,
            default, default, default, default, default, null);

    private static int RemainingMilliseconds(long deadline)
    {
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 : (int)Math.Min(remaining, int.MaxValue);
    }
}
