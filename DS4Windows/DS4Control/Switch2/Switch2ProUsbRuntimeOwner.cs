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
using System.Threading.Tasks;

namespace DS4Windows.Switch2;

public enum Switch2ProUsbRuntimeOwnerState : byte
{
    Invalid = 0,
    Created,
    Activating,
    Active,
    StopRequested,
    Stopped,
    Quarantined,
    Removed,
    Prepared,
    AbortedUnpublished,
}

public enum Switch2ProUsbRuntimeCreateFailureKind : byte
{
    None = 0,
    MissingDependency,
    InvalidGeneration,
    InvalidClock,
    InvalidCalibration,
    InvalidReadRetirementTimeout,
    RuntimeDeviceRejected,
    RegistrationRejected,
    TransportRejected,
    PumpRejected,
    DependencyThrew,
    RollbackRejected,
    RollbackTimedOut,
    InputAdoptionRejected,
}

/// <summary>
/// Exact creation evidence. Quarantine may retain either this composition
/// owner after a later rollback failure or a rejected partially-open native
/// lease through <see cref="TransportFailure"/>. The caller must retain the
/// reported owner; it is never a successfully activatable controller.
/// </summary>
public readonly struct Switch2ProUsbRuntimeCreateFailure
{
    internal Switch2ProUsbRuntimeCreateFailure(
        Switch2ProUsbRuntimeCreateFailureKind kind,
        Switch2RuntimeInputDeviceCreateFailure runtimeDeviceFailure,
        InputControllerRegistrationFailure registrationFailure,
        Switch2ProUsbTransportCreateFailure transportFailure,
        Switch2ProUsbInputReadPumpFailure pumpFailure,
        Switch2ProUsbDisposeFailure rollbackDisposeFailure,
        Switch2ProUsbRuntimeOwner quarantinedOwner)
    {
        Kind = kind;
        RuntimeDeviceFailure = runtimeDeviceFailure;
        RegistrationFailure = registrationFailure;
        TransportFailure = transportFailure;
        PumpFailure = pumpFailure;
        RollbackDisposeFailure = rollbackDisposeFailure;
        QuarantinedOwner = quarantinedOwner;
    }

    public Switch2ProUsbRuntimeCreateFailureKind Kind { get; }

    public Switch2RuntimeInputDeviceCreateFailure RuntimeDeviceFailure
    {
        get;
    }

    public InputControllerRegistrationFailure RegistrationFailure { get; }

    public Switch2ProUsbTransportCreateFailure TransportFailure { get; }

    public Switch2ProUsbInputReadPumpFailure PumpFailure { get; }

    public Switch2ProUsbDisposeFailure RollbackDisposeFailure { get; }

    public Switch2ProUsbRuntimeOwner QuarantinedOwner { get; }

    public bool RequiresQuarantine => QuarantinedOwner != null ||
        TransportFailure.RequiresQuarantine;

    public bool IsNone => Kind == Switch2ProUsbRuntimeCreateFailureKind.None;
}

public enum Switch2ProUsbRuntimeActivationFailure : byte
{
    None = 0,
    InvalidRegistration,
    OwnerAuthenticationFailed,
    InvalidState,
    OperationAlreadyInProgress,
    RuntimeStartRejected,
    PumpStartRejected,
    InitialPublicationRejected,
    RollbackRejected,
    QuarantineRequired,
}

public enum Switch2ProUsbRuntimePrepareFailure : byte
{
    None = 0,
    InvalidRegistration,
    InvalidSlotAdoptionCredential,
    OwnerAuthenticationFailed,
    InvalidTimeout,
    InvalidState,
    OperationAlreadyInProgress,
    RuntimeArmRejected,
    PumpPrepareRejected,
    PumpPrepareTimedOut,
    CleanupRejected,
    QuarantineRequired,
}

internal enum Switch2ProUsbRuntimeSlotAdoptionFailure : byte
{
    None = 0,
    InvalidToken,
    InvalidState,
    OperationAlreadyInProgress,
    DifferentSlotAlreadyAdopted,
    QuarantineRequired,
}

public enum Switch2ProUsbRuntimeCommitFailure : byte
{
    None = 0,
    InvalidCredential,
    StaleCredential,
    AlreadyConsumed,
    InvalidState,
    OperationAlreadyInProgress,
    PumpCommitRejected,
    QuarantineRequired,
    InvalidTimeout,
}

public enum Switch2ProUsbRuntimeUnpublishedAbortFailure : byte
{
    None = 0,
    InvalidRegistration,
    InvalidCredential,
    StaleCredential,
    AlreadyConsumed,
    InvalidTimeout,
    InvalidState,
    OperationAlreadyInProgress,
    PumpAbortRejected,
    PumpAbortTimedOut,
    ReadAlreadyStarted,
    RuntimeAbortRejected,
    DependencyThrew,
    QuarantineRequired,
}

/// <summary>
/// Exact single-use proof that one owner armed its runtime and parked its
/// worker before any native read. Copies authenticate the same capability;
/// whichever exact copy wins commit/abort consumes all of them.
/// </summary>
public readonly struct Switch2ProUsbRuntimePrepareCredential :
    IEquatable<Switch2ProUsbRuntimePrepareCredential>
{
    private readonly Switch2ProUsbRuntimeOwner issuer;
    private readonly object fence;

    internal Switch2ProUsbRuntimePrepareCredential(
        Switch2ProUsbRuntimeOwner issuer, object fence,
        ulong runtimeGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        RuntimeGeneration = runtimeGeneration;
    }

    public ulong RuntimeGeneration { get; }

    public bool IsValid => issuer != null && fence != null &&
        RuntimeGeneration != 0;

    internal Switch2ProUsbRuntimeOwner Issuer => issuer;

    internal object Fence => fence;

    public bool Equals(Switch2ProUsbRuntimePrepareCredential other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(fence, other.fence) &&
        RuntimeGeneration == other.RuntimeGeneration;

    public override bool Equals(object obj) => obj is
        Switch2ProUsbRuntimePrepareCredential other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 :
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(issuer),
        fence == null ? 0 :
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fence),
        RuntimeGeneration);

    public static bool operator ==(
        Switch2ProUsbRuntimePrepareCredential left,
        Switch2ProUsbRuntimePrepareCredential right) => left.Equals(right);

    public static bool operator !=(
        Switch2ProUsbRuntimePrepareCredential left,
        Switch2ProUsbRuntimePrepareCredential right) => !left.Equals(right);
}

/// <summary>
/// Owner-issued proof that one exact table slot won this USB composition
/// lifetime. The private fence makes the first distinct table/token the sole
/// authority for prepare and pre-prepare cleanup; exact retries are
/// idempotent and foreign-table losers cannot mutate the winner.
/// </summary>
internal readonly struct Switch2ProUsbRuntimeSlotAdoptionCredential :
    IEquatable<Switch2ProUsbRuntimeSlotAdoptionCredential>
{
    private readonly Switch2ProUsbRuntimeOwner issuer;
    private readonly object fence;
    private readonly InputControllerSlotToken slotToken;

    internal Switch2ProUsbRuntimeSlotAdoptionCredential(
        Switch2ProUsbRuntimeOwner issuer, object fence,
        in InputControllerSlotToken slotToken, ulong runtimeGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        this.slotToken = slotToken;
        RuntimeGeneration = runtimeGeneration;
    }

    internal InputControllerSlotToken SlotToken => slotToken;

    internal ulong RuntimeGeneration { get; }

    internal bool IsValid => issuer != null && fence != null &&
        slotToken.IsValid && RuntimeGeneration != 0;

    internal bool Authenticates(Switch2ProUsbRuntimeOwner candidate,
        object expectedFence, in InputControllerSlotToken expectedSlotToken,
        ulong expectedRuntimeGeneration) =>
        ReferenceEquals(issuer, candidate) &&
        ReferenceEquals(fence, expectedFence) &&
        slotToken.Equals(expectedSlotToken) &&
        RuntimeGeneration == expectedRuntimeGeneration;

    public bool Equals(Switch2ProUsbRuntimeSlotAdoptionCredential other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(fence, other.fence) &&
        slotToken.Equals(other.slotToken) &&
        RuntimeGeneration == other.RuntimeGeneration;

    public override bool Equals(object obj) => obj is
        Switch2ProUsbRuntimeSlotAdoptionCredential other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 :
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(issuer),
        fence == null ? 0 :
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fence),
        slotToken, RuntimeGeneration);

    public static bool operator ==(
        Switch2ProUsbRuntimeSlotAdoptionCredential left,
        Switch2ProUsbRuntimeSlotAdoptionCredential right) =>
        left.Equals(right);

    public static bool operator !=(
        Switch2ProUsbRuntimeSlotAdoptionCredential left,
        Switch2ProUsbRuntimeSlotAdoptionCredential right) =>
        !left.Equals(right);
}

public enum Switch2ProUsbRuntimeInputFailure : byte
{
    None = 0,
    LifecycleClosed,
    PublicationAlreadyInProgress,
    StaleDeviceGeneration,
    StaleTransportGeneration,
    ModelMismatch,
    TransportMismatch,
    ProfileMappingRejected,
    PublicationAdmissionTimedOut,
    RuntimeLifecycleClosed,
    RuntimeFrameRejected,
    RuntimePublicationRejected,
    DependencyThrew,
}

public enum Switch2ProUsbRuntimeLifecycleAttentionKind : byte
{
    Invalid = 0,
    InputRejected,
    SubscriberRejected,
    NativeReadFailure,
}

