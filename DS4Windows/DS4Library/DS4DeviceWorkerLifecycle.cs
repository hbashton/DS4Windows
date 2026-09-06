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
using DS4Windows.InputDevices;

namespace DS4Windows;

internal enum DS4DeviceWorkerLifecycleSupport : byte
{
    Invalid = 0,
    SupportedLegacyHid,
    UnsupportedDualSenseCompositeWorkers,
    UnsupportedSwitchOperationalSetup,
    UnsupportedNoHidExternalOwner,
    UnsupportedUnknownSubtype,
}

internal enum DS4DeviceWorkerLifecycleState : byte
{
    Created = 0,
    Starting,
    Started,
    StartUncertain,
    Stopping,
    StopUncertain,
    Stopped,
    QuarantinedUntracked,
}

internal enum DS4DeviceWorkerLifecycleOperation : byte
{
    Invalid = 0,
    Start,
    Stop,
}

internal enum DS4DeviceWorkerLifecycleOutcome : byte
{
    Invalid = 0,
    Succeeded,
    CleanRejected,
    OutcomeUncertain,
}

internal enum DS4DeviceWorkerLifecycleFailureKind : byte
{
    None = 0,
    InvalidArgument,
    UnsupportedDevice,
    GenerationExhausted,
    StaleCredential,
    WrongState,
    Busy,
    ReentrantCall,
    ConcurrentInterference,
    UntrackedExistingWorker,
    StartRejected,
    StartDependencyThrew,
    PartialStart,
    StopTimedOut,
    StopDependencyThrew,
    OutputFinalizationUncertain,
}

