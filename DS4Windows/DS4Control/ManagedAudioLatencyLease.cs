using System;
using System.Runtime;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Keeps blocking full collections out of a live managed audio path.
    ///
    /// The physical Bluetooth writer is isolated in its own process, but its
    /// encoded-audio producer still lives in DS4Windows. A blocking Gen2 GC in
    /// this process suspends that producer regardless of its MMCSS priority and
    /// can therefore empty the writer's bounded FIFO. The lease is process-wide
    /// and reference counted so multiple controller streams cannot restore the
    /// previous latency mode underneath one another.
    /// </summary>
    internal sealed class ManagedAudioLatencyLease : IDisposable
    {
        private static readonly object SyncRoot = new object();
        private static int activeLeases;
        private static GCLatencyMode restoreMode;
        private static bool changedLatencyMode;

        private int disposed;

        private ManagedAudioLatencyLease()
        {
        }

        internal static ManagedAudioLatencyLease Acquire()
        {
            lock (SyncRoot)
            {
                if (activeLeases == 0)
                {
                    restoreMode = GCSettings.LatencyMode;
                    changedLatencyMode = false;
                    if (restoreMode != GCLatencyMode.NoGCRegion &&
                        restoreMode != GCLatencyMode.SustainedLowLatency)
                    {
                        try
                        {
                            GCSettings.LatencyMode =
                                GCLatencyMode.SustainedLowLatency;
                            changedLatencyMode = GCSettings.LatencyMode ==
                                GCLatencyMode.SustainedLowLatency;
                        }
                        catch (InvalidOperationException)
                        {
                            // Another owner may already have entered a no-GC
                            // region. Preserve it and leave its lifecycle alone.
                        }
                    }
                }

                activeLeases++;
            }

            return new ManagedAudioLatencyLease();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (activeLeases > 0)
                {
                    activeLeases--;
                }

                if (activeLeases != 0 || !changedLatencyMode)
                {
                    return;
                }

                try
                {
                    // Do not overwrite a latency policy installed by another
                    // component while the audio lease was active.
                    if (GCSettings.LatencyMode ==
                        GCLatencyMode.SustainedLowLatency)
                    {
                        GCSettings.LatencyMode = restoreMode;
                    }
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    changedLatencyMode = false;
                }
            }
        }
    }
}
