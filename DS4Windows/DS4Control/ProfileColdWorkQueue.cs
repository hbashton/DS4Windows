using System;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows;

/// <summary>
/// One cold consumer and at most one pending profile refresh per slot. A busy
/// admission retries outside all controller locks; a newer request replaces
/// pending/retrying work, never creates a second consumer.
/// </summary>
internal sealed class ProfileColdWorkQueue
{
    private readonly object gate = new();
    private readonly Action<Exception> onError;
    private Func<bool> pending;
    private TaskCompletionSource<bool> completion;

    internal ProfileColdWorkQueue(Action<Exception> onError) =>
        this.onError = onError ?? throw new ArgumentNullException(nameof(onError));

    internal static bool TryApply(int slot, object lifecycleGate,
        Func<bool> isCurrent, Action apply)
    {
        if (!isCurrent()) return true;
        if (!ProfileMutationGate.TryEnter(slot, out var mutation)) return false;
        using (mutation)
        {
            // Startup can hold lifecycle then profile. Never wait in the
            // opposite order: retry outside both locks instead. Retirement
            // and replacement cannot race slot-indexed audio/sidecar work.
            if (!Monitor.TryEnter(lifecycleGate)) return false;
            try
            {
                if (isCurrent()) apply();
                return true;
            }
            finally { Monitor.Exit(lifecycleGate); }
        }
    }

    internal static bool TryRefreshMapping(int slot,
        ControllerProfileActionTarget target, long revision,
        Func<Action, bool> stableBackend, Action apply, out bool applied)
    {
        applied = false;
        bool IsCurrent() => target.IsCurrent && !target.Source.IsRemoved &&
            Global.IsCurrentProfileSwitchRevision(slot, revision);
        if (!IsCurrent()) return true;
        if (!ProfileMutationGate.TryEnter(slot, out var mutation)) return false;
        using (mutation)
        {
            bool finished = false, didApply = false;
            stableBackend(() =>
            {
                if (!IsCurrent()) { finished = true; return; }
                target.Source.TryHaltReportingRunAction(() =>
                {
                    if (!target.TryAcquire(out var lease, out var failure))
                    {
                        finished = failure is not (InputControllerSlotTableFailure.Busy or
                            InputControllerSlotTableFailure.TimedOut);
                        return;
                    }
                    using (lease)
                    {
                        if (IsCurrent()) { apply(); didApply = true; }
                        finished = true;
                    }
                });
            });
            applied = didApply;
            return finished;
        }
    }

    // true means applied or obsolete; false means retry after the backoff.
    internal Task Queue(Func<bool> tryApply)
    {
        ArgumentNullException.ThrowIfNull(tryApply);
        lock (gate)
        {
            pending = tryApply;
            if (completion == null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(Drain);
            }
            return completion.Task;
        }
    }

    private async Task Drain()
    {
        Func<bool> work = null;
        while (true)
        {
            lock (gate)
            {
                if (pending != null)
                {
                    work = pending;
                    pending = null;
                }
                if (work == null)
                {
                    TaskCompletionSource<bool> finished = completion;
                    completion = null;
                    finished.TrySetResult(true);
                    return;
                }
            }

            try
            {
                if (work()) work = null;
            }
            catch (Exception ex)
            {
                work = null;
                // Diagnostics must not strand this slot's worker.
                try { onError(ex); } catch { }
            }

            if (work != null) await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
