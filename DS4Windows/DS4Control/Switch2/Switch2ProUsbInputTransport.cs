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

/// <summary>
/// Candidate-specific, read-only Windows discovery seam. Implementations take
/// one atomic snapshot of a composite root and its present child interfaces.
/// Paths, serial numbers, handles, and other raw identities never cross this
/// boundary.
/// </summary>
public interface ISwitch2ProUsbOsDiscoveryAdapter
{
    bool TryObserveComposite(out Switch2ProUsbCompositeObservation observation);
}

/// <summary>
/// Native lease opener kept separate from descriptor discovery. It must reopen
/// and verify the exact registration before returning a lease; the owner also
/// checks the returned registration before accepting it.
/// </summary>
public interface ISwitch2ProUsbNativeAdapter
{
    bool TryOpenReadOnlyComposite(
        in Switch2PhysicalInputRegistration registration,
        out ISwitch2ProUsbReadOnlyCompositeLease lease);
}

/// <summary>
/// The only live native surface in this tranche. The lease retains both the
/// admitted MI_00 input interface and the MI_01 topology/presence lease, but it
/// exposes input reads only. In particular, it has no command, output, feature,
/// LED, or haptic operation.
/// </summary>
public interface ISwitch2ProUsbReadOnlyCompositeLease
{
    Switch2PhysicalInputRegistration Registration { get; }

    /// <summary>
    /// Starts exactly one 64-byte MI_00 read. A false result guarantees that no
    /// completion for <paramref name="claim"/> has run or can subsequently run.
    /// A true result permits a completion before this method returns.
    /// </summary>
    bool TryBeginInputRead(byte[] destination, int offset, int count,
        in Switch2ProUsbReadClaim claim,
        ISwitch2ProUsbReadCompletionTarget completionTarget);

    /// <summary>
    /// Requests cancellation without waiting. Repeated calls for the same claim
    /// are prohibited by the owner. This exact-claim operation may run while
    /// retirement is waiting; implementations must serialize native release
    /// against cancellation without preventing that stop path.
    /// </summary>
    bool TryCancelInputRead(in Switch2ProUsbReadClaim claim);

    /// <summary>
    /// Waits for the exact submitted read to reach native and managed callback
    /// quiescence, then releases only that submission's reusable native
    /// storage. A successful call permits the next read; it does not quiesce
    /// or dispose the composite lease. A false result retains ownership and
    /// permits an exact retry. Begin never retires a prior submission
    /// implicitly: callers must cross this exact-claim boundary first.
    /// </summary>
    bool TryRetireCompletedInputRead(in Switch2ProUsbReadClaim claim,
        int timeoutMilliseconds);

    /// <summary>
    /// Waits at most <paramref name="timeoutMilliseconds"/>. A true result is a
    /// native guarantee that the input read is quiescent and no later completion
    /// can be made for any previously submitted claim. The owner serializes
    /// this call after the start and cancellation methods have returned; lease
    /// implementations need not make those three control calls concurrent-safe.
    /// </summary>
    bool TryWaitForInputQuiescence(int timeoutMilliseconds);

    /// <summary>
    /// Releases an already native/managed-quiescent lease. Implementations must
    /// not submit controller I/O or invoke callbacks from this method.
    /// Synchronous managed/native resource release may block and has no hard
    /// wall-clock bound unless a stronger implementation contract says so.
    /// </summary>
    void DisposeQuiesced();
}

public interface ISwitch2ProUsbReadCompletionTarget
{
    Switch2ProUsbReadCompletionDisposition CompleteInputRead(
        in Switch2ProUsbReadClaim claim, int bytesTransferred,
        long completionTimestampQpc, Switch2ProUsbNativeReadStatus status);
}

/// <summary>
/// Downstream publication seam. The owner calls it only after the packet has
/// been copied into an immutable canonical frame, and never while holding its
/// lifecycle lock.
/// </summary>
public interface ISwitch2ProUsbInputSink
{
    bool TryPublish(in Switch2CanonicalInputFrame frame);
}

public enum Switch2ProUsbNativeReadStatus : byte
{
    Invalid = 0,
    Completed,
    Cancelled,
    DeviceRemoved,
    Failed,
}

public enum Switch2ProUsbInputTransportState : byte
{
    Invalid = 0,
    Open,
    StopRequested,
    Quiesced,
    Disposing,
    Disposed,
}

public enum Switch2ProUsbTransportCreateFailureKind : byte
{
    None = 0,
    MissingDependency,
    DiscoveryRejected,
    CompositeRejected,
    InvalidLifetime,
    AdapterRejected,
    NativeLeaseRejected,
    LeaseRegistrationMismatch,
}

