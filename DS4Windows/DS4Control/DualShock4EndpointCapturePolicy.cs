using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;

namespace DS4Windows
{
    internal enum DualShock4EndpointCaptureBackend
    {
        StandardLoopback,
        SoftwareRouterPollingLoopback,
    }

    /// <summary>
    /// Selects the conservative endpoint-loopback implementation for physical
    /// render devices and a short polling buffer for positively identified
    /// SteelSeries Sonar software routes. Product text alone is deliberately
    /// insufficient: the endpoint must also expose the ROOT\MEDIA adapter
    /// projection used by the Sonar virtual audio driver.
    /// </summary>
    internal static class DualShock4EndpointCapturePolicy
    {
        internal static DualShock4EndpointCaptureBackend SelectBackend(
            MMDevice endpoint)
        {
            if (endpoint == null)
            {
                return DualShock4EndpointCaptureBackend.StandardLoopback;
            }

            string productIdentity = string.Join(" ",
                GetEndpointText(() => endpoint.FriendlyName),
                GetEndpointText(() => endpoint.DeviceFriendlyName),
                GetEndpointProperty(endpoint,
                    PropertyKeys.PKEY_DeviceInterface_FriendlyName),
                GetEndpointProperty(endpoint,
                    PropertyKeys.PKEY_Device_FriendlyName),
                GetEndpointProperty(endpoint,
                    PropertyKeys.PKEY_Device_DeviceDesc));
            return SelectBackend(productIdentity,
                GetEndpointProperty(endpoint,
                    PropertyKeys.PKEY_Device_InstanceId),
                GetEndpointProperty(endpoint,
                    PropertyKeys.PKEY_Device_ControllerDeviceId),
                GetEndpointProperty(endpoint,
                    PropertyKeys.PKEY_Device_InterfaceKey));
        }

        internal static DualShock4EndpointCaptureBackend SelectBackend(
            string productIdentity, string instanceId,
            string controllerDeviceId, string interfaceKey)
        {
            if (!HasSteelSeriesSonarIdentity(productIdentity))
            {
                return DualShock4EndpointCaptureBackend.StandardLoopback;
            }

            string[] deviceEvidence =
            {
                instanceId ?? string.Empty,
                controllerDeviceId ?? string.Empty,
                interfaceKey ?? string.Empty,
            };
            foreach (string evidence in deviceEvidence)
            {
                if (HasPhysicalAudioAncestry(evidence))
                {
                    return DualShock4EndpointCaptureBackend.StandardLoopback;
                }
            }

            foreach (string evidence in deviceEvidence)
            {
                if (HasSonarSoftwareAdapterEvidence(evidence))
                {
                    return DualShock4EndpointCaptureBackend.
                        SoftwareRouterPollingLoopback;
                }
            }

            return DualShock4EndpointCaptureBackend.StandardLoopback;
        }

        internal static WaveFormat NormalizeCaptureWaveFormat(
            WaveFormat format)
        {
            if (format == null)
            {
                throw new ArgumentNullException(nameof(format));
            }

            // NAudio's WASAPI buffers use this normalized contract. In
            // particular, Sonar commonly exposes extensible IEEE-float PCM;
            // consumers must see IEEE float rather than interpret it as Int32.
            return format.AsStandardWaveFormat();
        }

        internal static string FormatCaptureWaveFormat(WaveFormat format)
        {
            WaveFormat normalized = NormalizeCaptureWaveFormat(format);
            return $"{normalized.SampleRate} Hz, {normalized.Channels} ch, " +
                $"{normalized.BitsPerSample}-bit {normalized.Encoding}";
        }

        private static bool HasSteelSeriesSonarIdentity(string identity)
        {
            string normalized = (identity ?? string.Empty)
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
            return normalized.IndexOf("SteelSeries",
                       StringComparison.OrdinalIgnoreCase) >= 0 &&
                normalized.IndexOf("Sonar",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasSonarSoftwareAdapterEvidence(string evidence)
        {
            string normalized = NormalizeDeviceEvidence(evidence);
            // SWD\MMDEVAPI is the leaf identity of ordinary Windows audio
            // endpoints too, including endpoints backed by physical devices.
            // It is therefore not independent evidence of a software router.
            // Sonar's controller-device and interface projections both name
            // its ROOT\MEDIA adapter; require that narrower evidence.
            return normalized.IndexOf(@"\ROOT\MEDIA\",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@".ROOT\MEDIA\",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasPhysicalAudioAncestry(string evidence)
        {
            string normalized = NormalizeDeviceEvidence(evidence);
            return normalized.IndexOf(@"\USB\",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\HDAUDIO\",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf(@"\BTH",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeDeviceEvidence(string evidence)
        {
            string normalized = (evidence ?? string.Empty)
                .Replace('#', '\\')
                .Replace('/', '\\');
            return "\\" + normalized.Trim('\\') + "\\";
        }

        private static string GetEndpointText(Func<string> valueFactory)
        {
            try
            {
                return valueFactory?.Invoke() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetEndpointProperty(MMDevice endpoint,
            PropertyKey propertyKey)
        {
            try
            {
                return endpoint?.Properties[propertyKey]?.Value?.ToString() ??
                    string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Shared-mode loopback with the same bounded polling strategy used by the
    /// processed application-route capture. The short requested buffer avoids
    /// the roughly 100 ms burst behavior of NAudio's default polling capture.
    /// </summary>
    internal sealed class DualShock4SoftwareRouterLoopbackCapture :
        WasapiCapture
    {
        internal const int RequestedBufferMilliseconds = 4;
        internal const AudioClientStreamFlags CaptureStreamFlags =
            AudioClientStreamFlags.Loopback |
            AudioClientStreamFlags.AutoConvertPcm |
            AudioClientStreamFlags.SrcDefaultQuality;

        internal DualShock4SoftwareRouterLoopbackCapture(MMDevice endpoint) :
            base(endpoint, false, RequestedBufferMilliseconds)
        {
        }

        public override WaveFormat WaveFormat
        {
            get => DualShock4EndpointCapturePolicy.
                NormalizeCaptureWaveFormat(base.WaveFormat);
            set => base.WaveFormat = value;
        }

        protected override AudioClientStreamFlags
            GetAudioClientStreamFlags() => CaptureStreamFlags;
    }
}
