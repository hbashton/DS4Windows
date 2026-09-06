/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

The one-owner, latest-state interpolation model is adapted from the
GPL-3.0 licensed Switch2Connect project, commit
61ac6642ce12fe7217e38a860b14863b18ca7e28, src/controller.py. DS4Windows
keeps its canonical profile mapper authoritative and uses this worker only to
present already-mapped continuous Switch 2 mouse velocities at high rate.
*/

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using DS4Windows.DS4Control;

namespace DS4Windows.Switch2;

internal enum Switch2ContinuousMouseSource : byte
{
    Gyro = 1,
    Ir = 2,
    StickAssist = 3,
    MappedStick = 4,
}

internal struct Switch2ContinuousMouseSourceState
{
    internal bool Active;
    internal double VelocityX;
    internal double VelocityY;
    internal long TimestampQpc;
}

internal struct Switch2HighRateMouseSourceMixer
{
    internal Switch2ContinuousMouseSourceState Gyro;
    internal Switch2ContinuousMouseSourceState Ir;
    internal Switch2ContinuousMouseSourceState StickAssist;
    internal Switch2ContinuousMouseSourceState MappedStick;
    internal long ProfileRevision;
    internal bool HasProfileRevision;

    internal bool TryUpdate(Switch2ContinuousMouseSource source, bool active,
        double velocityX, double velocityY, long timestampQpc,
        long profileRevision)
    {
        if (source is not (Switch2ContinuousMouseSource.Gyro or
                Switch2ContinuousMouseSource.Ir or
                Switch2ContinuousMouseSource.StickAssist or
                Switch2ContinuousMouseSource.MappedStick) ||
            timestampQpc <= 0 || profileRevision < 0 ||
            !double.IsFinite(velocityX) || !double.IsFinite(velocityY) ||
            Math.Abs(velocityX) >
                Switch2HighRateMousePresenter.MaximumVelocityPixelsPerSecond ||
            Math.Abs(velocityY) >
                Switch2HighRateMousePresenter.MaximumVelocityPixelsPerSecond)
        {
            return false;
        }

        if (!HasProfileRevision || ProfileRevision != profileRevision)
        {
            this = default;
            HasProfileRevision = true;
            ProfileRevision = profileRevision;
        }

        Switch2ContinuousMouseSourceState next = new()
        {
            Active = active,
            VelocityX = active ? velocityX : 0.0,
            VelocityY = active ? velocityY : 0.0,
            TimestampQpc = timestampQpc,
        };
        switch (source)
        {
            case Switch2ContinuousMouseSource.Gyro:
                Gyro = next;
                break;
            case Switch2ContinuousMouseSource.Ir:
                Ir = next;
                break;
            case Switch2ContinuousMouseSource.StickAssist:
                StickAssist = next;
                break;
            case Switch2ContinuousMouseSource.MappedStick:
                MappedStick = next;
                break;
        }

        return true;
    }

    internal bool TryUpdateMappingSources(bool stickAssistActive,
        double stickAssistVelocityX, double stickAssistVelocityY,
        bool irActive, double irVelocityX, double irVelocityY,
        bool mappedStickActive, double mappedStickVelocityX,
        double mappedStickVelocityY, long timestampQpc,
        long profileRevision)
    {
        // Commit all Mapping-owned sources together. A rejected value must not
        // leave one source live while Mapping takes the exact per-report
        // fallback path for the batch.
        Switch2HighRateMouseSourceMixer candidate = this;
        if (!candidate.TryUpdate(Switch2ContinuousMouseSource.StickAssist,
                stickAssistActive, stickAssistVelocityX,
                stickAssistVelocityY, timestampQpc, profileRevision) ||
            !candidate.TryUpdate(Switch2ContinuousMouseSource.Ir, irActive,
                irVelocityX, irVelocityY, timestampQpc, profileRevision) ||
            !candidate.TryUpdate(Switch2ContinuousMouseSource.MappedStick,
                mappedStickActive, mappedStickVelocityX,
                mappedStickVelocityY, timestampQpc, profileRevision))
        {
            return false;
        }

        this = candidate;
        return true;
    }