public readonly struct Switch2ProUsbTransportCreateFailure
{
    internal Switch2ProUsbTransportCreateFailure(
        Switch2ProUsbTransportCreateFailureKind kind,
        Switch2PhysicalAdmissionFailure admissionFailure,
        Switch2PhysicalInputFailure inputFailure)
        : this(kind, admissionFailure, inputFailure, default, null)
    {
    }

    internal Switch2ProUsbTransportCreateFailure(
        Switch2ProUsbTransportCreateFailureKind kind,
        Switch2PhysicalAdmissionFailure admissionFailure,
        Switch2PhysicalInputFailure inputFailure,
        Switch2ProUsbDisposeFailure rejectedLeaseDisposeFailure,
        Switch2ProUsbRejectedLeaseOwner quarantinedLeaseOwner)
    {
        Kind = kind;
        AdmissionFailure = admissionFailure;
        InputFailure = inputFailure;
        RejectedLeaseDisposeFailure = rejectedLeaseDisposeFailure;
        QuarantinedLeaseOwner = quarantinedLeaseOwner;
    }

    public Switch2ProUsbTransportCreateFailureKind Kind { get; }

    public Switch2PhysicalAdmissionFailure AdmissionFailure { get; }

    public Switch2PhysicalInputFailure InputFailure { get; }

    /// <summary>
    /// Exact failure from cleanup attempted when a native adapter returned a
    /// lease that could not be admitted. Native-quiescence waiting consumes the
    /// supplied budget; synchronous resource release has no hard deadline. None
    /// means either no lease was returned or cleanup succeeded.
    /// </summary>
    public Switch2ProUsbDisposeFailure RejectedLeaseDisposeFailure { get; }

    /// <summary>
    /// Retains the exact rejected native lease when cleanup could not prove
    /// release within its managed quiescence-wait budget. Callers must keep this
    /// owner and may retry only the operation it exposes; the raw lease and
    /// controller identity stay hidden.
    /// </summary>
    public Switch2ProUsbRejectedLeaseOwner QuarantinedLeaseOwner { get; }

    public bool RequiresQuarantine => QuarantinedLeaseOwner != null;

    public bool IsNone => Kind == Switch2ProUsbTransportCreateFailureKind.None;
}

public enum Switch2ProUsbReadBeginFailure : byte
{
    None = 0,
    LifecycleClosed,
    ReadAlreadyOutstanding,
    SequenceExhausted,
    NativeStartRejected,
    OwnershipRejected,
}

public enum Switch2ProUsbReadCompletionDisposition : byte
{
    Invalid = 0,
    Published,
    SinkRejected,
    StaleClaim,
    LifecycleSuppressed,
    NativeCancelled,
    NativeFailure,
    InvalidReport,
}

public enum Switch2ProUsbReadRetirementFailure : byte
{
    None = 0,
    InvalidTimeout,
    InvalidClaim,
    OperationAlreadyInProgress,
    NativeQuiescenceTimedOut,
}

/// <summary>
/// Claim-keyed result published only after the exact native submission and
/// its managed completion callback are quiescent. A successful retirement can
/// have no completion when cancellation reached native quiescence without a
/// callback; that result is stop-only and can never authorize rearm.
/// </summary>
public readonly struct Switch2ProUsbReadRetirementResult
{
    internal Switch2ProUsbReadRetirementResult(
        in Switch2ProUsbReadClaim claim, bool completionObserved,
        Switch2ProUsbReadCompletionDisposition completionDisposition)
    {
        Claim = claim;
        CompletionObserved = completionObserved;
        CompletionDisposition = completionDisposition;
    }

    public Switch2ProUsbReadClaim Claim { get; }

    public bool CompletionObserved { get; }

    public Switch2ProUsbReadCompletionDisposition CompletionDisposition
    {
        get;
    }

    public bool PermitsRearm => CompletionObserved &&
        CompletionDisposition ==
            Switch2ProUsbReadCompletionDisposition.Published;
}

public enum Switch2ProUsbDisposeFailure : byte
{
    None = 0,
    InvalidTimeout,
    OperationAlreadyInProgress,
    NativeTransitionTimedOut,
    NativeQuiescenceTimedOut,
    ManagedCallbackTimedOut,
    NativeDisposeRejected,
    NativeQuiescenceRejected,
}

public enum Switch2ProUsbRejectedLeaseState : byte
{
    Invalid = 0,
    Retained,
    Quarantined,
    Disposed,
}

/// <summary>
/// Retains a native lease that was returned by the open boundary but rejected
/// before any input read could be submitted. Native-quiescence waiting is
/// budgeted and the exact lease remains strongly owned after a timeout or
/// dependency exception; synchronous release itself has no hard deadline.
/// A disposal exception is outcome-uncertain, so that state is quarantined and
/// disposal is never attempted a second time.
/// </summary>
public sealed class Switch2ProUsbRejectedLeaseOwner
{
    private readonly object gate = new();
    private readonly ISwitch2ProUsbReadOnlyCompositeLease nativeLease;

