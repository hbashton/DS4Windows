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
/// Reentrant, synchronous admission for cold native attach/detach work.
/// The state monitor is never held during that work, so cancellation can
/// wake a waiting caller without acquiring or releasing the actual owner's
/// mutation lease. No polling, timer or input-path synchronization is used.
/// </summary>
internal sealed class ViiperNativeMutationGate
{
    private readonly object stateGate = new();
    private int ownerThreadId;
    private int depth;

    internal IDisposable Enter(CancellationToken cancellationToken = default)
    {
        using CancellationTokenRegistration cancellation = cancellationToken.UnsafeRegister(
            static state => ((ViiperNativeMutationGate)state).WakeWaiters(), this);
        int threadId = Environment.CurrentManagedThreadId;
        lock (stateGate)
        {
            while (ownerThreadId != 0 && ownerThreadId != threadId)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(stateGate);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Allocate before mutating admission: a failed allocation cannot
            // leave a gate owned without a scope capable of releasing it.
            var lease = new Lease(this, threadId);
            depth = checked(depth + 1);
            ownerThreadId = threadId;
            return lease;
        }
        // The cancellation registration is joined only after stateGate has
        // been released, avoiding a callback-vs-registration-disposal deadlock.
    }

    private void WakeWaiters()
    {
        lock (stateGate)
            Monitor.PulseAll(stateGate);
    }

    private void Exit(int threadId)
    {
        lock (stateGate)
        {
            if (ownerThreadId != threadId || depth <= 0)
                throw new SynchronizationLockException("Native mutation scope has no matching owner.");
            if (--depth == 0)
            {
                ownerThreadId = 0;
                Monitor.PulseAll(stateGate);
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly ViiperNativeMutationGate owner;
        private readonly int threadId;
        private int disposed;

        internal Lease(ViiperNativeMutationGate owner, int threadId)
        {
            this.owner = owner;
            this.threadId = threadId;
        }

        public void Dispose()
        {
            if (Volatile.Read(ref disposed) != 0)
                return;
            if (Environment.CurrentManagedThreadId != threadId)
                throw new SynchronizationLockException("Native mutation scope must exit on its acquiring thread.");
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Exit(threadId);
        }
    }
}
