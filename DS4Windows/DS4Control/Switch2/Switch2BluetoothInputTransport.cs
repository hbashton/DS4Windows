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
/// Scan-owned authority to open one already-remembered BLE peer for read-only
/// input. Only <see cref="Switch2BluetoothCandidateRegistry"/> can create it.
/// It contains no Bluetooth address, bond, key, path, or persistent identity.
/// </summary>
internal readonly struct Switch2BluetoothConnectionAdmission :
    IEquatable<Switch2BluetoothConnectionAdmission>
{
    internal Switch2BluetoothConnectionAdmission(ulong scanGeneration,
        Switch2ControllerModel model, ushort productId)
    {
        ScanGeneration = scanGeneration;
        Model = model;
        ProductId = productId;
        reservation = new Switch2BluetoothConnectionReservation();
    }

    internal ulong ScanGeneration { get; }

    internal Switch2ControllerModel Model { get; }

    internal ushort ProductId { get; }

    private readonly Switch2BluetoothConnectionReservation reservation;

    internal bool IsValid => ScanGeneration != 0 && reservation != null &&
        IsExactPhysicalIdentity(Model, ProductId);

    internal bool TryConsume() => reservation != null &&
        reservation.TryConsume();

    /// <summary>
    /// Atomically consumes one exact left/right admission pair. A concurrent
    /// standalone consumer either wins before this call (consuming neither
    /// peer here) or loses after both peers are consumed here; one-sided pair
    /// consumption is impossible.
    /// </summary>
    internal static bool TryConsumePair(
        in Switch2BluetoothConnectionAdmission left,
        in Switch2BluetoothConnectionAdmission right) =>
        left.IsValid && right.IsValid &&
        left.Model == Switch2ControllerModel.JoyCon2Left &&
        right.Model == Switch2ControllerModel.JoyCon2Right &&
        left.ScanGeneration == right.ScanGeneration &&
        !ReferenceEquals(left.reservation, right.reservation) &&
        Switch2BluetoothConnectionReservation.TryConsumePair(
            left.reservation, right.reservation);

    public bool Equals(Switch2BluetoothConnectionAdmission other) =>
        ScanGeneration == other.ScanGeneration &&
        Model == other.Model && ProductId == other.ProductId &&
        ReferenceEquals(reservation, other.reservation);

    public override bool Equals(object obj) =>
        obj is Switch2BluetoothConnectionAdmission other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(ScanGeneration,
        Model, ProductId, reservation);

    private static bool IsExactPhysicalIdentity(Switch2ControllerModel model,
        ushort productId) => (model, productId) switch
        {
            (Switch2ControllerModel.JoyCon2Right,
                Switch2AdvertisementCodec.JoyCon2RightProductId) => true,
            (Switch2ControllerModel.JoyCon2Left,
                Switch2AdvertisementCodec.JoyCon2LeftProductId) => true,
            (Switch2ControllerModel.ProController2,
                Switch2AdvertisementCodec.ProController2ProductId) => true,
            _ => false,
        };
}

internal sealed class Switch2BluetoothConnectionReservation
{
    private static readonly object PairGate = new();
    private int consumed;

    internal bool TryConsume()
    {
        lock (PairGate)
        {
            if (consumed != 0)
            {
                return false;
            }
            consumed = 1;
            return true;
        }
    }

    internal static bool TryConsumePair(
        Switch2BluetoothConnectionReservation left,
        Switch2BluetoothConnectionReservation right)
    {
        if (left == null || right == null || ReferenceEquals(left, right))
        {
            return false;
        }
        lock (PairGate)
        {
            if (left.consumed != 0 || right.consumed != 0)
            {
                return false;
            }
            left.consumed = 1;
            right.consumed = 1;
            return true;
        }
    }
}

/// <summary>
/// Immutable result of a platform GATT enumeration. Counts are counts of the
/// exact Nintendo service and Common05 characteristic, not total Generic
/// Access/Attribute services exposed by Windows.
/// </summary>
internal readonly struct Switch2BluetoothGattSnapshot
{
    internal Switch2BluetoothGattSnapshot(ulong scanGeneration,
        byte matchingServiceCount, byte matchingCommon05Count,
        Guid serviceUuid, Guid characteristicUuid,
        Switch2GattProperty characteristicProperties)
    {
        ScanGeneration = scanGeneration;
        MatchingServiceCount = matchingServiceCount;
        MatchingCommon05Count = matchingCommon05Count;
        ServiceUuid = serviceUuid;
        CharacteristicUuid = characteristicUuid;
        CharacteristicProperties = characteristicProperties;
    }

    internal ulong ScanGeneration { get; }

    internal byte MatchingServiceCount { get; }

    internal byte MatchingCommon05Count { get; }

    internal Guid ServiceUuid { get; }

    internal Guid CharacteristicUuid { get; }

    internal Switch2GattProperty CharacteristicProperties { get; }

    internal bool IsExactCommon05For(ulong expectedScanGeneration)
    {
        const Switch2GattProperty required = Switch2GattProperty.Read |
            Switch2GattProperty.Notify;
        return expectedScanGeneration != 0 &&
            ScanGeneration == expectedScanGeneration &&
            MatchingServiceCount == 1 && MatchingCommon05Count == 1 &&
            ServiceUuid == Switch2InputCodec.ServiceUuid &&
            CharacteristicUuid ==
                Switch2InputCodec.Common05CharacteristicUuid &&
            CharacteristicProperties == required;
    }
}

internal delegate void Switch2BluetoothInputNotification(
    ulong transportGeneration, Guid serviceUuid, Guid characteristicUuid,
    ReadOnlySpan<byte> body, long completionTimestampQpc);

internal delegate void Switch2BluetoothInputDisconnected(
    ulong transportGeneration);

/// <summary>
/// Narrow capability lease for a future Windows BLE adapter. The only GATT
/// mutation represented here is enabling or disabling notifications on the
/// pre-admitted Common05 characteristic. Pairing, association, arbitrary
/// characteristic writes, NVM access, command channels, and output are absent.
/// Implementations must make both operations non-blocking and callback-safe.
/// </summary>
internal interface ISwitch2BluetoothInputLease
{
    Switch2BluetoothConnectionAdmission Admission { get; }

    Switch2BluetoothGattSnapshot GattSnapshot { get; }

    bool TrySubscribeCccdNotify(ulong transportGeneration,
        Switch2BluetoothInputNotification notification,
        Switch2BluetoothInputDisconnected disconnected);

    bool TryUnsubscribeCccdNone(ulong transportGeneration);
}

internal enum Switch2BluetoothInputLeaseReleaseResult : byte
{
    Invalid = 0,
    Released,
    TimedOut,
    Rejected,
}

/// <summary>
/// Exact bounded teardown proof required by a runtime composition owner. A
/// successful CCCD-None request is only teardown initiation; this separate
/// capability reports the eventual callback/resource-release Boolean for the
/// same transport generation. Implementations must retain ambiguous lifetimes
/// when the wait times out or release returns false.
/// </summary>
internal interface ISwitch2BluetoothInputLeaseReleaseProof
{
    Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
        ulong transportGeneration, int timeoutMilliseconds);
}

internal enum Switch2BluetoothInputEndReason : byte
{
    None = 0,
    Disconnected,
    Stopped,
    QueueOverflow,
    SinkFailure,
    ActivationAborted,
}

/// <summary>
/// Canonical publication boundary. Joy-Con loss and Pro clear are separate so
/// a consumer cannot accidentally treat a single-half loss as a whole-device
/// Pro clear. Calls are serialized by the input owner.
/// </summary>
internal interface ISwitch2BluetoothCanonicalInputSink
{
    // Bounded, nonblocking read of the exact runtime's cold output handoff.
    // Normal gameplay must never opt into latest-state replacement.
    bool IsVirtualOutputTransitionActive => false;

    void PublishPro(in Switch2CanonicalInputFrame frame);

    void PublishJoyCon(in Switch2CanonicalInputFrame frame);

    void ClearPro(ulong deviceGeneration, ulong transportGeneration,
        Switch2BluetoothInputEndReason reason);

    void LoseJoyConHalf(Switch2StickSide side, ulong deviceGeneration,
        ulong transportGeneration, Switch2BluetoothInputEndReason reason);
}

internal enum Switch2BluetoothInputStartFailure : byte
{
    None = 0,
    InvalidArgument,
    InvalidAdmission,
    LeaseIdentityMismatch,
    InvalidGattShape,
    InvalidProtocolIdentity,
    InvalidSessionDescriptor,
    InvalidCalibration,
    SubscriptionFailed,
    SubscriptionInterrupted,
    AdmissionAlreadyConsumed,
    LeaseInspectionFailed,
}

internal enum Switch2BluetoothInputPairPrepareFailure : byte
{
    None = 0,
    InvalidPairAdmission,
    PreflightFailed,
    AdmissionMismatch,
    AdmissionUnavailable,
    SubscriptionFailed,
    SubscriptionInterrupted,
}

