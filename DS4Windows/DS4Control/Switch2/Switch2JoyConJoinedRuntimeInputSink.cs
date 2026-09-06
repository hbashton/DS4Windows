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
using DS4Windows.InputDevices;

namespace DS4Windows.Switch2;

internal enum Switch2JoyConJoinedRuntimeSinkFailure : byte
{
    None = 0,
    InvalidArgument,
    DescriptorMismatch,
    RuntimeDeviceMismatch,
    DescriptorNotBound,
    LifecycleClosed,
    CanonicalFrameMismatch,
    PublicationAlreadyInProgress,
    PublicationSerializationTimedOut,
    CoordinatorRejected,
    RuntimeFrameRejected,
    RuntimeSubscriberRejected,
    RuntimePublicationAdmissionTimedOut,
    TerminalIdentityMismatch,
    InvalidTerminalCredential,
    TerminalNotRequested,
    TerminalCompletionReentrant,
    TerminalSchedulerRejected,
    TerminalPublicationRejected,
    TerminalDeliveryTimedOut,
    TerminalDeliveryRejected,
    DependencyThrew,
}

internal readonly struct Switch2JoyConJoinedRuntimeSinkBindingCredential
{
    private readonly Switch2JoyConJoinedRuntimeInputSink issuer;
    private readonly object fence;

    internal Switch2JoyConJoinedRuntimeSinkBindingCredential(
        Switch2JoyConJoinedRuntimeInputSink issuer, object fence,
        ulong runtimeGeneration, ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        RuntimeGeneration = runtimeGeneration;
        PairEpoch = pairEpoch;
        LeftDeviceGeneration = leftDeviceGeneration;
        LeftTransportGeneration = leftTransportGeneration;
        RightDeviceGeneration = rightDeviceGeneration;
        RightTransportGeneration = rightTransportGeneration;
    }

    internal ulong RuntimeGeneration { get; }

    internal ulong PairEpoch { get; }

    internal ulong LeftDeviceGeneration { get; }

    internal ulong LeftTransportGeneration { get; }

    internal ulong RightDeviceGeneration { get; }

    internal ulong RightTransportGeneration { get; }

    internal bool Authenticates(Switch2JoyConJoinedRuntimeInputSink candidate,
        object expectedFence) => ReferenceEquals(issuer, candidate) &&
        ReferenceEquals(fence, expectedFence) && RuntimeGeneration != 0 &&
        PairEpoch != 0 && LeftDeviceGeneration != 0 &&
        LeftTransportGeneration != 0 && RightDeviceGeneration != 0 &&
        RightTransportGeneration != 0;
}

internal readonly struct Switch2JoyConJoinedRuntimeTerminalCredential
{
    private readonly Switch2JoyConJoinedRuntimeInputSink issuer;
    private readonly object fence;

    internal Switch2JoyConJoinedRuntimeTerminalCredential(
        Switch2JoyConJoinedRuntimeInputSink issuer, object fence,
        ulong runtimeGeneration, ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration)
    {
        this.issuer = issuer;
        this.fence = fence;
        RuntimeGeneration = runtimeGeneration;
        PairEpoch = pairEpoch;
        LeftDeviceGeneration = leftDeviceGeneration;
        LeftTransportGeneration = leftTransportGeneration;
        RightDeviceGeneration = rightDeviceGeneration;
        RightTransportGeneration = rightTransportGeneration;
    }

    internal ulong RuntimeGeneration { get; }

    internal ulong PairEpoch { get; }

    internal ulong LeftDeviceGeneration { get; }

    internal ulong LeftTransportGeneration { get; }

    internal ulong RightDeviceGeneration { get; }

    internal ulong RightTransportGeneration { get; }

    internal bool Authenticates(Switch2JoyConJoinedRuntimeInputSink candidate,
        object expectedFence, ulong expectedRuntimeGeneration,
        ulong expectedPairEpoch, ulong expectedLeftDeviceGeneration,
        ulong expectedLeftTransportGeneration,
        ulong expectedRightDeviceGeneration,
        ulong expectedRightTransportGeneration) =>
        ReferenceEquals(issuer, candidate) &&
        ReferenceEquals(fence, expectedFence) &&
        RuntimeGeneration == expectedRuntimeGeneration &&
        PairEpoch == expectedPairEpoch &&
        LeftDeviceGeneration == expectedLeftDeviceGeneration &&
        LeftTransportGeneration == expectedLeftTransportGeneration &&
        RightDeviceGeneration == expectedRightDeviceGeneration &&
        RightTransportGeneration == expectedRightTransportGeneration;
}

