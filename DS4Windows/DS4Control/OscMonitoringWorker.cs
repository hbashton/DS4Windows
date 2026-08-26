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
    /// Owns outbound OSC monitoring callbacks. A physical report publisher
    /// performs only bounded state copies and a signal; message construction
    /// and UDP writes execute on this worker. Each controller has one
    /// replaceable pending snapshot, so a blocked receiver cannot create a
    /// stale monitoring backlog.
    /// </summary>
    internal sealed class OscMonitoringWorker : IDisposable
    {
        private readonly object stateLock = new();
        private readonly DS4State[] previousStates;
        private readonly DS4State[] currentStates;
        private readonly bool[] pending;
        private readonly DS4State dispatchPrevious = new();
        private readonly DS4State dispatchCurrent = new();
        private readonly Action<int, DS4State, DS4State> dispatch;
        private readonly Action<Exception> reportFailure;
        private readonly AutoResetEvent workAvailable = new(false);
        private readonly ManualResetEvent idle = new(true);
        private readonly Thread worker;
        private int pendingCount;
        private int nextController;
        private bool accepting;
        private bool callbackRunning;
        private bool stopping;
        private int disposeState;
        private long replacementCount;

        internal OscMonitoringWorker(int controllerCount,
            Action<int, DS4State, DS4State> dispatch,
            Action<Exception> reportFailure = null)
        {
            if (controllerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controllerCount));
            }

            this.dispatch = dispatch ?? throw new ArgumentNullException(
                nameof(dispatch));
            this.reportFailure = reportFailure;
            previousStates = new DS4State[controllerCount];
            currentStates = new DS4State[controllerCount];
            pending = new bool[controllerCount];
            for (int index = 0; index < controllerCount; index++)
            {
                previousStates[index] = new DS4State();
                currentStates[index] = new DS4State();
            }

            worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "OSC monitoring output",
                Priority = ThreadPriority.BelowNormal,
            };
            worker.Start();
        }

        internal long ReplacementCount
        {
            get
            {
                lock (stateLock)
                {
                    return replacementCount;
                }
            }
        }

        internal bool IsAlive => worker.IsAlive;

        internal void Resume()
        {
            lock (stateLock)
            {
                if (!stopping && Volatile.Read(ref disposeState) == 0)
                {
                    accepting = true;
                }
            }
        }

        internal bool Publish(int controller, DS4State previous,
            DS4State current)
        {
            if ((uint)controller >= (uint)pending.Length ||
                previous == null || current == null)
            {
                return false;
            }

            lock (stateLock)
            {
                if (!accepting || stopping)
                {
                    return false;
                }

                if (!pending[controller])
                {
                    previous.CopyTo(previousStates[controller]);
                    pending[controller] = true;
                    pendingCount++;
                    idle.Reset();
                }
                else
                {
                    replacementCount++;
                }
                current.CopyTo(currentStates[controller]);
                workAvailable.Set();
            }
            return true;
        }

        /// <summary>
        /// Stops accepting work, discards work which has not started, and
        /// waits for an already-started callback. This is a lifecycle-only
        /// barrier and is never called by the physical report callback.
        /// </summary>
        internal void Pause()
        {
            if (Volatile.Read(ref disposeState) != 0)
            {
                return;
            }
            PauseCore();
        }

        private void PauseCore()
        {
            lock (stateLock)
            {
                accepting = false;
                for (int index = 0; index < pending.Length; index++)
                {
                    pending[index] = false;
                }
                pendingCount = 0;
                if (!callbackRunning)
                {
                    idle.Set();
                }
            }
            workAvailable.Set();
            idle.WaitOne();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref disposeState, 1, 0) != 0)
            {
                return;
            }
            PauseCore();
            lock (stateLock)
            {
                stopping = true;
                workAvailable.Set();
            }
            worker.Join();
            workAvailable.Dispose();
            idle.Dispose();
            Volatile.Write(ref disposeState, 2);
        }

        private void WorkerLoop()
        {
            while (true)
            {
                workAvailable.WaitOne();
                while (TryClaim(out int controller))
                {
                    try
                    {
                        dispatch(controller, dispatchPrevious,
                            dispatchCurrent);
                    }
                    catch (Exception ex)
                    {
                        reportFailure?.Invoke(ex);
                    }
                    finally
                    {
                        CompleteCallback();
                    }
                }

                lock (stateLock)
                {
                    if (stopping)
                    {
                        return;
                    }
                }
            }
        }

        private bool TryClaim(out int controller)
        {
            lock (stateLock)
            {
                if (stopping || pendingCount == 0)
                {
                    controller = -1;
                    if (!callbackRunning)
                    {
                        idle.Set();
                    }
                    return false;
                }

                for (int offset = 0; offset < pending.Length; offset++)
                {
                    int candidate = (nextController + offset) %
                        pending.Length;
                    if (!pending[candidate])
                    {
                        continue;
                    }

                    previousStates[candidate].CopyTo(dispatchPrevious);
                    currentStates[candidate].CopyTo(dispatchCurrent);
                    pending[candidate] = false;
                    pendingCount--;
                    nextController = (candidate + 1) % pending.Length;
                    callbackRunning = true;
                    controller = candidate;
                    return true;
                }

                controller = -1;
                pendingCount = 0;
                idle.Set();
                return false;
            }
        }

        private void CompleteCallback()
        {
            lock (stateLock)
            {
                callbackRunning = false;
                if (pendingCount == 0)
                {
                    idle.Set();
                }
                else
                {
                    workAvailable.Set();
                }
            }
        }
    }
}
