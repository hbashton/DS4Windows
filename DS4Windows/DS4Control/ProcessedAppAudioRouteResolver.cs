using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Diagnostics;

namespace DS4Windows
{
    /// <summary>
    /// Finds the render endpoint carrying only the selected application.
    /// Windows process loopback taps the application before endpoint/session
    /// processing; endpoint loopback provides the exact waveform heard in the
    /// system mix. We only select an endpoint when no unrelated application is
    /// active on it, so the user's per-app selection remains isolated.
    /// </summary>
    internal static class ProcessedAppAudioRouteResolver
    {
        internal static MMDevice FindExclusiveRoute(int processId)
        {
            int targetRoot = ProcessLoopbackWaveCapture
                .ResolveCaptureRootProcessId(processId);
            if (targetRoot <= 0)
            {
                return null;
            }

            using var enumerator = new MMDeviceEnumerator();
            MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active);
            MMDevice selected = null;
            foreach (MMDevice endpoint in endpoints)
            {
                try
                {
                    if (!HasExclusiveTargetSession(endpoint, targetRoot))
                    {
                        continue;
                    }

                    selected = endpoint;
                    break;
                }
                catch
                {
                    // The audio graph may rebuild while it is enumerated.
                }
            }

            foreach (MMDevice endpoint in endpoints)
            {
                if (!ReferenceEquals(endpoint, selected))
                {
                    endpoint.Dispose();
                }
            }

            return selected;
        }

        /// <summary>
        /// Returns true only when an active session belonging to the selected
        /// process tree is currently producing a non-zero signal on this
        /// render route. This lets process capture distinguish an idle app
        /// from a live endpoint whose loopback worker silently stalled.
        /// </summary>
        internal static bool IsTargetRouteAudiblyActive(MMDevice endpoint,
            int processId)
        {
            if (endpoint == null || processId <= 0)
            {
                return false;
            }

            int targetRoot = ProcessLoopbackWaveCapture
                .ResolveCaptureRootProcessId(processId);
            if (targetRoot <= 0)
            {
                return false;
            }

            AudioSessionManager manager = endpoint.AudioSessionManager;
            try
            {
                SessionCollection sessions = manager.Sessions;
                for (int index = 0; index < sessions.Count; index++)
                {
                    using AudioSessionControl session = sessions[index];
                    if (session.State !=
                        AudioSessionState.AudioSessionStateActive)
                    {
                        continue;
                    }

                    int sessionProcessId = unchecked((int)
                        session.GetProcessID);
                    if (sessionProcessId > 0 &&
                        ProcessLoopbackWaveCapture.ResolveCaptureRootProcessId(
                            sessionProcessId) == targetRoot &&
                        session.AudioMeterInformation.MasterPeakValue >
                            0.0001f)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                manager.Dispose();
            }

            return false;
        }

        /// <summary>
        /// Checks every active render route for an audible session belonging
        /// to the selected process tree. Application-loopback is independent
        /// of the final endpoint, so its watchdog must follow browser/game
        /// sessions when Windows or an audio router moves them between
        /// endpoints.
        /// </summary>
        internal static bool IsTargetAudiblyActiveAnywhere(int processId)
        {
            if (processId <= 0)
            {
                return false;
            }

            using var enumerator = new MMDeviceEnumerator();
            MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active);
            bool audible = false;
            foreach (MMDevice endpoint in endpoints)
            {
                try
                {
                    audible |= IsTargetRouteAudiblyActive(endpoint,
                        processId);
                }
                catch
                {
                    // An endpoint may disappear while Windows rebuilds the
                    // graph. Continue checking the remaining routes.
                }
                finally
                {
                    endpoint.Dispose();
                }
            }

            return audible;
        }

        private static bool HasExclusiveTargetSession(MMDevice endpoint,
            int targetRoot)
        {
            AudioSessionManager manager = endpoint.AudioSessionManager;
            try
            {
                SessionCollection sessions = manager.Sessions;
                bool targetActive = false;
                for (int index = 0; index < sessions.Count; index++)
                {
                    using AudioSessionControl session = sessions[index];
                    if (session.State !=
                        AudioSessionState.AudioSessionStateActive)
                    {
                        continue;
                    }

                    int sessionProcessId = unchecked((int)
                        session.GetProcessID);
                    if (sessionProcessId <= 0 ||
                        IsCaptureHostProcess(sessionProcessId))
                    {
                        continue;
                    }

                    int sessionRoot = ProcessLoopbackWaveCapture
                        .ResolveCaptureRootProcessId(sessionProcessId);
                    if (sessionRoot == targetRoot)
                    {
                        targetActive = true;
                    }
                    else
                    {
                        return false;
                    }
                }

                return targetActive;
            }
            finally
            {
                manager.Dispose();
            }
        }

        private static bool IsCaptureHostProcess(int processId)
        {
            if (processId == Environment.ProcessId)
            {
                return true;
            }

            try
            {
                using Process process = Process.GetProcessById(processId);
                return process.ProcessName.StartsWith("DS4Windows",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