/// <summary>
/// Dormant two-source canonical-to-runtime bridge for one joined Joy-Con
/// lifetime. Two independently serialized Bluetooth owners may target this
/// sink; this object supplies the missing cross-owner serialization and fences
/// every input and loss callback to the exact pair epoch and both immutable
/// physical descriptors. It performs no discovery, association, registration,
/// transport I/O, owner construction, or production wiring.
/// </summary>
internal sealed class Switch2JoyConJoinedRuntimeInputSink :
    ISwitch2BluetoothCanonicalInputSink
{
    // Both physical halves observe the same joined runtime handoff.
    public bool IsVirtualOutputTransitionActive =>
        runtimeDevice.IsVirtualOutputTransitionActive;

    private readonly object sync = new();
    private readonly Switch2RuntimeInputDevice runtimeDevice;
    private readonly Switch2JoyConPairPolicy pairPolicy;
    private readonly int runtimeOperationTimeoutMilliseconds;
    private readonly ISwitch2RuntimeTerminalScheduler terminalScheduler;
    private readonly ulong runtimeGeneration;
    private readonly ulong pairEpoch;
    private readonly ulong expectedLeftDeviceGeneration;
    private readonly ulong expectedLeftTransportGeneration;
    private readonly ulong expectedRightDeviceGeneration;
    private readonly ulong expectedRightTransportGeneration;
    private readonly object descriptorBindingFence = new();
    private readonly object terminalFence = new();

    private Switch2InputSessionDescriptor leftDescriptor;
    private Switch2InputSessionDescriptor rightDescriptor;
    private Switch2JoyConJoinedCoordinatorState coordinatorState;
    private bool descriptorBound;
    private bool leftAttached;
    private bool rightAttached;
    private bool pendingLeftLoss;
    private bool pendingRightLoss;
    private bool publicationInProgress;
    private int publicationThreadId;
    private Switch2JoyConJoinedRuntimeSinkFailure lastFailure;
    private Switch2JoyConJoinedCoordinatorFailure lastCoordinatorFailure;
    private Switch2JoyConPairRejection lastPairRejection;
    private Switch2JoyConProfileInputFailure lastProfileFailure;
    private Switch2JoyConPairDisposition lastPairDisposition;
    private long consumedCount;
    private long stateOnlyCount;
    private long publishedCount;
    private long publicationSerializationWaitCount;
    private long runtimeAdmissionWaitCount;

    private bool terminalRequested;
    private Switch2BluetoothInputEndReason terminalReason;
    private Switch2BluetoothRuntimeTerminalState terminalState;
    private Switch2JoyConJoinedRuntimeSinkFailure terminalFailure;
    private bool terminalScheduleInProgress;
    private Task<Switch2TerminalNeutralRequestResult> terminalTask;
    private int terminalPublicationThreadId;
    private long terminalScheduleAttemptCount;
    private long terminalWaitTimeoutCount;

    private Switch2JoyConJoinedRuntimeInputSink(
        in Switch2InputSessionDescriptor leftDescriptor,
        in Switch2InputSessionDescriptor rightDescriptor,
        in Switch2JoyConJoinedCoordinatorState coordinatorState,
        Switch2RuntimeInputDevice runtimeDevice,
        in Switch2JoyConPairPolicy pairPolicy,
        int runtimeOperationTimeoutMilliseconds,
        ISwitch2RuntimeTerminalScheduler terminalScheduler,
        bool descriptorBound, ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration)
    {
        this.leftDescriptor = leftDescriptor;
        this.rightDescriptor = rightDescriptor;
        this.coordinatorState = coordinatorState;
        this.runtimeDevice = runtimeDevice;
        this.pairPolicy = pairPolicy;
        this.runtimeOperationTimeoutMilliseconds =
            runtimeOperationTimeoutMilliseconds;
        this.terminalScheduler = terminalScheduler;
        this.descriptorBound = descriptorBound;
        leftAttached = descriptorBound;
        rightAttached = descriptorBound;
        runtimeGeneration = runtimeDevice.RuntimeGeneration;
        this.pairEpoch = pairEpoch;
        expectedLeftDeviceGeneration = leftDeviceGeneration;
        expectedLeftTransportGeneration = leftTransportGeneration;
        expectedRightDeviceGeneration = rightDeviceGeneration;
        expectedRightTransportGeneration = rightTransportGeneration;
    }

    internal Switch2RuntimeInputDevice RuntimeDevice => runtimeDevice;

    internal ulong PairEpoch => pairEpoch;

    internal bool DescriptorBound
    {
        get
        {
            lock (sync)
            {
                return descriptorBound;
            }
        }
    }

    internal bool LeftAttached
    {
        get
        {
            lock (sync)
            {
                return leftAttached;
            }
        }
    }

    internal bool RightAttached
    {
        get
        {
            lock (sync)
            {
                return rightAttached;
            }
        }
    }

    internal bool HasStagedLeft
    {
        get
        {
            lock (sync)
            {
                return coordinatorState.PairState.HasLeft;
            }
        }
    }

    internal bool HasStagedRight
    {
        get
        {
            lock (sync)
            {
                return coordinatorState.PairState.HasRight;
            }
        }
    }

    internal bool MapperHasAcceptedLeft
    {
        get
        {
            lock (sync)
            {
                return coordinatorState.MapperState.HasAcceptedLeft;
            }
        }
    }

    internal bool MapperHasAcceptedRight
    {
        get
        {
            lock (sync)
            {
                return coordinatorState.MapperState.HasAcceptedRight;
            }
        }
    }

    internal long ConsumedCount
    {
        get
        {
            lock (sync)
            {
                return consumedCount;
            }
        }
    }

    internal long StateOnlyCount
    {
        get
        {
            lock (sync)
            {
                return stateOnlyCount;
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

    internal long PublicationSerializationWaitCount =>
        Interlocked.Read(ref publicationSerializationWaitCount);

    internal long RuntimeAdmissionWaitCount =>
        Interlocked.Read(ref runtimeAdmissionWaitCount);

    internal Switch2JoyConJoinedRuntimeSinkFailure LastFailure
    {
        get
        {
            lock (sync)
            {
                return lastFailure;
            }
        }
    }

    internal Switch2JoyConJoinedCoordinatorFailure LastCoordinatorFailure
    {
        get
        {
            lock (sync)
            {
                return lastCoordinatorFailure;
            }
        }
    }

    internal Switch2JoyConPairRejection LastPairRejection
    {
        get
        {
            lock (sync)
            {
                return lastPairRejection;
            }
        }
    }

    internal Switch2JoyConProfileInputFailure LastProfileFailure
    {
        get
        {
            lock (sync)
            {
                return lastProfileFailure;
            }
        }
    }

    internal Switch2JoyConPairDisposition LastPairDisposition
    {
        get
        {
            lock (sync)
            {
                return lastPairDisposition;
            }
        }
    }

    internal bool PublicationInProgress
    {
        get
        {
            lock (sync)
            {
                return publicationInProgress;
            }
        }
    }

    internal bool IsCurrentPublicationThread
    {
        get
        {
            lock (sync)
            {
                return publicationInProgress && publicationThreadId ==
                    Environment.CurrentManagedThreadId;
            }
        }
    }

    internal bool TerminalPublicationInProgress
    {
        get
        {
            lock (sync)
            {
                return terminalPublicationThreadId != 0;
            }
        }
    }

    internal bool IsCurrentTerminalPublicationThread
    {
        get
        {
            lock (sync)
            {
                return terminalPublicationThreadId ==
                    Environment.CurrentManagedThreadId;
            }
        }
    }

    internal bool TerminalRequested
    {
        get
        {
            lock (sync)
            {
                return terminalRequested;
            }
        }
    }

    internal Switch2BluetoothInputEndReason TerminalReason
    {
        get
        {
            lock (sync)
            {
                return terminalReason;
            }
        }
    }

    internal Switch2BluetoothRuntimeTerminalState TerminalState
    {
        get
        {
            lock (sync)
            {
                return terminalState;
            }
        }
    }

    internal Switch2JoyConJoinedRuntimeSinkFailure TerminalFailure
    {
        get
        {
            lock (sync)
            {
                return terminalFailure;
            }
        }
    }

    internal long TerminalScheduleAttemptCount =>
        Interlocked.Read(ref terminalScheduleAttemptCount);

    internal long TerminalWaitTimeoutCount =>
        Interlocked.Read(ref terminalWaitTimeoutCount);

    internal static bool TryCreateBound(ulong pairEpoch,
        in Switch2InputSessionDescriptor leftDescriptor,
        in Switch2InputSessionDescriptor rightDescriptor,
        Switch2RuntimeInputDevice runtimeDevice,
        in Switch2JoyConPairPolicy pairPolicy,
        int runtimeOperationTimeoutMilliseconds,
        ISwitch2RuntimeTerminalScheduler terminalScheduler,
        out Switch2JoyConJoinedRuntimeInputSink sink,
        out Switch2JoyConJoinedRuntimeTerminalCredential terminalCredential,
        out Switch2JoyConJoinedRuntimeSinkFailure failure)
    {
        sink = null;
        terminalCredential = default;
        if (!ValidateArguments(pairEpoch, leftDescriptor.DeviceGeneration,
                leftDescriptor.TransportGeneration,
                rightDescriptor.DeviceGeneration,
                rightDescriptor.TransportGeneration, runtimeDevice,
                runtimeOperationTimeoutMilliseconds, terminalScheduler,
                out failure))
        {
            return false;
        }
        if (!IsExactBluetoothDescriptor(leftDescriptor,
                Switch2ControllerModel.JoyCon2Left,
                leftDescriptor.DeviceGeneration,
                leftDescriptor.TransportGeneration) ||
            !IsExactBluetoothDescriptor(rightDescriptor,
                Switch2ControllerModel.JoyCon2Right,
                rightDescriptor.DeviceGeneration,
                rightDescriptor.TransportGeneration))
        {
            failure = Switch2JoyConJoinedRuntimeSinkFailure.
                DescriptorMismatch;
            return false;
        }
        if (!RuntimeMatches(runtimeDevice, pairEpoch,
                leftDescriptor.DeviceGeneration,
                leftDescriptor.TransportGeneration,
                rightDescriptor.DeviceGeneration,
                rightDescriptor.TransportGeneration))
        {
            failure = Switch2JoyConJoinedRuntimeSinkFailure.
                RuntimeDeviceMismatch;
            return false;
        }
        if (!Switch2JoyConJoinedCoordinatorState.TryCreate(pairEpoch,
                leftDescriptor, rightDescriptor, out var coordinatorState))
        {
            failure = Switch2JoyConJoinedRuntimeSinkFailure.
                DescriptorMismatch;
            return false;
        }

        sink = new Switch2JoyConJoinedRuntimeInputSink(leftDescriptor,
            rightDescriptor, coordinatorState, runtimeDevice, pairPolicy,
            runtimeOperationTimeoutMilliseconds, terminalScheduler,
            descriptorBound: true, pairEpoch,
            leftDescriptor.DeviceGeneration,
            leftDescriptor.TransportGeneration,
            rightDescriptor.DeviceGeneration,
            rightDescriptor.TransportGeneration);
        terminalCredential = sink.CreateTerminalCredential();
        failure = Switch2JoyConJoinedRuntimeSinkFailure.None;
        return true;
    }

    internal static bool TryCreateUnbound(ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration,
        Switch2RuntimeInputDevice runtimeDevice,
        in Switch2JoyConPairPolicy pairPolicy,
        int runtimeOperationTimeoutMilliseconds,
        ISwitch2RuntimeTerminalScheduler terminalScheduler,
        out Switch2JoyConJoinedRuntimeInputSink sink,
        out Switch2JoyConJoinedRuntimeSinkBindingCredential bindingCredential,
        out Switch2JoyConJoinedRuntimeTerminalCredential terminalCredential,
        out Switch2JoyConJoinedRuntimeSinkFailure failure)
    {
        sink = null;
        bindingCredential = default;
        terminalCredential = default;
        if (!ValidateArguments(pairEpoch, leftDeviceGeneration,
                leftTransportGeneration, rightDeviceGeneration,
                rightTransportGeneration, runtimeDevice,
                runtimeOperationTimeoutMilliseconds, terminalScheduler,
                out failure))
        {
            return false;
        }
        if (!RuntimeMatches(runtimeDevice, pairEpoch, leftDeviceGeneration,
                leftTransportGeneration, rightDeviceGeneration,
                rightTransportGeneration))
        {
            failure = Switch2JoyConJoinedRuntimeSinkFailure.
                RuntimeDeviceMismatch;
            return false;
        }

        sink = new Switch2JoyConJoinedRuntimeInputSink(default, default,
            default, runtimeDevice, pairPolicy,
            runtimeOperationTimeoutMilliseconds, terminalScheduler,
            descriptorBound: false, pairEpoch, leftDeviceGeneration,
            leftTransportGeneration, rightDeviceGeneration,
            rightTransportGeneration);
        bindingCredential =
            new Switch2JoyConJoinedRuntimeSinkBindingCredential(sink,
                sink.descriptorBindingFence, sink.runtimeGeneration, pairEpoch,
                leftDeviceGeneration, leftTransportGeneration,
                rightDeviceGeneration, rightTransportGeneration);
        terminalCredential = sink.CreateTerminalCredential();
        failure = Switch2JoyConJoinedRuntimeSinkFailure.None;
        return true;
    }

    internal bool TryBindDescriptors(
        in Switch2JoyConJoinedRuntimeSinkBindingCredential credential,
        in Switch2InputSessionDescriptor exactLeftDescriptor,
        in Switch2InputSessionDescriptor exactRightDescriptor,
        out Switch2JoyConJoinedRuntimeSinkFailure failure)
    {
        if (!credential.Authenticates(this, descriptorBindingFence) ||
            credential.RuntimeGeneration != runtimeGeneration ||
            credential.PairEpoch != pairEpoch ||
            credential.LeftDeviceGeneration !=
                expectedLeftDeviceGeneration ||
            credential.LeftTransportGeneration !=
                expectedLeftTransportGeneration ||
            credential.RightDeviceGeneration !=
                expectedRightDeviceGeneration ||
            credential.RightTransportGeneration !=
                expectedRightTransportGeneration ||
            !IsExactBluetoothDescriptor(exactLeftDescriptor,
                Switch2ControllerModel.JoyCon2Left,
                expectedLeftDeviceGeneration,
                expectedLeftTransportGeneration) ||
            !IsExactBluetoothDescriptor(exactRightDescriptor,
                Switch2ControllerModel.JoyCon2Right,
                expectedRightDeviceGeneration,
                expectedRightTransportGeneration) ||
            !Switch2JoyConJoinedCoordinatorState.TryCreate(pairEpoch,
                exactLeftDescriptor, exactRightDescriptor,
                out var initialCoordinator))
        {
            failure = Switch2JoyConJoinedRuntimeSinkFailure.
                DescriptorMismatch;
            return false;
        }

        lock (sync)
        {
            if (descriptorBound || publicationInProgress ||
                terminalRequested || runtimeDevice.RuntimeState !=
                    Switch2RuntimeInputDeviceState.Created)
            {
                failure = Switch2JoyConJoinedRuntimeSinkFailure.
                    LifecycleClosed;
                return false;
            }

            leftDescriptor = exactLeftDescriptor;
            rightDescriptor = exactRightDescriptor;
            coordinatorState = initialCoordinator;
            leftAttached = true;
            rightAttached = true;
            descriptorBound = true;
        }
        failure = Switch2JoyConJoinedRuntimeSinkFailure.None;
        return true;
    }

    public void PublishPro(in Switch2CanonicalInputFrame frame)
    {
        RejectCanonicalKind();
    }

    public void PublishJoyCon(in Switch2CanonicalInputFrame frame)
    {
        Switch2JoyConJoinedCoordinatorState current = BeginPublication(frame);
        bool consumed = false;
        bool published = false;
        bool stateOnly = false;
        bool finalized = false;
        Switch2JoyConJoinedRuntimeSinkFailure failure = default;
        Switch2JoyConJoinedCoordinatorState candidate = default;
        Switch2JoyConJoinedCoordinatorResult coordinatorResult = default;
        try
        {
            Switch2JoyConPairEvent pairEvent =
                Switch2JoyConPairEvent.Input(pairEpoch, frame);
            if (!Switch2JoyConJoinedCoordinator.TryProcess(current, pairEvent,
                    pairPolicy, out candidate, out coordinatorResult))
            {
                failure = Switch2JoyConJoinedRuntimeSinkFailure.
                    CoordinatorRejected;
            }
            else if (coordinatorResult.PairResult.Disposition is
                    Switch2JoyConPairDisposition.WaitingForOtherHalf or
                    Switch2JoyConPairDisposition.StaleHalf)
            {
                consumed = true;
                stateOnly = true;
            }
            else if (coordinatorResult.HasProfileFrame)
            {
                Switch2RuntimePublicationResult publication =
                    PublishJoinedWithAdmission(coordinatorResult.ProfileFrame);
                published = publication ==
                    Switch2RuntimePublicationResult.Published;
                consumed = published;
                if (!published)
                {
                    failure = FailureFor(publication);
                }
            }
            else
            {
                failure = Switch2JoyConJoinedRuntimeSinkFailure.
                    CoordinatorRejected;
            }
        }
        catch
        {
            failure = Switch2JoyConJoinedRuntimeSinkFailure.DependencyThrew;
        }
        finally
        {
            finalized = EndPublication(consumed, published, stateOnly,
                candidate, coordinatorResult, failure);
        }

        if (!consumed || !finalized)
        {
            throw new InvalidOperationException(
                "Joined Joy-Con runtime publication was rejected.");
        }
    }

    public void ClearPro(ulong deviceGeneration, ulong transportGeneration,
        Switch2BluetoothInputEndReason reason)
    {
        lock (sync)
        {
            lastFailure = Switch2JoyConJoinedRuntimeSinkFailure.
                TerminalIdentityMismatch;
        }
        throw new InvalidOperationException(
            "A joined Joy-Con sink cannot clear a Pro lifetime.");
    }

    public void LoseJoyConHalf(Switch2StickSide side,
        ulong deviceGeneration, ulong transportGeneration,
        Switch2BluetoothInputEndReason reason)
    {
        bool applied;
        lock (sync)
        {
            bool validReason = reason is
                Switch2BluetoothInputEndReason.Disconnected or
                Switch2BluetoothInputEndReason.Stopped or
                Switch2BluetoothInputEndReason.QueueOverflow or
                Switch2BluetoothInputEndReason.SinkFailure;
            bool left = side == Switch2StickSide.Left;
            bool right = side == Switch2StickSide.Right;
            bool exactLifetime = left ?
                deviceGeneration == expectedLeftDeviceGeneration &&
                transportGeneration == expectedLeftTransportGeneration :
                right && deviceGeneration == expectedRightDeviceGeneration &&
                transportGeneration == expectedRightTransportGeneration;
            if (!descriptorBound || !validReason || !exactLifetime)
            {
                lastFailure = Switch2JoyConJoinedRuntimeSinkFailure.
                    TerminalIdentityMismatch;
                throw new InvalidOperationException(
                    "Joined Joy-Con terminal identity was rejected.");
            }

            bool attached = left ? leftAttached : rightAttached;
            if (attached)
            {
                if (left)
                {
                    leftAttached = false;
                    pendingLeftLoss = true;
                }
                else
                {
                    rightAttached = false;
                    pendingRightLoss = true;
                }
            }

            if (!terminalRequested)
            {
                terminalRequested = true;
                terminalReason = reason;
                terminalState =
                    Switch2BluetoothRuntimeTerminalState.Requested;
            }

            // A second exact physical callback is cleanup evidence, not a new
            // logical terminal epoch. It may carry a later local reason; the
            // first exact reason remains authoritative.
            applied = publicationInProgress || ApplyPendingLossNoLock();
            lastFailure = applied ?
                Switch2JoyConJoinedRuntimeSinkFailure.None :
                Switch2JoyConJoinedRuntimeSinkFailure.CoordinatorRejected;
            Monitor.PulseAll(sync);
        }

        if (!applied)
        {
            throw new InvalidOperationException(
                "Joined Joy-Con loss could not retire coordinator state.");
        }
    }

    internal bool TryCompleteTerminalNeutral(
        in Switch2JoyConJoinedRuntimeTerminalCredential credential,
        int timeoutMilliseconds,
        out Switch2JoyConJoinedRuntimeSinkFailure failure)
    {
        if (!credential.Authenticates(this, terminalFence,
                runtimeGeneration, pairEpoch,
                expectedLeftDeviceGeneration,
                expectedLeftTransportGeneration,
                expectedRightDeviceGeneration,
                expectedRightTransportGeneration) ||
            timeoutMilliseconds < 0 || timeoutMilliseconds >
                InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            failure = Switch2JoyConJoinedRuntimeSinkFailure.
                InvalidTerminalCredential;
            return false;
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        Task<Switch2TerminalNeutralRequestResult> exactTask = null;
        while (exactTask == null)
        {
            lock (sync)
            {
                if (!terminalRequested)
                {
                    SetTerminalFailureNoLock(
                        Switch2JoyConJoinedRuntimeSinkFailure.
                            TerminalNotRequested);
                    failure = Switch2JoyConJoinedRuntimeSinkFailure.
                        TerminalNotRequested;
                    return false;
                }
                if (terminalState ==
                    Switch2BluetoothRuntimeTerminalState.Delivered)
                {
                    failure = Switch2JoyConJoinedRuntimeSinkFailure.None;
                    return true;
                }
                if (terminalState ==
                    Switch2BluetoothRuntimeTerminalState.Rejected)
                {
                    failure = terminalFailure == default ?
                        Switch2JoyConJoinedRuntimeSinkFailure.
                            TerminalDeliveryRejected : terminalFailure;
                    return false;
                }
                if (publicationInProgress && publicationThreadId ==
                        Environment.CurrentManagedThreadId)
                {
                    SetTerminalFailureNoLock(
                        Switch2JoyConJoinedRuntimeSinkFailure.
                            TerminalCompletionReentrant);
                    failure = Switch2JoyConJoinedRuntimeSinkFailure.
                        TerminalCompletionReentrant;
                    return false;
                }
                if (terminalTask != null)
                {
                    exactTask = terminalTask;
                    break;
                }
                if (!terminalScheduleInProgress)
                {
                    terminalScheduleInProgress = true;
                    break;
                }

                int remaining = RemainingMilliseconds(deadline);
                if (remaining == 0 || !Monitor.Wait(sync, remaining))
                {
                    Interlocked.Increment(ref terminalWaitTimeoutCount);
                    SetTerminalFailureNoLock(
                        Switch2JoyConJoinedRuntimeSinkFailure.
                            TerminalDeliveryTimedOut);
                    failure = Switch2JoyConJoinedRuntimeSinkFailure.
                        TerminalDeliveryTimedOut;
                    return false;
                }
            }
        }

        if (exactTask == null)
        {
            bool scheduled;
            Interlocked.Increment(ref terminalScheduleAttemptCount);
            try
            {
                scheduled = terminalScheduler.TrySchedule(
                    PublishTerminalNeutral, out exactTask) &&
                    exactTask != null;
            }
            catch
            {
                scheduled = false;
            }

            lock (sync)
            {
                terminalScheduleInProgress = false;
                if (!scheduled)
                {
                    terminalState =
                        Switch2BluetoothRuntimeTerminalState.Rejected;
                    SetTerminalFailureNoLock(
                        Switch2JoyConJoinedRuntimeSinkFailure.
                            TerminalSchedulerRejected);
                }
                else
                {
                    terminalTask = exactTask;
                }
                Monitor.PulseAll(sync);
            }
            if (!scheduled)
            {
                failure = Switch2JoyConJoinedRuntimeSinkFailure.
                    TerminalSchedulerRejected;
                return false;
            }
        }

        int taskRemaining = RemainingMilliseconds(deadline);
        try
        {
            if (!exactTask.Wait(taskRemaining))
            {
                Interlocked.Increment(ref terminalWaitTimeoutCount);
                lock (sync)
                {
                    SetTerminalFailureNoLock(
                        Switch2JoyConJoinedRuntimeSinkFailure.
                            TerminalDeliveryTimedOut);
                }
                failure = Switch2JoyConJoinedRuntimeSinkFailure.
                    TerminalDeliveryTimedOut;
                return false;
            }
        }
        catch
        {
            lock (sync)
            {
                terminalState =
                    Switch2BluetoothRuntimeTerminalState.Rejected;
                SetTerminalFailureNoLock(
                    Switch2JoyConJoinedRuntimeSinkFailure.DependencyThrew);
            }
            failure = Switch2JoyConJoinedRuntimeSinkFailure.DependencyThrew;
            return false;
        }

        Switch2TerminalNeutralRequestResult result = exactTask.Result;
        if (result == Switch2TerminalNeutralRequestResult.
                RejectedAlreadyReserved)
        {
            lock (sync)
            {
                terminalState =
                    Switch2BluetoothRuntimeTerminalState.Rejected;
                SetTerminalFailureNoLock(
                    Switch2JoyConJoinedRuntimeSinkFailure.
                        TerminalPublicationRejected);
            }
            failure = Switch2JoyConJoinedRuntimeSinkFailure.
                TerminalPublicationRejected;
            return false;
        }

        lock (sync)
        {
            if (terminalState ==
                Switch2BluetoothRuntimeTerminalState.Delivered)
            {
                failure = Switch2JoyConJoinedRuntimeSinkFailure.None;
                return true;
            }
            if (terminalState ==
                Switch2BluetoothRuntimeTerminalState.Rejected)
            {
                failure = terminalFailure == default ?
                    Switch2JoyConJoinedRuntimeSinkFailure.
                        TerminalDeliveryRejected : terminalFailure;
                return false;
            }
            terminalState =
                Switch2BluetoothRuntimeTerminalState.AcceptedPending;
        }

        bool completed;
        try
        {
            completed = runtimeDevice.TryWaitForTerminalNeutralCompletion(
                RemainingMilliseconds(deadline));
        }
        catch
        {
            lock (sync)
            {
                terminalState =
                    Switch2BluetoothRuntimeTerminalState.Rejected;
                SetTerminalFailureNoLock(
                    Switch2JoyConJoinedRuntimeSinkFailure.DependencyThrew);
            }
            failure = Switch2JoyConJoinedRuntimeSinkFailure.DependencyThrew;
            return false;
        }
        if (!completed)
        {
            Interlocked.Increment(ref terminalWaitTimeoutCount);
            lock (sync)
            {
                SetTerminalFailureNoLock(
                    Switch2JoyConJoinedRuntimeSinkFailure.
                        TerminalDeliveryTimedOut);
            }
            failure = Switch2JoyConJoinedRuntimeSinkFailure.
                TerminalDeliveryTimedOut;
            return false;
        }
        if (!runtimeDevice.TerminalNeutralCompleted ||
            !runtimeDevice.TerminalNeutralReported)
        {
            lock (sync)
            {
                terminalState =
                    Switch2BluetoothRuntimeTerminalState.Rejected;
                SetTerminalFailureNoLock(
                    Switch2JoyConJoinedRuntimeSinkFailure.
                        TerminalDeliveryRejected);
            }
            failure = Switch2JoyConJoinedRuntimeSinkFailure.
                TerminalDeliveryRejected;
            return false;
        }

        lock (sync)
        {
            terminalState = Switch2BluetoothRuntimeTerminalState.Delivered;
        }
        failure = Switch2JoyConJoinedRuntimeSinkFailure.None;
        return true;
    }

    private Switch2JoyConJoinedCoordinatorState BeginPublication(
        in Switch2CanonicalInputFrame frame)
    {
        long deadline = Environment.TickCount64 +
            runtimeOperationTimeoutMilliseconds;
        bool waitCounted = false;
        lock (sync)
        {
            while (true)
            {
                Switch2JoyConJoinedRuntimeSinkFailure failure;
                if (!descriptorBound)
                {
                    failure = Switch2JoyConJoinedRuntimeSinkFailure.
                        DescriptorNotBound;
                }
                else if (terminalRequested || runtimeDevice.RuntimeState !=
                         Switch2RuntimeInputDeviceState.Active)
                {
                    failure = Switch2JoyConJoinedRuntimeSinkFailure.
                        LifecycleClosed;
                }
                else
                {
                    bool left = frame.Model ==
                        Switch2ControllerModel.JoyCon2Left;
                    bool right = frame.Model ==
                        Switch2ControllerModel.JoyCon2Right;
                    Switch2InputSessionDescriptor expected = left ?
                        leftDescriptor : right ? rightDescriptor : default;
                    bool attached = left ? leftAttached :
                        right && rightAttached;
                    if ((!left && !right) || !attached ||
                        !frame.Descriptor.Equals(expected))
                    {
                        failure = Switch2JoyConJoinedRuntimeSinkFailure.
                            CanonicalFrameMismatch;
                    }
                    else if (!publicationInProgress)
                    {
                        publicationInProgress = true;
                        publicationThreadId =
                            Environment.CurrentManagedThreadId;
                        return coordinatorState;
                    }
                    else if (publicationThreadId ==
                             Environment.CurrentManagedThreadId)
                    {
                        failure = Switch2JoyConJoinedRuntimeSinkFailure.
                            PublicationAlreadyInProgress;
                    }
                    else
                    {
                        int remaining = RemainingMilliseconds(deadline);
                        if (remaining != 0)
                        {
                            if (!waitCounted)
                            {
                                waitCounted = true;
                                Interlocked.Increment(ref
                                    publicationSerializationWaitCount);
                            }
                            if (Monitor.Wait(sync, remaining))
                            {
                                continue;
                            }
                        }
                        failure = Switch2JoyConJoinedRuntimeSinkFailure.
                            PublicationSerializationTimedOut;
                    }
                }

                lastFailure = failure;
                throw new InvalidOperationException(
                    "Joined Joy-Con canonical publication was rejected.");
            }
        }
    }

    private bool EndPublication(bool consumed, bool published,
        bool stateOnly,
        in Switch2JoyConJoinedCoordinatorState candidate,
        in Switch2JoyConJoinedCoordinatorResult coordinatorResult,
        Switch2JoyConJoinedRuntimeSinkFailure failure)
    {
        lock (sync)
        {
            if (consumed)
            {
                coordinatorState = candidate;
            }
            lastCoordinatorFailure = coordinatorResult.Failure;
            lastPairRejection = coordinatorResult.PairResult.Rejection;
            lastProfileFailure = coordinatorResult.ProfileFailure;
            lastPairDisposition = coordinatorResult.PairResult.Disposition;

            bool pendingLossApplied = ApplyPendingLossNoLock();
            publicationInProgress = false;
            publicationThreadId = 0;
            if (consumed && pendingLossApplied)
            {
                consumedCount++;
                if (stateOnly)
                {
                    stateOnlyCount++;
                }
                if (published)
                {
                    publishedCount++;
                }
                lastFailure = Switch2JoyConJoinedRuntimeSinkFailure.None;
            }
            else
            {
                lastFailure = !pendingLossApplied ?
                    Switch2JoyConJoinedRuntimeSinkFailure.CoordinatorRejected :
                    failure == default ?
                        Switch2JoyConJoinedRuntimeSinkFailure.DependencyThrew :
                        failure;
            }
            Monitor.PulseAll(sync);
            return pendingLossApplied;
        }
    }

    private bool ApplyPendingLossNoLock()
    {
        if (pendingLeftLoss)
        {
            pendingLeftLoss = false;
            if (coordinatorState.PairState.HasLeft &&
                !TryApplyLossNoLock(Switch2StickSide.Left,
                    expectedLeftDeviceGeneration,
                    expectedLeftTransportGeneration))
            {
                return false;
            }
        }
        if (pendingRightLoss)
        {
            pendingRightLoss = false;
            if (coordinatorState.PairState.HasRight &&
                !TryApplyLossNoLock(Switch2StickSide.Right,
                    expectedRightDeviceGeneration,
                    expectedRightTransportGeneration))
            {
                return false;
            }
        }
        return true;
    }

    private bool TryApplyLossNoLock(Switch2StickSide side,
        ulong deviceGeneration, ulong transportGeneration)
    {
        Switch2JoyConPairEvent pairEvent = Switch2JoyConPairEvent.HalfLost(
            pairEpoch, side, deviceGeneration, transportGeneration);
        if (!Switch2JoyConJoinedCoordinator.TryProcess(coordinatorState,
                pairEvent, pairPolicy, out var next, out var result))
        {
            lastCoordinatorFailure = result.Failure;
            lastPairRejection = result.PairResult.Rejection;
            lastProfileFailure = result.ProfileFailure;
            lastPairDisposition = result.PairResult.Disposition;
            return false;
        }

        coordinatorState = next;
        lastCoordinatorFailure = result.Failure;
        lastPairRejection = result.PairResult.Rejection;
        lastProfileFailure = result.ProfileFailure;
        lastPairDisposition = result.PairResult.Disposition;
        return true;
    }

    private Switch2RuntimePublicationResult PublishJoinedWithAdmission(
        in Switch2JoyConProfileInputFrame frame)
    {
        long deadline = Environment.TickCount64 +
            runtimeOperationTimeoutMilliseconds;
        while (true)
        {
            Switch2RuntimePublicationResult result = runtimeDevice.
                TryPublishJoinedJoyConDetailed(frame);
            if (result != Switch2RuntimePublicationResult.PublicationBusy)
            {
                return result;
            }

            int remaining = RemainingMilliseconds(deadline);
            if (remaining == 0)
            {
                return Switch2RuntimePublicationResult.PublicationBusy;
            }
            Interlocked.Increment(ref runtimeAdmissionWaitCount);
            runtimeDevice.TryWaitForPublicationAvailability(remaining);
        }
    }

    private Switch2TerminalNeutralRequestResult PublishTerminalNeutral()
    {
        lock (sync)
        {
            terminalPublicationThreadId =
                Environment.CurrentManagedThreadId;
        }
        try
        {
            return runtimeDevice.RequestTerminalNeutral();
        }
        finally
        {
            lock (sync)
            {
                terminalPublicationThreadId = 0;
                Monitor.PulseAll(sync);
            }
        }
    }

    private void RejectCanonicalKind()
    {
        lock (sync)
        {
            lastFailure = Switch2JoyConJoinedRuntimeSinkFailure.
                CanonicalFrameMismatch;
        }
        throw new InvalidOperationException(
            "A joined Joy-Con sink accepts only Joy-Con frames.");
    }

    private Switch2JoyConJoinedRuntimeTerminalCredential
        CreateTerminalCredential() => new(this, terminalFence,
            runtimeGeneration, pairEpoch, expectedLeftDeviceGeneration,
            expectedLeftTransportGeneration,
            expectedRightDeviceGeneration,
            expectedRightTransportGeneration);

    private void SetTerminalFailureNoLock(
        Switch2JoyConJoinedRuntimeSinkFailure failure)
    {
        if (terminalFailure == Switch2JoyConJoinedRuntimeSinkFailure.None)
        {
            terminalFailure = failure;
        }
        lastFailure = failure;
    }

    private static bool ValidateArguments(ulong pairEpoch,
        ulong leftDeviceGeneration, ulong leftTransportGeneration,
        ulong rightDeviceGeneration, ulong rightTransportGeneration,
        Switch2RuntimeInputDevice runtimeDevice,
        int runtimeOperationTimeoutMilliseconds,
        ISwitch2RuntimeTerminalScheduler terminalScheduler,
        out Switch2JoyConJoinedRuntimeSinkFailure failure)
    {
        if (pairEpoch == 0 || leftDeviceGeneration == 0 ||
            leftTransportGeneration == 0 || rightDeviceGeneration == 0 ||
            rightTransportGeneration == 0 || runtimeDevice == null ||
            terminalScheduler == null ||
            runtimeOperationTimeoutMilliseconds <= 0 ||
            runtimeOperationTimeoutMilliseconds >
                InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            failure = Switch2JoyConJoinedRuntimeSinkFailure.InvalidArgument;
            return false;
        }
        failure = Switch2JoyConJoinedRuntimeSinkFailure.None;
        return true;
    }

    private static bool RuntimeMatches(Switch2RuntimeInputDevice runtimeDevice,
        ulong pairEpoch, ulong leftDeviceGeneration,
        ulong leftTransportGeneration, ulong rightDeviceGeneration,
        ulong rightTransportGeneration) => runtimeDevice.RuntimeState ==
            Switch2RuntimeInputDeviceState.Created &&
        runtimeDevice.HasExactJoinedBluetoothBinding(pairEpoch,
            leftDeviceGeneration, leftTransportGeneration,
            rightDeviceGeneration, rightTransportGeneration);

    private static bool IsExactBluetoothDescriptor(
        in Switch2InputSessionDescriptor descriptor,
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration)
    {
        const Switch2GattProperty required = Switch2GattProperty.Read |
            Switch2GattProperty.Notify;
        return descriptor.IsValid && descriptor.Identity.Model == model &&
            descriptor.DeviceGeneration == deviceGeneration &&
            descriptor.TransportGeneration == transportGeneration &&
            descriptor.Identity.Transport == Switch2Transport.BluetoothLe &&
            descriptor.Identity.ProtocolRevision ==
                Switch2InputProtocolRevision.BluetoothLeCommon05V1 &&
            descriptor.Identity.ServiceUuid == Switch2InputCodec.ServiceUuid &&
            descriptor.Identity.CharacteristicUuid ==
                Switch2InputCodec.Common05CharacteristicUuid &&
            descriptor.Identity.GattProperties == required;
    }

    private static Switch2JoyConJoinedRuntimeSinkFailure FailureFor(
        Switch2RuntimePublicationResult result) => result switch
        {
            Switch2RuntimePublicationResult.LifecycleClosed =>
                Switch2JoyConJoinedRuntimeSinkFailure.LifecycleClosed,
            Switch2RuntimePublicationResult.FrameRejected =>
                Switch2JoyConJoinedRuntimeSinkFailure.RuntimeFrameRejected,
            Switch2RuntimePublicationResult.SubscriberRejected =>
                Switch2JoyConJoinedRuntimeSinkFailure.
                    RuntimeSubscriberRejected,
            Switch2RuntimePublicationResult.PublicationBusy =>
                Switch2JoyConJoinedRuntimeSinkFailure.
                    RuntimePublicationAdmissionTimedOut,
            _ => Switch2JoyConJoinedRuntimeSinkFailure.DependencyThrew,
        };

    private static int RemainingMilliseconds(long deadline)
    {
        long remaining = deadline - Environment.TickCount64;
        return remaining <= 0 ? 0 :
            (int)Math.Min(int.MaxValue, remaining);
    }
}
