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

internal enum Switch2BluetoothInputDrainPumpState : byte
{
    Invalid = 0,
    Created,
    Starting,
    Parked,
    Running,
    StopRequested,
    Stopped,
    Quarantined,
}

internal enum Switch2BluetoothInputDrainPumpFailure : byte
{
    None = 0,
    MissingOwner,
    InvalidStartTimeout,
    InvalidStopTimeout,
    OwnerRejected,
    InvalidState,
    WorkerStartRejected,
    WorkerParkTimedOut,
    WorkerExitTimedOut,
    OperationAlreadyInProgress,
    SelfJoinRejected,
    OwnerWaitRejected,
    SinkRejected,
    UnexpectedWorkerFailure,
}

internal enum Switch2BluetoothInputDrainPumpAttentionKind : byte
{
    OwnerRetired = 1,
    WorkerFailure,
    UnexpectedWorkerFailure,
}

/// <summary>
/// Preallocated, exact-generation service wake-up evidence. Receipt is not
/// proof that the drain worker, platform callback, or terminal report has
/// quiesced; the future composition owner must perform its own bounded stop.
/// </summary>
internal sealed class Switch2BluetoothInputDrainPumpAttention
{
    internal Switch2BluetoothInputDrainPumpAttention(
        Switch2BluetoothInputDrainPumpAttentionKind kind,
        ulong deviceGeneration, ulong transportGeneration,
        Switch2BluetoothInputEndReason endReason,
        Switch2BluetoothInputDrainPumpFailure pumpFailure)
    {
        Kind = kind;
        DeviceGeneration = deviceGeneration;
        TransportGeneration = transportGeneration;
        EndReason = endReason;
        PumpFailure = pumpFailure;
    }

    internal Switch2BluetoothInputDrainPumpAttentionKind Kind { get; }

    internal ulong DeviceGeneration { get; }

    internal ulong TransportGeneration { get; }

    internal Switch2BluetoothInputEndReason EndReason { get; }

    internal Switch2BluetoothInputDrainPumpFailure PumpFailure { get; }
}

/// <summary>
/// Completion-driven drain worker for one dormant Bluetooth input owner. The
/// worker reaches a proven start park while the owner is Prepared, then blocks
/// on the owner's monitor until exact owner commit, queued work, or retirement.
/// It has no timer, polling loop, sleep, UI, discovery, registration, reconnect,
/// controller-output, or hardware capability.
/// </summary>
internal sealed class Switch2BluetoothInputDrainPump
{
    internal const int MaximumLifecycleTimeoutMilliseconds =
        InputControllerRegistration.MaximumStopTimeoutMilliseconds;

    private enum StartDecision : byte
    {
        Pending = 0,
        Proceed,
        Abort,
    }

    private readonly object gate = new();
    private readonly object ownerFence = new();
    private readonly Switch2BluetoothInputOwner owner;
    private readonly Thread worker;
    private readonly Action<Thread> workerStarter;
    private readonly Action beforeWorkerPark;
    private readonly Action afterActivation;
    private readonly Switch2BluetoothInputDrainPumpAttention
        disconnectedAttention;
    private readonly Switch2BluetoothInputDrainPumpAttention overflowAttention;
    private readonly Switch2BluetoothInputDrainPumpAttention sinkAttention;
    private readonly Switch2BluetoothInputDrainPumpAttention
        unexpectedAttention;
    private readonly Switch2BluetoothInputDrainPumpAttention[]
        workerFailureAttentions;

    private Switch2BluetoothInputDrainPumpState state =
        Switch2BluetoothInputDrainPumpState.Created;
    private Switch2BluetoothInputDrainPumpFailure terminalFailure;
    private StartDecision startDecision;
    private bool lifecycleOperationInProgress;
    private bool workerStarted;
    private bool workerParked;
    private bool workerExited;
    private bool quarantineRequired;
    private bool lifecycleAttentionRaised;
    private Action<Switch2BluetoothInputDrainPumpAttention>
        lifecycleAttentionHandler;
    private long wakeCount;
    private long drainAttemptCount;
    private long publishedCount;
    private long rejectedCount;
    private long lifecycleAttentionFailureCount;