internal enum Switch2BluetoothInputPairSideFailure : byte
{
    None = 0,
    NotAttempted,
    InvalidArgument,
    InvalidAdmission,
    LeaseInspectionFailed,
    LeaseIdentityMismatch,
    MissingReleaseProof,
    PairAdmissionMismatch,
    InvalidGattShape,
    InvalidProtocolIdentity,
    InvalidSessionDescriptor,
    InvalidCalibration,
    SubscriptionRejected,
    SubscriptionFaulted,
    SubscriptionInterrupted,
}

internal enum Switch2BluetoothInputCleanupRequestResult : byte
{
    Invalid = 0,
    Accepted,
    Rejected,
    Faulted,
}

/// <summary>
/// Retained exact-generation authority to observe one compensating CCCD-None
/// request. It contains no owner or canonical sink and cannot reactivate input.
/// </summary>
internal readonly struct Switch2BluetoothInputCleanupSideEvidence
{
    private readonly ISwitch2BluetoothInputLeaseReleaseProof releaseProof;

    internal Switch2BluetoothInputCleanupSideEvidence(
        ISwitch2BluetoothInputLeaseReleaseProof releaseProof,
        ulong transportGeneration,
        Switch2BluetoothInputCleanupRequestResult requestResult)
    {
        this.releaseProof = releaseProof;
        TransportGeneration = transportGeneration;
        RequestResult = requestResult;
    }

    internal ulong TransportGeneration { get; }

    internal Switch2BluetoothInputCleanupRequestResult RequestResult { get; }

    internal bool IsValid => releaseProof != null &&
        TransportGeneration != 0 && RequestResult !=
            Switch2BluetoothInputCleanupRequestResult.Invalid;

    internal Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
        int timeoutMilliseconds)
    {
        if (!IsValid || timeoutMilliseconds < 0)
        {
            return Switch2BluetoothInputLeaseReleaseResult.Invalid;
        }
        try
        {
            return releaseProof.WaitForRelease(TransportGeneration,
                timeoutMilliseconds);
        }
        catch
        {
            return Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }
    }
}

internal readonly struct Switch2BluetoothInputPairCleanupEvidence
{
    internal Switch2BluetoothInputPairCleanupEvidence(
        in Switch2BluetoothInputCleanupSideEvidence left,
        in Switch2BluetoothInputCleanupSideEvidence right)
    {
        Left = left;
        Right = right;
    }

    internal Switch2BluetoothInputCleanupSideEvidence Left { get; }

    internal Switch2BluetoothInputCleanupSideEvidence Right { get; }

    internal bool IsValid => Left.IsValid && Right.IsValid;
}

/// <summary>
/// Complete outcome of one pair preparation attempt. A failed consumed attempt
/// exposes only release evidence; retired unpublished owners are never returned.
/// </summary>
internal readonly struct Switch2BluetoothInputPairPrepareResult
{
    internal Switch2BluetoothInputPairPrepareResult(
        Switch2BluetoothInputPairPrepareFailure failure,
        Switch2BluetoothInputPairSideFailure leftFailure,
        Switch2BluetoothInputPairSideFailure rightFailure,
        bool admissionsConsumedByThisCall,
        Switch2BluetoothInputOwner leftOwner,
        in Switch2BluetoothInputPrepareCredential leftCredential,
        Switch2BluetoothInputOwner rightOwner,
        in Switch2BluetoothInputPrepareCredential rightCredential,
        in Switch2BluetoothInputPairCleanupEvidence cleanupEvidence)
    {
        Failure = failure;
        LeftFailure = leftFailure;
        RightFailure = rightFailure;
        AdmissionsConsumedByThisCall = admissionsConsumedByThisCall;
        LeftOwner = leftOwner;
        LeftCredential = leftCredential;
        RightOwner = rightOwner;
        RightCredential = rightCredential;
        CleanupEvidence = cleanupEvidence;
    }

    internal Switch2BluetoothInputPairPrepareFailure Failure { get; }

    internal Switch2BluetoothInputPairSideFailure LeftFailure { get; }

    internal Switch2BluetoothInputPairSideFailure RightFailure { get; }

    internal bool AdmissionsConsumedByThisCall { get; }

    internal Switch2BluetoothInputOwner LeftOwner { get; }

    internal Switch2BluetoothInputPrepareCredential LeftCredential { get; }

    internal Switch2BluetoothInputOwner RightOwner { get; }

    internal Switch2BluetoothInputPrepareCredential RightCredential { get; }

    internal Switch2BluetoothInputPairCleanupEvidence CleanupEvidence
    {
        get;
    }

    internal bool IsPrepared => Failure ==
            Switch2BluetoothInputPairPrepareFailure.None &&
        AdmissionsConsumedByThisCall && LeftOwner != null &&
        RightOwner != null && LeftCredential.IsValid &&
        RightCredential.IsValid && !CleanupEvidence.IsValid;
}

internal enum Switch2BluetoothInputDrainDisposition : byte
{
    Inactive = 0,
    Empty,
    Rejected,
    Published,
    Busy,
}

internal enum Switch2BluetoothInputDrainSignal : byte
{
    Rejected = 0,
    Activated,
    WorkAvailable,
    Retired,
    PumpStopRequested,
}

internal enum Switch2BluetoothInputActivationFailure : byte
{
    None = 0,
    InvalidCredential,
    StaleCredential,
    AlreadyConsumed,
    InvalidState,
    PairOperationRequired,
}

