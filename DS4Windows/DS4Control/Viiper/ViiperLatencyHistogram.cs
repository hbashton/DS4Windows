using System;
using System.Diagnostics;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Fixed-bucket, allocation-free latency distribution for realtime paths.
    /// Snapshots and formatting are diagnostic-only cold operations.
    /// </summary>
    internal sealed class ViiperLatencyHistogram
    {
        internal const string EnvironmentVariableName =
            "DS4WINDOWS_VIIPER_LATENCY_DIAGNOSTICS";

        private static readonly long[] BucketUpperTicks = BuildBucketTicks();
        private readonly long[] buckets = new long[BucketUpperTicks.Length];
        private long count;
        private long maximumTicks;

        internal static bool Enabled { get; } = ParseEnabled(
            Environment.GetEnvironmentVariable(EnvironmentVariableName));

        internal void Observe(long elapsedTicks)
        {
            if (!Enabled || elapsedTicks < 0)
            {
                return;
            }

            int bucket = BucketUpperTicks.Length - 1;
            for (int index = 0; index < BucketUpperTicks.Length; index++)
            {
                if (elapsedTicks <= BucketUpperTicks[index])
                {
                    bucket = index;
                    break;
                }
            }
            Interlocked.Increment(ref buckets[bucket]);
            Interlocked.Increment(ref count);
            RecordMaximum(ref maximumTicks, elapsedTicks);
        }

        internal ViiperLatencySnapshot Snapshot()
        {
            long sampleCount = Interlocked.Read(ref count);
            if (sampleCount == 0)
            {
                return default;
            }

            return new ViiperLatencySnapshot(sampleCount,
                QuantileTicks(sampleCount, 0.50),
                QuantileTicks(sampleCount, 0.95),
                QuantileTicks(sampleCount, 0.99),
                QuantileTicks(sampleCount, 0.999),
                Interlocked.Read(ref maximumTicks));
        }

        private long QuantileTicks(long sampleCount, double quantile)
        {
            long target = Math.Max(1,
                (long)Math.Ceiling(sampleCount * quantile));
            long cumulative = 0;
            for (int index = 0; index < buckets.Length; index++)
            {
                cumulative += Interlocked.Read(ref buckets[index]);
                if (cumulative >= target)
                {
                    return index == buckets.Length - 1 ?
                        Interlocked.Read(ref maximumTicks) :
                        BucketUpperTicks[index];
                }
            }
            return Interlocked.Read(ref maximumTicks);
        }

        private static long[] BuildBucketTicks()
        {
            int[] microseconds =
            {
                1, 2, 5, 10, 20, 50, 100, 200, 500,
                1_000, 2_000, 5_000, 10_000, 20_000, 50_000,
                100_000, 250_000, 500_000, 1_000_000,
            };
            long[] ticks = new long[microseconds.Length];
            for (int index = 0; index < ticks.Length; index++)
            {
                ticks[index] = Math.Max(1,
                    Stopwatch.Frequency * microseconds[index] / 1_000_000);
            }
            return ticks;
        }

        private static bool ParseEnabled(string value) =>
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

        private static void RecordMaximum(ref long target, long candidate)
        {
            long current = Interlocked.Read(ref target);
            while (candidate > current)
            {
                long observed = Interlocked.CompareExchange(ref target,
                    candidate, current);
                if (observed == current)
                {
                    return;
                }
                current = observed;
            }
        }
    }

    internal readonly struct ViiperLatencySnapshot
    {
        internal ViiperLatencySnapshot(long count, long p50Ticks,
            long p95Ticks, long p99Ticks, long p999Ticks, long maximumTicks)
        {
            Count = count;
            P50Ticks = p50Ticks;
            P95Ticks = p95Ticks;
            P99Ticks = p99Ticks;
            P999Ticks = p999Ticks;
            MaximumTicks = maximumTicks;
        }

        internal long Count { get; }
        internal long P50Ticks { get; }
        internal long P95Ticks { get; }
        internal long P99Ticks { get; }
        internal long P999Ticks { get; }
        internal long MaximumTicks { get; }

        internal string Format(string name)
        {
            return $"{name}[count={Count} " +
                $"p50={Milliseconds(P50Ticks):F3}ms " +
                $"p95={Milliseconds(P95Ticks):F3}ms " +
                $"p99={Milliseconds(P99Ticks):F3}ms " +
                $"p99.9={Milliseconds(P999Ticks):F3}ms " +
                $"max={Milliseconds(MaximumTicks):F3}ms]";
        }

        private static double Milliseconds(long ticks) =>
            ticks * 1000.0 / Stopwatch.Frequency;
    }
}