/// <summary>
/// Immutable, preallocated evidence that one exact runtime generation needs
/// service-owned retirement. It is a one-shot wake-up, not teardown proof.
/// </summary>
public sealed class Switch2ProUsbRuntimeLifecycleAttentionEventArgs :
    EventArgs
{
    internal Switch2ProUsbRuntimeLifecycleAttentionEventArgs(
        Switch2ProUsbRuntimeLifecycleAttentionKind kind,
        ulong runtimeGeneration)
    {
        if (kind is not (Switch2ProUsbRuntimeLifecycleAttentionKind.
                InputRejected or
            Switch2ProUsbRuntimeLifecycleAttentionKind.SubscriberRejected or
            Switch2ProUsbRuntimeLifecycleAttentionKind.NativeReadFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (runtimeGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeGeneration));
        }

        Kind = kind;
        RuntimeGeneration = runtimeGeneration;
    }

    public Switch2ProUsbRuntimeLifecycleAttentionKind Kind { get; }

    public ulong RuntimeGeneration { get; }
}

public enum Switch2ProUsbRuntimeStopFailureKind : byte
{
    None = 0,
    InvalidOwner,
    InvalidTimeout,
    OperationAlreadyInProgress,
    PumpRejected,
    PumpTimedOut,
    SinkPublicationTimedOut,
    TerminalPublicationTimedOut,
    TerminalPublicationRejected,
    TerminalDeliveryRejected,
    DependencyThrew,
    QuarantineRequired,
}

public readonly struct Switch2ProUsbRuntimeStopFailure
{
    internal Switch2ProUsbRuntimeStopFailure(
        Switch2ProUsbRuntimeStopFailureKind kind,
        Switch2ProUsbInputReadPumpFailure pumpFailure,
        Switch2ProUsbDisposeFailure disposeFailure, bool requiresQuarantine)
    {
        Kind = kind;
        PumpFailure = pumpFailure;
        DisposeFailure = disposeFailure;
        RequiresQuarantine = requiresQuarantine;
    }

    public Switch2ProUsbRuntimeStopFailureKind Kind { get; }

    public Switch2ProUsbInputReadPumpFailure PumpFailure { get; }

    public Switch2ProUsbDisposeFailure DisposeFailure { get; }

    public bool RequiresQuarantine { get; }

    public bool IsNone => Kind == Switch2ProUsbRuntimeStopFailureKind.None;
}

internal interface ISwitch2ProUsbRuntimePumpFactory
{
    bool TryCreate(Switch2ProUsbInputTransportOwner transportOwner,
        int readRetirementTimeoutMilliseconds,
        out ISwitch2ProUsbRuntimeReadPump pump,
        out Switch2ProUsbInputReadPumpFailure failure);
}

internal interface ISwitch2ProUsbRuntimeTerminalScheduler :
    ISwitch2RuntimeTerminalScheduler
{
}

internal sealed class Switch2ProUsbRuntimeTerminalScheduler :
    ISwitch2ProUsbRuntimeTerminalScheduler
{
    internal static readonly Switch2ProUsbRuntimeTerminalScheduler Instance =
        new();

    private Switch2ProUsbRuntimeTerminalScheduler()
    {
    }

    public bool TrySchedule(
        Func<Switch2TerminalNeutralRequestResult> callback,
        out Task<Switch2TerminalNeutralRequestResult> task)
    {
        return Switch2RuntimeTerminalScheduler.Instance.TrySchedule(callback,
            out task);
    }
}

internal interface ISwitch2ProUsbRuntimeReadPump
{
    Switch2ProUsbInputReadPumpState State { get; }

    Switch2ProUsbInputReadPumpFailure TerminalFailure { get; }

    Switch2ProUsbDisposeFailure LastDisposeFailure { get; }

    long StartedReadCount { get; }

    long RetiredReadCount { get; }

    bool TrySetLifecycleAttentionHandler(
        Action<Switch2ProUsbInputReadPumpFailure> handler);

    bool TryPrepareStart(int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure);

    // False is a pre-release rejection: the worker gate remains parked and the
    // exact private fence can still abort it. An exception is treated as an
    // ambiguous side effect by the composition owner. The implementation must
    // return within the explicit bound even though its successful path is an
    // in-memory gate transition.
    bool TryCommitPrepared(int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure);

    bool TryAbortPrepared(int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure);

    bool TryStart(out Switch2ProUsbInputReadPumpFailure failure);

    bool RequestStop();

    bool TryStopAndDispose(int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure);
}

internal sealed class Switch2ProUsbRuntimeReadPump :
    ISwitch2ProUsbRuntimeReadPump
{
    private readonly Switch2ProUsbInputReadPump pump;
    private readonly object activationFence = new();

    internal Switch2ProUsbRuntimeReadPump(Switch2ProUsbInputReadPump pump)
    {
        this.pump = pump ?? throw new ArgumentNullException(nameof(pump));
    }

    public Switch2ProUsbInputReadPumpState State => pump.State;

    public Switch2ProUsbInputReadPumpFailure TerminalFailure =>
        pump.TerminalFailure;

    public Switch2ProUsbDisposeFailure LastDisposeFailure =>
        pump.LastDisposeFailure;

    public long StartedReadCount => pump.StartedReadCount;

    public long RetiredReadCount => pump.RetiredReadCount;

    public bool TrySetLifecycleAttentionHandler(
        Action<Switch2ProUsbInputReadPumpFailure> handler) =>
        pump.TrySetLifecycleAttentionHandler(handler);

    public bool TryPrepareStart(int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure) =>
        pump.TryPrepareStart(activationFence, timeoutMilliseconds,
            out failure);

    public bool TryCommitPrepared(int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure) =>
        timeoutMilliseconds >= 0 && timeoutMilliseconds <=
            Switch2ProUsbInputTransportOwner.
                MaximumDisposeTimeoutMilliseconds ?
            pump.TryCommitPrepared(activationFence, out failure) :
            FailInvalidCommitTimeout(out failure);

    public bool TryAbortPrepared(int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure) =>
        pump.TryAbortPrepared(activationFence, timeoutMilliseconds,
            out failure);

    public bool TryStart(out Switch2ProUsbInputReadPumpFailure failure) =>
        pump.TryStart(out failure);

    public bool RequestStop() => pump.RequestStop();

    public bool TryStopAndDispose(int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure) =>
        pump.TryStopAndDispose(timeoutMilliseconds, out failure);

    private static bool FailInvalidCommitTimeout(
        out Switch2ProUsbInputReadPumpFailure failure)
    {
        failure = Switch2ProUsbInputReadPumpFailure.InvalidCommitTimeout;
        return false;
    }
}

internal sealed class Switch2ProUsbRuntimePumpFactory :
    ISwitch2ProUsbRuntimePumpFactory
{
    internal static readonly Switch2ProUsbRuntimePumpFactory Instance = new();

    private Switch2ProUsbRuntimePumpFactory()
    {
    }

    public bool TryCreate(Switch2ProUsbInputTransportOwner transportOwner,
        int readRetirementTimeoutMilliseconds,
        out ISwitch2ProUsbRuntimeReadPump pump,
        out Switch2ProUsbInputReadPumpFailure failure)
    {
        pump = null;
        if (!Switch2ProUsbInputReadPump.TryCreate(transportOwner,
                readRetirementTimeoutMilliseconds,
                out Switch2ProUsbInputReadPump concrete, out failure))
        {
            return false;
        }
        pump = new Switch2ProUsbRuntimeReadPump(concrete);
        return true;
    }
}

