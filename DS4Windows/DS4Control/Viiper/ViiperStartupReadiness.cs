using System;
using System.Diagnostics;
using System.Threading;

namespace DS4Windows;

// Cold startup only. VIIPER allows two sequential 10-second prerequisite
// probes before opening its API. Do not kill a healthy owned child at 8 seconds
// while its own bounded driver check is still running. Ready remains immediate.
internal static class ViiperStartupReadiness
{
    internal const int BudgetMilliseconds = 25_000;
    internal delegate bool Probe(int timeoutMilliseconds, out string failure);

    internal static bool Wait(Probe probe, out string failure,
        Func<long> elapsedMilliseconds = null, Action<int> delay = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        Stopwatch elapsed = elapsedMilliseconds == null ? Stopwatch.StartNew() : null;
        elapsedMilliseconds ??= () => elapsed.ElapsedMilliseconds;
        delay ??= Thread.Sleep;
        failure = null;
        while (true)
        {
            long remaining = BudgetMilliseconds - elapsedMilliseconds();
            if (remaining <= 0) return false;
            bool ready = probe((int)Math.Min(1000, remaining), out failure);
            // A dependency that returns late must not authorize output. This
            // admission bound does not cancel an in-flight synchronous probe;
            // each probe is responsible for bounding its own operations.
            remaining = BudgetMilliseconds - elapsedMilliseconds();
            if (ready && remaining >= 0) return true;
            if (remaining <= 0) return false;
            delay((int)Math.Min(50, remaining));
        }
    }
}
