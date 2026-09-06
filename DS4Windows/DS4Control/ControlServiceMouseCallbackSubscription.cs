using System;
using System.Diagnostics;
using System.Threading;

namespace DS4Windows;

/// <summary>
/// Owns exactly one physical-source to logical-Mouse callback lifetime. Event
/// removal alone is insufficient: an event invocation can already have copied
/// its multicast delegate. Retired wrappers reject those copied callbacks.
/// </summary>
internal sealed class ControlServiceMouseCallbackSubscription
{
    private static long nextGeneration;
    private static int generationExhausted;
    [ThreadStatic] private static int callbackDepth;
    private const int RetiredMask = int.MinValue;
    private readonly object gate = new object();
    private readonly DS4Touchpad touchpad;
    private readonly DS4SixAxis sixAxis;
    private bool retired;
    private bool subscribed;
    private bool activated;
    private int admission = RetiredMask;

    internal ControlServiceMouseCallbackSubscription(Mouse mouse,
        DS4Device source, int logicalSlot)
    {
        Mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if ((uint)logicalSlot >= Global.MAX_DS4_CONTROLLER_COUNT)
            throw new ArgumentOutOfRangeException(nameof(logicalSlot));
        if (mouse.LogicalSlot != logicalSlot)
            throw new ArgumentException("Mouse belongs to another logical slot.", nameof(mouse));
        touchpad = source.Touchpad;
        sixAxis = source.SixAxis;
        if (touchpad == null || sixAxis == null)
            throw new ArgumentException("The physical source has no callback surfaces.", nameof(source));
        LogicalSlot = logicalSlot;
        if (Volatile.Read(ref generationExhausted) != 0)
            throw new InvalidOperationException("Mouse callback generation exhausted.");
        Generation = Interlocked.Increment(ref nextGeneration);
        if (Generation <= 0)
        {
            Volatile.Write(ref generationExhausted, 1);
            throw new InvalidOperationException("Mouse callback generation exhausted.");
        }
    }

    internal Mouse Mouse { get; }
    internal DS4Device Source { get; }
    internal int LogicalSlot { get; }
    internal long Generation { get; }
    internal bool IsRetired { get { lock (gate) return retired; } }
    internal bool IsAcceptingCallbacks => Volatile.Read(ref admission) >= 0;
    internal static bool IsInsideCallback => callbackDepth != 0;

    // Cold registry-owned operation. The source can report while handlers are
    // attached, but no wrapper is admitted until the complete set is attached.
    internal void Subscribe()
    {
        lock (gate)
        {
            if (retired || activated) throw new InvalidOperationException("Subscription is not prepared.");
            // Retain exact cleanup authority even if an event add fails part
            // way through. Admission remains closed until every add succeeds.
            activated = true;
            subscribed = true;
            touchpad.TouchButtonDown += TouchButtonDown;
            touchpad.TouchButtonUp += TouchButtonUp;
            touchpad.TouchesBegan += TouchesBegan;
            touchpad.TouchesBegan += TouchStartedOrEnded;
            touchpad.TouchesMoved += TouchesMoved;
            touchpad.TouchesEnded += TouchesEnded;
            touchpad.TouchesEnded += TouchStartedOrEnded;
            touchpad.TouchUnchanged += TouchUnchanged;
            touchpad.PreTouchProcess += PreTouchProcess;
            sixAxis.SixAccelMoved += SixAxisMoved;
            Volatile.Write(ref admission, 0);
        }
    }

    // Switch 2 processes gyro inside its already-admitted Report pipeline.
    // Its raw SixAxis event precedes table admission and must not invoke Mouse.
    // Direct mode reuses the same invocation/retirement and reentrancy guard
    // without adding an event subscriber, second table lease, or worker.
    internal void ActivateDirectPublication()
    {
        lock (gate)
        {
            if (retired || activated) throw new InvalidOperationException("Callback owner is not prepared.");
            activated = true;
            Volatile.Write(ref admission, 0);
        }
    }

    internal bool TryInvokeProjectedMotion(DS4SixAxis sender, SixAxisEventArgs args)
    {
        if (!TryEnter(sender, true)) return false;
        try { Mouse.sixaxisMoved(sender, args); return true; } finally { Exit(); }
    }

