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

public enum Switch2ProUsbInputReadPumpState : byte
{
    Invalid = 0,
    Created,
    Running,
    StopRequested,
    Stopped,
    Disposed,
    Preparing,
    Prepared,
}

public enum Switch2ProUsbInputReadPumpFailure : byte
{
    None = 0,
    MissingOwner,
    InvalidReadRetirementTimeout,
    InvalidStopTimeout,
    OwnerRejected,
    InvalidState,
    WorkerStartRejected,
    ReadStartRejected,
    ReadRetirementRejected,
    WorkerExitTimedOut,
    OwnerDisposeRejected,
    OperationAlreadyInProgress,
    UnexpectedWorkerFailure,
    ReadCompletionRejected,
    WorkerParkTimedOut,
    ActivationCredentialRejected,
    InvalidCommitTimeout,
}

internal enum Switch2ProUsbInputReadPumpActivationDecision : byte
{
    Pending = 0,
    Commit,
    Abort,
}

/// <summary>
/// Completion-driven owner of one dormant Switch 2 Pro USB input transport.
/// It issues one read, blocks on retirement of that exact native submission,
/// and immediately issues the next read. There is no timer, polling interval,
/// sleep, discovery, logging, UI call, or output capability in the loop.
/// </summary>
public sealed class Switch2ProUsbInputReadPump
{
    private readonly object gate = new();
    private readonly object ownerFence = new();
    private readonly Switch2ProUsbInputTransportOwner owner;
    private readonly int readRetirementTimeoutMilliseconds;
    private readonly Thread worker;
    private readonly Action<Thread> workerStarter;
    private readonly Action beforeWorkerPark;
    private readonly object oneShotActivationFence = new();

    private Switch2ProUsbInputReadPumpState state =
        Switch2ProUsbInputReadPumpState.Created;
    private Switch2ProUsbInputReadPumpFailure terminalFailure;
    private Switch2ProUsbReadBeginFailure lastReadBeginFailure;
    private Switch2ProUsbReadRetirementFailure lastRetirementFailure;
    private Switch2ProUsbReadRetirementResult lastRetirementResult;
    private Switch2ProUsbDisposeFailure lastDisposeFailure;
    private bool workerStarted;
    private bool workerExited;
    private bool lifecycleOperationInProgress;
    private bool workerParked;
    private bool lifecycleAttentionRaised;
    private Action<Switch2ProUsbInputReadPumpFailure>
        lifecycleAttentionHandler;
    private object preparedActivationFence;
    private Switch2ProUsbInputReadPumpActivationDecision activationDecision;
    private long startedReadCount;
    private long retiredReadCount;