    private Switch2ProUsbRejectedLeaseState state =
        Switch2ProUsbRejectedLeaseState.Retained;
    private bool lifecycleOperationInProgress;

    internal Switch2ProUsbRejectedLeaseOwner(
        ISwitch2ProUsbReadOnlyCompositeLease nativeLease)
    {
        this.nativeLease = nativeLease ??
            throw new ArgumentNullException(nameof(nativeLease));
    }

    public Switch2ProUsbRejectedLeaseState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public bool TryQuiesceAndDispose(int timeoutMilliseconds,
        out Switch2ProUsbDisposeFailure failure)
    {
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
                Switch2ProUsbInputTransportOwner.
                    MaximumDisposeTimeoutMilliseconds)
        {
            failure = Switch2ProUsbDisposeFailure.InvalidTimeout;
            return false;
        }

        lock (gate)
        {
            if (state == Switch2ProUsbRejectedLeaseState.Disposed)
            {
                failure = Switch2ProUsbDisposeFailure.None;
                return true;
            }
            if (state == Switch2ProUsbRejectedLeaseState.Quarantined)
            {
                failure = Switch2ProUsbDisposeFailure.NativeDisposeRejected;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2ProUsbDisposeFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            lifecycleOperationInProgress = true;
        }

        bool quiesced;
        try
        {
            quiesced = nativeLease.TryWaitForInputQuiescence(
                timeoutMilliseconds);
        }
        catch
        {
            ReleaseOperation();
            failure = Switch2ProUsbDisposeFailure.
                NativeQuiescenceRejected;
            return false;
        }

        if (!quiesced)
        {
            ReleaseOperation();
            failure = Switch2ProUsbDisposeFailure.
                NativeQuiescenceTimedOut;
            return false;
        }

        try
        {
            nativeLease.DisposeQuiesced();
        }
        catch
        {
            lock (gate)
            {
                state = Switch2ProUsbRejectedLeaseState.Quarantined;
                lifecycleOperationInProgress = false;
                Monitor.PulseAll(gate);
            }
            failure = Switch2ProUsbDisposeFailure.NativeDisposeRejected;
            return false;
        }

        lock (gate)
        {
            state = Switch2ProUsbRejectedLeaseState.Disposed;
            lifecycleOperationInProgress = false;
            Monitor.PulseAll(gate);
        }
        failure = Switch2ProUsbDisposeFailure.None;
        return true;
    }

    private void ReleaseOperation()
    {
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            Monitor.PulseAll(gate);
        }
    }
}

/// <summary>
/// Opaque read ownership token. The private owner fence prevents a claim from
/// another owner with coincidentally equal generations and sequence from being
/// admitted. No raw device identity is exposed or formatted.
/// </summary>
public readonly struct Switch2ProUsbReadClaim :
    IEquatable<Switch2ProUsbReadClaim>
{
    private readonly object ownerFence;

    internal Switch2ProUsbReadClaim(object ownerFence,
        ulong deviceGeneration, ulong transportGeneration, ulong sequence)
    {
        this.ownerFence = ownerFence;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        Sequence = sequence;
    }

    public ulong DeviceGeneration { get; }

    public ulong TransportGeneration { get; }

    public ulong Sequence { get; }

    public bool IsValid => ownerFence != null && DeviceGeneration != 0 &&
        TransportGeneration != 0 && Sequence != 0;

    public bool Equals(Switch2ProUsbReadClaim other) =>
        ReferenceEquals(ownerFence, other.ownerFence) &&
        DeviceGeneration == other.DeviceGeneration &&
        TransportGeneration == other.TransportGeneration &&
        Sequence == other.Sequence;

    public override bool Equals(object obj) =>
        obj is Switch2ProUsbReadClaim other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        ownerFence == null ? 0 : ownerFence.GetHashCode(), DeviceGeneration,
        TransportGeneration, Sequence);
}

