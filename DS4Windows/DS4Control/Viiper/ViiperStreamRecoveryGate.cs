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

namespace DS4Windows
{
    /// <summary>
    /// Elects one slow stream-recovery owner without holding a monitor across
    /// waits, logging, controller/API work, or socket I/O. Concurrent transport
    /// consumers wait on one reusable completion event and observe the elected
    /// owner's result for that failed generation rather than reopening twice.
    /// Input producers never enter this gate.
    /// </summary>
    internal sealed class ViiperStreamRecoveryGate
    {
        private const long NoGeneration = long.MinValue;

        private readonly ManualResetEvent completion =
            new ManualResetEvent(true);
        private int ownerActive;
        private long completedGeneration = NoGeneration;
        private int completedSuccessfully;

        internal bool ExecuteOrWait(long failedGeneration,
            Func<bool> recoverAsOwner, Func<bool> stopRequested = null)
        {
            ArgumentNullException.ThrowIfNull(recoverAsOwner);

            while (true)
            {
                if (Volatile.Read(ref completedGeneration) ==
                    failedGeneration)
                {
                    return Volatile.Read(ref completedSuccessfully) != 0;
                }
                if (stopRequested?.Invoke() == true)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref ownerActive, 1, 0) == 0)
                {
                    // A waiter can observe ownerActive before this Reset. Its
                    // wait loop rechecks ownerActive, so a stale signaled state
                    // cannot let it attempt a duplicate reopen.
                    completion.Reset();
                    bool succeeded = false;
                    try
                    {
                        if (stopRequested?.Invoke() != true)
                        {
                            succeeded = recoverAsOwner();
                        }
                        return succeeded;
                    }
                    finally
                    {
                        Volatile.Write(ref completedSuccessfully,
                            succeeded ? 1 : 0);
                        Interlocked.Exchange(ref completedGeneration,
                            failedGeneration);
                        Volatile.Write(ref ownerActive, 0);
                        completion.Set();
                    }
                }

                while (Volatile.Read(ref ownerActive) != 0)
                {
                    if (stopRequested?.Invoke() == true)
                    {
                        return false;
                    }
                    completion.WaitOne(50);
                }
            }
        }

        internal void Reset()
        {
            if (Volatile.Read(ref ownerActive) != 0)
            {
                throw new InvalidOperationException(
                    "A live recovery owner must retire before reset.");
            }

            Volatile.Write(ref completedSuccessfully, 0);
            Interlocked.Exchange(ref completedGeneration, NoGeneration);
            completion.Set();
        }

        internal void WaitForIdle()
        {
            while (Volatile.Read(ref ownerActive) != 0)
            {
                completion.WaitOne(50);
            }
        }
    }
}
