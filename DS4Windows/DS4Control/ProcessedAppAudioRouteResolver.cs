using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

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
            return FindExclusiveEndpoint(endpoints, endpoint =>
                TryGetExclusiveTargetPeak(endpoint, targetRoot, out float peak)
                    ? peak : (float?)null);
        }

        internal static TEndpoint FindExclusiveEndpoint<TEndpoint>(
            IEnumerable<TEndpoint> endpoints, Func<TEndpoint, float?> readExclusivePeak)
            where TEndpoint : class, IDisposable
        {
            TEndpoint selected = null;
            float selectedPeak = -1.0f;
            try
            {
                // MMDeviceCollection creates new wrappers on each enumeration.
                // Dispose this pass's actual rejected wrappers, not a second
                // enumeration whose objects are unrelated to the selected one.
                foreach (TEndpoint endpoint in endpoints)
                {
                    try
                    {
                        float? targetPeak = readExclusivePeak(endpoint);
                        if (!targetPeak.HasValue || targetPeak.Value <= selectedPeak)
                            continue;

                        TEndpoint previous = selected;
                        selected = endpoint;
                        selectedPeak = targetPeak.Value;
                        previous?.Dispose();
                    }
                    catch
                    {
                        // The audio graph may rebuild while it is enumerated.
                    }
                    finally
                    {
                        if (!ReferenceEquals(endpoint, selected)) endpoint.Dispose();
                    }
                }
            }
            catch
            {
                selected?.Dispose();
                throw;
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

            ReadTargetSessions(endpoint, targetRoot, out _,
                out float targetPeak, out _);
            return targetPeak > 0.0001f;
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

        private static bool TryGetExclusiveTargetPeak(MMDevice endpoint,
            int targetRoot, out float targetPeak)
        {
            ReadTargetSessions(endpoint, targetRoot, out bool targetActive,
                out targetPeak, out bool unrelatedActive);
            return targetActive && !unrelatedActive;
        }

        /// <summary>
        /// A fresh endpoint/session snapshot for each watchdog poll. Do not
        /// compare a retained MMDevice's cached session collection with a
        /// fresh "anywhere" scan: browser session replacement and disposal
        /// can otherwise make the same route look like a different route.
        /// </summary>
        internal static ProcessedAppAudioRouteObservation ObserveRoutes(
            int processId, string currentEndpointId)
        {
            int targetRoot = ProcessLoopbackWaveCapture.ResolveCaptureRootProcessId(processId);
            if (targetRoot <= 0 || string.IsNullOrWhiteSpace(currentEndpointId))
                return default;

            using var enumerator = new MMDeviceEnumerator();
            MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active);
            return ObserveEndpoints(endpoints.Cast<MMDevice>(), currentEndpointId,
                endpoint => endpoint.ID, endpoint =>
                {
                    ReadTargetSessions(endpoint, targetRoot, out bool targetActive,
                        out float targetPeak, out bool unrelatedActive);
                    return (targetActive, targetPeak, unrelatedActive);
                });
        }

        // The production traversal also accepts owned synthetic endpoints so
        // route identity, query failures and disposal can be tested without
        // activating Core Audio or accessing the user's running capture.
        internal static ProcessedAppAudioRouteObservation ObserveEndpoints<TEndpoint>(
            IEnumerable<TEndpoint> endpoints, string currentEndpointId,
            Func<TEndpoint, string> getEndpointId,
            Func<TEndpoint, (bool TargetActive, float TargetPeak, bool UnrelatedActive)> readSessions)
            where TEndpoint : IDisposable
        {
            bool currentKnown = true;
            bool currentAudible = false;
            bool currentUnrelated = false;
            string candidate = string.Empty;
            float selectedPeak = 0.0001f;
            foreach (TEndpoint endpoint in endpoints)
            {
                string endpointId = null;
                bool isCurrent = false;
                try
                {
                    endpointId = getEndpointId(endpoint);
                    isCurrent = string.Equals(endpointId, currentEndpointId,
                        StringComparison.OrdinalIgnoreCase);
                    var route = readSessions(endpoint);
                    if (isCurrent)
                    {
                        currentAudible = route.TargetPeak > 0.0001f;
                        currentUnrelated = route.UnrelatedActive;
                    }
                    if (route.TargetActive && !route.UnrelatedActive && route.TargetPeak > selectedPeak)
                    {
                        candidate = endpointId;
                        selectedPeak = route.TargetPeak;
                    }
                }
                catch
                {
                    if (isCurrent || endpointId == null) currentKnown = false;
                    // Failed observation is not evidence of relocation.
                }
                finally
                {
                    endpoint.Dispose();
                }
            }
            return new ProcessedAppAudioRouteObservation(currentKnown,
                currentAudible, currentUnrelated, candidate);
        }

        private static void ReadTargetSessions(MMDevice endpoint, int targetRoot,
            out bool targetActive, out float targetPeak, out bool unrelatedActive)
        {
            targetActive = false;
            targetPeak = 0.0f;
            unrelatedActive = false;
            // MMDevice caches and owns this manager. Disposing the borrowed
            // property clears its Sessions and poisons every subsequent query.
            // The endpoint owner disposes it, including on query failure.
            AudioSessionManager manager = endpoint.AudioSessionManager;
            SessionCollection sessions = manager.Sessions;
            for (int index = 0; index < sessions.Count; index++)
            {
                using AudioSessionControl session = sessions[index];
                if (session.State != AudioSessionState.AudioSessionStateActive)
                    continue;

                int sessionProcessId = unchecked((int)session.GetProcessID);
                if (sessionProcessId <= 0 || IsCaptureHostProcess(sessionProcessId))
                    continue;

                int sessionRoot = ProcessLoopbackWaveCapture.ResolveCaptureRootProcessId(sessionProcessId);
                if (sessionRoot == targetRoot)
                {
                    targetActive = true;
                    targetPeak = Math.Max(targetPeak,
                        session.AudioMeterInformation.MasterPeakValue);
                }
                else
                {
                    unrelatedActive = true;
                }
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
