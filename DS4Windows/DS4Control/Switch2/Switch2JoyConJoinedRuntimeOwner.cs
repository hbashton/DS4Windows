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

internal enum Switch2JoyConJoinedRuntimeCreateFailureKind : byte
{
    None = 0,
    MissingDependency,
    InvalidArgument,
    MissingReleaseProof,
    RuntimeDeviceRejected,
    RegistrationRejected,
    SinkRejected,
    PairInputRejected,
    DescriptorBindRejected,
    LeftPumpRejected,
    RightPumpRejected,
    AttentionRejected,
    DependencyThrew,
    RollbackRejected,
    RollbackTimedOut,
}

internal readonly struct Switch2JoyConJoinedRuntimeCreateFailure
{
    internal Switch2JoyConJoinedRuntimeCreateFailure(
        Switch2JoyConJoinedRuntimeCreateFailureKind kind,
        Switch2RuntimeInputDeviceCreateFailure runtimeFailure,
        InputControllerRegistrationFailure registrationFailure,
        Switch2JoyConJoinedRuntimeSinkFailure sinkFailure,
        in Switch2BluetoothInputPairPrepareResult pairResult,
        Switch2BluetoothInputDrainPumpFailure leftPumpFailure,
        Switch2BluetoothInputDrainPumpFailure rightPumpFailure,
        Switch2BluetoothInputLeaseReleaseResult leftReleaseResult,
        Switch2BluetoothInputLeaseReleaseResult rightReleaseResult,
        Switch2JoyConJoinedRuntimeOwner quarantinedOwner)
    {
        Kind = kind;
        RuntimeFailure = runtimeFailure;
        RegistrationFailure = registrationFailure;
        SinkFailure = sinkFailure;
        PairResult = pairResult;
        LeftPumpFailure = leftPumpFailure;
        RightPumpFailure = rightPumpFailure;
        LeftReleaseResult = leftReleaseResult;
        RightReleaseResult = rightReleaseResult;
        QuarantinedOwner = quarantinedOwner;
    }

    internal Switch2JoyConJoinedRuntimeCreateFailureKind Kind { get; }
    internal Switch2RuntimeInputDeviceCreateFailure RuntimeFailure { get; }
    internal InputControllerRegistrationFailure RegistrationFailure { get; }
    internal Switch2JoyConJoinedRuntimeSinkFailure SinkFailure { get; }
    internal Switch2BluetoothInputPairPrepareResult PairResult { get; }
    internal Switch2BluetoothInputDrainPumpFailure LeftPumpFailure { get; }
    internal Switch2BluetoothInputDrainPumpFailure RightPumpFailure { get; }
    internal Switch2BluetoothInputLeaseReleaseResult LeftReleaseResult { get; }
    internal Switch2BluetoothInputLeaseReleaseResult RightReleaseResult { get; }
    internal Switch2JoyConJoinedRuntimeOwner QuarantinedOwner { get; }
    internal bool RequiresQuarantine => QuarantinedOwner != null;
}

internal readonly struct Switch2JoyConJoinedRuntimeStopFailure
{
    internal Switch2JoyConJoinedRuntimeStopFailure(
        Switch2BluetoothRuntimeStopFailureKind kind,
        Switch2BluetoothInputDrainPumpFailure leftPumpFailure,
        Switch2BluetoothInputDrainPumpFailure rightPumpFailure,
        Switch2BluetoothInputLeaseReleaseResult leftReleaseResult,
        Switch2BluetoothInputLeaseReleaseResult rightReleaseResult,
        Switch2JoyConJoinedRuntimeSinkFailure sinkFailure)
    {
        Kind = kind;
        LeftPumpFailure = leftPumpFailure;
        RightPumpFailure = rightPumpFailure;
        LeftReleaseResult = leftReleaseResult;
        RightReleaseResult = rightReleaseResult;
        SinkFailure = sinkFailure;
    }

    internal Switch2BluetoothRuntimeStopFailureKind Kind { get; }
    internal Switch2BluetoothInputDrainPumpFailure LeftPumpFailure { get; }
    internal Switch2BluetoothInputDrainPumpFailure RightPumpFailure { get; }
    internal Switch2BluetoothInputLeaseReleaseResult LeftReleaseResult
    { get; }
    internal Switch2BluetoothInputLeaseReleaseResult RightReleaseResult
    { get; }
    internal Switch2JoyConJoinedRuntimeSinkFailure SinkFailure { get; }
}

internal readonly struct Switch2JoyConJoinedRuntimeSlotAdoptionCredential :
    IEquatable<Switch2JoyConJoinedRuntimeSlotAdoptionCredential>
{
    private readonly Switch2JoyConJoinedRuntimeOwner issuer;
    private readonly object fence;
    private readonly InputControllerSlotToken token;

    internal Switch2JoyConJoinedRuntimeSlotAdoptionCredential(
        Switch2JoyConJoinedRuntimeOwner issuer, object fence,
        in InputControllerSlotToken token, ulong runtimeGeneration,
        ulong pairEpoch, Switch2JoyConPairId pairId,
        ulong pairRecordRevision, ulong scanGeneration,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        this.token = token;
        RuntimeGeneration = runtimeGeneration;
        PairEpoch = pairEpoch;
        PairId = pairId;
        PairRecordRevision = pairRecordRevision;
        ScanGeneration = scanGeneration;
        LeftDeviceGeneration = leftDeviceGeneration;
        LeftTransportGeneration = leftTransportGeneration;
        RightDeviceGeneration = rightDeviceGeneration;
        RightTransportGeneration = rightTransportGeneration;
    }

    internal ulong RuntimeGeneration { get; }
    internal ulong PairEpoch { get; }
    internal Switch2JoyConPairId PairId { get; }
    internal ulong PairRecordRevision { get; }
    internal ulong ScanGeneration { get; }
    internal ulong LeftDeviceGeneration { get; }
    internal ulong LeftTransportGeneration { get; }
    internal ulong RightDeviceGeneration { get; }
    internal ulong RightTransportGeneration { get; }
    internal InputControllerSlotToken SlotToken => token;
    internal object Fence => fence;
    internal bool IsValid => issuer != null && fence != null && token.IsValid &&
        RuntimeGeneration != 0 && PairEpoch != 0 && PairId.IsValid &&
        PairRecordRevision != 0 && ScanGeneration != 0 &&
        LeftDeviceGeneration != 0 && LeftTransportGeneration != 0 &&
        RightDeviceGeneration != 0 && RightTransportGeneration != 0;

    internal bool Authenticates(Switch2JoyConJoinedRuntimeOwner candidate,
        object expectedFence, in InputControllerSlotToken expectedToken,
        ulong expectedRuntimeGeneration, ulong expectedPairEpoch,
        Switch2JoyConPairId expectedPairId, ulong expectedPairRecordRevision,
        ulong expectedScanGeneration, ulong expectedLeftDeviceGeneration,
        ulong expectedLeftTransportGeneration,
        ulong expectedRightDeviceGeneration,
        ulong expectedRightTransportGeneration) =>
        ReferenceEquals(issuer, candidate) &&
        ReferenceEquals(fence, expectedFence) && token.Equals(expectedToken) &&
        RuntimeGeneration == expectedRuntimeGeneration &&
        PairEpoch == expectedPairEpoch && PairId == expectedPairId &&
        PairRecordRevision == expectedPairRecordRevision &&
        ScanGeneration == expectedScanGeneration &&
        LeftDeviceGeneration == expectedLeftDeviceGeneration &&
        LeftTransportGeneration == expectedLeftTransportGeneration &&
        RightDeviceGeneration == expectedRightDeviceGeneration &&
        RightTransportGeneration == expectedRightTransportGeneration;

    public bool Equals(
        Switch2JoyConJoinedRuntimeSlotAdoptionCredential other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(fence, other.fence) && token.Equals(other.token) &&
        RuntimeGeneration == other.RuntimeGeneration &&
        PairEpoch == other.PairEpoch && PairId == other.PairId &&
        PairRecordRevision == other.PairRecordRevision &&
        ScanGeneration == other.ScanGeneration &&
        LeftDeviceGeneration == other.LeftDeviceGeneration &&
        LeftTransportGeneration == other.LeftTransportGeneration &&
        RightDeviceGeneration == other.RightDeviceGeneration &&
        RightTransportGeneration == other.RightTransportGeneration;

    public override bool Equals(object obj) => obj is
        Switch2JoyConJoinedRuntimeSlotAdoptionCredential other &&
        Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : RuntimeHelpers.GetHashCode(issuer),
        fence == null ? 0 : RuntimeHelpers.GetHashCode(fence), token,
        RuntimeGeneration, PairEpoch, PairId, PairRecordRevision,
        HashCode.Combine(ScanGeneration, LeftDeviceGeneration,
            LeftTransportGeneration, RightDeviceGeneration,
            RightTransportGeneration));
}