/// <summary>
/// Owns one exact 057E:2069/bcd0201 MI_00+MI_01 read-only USB lifetime.
/// Construction performs discovery admission before opening a native lease.
/// Reads are single-owner and generation fenced. Stop, budgeted native-
/// quiescence waiting, and disposal are explicit; this type starts no thread and performs no
/// output or persistent/system operation.
/// </summary>
public sealed class Switch2ProUsbInputTransportOwner :
    ISwitch2ProUsbReadCompletionTarget
{
    public const int MaximumDisposeTimeoutMilliseconds = 5_000;

    private readonly object gate = new();
    private readonly byte[] inputBuffer = new byte[
        Switch2PhysicalInputRegistration.ProUsbReportByteLength];
    private readonly ISwitch2ProUsbReadOnlyCompositeLease nativeLease;
    private readonly ISwitch2ProUsbInputSink sink;
    private readonly Switch2PhysicalInputLifetime lifetime;
    private readonly Switch2PhysicalInputAdapter inputAdapter;

    private Switch2ProUsbInputTransportState state =
        Switch2ProUsbInputTransportState.Open;
    private Switch2ProUsbReadClaim activeClaim;
    private Switch2ProUsbReadClaim nativeSubmissionClaim;
    private ulong readSequence;
    private bool readOutstanding;
    private bool nativeBeginInProgress;
    private bool completionInProgress;
    private bool publicationInProgress;
    private bool cancellationIssued;
    private bool nativeCancellationInProgress;
    private bool nativeQuiescent;
    private bool lifecycleOperationInProgress;
    private object continuousPumpFence;
    private Switch2ProUsbReadClaim completedClaim;
    private Switch2ProUsbReadCompletionDisposition completedDisposition;

    private Switch2ProUsbInputTransportOwner(
        ISwitch2ProUsbReadOnlyCompositeLease nativeLease,
        ISwitch2ProUsbInputSink sink,
        in Switch2PhysicalInputLifetime lifetime,
        Switch2PhysicalInputAdapter inputAdapter)
    {
        this.nativeLease = nativeLease;
        this.sink = sink;
        this.lifetime = lifetime;
        this.inputAdapter = inputAdapter;
    }

    public Switch2PhysicalInputLifetime Lifetime => lifetime;

    public Switch2ProUsbInputTransportState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public static bool TryCreate(ISwitch2ProUsbOsDiscoveryAdapter discovery,
        ISwitch2ProUsbNativeAdapter nativeAdapter,
        ISwitch2ProUsbInputSink sink, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration,
        out Switch2ProUsbInputTransportOwner owner,
        out Switch2ProUsbTransportCreateFailure failure)
    {
        owner = null;
        if (discovery == null || nativeAdapter == null || sink == null)
        {
            failure = CreateFailure(
                Switch2ProUsbTransportCreateFailureKind.MissingDependency);
            return false;
        }

        Switch2ProUsbCompositeObservation observation;
        try
        {
            if (!discovery.TryObserveComposite(out observation))
            {
                failure = CreateFailure(
                    Switch2ProUsbTransportCreateFailureKind.
                        DiscoveryRejected);
                return false;
            }
        }
        catch
        {
            failure = CreateFailure(
                Switch2ProUsbTransportCreateFailureKind.DiscoveryRejected);
            return false;
        }

        if (!Switch2PhysicalDeviceFactory.TryAdmitProUsb(observation,
                out Switch2PhysicalInputRegistration registration,
                out Switch2PhysicalAdmissionFailure admissionFailure))
        {
            failure = new Switch2ProUsbTransportCreateFailure(
                Switch2ProUsbTransportCreateFailureKind.CompositeRejected,
                admissionFailure, default);
            return false;
        }
        if (!Switch2PhysicalInputLifetime.TryCreate(registration,
                deviceGeneration, transportGeneration, qpcFrequency,
                out Switch2PhysicalInputLifetime lifetime))
        {
            failure = CreateFailure(
                Switch2ProUsbTransportCreateFailureKind.InvalidLifetime);
            return false;
        }

        return TryCreateFromAdmittedLifetime(nativeAdapter, sink, lifetime,
            calibration, out owner, out failure);
    }

    /// <summary>
    /// Owned-composite construction seam. The exact registration and clock
    /// facts come only from the already-admitted lifetime; this method performs
    /// no OS discovery and cannot select another present controller.
    /// </summary>
    internal static bool TryCreateFromAdmittedLifetime(
        ISwitch2ProUsbNativeAdapter nativeAdapter,
        ISwitch2ProUsbInputSink sink,
        in Switch2PhysicalInputLifetime lifetime,
        in Switch2InputCalibrationSnapshot calibration,
        out Switch2ProUsbInputTransportOwner owner,
        out Switch2ProUsbTransportCreateFailure failure)
    {
        owner = null;
        if (nativeAdapter == null || sink == null)
        {
            failure = CreateFailure(
                Switch2ProUsbTransportCreateFailureKind.MissingDependency);
            return false;
        }
        if (!lifetime.IsValid)
        {
            failure = CreateFailure(
                Switch2ProUsbTransportCreateFailureKind.InvalidLifetime);
            return false;
        }
        Switch2PhysicalInputRegistration registration = lifetime.Registration;
        if (!Switch2PhysicalInputAdapter.TryCreate(lifetime, calibration,
                out Switch2PhysicalInputAdapter inputAdapter,
                out Switch2PhysicalInputFailure inputFailure))
        {
            failure = new Switch2ProUsbTransportCreateFailure(
                Switch2ProUsbTransportCreateFailureKind.AdapterRejected,
                default, inputFailure);
            return false;
        }

        ISwitch2ProUsbReadOnlyCompositeLease lease = null;
        bool leaseOpened;
        try
        {
            leaseOpened = nativeAdapter.TryOpenReadOnlyComposite(registration,
                out lease);
        }
        catch
        {
            leaseOpened = false;
        }
        if (!leaseOpened || lease == null)
        {
            if (lease != null)
            {
                failure = CreateRejectedLeaseFailure(
                    Switch2ProUsbTransportCreateFailureKind.
                        NativeLeaseRejected, lease);
                return false;
            }
            failure = CreateFailure(
                Switch2ProUsbTransportCreateFailureKind.NativeLeaseRejected);
            return false;
        }

        bool leaseMatches;
        try
        {
            leaseMatches = lease.Registration.Equals(registration);
        }
        catch
        {
            leaseMatches = false;
        }
        if (!leaseMatches)
        {
            failure = CreateRejectedLeaseFailure(
                Switch2ProUsbTransportCreateFailureKind.
                    LeaseRegistrationMismatch, lease);
            return false;
        }

        owner = new Switch2ProUsbInputTransportOwner(lease, sink, lifetime,
            inputAdapter);
        failure = default;
        return true;
    }

    public bool TryBeginRead(out Switch2ProUsbReadClaim claim,
        out Switch2ProUsbReadBeginFailure failure) =>
        TryBeginReadCore(null, out claim, out failure);

    internal bool TryBeginRead(object ownerFence,
        out Switch2ProUsbReadClaim claim,
        out Switch2ProUsbReadBeginFailure failure) =>
        TryBeginReadCore(ownerFence, out claim, out failure);

    private bool TryBeginReadCore(object ownerFence,
        out Switch2ProUsbReadClaim claim,
        out Switch2ProUsbReadBeginFailure failure)
    {
        lock (gate)
        {
            if (!ReadOwnerMatchesNoLock(ownerFence))
            {
                claim = default;
                failure = Switch2ProUsbReadBeginFailure.OwnershipRejected;
                return false;
            }
            if (state != Switch2ProUsbInputTransportState.Open)
            {
                claim = default;
                failure = Switch2ProUsbReadBeginFailure.LifecycleClosed;
                return false;
            }
            if (readOutstanding || nativeBeginInProgress ||
                completionInProgress || publicationInProgress ||
                lifecycleOperationInProgress ||
                nativeSubmissionClaim.IsValid)
            {
                claim = default;
                failure = Switch2ProUsbReadBeginFailure.
                    ReadAlreadyOutstanding;
                return false;
            }
            if (readSequence == ulong.MaxValue)
            {
                state = Switch2ProUsbInputTransportState.StopRequested;
                claim = default;
                failure = Switch2ProUsbReadBeginFailure.SequenceExhausted;
                Monitor.PulseAll(gate);
                return false;
            }

            readSequence++;
            Switch2InputSessionDescriptor descriptor =
                lifetime.SessionDescriptor;
            claim = new Switch2ProUsbReadClaim(gate,
                descriptor.DeviceGeneration, descriptor.TransportGeneration,
                readSequence);
            activeClaim = claim;
            nativeSubmissionClaim = claim;
            completedClaim = default;
            completedDisposition = default;
            readOutstanding = true;
            nativeBeginInProgress = true;
            cancellationIssued = false;
        }

        bool started;
        bool startThrew = false;
        try
        {
            started = nativeLease.TryBeginInputRead(inputBuffer, 0,
                inputBuffer.Length, claim, this);
        }
        catch
        {
            started = false;
            startThrew = true;
        }

        bool issueCancellation = false;
        Switch2ProUsbReadClaim cancellationClaim = default;
        lock (gate)
        {
            nativeBeginInProgress = false;
            if (!started && !startThrew && readOutstanding &&
                activeClaim.Equals(claim))
            {
                readOutstanding = false;
                activeClaim = default;
                nativeSubmissionClaim = default;
            }
            if ((started || startThrew) &&
                state != Switch2ProUsbInputTransportState.Open &&
                TryReserveCancellationNoLock(out cancellationClaim))
            {
                issueCancellation = true;
            }
            Monitor.PulseAll(gate);
        }

        if (issueCancellation)
        {
            TryCancelNative(cancellationClaim);
        }
        if (!started)
        {
            RequestStop();
            claim = default;
            failure = Switch2ProUsbReadBeginFailure.NativeStartRejected;
            return false;
        }

        failure = Switch2ProUsbReadBeginFailure.None;
        return true;
    }

    /// <summary>
    /// Retires the exact completed native submission without closing the
    /// transport. This is the completion-driven rearm boundary used by the
    /// continuous input pump; it performs no polling, discovery, publication,
    /// or controller output.
    /// </summary>
    public bool TryRetireCompletedRead(in Switch2ProUsbReadClaim claim,
        int timeoutMilliseconds,
        out Switch2ProUsbReadRetirementFailure failure) =>
        TryRetireCompletedReadCore(null, claim, timeoutMilliseconds,
            out _, out failure);

    public bool TryRetireCompletedRead(in Switch2ProUsbReadClaim claim,
        int timeoutMilliseconds,
        out Switch2ProUsbReadRetirementResult result,
        out Switch2ProUsbReadRetirementFailure failure) =>
        TryRetireCompletedReadCore(null, claim, timeoutMilliseconds,
            out result, out failure);

    internal bool TryRetireCompletedRead(object ownerFence,
        in Switch2ProUsbReadClaim claim, int timeoutMilliseconds,
        out Switch2ProUsbReadRetirementFailure failure) =>
        TryRetireCompletedReadCore(ownerFence, claim, timeoutMilliseconds,
            out _, out failure);

    internal bool TryRetireCompletedRead(object ownerFence,
        in Switch2ProUsbReadClaim claim, int timeoutMilliseconds,
        out Switch2ProUsbReadRetirementResult result,
        out Switch2ProUsbReadRetirementFailure failure) =>
        TryRetireCompletedReadCore(ownerFence, claim, timeoutMilliseconds,
            out result, out failure);

    private bool TryRetireCompletedReadCore(object ownerFence,
        in Switch2ProUsbReadClaim claim, int timeoutMilliseconds,
        out Switch2ProUsbReadRetirementResult result,
        out Switch2ProUsbReadRetirementFailure failure)
    {
        result = default;
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            MaximumDisposeTimeoutMilliseconds)
        {
            failure = Switch2ProUsbReadRetirementFailure.InvalidTimeout;
            return false;
        }

        lock (gate)
        {
            if (!ReadOwnerMatchesNoLock(ownerFence) || !claim.IsValid ||
                !nativeSubmissionClaim.Equals(claim))
            {
                failure = Switch2ProUsbReadRetirementFailure.InvalidClaim;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2ProUsbReadRetirementFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            lifecycleOperationInProgress = true;
        }

        bool retired;
        try
        {
            retired = nativeLease.TryRetireCompletedInputRead(claim,
                timeoutMilliseconds);
        }
        catch
        {
            retired = false;
        }

        lock (gate)
        {
            if (retired && nativeSubmissionClaim.Equals(claim))
            {
                bool completionObserved = completedClaim.Equals(claim);
                Switch2ProUsbReadCompletionDisposition disposition =
                    completionObserved ? completedDisposition :
                    Switch2ProUsbReadCompletionDisposition.Invalid;
                result = new Switch2ProUsbReadRetirementResult(claim,
                    completionObserved, disposition);
                nativeSubmissionClaim = default;
                if (readOutstanding && activeClaim.Equals(claim))
                {
                    // Native quiescence without a completion callback cannot
                    // be treated as a successful input report. Retire the
                    // exact claim and stop this lifetime fail-closed.
                    readOutstanding = false;
                    activeClaim = default;
                    cancellationIssued = false;
                    if (state == Switch2ProUsbInputTransportState.Open)
                    {
                        state = Switch2ProUsbInputTransportState.
                            StopRequested;
                    }
                }
                if (completionObserved)
                {
                    completedClaim = default;
                    completedDisposition = default;
                }
            }
            else if (retired)
            {
                // The lifecycle fence makes this unreachable for a conforming
                // lease. Fail closed instead of reporting retirement for a
                // claim which is no longer the tracked native submission.
                retired = false;
            }
            lifecycleOperationInProgress = false;
            Monitor.PulseAll(gate);
        }

        failure = retired ? Switch2ProUsbReadRetirementFailure.None :
            Switch2ProUsbReadRetirementFailure.NativeQuiescenceTimedOut;
        return retired;
    }

    public bool RequestStop()
    {
        bool changed = false;
        bool issueCancellation = false;
        Switch2ProUsbReadClaim cancellationClaim = default;
        lock (gate)
        {
            if (state == Switch2ProUsbInputTransportState.Open)
            {
                state = Switch2ProUsbInputTransportState.StopRequested;
                changed = true;
            }
            if (state == Switch2ProUsbInputTransportState.StopRequested &&
                TryReserveCancellationNoLock(out cancellationClaim))
            {
                issueCancellation = true;
            }
            Monitor.PulseAll(gate);
        }

        if (issueCancellation)
        {
            TryCancelNative(cancellationClaim);
        }
        return changed;
    }

    public bool TryQuiesceAndDispose(int timeoutMilliseconds,
        out Switch2ProUsbDisposeFailure failure) =>
        TryQuiesceAndDisposeCore(null, timeoutMilliseconds, out failure);

    internal bool TryQuiesceAndDispose(object ownerFence,
        int timeoutMilliseconds, out Switch2ProUsbDisposeFailure failure) =>
        TryQuiesceAndDisposeCore(ownerFence, timeoutMilliseconds,
            out failure);

    private bool TryQuiesceAndDisposeCore(object ownerFence,
        int timeoutMilliseconds, out Switch2ProUsbDisposeFailure failure)
    {
        if (timeoutMilliseconds < 0 ||
            timeoutMilliseconds > MaximumDisposeTimeoutMilliseconds)
        {
            failure = Switch2ProUsbDisposeFailure.InvalidTimeout;
            return false;
        }

        RequestStop();
        lock (gate)
        {
            if (!ReadOwnerMatchesNoLock(ownerFence))
            {
                failure = Switch2ProUsbDisposeFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            if (state == Switch2ProUsbInputTransportState.Disposed)
            {
                failure = Switch2ProUsbDisposeFailure.None;
                return true;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2ProUsbDisposeFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            lifecycleOperationInProgress = true;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        lock (gate)
        {
            while (nativeBeginInProgress || nativeCancellationInProgress)
            {
                int remaining = RemainingMilliseconds(deadline,
                    timeoutMilliseconds);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    lifecycleOperationInProgress = false;
                    Monitor.PulseAll(gate);
                    failure = Switch2ProUsbDisposeFailure.
                        NativeTransitionTimedOut;
                    return false;
                }
            }
        }

        if (!nativeQuiescent)
        {
            int remaining = RemainingMilliseconds(deadline,
                timeoutMilliseconds);
            bool quiesced;
            try
            {
                quiesced = nativeLease.TryWaitForInputQuiescence(remaining);
            }
            catch
            {
                quiesced = false;
            }
            if (!quiesced)
            {
                ReleaseLifecycleOperation();
                failure = Switch2ProUsbDisposeFailure.
                    NativeQuiescenceTimedOut;
                return false;
            }

            lock (gate)
            {
                nativeQuiescent = true;
                // The native true result guarantees there can be no future
                // callback for this claim, including a cancelled read whose
                // platform API reports quiescence without a completion.
                readOutstanding = false;
                activeClaim = default;
                nativeSubmissionClaim = default;
                cancellationIssued = false;
                Monitor.PulseAll(gate);
            }
        }

        lock (gate)
        {
            while (nativeBeginInProgress || completionInProgress ||
                   publicationInProgress)
            {
                int remaining = RemainingMilliseconds(deadline,
                    timeoutMilliseconds);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    lifecycleOperationInProgress = false;
                    Monitor.PulseAll(gate);
                    failure = Switch2ProUsbDisposeFailure.
                        ManagedCallbackTimedOut;
                    return false;
                }
            }
            state = Switch2ProUsbInputTransportState.Quiesced;
            state = Switch2ProUsbInputTransportState.Disposing;
        }

        bool disposed = true;
        try
        {
            nativeLease.DisposeQuiesced();
        }
        catch
        {
            disposed = false;
        }

        lock (gate)
        {
            state = disposed ? Switch2ProUsbInputTransportState.Disposed :
                Switch2ProUsbInputTransportState.Quiesced;
            lifecycleOperationInProgress = false;
            Monitor.PulseAll(gate);
        }
        failure = disposed ? Switch2ProUsbDisposeFailure.None :
            Switch2ProUsbDisposeFailure.NativeDisposeRejected;
        return disposed;
    }

    internal bool TryAttachContinuousPump(object ownerFence)
    {
        if (ownerFence == null)
        {
            return false;
        }
        lock (gate)
        {
            if (state != Switch2ProUsbInputTransportState.Open ||
                continuousPumpFence != null || readOutstanding ||
                nativeBeginInProgress || completionInProgress ||
                publicationInProgress || lifecycleOperationInProgress ||
                nativeSubmissionClaim.IsValid)
            {
                return false;
            }
            continuousPumpFence = ownerFence;
            return true;
        }
    }

    private bool ReadOwnerMatchesNoLock(object ownerFence) =>
        continuousPumpFence == null ? ownerFence == null :
            ReferenceEquals(continuousPumpFence, ownerFence);

    public Switch2ProUsbReadCompletionDisposition CompleteInputRead(
        in Switch2ProUsbReadClaim claim, int bytesTransferred,
        long completionTimestampQpc, Switch2ProUsbNativeReadStatus status)
    {
        lock (gate)
        {
            if (!readOutstanding || !activeClaim.Equals(claim))
            {
                return Switch2ProUsbReadCompletionDisposition.StaleClaim;
            }

            readOutstanding = false;
            activeClaim = default;
            cancellationIssued = false;
            completionInProgress = true;
            Monitor.PulseAll(gate);
        }

        if (status != Switch2ProUsbNativeReadStatus.Completed)
        {
            bool lifecycleSuppressed;
            Switch2ProUsbReadCompletionDisposition disposition =
                status == Switch2ProUsbNativeReadStatus.Cancelled ?
                    Switch2ProUsbReadCompletionDisposition.NativeCancelled :
                    Switch2ProUsbReadCompletionDisposition.NativeFailure;
            lock (gate)
            {
                lifecycleSuppressed = state !=
                    Switch2ProUsbInputTransportState.Open;
                completionInProgress = false;
                if (!lifecycleSuppressed)
                {
                    state = Switch2ProUsbInputTransportState.StopRequested;
                }
                RecordCompletionNoLock(claim, disposition);
                Monitor.PulseAll(gate);
            }
            return disposition;
        }

        Switch2CanonicalInputFrame frame = default;
        bool parsed = bytesTransferred == inputBuffer.Length &&
            completionTimestampQpc >= 0 &&
            inputAdapter.TryProcess(lifetime,
                inputBuffer.AsSpan(0, bytesTransferred),
                completionTimestampQpc,
                out frame, out _);

        lock (gate)
        {
            completionInProgress = false;
            if (!parsed)
            {
                RecordCompletionNoLock(claim,
                    Switch2ProUsbReadCompletionDisposition.InvalidReport);
                Monitor.PulseAll(gate);
                return Switch2ProUsbReadCompletionDisposition.InvalidReport;
            }
            if (state != Switch2ProUsbInputTransportState.Open)
            {
                RecordCompletionNoLock(claim,
                    Switch2ProUsbReadCompletionDisposition.
                        LifecycleSuppressed);
                Monitor.PulseAll(gate);
                return Switch2ProUsbReadCompletionDisposition.
                    LifecycleSuppressed;
            }
            publicationInProgress = true;
            Monitor.PulseAll(gate);
        }

        bool published;
        try
        {
            published = sink.TryPublish(frame);
        }
        catch
        {
            published = false;
            RequestStop();
        }
        finally
        {
            lock (gate)
            {
                publicationInProgress = false;
                Monitor.PulseAll(gate);
            }
        }

        if (!published)
        {
            // Publication refusal means there is no live consumer for this
            // lifetime. Fail closed instead of silently reading and dropping.
            RequestStop();
        }

        Switch2ProUsbReadCompletionDisposition finalDisposition = published ?
            Switch2ProUsbReadCompletionDisposition.Published :
            Switch2ProUsbReadCompletionDisposition.SinkRejected;
        lock (gate)
        {
            RecordCompletionNoLock(claim, finalDisposition);
            Monitor.PulseAll(gate);
        }
        return finalDisposition;
    }

    private void RecordCompletionNoLock(in Switch2ProUsbReadClaim claim,
        Switch2ProUsbReadCompletionDisposition disposition)
    {
        completedClaim = claim;
        completedDisposition = disposition;
    }

    private bool TryReserveCancellationNoLock(
        out Switch2ProUsbReadClaim claim)
    {
        if (!readOutstanding || nativeBeginInProgress || cancellationIssued ||
            nativeCancellationInProgress)
        {
            claim = default;
            return false;
        }

        cancellationIssued = true;
        nativeCancellationInProgress = true;
        claim = activeClaim;
        return true;
    }

    private void ReleaseLifecycleOperation()
    {
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            Monitor.PulseAll(gate);
        }
    }

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

    private bool TryCancelNative(in Switch2ProUsbReadClaim claim)
    {
        try
        {
            return nativeLease.TryCancelInputRead(claim);
        }
        catch
        {
            return false;
        }
        finally
        {
            lock (gate)
            {
                nativeCancellationInProgress = false;
                Monitor.PulseAll(gate);
            }
        }
    }

    private static Switch2ProUsbTransportCreateFailure
        CreateRejectedLeaseFailure(
            Switch2ProUsbTransportCreateFailureKind kind,
            ISwitch2ProUsbReadOnlyCompositeLease lease)
    {
        var retainedOwner = new Switch2ProUsbRejectedLeaseOwner(lease);
        if (retainedOwner.TryQuiesceAndDispose(0,
                out Switch2ProUsbDisposeFailure disposeFailure))
        {
            return new Switch2ProUsbTransportCreateFailure(kind, default,
                default, disposeFailure, null);
        }

        return new Switch2ProUsbTransportCreateFailure(kind, default,
            default, disposeFailure, retainedOwner);
    }

    private static Switch2ProUsbTransportCreateFailure CreateFailure(
        Switch2ProUsbTransportCreateFailureKind kind) => new(kind, default,
        default);
}