/// <summary>
/// Exact one-shot authority to publish a prepared Bluetooth input lifetime.
/// The private reference fence authenticates the issuing owner while all three
/// externally meaningful generations prevent a copied credential from being
/// applied to another scan, device, or transport lifetime.
/// </summary>
internal readonly struct Switch2BluetoothInputPrepareCredential :
    IEquatable<Switch2BluetoothInputPrepareCredential>
{
    private readonly Switch2BluetoothInputOwner issuer;
    private readonly object fence;

    internal Switch2BluetoothInputPrepareCredential(
        Switch2BluetoothInputOwner issuer, object fence,
        ulong scanGeneration, ulong deviceGeneration,
        ulong transportGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        ScanGeneration = scanGeneration;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
    }

    internal ulong ScanGeneration { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal bool IsValid => issuer != null && fence != null &&
        ScanGeneration != 0 && DeviceGeneration != 0 &&
        TransportGeneration != 0;

    internal bool IsIssuedFor(Switch2BluetoothInputOwner expectedIssuer,
        ulong expectedScanGeneration, ulong expectedDeviceGeneration,
        ulong expectedTransportGeneration) =>
        ReferenceEquals(issuer, expectedIssuer) &&
        ScanGeneration == expectedScanGeneration &&
        DeviceGeneration == expectedDeviceGeneration &&
        TransportGeneration == expectedTransportGeneration;

    internal bool HasFence(object expectedFence) =>
        ReferenceEquals(fence, expectedFence);

    public bool Equals(Switch2BluetoothInputPrepareCredential other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(fence, other.fence) &&
        ScanGeneration == other.ScanGeneration &&
        DeviceGeneration == other.DeviceGeneration &&
        TransportGeneration == other.TransportGeneration;

    public override bool Equals(object obj) =>
        obj is Switch2BluetoothInputPrepareCredential other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(issuer, fence,
        ScanGeneration, DeviceGeneration, TransportGeneration);
}

/// <summary>
/// Generation-owned, bounded Common05 notification ingress. Notifications are
/// copied into fixed storage before the platform callback returns. The owner
/// retains only the latest initial state before activation. Once active, it
/// never overwrites an unread slot: overflow retires the complete generation,
/// increments a visible counter, and clears the queue. Only a lifetime which
/// committed activation emits one loss/clear; pre-commit retirement performs
/// unsubscribe-only cleanup. This type performs no discovery, association,
/// hardware access, output, or production device registration.
/// </summary>
internal sealed class Switch2BluetoothInputOwner
{
    internal const int MaximumQueueCapacity = 64;

    /// <summary>
    /// Private lifetime coupling for one successfully prepared L/R pair. It
    /// is intentionally not an activation capability: its only callback path
    /// retires an unpublished peer after the other half has already retired.
    /// </summary>
    private sealed class PairActivationFence
    {
        private Switch2BluetoothInputOwner left;
        private Switch2BluetoothInputOwner right;
        private int bindState;
        private bool unpublishedCleanupStarted;

        internal void Bind(Switch2BluetoothInputOwner leftOwner,
            Switch2BluetoothInputOwner rightOwner)
        {
            if (leftOwner == null || rightOwner == null ||
                ReferenceEquals(leftOwner, rightOwner) ||
                Interlocked.CompareExchange(ref bindState, -1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "A pair activation fence can be bound exactly once.");
            }
            left = leftOwner;
            right = rightOwner;
            // Publish the complete pair only after both references are stored.
            // Subscription callbacks may arrive on another thread as soon as
            // the platform subscribe call begins.
            Volatile.Write(ref bindState, 1);
        }

        internal bool IsBoundFor(Switch2BluetoothInputOwner owner) =>
            Volatile.Read(ref bindState) == 1 &&
            (ReferenceEquals(owner, left) || ReferenceEquals(owner, right));

        internal void RetirePreparedPairFromCallback(
            Switch2BluetoothInputOwner source,
            Switch2BluetoothInputEndReason reason)
        {
            Switch2BluetoothInputOwner leftOwner = left;
            Switch2BluetoothInputOwner rightOwner = right;
            if (leftOwner == null || rightOwner == null ||
                (!ReferenceEquals(source, leftOwner) &&
                 !ReferenceEquals(source, rightOwner)))
            {
                return;
            }

            lock (leftOwner.sync)
            {
                lock (rightOwner.sync)
                {
                    if (unpublishedCleanupStarted ||
                        source.state != OwnerState.Retired ||
                        source.activationCommitted)
                    {
                        return;
                    }

                    unpublishedCleanupStarted = true;
                    if (leftOwner.state is OwnerState.Preparing or
                            OwnerState.Prepared)
                    {
                        leftOwner.RetireUnpublishedStateNoPulseNoLock(reason);
                    }
                    if (rightOwner.state is OwnerState.Preparing or
                            OwnerState.Prepared)
                    {
                        rightOwner.RetireUnpublishedStateNoPulseNoLock(reason);
                    }
                    System.Threading.Monitor.PulseAll(leftOwner.sync);
                    System.Threading.Monitor.PulseAll(rightOwner.sync);
                }
            }

            // Platform calls remain outside both owner locks and are attempted
            // independently. UnsubscribeOnly contains a throwing lease.
            leftOwner.UnsubscribeOnly();
            rightOwner.UnsubscribeOnly();
        }
    }

    private enum OwnerState : byte
    {
        Preparing = 0,
        Prepared,
        Active,
        Retired,
    }

    private enum SubscriptionAttempt : byte
    {
        Accepted = 0,
        Rejected,
        Faulted,
    }

    private readonly struct PreparationPlan
    {
        internal PreparationPlan(
            in Switch2BluetoothConnectionAdmission admission,
            in Switch2InputSessionDescriptor descriptor,
            in Switch2InputCalibrationSnapshot calibration,
            int queueCapacity,
            ISwitch2BluetoothInputLeaseReleaseProof releaseProof)
        {
            Admission = admission;
            Descriptor = descriptor;
            Calibration = calibration;
            QueueCapacity = queueCapacity;
            ReleaseProof = releaseProof;
        }

        internal Switch2BluetoothConnectionAdmission Admission { get; }

        internal Switch2InputSessionDescriptor Descriptor { get; }

        internal Switch2InputCalibrationSnapshot Calibration { get; }

        internal int QueueCapacity { get; }

        internal ISwitch2BluetoothInputLeaseReleaseProof ReleaseProof
        {
            get;
        }
    }

    private readonly object sync = new();
    private readonly ISwitch2BluetoothInputLease lease;
    private readonly ISwitch2BluetoothCanonicalInputSink sink;
    private readonly Switch2InputSessionDescriptor descriptor;
    private readonly Switch2InputSession session;
    private readonly ulong scanGeneration;
    private readonly object activationFence = new();
    private readonly PairActivationFence pairActivationFence;
    private readonly byte[] queuedBodies;
    private readonly long[] queuedTimestamps;
    private readonly Switch2BluetoothInputNotification notificationCallback;
    private readonly Switch2BluetoothInputDisconnected disconnectedCallback;
    private OwnerState state;
    private int head;
    private int count;
    private long rejectedNotifications;
    private long overflowCount;
    private long publishedCount;
    private long retirementCallbackFailureCount;
    private Switch2InputSessionFailure lastSessionFailure;
    private Switch2BluetoothInputEndReason endReason;
    private bool publicationInProgress;
    private bool retirementCompletionPending;
    private bool preparedCredentialConsumed;
    private bool activationCommitted;
    private object drainPumpFence;
    private bool drainPumpPrepared;
    private bool drainPumpStopRequested;
    private bool drainPumpExited;

    private Switch2BluetoothInputOwner(
        ISwitch2BluetoothInputLease lease,
        ISwitch2BluetoothCanonicalInputSink sink,
        in Switch2InputSessionDescriptor descriptor,
        in Switch2InputCalibrationSnapshot calibration, int queueCapacity,
        ulong scanGeneration, PairActivationFence pairActivationFence = null)
    {
        this.lease = lease;
        this.sink = sink;
        this.descriptor = descriptor;
        this.pairActivationFence = pairActivationFence;
        this.scanGeneration = scanGeneration;
        session = new Switch2InputSession(descriptor, calibration);
        queuedBodies = new byte[queueCapacity *
            Switch2InputCodec.BluetoothLeBodyLength];
        queuedTimestamps = new long[queueCapacity];
        notificationCallback = OnNotification;
        disconnectedCallback = OnDisconnected;
        state = OwnerState.Preparing;
    }

    internal bool IsActive
    {
        get
        {
            lock (sync)
            {
                return state == OwnerState.Active;
            }
        }
    }

    internal bool IsPrepared
    {
        get
        {
            lock (sync)
            {
                return state == OwnerState.Prepared;
            }
        }
    }

    internal int QueueCapacity => queuedTimestamps.Length;

    internal ulong DeviceGeneration => descriptor.DeviceGeneration;

    internal ulong TransportGeneration => descriptor.TransportGeneration;

    internal Switch2InputSessionDescriptor Descriptor => descriptor;

    internal bool ActivationCommitted
    {
        get
        {
            lock (sync)
            {
                return activationCommitted;
            }
        }
    }

    internal bool DrainPumpExited
    {
        get
        {
            lock (sync)
            {
                return drainPumpExited;
            }
        }
    }

    internal int QueuedCount
    {
        get
        {
            lock (sync)
            {
                return count;
            }
        }
    }

    internal long RejectedNotificationCount
    {
        get
        {
            lock (sync)
            {
                return rejectedNotifications;
            }
        }
    }

    internal long OverflowCount
    {
        get
        {
            lock (sync)
            {
                return overflowCount;
            }
        }
    }

    internal long PublishedCount
    {
        get
        {
            lock (sync)
            {
                return publishedCount;
            }
        }
    }

    internal long RetirementCallbackFailureCount
    {
        get
        {
            lock (sync)
            {
                return retirementCallbackFailureCount;
            }
        }
    }

    internal Switch2InputSessionFailure LastSessionFailure
    {
        get
        {
            lock (sync)
            {
                return lastSessionFailure;
            }
        }
    }

    internal Switch2BluetoothInputEndReason EndReason
    {
        get
        {
            lock (sync)
            {
                return endReason;
            }
        }
    }

    internal static bool TryCreate(
        in Switch2BluetoothConnectionAdmission admission,
        ISwitch2BluetoothInputLease lease,
        ISwitch2BluetoothCanonicalInputSink sink, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration, int queueCapacity,
        out Switch2BluetoothInputOwner owner,
        out Switch2BluetoothInputStartFailure failure)
    {
        if (!TryPrepare(admission, lease, sink, deviceGeneration,
                transportGeneration, qpcFrequency, calibration, queueCapacity,
                out Switch2BluetoothInputOwner candidate,
                out Switch2BluetoothInputPrepareCredential credential,
                out failure))
        {
            owner = null;
            return false;
        }

        if (candidate.TryCommitPrepared(credential, out _))
        {
            owner = candidate;
            failure = Switch2BluetoothInputStartFailure.None;
            return true;
        }

        candidate.TryAbortPrepared(credential, out _);
        owner = null;
        failure = Switch2BluetoothInputStartFailure.SubscriptionInterrupted;
        return false;
    }

    /// <summary>
    /// Consumes admission and establishes the exact Common05 subscription, but
    /// leaves canonical publication parked. Valid inline notifications are
    /// copied into a latest-initial-state slot. Only the returned exact credential can
    /// commit or abort this prepared lifetime.
    /// </summary>
    internal static bool TryPrepare(
        in Switch2BluetoothConnectionAdmission admission,
        ISwitch2BluetoothInputLease lease,
        ISwitch2BluetoothCanonicalInputSink sink, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration, int queueCapacity,
        out Switch2BluetoothInputOwner owner,
        out Switch2BluetoothInputPrepareCredential credential,
        out Switch2BluetoothInputStartFailure failure)
    {
        owner = null;
        credential = default;
        if (!TryBuildPreparationPlan(admission, true, lease, sink,
                deviceGeneration, transportGeneration, qpcFrequency,
                calibration, queueCapacity, out PreparationPlan plan,
                out failure))
        {
            return false;
        }

        var candidate = new Switch2BluetoothInputOwner(lease, sink,
            plan.Descriptor, plan.Calibration, plan.QueueCapacity,
            plan.Admission.ScanGeneration);
        if (!plan.Admission.TryConsume())
        {
            failure = Switch2BluetoothInputStartFailure.
                AdmissionAlreadyConsumed;
            return false;
        }

        return TryPrepareAlreadyConsumed(candidate, lease,
            plan.Admission.ScanGeneration, transportGeneration, out owner,
            out credential, out failure);
    }

    /// <summary>
    /// Preflights both exact Joy-Con halves before atomically consuming their
    /// composite admission. The two Common05 subscriptions remain parked until
    /// a later atomic pair commit. No single-admission consume bypass is exposed.
    /// </summary>
    internal static bool TryPreparePair(
        in Switch2JoyConPairConnectionAdmission pairAdmission,
        ISwitch2BluetoothInputLease leftLease,
        ISwitch2BluetoothInputLease rightLease,
        ISwitch2BluetoothCanonicalInputSink sink,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        in Switch2InputCalibrationSnapshot leftCalibration,
        int leftQueueCapacity,
        ulong rightDeviceGeneration, ulong rightTransportGeneration,
        in Switch2InputCalibrationSnapshot rightCalibration,
        int rightQueueCapacity, long qpcFrequency,
        out Switch2BluetoothInputPairPrepareResult result)
    {
        result = default;
        if (!pairAdmission.IsValid)
        {
            result = PairResult(
                Switch2BluetoothInputPairPrepareFailure.InvalidPairAdmission,
                Switch2BluetoothInputPairSideFailure.None,
                Switch2BluetoothInputPairSideFailure.None, false);
            return false;
        }
        if (leftLease != null && ReferenceEquals(leftLease, rightLease))
        {
            result = PairResult(
                Switch2BluetoothInputPairPrepareFailure.AdmissionMismatch,
                Switch2BluetoothInputPairSideFailure.LeaseIdentityMismatch,
                Switch2BluetoothInputPairSideFailure.LeaseIdentityMismatch,
                false);
            return false;
        }

        bool leftValid = TryBuildPreparationPlan(default, false, leftLease,
                sink, leftDeviceGeneration,
                leftTransportGeneration, qpcFrequency, leftCalibration,
                leftQueueCapacity, out PreparationPlan leftPlan,
                out Switch2BluetoothInputStartFailure leftStartFailure);
        bool rightValid = TryBuildPreparationPlan(default, false, rightLease,
                sink, rightDeviceGeneration,
                rightTransportGeneration, qpcFrequency, rightCalibration,
                rightQueueCapacity, out PreparationPlan rightPlan,
                out Switch2BluetoothInputStartFailure rightStartFailure);
        Switch2BluetoothInputPairSideFailure leftFailure = leftValid ?
            Switch2BluetoothInputPairSideFailure.None :
            MapPairSideFailure(leftStartFailure);
        Switch2BluetoothInputPairSideFailure rightFailure = rightValid ?
            Switch2BluetoothInputPairSideFailure.None :
            MapPairSideFailure(rightStartFailure);
        if (!leftValid || !rightValid)
        {
            result = PairResult(
                Switch2BluetoothInputPairPrepareFailure.PreflightFailed,
                leftFailure, rightFailure, false);
            return false;
        }
        if (leftPlan.ReleaseProof == null)
        {
            leftFailure = Switch2BluetoothInputPairSideFailure.
                MissingReleaseProof;
        }
        if (rightPlan.ReleaseProof == null)
        {
            rightFailure = Switch2BluetoothInputPairSideFailure.
                MissingReleaseProof;
        }
        if (leftFailure != Switch2BluetoothInputPairSideFailure.None ||
            rightFailure != Switch2BluetoothInputPairSideFailure.None)
        {
            result = PairResult(
                Switch2BluetoothInputPairPrepareFailure.PreflightFailed,
                leftFailure, rightFailure, false);
            return false;
        }

        bool admissionsMatch = pairAdmission.MatchesExactAdmissions(
            leftPlan.Admission, rightPlan.Admission, out bool leftMatches,
            out bool rightMatches);
        if (!admissionsMatch)
        {
            result = PairResult(
                Switch2BluetoothInputPairPrepareFailure.AdmissionMismatch,
                leftMatches ? Switch2BluetoothInputPairSideFailure.None :
                    Switch2BluetoothInputPairSideFailure.
                        PairAdmissionMismatch,
                rightMatches ? Switch2BluetoothInputPairSideFailure.None :
                    Switch2BluetoothInputPairSideFailure.
                        PairAdmissionMismatch,
                false);
            return false;
        }

        // Allocate both bounded owners before spending either physical
        // admission. After the composite consume, the remaining path performs
        // only platform subscription and fixed-state transitions.
        var pairActivationFence = new PairActivationFence();
        var leftCandidate = new Switch2BluetoothInputOwner(leftLease, sink,
            leftPlan.Descriptor, leftPlan.Calibration,
            leftPlan.QueueCapacity, leftPlan.Admission.ScanGeneration,
            pairActivationFence);
        var rightCandidate = new Switch2BluetoothInputOwner(rightLease, sink,
            rightPlan.Descriptor, rightPlan.Calibration,
            rightPlan.QueueCapacity, rightPlan.Admission.ScanGeneration,
            pairActivationFence);
        pairActivationFence.Bind(leftCandidate, rightCandidate);

        if (!pairAdmission.TryConsume(
                out Switch2BluetoothConnectionAdmission consumedLeft,
                out Switch2BluetoothConnectionAdmission consumedRight))
        {
            result = PairResult(
                Switch2BluetoothInputPairPrepareFailure.AdmissionUnavailable,
                Switch2BluetoothInputPairSideFailure.None,
                Switch2BluetoothInputPairSideFailure.None, false);
            return false;
        }
        if (!consumedLeft.Equals(leftPlan.Admission) ||
            !consumedRight.Equals(rightPlan.Admission))
        {
            leftFailure = Switch2BluetoothInputPairSideFailure.
                PairAdmissionMismatch;
            rightFailure = Switch2BluetoothInputPairSideFailure.
                PairAdmissionMismatch;
            Switch2BluetoothInputPairCleanupEvidence evidence =
                RetireAndRequestPairCleanup(leftCandidate, leftLease,
                    leftPlan.ReleaseProof, leftTransportGeneration,
                    rightCandidate, rightLease, rightPlan.ReleaseProof,
                    rightTransportGeneration, ref leftFailure,
                    ref rightFailure);
            result = PairResult(
                Switch2BluetoothInputPairPrepareFailure.AdmissionMismatch,
                leftFailure, rightFailure, true,
                cleanupEvidence: evidence);
            return false;
        }

        return TryPreparePairAlreadyConsumed(leftCandidate, leftLease,
            leftPlan, rightCandidate, rightLease, rightPlan, out result);
    }

    private static bool TryPrepareAlreadyConsumed(
        Switch2BluetoothInputOwner candidate,
        ISwitch2BluetoothInputLease lease, ulong scanGeneration,
        ulong transportGeneration, out Switch2BluetoothInputOwner owner,
        out Switch2BluetoothInputPrepareCredential credential,
        out Switch2BluetoothInputStartFailure failure)
    {
        credential = default;
        SubscriptionAttempt attempt = TrySubscribe(candidate, lease,
            transportGeneration);
        bool interrupted;
        lock (candidate.sync)
        {
            interrupted = candidate.state != OwnerState.Preparing;
            if (attempt == SubscriptionAttempt.Accepted && !interrupted)
            {
                candidate.state = OwnerState.Prepared;
            }
            else if (!interrupted)
            {
                candidate.RetireUnpublishedNoLock(
                    Switch2BluetoothInputEndReason.None);
            }
        }

        if (attempt != SubscriptionAttempt.Accepted || interrupted)
        {
            // A failed or throwing platform call may still have installed a
            // handler or partially enabled the CCCD. Always issue the sole
            // available compensating operation, but never emit a clear for a
            // lifetime that did not become active.
            _ = TryRequestCleanup(lease, transportGeneration);
            owner = null;
            failure = interrupted ?
                Switch2BluetoothInputStartFailure.SubscriptionInterrupted :
                Switch2BluetoothInputStartFailure.SubscriptionFailed;
            return false;
        }

        owner = candidate;
        credential = new Switch2BluetoothInputPrepareCredential(candidate,
            candidate.activationFence, scanGeneration,
            candidate.descriptor.DeviceGeneration, transportGeneration);
        failure = Switch2BluetoothInputStartFailure.None;
        return true;
    }

    private static bool TryPreparePairAlreadyConsumed(
        Switch2BluetoothInputOwner leftCandidate,
        ISwitch2BluetoothInputLease leftLease,
        in PreparationPlan leftPlan,
        Switch2BluetoothInputOwner rightCandidate,
        ISwitch2BluetoothInputLease rightLease,
        in PreparationPlan rightPlan,
        out Switch2BluetoothInputPairPrepareResult result)
    {
        Switch2BluetoothInputPairSideFailure leftFailure =
            Switch2BluetoothInputPairSideFailure.None;
        Switch2BluetoothInputPairSideFailure rightFailure =
            Switch2BluetoothInputPairSideFailure.NotAttempted;
        SubscriptionAttempt leftAttempt = TrySubscribe(leftCandidate,
            leftLease, leftPlan.Descriptor.TransportGeneration);
        leftFailure = MapSubscriptionAttempt(leftAttempt);
        if (leftAttempt != SubscriptionAttempt.Accepted ||
            !IsPreparing(leftCandidate))
        {
            if (leftFailure == Switch2BluetoothInputPairSideFailure.None)
            {
                leftFailure = Switch2BluetoothInputPairSideFailure.
                    SubscriptionInterrupted;
            }
            return FailConsumedPairPreparation(leftCandidate, leftLease,
                leftPlan, rightCandidate, rightLease, rightPlan,
                ref leftFailure, ref rightFailure, out result);
        }

        SubscriptionAttempt rightAttempt = TrySubscribe(rightCandidate,
            rightLease, rightPlan.Descriptor.TransportGeneration);
        rightFailure = MapSubscriptionAttempt(rightAttempt);
        if (rightAttempt != SubscriptionAttempt.Accepted)
        {
            return FailConsumedPairPreparation(leftCandidate, leftLease,
                leftPlan, rightCandidate, rightLease, rightPlan,
                ref leftFailure, ref rightFailure, out result);
        }

        lock (leftCandidate.sync)
        {
            lock (rightCandidate.sync)
            {
                if (leftCandidate.state == OwnerState.Preparing &&
                    rightCandidate.state == OwnerState.Preparing)
                {
                    leftCandidate.state = OwnerState.Prepared;
                    rightCandidate.state = OwnerState.Prepared;
                    System.Threading.Monitor.PulseAll(leftCandidate.sync);
                    System.Threading.Monitor.PulseAll(rightCandidate.sync);
                }
                else
                {
                    if (leftCandidate.state != OwnerState.Preparing)
                    {
                        leftFailure = Switch2BluetoothInputPairSideFailure.
                            SubscriptionInterrupted;
                    }
                    if (rightCandidate.state != OwnerState.Preparing)
                    {
                        rightFailure = Switch2BluetoothInputPairSideFailure.
                            SubscriptionInterrupted;
                    }
                }
            }
        }
        if (leftFailure != Switch2BluetoothInputPairSideFailure.None ||
            rightFailure != Switch2BluetoothInputPairSideFailure.None)
        {
            return FailConsumedPairPreparation(leftCandidate, leftLease,
                leftPlan, rightCandidate, rightLease, rightPlan,
                ref leftFailure, ref rightFailure, out result);
        }

        var leftCredential = new Switch2BluetoothInputPrepareCredential(
            leftCandidate, leftCandidate.activationFence,
            leftPlan.Admission.ScanGeneration,
            leftPlan.Descriptor.DeviceGeneration,
            leftPlan.Descriptor.TransportGeneration);
        var rightCredential = new Switch2BluetoothInputPrepareCredential(
            rightCandidate, rightCandidate.activationFence,
            rightPlan.Admission.ScanGeneration,
            rightPlan.Descriptor.DeviceGeneration,
            rightPlan.Descriptor.TransportGeneration);
        result = PairResult(Switch2BluetoothInputPairPrepareFailure.None,
            Switch2BluetoothInputPairSideFailure.None,
            Switch2BluetoothInputPairSideFailure.None, true,
            leftCandidate, leftCredential, rightCandidate, rightCredential);
        return true;
    }

    private static bool TryBuildPreparationPlan(
        in Switch2BluetoothConnectionAdmission expectedAdmission,
        bool requireExpectedAdmission,
        ISwitch2BluetoothInputLease lease,
        ISwitch2BluetoothCanonicalInputSink sink, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency,
        in Switch2InputCalibrationSnapshot calibration, int queueCapacity,
        out PreparationPlan plan,
        out Switch2BluetoothInputStartFailure failure)
    {
        plan = default;
        if (lease is null || sink is null || deviceGeneration == 0 ||
            transportGeneration == 0 || qpcFrequency <= 0 ||
            queueCapacity is < 1 or > MaximumQueueCapacity)
        {
            failure = Switch2BluetoothInputStartFailure.InvalidArgument;
            return false;
        }
        if (requireExpectedAdmission && !expectedAdmission.IsValid)
        {
            failure = Switch2BluetoothInputStartFailure.InvalidAdmission;
            return false;
        }

        Switch2BluetoothConnectionAdmission observedAdmission;
        Switch2BluetoothGattSnapshot gatt;
        try
        {
            observedAdmission = lease.Admission;
            gatt = lease.GattSnapshot;
        }
        catch
        {
            failure = Switch2BluetoothInputStartFailure.LeaseInspectionFailed;
            return false;
        }
        if (!observedAdmission.IsValid)
        {
            failure = Switch2BluetoothInputStartFailure.InvalidAdmission;
            return false;
        }
        if (requireExpectedAdmission &&
            !observedAdmission.Equals(expectedAdmission))
        {
            failure = Switch2BluetoothInputStartFailure.LeaseIdentityMismatch;
            return false;
        }
        if (!gatt.IsExactCommon05For(observedAdmission.ScanGeneration))
        {
            failure = Switch2BluetoothInputStartFailure.InvalidGattShape;
            return false;
        }
        if (!Switch2InputProtocolIdentity.TryCreateBluetoothLe(
                gatt.ServiceUuid, gatt.CharacteristicUuid,
                gatt.CharacteristicProperties, observedAdmission.Model,
                out Switch2InputProtocolIdentity identity) ||
            identity.ProtocolRevision !=
                Switch2InputProtocolRevision.BluetoothLeCommon05V1)
        {
            failure = Switch2BluetoothInputStartFailure.InvalidProtocolIdentity;
            return false;
        }
        if (!Switch2InputSessionDescriptor.TryCreate(identity,
                deviceGeneration, transportGeneration, qpcFrequency,
                out Switch2InputSessionDescriptor descriptor))
        {
            failure = Switch2BluetoothInputStartFailure.
                InvalidSessionDescriptor;
            return false;
        }
        if (!calibration.IsValid ||
            calibration.Model != observedAdmission.Model ||
            calibration.DeviceGeneration != deviceGeneration)
        {
            failure = Switch2BluetoothInputStartFailure.InvalidCalibration;
            return false;
        }

        plan = new PreparationPlan(observedAdmission, descriptor,
            calibration, queueCapacity,
            lease as ISwitch2BluetoothInputLeaseReleaseProof);
        failure = Switch2BluetoothInputStartFailure.None;
        return true;
    }

    private static bool FailConsumedPairPreparation(
        Switch2BluetoothInputOwner leftCandidate,
        ISwitch2BluetoothInputLease leftLease,
        in PreparationPlan leftPlan,
        Switch2BluetoothInputOwner rightCandidate,
        ISwitch2BluetoothInputLease rightLease,
        in PreparationPlan rightPlan,
        ref Switch2BluetoothInputPairSideFailure leftFailure,
        ref Switch2BluetoothInputPairSideFailure rightFailure,
        out Switch2BluetoothInputPairPrepareResult result)
    {
        Switch2BluetoothInputPairCleanupEvidence evidence =
            RetireAndRequestPairCleanup(leftCandidate, leftLease,
                leftPlan.ReleaseProof,
                leftPlan.Descriptor.TransportGeneration, rightCandidate,
                rightLease, rightPlan.ReleaseProof,
                rightPlan.Descriptor.TransportGeneration, ref leftFailure,
                ref rightFailure);
        Switch2BluetoothInputPairPrepareFailure failure =
            leftFailure == Switch2BluetoothInputPairSideFailure.
                    SubscriptionInterrupted ||
                rightFailure == Switch2BluetoothInputPairSideFailure.
                    SubscriptionInterrupted ?
                Switch2BluetoothInputPairPrepareFailure.
                    SubscriptionInterrupted :
                Switch2BluetoothInputPairPrepareFailure.SubscriptionFailed;
        result = PairResult(failure, leftFailure, rightFailure, true,
            cleanupEvidence: evidence);
        return false;
    }

    private static Switch2BluetoothInputPairCleanupEvidence
        RetireAndRequestPairCleanup(
            Switch2BluetoothInputOwner leftCandidate,
            ISwitch2BluetoothInputLease leftLease,
            ISwitch2BluetoothInputLeaseReleaseProof leftReleaseProof,
            ulong leftTransportGeneration,
            Switch2BluetoothInputOwner rightCandidate,
            ISwitch2BluetoothInputLease rightLease,
            ISwitch2BluetoothInputLeaseReleaseProof rightReleaseProof,
            ulong rightTransportGeneration,
            ref Switch2BluetoothInputPairSideFailure leftFailure,
            ref Switch2BluetoothInputPairSideFailure rightFailure)
    {
        lock (leftCandidate.sync)
        {
            lock (rightCandidate.sync)
            {
                RetireFailedPairSideNoPulseNoLock(leftCandidate,
                    ref leftFailure);
                RetireFailedPairSideNoPulseNoLock(rightCandidate,
                    ref rightFailure);
                System.Threading.Monitor.PulseAll(leftCandidate.sync);
                System.Threading.Monitor.PulseAll(rightCandidate.sync);
            }
        }

        // Both exact leases are attempted independently and outside owner
        // locks. A false or throwing first attempt cannot suppress the second.
        Switch2BluetoothInputCleanupRequestResult leftRequest =
            TryRequestCleanup(leftLease, leftTransportGeneration);
        Switch2BluetoothInputCleanupRequestResult rightRequest =
            TryRequestCleanup(rightLease, rightTransportGeneration);
        var leftEvidence = new Switch2BluetoothInputCleanupSideEvidence(
            leftReleaseProof, leftTransportGeneration, leftRequest);
        var rightEvidence = new Switch2BluetoothInputCleanupSideEvidence(
            rightReleaseProof, rightTransportGeneration, rightRequest);
        return new Switch2BluetoothInputPairCleanupEvidence(leftEvidence,
            rightEvidence);
    }

    private static void RetireFailedPairSideNoPulseNoLock(
        Switch2BluetoothInputOwner candidate,
        ref Switch2BluetoothInputPairSideFailure failure)
    {
        if (candidate.state == OwnerState.Retired)
        {
            if (failure is Switch2BluetoothInputPairSideFailure.None or
                    Switch2BluetoothInputPairSideFailure.NotAttempted)
            {
                failure = Switch2BluetoothInputPairSideFailure.
                    SubscriptionInterrupted;
            }
            return;
        }
        candidate.RetireUnpublishedStateNoPulseNoLock(
            Switch2BluetoothInputEndReason.None);
    }

    private static bool IsPreparing(Switch2BluetoothInputOwner candidate)
    {
        lock (candidate.sync)
        {
            return candidate.state == OwnerState.Preparing;
        }
    }

    private static SubscriptionAttempt TrySubscribe(
        Switch2BluetoothInputOwner candidate,
        ISwitch2BluetoothInputLease lease, ulong transportGeneration)
    {
        try
        {
            return lease.TrySubscribeCccdNotify(transportGeneration,
                candidate.notificationCallback,
                candidate.disconnectedCallback) ?
                SubscriptionAttempt.Accepted : SubscriptionAttempt.Rejected;
        }
        catch
        {
            return SubscriptionAttempt.Faulted;
        }
    }

    private static Switch2BluetoothInputCleanupRequestResult
        TryRequestCleanup(ISwitch2BluetoothInputLease lease,
            ulong transportGeneration)
    {
        try
        {
            return lease.TryUnsubscribeCccdNone(transportGeneration) ?
                Switch2BluetoothInputCleanupRequestResult.Accepted :
                Switch2BluetoothInputCleanupRequestResult.Rejected;
        }
        catch
        {
            return Switch2BluetoothInputCleanupRequestResult.Faulted;
        }
    }

    private static Switch2BluetoothInputPairSideFailure MapPairSideFailure(
        Switch2BluetoothInputStartFailure failure) => failure switch
        {
            Switch2BluetoothInputStartFailure.InvalidArgument =>
                Switch2BluetoothInputPairSideFailure.InvalidArgument,
            Switch2BluetoothInputStartFailure.InvalidAdmission =>
                Switch2BluetoothInputPairSideFailure.InvalidAdmission,
            Switch2BluetoothInputStartFailure.LeaseInspectionFailed =>
                Switch2BluetoothInputPairSideFailure.LeaseInspectionFailed,
            Switch2BluetoothInputStartFailure.LeaseIdentityMismatch =>
                Switch2BluetoothInputPairSideFailure.LeaseIdentityMismatch,
            Switch2BluetoothInputStartFailure.InvalidGattShape =>
                Switch2BluetoothInputPairSideFailure.InvalidGattShape,
            Switch2BluetoothInputStartFailure.InvalidProtocolIdentity =>
                Switch2BluetoothInputPairSideFailure.InvalidProtocolIdentity,
            Switch2BluetoothInputStartFailure.InvalidSessionDescriptor =>
                Switch2BluetoothInputPairSideFailure.InvalidSessionDescriptor,
            Switch2BluetoothInputStartFailure.InvalidCalibration =>
                Switch2BluetoothInputPairSideFailure.InvalidCalibration,
            _ => Switch2BluetoothInputPairSideFailure.InvalidArgument,
        };

    private static Switch2BluetoothInputPairSideFailure MapSubscriptionAttempt(
        SubscriptionAttempt attempt) => attempt switch
        {
            SubscriptionAttempt.Accepted =>
                Switch2BluetoothInputPairSideFailure.None,
            SubscriptionAttempt.Rejected =>
                Switch2BluetoothInputPairSideFailure.SubscriptionRejected,
            _ => Switch2BluetoothInputPairSideFailure.SubscriptionFaulted,
        };

    private static Switch2BluetoothInputPairPrepareResult PairResult(
        Switch2BluetoothInputPairPrepareFailure failure,
        Switch2BluetoothInputPairSideFailure leftFailure,
        Switch2BluetoothInputPairSideFailure rightFailure,
        bool admissionsConsumedByThisCall) => new(failure, leftFailure,
            rightFailure, admissionsConsumedByThisCall, null, default, null,
            default, default);

    private static Switch2BluetoothInputPairPrepareResult PairResult(
        Switch2BluetoothInputPairPrepareFailure failure,
        Switch2BluetoothInputPairSideFailure leftFailure,
        Switch2BluetoothInputPairSideFailure rightFailure,
        bool admissionsConsumedByThisCall,
        in Switch2BluetoothInputPairCleanupEvidence cleanupEvidence) =>
        new(failure, leftFailure, rightFailure,
            admissionsConsumedByThisCall, null, default, null, default,
            cleanupEvidence);

    private static Switch2BluetoothInputPairPrepareResult PairResult(
        Switch2BluetoothInputPairPrepareFailure failure,
        Switch2BluetoothInputPairSideFailure leftFailure,
        Switch2BluetoothInputPairSideFailure rightFailure,
        bool admissionsConsumedByThisCall,
        Switch2BluetoothInputOwner leftOwner,
        in Switch2BluetoothInputPrepareCredential leftCredential,
        Switch2BluetoothInputOwner rightOwner,
        in Switch2BluetoothInputPrepareCredential rightCredential) =>
        new(failure, leftFailure, rightFailure,
            admissionsConsumedByThisCall, leftOwner, leftCredential,
            rightOwner, rightCredential, default);

    internal bool TryCommitPrepared(
        in Switch2BluetoothInputPrepareCredential credential,
        out Switch2BluetoothInputActivationFailure failure)
    {
        if (!credential.IsValid || !credential.IsIssuedFor(this,
                scanGeneration, descriptor.DeviceGeneration,
                descriptor.TransportGeneration))
        {
            failure = Switch2BluetoothInputActivationFailure.InvalidCredential;
            return false;
        }
        if (pairActivationFence != null)
        {
            failure = Switch2BluetoothInputActivationFailure.
                PairOperationRequired;
            return false;
        }
        lock (sync)
        {
            if (!TryValidatePreparedNoLock(credential,
                    requireDrainPumpReady: true, out failure))
            {
                return false;
            }

            CommitPreparedNoPulseNoLock();
            System.Threading.Monitor.PulseAll(sync);
        }

        failure = Switch2BluetoothInputActivationFailure.None;
        return true;
    }

    internal bool TryAbortPrepared(
        in Switch2BluetoothInputPrepareCredential credential,
        out Switch2BluetoothInputActivationFailure failure)
    {
        if (!credential.IsValid || !credential.IsIssuedFor(this,
                scanGeneration, descriptor.DeviceGeneration,
                descriptor.TransportGeneration))
        {
            failure = Switch2BluetoothInputActivationFailure.InvalidCredential;
            return false;
        }
        if (pairActivationFence != null)
        {
            failure = Switch2BluetoothInputActivationFailure.
                PairOperationRequired;
            return false;
        }
        lock (sync)
        {
            if (!TryValidatePreparedNoLock(credential,
                    requireDrainPumpReady: false, out failure))
            {
                return false;
            }

            RetireUnpublishedNoLock(
                Switch2BluetoothInputEndReason.ActivationAborted);
        }

        UnsubscribeOnly();
        failure = Switch2BluetoothInputActivationFailure.None;
        return true;
    }

    /// <summary>
    /// Atomically commits one exact prepared Joy-Con L/R pair. Both owners and
    /// credentials are validated before either lifetime changes state. The
    /// physical side ordering makes left-then-right the sole lock order for
    /// every pair operation, and parked workers are signaled only after both
    /// lifetimes are active.
    /// </summary>
    internal static bool TryCommitPreparedPair(
        Switch2BluetoothInputOwner leftOwner,
        in Switch2BluetoothInputPrepareCredential leftCredential,
        Switch2BluetoothInputOwner rightOwner,
        in Switch2BluetoothInputPrepareCredential rightCredential,
        out Switch2BluetoothInputActivationFailure failure)
    {
        if (!TryValidateExactPair(leftOwner, leftCredential, rightOwner,
                rightCredential, out failure))
        {
            return false;
        }

        // The model validation above guarantees that every caller acquires the
        // two locks in physical left-then-right order. Individual-owner methods
        // acquire only one lock, so they cannot create a reverse-order cycle.
        lock (leftOwner.sync)
        {
            lock (rightOwner.sync)
            {
                if (!leftOwner.TryValidatePreparedNoLock(leftCredential,
                        requireDrainPumpReady: true, out failure) ||
                    !rightOwner.TryValidatePreparedNoLock(rightCredential,
                        requireDrainPumpReady: true, out failure))
                {
                    return false;
                }

                leftOwner.CommitPreparedNoPulseNoLock();
                rightOwner.CommitPreparedNoPulseNoLock();

                // A waiter cannot observe one active half: both states were
                // committed while both locks were held before either pulse.
                System.Threading.Monitor.PulseAll(leftOwner.sync);
                System.Threading.Monitor.PulseAll(rightOwner.sync);
            }
        }

        failure = Switch2BluetoothInputActivationFailure.None;
        return true;
    }

    /// <summary>
    /// Atomically retires one exact unpublished Joy-Con L/R pair. Both prepared
    /// states are validated before either credential is consumed. CCCD release
    /// is attempted for both sides only after both owner locks are released.
    /// </summary>
    internal static bool TryAbortPreparedPair(
        Switch2BluetoothInputOwner leftOwner,
        in Switch2BluetoothInputPrepareCredential leftCredential,
        Switch2BluetoothInputOwner rightOwner,
        in Switch2BluetoothInputPrepareCredential rightCredential,
        out Switch2BluetoothInputActivationFailure failure)
    {
        if (!TryValidateExactPair(leftOwner, leftCredential, rightOwner,
                rightCredential, out failure))
        {
            return false;
        }

        lock (leftOwner.sync)
        {
            lock (rightOwner.sync)
            {
                if (!leftOwner.TryValidatePreparedNoLock(leftCredential,
                        requireDrainPumpReady: false, out failure) ||
                    !rightOwner.TryValidatePreparedNoLock(rightCredential,
                        requireDrainPumpReady: false, out failure))
                {
                    return false;
                }

                leftOwner.RetireUnpublishedStateNoPulseNoLock(
                    Switch2BluetoothInputEndReason.ActivationAborted);
                rightOwner.RetireUnpublishedStateNoPulseNoLock(
                    Switch2BluetoothInputEndReason.ActivationAborted);
                System.Threading.Monitor.PulseAll(leftOwner.sync);
                System.Threading.Monitor.PulseAll(rightOwner.sync);
            }
        }

        // Keep these as two explicit attempts. A future release implementation
        // that propagates an exception from one side must still reach the other.
        try
        {
            leftOwner.UnsubscribeOnly();
        }
        finally
        {
            rightOwner.UnsubscribeOnly();
        }

        failure = Switch2BluetoothInputActivationFailure.None;
        return true;
    }

    internal Switch2BluetoothInputDrainDisposition DrainOne()
    {
        Switch2CanonicalInputFrame frame;
        bool publishPro;
        lock (sync)
        {
            if (state != OwnerState.Active)
            {
                return Switch2BluetoothInputDrainDisposition.Inactive;
            }
            if (count == 0)
            {
                return Switch2BluetoothInputDrainDisposition.Empty;
            }
            if (publicationInProgress)
            {
                return Switch2BluetoothInputDrainDisposition.Busy;
            }

            int current = head;
            head = (head + 1) % queuedTimestamps.Length;
            count--;
            ReadOnlySpan<byte> body = queuedBodies.AsSpan(current *
                Switch2InputCodec.BluetoothLeBodyLength,
                Switch2InputCodec.BluetoothLeBodyLength);
            if (!session.TryProcess(descriptor, body,
                    queuedTimestamps[current], out frame,
                    out Switch2InputSessionFailure failure))
            {
                rejectedNotifications++;
                lastSessionFailure = failure;
                return Switch2BluetoothInputDrainDisposition.Rejected;
            }
            publicationInProgress = true;
            publishPro = descriptor.Identity.Model ==
                Switch2ControllerModel.ProController2;
        }

        bool published = true;
        try
        {
            if (publishPro)
            {
                sink.PublishPro(frame);
            }
            else
            {
                sink.PublishJoyCon(frame);
            }
        }
        catch
        {
            published = false;
        }

        bool completeRetirement = false;
        Switch2BluetoothInputEndReason retirementReason = default;
        lock (sync)
        {
            publicationInProgress = false;
            if (published)
            {
                publishedCount++;
                lastSessionFailure = Switch2InputSessionFailure.None;
            }
            else
            {
                rejectedNotifications++;
                if (state == OwnerState.Active)
                {
                    completeRetirement = RetireNoLock(
                        Switch2BluetoothInputEndReason.SinkFailure);
                }
            }
            if (retirementCompletionPending)
            {
                retirementCompletionPending = false;
                completeRetirement = true;
            }
            if (completeRetirement)
            {
                retirementReason = endReason;
            }
            System.Threading.Monitor.PulseAll(sync);
        }

        if (completeRetirement)
        {
            CompleteRetirement(retirementReason);
        }
        return published ? Switch2BluetoothInputDrainDisposition.Published :
            Switch2BluetoothInputDrainDisposition.Rejected;
    }

    internal bool Stop() => Retire(descriptor.TransportGeneration,
        Switch2BluetoothInputEndReason.Stopped);

    /// <summary>
    /// Attaches one exact dormant drain worker while this owner is prepared.
    /// The opaque reference fence cannot activate or retire the input lifetime.
    /// </summary>
    internal bool TryAttachDrainPump(object pumpFence)
    {
        if (pumpFence == null)
        {
            return false;
        }

        lock (sync)
        {
            if (state != OwnerState.Prepared || drainPumpFence != null)
            {
                return false;
            }
            drainPumpFence = pumpFence;
            return true;
        }
    }

    /// <summary>
    /// Records control-thread proof that the attached worker reached its start
    /// park. Once a pump is attached, owner commit fails closed until this
    /// proof exists.
    /// </summary>
    internal bool TryMarkDrainPumpPrepared(object pumpFence)
    {
        lock (sync)
        {
            if (!ReferenceEquals(drainPumpFence, pumpFence) ||
                state != OwnerState.Prepared || drainPumpPrepared ||
                drainPumpStopRequested || drainPumpExited)
            {
                return false;
            }
            drainPumpPrepared = true;
            System.Threading.Monitor.PulseAll(sync);
            return true;
        }
    }

    /// <summary>
    /// Completion-driven worker wait. It returns only for exact activation,
    /// publishable queued work, retirement, or the attached pump's own stop
    /// request. There is no timeout or polling path.
    /// </summary>
    internal Switch2BluetoothInputDrainSignal WaitForDrainSignal(
        object pumpFence, bool activationObserved)
    {
        lock (sync)
        {
            while (true)
            {
                if (!ReferenceEquals(drainPumpFence, pumpFence) ||
                    !drainPumpPrepared || drainPumpExited)
                {
                    return Switch2BluetoothInputDrainSignal.Rejected;
                }
                if (drainPumpStopRequested)
                {
                    return Switch2BluetoothInputDrainSignal.
                        PumpStopRequested;
                }
                if (state == OwnerState.Retired)
                {
                    return Switch2BluetoothInputDrainSignal.Retired;
                }
                if (state == OwnerState.Active)
                {
                    if (!activationObserved)
                    {
                        return Switch2BluetoothInputDrainSignal.Activated;
                    }
                    if (count != 0 && !publicationInProgress)
                    {
                        return Switch2BluetoothInputDrainSignal.WorkAvailable;
                    }
                }
                System.Threading.Monitor.Wait(sync);
            }
        }
    }

    internal bool TryRequestDrainPumpStop(object pumpFence)
    {
        lock (sync)
        {
            if (!ReferenceEquals(drainPumpFence, pumpFence) ||
                drainPumpExited)
            {
                return false;
            }
            drainPumpStopRequested = true;
            System.Threading.Monitor.PulseAll(sync);
            return true;
        }
    }

    internal bool TryMarkDrainPumpExited(object pumpFence)
    {
        lock (sync)
        {
            if (!ReferenceEquals(drainPumpFence, pumpFence) ||
                drainPumpExited)
            {
                return false;
            }
            drainPumpExited = true;
            drainPumpPrepared = false;
            System.Threading.Monitor.PulseAll(sync);
            return true;
        }
    }

    private void OnNotification(ulong transportGeneration, Guid serviceUuid,
        Guid characteristicUuid, ReadOnlySpan<byte> body,
        long completionTimestampQpc)
    {
        if (pairActivationFence != null &&
            !pairActivationFence.IsBoundFor(this))
        {
            lock (sync)
            {
                rejectedNotifications++;
            }
            return;
        }
        bool completeRetirement = false;
        lock (sync)
        {
            if (state is not (OwnerState.Preparing or OwnerState.Prepared or
                    OwnerState.Active) ||
                transportGeneration != descriptor.TransportGeneration ||
                serviceUuid != descriptor.Identity.ServiceUuid ||
                characteristicUuid != descriptor.Identity.CharacteristicUuid ||
                body.Length != Switch2InputCodec.BluetoothLeBodyLength ||
                completionTimestampQpc < 0)
            {
                rejectedNotifications++;
                return;
            }

            if (state is OwnerState.Preparing or OwnerState.Prepared ||
                sink.IsVirtualOutputTransitionActive)
            {
                // No virtual input lifetime exists yet. Xbox enumeration or a
                // manual Joy-Con join may take far longer than a report queue
                // can hold. The same applies during an explicit virtual-pad
                // replacement on the serialized controller queue. Keep its
                // current baseline, not old-output transitions to replay on
                // the new pad. Normal active FIFO/overflow remains unchanged.
                int newest = count == 0 ? 0 :
                    (head + count - 1) % queuedTimestamps.Length;
                if (count != 0 && completionTimestampQpc < queuedTimestamps[newest])
                {
                    rejectedNotifications++;
                    return;
                }
                head = 0;
                body.CopyTo(queuedBodies.AsSpan(0, Switch2InputCodec.BluetoothLeBodyLength));
                queuedTimestamps[0] = completionTimestampQpc;
                count = 1;
                System.Threading.Monitor.PulseAll(sync);
            }
            else if (count == queuedTimestamps.Length)
            {
                // Once active, ordered transitions cannot be silently dropped.
                overflowCount++;
                completeRetirement = RetireNoLock(
                    Switch2BluetoothInputEndReason.QueueOverflow);
            }
            else
            {
                int tail = (head + count) % queuedTimestamps.Length;
                body.CopyTo(queuedBodies.AsSpan(tail *
                    Switch2InputCodec.BluetoothLeBodyLength,
                    Switch2InputCodec.BluetoothLeBodyLength));
                queuedTimestamps[tail] = completionTimestampQpc;
                count++;
                System.Threading.Monitor.PulseAll(sync);
            }
        }

        if (completeRetirement)
        {
            CompleteRetirement(Switch2BluetoothInputEndReason.QueueOverflow);
        }
    }

    private void OnDisconnected(ulong transportGeneration)
    {
        if (pairActivationFence != null &&
            !pairActivationFence.IsBoundFor(this))
        {
            return;
        }
        bool completeRetirement = false;
        bool unsubscribeUnpublished = false;
        bool retireUnpublishedPair = false;
        lock (sync)
        {
            if (transportGeneration != descriptor.TransportGeneration)
            {
                return;
            }
            if (state is OwnerState.Preparing or OwnerState.Prepared)
            {
                bool wasPrepared = state == OwnerState.Prepared;
                retireUnpublishedPair = wasPrepared &&
                    pairActivationFence != null;
                unsubscribeUnpublished = wasPrepared &&
                    pairActivationFence == null;
                RetireUnpublishedNoLock(
                    Switch2BluetoothInputEndReason.Disconnected);
            }
            else if (state == OwnerState.Active)
            {
                completeRetirement = RetireNoLock(
                    Switch2BluetoothInputEndReason.Disconnected);
            }
        }

        if (completeRetirement)
        {
            CompleteRetirement(Switch2BluetoothInputEndReason.Disconnected);
        }
        else if (retireUnpublishedPair)
        {
            pairActivationFence.RetirePreparedPairFromCallback(this,
                Switch2BluetoothInputEndReason.Disconnected);
        }
        else if (unsubscribeUnpublished)
        {
            UnsubscribeOnly();
        }
    }

    private bool Retire(ulong transportGeneration,
        Switch2BluetoothInputEndReason reason)
    {
        bool completeRetirement;
        lock (sync)
        {
            if (state != OwnerState.Active ||
                transportGeneration != descriptor.TransportGeneration)
            {
                return false;
            }
            completeRetirement = RetireNoLock(reason);
        }

        if (completeRetirement)
        {
            CompleteRetirement(reason);
        }
        return true;
    }

    private bool RetireNoLock(Switch2BluetoothInputEndReason reason)
    {
        state = OwnerState.Retired;
        endReason = reason;
        head = 0;
        count = 0;
        System.Threading.Monitor.PulseAll(sync);
        if (publicationInProgress)
        {
            retirementCompletionPending = true;
            return false;
        }
        return true;
    }

    private void RetireUnpublishedNoLock(
        Switch2BluetoothInputEndReason reason)
    {
        RetireUnpublishedStateNoPulseNoLock(reason);
        System.Threading.Monitor.PulseAll(sync);
    }

    private void RetireUnpublishedStateNoPulseNoLock(
        Switch2BluetoothInputEndReason reason)
    {
        state = OwnerState.Retired;
        endReason = reason;
        preparedCredentialConsumed = true;
        head = 0;
        count = 0;
    }

    private void CommitPreparedNoPulseNoLock()
    {
        preparedCredentialConsumed = true;
        activationCommitted = true;
        state = OwnerState.Active;
    }

    private bool TryValidatePreparedNoLock(
        in Switch2BluetoothInputPrepareCredential credential,
        bool requireDrainPumpReady,
        out Switch2BluetoothInputActivationFailure failure)
    {
        if (!credential.HasFence(activationFence))
        {
            failure = Switch2BluetoothInputActivationFailure.StaleCredential;
            return false;
        }
        if (preparedCredentialConsumed)
        {
            failure = Switch2BluetoothInputActivationFailure.AlreadyConsumed;
            return false;
        }
        if (state != OwnerState.Prepared)
        {
            failure = Switch2BluetoothInputActivationFailure.InvalidState;
            return false;
        }
        if (requireDrainPumpReady && drainPumpFence != null &&
            (!drainPumpPrepared || drainPumpStopRequested || drainPumpExited))
        {
            failure = Switch2BluetoothInputActivationFailure.InvalidState;
            return false;
        }

        failure = Switch2BluetoothInputActivationFailure.None;
        return true;
    }

    private static bool TryValidateExactPair(
        Switch2BluetoothInputOwner leftOwner,
        in Switch2BluetoothInputPrepareCredential leftCredential,
        Switch2BluetoothInputOwner rightOwner,
        in Switch2BluetoothInputPrepareCredential rightCredential,
        out Switch2BluetoothInputActivationFailure failure)
    {
        if (leftOwner is null || rightOwner is null ||
            ReferenceEquals(leftOwner, rightOwner) ||
            leftOwner.descriptor.Identity.Model !=
                Switch2ControllerModel.JoyCon2Left ||
            rightOwner.descriptor.Identity.Model !=
                Switch2ControllerModel.JoyCon2Right ||
            leftOwner.pairActivationFence == null ||
            !ReferenceEquals(leftOwner.pairActivationFence,
                rightOwner.pairActivationFence) ||
            !leftCredential.IsValid || !rightCredential.IsValid ||
            !leftCredential.IsIssuedFor(leftOwner, leftOwner.scanGeneration,
                leftOwner.descriptor.DeviceGeneration,
                leftOwner.descriptor.TransportGeneration) ||
            !rightCredential.IsIssuedFor(rightOwner,
                rightOwner.scanGeneration,
                rightOwner.descriptor.DeviceGeneration,
                rightOwner.descriptor.TransportGeneration))
        {
            failure = Switch2BluetoothInputActivationFailure.InvalidCredential;
            return false;
        }

        failure = Switch2BluetoothInputActivationFailure.None;
        return true;
    }

    private void UnsubscribeOnly()
    {
        try
        {
            lease.TryUnsubscribeCccdNone(descriptor.TransportGeneration);
        }
        catch
        {
            // The local generation is already retired. A concrete adapter owns
            // any platform-level cleanup diagnostics; this boundary must never
            // turn unpublished cleanup failure into a canonical loss event.
        }
    }

    private void CompleteRetirement(Switch2BluetoothInputEndReason reason)
    {
        // The generation is already retired before this call. A platform that
        // invokes a late callback synchronously from unsubscribe therefore
        // cannot enqueue or publish it.
        UnsubscribeOnly();
        try
        {
            if (descriptor.Identity.Model ==
                Switch2ControllerModel.ProController2)
            {
                sink.ClearPro(descriptor.DeviceGeneration,
                    descriptor.TransportGeneration, reason);
                return;
            }

            sink.LoseJoyConHalf(descriptor.Identity.Model ==
                    Switch2ControllerModel.JoyCon2Left ?
                        Switch2StickSide.Left : Switch2StickSide.Right,
                descriptor.DeviceGeneration, descriptor.TransportGeneration,
                reason);
        }
        catch
        {
            lock (sync)
            {
                retirementCallbackFailureCount++;
            }
        }
    }
}
