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
/// Optional diagnostics only, never a canonical controller-input queue. Each
/// cold registration owns an exact source and a three-buffer SPSC mailbox.
/// Producers do not wait for logging, filesystem probes, UI or the consumer.
/// Facet revisions preserve one-shot notifications across latest-value
/// coalescing without replaying already delivered notifications.
/// </summary>
internal sealed class ReportDiagnosticsWorker : IDisposable
{
    private readonly object lifecycleLock = new(); // Cold transitions only.
    private readonly Source[] sources;
    private readonly Action<ReportDiagnosticsSnapshot> dispatch;
    private readonly Action<Exception> reportFailure;
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private int accepting, closed, publishers, workerExited, eventDisposed;
    private long dispatchFailures;

    internal ReportDiagnosticsWorker(int controllerCount,
        Action<ReportDiagnosticsSnapshot> dispatch,
        Action<Exception> reportFailure = null, bool startWorker = true)
    {
        if (controllerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(controllerCount));
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.reportFailure = reportFailure;
        sources = new Source[controllerCount];
        if (startWorker)
        {
            worker = new Thread(Run)
            {
                IsBackground = true, Name = "Controller report diagnostics",
                Priority = ThreadPriority.BelowNormal,
            };
            worker.Start();
        }
    }

    internal bool IsAlive => worker?.IsAlive == true;
    internal long DispatchFailureCount => Interlocked.Read(ref dispatchFailures);

    // The lifecycle owner captures this handle in the actual report callback.
    // Looking up a source by slot/device at report time would admit stale ABA
    // callbacks when that same device object is reused in a new registration.
    internal Source Register(int controller, DS4Device device)
    {
        if ((uint)controller >= (uint)sources.Length || device == null)
            return null;
        lock (lifecycleLock)
        {
            if (closed != 0 || accepting == 0)
                return null;
            var source = new Source(this, controller, device);
            Interlocked.Exchange(ref sources[controller], source)?.Retire();
            return source;
        }
    }

    internal void Resume()
    {
        lock (lifecycleLock)
            if (closed == 0) Volatile.Write(ref accepting, 1);
    }

    internal void Pause()
    {
        lock (lifecycleLock) PauseCore();
    }

    private void PauseCore()
    {
        Volatile.Write(ref accepting, 0);
        for (int slot = 0; slot < sources.Length; slot++)
            Interlocked.Exchange(ref sources[slot], null)?.Retire();
        // An already admitted callback may finish using its old identity.
        // Never wait for optional diagnostics on a controller teardown lane.
    }

    private void Run()
    {
        try
        {
            while (Volatile.Read(ref closed) == 0)
            {
                wake.WaitOne();
                if (Volatile.Read(ref closed) == 0) DrainCore();
            }
        }
        finally
        {
            Volatile.Write(ref workerExited, 1);
            TryDisposeEvent();
        }
    }

    internal int DrainOnce()
    {
        if (worker != null)
            throw new InvalidOperationException("The background worker owns this consumer.");
        return Volatile.Read(ref closed) == 0 ? DrainCore() : 0;
    }

