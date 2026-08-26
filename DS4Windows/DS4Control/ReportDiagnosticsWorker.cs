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
    /// Bounded latest-state lane for report diagnostics and UI notifications.
    /// It keeps logging, filesystem probes, tray callbacks, and formatting off
    /// latency-critical physical DualSense report callbacks.
    /// </summary>
    internal sealed class ReportDiagnosticsWorker : IDisposable
    {
        private readonly object stateLock = new();
        private readonly ReportDiagnosticsSnapshot[] slots;
        private readonly bool[] pending;
        private readonly Action<ReportDiagnosticsSnapshot> dispatch;
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

        internal ReportDiagnosticsWorker(int controllerCount,
            Action<ReportDiagnosticsSnapshot> dispatch,
            Action<Exception> reportFailure = null)
        {
            if (controllerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controllerCount));
            }
            this.dispatch = dispatch ?? throw new ArgumentNullException(
                nameof(dispatch));
            this.reportFailure = reportFailure;
            slots = new ReportDiagnosticsSnapshot[controllerCount];
            pending = new bool[controllerCount];
            worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "Controller report diagnostics",
                Priority = ThreadPriority.BelowNormal,
            };
            worker.Start();
        }

        internal bool IsAlive => worker.IsAlive;

        internal void Publish(in ReportDiagnosticsSnapshot snapshot)
        {
            int controller = snapshot.Controller;
            if ((uint)controller >= (uint)slots.Length ||
                !snapshot.HasWork)
            {
                return;
            }

            lock (stateLock)
            {
                if (!accepting || stopping)
                {
                    return;
                }
                if (pending[controller])
                {
                    slots[controller].Merge(snapshot);
                }
                else
                {
                    slots[controller] = snapshot;
                    pending[controller] = true;
                    pendingCount++;
                    idle.Reset();
                }
                workAvailable.Set();
            }
        }

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
                Array.Clear(pending, 0, pending.Length);
                Array.Clear(slots, 0, slots.Length);
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
                while (TryClaim(out ReportDiagnosticsSnapshot snapshot))
                {
                    try
                    {
                        dispatch(snapshot);
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

        private bool TryClaim(out ReportDiagnosticsSnapshot snapshot)
        {
            lock (stateLock)
            {
                if (stopping || pendingCount == 0)
                {
                    snapshot = default;
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

                    snapshot = slots[candidate];
                    slots[candidate] = default;
                    pending[candidate] = false;
                    pendingCount--;
                    nextController = (candidate + 1) % pending.Length;
                    callbackRunning = true;
                    return true;
                }

                pendingCount = 0;
                idle.Set();
                snapshot = default;
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

    internal struct ReportDiagnosticsSnapshot
    {
        internal int Controller;
        internal DS4Device Device;
        internal string DeviceError;
        internal bool LagChanged;
        internal bool LagOn;
        internal double Latency;
        internal bool FirstReport;
        internal string ProfileName;
        internal int Battery;
        internal bool BatteryNotification;
        internal bool StartupDiagnostic;
        internal int StartupReportCount;
        internal bool Synced;
        internal bool UseDInputOnly;
        internal OutContType ActiveOutput;
        internal bool Cross;
        internal bool Circle;
        internal bool PS;
        internal byte LX;
        internal byte LY;
        internal byte RX;
        internal byte RY;
        internal byte L2;
        internal byte R2;

        internal bool HasWork => !string.IsNullOrEmpty(DeviceError) ||
            LagChanged || FirstReport || BatteryNotification ||
            StartupDiagnostic;

        internal void Merge(in ReportDiagnosticsSnapshot newer)
        {
            Device = newer.Device ?? Device;
            if (!string.IsNullOrEmpty(newer.DeviceError))
            {
                DeviceError = newer.DeviceError;
            }
            if (newer.LagChanged)
            {
                LagChanged = true;
                LagOn = newer.LagOn;
                Latency = newer.Latency;
            }
            if (newer.FirstReport)
            {
                FirstReport = true;
                ProfileName = newer.ProfileName;
                Battery = newer.Battery;
            }
            if (newer.BatteryNotification)
            {
                BatteryNotification = true;
                Battery = newer.Battery;
            }
            if (newer.StartupDiagnostic)
            {
                StartupDiagnostic = true;
                StartupReportCount = newer.StartupReportCount;
                Synced = newer.Synced;
                UseDInputOnly = newer.UseDInputOnly;
                ActiveOutput = newer.ActiveOutput;
                Cross = newer.Cross;
                Circle = newer.Circle;
                PS = newer.PS;
                LX = newer.LX;
                LY = newer.LY;
                RX = newer.RX;
                RY = newer.RY;
                L2 = newer.L2;
                R2 = newer.R2;
            }
        }
    }
}