    internal readonly bool TrySnapshot(long nowQpc, long qpcFrequency,
        out double velocityX, out double velocityY)
    {
        velocityX = 0.0;
        velocityY = 0.0;
        if (nowQpc <= 0 || qpcFrequency <= 0)
        {
            return false;
        }

        bool active = AddIfFresh(Gyro, nowQpc, qpcFrequency,
            ref velocityX, ref velocityY);
        active |= AddIfFresh(Ir, nowQpc, qpcFrequency,
            ref velocityX, ref velocityY);
        active |= AddIfFresh(StickAssist, nowQpc, qpcFrequency,
            ref velocityX, ref velocityY);
        active |= AddIfFresh(MappedStick, nowQpc, qpcFrequency,
            ref velocityX, ref velocityY);
        return active;
    }

    private static bool AddIfFresh(
        in Switch2ContinuousMouseSourceState source, long nowQpc,
        long qpcFrequency, ref double velocityX, ref double velocityY)
    {
        if (!source.Active || source.TimestampQpc <= 0 ||
            nowQpc < source.TimestampQpc)
        {
            return false;
        }

        double ageSeconds = (nowQpc - source.TimestampQpc) /
            (double)qpcFrequency;
        if (!double.IsFinite(ageSeconds) || ageSeconds >
            Switch2HighRateMousePresenter.SourceTimeToLiveSeconds)
        {
            return false;
        }

        velocityX += source.VelocityX;
        velocityY += source.VelocityY;
        return true;
    }
}

internal struct Switch2HighRateMouseIntegrator
{
    internal double ResidualX;
    internal double ResidualY;

    internal bool TryStep(double velocityX, double velocityY,
        double elapsedSeconds, out int deltaX, out int deltaY)
    {
        deltaX = 0;
        deltaY = 0;
        if (!double.IsFinite(velocityX) || !double.IsFinite(velocityY) ||
            !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0)
        {
            Reset();
            return false;
        }

        if (elapsedSeconds > Switch2HighRateMousePresenter.MaximumIntervalSeconds)
        {
            elapsedSeconds =
                Switch2HighRateMousePresenter.LongIntervalFallbackSeconds;
        }

        double totalX = velocityX * elapsedSeconds + ResidualX;
        double totalY = velocityY * elapsedSeconds + ResidualY;
        if (!double.IsFinite(totalX) || !double.IsFinite(totalY) ||
            Math.Abs(totalX) > int.MaxValue || Math.Abs(totalY) > int.MaxValue)
        {
            Reset();
            return false;
        }

        deltaX = (int)totalX;
        deltaY = (int)totalY;
        ResidualX = totalX - deltaX;
        ResidualY = totalY - deltaY;
        return true;
    }

    internal void Reset()
    {
        ResidualX = 0.0;
        ResidualY = 0.0;
    }
}

/// <summary>
/// One lazy high-rate presenter owned by one logical Switch 2 runtime. Input
/// reports replace fixed-size source state; they never queue work or allocate.
/// The worker is the only owner of interpolation residuals and is synchronously
/// stopped by the runtime's terminal lifecycle.
/// </summary>
internal sealed class Switch2HighRateMousePresenter
{
    internal const double SourceTimeToLiveSeconds = 0.100;
    internal const double MaximumIntervalSeconds = 0.050;
    internal const double LongIntervalFallbackSeconds = 0.015;
    internal const double MaximumVelocityPixelsPerSecond = 1_000_000.0;

    private const int ActiveWaitMilliseconds = 1;
    private const int IdleWaitMilliseconds = 250;

    private readonly object gate = new();
    private readonly object presentationGate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Action<int, int> output;
    private Switch2HighRateMouseSourceMixer mixer;
    private Thread worker;
    private volatile bool stopped;
    private long clearRevision;

    internal Switch2HighRateMousePresenter() : this(PresentToGlobal)
    {
    }