    private int DrainCore()
    {
        int count = 0;
        for (int slot = 0; slot < sources.Length && Volatile.Read(ref closed) == 0; slot++)
        {
            Source source = Volatile.Read(ref sources[slot]);
            if (source == null || !source.TryTake(out ReportDiagnosticsSnapshot snapshot) ||
                !source.IsCurrent || !snapshot.HasWork)
                continue;
            try
            {
                dispatch(snapshot);
                count++;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref dispatchFailures);
                // A failing logger must not kill the only diagnostics consumer.
                try { reportFailure?.Invoke(ex); } catch { }
            }
        }
        return count;
    }

    public void Dispose()
    {
        // Protect the final signal and every admitted producer signal from
        // event disposal. New publishers after closure never touch the event.
        Interlocked.Increment(ref publishers);
        try
        {
            lock (lifecycleLock)
            {
                if (Interlocked.Exchange(ref closed, 1) != 0) return;
                PauseCore();
                wake.Set();
                if (worker == null) Volatile.Write(ref workerExited, 1);
            }
        }
        finally { EndPublish(); }
        // No Join: a blocked logger/UI callback cannot block service shutdown.
    }

    private void EndPublish()
    {
        Interlocked.Decrement(ref publishers);
        if (Volatile.Read(ref closed) != 0) TryDisposeEvent();
    }

    private void TryDisposeEvent()
    {
        if (Volatile.Read(ref closed) != 0 && Volatile.Read(ref workerExited) != 0 &&
            Volatile.Read(ref publishers) == 0 && Interlocked.Exchange(ref eventDisposed, 1) == 0)
            wake.Dispose();
    }

    internal sealed class Source
    {
        private struct VersionedSnapshot
        {
            internal ReportDiagnosticsSnapshot Value;
            internal ulong Error, Lag, First, Battery, Startup;
        }

        private const int Dirty = 4, IndexMask = 3;
        private readonly ReportDiagnosticsWorker owner;
        private readonly VersionedSnapshot[] buffers = new VersionedSnapshot[3];
        private VersionedSnapshot cumulative, delivered;
        private int writeIndex, readIndex = 1, middle = 2, retired, publishing;
        private long coalesced, concurrentPublishRejections;

        internal Source(ReportDiagnosticsWorker owner, int controller, DS4Device device)
        {
            this.owner = owner;
            Controller = controller;
            Device = device;
            cumulative.Value.Source = this;
            cumulative.Value.Controller = controller;
            cumulative.Value.Device = device;
        }

        internal int Controller { get; }
        internal DS4Device Device { get; }
        internal bool IsCurrent => Volatile.Read(ref retired) == 0 &&
            Volatile.Read(ref owner.accepting) != 0 && Volatile.Read(ref owner.closed) == 0 &&
            ReferenceEquals(Volatile.Read(ref owner.sources[Controller]), this);
        internal long CoalescedCount => Interlocked.Read(ref coalesced);
        internal long ConcurrentPublishRejectionCount => Interlocked.Read(ref concurrentPublishRejections);

        internal bool TryPublish(in ReportDiagnosticsSnapshot snapshot)
        {
            Interlocked.Increment(ref owner.publishers);
            bool entered = false;
            try
            {
                if (!IsCurrent || !snapshot.HasWork ||
                    (snapshot.Device != null && !ReferenceEquals(snapshot.Device, Device)))
                    return false;
                if (Interlocked.CompareExchange(ref publishing, 1, 0) != 0)
                {
                    Interlocked.Increment(ref concurrentPublishRejections);
                    return false;
                }
                entered = true;
                ReportDiagnosticsSnapshot update = snapshot;
                // Tray battery is sampled every report; only actual changes
                // need a wakeup. The first observation still delivers zero.
                if (update.BatteryNotification && cumulative.Battery != 0 &&
                    cumulative.Value.Battery == update.Battery &&
                    cumulative.Value.BatteryPolicyRevision == update.BatteryPolicyRevision)
                    update.BatteryNotification = false;
                if (!update.HasWork) return false;
                if (!string.IsNullOrEmpty(update.DeviceError)) cumulative.Error++;
                if (update.LagChanged) cumulative.Lag++;
                if (update.FirstReport) cumulative.First++;
                if (update.BatteryNotification) cumulative.Battery++;
                if (update.StartupDiagnostic) cumulative.Startup++;
                cumulative.Value.Merge(update);
                buffers[writeIndex] = cumulative;
                if (!IsCurrent) return false;
                int previous = Interlocked.Exchange(ref middle, writeIndex | Dirty);
                writeIndex = previous & IndexMask;
                if ((previous & Dirty) != 0) Interlocked.Increment(ref coalesced);
                owner.wake.Set();
                return true;
            }
            finally
            {
                if (entered) Volatile.Write(ref publishing, 0);
                owner.EndPublish();
            }
        }

        internal void Retire()
        {
            Interlocked.Exchange(ref retired, 1);
            Interlocked.CompareExchange(ref owner.sources[Controller], null, this);
        }

        internal bool TryTake(out ReportDiagnosticsSnapshot snapshot)
        {
            snapshot = default;
            if (!IsCurrent || (Volatile.Read(ref middle) & Dirty) == 0) return false;
            int claimed = Interlocked.Exchange(ref middle, readIndex);
            readIndex = claimed & IndexMask;
            VersionedSnapshot current = buffers[readIndex];
            snapshot = current.Value;
            if (current.Error == delivered.Error) snapshot.DeviceError = null;
            snapshot.LagChanged = current.Lag != delivered.Lag;
            snapshot.FirstReport = current.First != delivered.First;
            snapshot.BatteryNotification = current.Battery != delivered.Battery;
            snapshot.StartupDiagnostic = current.Startup != delivered.Startup;
            delivered = current;
            return true;
        }
    }
}

internal struct ReportDiagnosticsSnapshot
{
    internal ReportDiagnosticsWorker.Source Source;
    internal int Controller;
    internal DS4Device Device;
    internal string DeviceError;
    internal bool LagChanged;
    internal bool LagOn;
    internal double Latency;
    internal bool FirstReport;
    internal string ProfileName;
    internal int InitialBattery;
    internal int Battery;
    internal long BatteryPolicyRevision;
    internal bool BatteryNotification;
    internal bool StartupDiagnostic;
    internal int StartupReportCount;
    internal double StartupLatency;
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
        LagChanged || FirstReport || BatteryNotification || StartupDiagnostic;

    internal void Merge(in ReportDiagnosticsSnapshot newer)
    {
        // Source identity is immutable and never copied from producer input.
        if (!string.IsNullOrEmpty(newer.DeviceError)) DeviceError = newer.DeviceError;
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
            InitialBattery = newer.InitialBattery;
        }
        if (newer.BatteryNotification)
        {
            BatteryNotification = true;
            Battery = newer.Battery;
            BatteryPolicyRevision = newer.BatteryPolicyRevision;
        }
        if (newer.StartupDiagnostic)
        {
            StartupDiagnostic = true;
            StartupReportCount = newer.StartupReportCount;
            StartupLatency = newer.StartupLatency;
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