internal readonly struct DS4DeviceWorkerLifecycleResult
{
    private DS4DeviceWorkerLifecycleResult(
        DS4DeviceWorkerLifecycleOperation operation,
        DS4DeviceWorkerLifecycleOutcome outcome,
        DS4DeviceWorkerLifecycleFailureKind failureKind)
    {
        Operation = operation;
        Outcome = outcome;
        FailureKind = failureKind;
    }

    internal DS4DeviceWorkerLifecycleOperation Operation { get; }

    internal DS4DeviceWorkerLifecycleOutcome Outcome { get; }

    internal DS4DeviceWorkerLifecycleFailureKind FailureKind { get; }

    internal bool IsValid => IsDefined(Operation) && IsDefined(Outcome) &&
        IsDefined(FailureKind) && IsAllowedShape(Operation, Outcome,
            FailureKind);

    internal bool Succeeded => IsValid && Outcome ==
        DS4DeviceWorkerLifecycleOutcome.Succeeded;

    internal bool RequiresQuarantine => !IsValid || Outcome ==
        DS4DeviceWorkerLifecycleOutcome.OutcomeUncertain;

    internal static DS4DeviceWorkerLifecycleResult Success(
        DS4DeviceWorkerLifecycleOperation operation)
    {
        if (!IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
        return new DS4DeviceWorkerLifecycleResult(operation,
            DS4DeviceWorkerLifecycleOutcome.Succeeded,
            DS4DeviceWorkerLifecycleFailureKind.None);
    }

    internal static DS4DeviceWorkerLifecycleResult Reject(
        DS4DeviceWorkerLifecycleOperation operation,
        DS4DeviceWorkerLifecycleFailureKind failureKind) => CreateFailure(
            operation, DS4DeviceWorkerLifecycleOutcome.CleanRejected,
            failureKind);

    internal static DS4DeviceWorkerLifecycleResult Uncertain(
        DS4DeviceWorkerLifecycleOperation operation,
        DS4DeviceWorkerLifecycleFailureKind failureKind) => CreateFailure(
            operation, DS4DeviceWorkerLifecycleOutcome.OutcomeUncertain,
            failureKind);

    private static DS4DeviceWorkerLifecycleResult CreateFailure(
        DS4DeviceWorkerLifecycleOperation operation,
        DS4DeviceWorkerLifecycleOutcome outcome,
        DS4DeviceWorkerLifecycleFailureKind failureKind)
    {
        if (!IsDefined(operation) || !IsDefined(outcome) ||
            !IsDefined(failureKind) || !IsAllowedShape(operation, outcome,
                failureKind))
        {
            throw new ArgumentException(
                "The device worker lifecycle result is malformed.");
        }
        return new DS4DeviceWorkerLifecycleResult(operation, outcome,
            failureKind);
    }

    private static bool IsAllowedShape(
        DS4DeviceWorkerLifecycleOperation operation,
        DS4DeviceWorkerLifecycleOutcome outcome,
        DS4DeviceWorkerLifecycleFailureKind failureKind)
    {
        if (outcome == DS4DeviceWorkerLifecycleOutcome.Succeeded)
        {
            return failureKind == DS4DeviceWorkerLifecycleFailureKind.None;
        }
        if (failureKind == DS4DeviceWorkerLifecycleFailureKind.None)
        {
            return false;
        }
        if (outcome == DS4DeviceWorkerLifecycleOutcome.OutcomeUncertain)
        {
            return failureKind is
                    DS4DeviceWorkerLifecycleFailureKind.ReentrantCall or
                    DS4DeviceWorkerLifecycleFailureKind.
                        ConcurrentInterference ||
                operation == DS4DeviceWorkerLifecycleOperation.Start &&
                    failureKind is
                        DS4DeviceWorkerLifecycleFailureKind.
                            UntrackedExistingWorker or
                        DS4DeviceWorkerLifecycleFailureKind.
                            StartDependencyThrew or
                        DS4DeviceWorkerLifecycleFailureKind.PartialStart ||
                operation == DS4DeviceWorkerLifecycleOperation.Stop &&
                    failureKind is
                        DS4DeviceWorkerLifecycleFailureKind.StopTimedOut or
                        DS4DeviceWorkerLifecycleFailureKind.
                            StopDependencyThrew or
                        DS4DeviceWorkerLifecycleFailureKind.
                            OutputFinalizationUncertain;
        }
        return outcome == DS4DeviceWorkerLifecycleOutcome.CleanRejected &&
            (failureKind is
                    DS4DeviceWorkerLifecycleFailureKind.InvalidArgument or
                    DS4DeviceWorkerLifecycleFailureKind.UnsupportedDevice or
                    DS4DeviceWorkerLifecycleFailureKind.WrongState or
                    DS4DeviceWorkerLifecycleFailureKind.Busy ||
                operation == DS4DeviceWorkerLifecycleOperation.Start &&
                    failureKind is
                        DS4DeviceWorkerLifecycleFailureKind.
                            GenerationExhausted or
                        DS4DeviceWorkerLifecycleFailureKind.StartRejected ||
                operation == DS4DeviceWorkerLifecycleOperation.Stop &&
                    failureKind ==
                        DS4DeviceWorkerLifecycleFailureKind.StaleCredential);
    }

    private static bool IsDefined(
        DS4DeviceWorkerLifecycleOperation value) => value is
            DS4DeviceWorkerLifecycleOperation.Start or
            DS4DeviceWorkerLifecycleOperation.Stop;

    private static bool IsDefined(
        DS4DeviceWorkerLifecycleOutcome value) => value is
            DS4DeviceWorkerLifecycleOutcome.Succeeded or
            DS4DeviceWorkerLifecycleOutcome.CleanRejected or
            DS4DeviceWorkerLifecycleOutcome.OutcomeUncertain;

    private static bool IsDefined(
        DS4DeviceWorkerLifecycleFailureKind value) => value is >=
            DS4DeviceWorkerLifecycleFailureKind.None and <=
            DS4DeviceWorkerLifecycleFailureKind.OutputFinalizationUncertain;
}

internal readonly struct DS4DeviceWorkerLifecycleLease :
    IEquatable<DS4DeviceWorkerLifecycleLease>
{
    private readonly object issuer;

    internal DS4DeviceWorkerLifecycleLease(object issuer, DS4Device device,
        ulong generation)
    {
        this.issuer = issuer;
        Device = device;
        Generation = generation;
    }

    internal DS4Device Device { get; }

    internal ulong Generation { get; }

    internal bool IsValid => issuer != null && Device != null &&
        Generation != 0;

    internal object Issuer => issuer;

    public bool Equals(DS4DeviceWorkerLifecycleLease other) =>
        ReferenceEquals(issuer, other.issuer) &&
        ReferenceEquals(Device, other.Device) && Generation == other.Generation;

    public override bool Equals(object obj) => obj is
        DS4DeviceWorkerLifecycleLease other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        issuer == null ? 0 : RuntimeHelpers.GetHashCode(issuer),
        Device == null ? 0 : RuntimeHelpers.GetHashCode(Device), Generation);

    public static bool operator ==(DS4DeviceWorkerLifecycleLease left,
        DS4DeviceWorkerLifecycleLease right) => left.Equals(right);

    public static bool operator !=(DS4DeviceWorkerLifecycleLease left,
        DS4DeviceWorkerLifecycleLease right) => !left.Equals(right);
}

