namespace DS4WinWPF.DS4Forms
{
    /// <summary>
    /// Collapses duplicate WMI suspend/resume notifications into one service
    /// restart and invalidates a delayed resume when another suspend begins.
    /// </summary>
    internal sealed class PowerLifecyclePolicy
    {
        private readonly object sync = new();
        private bool closed;
        private bool restartPending;
        private bool resumeIssued;
        private long generation;

        public PowerSuspendTransition Suspend(bool serviceRunning)
        {
            lock (sync)
            {
                if (closed)
                {
                    return PowerSuspendTransition.Rejected;
                }

                generation++;
                resumeIssued = false;
                restartPending |= serviceRunning;
                return new PowerSuspendTransition(true, serviceRunning,
                    generation);
            }
        }

        public PowerResumeTransition Resume()
        {
            lock (sync)
            {
                if (closed)
                {
                    return PowerResumeTransition.Rejected;
                }

                // Windows can publish more than one resume notification for a
                // single sleep cycle. The first notification owns the restart;
                // later duplicates must not cancel its delayed lease.
                if (resumeIssued)
                {
                    return new PowerResumeTransition(true, false, generation);
                }

                generation++;
                resumeIssued = true;
                bool restartService = restartPending;
                restartPending = false;
                return new PowerResumeTransition(true, restartService,
                    generation);
            }
        }

        public bool IsCurrent(long candidateGeneration)
        {
            lock (sync)
            {
                return !closed && candidateGeneration == generation;
            }
        }

        public void Close()
        {
            lock (sync)
            {
                closed = true;
                restartPending = false;
                resumeIssued = false;
                generation++;
            }
        }
    }

    internal readonly struct PowerSuspendTransition
    {
        public static PowerSuspendTransition Rejected => new(false, false, 0);

        public PowerSuspendTransition(bool accepted, bool stopService,
            long generation)
        {
            Accepted = accepted;
            StopService = stopService;
            Generation = generation;
        }

        public bool Accepted { get; }
        public bool StopService { get; }
        public long Generation { get; }
    }

    internal readonly struct PowerResumeTransition
    {
        public static PowerResumeTransition Rejected => new(false, false, 0);

        public PowerResumeTransition(bool accepted, bool restartService,
            long generation)
        {
            Accepted = accepted;
            RestartService = restartService;
            Generation = generation;
        }

        public bool Accepted { get; }
        public bool RestartService { get; }
        public long Generation { get; }
    }
}