internal readonly struct Switch2JoyConJoinedRuntimePrepareCredential :
    IEquatable<Switch2JoyConJoinedRuntimePrepareCredential>
{
    private readonly Switch2JoyConJoinedRuntimeOwner issuer;
    private readonly object fence;
    private readonly InputControllerSlotToken token;

    internal Switch2JoyConJoinedRuntimePrepareCredential(
        Switch2JoyConJoinedRuntimeOwner issuer, object fence,
        in InputControllerSlotToken token, ulong runtimeGeneration,
        ulong pairEpoch, Switch2JoyConPairId pairId,
        ulong pairRecordRevision, ulong scanGeneration,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        this.token = token;
        RuntimeGeneration = runtimeGeneration;
        PairEpoch = pairEpoch;
        PairId = pairId;
        PairRecordRevision = pairRecordRevision;
        ScanGeneration = scanGeneration;
        LeftDeviceGeneration = leftDeviceGeneration;
        LeftTransportGeneration = leftTransportGeneration;
        RightDeviceGeneration = rightDeviceGeneration;
        RightTransportGeneration = rightTransportGeneration;
    }

    internal ulong RuntimeGeneration { get; }
    internal ulong PairEpoch { get; }
    internal Switch2JoyConPairId PairId { get; }
    internal ulong PairRecordRevision { get; }
    internal ulong ScanGeneration { get; }
    internal ulong LeftDeviceGeneration { get; }
    internal ulong LeftTransportGeneration { get; }
    internal ulong RightDeviceGeneration { get; }
    internal ulong RightTransportGeneration { get; }
    internal object Fence => fence;
    internal bool IsValid => issuer != null && fence != null && token.IsValid &&
        RuntimeGeneration != 0 && PairEpoch != 0 && PairId.IsValid &&
        PairRecordRevision != 0 && ScanGeneration != 0 &&
        LeftDeviceGeneration != 0 && LeftTransportGeneration != 0 &&
        RightDeviceGeneration != 0 && RightTransportGeneration != 0;

    internal bool Authenticates(Switch2JoyConJoinedRuntimeOwner candidate,
        object expectedFence, in InputControllerSlotToken expectedToken,
        ulong expectedRuntimeGeneration, ulong expectedPairEpoch,
        Switch2JoyConPairId expectedPairId, ulong expectedPairRecordRevision,
        ulong expectedScanGeneration, ulong expectedLeftDeviceGeneration,
        ulong expectedLeftTransportGeneration,
        ulong expectedRightDeviceGeneration,
        ulong expectedRightTransportGeneration) =>
        ReferenceEquals(issuer, candidate) &&
        ReferenceEquals(fence, expectedFence) && token.Equals(expectedToken) &&
        RuntimeGeneration == expectedRuntimeGeneration &&
        PairEpoch == expectedPairEpoch && PairId == expectedPairId &&
        PairRecordRevision == expectedPairRecordRevision &&
        ScanGeneration == expectedScanGeneration &&
        LeftDeviceGeneration == expectedLeftDeviceGeneration &&
        LeftTransportGeneration == expectedLeftTransportGeneration &&
        RightDeviceGeneration == expectedRightDeviceGeneration &&
        RightTransportGeneration == expectedRightTransportGeneration;

    public bool Equals(Switch2JoyConJoinedRuntimePrepareCredential other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(fence, other.fence) && token.Equals(other.token) &&
        RuntimeGeneration == other.RuntimeGeneration &&
        PairEpoch == other.PairEpoch && PairId == other.PairId &&
        PairRecordRevision == other.PairRecordRevision &&
        ScanGeneration == other.ScanGeneration &&
        LeftDeviceGeneration == other.LeftDeviceGeneration &&
        LeftTransportGeneration == other.LeftTransportGeneration &&
        RightDeviceGeneration == other.RightDeviceGeneration &&
        RightTransportGeneration == other.RightTransportGeneration;

    public override bool Equals(object obj) => obj is
        Switch2JoyConJoinedRuntimePrepareCredential other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : RuntimeHelpers.GetHashCode(issuer),
        fence == null ? 0 : RuntimeHelpers.GetHashCode(fence), token,
        RuntimeGeneration, PairEpoch, PairId, PairRecordRevision,
        HashCode.Combine(ScanGeneration, LeftDeviceGeneration,
            LeftTransportGeneration, RightDeviceGeneration,
            RightTransportGeneration));
}

internal sealed class Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs :
    EventArgs
{
    internal Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs(
        Switch2StickSide side, ulong runtimeGeneration, ulong pairEpoch,
        ulong deviceGeneration, ulong transportGeneration,
        Switch2BluetoothInputEndReason endReason,
        Switch2BluetoothInputDrainPumpFailure pumpFailure,
        bool userDisconnectRequested = false)
    {
        Side = side;
        RuntimeGeneration = runtimeGeneration;
        PairEpoch = pairEpoch;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        EndReason = endReason;
        PumpFailure = pumpFailure;
        UserDisconnectRequested = userDisconnectRequested;
    }

    internal Switch2StickSide Side { get; }
    internal ulong RuntimeGeneration { get; }
    internal ulong PairEpoch { get; }
    internal ulong DeviceGeneration { get; }
    internal ulong TransportGeneration { get; }
    internal Switch2BluetoothInputEndReason EndReason { get; }
    internal Switch2BluetoothInputDrainPumpFailure PumpFailure { get; }
    internal bool UserDisconnectRequested { get; }
}

