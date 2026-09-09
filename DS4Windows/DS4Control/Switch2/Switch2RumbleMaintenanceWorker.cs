using System;
using System.Threading;

namespace DS4Windows.Switch2;

/// <summary>
/// One coalescing output-only worker. Dormant controllers have no scheduled
/// timer; Wake never waits for a timer/output lock. Stop seals new work without
/// waiting on a controller input thread; the physical lifetime separately
/// drains any already-admitted write before retiring its transport.
/// </summary>
internal sealed class Switch2RumbleMaintenanceWorker
{
    // Source-backed cadence, not a hard-real-time Windows scheduling promise:
    // SDL c71abd08605b8bb7078372307a93274725c99fe0, SDL_hidapi_switch2.c
    // RUMBLE_INTERVAL=12; Switch2Connect 61ac6642 BT_RUMBLE_MIN_INTERVAL=.015.
    internal const int UsbIntervalMilliseconds = 12;
    internal const int BluetoothIntervalMilliseconds = 15;
    private const int Dormant = 0, Scheduled = 1, Running = 2, Signaled = 3, Stopped = 4;
    private readonly Func<ulong, bool> service;
    private readonly Timer timer;
    private readonly int intervalMilliseconds;
    private int state;
    private int failureCount;

    internal Switch2RumbleMaintenanceWorker(Func<ulong, bool> service,
        int intervalMilliseconds, bool automaticTimer = true)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        if (intervalMilliseconds is not (UsbIntervalMilliseconds or BluetoothIntervalMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        this.intervalMilliseconds = intervalMilliseconds;
        if (automaticTimer) timer = new Timer(TimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    internal bool IsScheduled => Volatile.Read(ref state) == Scheduled;
    internal bool IsStopped => Volatile.Read(ref state) == Stopped;
    internal int FailureCount => Volatile.Read(ref failureCount);

    internal void Wake()
    {
        while (true)
        {
            int observed = Volatile.Read(ref state);
            if (observed is Scheduled or Signaled or Stopped) return;
            int next = observed == Running ? Signaled : Scheduled;
            if (Interlocked.CompareExchange(ref state, next, observed) != observed) continue;
            if (next == Scheduled) ScheduleNext(intervalMilliseconds);
            return;
        }
    }

    internal void Stop()
    {
        Interlocked.Exchange(ref state, Stopped);
        timer?.Dispose();
    }

    private void TimerTick(object unused)
    {
        if (ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now)) RunScheduledTick(now);
        else Stop();
    }

    // Explicit timestamps allow deterministic scheduler tests without timers,
    // controller handles, user profiles, or sleeping through physical effects.
    internal void RunScheduledTick(ulong nowMicroseconds)
    {
        if (Interlocked.CompareExchange(ref state, Running, Scheduled) != Scheduled) return;
        bool moreWork = false;
        try { moreWork = service(nowMicroseconds); }
        catch { Interlocked.Increment(ref failureCount); }
        finally
        {
            while (true)
            {
                int observed = Volatile.Read(ref state);
                if (observed == Stopped) break;
                int next = moreWork || observed == Signaled ? Scheduled : Dormant;
                if (Interlocked.CompareExchange(ref state, next, observed) != observed) continue;
                if (next == Scheduled)
                {
                    // Match the donors' send-start cadence without piling up
                    // missed ticks behind slow Bluetooth I/O.
                    int delay = intervalMilliseconds;
                    if (ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong completed) && completed >= nowMicroseconds)
                        delay = (int)Math.Max(1UL, (ulong)intervalMilliseconds - Math.Min(
                            (ulong)intervalMilliseconds - 1, (completed - nowMicroseconds) / 1000));
                    ScheduleNext(delay);
                }
                break;
            }
        }
    }

    private void ScheduleNext(int delayMilliseconds)
    {
        try { timer?.Change(delayMilliseconds, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* Stop raced a previously admitted wake. */ }
    }
}