    private Switch2BluetoothInputDrainPump(
        Switch2BluetoothInputOwner owner, Action<Thread> workerStarter,
        Action beforeWorkerPark, Action afterActivation)
    {
        this.owner = owner;
        this.workerStarter = workerStarter;
        this.beforeWorkerPark = beforeWorkerPark;
        this.afterActivation = afterActivation;
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "DS4Windows Switch 2 Bluetooth input",
            // Match every established physical controller reader in
            // DS4Windows. This worker is completion-driven and blocks on the
            // owner monitor, so the priority applies only while a received
            // report is being decoded and published; it creates no polling or
            // synthetic report cadence.
            Priority = ThreadPriority.AboveNormal,
        };

        ulong deviceGeneration = owner.DeviceGeneration;
        ulong transportGeneration = owner.TransportGeneration;
        disconnectedAttention = new Switch2BluetoothInputDrainPumpAttention(
            Switch2BluetoothInputDrainPumpAttentionKind.OwnerRetired,
            deviceGeneration, transportGeneration,
            Switch2BluetoothInputEndReason.Disconnected, default);
        overflowAttention = new Switch2BluetoothInputDrainPumpAttention(
            Switch2BluetoothInputDrainPumpAttentionKind.OwnerRetired,
            deviceGeneration, transportGeneration,
            Switch2BluetoothInputEndReason.QueueOverflow, default);
        sinkAttention = new Switch2BluetoothInputDrainPumpAttention(
            Switch2BluetoothInputDrainPumpAttentionKind.OwnerRetired,
            deviceGeneration, transportGeneration,
            Switch2BluetoothInputEndReason.SinkFailure,
            Switch2BluetoothInputDrainPumpFailure.SinkRejected);
        unexpectedAttention = new Switch2BluetoothInputDrainPumpAttention(
            Switch2BluetoothInputDrainPumpAttentionKind.
                UnexpectedWorkerFailure,
            deviceGeneration, transportGeneration,
            Switch2BluetoothInputEndReason.Stopped,
            Switch2BluetoothInputDrainPumpFailure.UnexpectedWorkerFailure);
        workerFailureAttentions = new
            Switch2BluetoothInputDrainPumpAttention[
                (int)Switch2BluetoothInputDrainPumpFailure.
                    UnexpectedWorkerFailure + 1];
        for (int index = 1; index < workerFailureAttentions.Length; index++)
        {
            var pumpFailure = (Switch2BluetoothInputDrainPumpFailure)index;
            workerFailureAttentions[index] =
                pumpFailure == Switch2BluetoothInputDrainPumpFailure.
                    UnexpectedWorkerFailure ? unexpectedAttention :
                new Switch2BluetoothInputDrainPumpAttention(
                    Switch2BluetoothInputDrainPumpAttentionKind.WorkerFailure,
                    deviceGeneration, transportGeneration,
                    Switch2BluetoothInputEndReason.Stopped, pumpFailure);
        }
    }

    internal Switch2BluetoothInputDrainPumpState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    internal Switch2BluetoothInputDrainPumpFailure TerminalFailure
    {
        get
        {
            lock (gate)
            {
                return terminalFailure;
            }
        }
    }

    internal bool RequiresQuarantine
    {
        get
        {
            lock (gate)
            {
                return quarantineRequired;
            }
        }
    }

    internal long WakeCount => Interlocked.Read(ref wakeCount);

    internal long DrainAttemptCount => Interlocked.Read(ref drainAttemptCount);

    internal long PublishedCount => Interlocked.Read(ref publishedCount);

    internal long RejectedCount => Interlocked.Read(ref rejectedCount);

    internal long LifecycleAttentionFailureCount =>
        Interlocked.Read(ref lifecycleAttentionFailureCount);

    internal bool IsCurrentWorkerThread =>
        ReferenceEquals(Thread.CurrentThread, worker);

    internal static bool TryCreate(Switch2BluetoothInputOwner owner,
        out Switch2BluetoothInputDrainPump pump,
        out Switch2BluetoothInputDrainPumpFailure failure) => TryCreateCore(
            owner, static thread => thread.Start(), beforeWorkerPark: null,
            afterActivation: null, out pump, out failure);

    internal static bool TryCreateCore(Switch2BluetoothInputOwner owner,
        Action<Thread> workerStarter, Action beforeWorkerPark,
        Action afterActivation,
        out Switch2BluetoothInputDrainPump pump,
        out Switch2BluetoothInputDrainPumpFailure failure)
    {
        pump = null;
        if (owner == null || workerStarter == null)
        {
            failure = Switch2BluetoothInputDrainPumpFailure.MissingOwner;
            return false;
        }

        Switch2BluetoothInputDrainPump candidate;
        try
        {
            candidate = new Switch2BluetoothInputDrainPump(owner,
                workerStarter, beforeWorkerPark, afterActivation);
        }
        catch
        {
            failure = Switch2BluetoothInputDrainPumpFailure.
                WorkerStartRejected;
            return false;
        }
        if (!owner.TryAttachDrainPump(candidate.ownerFence))
        {
            failure = Switch2BluetoothInputDrainPumpFailure.OwnerRejected;
            return false;
        }

        pump = candidate;
        failure = Switch2BluetoothInputDrainPumpFailure.None;
        return true;
    }

    /// <summary>
    /// Installs one control-plane lifecycle wake-up before worker start. The
    /// callback is invoked at most once and always outside owner and pump locks.
    /// </summary>
    internal bool TrySetLifecycleAttentionHandler(
        Action<Switch2BluetoothInputDrainPumpAttention> handler)
    {
        if (handler == null)
        {
            return false;
        }

        lock (gate)
        {
            if (state != Switch2BluetoothInputDrainPumpState.Created ||
                workerStarted || lifecycleAttentionHandler != null)
            {
                return false;
            }
            lifecycleAttentionHandler = handler;
            return true;
        }
    }

    internal bool TryStartParked(int timeoutMilliseconds,
        out Switch2BluetoothInputDrainPumpFailure failure)
    {
        if (timeoutMilliseconds <= 0 || timeoutMilliseconds >
            MaximumLifecycleTimeoutMilliseconds)
        {
            failure = Switch2BluetoothInputDrainPumpFailure.
                InvalidStartTimeout;
            return false;
        }
        if (!owner.IsPrepared)
        {
            failure = Switch2BluetoothInputDrainPumpFailure.InvalidState;
            return false;
        }

        lock (gate)
        {
            if (state != Switch2BluetoothInputDrainPumpState.Created ||
                lifecycleOperationInProgress)
            {
                failure = Switch2BluetoothInputDrainPumpFailure.InvalidState;
                return false;
            }
            lifecycleOperationInProgress = true;
            state = Switch2BluetoothInputDrainPumpState.Starting;
            startDecision = StartDecision.Pending;
            workerStarted = true;
            try
            {
                workerStarter(worker);
            }
            catch
            {
                workerStarted = false;
                workerExited = true;
                state = Switch2BluetoothInputDrainPumpState.Stopped;
                startDecision = StartDecision.Abort;
                lifecycleOperationInProgress = false;
                SetTerminalFailureNoLock(
                    Switch2BluetoothInputDrainPumpFailure.
                        WorkerStartRejected);
                owner.TryRequestDrainPumpStop(ownerFence);
                owner.TryMarkDrainPumpExited(ownerFence);
                failure = Switch2BluetoothInputDrainPumpFailure.
                    WorkerStartRejected;
                return false;
            }
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        bool reachedPark;
        Switch2BluetoothInputDrainPumpFailure observedFailure;
        lock (gate)
        {
            while (!workerParked && !workerExited && state ==
                Switch2BluetoothInputDrainPumpState.Starting)
            {
                int remaining = RemainingMilliseconds(deadline,
                    timeoutMilliseconds);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    break;
                }
            }
            reachedPark = workerParked && !workerExited && state ==
                Switch2BluetoothInputDrainPumpState.Starting;
            observedFailure = terminalFailure;
        }

        bool ownerPrepared = reachedPark &&
            owner.TryMarkDrainPumpPrepared(ownerFence);
        lock (gate)
        {
            bool prepared = ownerPrepared && workerParked && !workerExited &&
                state == Switch2BluetoothInputDrainPumpState.Starting &&
                startDecision == StartDecision.Pending;
            if (prepared)
            {
                state = Switch2BluetoothInputDrainPumpState.Parked;
                startDecision = StartDecision.Proceed;
                lifecycleOperationInProgress = false;
                Monitor.PulseAll(gate);
                failure = Switch2BluetoothInputDrainPumpFailure.None;
                return true;
            }

            startDecision = StartDecision.Abort;
            if (state != Switch2BluetoothInputDrainPumpState.Stopped)
            {
                state = Switch2BluetoothInputDrainPumpState.StopRequested;
            }
            Switch2BluetoothInputDrainPumpFailure startFailure =
                observedFailure != Switch2BluetoothInputDrainPumpFailure.None ?
                    observedFailure :
                    Switch2BluetoothInputDrainPumpFailure.WorkerParkTimedOut;
            SetTerminalFailureNoLock(startFailure);
            quarantineRequired = workerStarted && !workerExited;
            lifecycleOperationInProgress = false;
            Monitor.PulseAll(gate);
            failure = startFailure;
        }
        owner.TryRequestDrainPumpStop(ownerFence);
        return false;
    }

    internal bool TryStopAndJoin(int timeoutMilliseconds,
        out Switch2BluetoothInputDrainPumpFailure failure)
    {
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            MaximumLifecycleTimeoutMilliseconds)
        {
            failure = Switch2BluetoothInputDrainPumpFailure.
                InvalidStopTimeout;
            return false;
        }
        if (owner.IsPrepared)
        {
            failure = Switch2BluetoothInputDrainPumpFailure.InvalidState;
            return false;
        }

        bool waitForWorker;
        bool markExitedWithoutWorker;
        lock (gate)
        {
            if (ReferenceEquals(Thread.CurrentThread, worker))
            {
                SetTerminalFailureNoLock(
                    Switch2BluetoothInputDrainPumpFailure.SelfJoinRejected);
                failure = Switch2BluetoothInputDrainPumpFailure.
                    SelfJoinRejected;
                return false;
            }
            if (lifecycleOperationInProgress)
            {
                failure = Switch2BluetoothInputDrainPumpFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            lifecycleOperationInProgress = true;
            if (!workerStarted)
            {
                workerExited = true;
                state = Switch2BluetoothInputDrainPumpState.Stopped;
            }
            else if (!workerExited)
            {
                state = Switch2BluetoothInputDrainPumpState.StopRequested;
                startDecision = StartDecision.Abort;
                Monitor.PulseAll(gate);
            }
            // A sticky logical exit flag is not operating-system thread
            // termination proof. Join every started worker, including one
            // which has completed its final owner interaction but has not yet
            // returned from Run.
            waitForWorker = workerStarted;
            markExitedWithoutWorker = !workerStarted;
        }

        // Active owner stop preserves its existing serialized terminal callback.
        // A Prepared owner is not generically abortable; its exact credential
        // remains the only authority that can retire it without a clear.
        owner.Stop();
        owner.TryRequestDrainPumpStop(ownerFence);
        if (markExitedWithoutWorker)
        {
            owner.TryMarkDrainPumpExited(ownerFence);
        }

        bool joined = !waitForWorker;
        if (waitForWorker)
        {
            try
            {
                joined = worker.Join(timeoutMilliseconds);
            }
            catch
            {
                joined = false;
            }
        }

        lock (gate)
        {
            lifecycleOperationInProgress = false;
            if (!joined || !workerExited)
            {
                state = Switch2BluetoothInputDrainPumpState.Quarantined;
                quarantineRequired = true;
                SetTerminalFailureNoLock(
                    Switch2BluetoothInputDrainPumpFailure.
                        WorkerExitTimedOut);
                failure = Switch2BluetoothInputDrainPumpFailure.
                    WorkerExitTimedOut;
                return false;
            }
            state = Switch2BluetoothInputDrainPumpState.Stopped;
            quarantineRequired = false;
        }

        failure = Switch2BluetoothInputDrainPumpFailure.None;
        return true;
    }

    private void Run()
    {
        bool activationObserved = false;
        try
        {
            beforeWorkerPark?.Invoke();
            lock (gate)
            {
                workerParked = true;
                Monitor.PulseAll(gate);
                while (startDecision == StartDecision.Pending)
                {
                    Monitor.Wait(gate);
                }
                if (startDecision != StartDecision.Proceed)
                {
                    return;
                }
            }

            while (true)
            {
                Switch2BluetoothInputDrainSignal signal =
                    owner.WaitForDrainSignal(ownerFence, activationObserved);
                Interlocked.Increment(ref wakeCount);
                if (signal == Switch2BluetoothInputDrainSignal.Rejected)
                {
                    lock (gate)
                    {
                        SetTerminalFailureNoLock(
                            Switch2BluetoothInputDrainPumpFailure.
                                OwnerWaitRejected);
                    }
                    break;
                }
                if (signal is Switch2BluetoothInputDrainSignal.Retired or
                    Switch2BluetoothInputDrainSignal.PumpStopRequested)
                {
                    break;
                }
                if (signal == Switch2BluetoothInputDrainSignal.Activated)
                {
                    activationObserved = true;
                    lock (gate)
                    {
                        if (state ==
                            Switch2BluetoothInputDrainPumpState.Parked)
                        {
                            state = Switch2BluetoothInputDrainPumpState.Running;
                        }
                    }
                    afterActivation?.Invoke();
                }

                while (true)
                {
                    Switch2BluetoothInputDrainDisposition disposition =
                        owner.DrainOne();
                    Interlocked.Increment(ref drainAttemptCount);
                    if (disposition ==
                        Switch2BluetoothInputDrainDisposition.Published)
                    {
                        Interlocked.Increment(ref publishedCount);
                        continue;
                    }
                    if (disposition ==
                        Switch2BluetoothInputDrainDisposition.Rejected)
                    {
                        Interlocked.Increment(ref rejectedCount);
                        if (owner.EndReason ==
                            Switch2BluetoothInputEndReason.SinkFailure)
                        {
                            lock (gate)
                            {
                                SetTerminalFailureNoLock(
                                    Switch2BluetoothInputDrainPumpFailure.
                                        SinkRejected);
                            }
                        }
                        continue;
                    }
                    break;
                }
            }
        }
        catch
        {
            lock (gate)
            {
                SetTerminalFailureNoLock(
                    Switch2BluetoothInputDrainPumpFailure.
                        UnexpectedWorkerFailure);
            }
        }
        finally
        {
            bool wasActive = owner.ActivationCommitted;
            owner.Stop();
            Action<Switch2BluetoothInputDrainPumpAttention> attention = null;
            Switch2BluetoothInputDrainPumpAttention evidence = null;
            lock (gate)
            {
                workerParked = false;
                if (wasActive && !lifecycleAttentionRaised &&
                    lifecycleAttentionHandler != null)
                {
                    evidence = AttentionForNoLock(owner.EndReason);
                    if (evidence != null)
                    {
                        lifecycleAttentionRaised = true;
                        attention = lifecycleAttentionHandler;
                    }
                }
            }

            try
            {
                attention?.Invoke(evidence);
            }
            catch
            {
                // Attention is advisory and never teardown proof. Sticky
                // generation/failure evidence and callback-failure count remain
                // queryable without retrying a second logical wake-up.
                Interlocked.Increment(ref lifecycleAttentionFailureCount);
            }

            // The advisory callback is part of the worker lifetime. Do not
            // publish either the owner-side exit fence or the pump's logical
            // exit until it has returned. A control thread still performs an
            // actual Thread.Join before it may claim bounded teardown proof.
            owner.TryMarkDrainPumpExited(ownerFence);
            lock (gate)
            {
                workerExited = true;
                // A prior bounded wait which timed out stays quarantined until
                // a control thread performs an actual successful Thread.Join.
                // The worker cannot prove its own operating-system exit.
                state = quarantineRequired ?
                    Switch2BluetoothInputDrainPumpState.Quarantined :
                    Switch2BluetoothInputDrainPumpState.Stopped;
                Monitor.PulseAll(gate);
            }
        }
    }

    private Switch2BluetoothInputDrainPumpAttention AttentionForNoLock(
        Switch2BluetoothInputEndReason reason)
    {
        Switch2BluetoothInputDrainPumpAttention retirement = reason switch
        {
            Switch2BluetoothInputEndReason.Disconnected =>
                disconnectedAttention,
            Switch2BluetoothInputEndReason.QueueOverflow => overflowAttention,
            Switch2BluetoothInputEndReason.SinkFailure => sinkAttention,
            _ => null,
        };
        if (retirement != null)
        {
            return retirement;
        }
        int failureIndex = (int)terminalFailure;
        return failureIndex > 0 &&
            failureIndex < workerFailureAttentions.Length ?
                workerFailureAttentions[failureIndex] : null;
    }

    private void SetTerminalFailureNoLock(
        Switch2BluetoothInputDrainPumpFailure failure)
    {
        if (terminalFailure == Switch2BluetoothInputDrainPumpFailure.None)
        {
            terminalFailure = failure;
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
        return remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, remaining);
    }
}