    internal bool TryRetire(int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        long started = Stopwatch.GetTimestamp();
        lock (gate)
        {
            retired = true;
            Interlocked.Or(ref admission, RetiredMask);
            if (subscribed)
            {
                // These are ordinary event fields, not device/controller I/O.
                // Method + unique owner identity removes only our exact handlers.
                touchpad.TouchButtonDown -= TouchButtonDown;
                touchpad.TouchButtonUp -= TouchButtonUp;
                touchpad.TouchesBegan -= TouchesBegan;
                touchpad.TouchesBegan -= TouchStartedOrEnded;
                touchpad.TouchesMoved -= TouchesMoved;
                touchpad.TouchesEnded -= TouchesEnded;
                touchpad.TouchesEnded -= TouchStartedOrEnded;
                touchpad.TouchUnchanged -= TouchUnchanged;
                touchpad.PreTouchProcess -= PreTouchProcess;
                sixAxis.SixAccelMoved -= SixAxisMoved;
                subscribed = false;
            }
            while ((Volatile.Read(ref admission) & int.MaxValue) != 0)
            {
                // Never wait from a Mouse callback, including nested callbacks
                // targeting another owner. That could await this very thread.
                if (callbackDepth != 0) return false;
                int remaining = Remaining(started, timeoutMilliseconds);
                if (remaining == 0) return false;
                Monitor.Wait(gate, remaining);
            }
            return true;
        }
    }

    internal static int Remaining(long started, int timeoutMilliseconds)
    {
        double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        return Math.Max(0, timeoutMilliseconds - (int)Math.Min(int.MaxValue, Math.Ceiling(elapsed)));
    }

    private bool TryEnter(object sender, bool sixAxis)
    {
        if (!ReferenceEquals(sender, sixAxis ? (object)this.sixAxis : touchpad))
            return false;
        // Admission and retirement are one atomic word. A reader cannot pass
        // a pre-retirement test, pause, and increment after a completed drain.
        // The ordinary callback path takes no monitor and allocates nothing.
        int observed = Volatile.Read(ref admission);
        while (observed >= 0 && observed != int.MaxValue)
        {
            int current = Interlocked.CompareExchange(ref admission, observed + 1, observed);
            if (current == observed)
            {
                callbackDepth++;
                return true;
            }
            observed = current;
        }
        return false;
    }

    private void Exit()
    {
        callbackDepth--;
        if (Interlocked.Decrement(ref admission) == RetiredMask)
        {
            // Only the last callback of a retired owner reaches this cold
            // wakeup. No controller code runs under the retirement monitor.
            lock (gate) Monitor.PulseAll(gate);
        }
    }

    private void TouchButtonDown(DS4Touchpad sender, TouchpadEventArgs args)
    {
        if (!TryEnter(sender, false)) return;
        try { Mouse.touchButtonDown(sender, args); } finally { Exit(); }
    }
    private void TouchButtonUp(DS4Touchpad sender, TouchpadEventArgs args)
    {
        if (!TryEnter(sender, false)) return;
        try { Mouse.touchButtonUp(sender, args); } finally { Exit(); }
    }
    private void TouchesBegan(DS4Touchpad sender, TouchpadEventArgs args)
    {
        if (!TryEnter(sender, false)) return;
        try { Mouse.touchesBegan(sender, args); } finally { Exit(); }
    }
    private void TouchStartedOrEnded(DS4Touchpad sender, TouchpadEventArgs args)
    {
        if (!TryEnter(sender, false)) return;
        try { Mouse.TouchStartedOrEnded(sender, args); } finally { Exit(); }
    }
    private void TouchesMoved(DS4Touchpad sender, TouchpadEventArgs args)
    {
        if (!TryEnter(sender, false)) return;
        try { Mouse.touchesMoved(sender, args); } finally { Exit(); }
    }
    private void TouchesEnded(DS4Touchpad sender, TouchpadEventArgs args)
    {
        if (!TryEnter(sender, false)) return;
        try { Mouse.touchesEnded(sender, args); } finally { Exit(); }
    }
    private void TouchUnchanged(DS4Touchpad sender, EventArgs args)
    {
        if (!TryEnter(sender, false)) return;
        try { Mouse.touchUnchanged(sender, args); } finally { Exit(); }
    }
    private void PreTouchProcess(DS4Touchpad sender, EventArgs args)
    {
        if (!TryEnter(sender, false)) return;
        try { Mouse.populatePriorButtonStates(); } finally { Exit(); }
    }
    private void SixAxisMoved(DS4SixAxis sender, SixAxisEventArgs args)
    {
        TryInvokeProjectedMotion(sender, args);
    }
}