internal static class DS4DeviceWorkerLifecycleSupportPolicy
{
    internal static DS4DeviceWorkerLifecycleSupport Classify(Type deviceType,
        bool hasHidInterface)
    {
        if (deviceType == null)
        {
            return DS4DeviceWorkerLifecycleSupport.Invalid;
        }
        if (!hasHidInterface)
        {
            return DS4DeviceWorkerLifecycleSupport.
                UnsupportedNoHidExternalOwner;
        }
        if (deviceType == typeof(DualSenseDevice))
        {
            return DS4DeviceWorkerLifecycleSupport.
                UnsupportedDualSenseCompositeWorkers;
        }
        if (deviceType == typeof(SwitchProDevice) ||
            deviceType == typeof(JoyConDevice))
        {
            return DS4DeviceWorkerLifecycleSupport.
                UnsupportedSwitchOperationalSetup;
        }
        if (deviceType == typeof(DS4Device) ||
            deviceType == typeof(DS3Device))
        {
            return DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid;
        }
        return DS4DeviceWorkerLifecycleSupport.UnsupportedUnknownSubtype;
    }
}

/// <summary>
/// Serializes one opt-in worker lifetime without owning a HID operation. Start
/// and stop callbacks execute outside this boundary's gate. Worker-start
/// commits are witnessed at the owner immediately after Thread.Start returns.
/// An uncertain start retains and returns its exact cleanup lease; a new
/// generation can never overtake uncertain worker ownership.
/// </summary>
internal sealed class DS4DeviceWorkerLifecycleBoundary
{
    private readonly object gate = new();
    private readonly object issuer = new();

    private DS4DeviceWorkerLifecycleState state;
    private ulong lastGeneration;
    private DS4DeviceWorkerLifecycleLease currentLease;
    private int operationOwnerThreadId;
    private bool transitionViolated;
    private DS4DeviceWorkerLifecycleFailureKind transitionViolationKind;
    private bool untrackedWorkerObserved;
    private bool foreignWorkerObservedDuringStart;
    private ulong inputWorkerGeneration;
    private ulong outputWorkerGeneration;
    private int witnessRecordingFailed;

    internal DS4DeviceWorkerLifecycleState State
    {
        get { lock (gate) { return state; } }
    }

    internal DS4DeviceWorkerLifecycleLease CurrentLease
    {
        get { lock (gate) { return currentLease; } }
    }

    internal object Gate => gate;

