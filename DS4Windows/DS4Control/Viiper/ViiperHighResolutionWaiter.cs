/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    internal enum ViiperDeadlineWaitResult : byte
    {
        DeadlineReached,
        Stopped,
        Interrupted,
    }

    /// <summary>
    /// One reusable high-resolution waitable timer for an explicitly capped
    /// VIIPER state writer. The default V5 path never enters this waiter.
    /// </summary>
    internal sealed class ViiperHighResolutionWaiter : IDisposable
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;
        private const uint Infinite = 0xFFFFFFFF;
        private const uint WaitObject0 = 0;
        private readonly IntPtr[] waitHandles = new IntPtr[3];
        private IntPtr timer;

        internal ViiperHighResolutionWaiter()
        {
            timer = CreateWaitableTimerExW(IntPtr.Zero, null,
                CreateWaitableTimerHighResolution, TimerAllAccess);
            if (timer == IntPtr.Zero)
            {
                timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0,
                    TimerAllAccess);
            }
            waitHandles[0] = timer;
        }

        internal ViiperDeadlineWaitResult WaitUntil(long deadline,
            ManualResetEvent stopSignal, AutoResetEvent interruptSignal)
        {
            ArgumentNullException.ThrowIfNull(stopSignal);
            ArgumentNullException.ThrowIfNull(interruptSignal);
            while (true)
            {
                if (stopSignal.WaitOne(0))
                {
                    return ViiperDeadlineWaitResult.Stopped;
                }
                if (interruptSignal.WaitOne(0))
                {
                    return ViiperDeadlineWaitResult.Interrupted;
                }

                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    return ViiperDeadlineWaitResult.DeadlineReached;
                }

                if (timer != IntPtr.Zero)
                {
                    long dueTime = -Math.Max(1,
                        remaining * 10_000_000L / Stopwatch.Frequency);
                    if (SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero,
                            IntPtr.Zero, false))
                    {
                        waitHandles[1] = stopSignal.SafeWaitHandle.
                            DangerousGetHandle();
                        waitHandles[2] = interruptSignal.SafeWaitHandle.
                            DangerousGetHandle();
                        uint result = WaitForMultipleObjects(3, waitHandles,
                            false, Infinite);
                        return result switch
                        {
                            WaitObject0 =>
                                ViiperDeadlineWaitResult.DeadlineReached,
                            WaitObject0 + 1 =>
                                ViiperDeadlineWaitResult.Stopped,
                            _ => ViiperDeadlineWaitResult.Interrupted,
                        };
                    }
                }

                // Fallback only when the OS timer could not be created or
                // armed. Use interruptible one-millisecond slices for the
                // coarse portion, then yield to the scheduler for a
                // sub-millisecond remainder. A bounded final spin is only
                // justified by measured tail improvement, so this path does
                // not busy-spin or round the remainder up to a mandatory
                // millisecond.
                if (remaining >= Stopwatch.Frequency / 1000)
                {
                    int coarseMilliseconds = (int)Math.Max(0,
                        remaining * 1000 / Stopwatch.Frequency - 1);
                    if (coarseMilliseconds > 0)
                    {
                        // Keep the fallback responsive to media wakeups even
                        // when the high-resolution timer API is unavailable.
                        if (stopSignal.WaitOne(Math.Min(1,
                                coarseMilliseconds)))
                        {
                            return ViiperDeadlineWaitResult.Stopped;
                        }
                    }
                    else
                    {
                        Thread.Yield();
                    }
                    continue;
                }

                Thread.Yield();
            }
        }

        public void Dispose()
        {
            IntPtr handle = Interlocked.Exchange(ref timer, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(
            IntPtr timerAttributes, string timerName, uint flags,
            uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(IntPtr timer,
            ref long dueTime, int period, IntPtr completionRoutine,
            IntPtr completionArgument,
            [MarshalAs(UnmanagedType.Bool)] bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForMultipleObjects(uint count,
            [In] IntPtr[] handles,
            [MarshalAs(UnmanagedType.Bool)] bool waitAll,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
