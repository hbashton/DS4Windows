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

namespace DS4Windows;

/// <summary>
/// One-worker handoff with one active payload and one bounded ACK-successor
/// slot. VIIPER can receive the ACK and send its successor before the local
/// ACK call/completion callback returns. That successor must not overwrite the
/// active payload or be misclassified as overlapping physical delivery.
/// Physical output may block within its bounded transport
/// operation; keeping it off the broker reader lets that reader continue to
/// accept semantic-input acknowledgements in parallel. The feedback ACK still
/// follows the exact physical delivery result.
/// </summary>
internal sealed class XboxOneFeedbackDeliveryDispatcher : IDisposable
{
    private readonly object gate = new();
    private readonly Func<byte[], int, bool> deliver;
    private readonly Action<ulong, bool> acknowledge;
    private readonly Action fault;
    private readonly Action<byte[], int, ulong, bool, bool> completed;
    private readonly AutoResetEvent workReady = new(false);
    private readonly ManualResetEvent idle = new(true);
    private readonly byte[] payload =
        new byte[ControllerFeedbackFrame.SerializedLength];
    private readonly byte[] deliveryPayload =
        new byte[ControllerFeedbackFrame.SerializedLength];
    private readonly Thread worker;
    private readonly Func<bool> processLocalPolicy;
    private readonly WaitHandle[] workSignals;

    private ulong correlation;
    private bool outstanding;
    private bool deliveryActive;
    private bool acceptAcknowledgedSuccessor;
    private ulong lastAcceptedCorrelation;
    private bool stopping;
    private int disposed;

    internal XboxOneFeedbackDeliveryDispatcher(
        Func<byte[], int, bool> deliver,
        Action<ulong, bool> acknowledge,
        Action fault,
        string workerName = "VIIPER Xbox One feedback delivery",
        Action<byte[], int, ulong, bool, bool> completed = null,
        WaitHandle localPolicySignal = null, Func<bool> processLocalPolicy = null)
    {
        this.deliver = deliver ?? throw new ArgumentNullException(
            nameof(deliver));
        this.acknowledge = acknowledge ?? throw new ArgumentNullException(
            nameof(acknowledge));
        this.fault = fault ?? throw new ArgumentNullException(nameof(fault));
        this.completed = completed;
        if ((localPolicySignal == null) != (processLocalPolicy == null))
            throw new ArgumentException("A local policy signal and callback must be provided together.");
        this.processLocalPolicy = processLocalPolicy;
        workSignals = localPolicySignal == null ? null : new WaitHandle[] { workReady, localPolicySignal };
        worker = new Thread(DispatchLoop)
        {
            IsBackground = true,
            Name = string.IsNullOrWhiteSpace(workerName) ?
                "VIIPER Xbox One feedback delivery" : workerName,
            Priority = ThreadPriority.AboveNormal,
        };
        worker.Start();
    }

    internal bool TryEnqueue(ReadOnlySpan<byte> source,
        ulong feedbackCorrelation)
    {
        if (source.Length != payload.Length || feedbackCorrelation == 0 ||
            Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        lock (gate)
        {
            if (stopping || outstanding || Volatile.Read(ref disposed) != 0 ||
                deliveryActive && !acceptAcknowledgedSuccessor ||
                feedbackCorrelation <= lastAcceptedCorrelation)
            {
                return false;
            }

            source.CopyTo(payload);
            correlation = feedbackCorrelation;
            lastAcceptedCorrelation = feedbackCorrelation;
            outstanding = true;
            idle.Reset();
            workReady.Set();
        }
        return true;
    }

    internal bool WaitForIdle(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }
        return idle.WaitOne(timeoutMilliseconds);
    }

    private void DispatchLoop()
    {
        try
        {
            bool retryLocalPolicy = false;
            while (true)
            {
                if (workSignals == null) workReady.WaitOne();
                else
                {
                    int wake = WaitHandle.WaitAny(workSignals, retryLocalPolicy ? 100 : Timeout.Infinite);
                    bool runLocalPolicy = wake is 1 or WaitHandle.WaitTimeout || workSignals[1].WaitOne(0);
                    lock (gate) { if (stopping) runLocalPolicy = false; }
                    if (runLocalPolicy)
                    {
                        // No broker payload, correlation or ACK is fabricated.
                        // A failed physical refresh retries only on this worker,
                        // using a bounded idle backoff, not on the input reader.
                        try { retryLocalPolicy = !processLocalPolicy(); }
                        catch { retryLocalPolicy = true; }
                    }
                }

                ulong currentCorrelation;
                lock (gate)
                {
                    if (!outstanding)
                    {
                        if (stopping)
                        {
                            return;
                        }
                        continue;
                    }
                    currentCorrelation = correlation;
                    payload.CopyTo(deliveryPayload, 0);
                    correlation = 0;
                    outstanding = false;
                    deliveryActive = true;
                    acceptAcknowledgedSuccessor = false;
                }

                bool accepted = false;
                bool failed = false;
                try
                {
                    accepted = deliver(deliveryPayload, deliveryPayload.Length);
                }
                catch
                {
                    accepted = false;
                }

                lock (gate)
                {
                    // A peer may consume the ACK before Write returns. Reserve
                    // room for exactly one successor before publishing it.
                    // The worker cannot deliver that value until this ACK and
                    // callback finish; a failed ACK discards it without output.
                    acceptAcknowledgedSuccessor = accepted && !stopping;
                }

                try
                {
                    acknowledge(currentCorrelation, accepted);
                }
                catch
                {
                    failed = true;
                }

                try
                {
                    // The payload remains owned by this dispatcher until the
                    // callback returns. Completion is deliberately after the
                    // broker write so lifecycle code can distinguish a
                    // locally applied value from an acknowledged one.
                    completed?.Invoke(deliveryPayload, deliveryPayload.Length,
                        currentCorrelation, accepted, !failed);
                }
                catch
                {
                    // Completion is observational. It must never alter the
                    // exact physical-delivery or broker-ack result.
                }

                lock (gate)
                {
                    Array.Clear(deliveryPayload, 0, deliveryPayload.Length);
                    deliveryActive = false;
                    acceptAcknowledgedSuccessor = false;
                    if (!accepted || failed)
                    {
                        stopping = true;
                        outstanding = false;
                        correlation = 0;
                        Array.Clear(payload, 0, payload.Length);
                    }
                    else if (!outstanding)
                    {
                        idle.Set();
                    }
                }

                if (!accepted || failed)
                {
                    try
                    {
                        fault();
                    }
                    catch
                    {
                        // The exact stream is already terminal from this
                        // dispatcher's perspective. Cleanup remains owned by
                        // its outer stream lifetime.
                    }
                    idle.Set();
                    return;
                }

                lock (gate)
                {
                    if (stopping && !outstanding)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            idle.Set();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (gate)
        {
            stopping = true;
        }
        workReady.Set();
        if (Thread.CurrentThread.ManagedThreadId != worker.ManagedThreadId)
        {
            worker.Join();
        }

        workReady.Dispose();
        idle.Dispose();
    }
}