    internal void WitnessWorkerStartCommit(DS4Device device, bool inputWorker)
    {
        // This witness is deliberately non-throwing: it follows an existing
        // public Thread.Start call and therefore cannot alter that call's
        // exception behavior.
        try
        {
            int threadId = Environment.CurrentManagedThreadId;
            lock (gate)
            {
                if (state == DS4DeviceWorkerLifecycleState.Starting &&
                    ReferenceEquals(currentLease.Device, device))
                {
                    if (operationOwnerThreadId == threadId)
                    {
                        if (inputWorker)
                        {
                            inputWorkerGeneration = currentLease.Generation;
                        }
                        else
                        {
                            outputWorkerGeneration = currentLease.Generation;
                        }
                    }
                    else
                    {
                        foreignWorkerObservedDuringStart = true;
                    }
                    return;
                }

                untrackedWorkerObserved = true;
                if (state == DS4DeviceWorkerLifecycleState.Created)
                {
                    state = DS4DeviceWorkerLifecycleState.
                        QuarantinedUntracked;
                }
            }
        }
        catch
        {
            // Failing to record an optional dormant witness must not change
            // the legacy public StartUpdate contract. The typed path will not
            // observe its exact generation and therefore cannot claim it.
            Volatile.Write(ref witnessRecordingFailed, 1);
        }
    }