/// <summary>
/// Cold subscription ownership only. No report callback takes this registry's
/// gate; it must never be used as a whole-report or transport serializer.
/// A timed-out retired owner remains a tombstone until its drain is proven.
/// </summary>
internal sealed class ControlServiceMouseCallbackRegistry
{
    private readonly object gate = new object();
    private readonly ControlServiceMouseCallbackSubscription[] slots =
        new ControlServiceMouseCallbackSubscription[Global.MAX_DS4_CONTROLLER_COUNT];

    internal bool TryReplace(int logicalSlot, Mouse mouse, DS4Device source,
        int timeoutMilliseconds)
    {
        if ((uint)logicalSlot >= (uint)slots.Length)
            throw new ArgumentOutOfRangeException(nameof(logicalSlot));
        if (ControlServiceMouseCallbackSubscription.IsInsideCallback) return false;
        lock (gate)
        {
            var existing = slots[logicalSlot];
            if (existing != null && existing.IsAcceptingCallbacks &&
                ReferenceEquals(existing.Mouse, mouse) && ReferenceEquals(existing.Source, source))
                return true;
            if (!TryRetireNoLock(logicalSlot, existing?.Mouse, source, timeoutMilliseconds)) return false;
            var next = new ControlServiceMouseCallbackSubscription(mouse, source, logicalSlot);
            Volatile.Write(ref slots[logicalSlot], next);
            next.Subscribe();
            return true;
        }
    }

    internal bool TryRetireSource(DS4Device source, int timeoutMilliseconds)
    {
        // A cold closer may already hold the registry gate while waiting for
        // this callback. Never take that gate reentrantly from controller work.
        if (ControlServiceMouseCallbackSubscription.IsInsideCallback) return false;
        if (source == null) throw new ArgumentNullException(nameof(source));
        lock (gate) return TryRetireNoLock(-1, null, source, timeoutMilliseconds);
    }

    internal bool TryRetireMouse(int logicalSlot, Mouse expectedMouse, int timeoutMilliseconds)
    {
        if (ControlServiceMouseCallbackSubscription.IsInsideCallback) return false;
        lock (gate) return TryRetireNoLock(logicalSlot, expectedMouse, null, timeoutMilliseconds);
    }

    internal void RevokeSourceFromCallback(DS4Device source)
    {
        // Emergency reentrant removal must not acquire the registry gate: an
        // external closer can hold it while awaiting this callback. Snapshots
        // carry exact object identities; no slot-only deletion occurs here.
        for (int i = 0; i < slots.Length; i++)
        {
            var owner = Volatile.Read(ref slots[i]);
            if (owner != null && (ReferenceEquals(owner.Source, source) ||
                ReferenceEquals(owner.Mouse.BoundDevice, source)))
                owner.TryRetire(0);
        }
    }

    private bool TryRetireNoLock(int logicalSlot, Mouse expectedMouse,
        DS4Device source, int timeoutMilliseconds)
    {
        long started = Stopwatch.GetTimestamp();
        bool drained = true;
        for (int i = 0; i < slots.Length; i++)
        {
            var owner = slots[i];
            if (owner == null) continue;
            bool exactMouse = expectedMouse != null && i == logicalSlot &&
                ReferenceEquals(owner.Mouse, expectedMouse);
            bool exactSource = source != null && (ReferenceEquals(owner.Source, source) ||
                ReferenceEquals(owner.Mouse.BoundDevice, source));
            if (!exactMouse && !exactSource) continue;
            if (owner.TryRetire(ControlServiceMouseCallbackSubscription.Remaining(started, timeoutMilliseconds)))
            {
                // A secondary physical source can drive the primary's Mouse.
                // Retire its contribution at that logical slot, not the removed
                // physical slot. Only this still-owning registry incarnation
                // may reset it; replaying a stale helper must not reset a successor.
                Mapping.RequestPostMapStickReset(owner.LogicalSlot);
                Volatile.Write(ref slots[i], null);
            }
            else drained = false;
        }
        return drained;
    }
}