/// <summary>
/// Dormant composition owner for one admitted Switch 2 Pro USB input lifetime.
/// It is the only sink behind its transport and the only registration owner for
/// its no-HID runtime device. It performs no discovery until its explicit
/// factory is called, and it has no production, ControlService, DS4Devices,
/// output, command, LED, or haptic call site.
/// </summary>
public sealed class Switch2ProUsbRuntimeOwner :
    ISwitch2ProUsbInputSink, IInputControllerRegistrationOwner
{
    public const int MaximumStopTimeoutMilliseconds =
        Switch2ProUsbInputTransportOwner.MaximumDisposeTimeoutMilliseconds;

    private readonly object gate = new();
    private readonly object lifecycleAttentionGate = new();
    private readonly ulong deviceGeneration;
    private readonly ulong transportGeneration;
    private readonly Switch2RuntimeInputDevice runtimeInputDevice;
    private readonly InputControllerRegistration registration;
    private readonly int publicationAdmissionTimeoutMilliseconds;
    private readonly ISwitch2ProUsbRuntimeTerminalScheduler terminalScheduler;
    private readonly Switch2ProUsbRuntimeLifecycleAttentionEventArgs
        inputRejectedAttention;
    private readonly Switch2ProUsbRuntimeLifecycleAttentionEventArgs
        subscriberRejectedAttention;
    private readonly Switch2ProUsbRuntimeLifecycleAttentionEventArgs
        nativeReadFailureAttention;

    private Switch2ProUsbInputTransportOwner transportOwner;
    private ISwitch2ProUsbRuntimeReadPump readPump;
    private Switch2ProUsbRuntimeOwnerState state =
        Switch2ProUsbRuntimeOwnerState.Created;
    private Switch2ProUsbRuntimeInputFailure lastInputFailure;
    private Switch2ProProfileInputFailure lastProfileMappingFailure;
    private Switch2ProUsbRuntimeStopFailure lastStopFailure;
    private Task<Switch2TerminalNeutralRequestResult> terminalTask;
    private bool terminalTaskScheduled;
    private bool lifecycleOperationInProgress;
    private InputControllerSlotToken boundSlotToken;
    private object slotAdoptionFence;
    private object preparedCredentialFence;
    private bool preparedCredentialConsumed;
    private bool commitOpened;
    private bool inputPublicationInProgress;
    private int terminalPublicationThreadId;
    private bool requiresQuarantine;
    private bool creationFailed;
    private string lastPrepareDiagnostic = "never-entered";
    private int lifecycleAttentionRaised;
    private EventHandler<Switch2ProUsbRuntimeLifecycleAttentionEventArgs>
        lifecycleAttentionHandlers;

    private Switch2ProUsbRuntimeOwner(ulong deviceGeneration,
        ulong transportGeneration,
        Switch2RuntimeInputDevice runtimeInputDevice,
        int publicationAdmissionTimeoutMilliseconds,
        ISwitch2ProUsbRuntimeTerminalScheduler terminalScheduler)
    {
        this.deviceGeneration = deviceGeneration;
        this.transportGeneration = transportGeneration;
        this.runtimeInputDevice = runtimeInputDevice;
        this.publicationAdmissionTimeoutMilliseconds =
            publicationAdmissionTimeoutMilliseconds;
        this.terminalScheduler = terminalScheduler;
        inputRejectedAttention = new(
            Switch2ProUsbRuntimeLifecycleAttentionKind.InputRejected,
            deviceGeneration);
        subscriberRejectedAttention = new(
            Switch2ProUsbRuntimeLifecycleAttentionKind.SubscriberRejected,
            deviceGeneration);
        nativeReadFailureAttention = new(
            Switch2ProUsbRuntimeLifecycleAttentionKind.NativeReadFailure,
            deviceGeneration);

        if (!InputControllerRegistration.TryCreate(runtimeInputDevice,
                deviceGeneration, InputControllerOwnershipKind.Switch2Runtime,
                hasHidInterface: false, hasPersistentIdentity: false, this,
                out registration,
                out InputControllerRegistrationFailure registrationFailure))
        {
            throw new InvalidOperationException(
                $"Runtime registration rejected: {registrationFailure}.");
        }
    }

    public InputControllerOwnershipKind Kind =>
        InputControllerOwnershipKind.Switch2Runtime;

    public Switch2RuntimeInputDevice RuntimeInputDevice => runtimeInputDevice;

    public InputControllerRegistration Registration => registration;

    /// <summary>
    /// One generation-fenced, coalesced wake-up for the service registration
    /// coordinator. Subscribers are invoked outside every owner, pump, and
    /// transport lock. The first terminal source wins for this lifetime.
    /// </summary>
    public event EventHandler<Switch2ProUsbRuntimeLifecycleAttentionEventArgs>
        LifecycleAttention
    {
        add
        {
            if (value == null)
            {
                return;
            }
            lock (lifecycleAttentionGate)
            {
                lifecycleAttentionHandlers += value;
            }
        }
        remove
        {
            if (value == null)
            {
                return;
            }
            lock (lifecycleAttentionGate)
            {
                lifecycleAttentionHandlers -= value;
            }
        }
    }

    public Switch2ProUsbRuntimeOwnerState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public bool RequiresQuarantine
    {
        get
        {
            lock (gate)
            {
                return requiresQuarantine;
            }
        }
    }

    public Switch2ProUsbRuntimeInputFailure LastInputFailure
    {
        get
        {
            lock (gate)
            {
                return lastInputFailure;
            }
        }
    }

    public Switch2ProProfileInputFailure LastProfileMappingFailure
    {
        get
        {
            lock (gate)
            {
                return lastProfileMappingFailure;
            }
        }
    }

    public Switch2ProUsbRuntimeStopFailure LastStopFailure
    {
        get
        {
            lock (gate)
            {
                return lastStopFailure;
            }
        }
    }

    internal string LastPrepareDiagnostic =>
        Volatile.Read(ref lastPrepareDiagnostic);

    internal Switch2ProUsbInputTransportOwner TransportOwner =>
        transportOwner;

    internal ISwitch2ProUsbRuntimeReadPump ReadPump => readPump;

    internal void MarkOwnedCompositeCreationQuarantined() =>
        MarkCreationQuarantined();

    public static bool TryCreate(ISwitch2ProUsbOsDiscoveryAdapter discovery,
        ISwitch2ProUsbNativeAdapter nativeAdapter, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration,
        int readRetirementTimeoutMilliseconds,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbRuntimeCreateFailure failure) => TryCreateCore(
            discovery, nativeAdapter,
            Switch2ProUsbRuntimePumpFactory.Instance, deviceGeneration,
            transportGeneration, qpcFrequency, calibration,
            readRetirementTimeoutMilliseconds, out owner, out registration,
            out failure);

    internal static bool TryCreateCore(
        ISwitch2ProUsbOsDiscoveryAdapter discovery,
        ISwitch2ProUsbNativeAdapter nativeAdapter,
        ISwitch2ProUsbRuntimePumpFactory pumpFactory,
        ulong deviceGeneration, ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration,
        int readRetirementTimeoutMilliseconds,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbRuntimeCreateFailure failure)
    {
        return TryCreateCore(discovery, nativeAdapter, pumpFactory,
            Switch2ProUsbRuntimeTerminalScheduler.Instance,
            inputAdoptionBinder: null, admittedOwnedLifetime: null,
            deviceGeneration, transportGeneration, qpcFrequency, calibration,
            readRetirementTimeoutMilliseconds, out owner, out registration,
            out failure);
    }

    internal static bool TryCreateCore(
        ISwitch2ProUsbOsDiscoveryAdapter discovery,
        ISwitch2ProUsbNativeAdapter nativeAdapter,
        ISwitch2ProUsbRuntimePumpFactory pumpFactory,
        ISwitch2ProUsbRuntimeTerminalScheduler terminalScheduler,
        ulong deviceGeneration, ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration,
        int readRetirementTimeoutMilliseconds,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbRuntimeCreateFailure failure)
    {
        return TryCreateCore(discovery, nativeAdapter, pumpFactory,
            terminalScheduler, inputAdoptionBinder: null,
            admittedOwnedLifetime: null, deviceGeneration,
            transportGeneration, qpcFrequency, calibration,
            readRetirementTimeoutMilliseconds, out owner, out registration,
            out failure);
    }

    /// <summary>
    /// Dormant construction-only seam used by the owned-composite adoption
    /// factory. The binder sees the exact runtime owner and registration before
    /// the mediated input facet can escape. The concrete mediator is both the
    /// binder and the only native-adapter-shaped handoff accepted here, so a
    /// caller cannot combine an admitted lifetime with an unrelated Windows
    /// adapter. Existing production creation paths retain their previous
    /// discovery/open behavior.
    /// </summary>
    internal static bool TryCreateOwnedCompositeCore(
        Switch2ProUsbOwnedCompositeInputAdoptionIssuer mediator,
        ISwitch2ProUsbRuntimePumpFactory pumpFactory,
        ISwitch2ProUsbRuntimeTerminalScheduler terminalScheduler,
        in Switch2PhysicalInputLifetime admittedLifetime,
        in Switch2InputCalibrationSnapshot calibration,
        int readRetirementTimeoutMilliseconds,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbRuntimeCreateFailure failure)
    {
        Switch2InputSessionDescriptor descriptor =
            admittedLifetime.SessionDescriptor;
        return TryCreateCore(discovery: null, mediator, pumpFactory,
            terminalScheduler, mediator, admittedLifetime,
            descriptor.DeviceGeneration, descriptor.TransportGeneration,
            descriptor.QpcFrequency, calibration,
            readRetirementTimeoutMilliseconds, out owner, out registration,
            out failure);
    }

    private static bool TryCreateCore(
        ISwitch2ProUsbOsDiscoveryAdapter discovery,
        ISwitch2ProUsbNativeAdapter nativeAdapter,
        ISwitch2ProUsbRuntimePumpFactory pumpFactory,
        ISwitch2ProUsbRuntimeTerminalScheduler terminalScheduler,
        ISwitch2ProUsbRuntimeInputAdoptionBinder inputAdoptionBinder,
        Switch2PhysicalInputLifetime? admittedOwnedLifetime,
        ulong deviceGeneration, ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration,
        int readRetirementTimeoutMilliseconds,
        out Switch2ProUsbRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2ProUsbRuntimeCreateFailure failure)
    {
        owner = null;
        registration = default;
        if (nativeAdapter == null || pumpFactory == null ||
            terminalScheduler == null ||
            admittedOwnedLifetime == null && discovery == null)
        {
            failure = CreateFailure(
                Switch2ProUsbRuntimeCreateFailureKind.MissingDependency);
            return false;
        }
        if (deviceGeneration == 0 || transportGeneration == 0)
        {
            failure = CreateFailure(
                Switch2ProUsbRuntimeCreateFailureKind.InvalidGeneration);
            return false;
        }
        if (qpcFrequency <= 0)
        {
            failure = CreateFailure(
                Switch2ProUsbRuntimeCreateFailureKind.InvalidClock);
            return false;
        }
        if (admittedOwnedLifetime is Switch2PhysicalInputLifetime exact &&
            (!exact.IsValid || exact.SessionDescriptor.DeviceGeneration !=
                    deviceGeneration ||
                exact.SessionDescriptor.TransportGeneration !=
                    transportGeneration ||
                exact.SessionDescriptor.QpcFrequency != qpcFrequency))
        {
            failure = CreateFailure(
                Switch2ProUsbRuntimeCreateFailureKind.InvalidGeneration);
            return false;
        }
        if (!calibration.IsValid || calibration.Model !=
                Switch2ControllerModel.ProController2 ||
            calibration.DeviceGeneration != deviceGeneration)
        {
            failure = CreateFailure(
                Switch2ProUsbRuntimeCreateFailureKind.InvalidCalibration);
            return false;
        }
        if (readRetirementTimeoutMilliseconds <= 0 ||
            readRetirementTimeoutMilliseconds >
                MaximumStopTimeoutMilliseconds)
        {
            failure = CreateFailure(Switch2ProUsbRuntimeCreateFailureKind.
                InvalidReadRetirementTimeout);
            return false;
        }

        if (!Switch2RuntimeInputDevice.TryCreatePro(deviceGeneration,
                transportGeneration, Switch2Transport.Usb,
                out Switch2RuntimeInputDevice runtimeDevice,
                out Switch2RuntimeInputDeviceCreateFailure runtimeFailure))
        {
            failure = new Switch2ProUsbRuntimeCreateFailure(
                Switch2ProUsbRuntimeCreateFailureKind.RuntimeDeviceRejected,
                runtimeFailure, default, default, default, default, null);
            return false;
        }

        Switch2ProUsbRuntimeOwner candidate;
        try
        {
            candidate = new Switch2ProUsbRuntimeOwner(deviceGeneration,
                transportGeneration, runtimeDevice,
                readRetirementTimeoutMilliseconds, terminalScheduler);
        }
        catch
        {
            failure = new Switch2ProUsbRuntimeCreateFailure(
                Switch2ProUsbRuntimeCreateFailureKind.RegistrationRejected,
                default, InputControllerRegistrationFailure.
                    OwnerAuthenticationFailed,
                default, default, default, null);
            return false;
        }

        if (inputAdoptionBinder != null)
        {
            bool bound;
            try
            {
                bound = inputAdoptionBinder.TryBindRuntimeOwner(candidate,
                    candidate.registration);
            }
            catch
            {
                bound = false;
            }
            if (!bound)
            {
                failure = CreateFailure(
                    Switch2ProUsbRuntimeCreateFailureKind.
                        InputAdoptionRejected);
                return false;
            }
        }

        Switch2ProUsbInputTransportOwner transport;
        Switch2ProUsbTransportCreateFailure transportFailure;
        try
        {
            bool transportCreated = admittedOwnedLifetime is
                    Switch2PhysicalInputLifetime exactLifetime ?
                Switch2ProUsbInputTransportOwner.
                    TryCreateFromAdmittedLifetime(nativeAdapter, candidate,
                        exactLifetime, calibration, out transport,
                        out transportFailure) :
                Switch2ProUsbInputTransportOwner.TryCreate(discovery,
                    nativeAdapter, candidate, deviceGeneration,
                    transportGeneration, qpcFrequency, calibration,
                    out transport, out transportFailure);
            if (!transportCreated)
            {
                failure = new Switch2ProUsbRuntimeCreateFailure(
                    Switch2ProUsbRuntimeCreateFailureKind.TransportRejected,
                    default, default, transportFailure, default, default,
                    null);
                return false;
            }
        }
        catch
        {
            failure = CreateFailure(
                Switch2ProUsbRuntimeCreateFailureKind.DependencyThrew);
            return false;
        }

        candidate.transportOwner = transport;
        ISwitch2ProUsbRuntimeReadPump pump = null;
        Switch2ProUsbInputReadPumpFailure pumpFailure;
        bool pumpCreated;
        bool pumpThrew = false;
        try
        {
            pumpCreated = pumpFactory.TryCreate(transport,
                readRetirementTimeoutMilliseconds, out pump,
                out pumpFailure);
        }
        catch
        {
            pumpCreated = false;
            pumpThrew = true;
            pumpFailure = Switch2ProUsbInputReadPumpFailure.
                UnexpectedWorkerFailure;
        }

        if (!pumpCreated || pump == null)
        {
            transport.RequestStop();
            bool rolledBack = transport.TryQuiesceAndDispose(
                MaximumStopTimeoutMilliseconds,
                out Switch2ProUsbDisposeFailure rollbackFailure);
            if (!rolledBack)
            {
                candidate.MarkCreationQuarantined();
                owner = candidate;
                failure = new Switch2ProUsbRuntimeCreateFailure(
                    IsTimeout(rollbackFailure) ?
                        Switch2ProUsbRuntimeCreateFailureKind.
                            RollbackTimedOut :
                        Switch2ProUsbRuntimeCreateFailureKind.
                            RollbackRejected,
                    default, default, default, pumpFailure, rollbackFailure,
                    candidate);
                return false;
            }

            failure = new Switch2ProUsbRuntimeCreateFailure(pumpThrew ?
                    Switch2ProUsbRuntimeCreateFailureKind.DependencyThrew :
                    Switch2ProUsbRuntimeCreateFailureKind.PumpRejected,
                default, default, default, pumpFailure, rollbackFailure,
                null);
            return false;
        }

        candidate.readPump = pump;
        bool attentionInstalled;
        try
        {
            attentionInstalled = pump.TrySetLifecycleAttentionHandler(
                candidate.OnPumpLifecycleAttention);
        }
        catch
        {
            attentionInstalled = false;
        }
        if (!attentionInstalled)
        {
            bool rolledBack;
            Switch2ProUsbInputReadPumpFailure rollbackPumpFailure;
            try
            {
                pump.RequestStop();
                rolledBack = pump.TryStopAndDispose(
                    MaximumStopTimeoutMilliseconds,
                    out rollbackPumpFailure);
            }
            catch
            {
                rolledBack = false;
                rollbackPumpFailure = Switch2ProUsbInputReadPumpFailure.
                    UnexpectedWorkerFailure;
            }
            if (!rolledBack)
            {
                candidate.MarkCreationQuarantined();
                owner = candidate;
                failure = new Switch2ProUsbRuntimeCreateFailure(
                    Switch2ProUsbRuntimeCreateFailureKind.RollbackRejected,
                    default, default, default, rollbackPumpFailure,
                    pump.LastDisposeFailure, candidate);
                return false;
            }

            failure = new Switch2ProUsbRuntimeCreateFailure(
                Switch2ProUsbRuntimeCreateFailureKind.PumpRejected,
                default, default, default,
                Switch2ProUsbInputReadPumpFailure.OwnerRejected,
                default, null);
            return false;
        }
        owner = candidate;
        registration = candidate.registration;
        failure = default;
        return true;
    }

    /// <summary>
    /// Adopts the exact slot returned by a completed table bind. The first
    /// distinct token wins; repeating that exact token is idempotent while a
    /// foreign table/token receives no cleanup authority.
    /// </summary>
    internal bool TryAdoptBoundSlot(
        in InputControllerSlotToken exactBoundSlotToken,
        out Switch2ProUsbRuntimeSlotAdoptionCredential credential,
        out Switch2ProUsbRuntimeSlotAdoptionFailure failure)
    {
        credential = default;
        if (!exactBoundSlotToken.IsValid ||
            !exactBoundSlotToken.Registration.Equals(registration) ||
            !ReferenceEquals(exactBoundSlotToken.Registration.Owner, this) ||
            !exactBoundSlotToken.Registration.IsOwnerAuthenticated)
        {
            failure = Switch2ProUsbRuntimeSlotAdoptionFailure.InvalidToken;
            return false;
        }

        lock (gate)
        {
            if (creationFailed || requiresQuarantine)
            {
                failure = Switch2ProUsbRuntimeSlotAdoptionFailure.
                    QuarantineRequired;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2ProUsbRuntimeSlotAdoptionFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            if (state != Switch2ProUsbRuntimeOwnerState.Created)
            {
                failure = Switch2ProUsbRuntimeSlotAdoptionFailure.
                    InvalidState;
                return false;
            }
            if (slotAdoptionFence != null)
            {
                if (!exactBoundSlotToken.Equals(boundSlotToken))
                {
                    failure = Switch2ProUsbRuntimeSlotAdoptionFailure.
                        DifferentSlotAlreadyAdopted;
                    return false;
                }
            }
            else
            {
                boundSlotToken = exactBoundSlotToken;
                slotAdoptionFence = new object();
            }

            credential = new Switch2ProUsbRuntimeSlotAdoptionCredential(
                this, slotAdoptionFence, boundSlotToken, deviceGeneration);
        }
        failure = Switch2ProUsbRuntimeSlotAdoptionFailure.None;
        return true;
    }

    /// <summary>
    /// Arms the no-HID runtime and starts the pump worker, then waits until the
    /// worker is parked before native begin. No report or native read can occur
    /// until the returned exact credential is committed.
    /// </summary>
    public bool TryPrepareActivation(
        in InputControllerRegistration exactRegistration,
        int timeoutMilliseconds,
        out Switch2ProUsbRuntimePrepareCredential credential,
        out Switch2ProUsbRuntimePrepareFailure failure) =>
        TryPrepareActivationCore(exactRegistration, default,
            requireSlotAdoption: false, timeoutMilliseconds, out credential,
            out failure);

    internal bool TryPrepareActivation(
        in Switch2ProUsbRuntimeSlotAdoptionCredential adoptionCredential,
        int timeoutMilliseconds,
        out Switch2ProUsbRuntimePrepareCredential credential,
        out Switch2ProUsbRuntimePrepareFailure failure) =>
        TryPrepareActivationCore(adoptionCredential.SlotToken.Registration,
            adoptionCredential, requireSlotAdoption: true,
            timeoutMilliseconds, out credential, out failure);

    private bool TryPrepareActivationCore(
        in InputControllerRegistration exactRegistration,
        in Switch2ProUsbRuntimeSlotAdoptionCredential adoptionCredential,
        bool requireSlotAdoption, int timeoutMilliseconds,
        out Switch2ProUsbRuntimePrepareCredential credential,
        out Switch2ProUsbRuntimePrepareFailure failure)
    {
        Volatile.Write(ref lastPrepareDiagnostic, "entered");
        credential = default;
        if (requireSlotAdoption && (!adoptionCredential.IsValid ||
                !adoptionCredential.SlotToken.Registration.Equals(
                    registration)))
        {
            failure = Switch2ProUsbRuntimePrepareFailure.
                InvalidSlotAdoptionCredential;
            Volatile.Write(ref lastPrepareDiagnostic,
                "invalid-adoption-before-registration");
            return false;
        }
        if (!exactRegistration.Equals(registration))
        {
            failure = Switch2ProUsbRuntimePrepareFailure.InvalidRegistration;
            Volatile.Write(ref lastPrepareDiagnostic,
                "invalid-registration");
            return false;
        }
        if (!exactRegistration.IsOwnerAuthenticated)
        {
            failure = Switch2ProUsbRuntimePrepareFailure.
                OwnerAuthenticationFailed;
            Volatile.Write(ref lastPrepareDiagnostic,
                "owner-authentication-failed");
            return false;
        }
        if (timeoutMilliseconds <= 0 || timeoutMilliseconds >
            MaximumStopTimeoutMilliseconds)
        {
            failure = Switch2ProUsbRuntimePrepareFailure.InvalidTimeout;
            Volatile.Write(ref lastPrepareDiagnostic, "invalid-timeout");
            return false;
        }

        lock (gate)
        {
            if (requireSlotAdoption ?
                    !adoptionCredential.Authenticates(this,
                        slotAdoptionFence, boundSlotToken,
                        deviceGeneration) :
                    slotAdoptionFence != null)
            {
                failure = Switch2ProUsbRuntimePrepareFailure.
                    InvalidSlotAdoptionCredential;
                Volatile.Write(ref lastPrepareDiagnostic,
                    "invalid-adoption-under-owner-gate");
                return false;
            }
            if (creationFailed || requiresQuarantine)
            {
                failure = Switch2ProUsbRuntimePrepareFailure.
                    QuarantineRequired;
                Volatile.Write(ref lastPrepareDiagnostic,
                    "already-quarantined");
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2ProUsbRuntimePrepareFailure.
                    OperationAlreadyInProgress;
                Volatile.Write(ref lastPrepareDiagnostic,
                    "operation-already-in-progress");
                return false;
            }
            if (state != Switch2ProUsbRuntimeOwnerState.Created ||
                readPump == null || transportOwner == null)
            {
                failure = Switch2ProUsbRuntimePrepareFailure.InvalidState;
                Volatile.Write(ref lastPrepareDiagnostic,
                    "invalid-owner-state");
                return false;
            }
            lifecycleOperationInProgress = true;
            state = Switch2ProUsbRuntimeOwnerState.Activating;
        }
        Volatile.Write(ref lastPrepareDiagnostic, "activation-admitted");

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        bool runtimeArmed;
        try
        {
            runtimeInputDevice.StartUpdate();
            runtimeArmed = runtimeInputDevice.RuntimeState ==
                Switch2RuntimeInputDeviceState.Active;
        }
        catch
        {
            runtimeArmed = false;
        }

        Switch2ProUsbInputReadPumpFailure pumpFailure = default;
        bool pumpPrepared = false;
        if (runtimeArmed)
        {
            int remaining = RemainingMilliseconds(deadline,
                timeoutMilliseconds);
            if (remaining == 0)
            {
                pumpFailure = Switch2ProUsbInputReadPumpFailure.
                    WorkerParkTimedOut;
            }
            else
            {
                try
                {
                    pumpPrepared = readPump.TryPrepareStart(remaining,
                        out pumpFailure);
                }
                catch
                {
                    pumpFailure = Switch2ProUsbInputReadPumpFailure.
                        UnexpectedWorkerFailure;
                }
            }
        }

        bool zeroReads = false;
        if (runtimeArmed && pumpPrepared)
        {
            try
            {
                zeroReads = readPump.StartedReadCount == 0 &&
                    readPump.RetiredReadCount == 0;
            }
            catch
            {
                pumpFailure = Switch2ProUsbInputReadPumpFailure.
                    UnexpectedWorkerFailure;
            }
        }

        if (!runtimeArmed || !pumpPrepared || !zeroReads)
        {
            bool cleaned = TryCleanupFailedUnpublishedPrepare(deadline,
                timeoutMilliseconds);
            lock (gate)
            {
                lifecycleOperationInProgress = false;
                if (cleaned)
                {
                    state = Switch2ProUsbRuntimeOwnerState.
                        AbortedUnpublished;
                }
                else
                {
                    requiresQuarantine = true;
                    state = Switch2ProUsbRuntimeOwnerState.Quarantined;
                }
                Monitor.PulseAll(gate);
            }

            failure = !cleaned ?
                Switch2ProUsbRuntimePrepareFailure.QuarantineRequired :
                !runtimeArmed ?
                    Switch2ProUsbRuntimePrepareFailure.RuntimeArmRejected :
                pumpFailure == Switch2ProUsbInputReadPumpFailure.
                    WorkerParkTimedOut ?
                    Switch2ProUsbRuntimePrepareFailure.PumpPrepareTimedOut :
                    Switch2ProUsbRuntimePrepareFailure.PumpPrepareRejected;
            Volatile.Write(ref lastPrepareDiagnostic, !cleaned ?
                "cleanup-failed" : !runtimeArmed ?
                "runtime-arm-rejected" : !pumpPrepared ?
                "pump-prepare-rejected" : "pump-read-count-not-zero");
            return false;
        }

        object fence = new();
        lock (gate)
        {
            preparedCredentialFence = fence;
            preparedCredentialConsumed = false;
            commitOpened = false;
            lifecycleOperationInProgress = false;
            state = Switch2ProUsbRuntimeOwnerState.Prepared;
            Monitor.PulseAll(gate);
        }
        credential = new Switch2ProUsbRuntimePrepareCredential(this, fence,
            deviceGeneration);
        failure = Switch2ProUsbRuntimePrepareFailure.None;
        Volatile.Write(ref lastPrepareDiagnostic, "prepared");
        return true;
    }

    /// <summary>
    /// Opens the parked worker gate. Admission is made visible before the gate
    /// release. The successful path performs no I/O, join, callback, or report
    /// wait. A dependency rejection is quarantined and receives bounded
    /// fail-closed cleanup because its consumed credential cannot be reused.
    /// </summary>
    public bool TryCommitPrepared(
        in Switch2ProUsbRuntimePrepareCredential credential,
        out Switch2ProUsbRuntimeCommitFailure failure) =>
        TryCommitPreparedCore(credential,
            publicationAdmissionTimeoutMilliseconds, out failure);

    internal bool TryCommitPrepared(
        in Switch2ProUsbRuntimePrepareCredential credential,
        int cleanupTimeoutMilliseconds,
        out Switch2ProUsbRuntimeCommitFailure failure) =>
        TryCommitPreparedCore(credential, cleanupTimeoutMilliseconds,
            out failure);

    private bool TryCommitPreparedCore(
        in Switch2ProUsbRuntimePrepareCredential credential,
        int cleanupTimeoutMilliseconds,
        out Switch2ProUsbRuntimeCommitFailure failure)
    {
        if (cleanupTimeoutMilliseconds < 0 ||
            cleanupTimeoutMilliseconds > MaximumStopTimeoutMilliseconds)
        {
            failure = Switch2ProUsbRuntimeCommitFailure.InvalidTimeout;
            return false;
        }
        if (!credential.IsValid || !ReferenceEquals(credential.Issuer, this) ||
            credential.RuntimeGeneration != deviceGeneration)
        {
            failure = Switch2ProUsbRuntimeCommitFailure.InvalidCredential;
            return false;
        }

        lock (gate)
        {
            if (!ReferenceEquals(credential.Fence,
                    preparedCredentialFence))
            {
                failure = Switch2ProUsbRuntimeCommitFailure.StaleCredential;
                return false;
            }
            if (preparedCredentialConsumed)
            {
                failure = Switch2ProUsbRuntimeCommitFailure.AlreadyConsumed;
                return false;
            }
            if (creationFailed || requiresQuarantine)
            {
                failure = Switch2ProUsbRuntimeCommitFailure.
                    QuarantineRequired;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2ProUsbRuntimeCommitFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            if (state != Switch2ProUsbRuntimeOwnerState.Prepared)
            {
                failure = Switch2ProUsbRuntimeCommitFailure.InvalidState;
                return false;
            }

            lifecycleOperationInProgress = true;
            preparedCredentialConsumed = true;
            commitOpened = false;
            // The worker can win immediately after its gate opens, so owner
            // admission must become visible first.
            state = Switch2ProUsbRuntimeOwnerState.Active;
        }

        long commitDeadline = Environment.TickCount64 +
            cleanupTimeoutMilliseconds;
        bool committed;
        bool dependencyThrew = false;
        try
        {
            committed = readPump.TryCommitPrepared(
                cleanupTimeoutMilliseconds, out _);
        }
        catch
        {
            committed = false;
            dependencyThrew = true;
        }

        lock (gate)
        {
            lifecycleOperationInProgress = false;
            commitOpened = committed || dependencyThrew;
            if (!committed)
            {
                // The exact credential is consumed and the pump dependency's
                // result cannot be retried. Never expose this as an ordinary
                // reusable lifetime.
                requiresQuarantine = true;
                state = Switch2ProUsbRuntimeOwnerState.Quarantined;
            }
            Monitor.PulseAll(gate);
        }

        if (!committed)
        {
            TryCleanupRejectedCommit(dependencyThrew, commitDeadline,
                cleanupTimeoutMilliseconds);
        }
        failure = committed ? Switch2ProUsbRuntimeCommitFailure.None :
            Switch2ProUsbRuntimeCommitFailure.QuarantineRequired;
        if (committed)
        {
            _ = runtimeInputDevice.TryStartConnectionHaptic();
        }
        return committed;
    }

    public bool TryAbortPrepared(
        in Switch2ProUsbRuntimePrepareCredential credential,
        int timeoutMilliseconds,
        out Switch2ProUsbRuntimeUnpublishedAbortFailure failure)
    {
        if (!credential.IsValid || !ReferenceEquals(credential.Issuer, this) ||
            credential.RuntimeGeneration != deviceGeneration)
        {
            failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                InvalidCredential;
            return false;
        }
        return TryAbortUnpublishedCore(credential.Fence,
            requirePreparedCredential: true,
            adoptionCredential: default, requireSlotAdoption: false,
            timeoutMilliseconds: timeoutMilliseconds, out failure);
    }

    /// <summary>
    /// Disposes a newly-created owner after reservation/bind setup failed,
    /// without arming the runtime, starting a worker, or publishing neutral.
    /// </summary>
    public bool TryAbortUnpublished(
        in InputControllerRegistration exactRegistration,
        int timeoutMilliseconds,
        out Switch2ProUsbRuntimeUnpublishedAbortFailure failure)
    {
        if (!exactRegistration.Equals(registration))
        {
            failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                InvalidRegistration;
            return false;
        }
        lock (gate)
        {
            if (state == Switch2ProUsbRuntimeOwnerState.AbortedUnpublished &&
                !creationFailed && !requiresQuarantine &&
                !lifecycleOperationInProgress)
            {
                // A failed prepare can already have completed exact, silent
                // cleanup. The retained owner state is the proof; requiring
                // live registration authentication here would reject that
                // proof because authentication intentionally ends at abort.
                failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.None;
                return true;
            }
            if (slotAdoptionFence != null)
            {
                failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                    InvalidCredential;
                return false;
            }
        }
        if (!exactRegistration.IsOwnerAuthenticated)
        {
            failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                InvalidRegistration;
            return false;
        }
        return TryAbortUnpublishedCore(credentialFence: null,
            requirePreparedCredential: false,
            adoptionCredential: default, requireSlotAdoption: false,
            timeoutMilliseconds: timeoutMilliseconds, out failure);
    }

    internal bool TryAbortUnpublished(
        in Switch2ProUsbRuntimeSlotAdoptionCredential adoptionCredential,
        int timeoutMilliseconds,
        out Switch2ProUsbRuntimeUnpublishedAbortFailure failure)
    {
        if (!adoptionCredential.IsValid ||
            !adoptionCredential.SlotToken.Registration.Equals(registration))
        {
            failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                InvalidCredential;
            return false;
        }
        lock (gate)
        {
            if (state == Switch2ProUsbRuntimeOwnerState.AbortedUnpublished &&
                !creationFailed && !requiresQuarantine &&
                adoptionCredential.Authenticates(this, slotAdoptionFence,
                    boundSlotToken, deviceGeneration))
            {
                failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.None;
                return true;
            }
        }
        return TryAbortUnpublishedCore(credentialFence: null,
            requirePreparedCredential: false,
            adoptionCredential: adoptionCredential,
            requireSlotAdoption: true,
            timeoutMilliseconds: timeoutMilliseconds, out failure);
    }

    /// <summary>
    /// Non-integration convenience built on the exact parked boundary. A table
    /// adapter must instead Prepare while Bound, transition to Attached, then
    /// Commit the returned credential.
    /// </summary>
    public bool TryActivate(in InputControllerRegistration exactRegistration,
        out Switch2ProUsbRuntimeActivationFailure failure)
    {
        if (!TryPrepareActivation(exactRegistration,
                MaximumStopTimeoutMilliseconds,
                out Switch2ProUsbRuntimePrepareCredential credential,
                out Switch2ProUsbRuntimePrepareFailure prepareFailure))
        {
            failure = prepareFailure switch
            {
                Switch2ProUsbRuntimePrepareFailure.InvalidRegistration =>
                    Switch2ProUsbRuntimeActivationFailure.InvalidRegistration,
                Switch2ProUsbRuntimePrepareFailure.OwnerAuthenticationFailed =>
                    Switch2ProUsbRuntimeActivationFailure.
                        OwnerAuthenticationFailed,
                Switch2ProUsbRuntimePrepareFailure.
                    OperationAlreadyInProgress =>
                    Switch2ProUsbRuntimeActivationFailure.
                        OperationAlreadyInProgress,
                Switch2ProUsbRuntimePrepareFailure.QuarantineRequired =>
                    Switch2ProUsbRuntimeActivationFailure.QuarantineRequired,
                Switch2ProUsbRuntimePrepareFailure.RuntimeArmRejected =>
                    Switch2ProUsbRuntimeActivationFailure.RuntimeStartRejected,
                _ => Switch2ProUsbRuntimeActivationFailure.PumpStartRejected,
            };
            return false;
        }
        if (!TryCommitPrepared(credential,
                out Switch2ProUsbRuntimeCommitFailure commitFailure))
        {
            failure = commitFailure ==
                    Switch2ProUsbRuntimeCommitFailure.QuarantineRequired ?
                Switch2ProUsbRuntimeActivationFailure.QuarantineRequired :
                Switch2ProUsbRuntimeActivationFailure.PumpStartRejected;
            return false;
        }

        bool healthy;
        lock (gate)
        {
            healthy = state == Switch2ProUsbRuntimeOwnerState.Active &&
                lastInputFailure == Switch2ProUsbRuntimeInputFailure.None;
        }
        if (!healthy)
        {
            bool stopped = TryStopAndQuiesce(runtimeInputDevice,
                deviceGeneration, MaximumStopTimeoutMilliseconds, out _);
            failure = stopped ? Switch2ProUsbRuntimeActivationFailure.
                    InitialPublicationRejected :
                Switch2ProUsbRuntimeActivationFailure.QuarantineRequired;
            return false;
        }

        failure = Switch2ProUsbRuntimeActivationFailure.None;
        return true;
    }

    public bool Authenticates(DS4Device device, ulong generation)
    {
        lock (gate)
        {
            return !creationFailed && state !=
                    Switch2ProUsbRuntimeOwnerState.Removed &&
                state != Switch2ProUsbRuntimeOwnerState.AbortedUnpublished &&
                ReferenceEquals(device, runtimeInputDevice) &&
                generation == deviceGeneration;
        }
    }

    public bool TryPublish(in Switch2CanonicalInputFrame frame)
    {
        Switch2ProUsbRuntimeInputFailure immediateFailure = default;
        lock (gate)
        {
            if (state != Switch2ProUsbRuntimeOwnerState.Active)
            {
                lastInputFailure =
                    Switch2ProUsbRuntimeInputFailure.LifecycleClosed;
                return false;
            }
            if (inputPublicationInProgress)
            {
                immediateFailure = Switch2ProUsbRuntimeInputFailure.
                    PublicationAlreadyInProgress;
                RejectInputNoLock(immediateFailure);
            }
            else if (frame.DeviceGeneration != deviceGeneration)
            {
                immediateFailure = Switch2ProUsbRuntimeInputFailure.
                    StaleDeviceGeneration;
                RejectInputNoLock(immediateFailure);
            }
            else if (frame.TransportGeneration != transportGeneration)
            {
                immediateFailure = Switch2ProUsbRuntimeInputFailure.
                    StaleTransportGeneration;
                RejectInputNoLock(immediateFailure);
            }
            else if (frame.Model != Switch2ControllerModel.ProController2)
            {
                immediateFailure = Switch2ProUsbRuntimeInputFailure.
                    ModelMismatch;
                RejectInputNoLock(immediateFailure);
            }
            else if (frame.Transport != Switch2Transport.Usb)
            {
                immediateFailure = Switch2ProUsbRuntimeInputFailure.
                    TransportMismatch;
                RejectInputNoLock(immediateFailure);
            }
            else
            {
                inputPublicationInProgress = true;
            }
        }

        if (immediateFailure != Switch2ProUsbRuntimeInputFailure.None)
        {
            RaiseLifecycleAttention(inputRejectedAttention);
            return false;
        }

        bool accepted = false;
        Switch2ProProfileInputFailure mappingFailure = default;
        Switch2ProUsbRuntimeInputFailure inputFailure = default;
        try
        {
            if (!Switch2ProProfileInputMapper.TryMap(frame,
                    out Switch2ProProfileInputFrame profileFrame,
                    out mappingFailure))
            {
                inputFailure = Switch2ProUsbRuntimeInputFailure.
                    ProfileMappingRejected;
            }
            else
            {
                accepted = TryPublishExactProfileFrame(profileFrame,
                    out inputFailure);
            }
        }
        catch
        {
            inputFailure =
                Switch2ProUsbRuntimeInputFailure.DependencyThrew;
        }
        finally
        {
            Switch2ProUsbRuntimeLifecycleAttentionEventArgs attention = null;
            lock (gate)
            {
                inputPublicationInProgress = false;
                if (!accepted)
                {
                    lastInputFailure = inputFailure;
                    lastProfileMappingFailure = mappingFailure;
                    if (state == Switch2ProUsbRuntimeOwnerState.Active)
                    {
                        state = Switch2ProUsbRuntimeOwnerState.StopRequested;
                    }
                    attention = inputFailure ==
                        Switch2ProUsbRuntimeInputFailure.
                            RuntimePublicationRejected ?
                        subscriberRejectedAttention : inputRejectedAttention;
                }
                Monitor.PulseAll(gate);
            }
            if (attention != null)
            {
                RaiseLifecycleAttention(attention);
            }
        }

        return accepted;
    }

    public bool TryStopAndQuiesce(DS4Device device, ulong generation,
        int timeoutMilliseconds,
        out InputControllerOwnerOperationFailure failure)
    {
        if (!ReferenceEquals(device, runtimeInputDevice) ||
            generation != deviceGeneration)
        {
            SetStopFailure(new Switch2ProUsbRuntimeStopFailure(
                Switch2ProUsbRuntimeStopFailureKind.InvalidOwner, default,
                default, requiresQuarantine: false));
            failure = InputControllerOwnerOperationFailure.
                OwnerAuthenticationFailed;
            return false;
        }
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            MaximumStopTimeoutMilliseconds)
        {
            SetStopFailure(new Switch2ProUsbRuntimeStopFailure(
                Switch2ProUsbRuntimeStopFailureKind.InvalidTimeout, default,
                default, requiresQuarantine: false));
            failure = InputControllerOwnerOperationFailure.StopRejected;
            return false;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        lock (gate)
        {
            // Re-evaluate every terminal and credential-protected state after
            // every lifecycle wait. In particular, Activating may become
            // Prepared, and another stop may become Stopped then Removed while
            // this caller is asleep. Neither may be overwritten.
            while (true)
            {
                if (creationFailed || requiresQuarantine)
                {
                    lastStopFailure = new Switch2ProUsbRuntimeStopFailure(
                        Switch2ProUsbRuntimeStopFailureKind.
                            QuarantineRequired,
                        default, default, requiresQuarantine: true);
                    failure = InputControllerOwnerOperationFailure.
                        StopRejected;
                    return false;
                }
                if (state == Switch2ProUsbRuntimeOwnerState.Stopped)
                {
                    lastStopFailure = default;
                    failure = InputControllerOwnerOperationFailure.None;
                    return true;
                }
                if (state == Switch2ProUsbRuntimeOwnerState.Removed ||
                    state == Switch2ProUsbRuntimeOwnerState.
                        AbortedUnpublished)
                {
                    lastStopFailure = new Switch2ProUsbRuntimeStopFailure(
                        Switch2ProUsbRuntimeStopFailureKind.InvalidOwner,
                        default, default, requiresQuarantine: false);
                    failure = InputControllerOwnerOperationFailure.
                        StopRejected;
                    return false;
                }
                if (state == Switch2ProUsbRuntimeOwnerState.Prepared)
                {
                    lastStopFailure = new Switch2ProUsbRuntimeStopFailure(
                        Switch2ProUsbRuntimeStopFailureKind.
                            OperationAlreadyInProgress,
                        default, default, requiresQuarantine: false);
                    failure = InputControllerOwnerOperationFailure.
                        StopRejected;
                    return false;
                }

                if (inputPublicationInProgress ||
                    terminalPublicationThreadId != 0)
                {
                    // Report callbacks can dispatch a stop to another thread
                    // and synchronously wait for it. That thread must not join
                    // the pump worker executing the callback. The future table
                    // adapter drains report leases before retrying this stop.
                    lastStopFailure = new Switch2ProUsbRuntimeStopFailure(
                        Switch2ProUsbRuntimeStopFailureKind.
                            OperationAlreadyInProgress,
                        default, default, requiresQuarantine: false);
                    failure = InputControllerOwnerOperationFailure.
                        StopRejected;
                    return false;
                }

                if (!lifecycleOperationInProgress)
                {
                    lifecycleOperationInProgress = true;
                    state = Switch2ProUsbRuntimeOwnerState.StopRequested;
                    break;
                }

                int remaining = RemainingMilliseconds(deadline,
                    timeoutMilliseconds);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    lastStopFailure = new Switch2ProUsbRuntimeStopFailure(
                        Switch2ProUsbRuntimeStopFailureKind.
                            OperationAlreadyInProgress,
                        default, default, requiresQuarantine: false);
                    failure = InputControllerOwnerOperationFailure.
                        StopRejected;
                    return false;
                }
            }
        }

        bool result = TryStopCore(deadline, timeoutMilliseconds,
            out Switch2ProUsbRuntimeStopFailure stopFailure);
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            lastStopFailure = stopFailure;
            if (result)
            {
                state = Switch2ProUsbRuntimeOwnerState.Stopped;
            }
            else
            {
                requiresQuarantine = true;
                state = Switch2ProUsbRuntimeOwnerState.Quarantined;
            }
            Monitor.PulseAll(gate);
        }

        failure = result ? InputControllerOwnerOperationFailure.None :
            InputControllerOwnerOperationFailure.StopRejected;
        return result;
    }

    public bool TryRemove(DS4Device device, ulong generation,
        out InputControllerOwnerOperationFailure failure)
    {
        lock (gate)
        {
            if (!ReferenceEquals(device, runtimeInputDevice) ||
                generation != deviceGeneration)
            {
                failure = InputControllerOwnerOperationFailure.
                    OwnerAuthenticationFailed;
                return false;
            }
            if (lifecycleOperationInProgress || requiresQuarantine ||
                state != Switch2ProUsbRuntimeOwnerState.Stopped ||
                !runtimeInputDevice.TerminalNeutralCompleted ||
                !runtimeInputDevice.TerminalNeutralReported)
            {
                failure = InputControllerOwnerOperationFailure.RemoveRejected;
                return false;
            }

            state = Switch2ProUsbRuntimeOwnerState.Removed;
            failure = InputControllerOwnerOperationFailure.None;
            return true;
        }
    }

    private bool TryAbortUnpublishedCore(object credentialFence,
        bool requirePreparedCredential,
        in Switch2ProUsbRuntimeSlotAdoptionCredential adoptionCredential,
        bool requireSlotAdoption, int timeoutMilliseconds,
        out Switch2ProUsbRuntimeUnpublishedAbortFailure failure)
    {
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            MaximumStopTimeoutMilliseconds)
        {
            failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                InvalidTimeout;
            return false;
        }

        lock (gate)
        {
            if (!requirePreparedCredential && (requireSlotAdoption ?
                    !adoptionCredential.Authenticates(this,
                        slotAdoptionFence, boundSlotToken,
                        deviceGeneration) :
                    slotAdoptionFence != null))
            {
                failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                    InvalidCredential;
                return false;
            }
            if (creationFailed || requiresQuarantine)
            {
                failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                    QuarantineRequired;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            if (requirePreparedCredential)
            {
                if (!ReferenceEquals(credentialFence,
                        preparedCredentialFence))
                {
                    failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                        StaleCredential;
                    return false;
                }
                if (preparedCredentialConsumed)
                {
                    failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                        AlreadyConsumed;
                    return false;
                }
                if (state != Switch2ProUsbRuntimeOwnerState.Prepared ||
                    commitOpened)
                {
                    failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                        InvalidState;
                    return false;
                }
                preparedCredentialConsumed = true;
            }
            else if (state != Switch2ProUsbRuntimeOwnerState.Created ||
                preparedCredentialFence != null || commitOpened)
            {
                failure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                    InvalidState;
                return false;
            }

            lifecycleOperationInProgress = true;
            state = Switch2ProUsbRuntimeOwnerState.StopRequested;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        bool pumpStopped;
        Switch2ProUsbInputReadPumpFailure pumpFailure;
        try
        {
            int remaining = RemainingMilliseconds(deadline,
                timeoutMilliseconds);
            pumpStopped = requirePreparedCredential ?
                readPump.TryAbortPrepared(remaining, out pumpFailure) :
                readPump.TryStopAndDispose(remaining, out pumpFailure);
        }
        catch
        {
            pumpStopped = false;
            pumpFailure = Switch2ProUsbInputReadPumpFailure.
                UnexpectedWorkerFailure;
        }

        Switch2ProUsbRuntimeUnpublishedAbortFailure resultFailure = default;
        bool zeroReads = readPump.StartedReadCount == 0 &&
            readPump.RetiredReadCount == 0;
        bool runtimeAborted = false;
        if (pumpStopped && zeroReads)
        {
            try
            {
                runtimeAborted = runtimeInputDevice.
                    TryAbortUnpublishedActivation();
            }
            catch
            {
                resultFailure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                    DependencyThrew;
            }
        }
        if (!pumpStopped)
        {
            resultFailure = IsTimeout(pumpFailure,
                    readPump.LastDisposeFailure) ?
                Switch2ProUsbRuntimeUnpublishedAbortFailure.
                    PumpAbortTimedOut :
                Switch2ProUsbRuntimeUnpublishedAbortFailure.
                    PumpAbortRejected;
        }
        else if (!zeroReads)
        {
            resultFailure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                ReadAlreadyStarted;
        }
        else if (!runtimeAborted && resultFailure == default)
        {
            resultFailure = Switch2ProUsbRuntimeUnpublishedAbortFailure.
                RuntimeAbortRejected;
        }

        bool aborted = resultFailure ==
            Switch2ProUsbRuntimeUnpublishedAbortFailure.None;
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            if (aborted)
            {
                state = Switch2ProUsbRuntimeOwnerState.AbortedUnpublished;
            }
            else
            {
                requiresQuarantine = true;
                state = Switch2ProUsbRuntimeOwnerState.Quarantined;
            }
            Monitor.PulseAll(gate);
        }
        failure = resultFailure;
        return aborted;
    }

    private bool TryCleanupFailedUnpublishedPrepare(long deadline,
        int originalTimeout)
    {
        try
        {
            readPump.RequestStop();
            int remaining = RemainingMilliseconds(deadline, originalTimeout);
            if (!readPump.TryStopAndDispose(remaining, out _) ||
                readPump.StartedReadCount != 0 ||
                readPump.RetiredReadCount != 0)
            {
                return false;
            }
            return runtimeInputDevice.TryAbortUnpublishedActivation();
        }
        catch
        {
            return false;
        }
    }

    private bool TryCleanupRejectedCommit(bool dependencyThrew,
        long deadline, int originalTimeout)
    {
        try
        {
            bool stopped;
            if (!dependencyThrew)
            {
                // ISwitch2ProUsbRuntimeReadPump defines false as a rejection
                // before the parked gate opens. Its private exact fence is
                // therefore still authoritative for unpublished cleanup.
                stopped = readPump.TryAbortPrepared(
                    RemainingMilliseconds(deadline, originalTimeout),
                    out _);
                if (!stopped)
                {
                    // A hostile implementation may violate the pre-release
                    // false contract. Fall back to generic retirement using
                    // only the same outer deadline; quarantine remains sticky
                    // regardless of whether this salvage path succeeds.
                    readPump.RequestStop();
                    stopped = readPump.TryStopAndDispose(
                        RemainingMilliseconds(deadline,
                            originalTimeout), out _);
                }
            }
            else
            {
                // A hostile dependency may throw after an unknown side effect.
                // Retire generically and retain sticky quarantine; never claim
                // unpublished proof from the exception path.
                readPump.RequestStop();
                stopped = readPump.TryStopAndDispose(
                    RemainingMilliseconds(deadline, originalTimeout),
                    out _);
            }

            if (!stopped || dependencyThrew ||
                readPump.StartedReadCount != 0 ||
                readPump.RetiredReadCount != 0)
            {
                return false;
            }
            return runtimeInputDevice.TryAbortUnpublishedActivation();
        }
        catch
        {
            return false;
        }
    }

    private bool TryStopCore(long deadline, int originalTimeout,
        out Switch2ProUsbRuntimeStopFailure failure)
    {
        ISwitch2ProUsbRuntimeReadPump pump = readPump;
        if (pump == null)
        {
            return FailStop(Switch2ProUsbRuntimeStopFailureKind.PumpRejected,
                default, default, out failure);
        }

        try
        {
            pump.RequestStop();
        }
        catch
        {
            return FailStop(Switch2ProUsbRuntimeStopFailureKind.
                DependencyThrew, default, default, out failure);
        }

        int remaining = RemainingMilliseconds(deadline, originalTimeout);
        bool pumpStopped;
        Switch2ProUsbInputReadPumpFailure pumpFailure;
        try
        {
            pumpStopped = pump.TryStopAndDispose(remaining,
                out pumpFailure);
        }
        catch
        {
            return FailStop(Switch2ProUsbRuntimeStopFailureKind.
                DependencyThrew, default, default, out failure);
        }
        if (!pumpStopped)
        {
            Switch2ProUsbDisposeFailure disposeFailure = pump.
                LastDisposeFailure;
            return FailStop(IsTimeout(pumpFailure, disposeFailure) ?
                    Switch2ProUsbRuntimeStopFailureKind.PumpTimedOut :
                    Switch2ProUsbRuntimeStopFailureKind.PumpRejected,
                pumpFailure, disposeFailure, out failure);
        }

        lock (gate)
        {
            while (inputPublicationInProgress)
            {
                remaining = RemainingMilliseconds(deadline, originalTimeout);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    return FailStop(
                        Switch2ProUsbRuntimeStopFailureKind.
                            SinkPublicationTimedOut,
                        default, default, out failure);
                }
            }
        }

        Task<Switch2TerminalNeutralRequestResult> publicationTask;
        bool schedule;
        lock (gate)
        {
            schedule = !terminalTaskScheduled;
            if (schedule)
            {
                terminalTaskScheduled = true;
            }
            publicationTask = terminalTask;
        }

        if (schedule)
        {
            try
            {
                if (!terminalScheduler.TrySchedule(PublishTerminalNeutral,
                        out publicationTask) || publicationTask == null)
                {
                    return FailStop(Switch2ProUsbRuntimeStopFailureKind.
                        DependencyThrew, default, default, out failure);
                }
            }
            catch
            {
                return FailStop(Switch2ProUsbRuntimeStopFailureKind.
                    DependencyThrew, default, default, out failure);
            }
            lock (gate)
            {
                terminalTask = publicationTask;
                Monitor.PulseAll(gate);
            }
        }
        else if (publicationTask == null)
        {
            lock (gate)
            {
                while (terminalTask == null)
                {
                    remaining = RemainingMilliseconds(deadline,
                        originalTimeout);
                    if (remaining == 0 || !Monitor.Wait(gate, remaining))
                    {
                        return FailStop(
                            Switch2ProUsbRuntimeStopFailureKind.
                                TerminalPublicationTimedOut,
                            default, default, out failure);
                    }
                }
                publicationTask = terminalTask;
            }
        }

        remaining = RemainingMilliseconds(deadline, originalTimeout);
        try
        {
            if (!publicationTask.Wait(remaining))
            {
                return FailStop(Switch2ProUsbRuntimeStopFailureKind.
                    TerminalPublicationTimedOut, default, default,
                    out failure);
            }
        }
        catch
        {
            return FailStop(Switch2ProUsbRuntimeStopFailureKind.
                DependencyThrew, default, default, out failure);
        }

        Switch2TerminalNeutralRequestResult requestResult =
            publicationTask.Result;
        if (requestResult == Switch2TerminalNeutralRequestResult.
                RejectedAlreadyReserved)
        {
            return FailStop(Switch2ProUsbRuntimeStopFailureKind.
                TerminalPublicationRejected, default, default, out failure);
        }

        remaining = RemainingMilliseconds(deadline, originalTimeout);
        if (!runtimeInputDevice.TryWaitForTerminalNeutralCompletion(remaining))
        {
            return FailStop(Switch2ProUsbRuntimeStopFailureKind.
                TerminalPublicationTimedOut, default, default, out failure);
        }
        if (!runtimeInputDevice.TerminalNeutralReported)
        {
            return FailStop(Switch2ProUsbRuntimeStopFailureKind.
                TerminalDeliveryRejected, default, default, out failure);
        }

        failure = default;
        return true;
    }

    private Switch2TerminalNeutralRequestResult PublishTerminalNeutral()
    {
        lock (gate)
        {
            terminalPublicationThreadId =
                Environment.CurrentManagedThreadId;
        }
        try
        {
            // Runtime Report subscribers are invoked here with no owner or pump
            // lifecycle lock held. This is publication/delivery evidence only;
            // a later service adapter must separately acknowledge its exact
            // typed terminal report lease after On_Report returns.
            return runtimeInputDevice.RequestTerminalNeutral();
        }
        finally
        {
            lock (gate)
            {
                terminalPublicationThreadId = 0;
                Monitor.PulseAll(gate);
            }
        }
    }

    private bool TryPublishExactProfileFrame(
        in Switch2ProProfileInputFrame profileFrame,
        out Switch2ProUsbRuntimeInputFailure failure)
    {
        long deadline = Environment.TickCount64 +
            publicationAdmissionTimeoutMilliseconds;
        while (true)
        {
            Switch2RuntimePublicationResult result = runtimeInputDevice.
                TryPublishProDetailed(profileFrame);
            if (result == Switch2RuntimePublicationResult.Published)
            {
                failure = Switch2ProUsbRuntimeInputFailure.None;
                return true;
            }
            if (result != Switch2RuntimePublicationResult.PublicationBusy)
            {
                failure = FailureFor(result);
                return false;
            }

            int remaining = RemainingMilliseconds(deadline,
                publicationAdmissionTimeoutMilliseconds);
            if (remaining == 0 || !runtimeInputDevice.
                    TryWaitForPublicationAvailability(remaining))
            {
                failure = Switch2ProUsbRuntimeInputFailure.
                    PublicationAdmissionTimedOut;
                return false;
            }
        }
    }

    private static Switch2ProUsbRuntimeInputFailure FailureFor(
        Switch2RuntimePublicationResult result) => result switch
        {
            Switch2RuntimePublicationResult.LifecycleClosed =>
                Switch2ProUsbRuntimeInputFailure.RuntimeLifecycleClosed,
            Switch2RuntimePublicationResult.FrameRejected =>
                Switch2ProUsbRuntimeInputFailure.RuntimeFrameRejected,
            Switch2RuntimePublicationResult.SubscriberRejected =>
                Switch2ProUsbRuntimeInputFailure.RuntimePublicationRejected,
            _ => Switch2ProUsbRuntimeInputFailure.DependencyThrew,
        };

    private bool RejectInputNoLock(Switch2ProUsbRuntimeInputFailure reason)
    {
        lastInputFailure = reason;
        state = Switch2ProUsbRuntimeOwnerState.StopRequested;
        return false;
    }

    private void OnPumpLifecycleAttention(
        Switch2ProUsbInputReadPumpFailure failure)
    {
        if (failure == Switch2ProUsbInputReadPumpFailure.None)
        {
            return;
        }

        bool admitted;
        lock (gate)
        {
            // State becomes Active before the parked pump gate is released.
            // The worker may fail immediately after that release and before
            // TryCommitPrepared returns to set diagnostic commitOpened.
            admitted = state is
                (Switch2ProUsbRuntimeOwnerState.Active or
                    Switch2ProUsbRuntimeOwnerState.StopRequested);
            if (state == Switch2ProUsbRuntimeOwnerState.Active)
            {
                state = Switch2ProUsbRuntimeOwnerState.StopRequested;
            }
        }
        if (admitted)
        {
            RaiseLifecycleAttention(nativeReadFailureAttention);
        }
    }

    private void RaiseLifecycleAttention(
        Switch2ProUsbRuntimeLifecycleAttentionEventArgs evidence)
    {
        if (Interlocked.CompareExchange(ref lifecycleAttentionRaised, 1, 0) !=
            0)
        {
            return;
        }

        EventHandler<Switch2ProUsbRuntimeLifecycleAttentionEventArgs>
            handlers;
        lock (lifecycleAttentionGate)
        {
            handlers = lifecycleAttentionHandlers;
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
                    Switch2ProUsbRuntimeLifecycleAttentionEventArgs>)
                    invocationList[index])(this, evidence);
            }
            catch
            {
                // The exact evidence and owner failure remain sticky. One
                // hostile observer cannot suppress the coordinator observer
                // or cause another callback.
            }
        }
    }

    private void CompleteFailedActivation()
    {
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            state = Switch2ProUsbRuntimeOwnerState.StopRequested;
            Monitor.PulseAll(gate);
        }
    }

    private void MarkCreationQuarantined()
    {
        lock (gate)
        {
            creationFailed = true;
            requiresQuarantine = true;
            state = Switch2ProUsbRuntimeOwnerState.Quarantined;
        }
    }

    private void SetStopFailure(in Switch2ProUsbRuntimeStopFailure failure)
    {
        lock (gate)
        {
            lastStopFailure = failure;
        }
    }

    private static bool FailStop(Switch2ProUsbRuntimeStopFailureKind kind,
        Switch2ProUsbInputReadPumpFailure pumpFailure,
        Switch2ProUsbDisposeFailure disposeFailure,
        out Switch2ProUsbRuntimeStopFailure failure)
    {
        failure = new Switch2ProUsbRuntimeStopFailure(kind, pumpFailure,
            disposeFailure, requiresQuarantine: true);
        return false;
    }

    private static Switch2ProUsbRuntimeCreateFailure CreateFailure(
        Switch2ProUsbRuntimeCreateFailureKind kind) => new(kind, default,
        default, default, default, default, null);

    private static bool IsTimeout(
        Switch2ProUsbDisposeFailure failure) => failure is
        Switch2ProUsbDisposeFailure.NativeTransitionTimedOut or
        Switch2ProUsbDisposeFailure.NativeQuiescenceTimedOut or
        Switch2ProUsbDisposeFailure.ManagedCallbackTimedOut;

    private static bool IsTimeout(Switch2ProUsbInputReadPumpFailure failure,
        Switch2ProUsbDisposeFailure disposeFailure) => failure is
            Switch2ProUsbInputReadPumpFailure.WorkerExitTimedOut or
            Switch2ProUsbInputReadPumpFailure.ReadRetirementRejected ||
        IsTimeout(disposeFailure);

    private static int RemainingMilliseconds(long deadline,
        int originalTimeout)
    {
        if (originalTimeout == 0)
        {
            return 0;
        }
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 : (int)Math.Min(remaining, int.MaxValue);
    }
}
