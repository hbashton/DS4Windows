using System;
using System.Threading;

namespace DS4Windows.InputDevices;

// One physical legacy Nintendo lifetime, not another feedback mapper. Input
// prepares the existing packet format; this owner alone submits rumble to HID.
internal sealed class LegacyNintendoRumbleOutput
{
    private const int MaximumStopAttempts = 3;
    private readonly object gate = new();
    private readonly byte[] latest;
    private readonly byte[] writing;
    private readonly Func<byte[], bool> submit;
    private Thread worker;
    private ulong revision;
    private bool pending;
    private bool active;
    private bool possiblyActive;
    private bool wake;
    private bool stopping;
    private int stopAttempts;
    private int pumping;
    private Exception lastWriteException;

    internal LegacyNintendoRumbleOutput(int reportLength, Func<byte[], bool> submit)
    {
        if (reportLength < 10) throw new ArgumentOutOfRangeException(nameof(reportLength));
        this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
        latest = new byte[reportLength];
        writing = new byte[reportLength];
    }

    internal bool StopDelivered { get { lock (gate) return stopping && !possiblyActive && !pending; } }
    internal Exception LastWriteException { get { lock (gate) return lastWriteException; } }

    internal void Start(string name)
    {
        lock (gate)
        {
            if (worker != null || stopping) throw new InvalidOperationException("Rumble owner cannot restart.");
            worker = new Thread(Run) { Name = name, IsBackground = true, Priority = ThreadPriority.Normal };
            try { worker.Start(); }
            catch { worker = null; throw; }
        }
    }

    internal bool Publish(ReadOnlySpan<byte> report, bool isActive)
    {
        if (report.Length != latest.Length) throw new ArgumentException("Wrong rumble report length.", nameof(report));
        lock (gate)
        {
            if (stopping) return false;
            report.CopyTo(latest);
            unchecked { ++revision; }
            pending = true;
            active = isActive;
            WakeNoLock();
            return true;
        }
    }

    // Preserve the existing input-pass refresh/retry cadence. No polling timer,
    // unbounded queue, native call or wait for delivery exists on this path.
    internal void RequestRetry()
    {
        lock (gate)
            if (!stopping && pending) WakeNoLock();
    }

    private void WakeNoLock()
    {
        wake = true;
        Monitor.Pulse(gate);
    }

    internal bool PumpOnce()
    {
        if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0) return false;
        try
        {
            ulong claimedRevision;
            bool claimedActive;
            lock (gate)
            {
                if (!wake || !pending) return false;
                wake = false;
                latest.CopyTo(writing, 0);
                claimedRevision = revision;
                claimedActive = active;
                // A failed/throwing native call may still have reached hardware.
                if (claimedActive) possiblyActive = true;
                if (stopping) ++stopAttempts;
            }

            bool accepted;
            try { accepted = submit(writing); }
            catch (Exception error)
            {
                // Do not crash the input lifetime from this background thread.
                // Keep bounded diagnostic evidence; an exception never ACKs I/O.
                lock (gate) lastWriteException = error;
                accepted = false;
            }

            lock (gate)
            {
                if (accepted && !claimedActive) possiblyActive = false;
                if (accepted && claimedRevision == revision)
                {
                    pending = false;
                    wake = false; // a retry wake arriving during I/O is obsolete
                }
                if (stopping)
                {
                    // Stop seals publication and drains only its neutral. This
                    // bounded retry count is not a hard native completion bound.
                    if (!possiblyActive)
                    {
                        pending = false;
                        wake = false;
                    }
                    else if (stopAttempts < MaximumStopAttempts)
                    {
                        WakeNoLock();
                    }
                    else
                    {
                        wake = false;
                    }
                }
            }
            return true;
        }
        finally { Volatile.Write(ref pumping, 0); }
    }

    internal void RequestStop(ReadOnlySpan<byte> neutral)
    {
        if (neutral.Length != latest.Length) throw new ArgumentException("Wrong neutral report length.", nameof(neutral));
        lock (gate)
        {
            if (stopping) return;
            stopping = true;
            active = false;
            neutral.CopyTo(latest);
            unchecked { ++revision; }
            // Unsubmitted effects can be discarded. An in-flight active write
            // has already set possiblyActive under this same gate.
            pending = possiblyActive;
            WakeNoLock();
        }
    }

    internal bool StopAndJoin(ReadOnlySpan<byte> neutral)
    {
        RequestStop(neutral);
        Thread captured;
        lock (gate) captured = worker;
        if (captured == Thread.CurrentThread) return false;
        captured?.Join();
        return StopDelivered;
    }

    private void Run()
    {
        while (true)
        {
            lock (gate)
            {
                while (!wake && !stopping) Monitor.Wait(gate);
                if (stopping && (!wake || !pending)) return;
            }
            PumpOnce();
        }
    }
}
