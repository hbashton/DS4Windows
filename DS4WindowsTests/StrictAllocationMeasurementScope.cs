using System.Runtime;

namespace DS4WindowsTests;

/// <summary>
/// Isolates a bounded, synchronous allocation-counter window from GC allocation-
/// context repair. This is test infrastructure, not a production GC policy.
/// Warm the workload first, enter before reading the initial counter, read the
/// final counter before disposal, and assert the unchanged allocation limit only
/// after disposal succeeds. Do not use this across await or around an entire test.
/// </summary>
internal sealed class StrictAllocationMeasurementScope : IDisposable
{
    // The reservation covers the whole process, including test-host threads.
    // The two-argument API may reserve twice this amount across SOH and LOH.
    internal const long ReservationBytes = 16L * 1024 * 1024;
    private static readonly object Gate = new();
    private static StrictAllocationMeasurementScope activeScope;
    private readonly INoGcRegionRuntime runtime;
    private readonly int ownerThreadId;
    private bool disposed;

    private StrictAllocationMeasurementScope(INoGcRegionRuntime runtime)
    {
        this.runtime = runtime;
        ownerThreadId = Environment.CurrentManagedThreadId;
    }

    internal static StrictAllocationMeasurementScope Begin() =>
        Begin(DotNetNoGcRegionRuntime.Instance);

    // The seam exercises failure/ownership handling without forcing global GCs
    // or exhausting the real process's no-GC reservation in lifecycle tests.
    internal static StrictAllocationMeasurementScope Begin(INoGcRegionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var scope = new StrictAllocationMeasurementScope(runtime);
        Monitor.Enter(Gate);
        try
        {
            // Monitor is reentrant: explicitly reject same-thread nesting.
            if (activeScope != null)
                throw new InvalidOperationException("Allocation measurement scopes cannot be nested.");
            if (runtime.IsActive)
                throw new InvalidOperationException("An externally owned no-GC region is already active.");

            // Setup can wait for an existing background GC. It is deliberately
            // outside the counters; disallowFullBlockingGC is not a no-pause promise.
            // There are no retries, skips, adaptive budgets, or tolerance changes.
            if (!runtime.TryStart(ReservationBytes, disallowFullBlockingGc: true))
                throw new InvalidOperationException("Could not reserve the allocation measurement no-GC region.");
            activeScope = scope;
            return scope;
        }
        catch
        {
            Monitor.Exit(Gate);
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("Allocation measurement must end on its starting thread.");

        try
        {
            if (!ReferenceEquals(activeScope, this))
                throw new InvalidOperationException("Allocation measurement scope ownership was lost.");
            if (!runtime.IsActive)
                throw new InvalidOperationException("The allocation measurement no-GC region ended prematurely.");

            // EndNoGCRegion throws if a collection or excess allocation invalidated
            // the reservation. Such failures must fail the test, even for delta 0.
            runtime.End();
        }
        finally
        {
            disposed = true;
            activeScope = null;
            Monitor.Exit(Gate);
        }
    }

    internal interface INoGcRegionRuntime
    {
        bool IsActive { get; }
        bool TryStart(long totalSize, bool disallowFullBlockingGc);
        void End();
    }

    private sealed class DotNetNoGcRegionRuntime : INoGcRegionRuntime
    {
        internal static readonly DotNetNoGcRegionRuntime Instance = new();
        public bool IsActive => GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
        public bool TryStart(long totalSize, bool disallowFullBlockingGc) =>
            GC.TryStartNoGCRegion(totalSize, disallowFullBlockingGc);
        public void End() => GC.EndNoGCRegion();
    }
}
