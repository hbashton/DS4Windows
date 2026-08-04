using System;
using System.ComponentModel;
using System.Diagnostics;
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
        private static ProcessPriorityClass restoreProcessPriority;
        private static bool hasRestoreProcessPriority;
        private static bool raisedProcessPriority;

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

                    CaptureAndRaiseProcessPriorityLocked();
                }

                activeLeases++;
            }

            return new ManagedAudioLatencyLease();
        }

        /// <summary>
        /// Applies the priority selected by the user while retaining the
        /// minimum scheduling class required by a live managed media path.
        /// The selected value remains the value restored when the final media
        /// lease ends.
        /// </summary>
        internal static ProcessPriorityClass ApplyRequestedProcessPriority(
            ProcessPriorityClass requestedPriority)
        {
            lock (SyncRoot)
            {
                restoreProcessPriority = requestedPriority;
                hasRestoreProcessPriority = true;

                ProcessPriorityClass effectivePriority =
                    ResolveEffectiveProcessPriority(requestedPriority,
                        activeLeases > 0);
                ProcessPriorityClass appliedPriority = TrySetProcessPriority(
                    effectivePriority, requestedPriority);
                raisedProcessPriority = activeLeases > 0 &&
                    effectivePriority == ProcessPriorityClass.High &&
                    requestedPriority != ProcessPriorityClass.High &&
                    appliedPriority == ProcessPriorityClass.High;
                return appliedPriority;
            }
        }

        internal static ProcessPriorityClass ResolveEffectiveProcessPriority(
            ProcessPriorityClass requestedPriority, bool mediaActive)
        {
            if (!mediaActive)
            {
                return requestedPriority;
            }

            switch (requestedPriority)
            {
                case ProcessPriorityClass.Idle:
                case ProcessPriorityClass.BelowNormal:
                case ProcessPriorityClass.Normal:
                case ProcessPriorityClass.AboveNormal:
                    return ProcessPriorityClass.High;
                default:
                    return requestedPriority;
            }
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

                if (activeLeases != 0)
                {
                    return;
                }

                RestoreProcessPriorityLocked();

                if (changedLatencyMode)
                {
                    try
                    {
                        // Do not overwrite a latency policy installed by
                        // another component while the audio lease was active.
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

        private static void CaptureAndRaiseProcessPriorityLocked()
        {
            try
            {
                using Process process = Process.GetCurrentProcess();
                restoreProcessPriority = process.PriorityClass;
                hasRestoreProcessPriority = true;
                ProcessPriorityClass effectivePriority =
                    ResolveEffectiveProcessPriority(restoreProcessPriority,
                        mediaActive: true);
                if (effectivePriority != restoreProcessPriority)
                {
                    process.PriorityClass = effectivePriority;
                    raisedProcessPriority = process.PriorityClass ==
                        ProcessPriorityClass.High;
                }
                else
                {
                    raisedProcessPriority = false;
                }
            }
            catch (InvalidOperationException)
            {
                hasRestoreProcessPriority = false;
                raisedProcessPriority = false;
            }
            catch (Win32Exception)
            {
                hasRestoreProcessPriority = false;
                raisedProcessPriority = false;
            }
        }

        private static ProcessPriorityClass TrySetProcessPriority(
            ProcessPriorityClass effectivePriority,
            ProcessPriorityClass fallbackPriority)
        {
            try
            {
                using Process process = Process.GetCurrentProcess();
                process.PriorityClass = effectivePriority;
                return process.PriorityClass;
            }
            catch (InvalidOperationException)
            {
                return fallbackPriority;
            }
            catch (Win32Exception)
            {
                return fallbackPriority;
            }
        }

        private static void RestoreProcessPriorityLocked()
        {
            try
            {
                if (raisedProcessPriority && hasRestoreProcessPriority)
                {
                    using Process process = Process.GetCurrentProcess();
                    // A different owner may have selected a new priority while
                    // audio was active. Restore only the exact High class this
                    // lease applied; otherwise preserve the external change.
                    if (process.PriorityClass == ProcessPriorityClass.High &&
                        restoreProcessPriority != ProcessPriorityClass.High)
                    {
                        process.PriorityClass = restoreProcessPriority;
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
            finally
            {
                raisedProcessPriority = false;
                hasRestoreProcessPriority = false;
            }
        }
    }
}