    internal bool TryStart(DS4Device device,
        DS4DeviceWorkerLifecycleSupport support,
        Action start,
        out DS4DeviceWorkerLifecycleLease lease,
        out DS4DeviceWorkerLifecycleResult result)
    {
        lease = default;
        if (device == null || start == null)
        {
            result = DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Start,
                DS4DeviceWorkerLifecycleFailureKind.InvalidArgument);
            return false;
        }
        if (support !=
            DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid)
        {
            result = DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Start,
                DS4DeviceWorkerLifecycleFailureKind.UnsupportedDevice);
            return false;
        }

        int threadId = Environment.CurrentManagedThreadId;
        lock (gate)
        {
            if (state == DS4DeviceWorkerLifecycleState.Started)
            {
                lease = currentLease;
                result = DS4DeviceWorkerLifecycleResult.Success(
                    DS4DeviceWorkerLifecycleOperation.Start);
                return true;
            }
            if (state is DS4DeviceWorkerLifecycleState.Starting or
                DS4DeviceWorkerLifecycleState.Stopping)
            {
                transitionViolated = true;
                bool reentrant = operationOwnerThreadId == threadId;
                RecordTransitionViolationNoLock(reentrant);
                result = reentrant ?
                    DS4DeviceWorkerLifecycleResult.Uncertain(
                        DS4DeviceWorkerLifecycleOperation.Start,
                        DS4DeviceWorkerLifecycleFailureKind.ReentrantCall) :
                    DS4DeviceWorkerLifecycleResult.Reject(
                        DS4DeviceWorkerLifecycleOperation.Start,
                        DS4DeviceWorkerLifecycleFailureKind.Busy);
                if (reentrant)
                {
                    lease = currentLease;
                }
                return false;
            }
            if (state ==
                DS4DeviceWorkerLifecycleState.QuarantinedUntracked ||
                untrackedWorkerObserved ||
                Volatile.Read(ref witnessRecordingFailed) != 0)
            {
                state = DS4DeviceWorkerLifecycleState.QuarantinedUntracked;
                result = DS4DeviceWorkerLifecycleResult.Uncertain(
                    DS4DeviceWorkerLifecycleOperation.Start,
                    DS4DeviceWorkerLifecycleFailureKind.
                        UntrackedExistingWorker);
                return false;
            }
            if (state != DS4DeviceWorkerLifecycleState.Created)
            {
                lease = currentLease;
                result = DS4DeviceWorkerLifecycleResult.Reject(
                    DS4DeviceWorkerLifecycleOperation.Start,
                    DS4DeviceWorkerLifecycleFailureKind.WrongState);
                return false;
            }

            if (lastGeneration == ulong.MaxValue)
            {
                result = DS4DeviceWorkerLifecycleResult.Reject(
                    DS4DeviceWorkerLifecycleOperation.Start,
                    DS4DeviceWorkerLifecycleFailureKind.GenerationExhausted);
                return false;
            }

            ulong generation = ++lastGeneration;
            currentLease = new DS4DeviceWorkerLifecycleLease(issuer, device,
                generation);
            lease = currentLease;
            state = DS4DeviceWorkerLifecycleState.Starting;
            operationOwnerThreadId = threadId;
            transitionViolated = false;
            transitionViolationKind =
                DS4DeviceWorkerLifecycleFailureKind.None;
            foreignWorkerObservedDuringStart = false;
            inputWorkerGeneration = 0;
            outputWorkerGeneration = 0;
        }

        Exception dependencyException = null;
        try
        {
            start();
        }
        catch (Exception ex)
        {
            dependencyException = ex;
        }

        lock (gate)
        {
            operationOwnerThreadId = 0;
            bool inputCommitted = inputWorkerGeneration ==
                currentLease.Generation;
            bool anyCommitted = inputCommitted || outputWorkerGeneration ==
                currentLease.Generation || foreignWorkerObservedDuringStart ||
                Volatile.Read(ref witnessRecordingFailed) != 0;
            if (transitionViolated)
            {
                state = DS4DeviceWorkerLifecycleState.StartUncertain;
                result = DS4DeviceWorkerLifecycleResult.Uncertain(
                    DS4DeviceWorkerLifecycleOperation.Start,
                    transitionViolationKind);
                return false;
            }
            if (foreignWorkerObservedDuringStart ||
                Volatile.Read(ref witnessRecordingFailed) != 0)
            {
                state = DS4DeviceWorkerLifecycleState.StartUncertain;
                result = DS4DeviceWorkerLifecycleResult.Uncertain(
                    DS4DeviceWorkerLifecycleOperation.Start,
                    DS4DeviceWorkerLifecycleFailureKind.
                        UntrackedExistingWorker);
                return false;
            }
            if (dependencyException != null)
            {
                state = DS4DeviceWorkerLifecycleState.StartUncertain;
                result = DS4DeviceWorkerLifecycleResult.Uncertain(
                    DS4DeviceWorkerLifecycleOperation.Start,
                    anyCommitted ?
                        DS4DeviceWorkerLifecycleFailureKind.PartialStart :
                        DS4DeviceWorkerLifecycleFailureKind.
                            StartDependencyThrew);
                return false;
            }
            if (!inputCommitted)
            {
                if (anyCommitted)
                {
                    state = DS4DeviceWorkerLifecycleState.StartUncertain;
                    result = DS4DeviceWorkerLifecycleResult.Uncertain(
                        DS4DeviceWorkerLifecycleOperation.Start,
                        DS4DeviceWorkerLifecycleFailureKind.PartialStart);
                    return false;
                }

                state = DS4DeviceWorkerLifecycleState.Created;
                currentLease = default;
                lease = default;
                result = DS4DeviceWorkerLifecycleResult.Reject(
                    DS4DeviceWorkerLifecycleOperation.Start,
                    DS4DeviceWorkerLifecycleFailureKind.StartRejected);
                return false;
            }

            state = DS4DeviceWorkerLifecycleState.Started;
            result = DS4DeviceWorkerLifecycleResult.Success(
                DS4DeviceWorkerLifecycleOperation.Start);
            return true;
        }
    }

    internal bool TryStop(DS4Device device,
        DS4DeviceWorkerLifecycleSupport support,
        in DS4DeviceWorkerLifecycleLease lease, int timeoutMilliseconds,
        Func<int, DS4DeviceWorkerLifecycleResult> stop,
        out DS4DeviceWorkerLifecycleResult result)
    {
        if (device == null || stop == null || timeoutMilliseconds < 0 ||
            timeoutMilliseconds >
                InputControllerRegistration.MaximumStopTimeoutMilliseconds)
        {
            result = DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Stop,
                DS4DeviceWorkerLifecycleFailureKind.InvalidArgument);
            return false;
        }
        if (support !=
            DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid)
        {
            result = DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Stop,
                DS4DeviceWorkerLifecycleFailureKind.UnsupportedDevice);
            return false;
        }

        int threadId = Environment.CurrentManagedThreadId;
        DS4DeviceWorkerLifecycleState priorState;
        lock (gate)
        {
            if (!lease.IsValid || !ReferenceEquals(lease.Issuer, issuer) ||
                !ReferenceEquals(lease.Device, device) ||
                lease != currentLease)
            {
                result = DS4DeviceWorkerLifecycleResult.Reject(
                    DS4DeviceWorkerLifecycleOperation.Stop,
                    DS4DeviceWorkerLifecycleFailureKind.StaleCredential);
                return false;
            }
            if (state == DS4DeviceWorkerLifecycleState.Stopped)
            {
                result = DS4DeviceWorkerLifecycleResult.Success(
                    DS4DeviceWorkerLifecycleOperation.Stop);
                return true;
            }
            if (state is DS4DeviceWorkerLifecycleState.Starting or
                DS4DeviceWorkerLifecycleState.Stopping)
            {
                transitionViolated = true;
                bool reentrant = operationOwnerThreadId == threadId;
                RecordTransitionViolationNoLock(reentrant);
                result = reentrant ?
                    DS4DeviceWorkerLifecycleResult.Uncertain(
                        DS4DeviceWorkerLifecycleOperation.Stop,
                        DS4DeviceWorkerLifecycleFailureKind.ReentrantCall) :
                    DS4DeviceWorkerLifecycleResult.Reject(
                        DS4DeviceWorkerLifecycleOperation.Stop,
                        DS4DeviceWorkerLifecycleFailureKind.Busy);
                return false;
            }
            if (state is not (DS4DeviceWorkerLifecycleState.Started or
                    DS4DeviceWorkerLifecycleState.StartUncertain or
                    DS4DeviceWorkerLifecycleState.StopUncertain))
            {
                result = DS4DeviceWorkerLifecycleResult.Reject(
                    DS4DeviceWorkerLifecycleOperation.Stop,
                    DS4DeviceWorkerLifecycleFailureKind.WrongState);
                return false;
            }

            priorState = state;
            state = DS4DeviceWorkerLifecycleState.Stopping;
            operationOwnerThreadId = threadId;
            transitionViolated = false;
            transitionViolationKind =
                DS4DeviceWorkerLifecycleFailureKind.None;
        }

        DS4DeviceWorkerLifecycleResult stopResult;
        try
        {
            stopResult = stop(timeoutMilliseconds);
        }
        catch
        {
            stopResult = DS4DeviceWorkerLifecycleResult.Uncertain(
                DS4DeviceWorkerLifecycleOperation.Stop,
                DS4DeviceWorkerLifecycleFailureKind.StopDependencyThrew);
        }
        if (!stopResult.IsValid || stopResult.Operation !=
            DS4DeviceWorkerLifecycleOperation.Stop)
        {
            stopResult = DS4DeviceWorkerLifecycleResult.Uncertain(
                DS4DeviceWorkerLifecycleOperation.Stop,
                DS4DeviceWorkerLifecycleFailureKind.StopDependencyThrew);
        }

        lock (gate)
        {
            operationOwnerThreadId = 0;
            if (transitionViolated)
            {
                state = DS4DeviceWorkerLifecycleState.StopUncertain;
                result = DS4DeviceWorkerLifecycleResult.Uncertain(
                    DS4DeviceWorkerLifecycleOperation.Stop,
                    transitionViolationKind);
                return false;
            }
            if (stopResult.Succeeded)
            {
                state = DS4DeviceWorkerLifecycleState.Stopped;
                result = stopResult;
                return true;
            }
            if (stopResult.RequiresQuarantine)
            {
                state = DS4DeviceWorkerLifecycleState.StopUncertain;
            }
            else
            {
                state = priorState;
            }
            result = stopResult;
            return false;
        }
    }

    private void RecordTransitionViolationNoLock(bool reentrant)
    {
        if (reentrant || transitionViolationKind ==
                DS4DeviceWorkerLifecycleFailureKind.None)
        {
            transitionViolationKind = reentrant ?
                DS4DeviceWorkerLifecycleFailureKind.ReentrantCall :
                DS4DeviceWorkerLifecycleFailureKind.ConcurrentInterference;
        }
    }
}