/// <summary>
/// Composition owner for one exact Joy-Con L/R association. The association
/// can be an explicitly persisted pair or one transient automatic-pair epoch;
/// downstream ownership and teardown authenticate both forms identically.
/// It owns one logical registration and one joined mapping sink while retaining
/// two independently proven physical BLE releases. It performs no discovery,
/// pair-store mutation, table coordination, output, or production wiring.
/// </summary>
internal sealed class Switch2JoyConJoinedRuntimeOwner :
    IInputControllerRegistrationOwner
{
    internal const int MaximumTimeoutMilliseconds =
        InputControllerRegistration.MaximumStopTimeoutMilliseconds;

    private readonly object gate = new();
    private readonly object attentionGate = new();
    private readonly Switch2JoyConPairId pairId;
    private readonly ulong pairRecordRevision;
    private readonly ulong scanGeneration;
    private readonly ulong runtimeGeneration;
    private readonly ulong pairEpoch;
    private readonly ulong leftDeviceGeneration;
    private readonly ulong leftTransportGeneration;
    private readonly ulong rightDeviceGeneration;
    private readonly ulong rightTransportGeneration;
    private readonly ISwitch2BluetoothInputLease leftLease;
    private readonly ISwitch2BluetoothInputLease rightLease;
    private readonly ISwitch2BluetoothInputLeaseReleaseProof leftReleaseProof;
    private readonly ISwitch2BluetoothInputLeaseReleaseProof rightReleaseProof;
    private readonly Switch2RuntimeInputDevice runtimeDevice;
    private readonly InputControllerRegistration registration;
    private readonly Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs
        userDisconnectAttention;

    private Switch2JoyConJoinedRuntimeInputSink sink;
    private Switch2JoyConJoinedRuntimeTerminalCredential terminalCredential;
    private Switch2BluetoothInputOwner leftInputOwner;
    private Switch2BluetoothInputPrepareCredential leftInputCredential;
    private Switch2BluetoothInputOwner rightInputOwner;
    private Switch2BluetoothInputPrepareCredential rightInputCredential;
    private ISwitch2BluetoothRuntimeDrainPump leftPump;
    private ISwitch2BluetoothRuntimeDrainPump rightPump;
    private Switch2BluetoothFeedbackLifetime feedbackLifetime;

    private Switch2BluetoothRuntimeOwnerState state =
        Switch2BluetoothRuntimeOwnerState.Created;
    private Switch2JoyConJoinedRuntimeStopFailure lastStopFailure;
    private Switch2BluetoothRuntimeAbortFailure lastAbortFailure;
    private bool dependenciesComplete;
    private bool creationFailed;
    private bool requiresQuarantine;
    private bool lifecycleOperationInProgress;
    private bool leftReleaseProven;
    private bool rightReleaseProven;
    private object slotAdoptionFence;
    private InputControllerSlotToken boundSlotToken;
    private object preparedFence;
    private bool preparedCredentialConsumed;
    private bool retirementArmed;
    private InputControllerRetirementClaim retirementClaim;
    private int attentionRaised;
    private EventHandler<Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
        attentionHandlers;

    private Switch2JoyConJoinedRuntimeOwner(
        in Switch2JoyConPairConnectionAdmission pairAdmission,
        ISwitch2BluetoothInputLease leftLease,
        ISwitch2BluetoothInputLease rightLease,
        ISwitch2BluetoothInputLeaseReleaseProof leftReleaseProof,
        ISwitch2BluetoothInputLeaseReleaseProof rightReleaseProof,
        ulong runtimeGeneration, ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration,
        Switch2RuntimeInputDevice runtimeDevice)
    {
        pairId = pairAdmission.PairId;
        pairRecordRevision = pairAdmission.PairRecordRevision;
        scanGeneration = pairAdmission.ScanGeneration;
        this.leftLease = leftLease;
        this.rightLease = rightLease;
        this.leftReleaseProof = leftReleaseProof;
        this.rightReleaseProof = rightReleaseProof;
        this.runtimeGeneration = runtimeGeneration;
        this.pairEpoch = pairEpoch;
        this.leftDeviceGeneration = leftDeviceGeneration;
        this.leftTransportGeneration = leftTransportGeneration;
        this.rightDeviceGeneration = rightDeviceGeneration;
        this.rightTransportGeneration = rightTransportGeneration;
        this.runtimeDevice = runtimeDevice;
        userDisconnectAttention = new(Switch2StickSide.Invalid,
            runtimeGeneration, pairEpoch, 0, 0,
            Switch2BluetoothInputEndReason.Stopped, default,
            userDisconnectRequested: true);
        if (!InputControllerRegistration.TryCreate(runtimeDevice,
                runtimeGeneration, InputControllerOwnershipKind.Switch2Runtime,
                hasHidInterface: false, hasPersistentIdentity: false, this,
                out registration,
                out InputControllerRegistrationFailure failure))
        {
            throw new InvalidOperationException(
                $"Joined Joy-Con registration rejected: {failure}.");
        }
        if (!runtimeDevice.TryBindBluetoothDisconnectRequest(
                TryRequestUserDisconnect))
        {
            throw new InvalidOperationException(
                "Joined Joy-Con disconnect lifecycle binding was rejected.");
        }
    }

    public InputControllerOwnershipKind Kind =>
        InputControllerOwnershipKind.Switch2Runtime;
    internal Switch2JoyConPairId PairId => pairId;
    internal ulong PairRecordRevision => pairRecordRevision;
    internal ulong ScanGeneration => scanGeneration;
    internal ulong RuntimeGeneration => runtimeGeneration;
    internal ulong PairEpoch => pairEpoch;
    internal Switch2RuntimeInputDevice RuntimeDevice => runtimeDevice;
    internal InputControllerRegistration Registration => registration;
    internal Switch2JoyConJoinedRuntimeInputSink Sink => sink;
    internal Switch2BluetoothInputOwner LeftInputOwner => leftInputOwner;
    internal Switch2BluetoothInputOwner RightInputOwner => rightInputOwner;
    internal ISwitch2BluetoothRuntimeDrainPump LeftDrainPump => leftPump;
    internal ISwitch2BluetoothRuntimeDrainPump RightDrainPump => rightPump;
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
    internal bool LeftReleaseProven
    {
        get { lock (gate) { return leftReleaseProven; } }
    }
    internal bool RightReleaseProven
    {
        get { lock (gate) { return rightReleaseProven; } }
    }
    internal bool DependenciesComplete
    {
        get { lock (gate) { return dependenciesComplete; } }
    }
    internal Switch2JoyConJoinedRuntimeStopFailure LastStopFailure
    {
        get { lock (gate) { return lastStopFailure; } }
    }
    internal Switch2BluetoothRuntimeAbortFailure LastAbortFailure
    {
        get { lock (gate) { return lastAbortFailure; } }
    }

    internal event EventHandler<
        Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
        LifecycleAttention
    {
        add { lock (attentionGate) { attentionHandlers += value; } }
        remove { lock (attentionGate) { attentionHandlers -= value; } }
    }

    internal static bool TryCreate(
        in Switch2JoyConPairConnectionAdmission pairAdmission,
        ISwitch2BluetoothInputLease leftLease,
        ISwitch2BluetoothInputLease rightLease,
        ulong runtimeGeneration, ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        in Switch2InputCalibrationSnapshot leftCalibration,
        int leftQueueCapacity, ulong rightDeviceGeneration,
        ulong rightTransportGeneration,
        in Switch2InputCalibrationSnapshot rightCalibration,
        int rightQueueCapacity, long qpcFrequency,
        in Switch2JoyConPairPolicy pairPolicy,
        int lifecycleTimeoutMilliseconds,
        out Switch2JoyConJoinedRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2JoyConJoinedRuntimeCreateFailure failure) => TryCreateCore(
            pairAdmission, leftLease, rightLease, runtimeGeneration, pairEpoch,
            leftDeviceGeneration, leftTransportGeneration, leftCalibration,
            leftQueueCapacity, rightDeviceGeneration,
            rightTransportGeneration, rightCalibration, rightQueueCapacity,
            qpcFrequency, pairPolicy, lifecycleTimeoutMilliseconds,
            Switch2BluetoothRuntimeDrainPumpFactory.Instance,
            Switch2RuntimeTerminalScheduler.Instance, out owner,
            out registration, out failure);

    internal static bool TryCreateCore(
        in Switch2JoyConPairConnectionAdmission pairAdmission,
        ISwitch2BluetoothInputLease leftLease,
        ISwitch2BluetoothInputLease rightLease,
        ulong runtimeGeneration, ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        in Switch2InputCalibrationSnapshot leftCalibration,
        int leftQueueCapacity, ulong rightDeviceGeneration,
        ulong rightTransportGeneration,
        in Switch2InputCalibrationSnapshot rightCalibration,
        int rightQueueCapacity, long qpcFrequency,
        in Switch2JoyConPairPolicy pairPolicy,
        int lifecycleTimeoutMilliseconds,
        ISwitch2BluetoothRuntimeDrainPumpFactory pumpFactory,
        ISwitch2RuntimeTerminalScheduler terminalScheduler,
        out Switch2JoyConJoinedRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2JoyConJoinedRuntimeCreateFailure failure)
    {
        owner = null;
        registration = default;
        if (leftLease == null || rightLease == null || pumpFactory == null ||
            terminalScheduler == null)
        {
            failure = CreateFailure(
                Switch2JoyConJoinedRuntimeCreateFailureKind.MissingDependency);
            return false;
        }
        if (leftLease is not ISwitch2BluetoothInputLeaseReleaseProof leftProof ||
            rightLease is not ISwitch2BluetoothInputLeaseReleaseProof rightProof)
        {
            failure = CreateFailure(
                Switch2JoyConJoinedRuntimeCreateFailureKind.MissingReleaseProof);
            return false;
        }
        if (!pairAdmission.IsValid || ReferenceEquals(leftLease, rightLease) ||
            runtimeGeneration == 0 || pairEpoch == 0 ||
            leftDeviceGeneration == 0 || leftTransportGeneration == 0 ||
            rightDeviceGeneration == 0 || rightTransportGeneration == 0 ||
            qpcFrequency <= 0 || leftQueueCapacity is < 1 or >
                Switch2BluetoothInputOwner.MaximumQueueCapacity ||
            rightQueueCapacity is < 1 or >
                Switch2BluetoothInputOwner.MaximumQueueCapacity ||
            lifecycleTimeoutMilliseconds <= 0 ||
            lifecycleTimeoutMilliseconds > MaximumTimeoutMilliseconds ||
            !leftCalibration.IsValid ||
            leftCalibration.Model != Switch2ControllerModel.JoyCon2Left ||
            leftCalibration.DeviceGeneration != leftDeviceGeneration ||
            !rightCalibration.IsValid ||
            rightCalibration.Model != Switch2ControllerModel.JoyCon2Right ||
            rightCalibration.DeviceGeneration != rightDeviceGeneration)
        {
            failure = CreateFailure(
                Switch2JoyConJoinedRuntimeCreateFailureKind.InvalidArgument);
            return false;
        }

        if (!Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(
                runtimeGeneration, pairEpoch, leftDeviceGeneration,
                leftTransportGeneration, rightDeviceGeneration,
                rightTransportGeneration,
                out Switch2RuntimeInputDevice runtime,
                out Switch2RuntimeInputDeviceCreateFailure runtimeFailure))
        {
            failure = new Switch2JoyConJoinedRuntimeCreateFailure(
                Switch2JoyConJoinedRuntimeCreateFailureKind.
                    RuntimeDeviceRejected,
                runtimeFailure, default, default, default, default, default,
                default, default, null);
            return false;
        }

        Switch2JoyConJoinedRuntimeOwner candidate;
        try
        {
            candidate = new Switch2JoyConJoinedRuntimeOwner(pairAdmission,
                leftLease, rightLease, leftProof, rightProof,
                runtimeGeneration, pairEpoch, leftDeviceGeneration,
                leftTransportGeneration, rightDeviceGeneration,
                rightTransportGeneration, runtime);
        }
        catch
        {
            runtime.TryAbortUnpublishedActivation();
            failure = new Switch2JoyConJoinedRuntimeCreateFailure(
                Switch2JoyConJoinedRuntimeCreateFailureKind.
                    RegistrationRejected,
                default, InputControllerRegistrationFailure.
                    OwnerAuthenticationFailed,
                default, default, default, default, default, default, null);
            return false;
        }

        if (!Switch2JoyConJoinedRuntimeInputSink.TryCreateUnbound(pairEpoch,
                leftDeviceGeneration, leftTransportGeneration,
                rightDeviceGeneration, rightTransportGeneration, runtime,
                pairPolicy, lifecycleTimeoutMilliseconds, terminalScheduler,
                out Switch2JoyConJoinedRuntimeInputSink sink,
                out Switch2JoyConJoinedRuntimeSinkBindingCredential bind,
                out Switch2JoyConJoinedRuntimeTerminalCredential terminal,
                out Switch2JoyConJoinedRuntimeSinkFailure sinkFailure))
        {
            runtime.TryAbortUnpublishedActivation();
            failure = new Switch2JoyConJoinedRuntimeCreateFailure(
                Switch2JoyConJoinedRuntimeCreateFailureKind.SinkRejected,
                default, default, sinkFailure, default, default, default,
                default, default, null);
            return false;
        }
        candidate.sink = sink;
        candidate.terminalCredential = terminal;

        Switch2BluetoothInputPairPrepareResult pairResult;
        bool pairPrepared;
        try
        {
            pairPrepared = Switch2BluetoothInputOwner.TryPreparePair(
                pairAdmission, leftLease, rightLease, sink,
                leftDeviceGeneration, leftTransportGeneration,
                leftCalibration, leftQueueCapacity, rightDeviceGeneration,
                rightTransportGeneration, rightCalibration,
                rightQueueCapacity, qpcFrequency, out pairResult);
        }
        catch
        {
            return RetainUnknownPreparation(candidate,
                lifecycleTimeoutMilliseconds, out owner, out registration,
                out failure);
        }
        if (!pairPrepared || !pairResult.IsPrepared)
        {
            if (!pairResult.AdmissionsConsumedByThisCall)
            {
                runtime.TryAbortUnpublishedActivation();
                failure = new Switch2JoyConJoinedRuntimeCreateFailure(
                    Switch2JoyConJoinedRuntimeCreateFailureKind.
                        PairInputRejected,
                    default, default, default, pairResult, default, default,
                    default, default, null);
                return false;
            }
            return ResolveConsumedPrepareFailure(candidate, pairResult,
                lifecycleTimeoutMilliseconds, out owner, out registration,
                out failure);
        }
        candidate.leftInputOwner = pairResult.LeftOwner;
        candidate.leftInputCredential = pairResult.LeftCredential;
        candidate.rightInputOwner = pairResult.RightOwner;
        candidate.rightInputCredential = pairResult.RightCredential;

        if (!sink.TryBindDescriptors(bind, pairResult.LeftOwner.Descriptor,
                pairResult.RightOwner.Descriptor, out sinkFailure))
        {
            return FailPostPairPrepare(candidate, lifecycleTimeoutMilliseconds,
                Switch2JoyConJoinedRuntimeCreateFailureKind.
                    DescriptorBindRejected,
                sinkFailure, default, default, out owner, out registration,
                out failure);
        }

        bool leftCreated;
        Switch2BluetoothInputDrainPumpFailure leftPumpFailure;
        try
        {
            leftCreated = pumpFactory.TryCreate(pairResult.LeftOwner,
                out candidate.leftPump, out leftPumpFailure);
        }
        catch
        {
            return RetainUnknownPump(candidate, lifecycleTimeoutMilliseconds,
                isLeft: true,
                Switch2BluetoothInputDrainPumpFailure.UnexpectedWorkerFailure,
                out owner, out registration, out failure);
        }
        if (!leftCreated || candidate.leftPump == null)
        {
            return FailPostPairPrepare(candidate, lifecycleTimeoutMilliseconds,
                Switch2JoyConJoinedRuntimeCreateFailureKind.LeftPumpRejected,
                default, leftPumpFailure, default, out owner,
                out registration, out failure);
        }

        bool rightCreated;
        Switch2BluetoothInputDrainPumpFailure rightPumpFailure;
        try
        {
            rightCreated = pumpFactory.TryCreate(pairResult.RightOwner,
                out candidate.rightPump, out rightPumpFailure);
        }
        catch
        {
            return RetainUnknownPump(candidate, lifecycleTimeoutMilliseconds,
                isLeft: false,
                Switch2BluetoothInputDrainPumpFailure.UnexpectedWorkerFailure,
                out owner, out registration, out failure);
        }
        if (!rightCreated || candidate.rightPump == null)
        {
            return FailPostPairPrepare(candidate, lifecycleTimeoutMilliseconds,
                Switch2JoyConJoinedRuntimeCreateFailureKind.RightPumpRejected,
                default, default, rightPumpFailure, out owner,
                out registration, out failure);
        }

        bool leftAttention;
        bool rightAttention;
        try
        {
            leftAttention = candidate.leftPump.
                TrySetLifecycleAttentionHandler(candidate.OnLeftPumpAttention);
        }
        catch
        {
            leftAttention = false;
        }
        try
        {
            rightAttention = candidate.rightPump.
                TrySetLifecycleAttentionHandler(candidate.OnRightPumpAttention);
        }
        catch
        {
            rightAttention = false;
        }
        if (!leftAttention || !rightAttention)
        {
            return FailPostPairPrepare(candidate, lifecycleTimeoutMilliseconds,
                Switch2JoyConJoinedRuntimeCreateFailureKind.AttentionRejected,
                default,
                leftAttention ? default :
                    Switch2BluetoothInputDrainPumpFailure.OwnerRejected,
                rightAttention ? default :
                    Switch2BluetoothInputDrainPumpFailure.OwnerRejected,
                out owner, out registration, out failure);
        }

        ISwitch2BluetoothHdRumbleBindableTransportLease leftOutput =
            leftLease as ISwitch2BluetoothHdRumbleBindableTransportLease;
        ISwitch2BluetoothHdRumbleBindableTransportLease rightOutput =
            rightLease as ISwitch2BluetoothHdRumbleBindableTransportLease;
        bool leftHasOutput = leftOutput?.HasHdRumbleOutput == true;
        bool rightHasOutput = rightOutput?.HasHdRumbleOutput == true;
        Switch2BluetoothFeedbackLifetime feedback = null;
        if (leftHasOutput != rightHasOutput || leftHasOutput &&
            (!Switch2BluetoothFeedbackLifetime.TryCreateJoined(leftOutput,
                    rightOutput, runtimeGeneration, pairEpoch,
                    leftDeviceGeneration, leftTransportGeneration,
                    rightDeviceGeneration, rightTransportGeneration,
                    out feedback) ||
                !runtime.TryAttachJoinedBluetoothFeedbackLifetime(
                    runtimeGeneration, pairEpoch, feedback)))
        {
            return FailPostPairPrepare(candidate, lifecycleTimeoutMilliseconds,
                Switch2JoyConJoinedRuntimeCreateFailureKind.DependencyThrew,
                default,
                Switch2BluetoothInputDrainPumpFailure.OwnerRejected,
                Switch2BluetoothInputDrainPumpFailure.OwnerRejected,
                out owner, out registration, out failure);
        }
        if (leftHasOutput)
        {
            candidate.feedbackLifetime = feedback;
        }

        lock (candidate.gate)
        {
            candidate.dependenciesComplete = candidate.sink != null &&
                candidate.leftInputOwner != null &&
                candidate.rightInputOwner != null &&
                candidate.leftPump != null && candidate.rightPump != null;
        }
        owner = candidate;
        registration = candidate.registration;
        failure = default;
        return true;
    }

    internal bool TryAdoptBoundSlot(
        in InputControllerSlotToken exactBoundSlotToken,
        out Switch2JoyConJoinedRuntimeSlotAdoptionCredential credential,
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
            credential = CreateAdoptionCredential();
        }
        failure = Switch2BluetoothRuntimeSlotAdoptionFailure.None;
        return true;
    }

    internal bool TryPrepareActivation(
        in Switch2JoyConJoinedRuntimeSlotAdoptionCredential adoptionCredential,
        int timeoutMilliseconds,
        out Switch2JoyConJoinedRuntimePrepareCredential credential,
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
            if (!AuthenticatesAdoption(adoptionCredential))
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
        bool runtimeStarted = false;
        bool leftStarted = false;
        bool rightStarted = false;
        try
        {
            runtimeDevice.StartUpdate();
            runtimeStarted = runtimeDevice.RuntimeState ==
                Switch2RuntimeInputDeviceState.Active;
        }
        catch
        {
        }
        Switch2BluetoothInputDrainPumpFailure leftPumpFailure = default;
        Switch2BluetoothInputDrainPumpFailure rightPumpFailure = default;
        if (runtimeStarted)
        {
            try
            {
                int remaining = RemainingMilliseconds(deadline);
                leftStarted = remaining != 0 && leftPump.TryStartParked(
                    remaining, out leftPumpFailure);
            }
            catch
            {
                leftPumpFailure = Switch2BluetoothInputDrainPumpFailure.
                    UnexpectedWorkerFailure;
            }
        }
        if (leftStarted)
        {
            try
            {
                int remaining = RemainingMilliseconds(deadline);
                rightStarted = remaining != 0 && rightPump.TryStartParked(
                    remaining, out rightPumpFailure);
            }
            catch
            {
                rightPumpFailure = Switch2BluetoothInputDrainPumpFailure.
                    UnexpectedWorkerFailure;
            }
        }
        if (!runtimeStarted || !leftStarted || !rightStarted)
        {
            bool cleaned = CleanupUnpublished(deadline, out var abortFailure);
            lock (gate)
            {
                lifecycleOperationInProgress = false;
                lastAbortFailure = abortFailure;
                if (cleaned)
                {
                    state = Switch2BluetoothRuntimeOwnerState.
                        AbortedUnpublished;
                }
                else
                {
                    requiresQuarantine = true;
                    state = Switch2BluetoothRuntimeOwnerState.Quarantined;
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

        object exactFence = new();
        lock (gate)
        {
            preparedFence = exactFence;
            preparedCredentialConsumed = false;
            lifecycleOperationInProgress = false;
            state = Switch2BluetoothRuntimeOwnerState.Prepared;
            Monitor.PulseAll(gate);
        }
        credential = CreatePrepareCredential(exactFence);
        failure = Switch2BluetoothRuntimePrepareFailure.None;
        return true;
    }

    internal bool TryCommitPrepared(
        in Switch2JoyConJoinedRuntimePrepareCredential credential,
        in InputControllerActivationCommitCredential activationCommit,
        out Switch2BluetoothRuntimeCommitFailure failure)
    {
        if (!credential.IsValid || !AuthenticatesPrepare(credential))
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
            if (!ReferenceEquals(preparedFence, credential.Fence))
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
            committed = Switch2BluetoothInputOwner.TryCommitPreparedPair(
                leftInputOwner, leftInputCredential, rightInputOwner,
                rightInputCredential, out _);
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
        in Switch2JoyConJoinedRuntimePrepareCredential credential,
        int timeoutMilliseconds,
        out Switch2BluetoothRuntimeAbortFailure failure)
    {
        if (!credential.IsValid || !AuthenticatesPrepare(credential))
        {
            failure = Switch2BluetoothRuntimeAbortFailure.InvalidCredential;
            return false;
        }
        return TryAbortCore(requireCredential: true, credential.Fence,
            timeoutMilliseconds, out failure);
    }

    internal bool TryAbortUnpublished(
        in Switch2JoyConJoinedRuntimeSlotAdoptionCredential adoptionCredential,
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
            if (!AuthenticatesAdoption(adoptionCredential))
            {
                failure = Switch2BluetoothRuntimeAbortFailure.
                    InvalidCredential;
                return false;
            }
            if (state == Switch2BluetoothRuntimeOwnerState.AbortedUnpublished)
            {
                bool proof = dependenciesComplete && leftReleaseProven &&
                    rightReleaseProven && leftPump.State ==
                        Switch2BluetoothInputDrainPumpState.Stopped &&
                    rightPump.State ==
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
        return TryAbortCore(requireCredential: false, null,
            timeoutMilliseconds, out failure);
    }

    /// <summary>
    /// Releases a successfully-created joined owner when the registration
    /// table rejects it before a slot-adoption credential can exist. The
    /// exact authenticated registration is required so neither half of a
    /// different physical pair can be retired accidentally.
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
            exactRegistration.Generation != runtimeGeneration)
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
                generation == runtimeGeneration;
        }
    }

    public bool TryStopAndQuiesce(DS4Device device, ulong generation,
        int timeoutMilliseconds,
        out InputControllerOwnerOperationFailure failure)
    {
        if (!ReferenceEquals(device, runtimeDevice) ||
            generation != runtimeGeneration)
        {
            SetStopFailure(Switch2BluetoothRuntimeStopFailureKind.InvalidOwner);
            failure = InputControllerOwnerOperationFailure.
                OwnerAuthenticationFailed;
            return false;
        }
        if (timeoutMilliseconds < 0 ||
            timeoutMilliseconds > MaximumTimeoutMilliseconds)
        {
            SetStopFailure(Switch2BluetoothRuntimeStopFailureKind.InvalidTimeout);
            failure = InputControllerOwnerOperationFailure.StopRejected;
            return false;
        }
        // Check before waiting on another lifecycle operation. A synchronous
        // pump-attention or terminal Report subscriber can re-enter while the
        // control-thread stop owns that operation; waiting here would make the
        // callback and its caller wait on each other. An independent caller
        // must still join that operation even while its terminal callback runs.
        if (IsCurrentCallbackThread())
        {
            SetStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.CallbackActive);
            failure = InputControllerOwnerOperationFailure.StopRejected;
            return false;
        }
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        bool wasQuarantined;
        lock (gate)
        {
            if (creationFailed || !dependenciesComplete)
            {
                SetStopFailureNoLock(
                    Switch2BluetoothRuntimeStopFailureKind.QuarantineRequired);
                failure = InputControllerOwnerOperationFailure.StopRejected;
                return false;
            }
            if (!retirementArmed)
            {
                SetStopFailureNoLock(
                    Switch2BluetoothRuntimeStopFailureKind.RetirementNotArmed);
                failure = InputControllerOwnerOperationFailure.StopRejected;
                return false;
            }
            while (lifecycleOperationInProgress)
            {
                int remaining = RemainingMilliseconds(deadline);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    SetStopFailureNoLock(
                        Switch2BluetoothRuntimeStopFailureKind.
                            OperationAlreadyInProgress);
                    failure = InputControllerOwnerOperationFailure.StopRejected;
                    return false;
                }
            }
            if (state == Switch2BluetoothRuntimeOwnerState.Stopped)
            {
                failure = InputControllerOwnerOperationFailure.None;
                return true;
            }
            if (state is Switch2BluetoothRuntimeOwnerState.Created or
                Switch2BluetoothRuntimeOwnerState.Preparing or
                Switch2BluetoothRuntimeOwnerState.Prepared or
                Switch2BluetoothRuntimeOwnerState.Removed or
                Switch2BluetoothRuntimeOwnerState.AbortedUnpublished)
            {
                SetStopFailureNoLock(
                    Switch2BluetoothRuntimeStopFailureKind.InvalidState);
                failure = InputControllerOwnerOperationFailure.StopRejected;
                return false;
            }
            wasQuarantined = requiresQuarantine;
            lifecycleOperationInProgress = true;
            state = Switch2BluetoothRuntimeOwnerState.StopRequested;
        }

        if (HasActiveCallbackFence())
        {
            lock (gate)
            {
                lifecycleOperationInProgress = false;
                state = Switch2BluetoothRuntimeOwnerState.StopRequested;
                SetStopFailureNoLock(
                    Switch2BluetoothRuntimeStopFailureKind.CallbackActive);
                Monitor.PulseAll(gate);
            }
            failure = InputControllerOwnerOperationFailure.StopRejected;
            return false;
        }

        bool stopped = TryStopCore(deadline,
            out Switch2JoyConJoinedRuntimeStopFailure stopFailure);
        lock (gate)
        {
            lifecycleOperationInProgress = false;
            if (!stopped || wasQuarantined)
            {
                requiresQuarantine = true;
                state = Switch2BluetoothRuntimeOwnerState.Quarantined;
                lastStopFailure = stopFailure.Kind ==
                        Switch2BluetoothRuntimeStopFailureKind.None ?
                    new Switch2JoyConJoinedRuntimeStopFailure(
                        Switch2BluetoothRuntimeStopFailureKind.
                            QuarantineRequired,
                        default, default, default, default, default) :
                    stopFailure;
            }
            else
            {
                state = Switch2BluetoothRuntimeOwnerState.Stopped;
                lastStopFailure = default;
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
                generation != runtimeGeneration)
            {
                failure = InputControllerOwnerOperationFailure.
                    OwnerAuthenticationFailed;
                return false;
            }
            if (lifecycleOperationInProgress || requiresQuarantine ||
                !retirementArmed ||
                state != Switch2BluetoothRuntimeOwnerState.Stopped ||
                !leftReleaseProven || !rightReleaseProven ||
                leftPump.State != Switch2BluetoothInputDrainPumpState.Stopped ||
                rightPump.State != Switch2BluetoothInputDrainPumpState.Stopped ||
                (feedbackLifetime != null && !feedbackLifetime.IsRetired) ||
                sink.TerminalState !=
                    Switch2BluetoothRuntimeTerminalState.Delivered ||
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

        bool cleaned = CleanupUnpublished(
            Environment.TickCount64 + timeoutMilliseconds, out failure);
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
        bool pairAborted = false;
        bool dependencyThrew = false;
        try
        {
            if (leftInputOwner.IsPrepared && rightInputOwner.IsPrepared)
            {
                pairAborted = Switch2BluetoothInputOwner.TryAbortPreparedPair(
                    leftInputOwner, leftInputCredential, rightInputOwner,
                    rightInputCredential, out _);
            }
            else
            {
                pairAborted = !leftInputOwner.ActivationCommitted &&
                    !rightInputOwner.ActivationCommitted &&
                    !leftInputOwner.IsPrepared && !rightInputOwner.IsPrepared;
            }
        }
        catch
        {
            dependencyThrew = true;
        }

        bool leftPumpStopped = TryStopPump(leftPump, deadline,
            out Switch2BluetoothInputDrainPumpFailure leftPumpFailure);
        bool rightPumpStopped = TryStopPump(rightPump, deadline,
            out Switch2BluetoothInputDrainPumpFailure rightPumpFailure);
        bool feedbackAborted = feedbackLifetime == null ||
            feedbackLifetime.TryAbortUnpublished();
        Switch2BluetoothInputLeaseReleaseResult leftRelease =
            WaitForRelease(leftReleaseProof, leftTransportGeneration,
                deadline);
        Switch2BluetoothInputLeaseReleaseResult rightRelease =
            WaitForRelease(rightReleaseProof, rightTransportGeneration,
                deadline);
        lock (gate)
        {
            leftReleaseProven = leftRelease ==
                Switch2BluetoothInputLeaseReleaseResult.Released;
            rightReleaseProven = rightRelease ==
                Switch2BluetoothInputLeaseReleaseResult.Released;
        }
        bool runtimeAborted;
        try
        {
            runtimeAborted = runtimeDevice.TryAbortUnpublishedActivation();
        }
        catch
        {
            runtimeAborted = false;
            dependencyThrew = true;
        }
        bool cleaned = pairAborted && leftPumpStopped && rightPumpStopped &&
            feedbackAborted &&
            leftRelease == Switch2BluetoothInputLeaseReleaseResult.Released &&
            rightRelease == Switch2BluetoothInputLeaseReleaseResult.Released &&
            runtimeAborted && !dependencyThrew;
        failure = cleaned ? Switch2BluetoothRuntimeAbortFailure.None :
            dependencyThrew ? Switch2BluetoothRuntimeAbortFailure.
                DependencyThrew :
            !pairAborted ? Switch2BluetoothRuntimeAbortFailure.
                InputAbortRejected :
            !leftPumpStopped || !rightPumpStopped ?
                (leftPumpFailure ==
                    Switch2BluetoothInputDrainPumpFailure.WorkerExitTimedOut ||
                 rightPumpFailure ==
                    Switch2BluetoothInputDrainPumpFailure.WorkerExitTimedOut ?
                    Switch2BluetoothRuntimeAbortFailure.PumpTimedOut :
                    Switch2BluetoothRuntimeAbortFailure.PumpRejected) :
            !feedbackAborted ? Switch2BluetoothRuntimeAbortFailure.
                RuntimeAbortRejected :
            leftRelease == Switch2BluetoothInputLeaseReleaseResult.TimedOut ||
                rightRelease ==
                    Switch2BluetoothInputLeaseReleaseResult.TimedOut ?
                Switch2BluetoothRuntimeAbortFailure.LeaseReleaseTimedOut :
            leftRelease != Switch2BluetoothInputLeaseReleaseResult.Released ||
                rightRelease !=
                    Switch2BluetoothInputLeaseReleaseResult.Released ?
                Switch2BluetoothRuntimeAbortFailure.LeaseReleaseRejected :
            Switch2BluetoothRuntimeAbortFailure.RuntimeAbortRejected;
        return cleaned;
    }

    private bool TryStopCore(long deadline,
        out Switch2JoyConJoinedRuntimeStopFailure failure)
    {
        int remaining = RemainingMilliseconds(deadline);
        if (feedbackLifetime != null &&
            (remaining <= 0 || !feedbackLifetime.TryStopAndRetire(
                maxAttempts: Math.Min(3, Math.Max(1, remaining / 100)))))
        {
            failure = new Switch2JoyConJoinedRuntimeStopFailure(
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalDeliveryRejected,
                default, default, default, default,
                Switch2JoyConJoinedRuntimeSinkFailure.
                    TerminalDeliveryRejected);
            return false;
        }

        bool dependencyThrew = false;
        try { leftInputOwner.Stop(); } catch { dependencyThrew = true; }
        try { rightInputOwner.Stop(); } catch { dependencyThrew = true; }

        bool leftPumpStopped = TryStopPump(leftPump, deadline,
            out Switch2BluetoothInputDrainPumpFailure leftPumpFailure);
        bool rightPumpStopped = TryStopPump(rightPump, deadline,
            out Switch2BluetoothInputDrainPumpFailure rightPumpFailure);
        Switch2BluetoothInputLeaseReleaseResult leftRelease =
            WaitForRelease(leftReleaseProof, leftTransportGeneration,
                deadline);
        Switch2BluetoothInputLeaseReleaseResult rightRelease =
            WaitForRelease(rightReleaseProof, rightTransportGeneration,
                deadline);
        lock (gate)
        {
            leftReleaseProven = leftRelease ==
                Switch2BluetoothInputLeaseReleaseResult.Released;
            rightReleaseProven = rightRelease ==
                Switch2BluetoothInputLeaseReleaseResult.Released;
        }

        bool terminal = false;
        Switch2JoyConJoinedRuntimeSinkFailure sinkFailure = default;
        if (sink.TerminalRequested)
        {
            try
            {
                terminal = sink.TryCompleteTerminalNeutral(terminalCredential,
                    RemainingMilliseconds(deadline), out sinkFailure);
            }
            catch
            {
                dependencyThrew = true;
                sinkFailure = Switch2JoyConJoinedRuntimeSinkFailure.
                    DependencyThrew;
            }
        }
        else
        {
            sinkFailure = Switch2JoyConJoinedRuntimeSinkFailure.
                TerminalNotRequested;
        }

        bool stopped = !dependencyThrew && leftPumpStopped &&
            rightPumpStopped && leftRelease ==
                Switch2BluetoothInputLeaseReleaseResult.Released &&
            rightRelease == Switch2BluetoothInputLeaseReleaseResult.Released &&
            terminal;
        Switch2BluetoothInputDrainPumpFailure exactPumpFailure =
            !leftPumpStopped ? leftPumpFailure : rightPumpFailure;
        Switch2BluetoothInputLeaseReleaseResult exactRelease =
            leftRelease != Switch2BluetoothInputLeaseReleaseResult.Released ?
                leftRelease : rightRelease;
        Switch2BluetoothRuntimeStopFailureKind kind = stopped ?
            Switch2BluetoothRuntimeStopFailureKind.None : dependencyThrew ?
            Switch2BluetoothRuntimeStopFailureKind.DependencyThrew :
            !leftPumpStopped || !rightPumpStopped ?
                (exactPumpFailure ==
                    Switch2BluetoothInputDrainPumpFailure.WorkerExitTimedOut ?
                    Switch2BluetoothRuntimeStopFailureKind.PumpTimedOut :
                    Switch2BluetoothRuntimeStopFailureKind.PumpRejected) :
            exactRelease == Switch2BluetoothInputLeaseReleaseResult.TimedOut ?
                Switch2BluetoothRuntimeStopFailureKind.LeaseReleaseTimedOut :
            exactRelease != Switch2BluetoothInputLeaseReleaseResult.Released ?
                Switch2BluetoothRuntimeStopFailureKind.LeaseReleaseRejected :
            !sink.TerminalRequested ?
                Switch2BluetoothRuntimeStopFailureKind.TerminalNotRequested :
            sinkFailure == Switch2JoyConJoinedRuntimeSinkFailure.
                    TerminalDeliveryTimedOut ?
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalPublicationTimedOut :
            sinkFailure == Switch2JoyConJoinedRuntimeSinkFailure.
                    TerminalDeliveryRejected ?
                Switch2BluetoothRuntimeStopFailureKind.
                    TerminalDeliveryRejected :
            Switch2BluetoothRuntimeStopFailureKind.
                TerminalPublicationRejected;
        failure = new Switch2JoyConJoinedRuntimeStopFailure(kind,
            leftPumpFailure, rightPumpFailure, leftRelease, rightRelease,
            sinkFailure);
        return stopped;
    }

    private void OnLeftPumpAttention(
        Switch2BluetoothInputDrainPumpAttention evidence) =>
        OnPumpAttention(Switch2StickSide.Left, evidence,
            leftDeviceGeneration, leftTransportGeneration);

    private void OnRightPumpAttention(
        Switch2BluetoothInputDrainPumpAttention evidence) =>
        OnPumpAttention(Switch2StickSide.Right, evidence,
            rightDeviceGeneration, rightTransportGeneration);

    private void OnPumpAttention(Switch2StickSide side,
        Switch2BluetoothInputDrainPumpAttention evidence,
        ulong expectedDeviceGeneration, ulong expectedTransportGeneration)
    {
        if (evidence == null ||
            evidence.DeviceGeneration != expectedDeviceGeneration ||
            evidence.TransportGeneration != expectedTransportGeneration ||
            Interlocked.CompareExchange(ref attentionRaised, 1, 0) != 0)
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
        EventHandler<Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
            handlers;
        lock (attentionGate) { handlers = attentionHandlers; }
        if (handlers == null) { return; }
        var args = new Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs(
            side, runtimeGeneration, pairEpoch, expectedDeviceGeneration,
            expectedTransportGeneration, evidence.EndReason,
            evidence.PumpFailure);
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<
                    Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>)
                    handler)(this, args);
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

        EventHandler<Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
            handlers;
        lock (attentionGate) { handlers = attentionHandlers; }
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
                    Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>)
                    handler)(this, userDisconnectAttention);
            }
            catch
            {
            }
        }
        return true;
    }

    private Switch2JoyConJoinedRuntimeSlotAdoptionCredential
        CreateAdoptionCredential() => new(this, slotAdoptionFence,
            boundSlotToken, runtimeGeneration, pairEpoch, pairId,
            pairRecordRevision, scanGeneration, leftDeviceGeneration,
            leftTransportGeneration, rightDeviceGeneration,
            rightTransportGeneration);

    private Switch2JoyConJoinedRuntimePrepareCredential
        CreatePrepareCredential(object fence) => new(this, fence,
            boundSlotToken, runtimeGeneration, pairEpoch, pairId,
            pairRecordRevision, scanGeneration, leftDeviceGeneration,
            leftTransportGeneration, rightDeviceGeneration,
            rightTransportGeneration);

    private bool AuthenticatesAdoption(
        in Switch2JoyConJoinedRuntimeSlotAdoptionCredential credential) =>
        credential.Authenticates(this, slotAdoptionFence, boundSlotToken,
            runtimeGeneration, pairEpoch, pairId, pairRecordRevision,
            scanGeneration, leftDeviceGeneration, leftTransportGeneration,
            rightDeviceGeneration, rightTransportGeneration);

    private bool AuthenticatesPrepare(
        in Switch2JoyConJoinedRuntimePrepareCredential credential) =>
        credential.Authenticates(this, preparedFence, boundSlotToken,
            runtimeGeneration, pairEpoch, pairId, pairRecordRevision,
            scanGeneration, leftDeviceGeneration, leftTransportGeneration,
            rightDeviceGeneration, rightTransportGeneration);

    private static bool ResolveConsumedPrepareFailure(
        Switch2JoyConJoinedRuntimeOwner candidate,
        in Switch2BluetoothInputPairPrepareResult pairResult,
        int timeoutMilliseconds, out Switch2JoyConJoinedRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2JoyConJoinedRuntimeCreateFailure failure)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        Switch2BluetoothInputLeaseReleaseResult left = pairResult.
            CleanupEvidence.Left.WaitForRelease(RemainingMilliseconds(deadline));
        Switch2BluetoothInputLeaseReleaseResult right = pairResult.
            CleanupEvidence.Right.WaitForRelease(RemainingMilliseconds(deadline));
        bool runtimeAborted = candidate.runtimeDevice.
            TryAbortUnpublishedActivation();
        bool clean = left == Switch2BluetoothInputLeaseReleaseResult.Released &&
            right == Switch2BluetoothInputLeaseReleaseResult.Released &&
            runtimeAborted;
        candidate.leftReleaseProven = left ==
            Switch2BluetoothInputLeaseReleaseResult.Released;
        candidate.rightReleaseProven = right ==
            Switch2BluetoothInputLeaseReleaseResult.Released;
        if (!clean)
        {
            candidate.MarkCreationQuarantined();
            owner = candidate;
            registration = default;
            failure = new Switch2JoyConJoinedRuntimeCreateFailure(
                left == Switch2BluetoothInputLeaseReleaseResult.TimedOut ||
                    right == Switch2BluetoothInputLeaseReleaseResult.TimedOut ?
                    Switch2JoyConJoinedRuntimeCreateFailureKind.
                        RollbackTimedOut :
                    Switch2JoyConJoinedRuntimeCreateFailureKind.
                        RollbackRejected,
                default, default, default, pairResult, default, default,
                left, right, candidate);
            return false;
        }
        owner = null;
        registration = default;
        failure = new Switch2JoyConJoinedRuntimeCreateFailure(
            Switch2JoyConJoinedRuntimeCreateFailureKind.PairInputRejected,
            default, default, default, pairResult, default, default,
            left, right, null);
        return false;
    }

    private static bool FailPostPairPrepare(
        Switch2JoyConJoinedRuntimeOwner candidate, int timeoutMilliseconds,
        Switch2JoyConJoinedRuntimeCreateFailureKind requestedKind,
        Switch2JoyConJoinedRuntimeSinkFailure sinkFailure,
        Switch2BluetoothInputDrainPumpFailure leftPumpFailure,
        Switch2BluetoothInputDrainPumpFailure rightPumpFailure,
        out Switch2JoyConJoinedRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2JoyConJoinedRuntimeCreateFailure failure)
    {
        bool clean = candidate.CleanupUnpublished(
            Environment.TickCount64 + timeoutMilliseconds,
            out Switch2BluetoothRuntimeAbortFailure abortFailure);
        Switch2BluetoothInputLeaseReleaseResult left =
            candidate.leftReleaseProven ?
                Switch2BluetoothInputLeaseReleaseResult.Released :
                abortFailure ==
                    Switch2BluetoothRuntimeAbortFailure.LeaseReleaseTimedOut ?
                    Switch2BluetoothInputLeaseReleaseResult.TimedOut :
                    Switch2BluetoothInputLeaseReleaseResult.Rejected;
        Switch2BluetoothInputLeaseReleaseResult right =
            candidate.rightReleaseProven ?
                Switch2BluetoothInputLeaseReleaseResult.Released :
                abortFailure ==
                    Switch2BluetoothRuntimeAbortFailure.LeaseReleaseTimedOut ?
                    Switch2BluetoothInputLeaseReleaseResult.TimedOut :
                    Switch2BluetoothInputLeaseReleaseResult.Rejected;
        if (!clean)
        {
            candidate.MarkCreationQuarantined();
            owner = candidate;
            registration = default;
            failure = new Switch2JoyConJoinedRuntimeCreateFailure(
                abortFailure ==
                    Switch2BluetoothRuntimeAbortFailure.LeaseReleaseTimedOut ||
                    abortFailure ==
                    Switch2BluetoothRuntimeAbortFailure.PumpTimedOut ?
                    Switch2JoyConJoinedRuntimeCreateFailureKind.
                        RollbackTimedOut :
                    Switch2JoyConJoinedRuntimeCreateFailureKind.
                        RollbackRejected,
                default, default, sinkFailure, default, leftPumpFailure,
                rightPumpFailure, left, right, candidate);
            return false;
        }
        owner = null;
        registration = default;
        failure = new Switch2JoyConJoinedRuntimeCreateFailure(requestedKind,
            default, default, sinkFailure, default, leftPumpFailure,
            rightPumpFailure, left, right, null);
        return false;
    }

    private static bool RetainUnknownPreparation(
        Switch2JoyConJoinedRuntimeOwner candidate, int timeoutMilliseconds,
        out Switch2JoyConJoinedRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2JoyConJoinedRuntimeCreateFailure failure)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        try { candidate.leftLease.TryUnsubscribeCccdNone(
            candidate.leftTransportGeneration); } catch { }
        try { candidate.rightLease.TryUnsubscribeCccdNone(
            candidate.rightTransportGeneration); } catch { }
        Switch2BluetoothInputLeaseReleaseResult left = WaitForRelease(
            candidate.leftReleaseProof, candidate.leftTransportGeneration,
            deadline);
        Switch2BluetoothInputLeaseReleaseResult right = WaitForRelease(
            candidate.rightReleaseProof, candidate.rightTransportGeneration,
            deadline);
        try { candidate.runtimeDevice.TryAbortUnpublishedActivation(); }
        catch { }
        candidate.leftReleaseProven = left ==
            Switch2BluetoothInputLeaseReleaseResult.Released;
        candidate.rightReleaseProven = right ==
            Switch2BluetoothInputLeaseReleaseResult.Released;
        candidate.MarkCreationQuarantined();
        owner = candidate;
        registration = default;
        failure = new Switch2JoyConJoinedRuntimeCreateFailure(
            Switch2JoyConJoinedRuntimeCreateFailureKind.DependencyThrew,
            default, default, default, default, default, default,
            left, right, candidate);
        return false;
    }

    private static bool RetainUnknownPump(
        Switch2JoyConJoinedRuntimeOwner candidate, int timeoutMilliseconds,
        bool isLeft, Switch2BluetoothInputDrainPumpFailure pumpFailure,
        out Switch2JoyConJoinedRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2JoyConJoinedRuntimeCreateFailure failure)
    {
        candidate.CleanupUnpublished(
            Environment.TickCount64 + timeoutMilliseconds, out _);
        candidate.MarkCreationQuarantined();
        owner = candidate;
        registration = default;
        failure = new Switch2JoyConJoinedRuntimeCreateFailure(
            Switch2JoyConJoinedRuntimeCreateFailureKind.DependencyThrew,
            default, default, default, default,
            isLeft ? pumpFailure : default,
            isLeft ? default : pumpFailure,
            candidate.leftReleaseProven ?
                Switch2BluetoothInputLeaseReleaseResult.Released :
                Switch2BluetoothInputLeaseReleaseResult.Rejected,
            candidate.rightReleaseProven ?
                Switch2BluetoothInputLeaseReleaseResult.Released :
                Switch2BluetoothInputLeaseReleaseResult.Rejected,
            candidate);
        return false;
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

    private static bool TryStopPump(ISwitch2BluetoothRuntimeDrainPump pump,
        long deadline, out Switch2BluetoothInputDrainPumpFailure failure)
    {
        if (pump == null)
        {
            failure = Switch2BluetoothInputDrainPumpFailure.None;
            return true;
        }
        try
        {
            return pump.TryStopAndJoin(RemainingMilliseconds(deadline),
                out failure);
        }
        catch
        {
            failure = Switch2BluetoothInputDrainPumpFailure.
                UnexpectedWorkerFailure;
            return false;
        }
    }

    private static Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
        ISwitch2BluetoothInputLeaseReleaseProof proof,
        ulong transportGeneration, long deadline)
    {
        try
        {
            return proof.WaitForRelease(transportGeneration,
                RemainingMilliseconds(deadline));
        }
        catch
        {
            return Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }
    }

    private void SetStopFailure(
        Switch2BluetoothRuntimeStopFailureKind kind)
    {
        lock (gate) { SetStopFailureNoLock(kind); }
    }

    private void SetStopFailureNoLock(
        Switch2BluetoothRuntimeStopFailureKind kind) =>
        lastStopFailure = new Switch2JoyConJoinedRuntimeStopFailure(kind,
            default, default, default, default, default);

    private bool HasActiveCallbackFence() =>
        leftPump?.IsCurrentWorkerThread == true ||
        rightPump?.IsCurrentWorkerThread == true ||
        sink?.PublicationInProgress == true ||
        sink?.IsCurrentPublicationThread == true ||
        sink?.TerminalPublicationInProgress == true;

    private bool IsCurrentCallbackThread() =>
        leftPump?.IsCurrentWorkerThread == true ||
        rightPump?.IsCurrentWorkerThread == true ||
        sink?.IsCurrentPublicationThread == true ||
        sink?.IsCurrentTerminalPublicationThread == true;

    private static Switch2JoyConJoinedRuntimeCreateFailure CreateFailure(
        Switch2JoyConJoinedRuntimeCreateFailureKind kind) => new(kind,
            default, default, default, default, default, default, default,
            default, null);

    private static int RemainingMilliseconds(long deadline)
    {
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 :
            (int)Math.Min(remaining, int.MaxValue);
    }
}
