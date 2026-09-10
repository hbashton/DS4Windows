using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    internal sealed class AppAudioSnapshot
    {
        public AppAudioSnapshot(string name, int processId,
            string executableName = "", string processPath = "",
            string sessionIdentifier = "", string sessionInstanceIdentifier = "")
        {
            Name = name ?? string.Empty;
            ProcessId = processId;
            ExecutableName = executableName ?? string.Empty;
            ProcessPath = processPath ?? string.Empty;
            SessionIdentifier = sessionIdentifier ?? string.Empty;
            SessionInstanceIdentifier = sessionInstanceIdentifier ?? string.Empty;
        }

        public string Name { get; }
        public int ProcessId { get; }
        public string ExecutableName { get; }
        public string ProcessPath { get; }
        public string SessionIdentifier { get; }
        public string SessionInstanceIdentifier { get; }
    }

    /// <summary>
    /// A cold UI snapshot shared by both app-source pickers. Per-app routing
    /// (including virtual mixers) need not use the default multimedia endpoint.
    /// No Core Audio objects escape the snapshot's owned endpoint lifetime.
    /// </summary>
    internal static class AppAudioSessionDiscovery
    {
        internal static List<AppAudioSnapshot> Read()
        {
            using var enumerator = new MMDeviceEnumerator();
            return Read(enumerator);
        }

        internal static List<AppAudioSnapshot> Read(MMDeviceEnumerator enumerator) =>
            ReadEndpoints(enumerator.EnumerateAudioEndPoints(DataFlow.Render,
                DeviceState.Active).Cast<MMDevice>(), CopySessions);

        // The same endpoint traversal is exercised without accessing Windows
        // audio in tests. A disappearing endpoint must not hide another route.
        internal static List<AppAudioSnapshot> ReadEndpoints<TEndpoint>(
            IEnumerable<TEndpoint> endpoints,
            Action<TEndpoint, List<AppAudioSnapshot>> copySessions)
            where TEndpoint : IDisposable
        {
            var sessions = new List<AppAudioSnapshot>();
            foreach (TEndpoint endpoint in endpoints)
            {
                try
                {
                    using (endpoint) copySessions(endpoint, sessions);
                }
                catch
                {
                    // Audio routes can disappear during a refresh. Preserve
                    // collected sessions and continue to remaining endpoints.
                }
            }

            return sessions.Where(session => session.ProcessId > 0)
                .GroupBy(session => (session.ProcessId,
                    session.SessionInstanceIdentifier))
                .Select(group => group.First())
                .OrderBy(session => session.Name,
                    StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void CopySessions(MMDevice endpoint,
            List<AppAudioSnapshot> destination)
        {
            SessionCollection sessions = endpoint.AudioSessionManager.Sessions;
            for (int index = 0; index < sessions.Count; index++)
            {
                try
                {
                    using AudioSessionControl session = sessions[index];
                    if (session.State == AudioSessionState.AudioSessionStateExpired)
                        continue;
                    int processId = checked((int)session.GetProcessID);
                    if (processId <= 0) continue;
                    string displayName = session.DisplayName;
                    string executableName = string.Empty;
                    string processPath = string.Empty;
                    try
                    {
                        using Process process = Process.GetProcessById(processId);
                        executableName = process.ProcessName;
                        if (string.IsNullOrWhiteSpace(displayName))
                            displayName = process.MainWindowTitle;
                        if (string.IsNullOrWhiteSpace(displayName))
                            displayName = executableName;
                        // Protected processes can reject this optional lookup
                        // without hiding their otherwise usable audio session.
                        processPath = process.MainModule?.FileName ?? string.Empty;
                    }
                    catch { }

                    destination.Add(new AppAudioSnapshot(
                        string.IsNullOrWhiteSpace(displayName)
                            ? $"Process {processId}" : displayName.Trim(),
                        processId, executableName, processPath,
                        session.GetSessionIdentifier,
                        session.GetSessionInstanceIdentifier));
                }
                catch
                {
                    // One closed session must not hide its endpoint's other apps.
                }
            }
        }
    }
}
