using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DS4Windows;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    internal sealed class AudioEndpointSnapshot
    {
        public string Name { get; }
        public string EndpointId { get; }
        public bool IsControllerAudio { get; }
        public ControllerAudioEndpointKind ControllerKind { get; }
        public int UsbipPort { get; }

        public AudioEndpointSnapshot(string name, string endpointId,
            bool isControllerAudio,
            ControllerAudioEndpointKind controllerKind =
                ControllerAudioEndpointKind.Any,
            int usbipPort = -1)
        {
            Name = name ?? string.Empty;
            EndpointId = endpointId ?? string.Empty;
            IsControllerAudio = isControllerAudio;
            ControllerKind = controllerKind;
            UsbipPort = usbipPort;
        }
    }

    internal sealed class AppAudioSnapshot
    {
        public AppAudioSnapshot(string name, int processId)
        {
            Name = name ?? string.Empty;
            ProcessId = processId;
        }

        public string Name { get; }
        public int ProcessId { get; }
    }

    /// <summary>
    /// Keeps slow Windows Core Audio and property-store access off WPF's
    /// dispatcher. A profile editor used to enumerate render endpoints three
    /// times and capture endpoints once while its bindings were being attached.
    /// Some audio drivers take hundreds of milliseconds to answer an individual
    /// property query, which made the whole window appear hung.
    /// </summary>
    internal static class AudioEndpointChoiceCache
    {
        private static readonly object syncRoot = new object();
        private static readonly TimeSpan cacheLifetime = TimeSpan.FromSeconds(10);
        private static IReadOnlyList<AudioEndpointSnapshot> renderEndpoints =
            Array.Empty<AudioEndpointSnapshot>();
        private static IReadOnlyList<AudioEndpointSnapshot> captureEndpoints =
            Array.Empty<AudioEndpointSnapshot>();
        private static IReadOnlyList<AppAudioSnapshot> appAudioSessions =
            Array.Empty<AppAudioSnapshot>();
        private static DateTime refreshedAtUtc = DateTime.MinValue;
        private static Task refreshTask;

        public static IReadOnlyList<AudioEndpointSnapshot> RenderEndpoints
        {
            get
            {
                lock (syncRoot)
                {
                    return renderEndpoints;
                }
            }
        }

        public static IReadOnlyList<AudioEndpointSnapshot> CaptureEndpoints
        {
            get
            {
                lock (syncRoot)
                {
                    return captureEndpoints;
                }
            }
        }

        public static IReadOnlyList<AppAudioSnapshot> AppAudioSessions
        {
            get
            {
                lock (syncRoot)
                {
                    return appAudioSessions;
                }
            }
        }

        public static List<AudioEndpointChoice> BuildControllerAudioChoices(
            string savedEndpointId,
            OutContType automaticOutputType = OutContType.None,
            int automaticUsbipPort = -1)
        {
            var choices = new List<AudioEndpointChoice>
            {
                new(BuildAutomaticControllerAudioName(automaticOutputType,
                    automaticUsbipPort), string.Empty),
                new("Default · all system audio",
                    DualSenseAudioPassthrough.DefaultSystemAudioEndpointId),
            };

            var appEndpointIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (AppAudioSnapshot app in AppAudioSessions)
            {
                int rootProcessId = ProcessLoopbackWaveCapture
                    .ResolveCaptureRootProcessId(app.ProcessId);
                string endpointId = ProcessLoopbackWaveCapture
                    .BuildEndpointId(rootProcessId > 0 ? rootProcessId :
                        app.ProcessId);
                if (!appEndpointIds.Add(endpointId))
                {
                    continue;
                }
                choices.Add(new AudioEndpointChoice(
                    $"{app.Name} · app only",
                    endpointId));
            }

            foreach (AudioEndpointSnapshot endpoint in RenderEndpoints)
            {
                string name = endpoint.Name;
                if (endpoint.IsControllerAudio)
                {
                    name += " · controller/game audio";
                }

                choices.Add(new AudioEndpointChoice(name,
                    endpoint.EndpointId));
            }

            if (!string.IsNullOrEmpty(savedEndpointId) &&
                choices.All(item => !string.Equals(item.EndpointId,
                    savedEndpointId, StringComparison.Ordinal)))
            {
                string name = ProcessLoopbackWaveCapture
                    .IsProcessEndpointId(savedEndpointId)
                    ? "Selected app · unavailable"
                    : "Saved endpoint · unavailable";
                choices.Add(new AudioEndpointChoice(name, savedEndpointId));
            }

            return choices;
        }

        internal static string BuildAutomaticControllerAudioName(
            OutContType outputType, int preferredUsbipPort,
            IReadOnlyList<AudioEndpointSnapshot> endpoints = null)
        {
            endpoints ??= RenderEndpoints;
            ControllerAudioEndpointKind preferredKind =
                DualSenseAudioPassthrough.GetEndpointKind(outputType);

            AudioEndpointSnapshot endpoint = endpoints
                .Where(item => item.IsControllerAudio)
                .OrderByDescending(item => preferredUsbipPort >= 0 &&
                    item.UsbipPort == preferredUsbipPort)
                .ThenByDescending(item => preferredKind ==
                        ControllerAudioEndpointKind.Any ||
                    item.ControllerKind == preferredKind)
                .FirstOrDefault();

            return endpoint == null || string.IsNullOrWhiteSpace(endpoint.Name)
                ? "Game Audio (virtual controller endpoint)"
                : $"Game Audio ({endpoint.Name})";
        }

        public static Task RefreshAsync(bool forceRefresh = false)
        {
            lock (syncRoot)
            {
                if (refreshTask != null && !refreshTask.IsCompleted)
                {
                    return refreshTask;
                }

                if (!forceRefresh &&
                    DateTime.UtcNow - refreshedAtUtc < cacheLifetime)
                {
                    return Task.CompletedTask;
                }

                refreshTask = Task.Run(RefreshCore);
                return refreshTask;
            }
        }

        private static void RefreshCore()
        {
            var newRenderEndpoints = new List<AudioEndpointSnapshot>();
            var newCaptureEndpoints = new List<AudioEndpointSnapshot>();
            var newAppAudioSessions = new List<AppAudioSnapshot>();

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                CopyEndpoints(enumerator, DataFlow.Render, newRenderEndpoints);
                CopyEndpoints(enumerator, DataFlow.Capture, newCaptureEndpoints);
                CopyAppAudioSessions(enumerator, newAppAudioSessions);
            }
            catch
            {
                // Keep the last known snapshot. Audio endpoint availability can
                // change while Windows is rebuilding its device graph.
                return;
            }

            lock (syncRoot)
            {
                renderEndpoints = newRenderEndpoints;
                captureEndpoints = newCaptureEndpoints;
                appAudioSessions = newAppAudioSessions;
                refreshedAtUtc = DateTime.UtcNow;
            }
        }

        private static void CopyAppAudioSessions(MMDeviceEnumerator enumerator,
            List<AppAudioSnapshot> destination)
        {
            MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active);
            var seen = new HashSet<int>();
            foreach (MMDevice endpoint in endpoints)
            {
                try
                {
                    AudioSessionManager manager = endpoint.AudioSessionManager;
                    try
                    {
                        SessionCollection sessions = manager.Sessions;
                        for (int index = 0; index < sessions.Count; index++)
                        {
                            using AudioSessionControl session = sessions[index];
                            if (session.State ==
                                AudioSessionState.AudioSessionStateExpired)
                            {
                                continue;
                            }

                            int processId = checked((int)session.GetProcessID);
                            if (processId <= 0 || !seen.Add(processId))
                            {
                                continue;
                            }

                            string displayName = session.DisplayName;
                            try
                            {
                                using Process process = Process.GetProcessById(
                                    processId);
                                if (string.IsNullOrWhiteSpace(displayName))
                                {
                                    displayName = process.MainWindowTitle;
                                }
                                if (string.IsNullOrWhiteSpace(displayName))
                                {
                                    displayName = process.ProcessName;
                                }
                            }
                            catch { }

                            destination.Add(new AppAudioSnapshot(
                                string.IsNullOrWhiteSpace(displayName)
                                    ? $"Process {processId}"
                                    : displayName.Trim(), processId));
                        }
                    }
                    finally
                    {
                        manager.Dispose();
                    }
                }
                catch
                {
                    // An application can move between render endpoints while
                    // this snapshot is being built. Keep sessions from the
                    // remaining active endpoints and let Refresh retry later.
                }
                finally
                {
                    endpoint?.Dispose();
                }
            }

            destination.Sort((left, right) => string.Compare(left.Name,
                right.Name, StringComparison.CurrentCultureIgnoreCase));
        }

        private static void CopyEndpoints(MMDeviceEnumerator enumerator,
            DataFlow flow, List<AudioEndpointSnapshot> destination)
        {
            MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(
                flow, DeviceState.Active);
            foreach (MMDevice endpoint in endpoints)
            {
                try
                {
                    string name = endpoint.FriendlyName ?? string.Empty;
                    string id = endpoint.ID ?? string.Empty;
                    bool controllerAudio = flow == DataFlow.Render &&
                        DualSenseAudioPassthrough.IsControllerAudioEndpoint(endpoint);
                    ControllerAudioEndpointKind controllerKind = controllerAudio
                        ? DualSenseAudioPassthrough.ClassifyEndpointIdentity(
                            $"{name} {id}")
                        : ControllerAudioEndpointKind.Any;
                    int usbipPort = controllerAudio &&
                        DualSenseAudioPassthrough.TryGetEndpointUsbipPort(
                            endpoint, out int resolvedUsbipPort)
                        ? resolvedUsbipPort
                        : -1;
                    destination.Add(new AudioEndpointSnapshot(name, id,
                        controllerAudio, controllerKind, usbipPort));
                }
                catch
                {
                    // A single disappearing endpoint must not discard the
                    // rest of the device snapshot.
                }
                finally
                {
                    endpoint?.Dispose();
                }
            }
        }
    }
}