    private Switch2ProUsbInputReadPump(
        Switch2ProUsbInputTransportOwner owner,
        int readRetirementTimeoutMilliseconds, Action<Thread> workerStarter,
        Action beforeWorkerPark)
    {
        this.owner = owner;
        this.readRetirementTimeoutMilliseconds =
            readRetirementTimeoutMilliseconds;
        this.workerStarter = workerStarter;
        this.beforeWorkerPark = beforeWorkerPark;
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "DS4Windows Switch 2 Pro USB input",
        };
    }

    public Switch2ProUsbInputReadPumpState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public Switch2ProUsbInputReadPumpFailure TerminalFailure
    {
        get
        {
            lock (gate)
            {
                return terminalFailure;
            }
        }
    }

    public Switch2ProUsbReadBeginFailure LastReadBeginFailure
    {
        get
        {
            lock (gate)
            {
                return lastReadBeginFailure;
            }
        }
    }

    public Switch2ProUsbReadRetirementFailure LastRetirementFailure
    {
        get
        {
            lock (gate)
            {
                return lastRetirementFailure;
            }
        }
    }

    public Switch2ProUsbReadRetirementResult LastRetirementResult
    {
        get
        {
            lock (gate)
            {
                return lastRetirementResult;
            }
        }
    }

    public Switch2ProUsbDisposeFailure LastDisposeFailure
    {
        get
        {
            lock (gate)
            {
                return lastDisposeFailure;
            }
        }
    }

    public long StartedReadCount => Interlocked.Read(ref startedReadCount);

    public long RetiredReadCount => Interlocked.Read(ref retiredReadCount);

    /// <summary>
    /// Installs the composition owner's one exact terminal-failure callback.
    /// It is a control-plane hook, never a per-report callback. Replacement is
    /// permitted only before the worker starts.
    /// </summary>
    internal bool TrySetLifecycleAttentionHandler(
        Action<Switch2ProUsbInputReadPumpFailure> handler)
    {
        if (handler == null)
        {
            return false;
        }

        lock (gate)
        {
            if (workerStarted || lifecycleAttentionHandler != null)
            {
                return false;
            }
            lifecycleAttentionHandler = handler;
            return true;
        }
    }

    public static bool TryCreate(Switch2ProUsbInputTransportOwner owner,
        int readRetirementTimeoutMilliseconds,
        out Switch2ProUsbInputReadPump pump,
        out Switch2ProUsbInputReadPumpFailure failure)
        => TryCreateCore(owner, readRetirementTimeoutMilliseconds,
            static thread => thread.Start(), beforeWorkerPark: null,
            out pump, out failure);

    internal static bool TryCreateCore(
        Switch2ProUsbInputTransportOwner owner,
        int readRetirementTimeoutMilliseconds, Action<Thread> workerStarter,
        Action beforeWorkerPark, out Switch2ProUsbInputReadPump pump,
        out Switch2ProUsbInputReadPumpFailure failure)
    {
        pump = null;
        if (owner == null || workerStarter == null)
        {
            failure = Switch2ProUsbInputReadPumpFailure.MissingOwner;
            return false;
        }
        if (readRetirementTimeoutMilliseconds <= 0 ||
            readRetirementTimeoutMilliseconds >
                Switch2ProUsbInputTransportOwner.
                    MaximumDisposeTimeoutMilliseconds)
        {
            failure = Switch2ProUsbInputReadPumpFailure.
                InvalidReadRetirementTimeout;
            return false;
        }

        Switch2ProUsbInputReadPump candidate;
        try
        {
            candidate = new Switch2ProUsbInputReadPump(owner,
                readRetirementTimeoutMilliseconds, workerStarter,
                beforeWorkerPark);
        }
        catch
        {
            failure = Switch2ProUsbInputReadPumpFailure.
                WorkerStartRejected;
            return false;
        }
        if (!owner.TryAttachContinuousPump(candidate.ownerFence))
        {
            failure = Switch2ProUsbInputReadPumpFailure.OwnerRejected;
            return false;
        }

        pump = candidate;
        failure = Switch2ProUsbInputReadPumpFailure.None;
        return true;
    }

    public bool TryStart(out Switch2ProUsbInputReadPumpFailure failure)
    {
        if (!TryPrepareStart(oneShotActivationFence,
                Switch2ProUsbInputTransportOwner.
                    MaximumDisposeTimeoutMilliseconds, out failure))
        {
            return false;
        }
        if (TryCommitPrepared(oneShotActivationFence, out failure))
        {
            return true;
        }

        // This convenience API owns its private credential, so a rejected
        // commit must not strand a caller behind an unreachable parked fence.
        if (!TryAbortPrepared(oneShotActivationFence,
                Switch2ProUsbInputTransportOwner.
                    MaximumDisposeTimeoutMilliseconds,
                out Switch2ProUsbInputReadPumpFailure cleanupFailure))
        {
            failure = cleanupFailure;
        }
        return false;
    }

    internal bool TryPrepareStart(object activationFence,
        int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure)
    {
        if (activationFence == null)
        {
            failure = Switch2ProUsbInputReadPumpFailure.
                ActivationCredentialRejected;
            return false;
        }
        if (timeoutMilliseconds <= 0 || timeoutMilliseconds >
            Switch2ProUsbInputTransportOwner.
                MaximumDisposeTimeoutMilliseconds)
        {
            failure = Switch2ProUsbInputReadPumpFailure.InvalidStopTimeout;
            return false;
        }

        lock (gate)
        {
            if (state != Switch2ProUsbInputReadPumpState.Created ||
                lifecycleOperationInProgress)
            {
                failure = Switch2ProUsbInputReadPumpFailure.InvalidState;
                return false;
            }
            lifecycleOperationInProgress = true;
            state = Switch2ProUsbInputReadPumpState.Preparing;
            preparedActivationFence = activationFence;
            activationDecision =
                Switch2ProUsbInputReadPumpActivationDecision.Pending;
            workerStarted = true;
            try
            {
                workerStarter(worker);
            }
            catch
            {
                workerStarted = false;
                state = Switch2ProUsbInputReadPumpState.Stopped;
                preparedActivationFence = null;
                activationDecision =
                    Switch2ProUsbInputReadPumpActivationDecision.Abort;
                lifecycleOperationInProgress = false;
                SetTerminalFailureNoLock(
                    Switch2ProUsbInputReadPumpFailure.WorkerStartRejected);
                owner.RequestStop();
                failure = Switch2ProUsbInputReadPumpFailure.
                    WorkerStartRejected;
                return false;
            }
        }

        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        bool prepared;
        lock (gate)
        {
            while (!workerParked && !workerExited && state ==
                Switch2ProUsbInputReadPumpState.Preparing)
            {
                int remaining = RemainingMilliseconds(deadline,
                    timeoutMilliseconds);
                if (remaining == 0 || !Monitor.Wait(gate, remaining))
                {
                    break;
                }
            }

            prepared = workerParked && !workerExited && state ==
                Switch2ProUsbInputReadPumpState.Preparing &&
                activationDecision ==
                    Switch2ProUsbInputReadPumpActivationDecision.Pending &&
                ReferenceEquals(preparedActivationFence, activationFence);
            if (prepared)
            {
                state = Switch2ProUsbInputReadPumpState.Prepared;
            }
            else
            {
                activationDecision =
                    Switch2ProUsbInputReadPumpActivationDecision.Abort;
                if (state != Switch2ProUsbInputReadPumpState.Stopped)
                {
                    state = Switch2ProUsbInputReadPumpState.StopRequested;
                }
                SetTerminalFailureNoLock(
                    Switch2ProUsbInputReadPumpFailure.WorkerParkTimedOut);
                Monitor.PulseAll(gate);
            }
            lifecycleOperationInProgress = false;
            Monitor.PulseAll(gate);
        }

        if (!prepared)
        {
            owner.RequestStop();
            failure = Switch2ProUsbInputReadPumpFailure.WorkerParkTimedOut;
            return false;
        }

        failure = Switch2ProUsbInputReadPumpFailure.None;
        return true;
    }

    internal bool TryCommitPrepared(object activationFence,
        out Switch2ProUsbInputReadPumpFailure failure)
    {
        lock (gate)
        {
            if (activationFence == null || state !=
                    Switch2ProUsbInputReadPumpState.Prepared ||
                lifecycleOperationInProgress || !workerParked ||
                activationDecision !=
                    Switch2ProUsbInputReadPumpActivationDecision.Pending ||
                !ReferenceEquals(preparedActivationFence, activationFence))
            {
                failure = Switch2ProUsbInputReadPumpFailure.
                    ActivationCredentialRejected;
                return false;
            }
            if (owner.State != Switch2ProUsbInputTransportState.Open)
            {
                failure = Switch2ProUsbInputReadPumpFailure.
                    ActivationCredentialRejected;
                return false;
            }

            // This is the complete commit boundary: no I/O, callback, worker
            // join, or wait. The parked worker may proceed only after this
            // exact reference credential changes the decision under the gate.
            activationDecision =
                Switch2ProUsbInputReadPumpActivationDecision.Commit;
            state = Switch2ProUsbInputReadPumpState.Running;
            Monitor.PulseAll(gate);
        }

        failure = Switch2ProUsbInputReadPumpFailure.None;
        return true;
    }

    internal bool TryAbortPrepared(object activationFence,
        int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure)
    {
        if (activationFence == null)
        {
            failure = Switch2ProUsbInputReadPumpFailure.
                ActivationCredentialRejected;
            return false;
        }
        return TryStopAndDisposeCore(timeoutMilliseconds, activationFence,
            out failure);
    }

    public bool RequestStop()
    {
        bool changed;
        lock (gate)
        {
            // A parked worker is protected by its exact prepare credential.
            // Generic lifecycle calls must not close the transport behind that
            // credential or make a later table Attach -> Commit fail.
            if (state is Switch2ProUsbInputReadPumpState.Preparing or
                Switch2ProUsbInputReadPumpState.Prepared)
            {
                return false;
            }

            changed = state == Switch2ProUsbInputReadPumpState.Created ||
                state == Switch2ProUsbInputReadPumpState.Running;
            if (state == Switch2ProUsbInputReadPumpState.Created)
            {
                state = Switch2ProUsbInputReadPumpState.Stopped;
            }
            else if (state == Switch2ProUsbInputReadPumpState.Running)
            {
                state = Switch2ProUsbInputReadPumpState.StopRequested;
                if (activationDecision ==
                    Switch2ProUsbInputReadPumpActivationDecision.Pending)
                {
                    activationDecision =
                        Switch2ProUsbInputReadPumpActivationDecision.Abort;
                    Monitor.PulseAll(gate);
                }
            }
        }
        owner.RequestStop();
        return changed;
    }

    public bool TryStopAndDispose(int timeoutMilliseconds,
        out Switch2ProUsbInputReadPumpFailure failure)
        => TryStopAndDisposeCore(timeoutMilliseconds,
            preparedActivationFenceClaim: null, out failure);

    private bool TryStopAndDisposeCore(int timeoutMilliseconds,
        object preparedActivationFenceClaim,
        out Switch2ProUsbInputReadPumpFailure failure)
    {
        if (timeoutMilliseconds < 0 || timeoutMilliseconds >
            Switch2ProUsbInputTransportOwner.
                MaximumDisposeTimeoutMilliseconds)
        {
            failure = Switch2ProUsbInputReadPumpFailure.InvalidStopTimeout;
            return false;
        }

        bool waitForWorker;
        lock (gate)
        {
            bool exactPreparedAbort = preparedActivationFenceClaim != null;
            if (exactPreparedAbort)
            {
                if (state != Switch2ProUsbInputReadPumpState.Prepared ||
                    lifecycleOperationInProgress || !workerParked ||
                    activationDecision !=
                        Switch2ProUsbInputReadPumpActivationDecision.Pending ||
                    !ReferenceEquals(this.preparedActivationFence,
                        preparedActivationFenceClaim))
                {
                    failure = Switch2ProUsbInputReadPumpFailure.
                        ActivationCredentialRejected;
                    return false;
                }
            }
            else if (state == Switch2ProUsbInputReadPumpState.Disposed)
            {
                failure = Switch2ProUsbInputReadPumpFailure.None;
                return true;
            }
            else if (state is Switch2ProUsbInputReadPumpState.Preparing or
                     Switch2ProUsbInputReadPumpState.Prepared)
            {
                failure = Switch2ProUsbInputReadPumpFailure.
                    ActivationCredentialRejected;
                return false;
            }
            if (lifecycleOperationInProgress ||
                ReferenceEquals(Thread.CurrentThread, worker))
            {
                failure = Switch2ProUsbInputReadPumpFailure.
                    OperationAlreadyInProgress;
                return false;
            }
            lifecycleOperationInProgress = true;
            if (state == Switch2ProUsbInputReadPumpState.Created)
            {
                state = Switch2ProUsbInputReadPumpState.Stopped;
            }
            else if (state is Switch2ProUsbInputReadPumpState.Prepared or
                     Switch2ProUsbInputReadPumpState.Running)
            {
                state = Switch2ProUsbInputReadPumpState.StopRequested;
                if (activationDecision ==
                    Switch2ProUsbInputReadPumpActivationDecision.Pending)
                {
                    activationDecision =
                        Switch2ProUsbInputReadPumpActivationDecision.Abort;
                    Monitor.PulseAll(gate);
                }
            }
            waitForWorker = workerStarted && !workerExited;
        }

        owner.RequestStop();
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        if (waitForWorker &&
            !worker.Join(RemainingMilliseconds(deadline,
                timeoutMilliseconds)))
        {
            lock (gate)
            {
                lifecycleOperationInProgress = false;
            }
            failure = Switch2ProUsbInputReadPumpFailure.WorkerExitTimedOut;
            return false;
        }

        int remaining = RemainingMilliseconds(deadline,
            timeoutMilliseconds);
        bool disposed = owner.TryQuiesceAndDispose(ownerFence, remaining,
            out Switch2ProUsbDisposeFailure disposeFailure);
        lock (gate)
        {
            lastDisposeFailure = disposeFailure;
            if (disposed)
            {
                state = Switch2ProUsbInputReadPumpState.Disposed;
            }
            lifecycleOperationInProgress = false;
        }
        if (!disposed)
        {
            failure = Switch2ProUsbInputReadPumpFailure.
                OwnerDisposeRejected;
            return false;
        }

        failure = Switch2ProUsbInputReadPumpFailure.None;
        return true;
    }

    private void Run()
    {
        try
        {
            beforeWorkerPark?.Invoke();
            lock (gate)
            {
                workerParked = true;
                Monitor.PulseAll(gate);
                while (activationDecision ==
                    Switch2ProUsbInputReadPumpActivationDecision.Pending)
                {
                    Monitor.Wait(gate);
                }

                if (activationDecision !=
                    Switch2ProUsbInputReadPumpActivationDecision.Commit)
                {
                    return;
                }
                state = Switch2ProUsbInputReadPumpState.Running;
            }

            while (owner.State == Switch2ProUsbInputTransportState.Open)
            {
                if (!owner.TryBeginRead(ownerFence,
                        out Switch2ProUsbReadClaim claim,
                        out Switch2ProUsbReadBeginFailure beginFailure))
                {
                    lock (gate)
                    {
                        lastReadBeginFailure = beginFailure;
                        if (beginFailure != Switch2ProUsbReadBeginFailure.
                            LifecycleClosed)
                        {
                            SetTerminalFailureNoLock(
                                Switch2ProUsbInputReadPumpFailure.
                                    ReadStartRejected);
                        }
                    }
                    break;
                }
                Interlocked.Increment(ref startedReadCount);

                if (!owner.TryRetireCompletedRead(ownerFence, claim,
                        readRetirementTimeoutMilliseconds,
                        out Switch2ProUsbReadRetirementResult
                            retirementResult,
                        out Switch2ProUsbReadRetirementFailure
                            retirementFailure))
                {
                    lock (gate)
                    {
                        lastRetirementFailure = retirementFailure;
                        SetTerminalFailureNoLock(
                            Switch2ProUsbInputReadPumpFailure.
                                ReadRetirementRejected);
                    }
                    break;
                }
                Interlocked.Increment(ref retiredReadCount);
                lock (gate)
                {
                    lastRetirementResult = retirementResult;
                }

                // Retirement only proves native/managed quiescence. It is the
                // claim-keyed completion result, together with the still-open
                // owner lifetime, that authorizes another submission. In
                // particular, cancellation without a callback is stop-only.
                Switch2ProUsbInputTransportState ownerState = owner.State;
                if (!retirementResult.PermitsRearm || ownerState !=
                    Switch2ProUsbInputTransportState.Open)
                {
                    if (!retirementResult.PermitsRearm)
                    {
                        lock (gate)
                        {
                            // A failed native completion has already closed
                            // the transport owner. Only an explicit pump stop
                            // suppresses lifecycle attention; the owner's
                            // StopRequested state alone is not evidence that
                            // the outer registration is being removed.
                            if (state == Switch2ProUsbInputReadPumpState.Running)
                            {
                                SetTerminalFailureNoLock(
                                    Switch2ProUsbInputReadPumpFailure.
                                        ReadCompletionRejected);
                            }
                        }
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
                    Switch2ProUsbInputReadPumpFailure.
                        UnexpectedWorkerFailure);
            }
        }
        finally
        {
            owner.RequestStop();
            Action<Switch2ProUsbInputReadPumpFailure> attention = null;
            Switch2ProUsbInputReadPumpFailure attentionFailure = default;
            lock (gate)
            {
                workerExited = true;
                if (state != Switch2ProUsbInputReadPumpState.Disposed)
                {
                    state = Switch2ProUsbInputReadPumpState.Stopped;
                }
                if (!lifecycleAttentionRaised && terminalFailure !=
                        Switch2ProUsbInputReadPumpFailure.None)
                {
                    lifecycleAttentionRaised = true;
                    attention = lifecycleAttentionHandler;
                    attentionFailure = terminalFailure;
                }
                Monitor.PulseAll(gate);
            }

            // A lifecycle callback can acquire the outer registration
            // coordinator and can schedule a bounded worker join. It must
            // therefore never run under this pump's gate or synchronously
            // perform retirement on the pump worker itself.
            try
            {
                attention?.Invoke(attentionFailure);
            }
            catch
            {
                // Attention is advisory wake-up evidence. The terminal
                // failure remains sticky and queryable even if a subscriber
                // violates its callback contract.
            }
        }
    }

    private void SetTerminalFailureNoLock(
        Switch2ProUsbInputReadPumpFailure failure)
    {
        if (terminalFailure == Switch2ProUsbInputReadPumpFailure.None)
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
        return remaining <= 0 ? 0 : (int)Math.Min(remaining, int.MaxValue);
    }
}
