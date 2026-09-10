using System;
using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace DS4Windows
{
    internal readonly struct ProcessedAppAudioRouteObservation
    {
        internal ProcessedAppAudioRouteObservation(bool currentRouteKnown,
            bool currentRouteAudible, bool currentRouteHasUnrelatedSession,
            string audibleExclusiveEndpointId)
        {
            CurrentRouteKnown = currentRouteKnown;
            CurrentRouteAudible = currentRouteAudible;
            CurrentRouteHasUnrelatedSession = currentRouteHasUnrelatedSession;
            AudibleExclusiveEndpointId = audibleExclusiveEndpointId ?? string.Empty;
        }

        internal bool CurrentRouteKnown { get; }
        internal bool CurrentRouteAudible { get; }
        internal bool CurrentRouteHasUnrelatedSession { get; }
        internal string AudibleExclusiveEndpointId { get; }
    }

    internal enum ProcessedAppAudioRouteRecoveryReason
    {
        None,
        CallbackStalled,
        Relocated,
        NoLongerExclusive,
    }

    /// <summary>
    /// Debounces positive route identity evidence; silence or failed meter
    /// queries alone cannot establish that the selected application moved.
    /// Owned by one capture watchdog, with no audio device or timer access.
    /// </summary>
    internal sealed class ProcessedAppAudioRouteRecovery
    {
        private string proposedEndpointId = string.Empty;
        private long proposedAt;

        internal ProcessedAppAudioRouteRecoveryReason Observe(string currentEndpointId,
            ProcessedAppAudioRouteObservation observation, long lastCallbackTimestamp,
            long now)
        {
            if (!observation.CurrentRouteKnown)
            {
                Reset();
                return ProcessedAppAudioRouteRecoveryReason.None;
            }
            if (observation.CurrentRouteHasUnrelatedSession)
            {
                Reset();
                return ProcessedAppAudioRouteRecoveryReason.NoLongerExclusive;
            }
            if (ProcessLoopbackWaveCapture.ShouldRecoverProcessedRoute(
                lastCallbackTimestamp, now, observation.CurrentRouteAudible))
            {
                Reset();
                return ProcessedAppAudioRouteRecoveryReason.CallbackStalled;
            }
            string candidate = observation.AudibleExclusiveEndpointId;
            if (observation.CurrentRouteAudible || string.IsNullOrWhiteSpace(candidate) ||
                string.IsNullOrWhiteSpace(currentEndpointId) || string.Equals(candidate,
                    currentEndpointId, StringComparison.OrdinalIgnoreCase))
            {
                Reset();
                return ProcessedAppAudioRouteRecoveryReason.None;
            }
            if (!string.Equals(candidate, proposedEndpointId, StringComparison.OrdinalIgnoreCase) ||
                now < proposedAt)
            {
                proposedEndpointId = candidate;
                proposedAt = now;
                return ProcessedAppAudioRouteRecoveryReason.None;
            }
            return now - proposedAt >= Stopwatch.Frequency *
                ProcessLoopbackWaveCapture.ProcessedRouteStallMilliseconds / 1000
                ? ProcessedAppAudioRouteRecoveryReason.Relocated
                : ProcessedAppAudioRouteRecoveryReason.None;
        }

        private void Reset()
        {
            proposedEndpointId = string.Empty;
            proposedAt = 0;
        }

        internal static string SelectCompatibleEndpoint(string endpointId,
            WaveFormat expected, WaveFormat actual)
        {
            // IWaveIn consumers already own decoders for the original format.
            // A new endpoint must match it exactly, otherwise the existing
            // process-loopback path requests that format with engine conversion.
            return !string.IsNullOrWhiteSpace(endpointId) && FormatsMatch(expected, actual)
                ? endpointId : string.Empty;
        }

        internal static bool FormatsMatch(WaveFormat expected, WaveFormat actual)
        {
            if (expected == null || actual == null) return false;
            // NAudio2.2.1 does not expose extensible valid-bits/channel-mask
            // properties. Its public serializer includes those and SubFormat;
            // comparing only Encoding=Extensible can mix PCM with IEEE float.
            return string.Equals(FormatIdentity(expected), FormatIdentity(actual),
                StringComparison.Ordinal);
        }

        internal static string FormatIdentity(WaveFormat format)
        {
            using var stream = new MemoryStream(64);
            using var writer = new BinaryWriter(stream);
            format.Serialize(writer);
            return Convert.ToHexString(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        }
    }
}