    internal Switch2HighRateMousePresenter(Action<int, int> output)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
    }

    internal bool TrySetSource(Switch2ContinuousMouseSource source,
        bool active, double velocityX, double velocityY,
        long profileRevision)
    {
        lock (gate)
        {
            if (stopped || !mixer.TryUpdate(source, active, velocityX,
                    velocityY, Stopwatch.GetTimestamp(), profileRevision))
            {
                return false;
            }

            StartWorkerNoLock(active);
        }

        wake.Set();
        return true;
    }

    internal bool TrySetMappingSources(bool stickAssistActive,
        double stickAssistVelocityX, double stickAssistVelocityY,
        bool irActive, double irVelocityX, double irVelocityY,
        bool mappedStickActive, double mappedStickVelocityX,
        double mappedStickVelocityY, long profileRevision)
    {
        lock (gate)
        {
            long timestampQpc = Stopwatch.GetTimestamp();
            if (stopped || !mixer.TryUpdateMappingSources(
                    stickAssistActive, stickAssistVelocityX,
                    stickAssistVelocityY, irActive, irVelocityX,
                    irVelocityY, mappedStickActive, mappedStickVelocityX,
                    mappedStickVelocityY, timestampQpc, profileRevision))
            {
                return false;
            }

            StartWorkerNoLock(stickAssistActive || irActive ||
                mappedStickActive);
        }

        // The three Mapping-owned sources share one lock acquisition and one
        // kernel wake per controller report. Gyro remains independently owned
        // by MouseCursor's motion callback.
        wake.Set();
        return true;
    }

    private void StartWorkerNoLock(bool active)
    {
        if (!active || worker != null)
        {
            return;
        }

        worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Switch 2 high-rate mouse presenter",
        };
        worker.Start();
    }

    // Does not wait on external output and does not stop the worker. Callers
    // must serialize source admission against this clear, then fence outside
    // their own runtime lock before acknowledging release.
    internal void ClearSources()
    {
        lock (gate)
        {
            mixer = default;
            Interlocked.Increment(ref clearRevision);
        }
        wake.Set();
    }

    internal bool FencePresentation(CancellationToken cancellationToken)
    {
        // Cold calibration admission only, never a report-path wait. A closed
        // wizard need not wait for an unrelated stuck OS mouse call; success
        // still requires crossing the presentation gate.
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Monitor.TryEnter(presentationGate, 20)) continue;
            try { return !cancellationToken.IsCancellationRequested; }
            finally { Monitor.Exit(presentationGate); }
        }
        return false;
    }

    internal void Stop()
    {
        Thread thread;
        lock (gate)
        {
            if (stopped)
            {
                return;
            }

            stopped = true;
            mixer = default;
            thread = worker;
        }

        wake.Set();
        // Wait for an already-admitted output call. Future calls observe the
        // terminal flag inside this same gate and cannot reach the OS handler.
        lock (presentationGate)
        {
        }
        if (thread != null && thread != Thread.CurrentThread)
        {
            thread.Join(IdleWaitMilliseconds + 100);
        }
    }

    private void WorkerLoop()
    {
        long frequency = Stopwatch.Frequency;
        long lastTimestamp = Stopwatch.GetTimestamp();
        Switch2HighRateMouseIntegrator integrator = default;
        long integratorRevision = Volatile.Read(ref clearRevision);
        bool timerAcquired = false;
        try
        {
            while (true)
            {
                long now = Stopwatch.GetTimestamp();
                bool stop;
                bool active;
                long revision;
                double velocityX = 0.0;
                double velocityY = 0.0;
                lock (gate)
                {
                    stop = stopped;
                    revision = clearRevision;
                    active = !stop && mixer.TrySnapshot(now, frequency,
                        out velocityX, out velocityY);
                }

                if (stop)
                {
                    return;
                }

                if (integratorRevision != revision)
                {
                    integrator.Reset();
                    integratorRevision = revision;
                    lastTimestamp = now;
                }

                if (!active)
                {
                    if (timerAcquired)
                    {
                        timeEndPeriod(1);
                        timerAcquired = false;
                    }
                    integrator.Reset();
                    lastTimestamp = now;
                    wake.WaitOne(IdleWaitMilliseconds);
                    continue;
                }

                if (!timerAcquired)
                {
                    timerAcquired = timeBeginPeriod(1) == 0;
                }

                double elapsedSeconds = (now - lastTimestamp) /
                    (double)frequency;
                lastTimestamp = now;
                if (integrator.TryStep(velocityX, velocityY, elapsedSeconds,
                        out int deltaX, out int deltaY) &&
                    (deltaX != 0 || deltaY != 0))
                {
                    Present(deltaX, deltaY, revision);
                }

                wake.WaitOne(ActiveWaitMilliseconds);
            }
        }
        finally
        {
            if (timerAcquired)
            {
                timeEndPeriod(1);
            }
        }
    }

    private void Present(int deltaX, int deltaY, long revision)
    {
        lock (presentationGate)
        {
            if (stopped || revision != Volatile.Read(ref clearRevision))
            {
                return;
            }

            try
            {
                output(deltaX, deltaY);
            }
            catch
            {
                // Output-handler replacement and service shutdown are external
                // lifecycle boundaries. A later source sample can use the new
                // handler; this worker must never take down controller input.
            }
        }
    }

    private static void PresentToGlobal(int deltaX, int deltaY)
    {
        VirtualKBMBase handler = Global.outputKBMHandler;
        handler?.MoveRelativeMouseImmediate(deltaX, deltaY);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
