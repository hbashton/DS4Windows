/*
DS4Windows
Copyright (C) 2026 DS4Windows contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DS4Windows
{
    internal enum GameDetectionEvidence
    {
        None,
        WindowsGameRecord,
        InstalledGameManifest,
        FullscreenDirect3D,
    }

    internal sealed class GameAudioCandidate
    {
        public int ProcessId { get; init; }
        public string ExecutableName { get; init; } = string.Empty;
        public string ProcessPath { get; init; } = string.Empty;
        public string WindowTitle { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public GameDetectionEvidence Evidence { get; init; }
        public bool HasActiveAudio { get; init; }
        public bool IsForeground { get; init; }
        public int Score { get; init; }

        public string EvidenceDescription => Evidence switch
        {
            GameDetectionEvidence.WindowsGameRecord => "Windows game record",
            GameDetectionEvidence.InstalledGameManifest => "installed-game manifest",
            GameDetectionEvidence.FullscreenDirect3D => "fullscreen Direct3D",
            _ => "game detector",
        };
    }

    /// <summary>
    /// Selects the process whose audio should drive Audio Haptics. Windows has
    /// no supported API that classifies an arbitrary PID as a game: the old
    /// Game Mode API reports only the calling process and is deprecated. This
    /// detector therefore combines Windows' own read-only Game DVR records,
    /// installed launcher manifests, active Core Audio sessions, the foreground
    /// window, and the documented fullscreen Direct3D shell signal.
    /// </summary>
    internal sealed class AutomaticGameAudioDetector
    {
        private const int CurrentProcessRetentionPoints = 35;
        private static readonly HashSet<string> ExcludedExecutables = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "applicationframehost", "audiodg", "brave", "cef", "chrome",
            "crashreportclient", "discord", "ds4windows", "eadesktop",
            "epicgameslauncher", "firefox",
            "explorer", "gamebar", "gamebarftserver", "gamingservices",
            "msedge", "obs32", "obs64", "opera", "overwolf", "rundll32",
            "searchhost",
            "spotify", "steam", "steamwebhelper", "taskhostw",
            "unitycrashhandler32", "unitycrashhandler64", "upc", "updater",
        };

        public bool TryDetect(int currentProcessId,
            out GameAudioCandidate candidate)
        {
            candidate = null;
            try
            {
                int foregroundProcessId = GetForegroundProcessId();
                bool fullscreenDirect3D = IsFullscreenDirect3DActive();
                Dictionary<int, bool> audioProcesses = GetAudioProcesses();
                if (foregroundProcessId > 0 &&
                    !audioProcesses.ContainsKey(foregroundProcessId))
                {
                    audioProcesses[foregroundProcessId] = false;
                }

                InstalledGameCatalog catalog = InstalledGameCatalog.Current;
                List<GameAudioCandidate> candidates = new();
                foreach ((int processId, bool activeAudio) in audioProcesses)
                {
                    GameAudioCandidate item = InspectProcess(processId,
                        activeAudio, processId == foregroundProcessId,
                        fullscreenDirect3D, catalog);
                    if (item != null)
                    {
                        candidates.Add(item);
                    }
                }

                if (candidates.Count == 0)
                {
                    return false;
                }

                GameAudioCandidate best = candidates
                    .OrderByDescending(item => item.Score)
                    .ThenByDescending(item => item.IsForeground)
                    .ThenByDescending(item => item.HasActiveAudio)
                    .First();
                GameAudioCandidate current = candidates.FirstOrDefault(
                    item => item.ProcessId == currentProcessId);
                if (current != null && current.Score +
                    CurrentProcessRetentionPoints >= best.Score)
                {
                    best = current;
                }

                candidate = best;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static GameAudioCandidate ScoreCandidate(int processId,
            string executableName, string processPath, string windowTitle,
            bool hasActiveAudio, bool isForeground, bool fullscreenDirect3D,
            GameDetectionEvidence catalogEvidence, string catalogName)
        {
            executableName = (executableName ?? string.Empty).Trim();
            string processBaseName = Path.GetFileNameWithoutExtension(
                executableName);
            if (ExcludedExecutables.Contains(processBaseName) ||
                LooksLikeHelper(processBaseName))
            {
                return null;
            }

            GameDetectionEvidence evidence = catalogEvidence;
            int score = catalogEvidence switch
            {
                GameDetectionEvidence.WindowsGameRecord => 500,
                GameDetectionEvidence.InstalledGameManifest => 350,
                _ => 0,
            };

            if (evidence == GameDetectionEvidence.None && isForeground &&
                fullscreenDirect3D)
            {
                evidence = GameDetectionEvidence.FullscreenDirect3D;
                score = 300;
            }
            if (evidence == GameDetectionEvidence.None)
            {
                return null;
            }

            if (hasActiveAudio) score += 55;
            if (isForeground) score += 75;
            if (!string.IsNullOrWhiteSpace(windowTitle)) score += 5;

            string displayName = !string.IsNullOrWhiteSpace(catalogName)
                ? catalogName
                : !string.IsNullOrWhiteSpace(windowTitle)
                    ? windowTitle : processBaseName;
            return new GameAudioCandidate
            {
                ProcessId = processId,
                ExecutableName = processBaseName,
                ProcessPath = processPath ?? string.Empty,
                WindowTitle = windowTitle ?? string.Empty,
                DisplayName = displayName,
                Evidence = evidence,
                HasActiveAudio = hasActiveAudio,
                IsForeground = isForeground,
                Score = score,
            };
        }

        private static GameAudioCandidate InspectProcess(int processId,
            bool activeAudio, bool isForeground, bool fullscreenDirect3D,
            InstalledGameCatalog catalog)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return null;
                }
                string executable = string.Empty;
                string path = string.Empty;
                string title = string.Empty;
                try { executable = process.ProcessName; } catch { }
                try { path = process.MainModule?.FileName ?? string.Empty; }
                catch { }
                try { title = process.MainWindowTitle ?? string.Empty; }
                catch { }
                GameDetectionEvidence evidence = catalog.Match(path,
                    executable, title, out string catalogName);
                return ScoreCandidate(processId, executable, path, title,
                    activeAudio, isForeground, fullscreenDirect3D, evidence,
                    catalogName);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<int, bool> GetAudioProcesses()
        {
            Dictionary<int, bool> result = new();
            using MMDeviceEnumerator enumerator = new();
            MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active);
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
                            if (session.State == AudioSessionState
                                .AudioSessionStateExpired)
                            {
                                continue;
                            }
                            int processId = unchecked((int)session.GetProcessID);
                            if (processId <= 0) continue;
                            bool active = session.State == AudioSessionState
                                .AudioSessionStateActive;
                            result[processId] = result.TryGetValue(processId,
                                out bool previous) ? previous || active : active;
                        }
                    }
                    finally { manager.Dispose(); }
                }
                catch
                {
                    // An endpoint can disappear while Windows rebuilds the
                    // audio graph. Other active endpoints remain useful.
                }
                finally { endpoint.Dispose(); }
            }
            return result;
        }

        private static bool LooksLikeHelper(string executableName)
        {
            string value = executableName?.ToLowerInvariant() ?? string.Empty;
            return value.Contains("crashpad") || value.Contains("crashreport") ||
                value.Contains("easyanticheat") || value.Contains("battleye") ||
                value.Contains("unins") || value.Contains("redist") ||
                value.EndsWith("launcher") || value.EndsWith("updater");
        }

        private static int GetForegroundProcessId()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return 0;
            }
            GetWindowThreadProcessId(window, out uint processId);
            return unchecked((int)processId);
        }

        private static bool IsFullscreenDirect3DActive() =>
            SHQueryUserNotificationState(out QueryUserNotificationState state)
                >= 0 && state ==
                QueryUserNotificationState.RunningDirect3DFullScreen;

        private enum QueryUserNotificationState
        {
            NotPresent = 1,
            Busy = 2,
            RunningDirect3DFullScreen = 3,
            PresentationMode = 4,
            AcceptsNotifications = 5,
            QuietTime = 6,
            App = 7,
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window,
            out uint processId);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(
            out QueryUserNotificationState state);
    }

    internal sealed class InstalledGameCatalog
    {
        private static readonly object CacheLock = new();
        private static InstalledGameCatalog cached;
        private static DateTime cacheExpiresUtc;
        private readonly Dictionary<string, (string Name,
            GameDetectionEvidence Evidence)> exactPaths = new(
                StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Name,
            GameDetectionEvidence Evidence)> executableNames = new(
                StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> ambiguousExecutables = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly List<(string SearchName, string DisplayName)>
            windowTitles = new();
        private readonly List<(string Root, string Name)> installRoots = new();

        public static InstalledGameCatalog Current
        {
            get
            {
                lock (CacheLock)
                {
                    if (cached == null || DateTime.UtcNow >= cacheExpiresUtc)
                    {
                        cached = Build();
                        cacheExpiresUtc = DateTime.UtcNow.AddMinutes(5);
                    }
                    return cached;
                }
            }
        }

        internal static InstalledGameCatalog FromEntries(
            IEnumerable<(string Path, string Name,
                GameDetectionEvidence Evidence)> entries)
        {
            InstalledGameCatalog catalog = new();
            foreach ((string path, string name,
                GameDetectionEvidence evidence) in entries)
            {
                if (evidence == GameDetectionEvidence.WindowsGameRecord)
                {
                    catalog.AddExact(path, name, windowsRecord: true);
                }
                else
                {
                    catalog.AddRoot(path, name);
                }
            }
            return catalog;
        }

        public GameDetectionEvidence Match(string path, string executableName,
            string windowTitle, out string displayName)
        {
            displayName = string.Empty;
            string normalized = NormalizePath(path);
            if (exactPaths.TryGetValue(normalized,
                out (string Name, GameDetectionEvidence Evidence) exact))
            {
                displayName = exact.Name;
                return exact.Evidence;
            }

            foreach ((string root, string name) in installRoots)
            {
                if (IsUnderRoot(normalized, root))
                {
                    displayName = name;
                    return GameDetectionEvidence.InstalledGameManifest;
                }
            }

            string executableBase = Path.GetFileNameWithoutExtension(
                executableName ?? string.Empty);
            if (!ambiguousExecutables.Contains(executableBase) &&
                executableNames.TryGetValue(executableBase,
                    out (string Name, GameDetectionEvidence Evidence) known))
            {
                displayName = known.Name;
                return known.Evidence;
            }

            string searchableTitle = NormalizeTitle(windowTitle);
            if (!string.IsNullOrWhiteSpace(searchableTitle))
            {
                foreach ((string searchName, string name) in windowTitles)
                {
                    if (($" {searchableTitle} ").Contains($" {searchName} ",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = name;
                        return GameDetectionEvidence.InstalledGameManifest;
                    }
                }
            }
            return GameDetectionEvidence.None;
        }

        private static InstalledGameCatalog Build()
        {
            InstalledGameCatalog catalog = new();
            catalog.ReadWindowsGameRecords();
            catalog.ReadSteamManifests();
            catalog.ReadEpicManifests();
            catalog.ReadGogRegistrations();
            return catalog;
        }

        private void ReadWindowsGameRecords()
        {
            try
            {
                using RegistryKey children = Registry.CurrentUser.OpenSubKey(
                    @"System\GameConfigStore\Children");
                foreach (string childName in children?.GetSubKeyNames() ??
                    Array.Empty<string>())
                {
                    using RegistryKey child = children.OpenSubKey(childName);
                    string path = child?.GetValue("MatchedExeFullPath") as string;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        AddExact(path, Path.GetFileNameWithoutExtension(path),
                            windowsRecord: true);
                    }
                }
            }
            catch { }
        }

        private void ReadSteamManifests()
        {
            HashSet<string> steamRoots = new(StringComparer.OrdinalIgnoreCase);
            AddSteamRegistryRoot(steamRoots, Registry.CurrentUser,
                @"Software\Valve\Steam");
            AddSteamRegistryRoot(steamRoots, Registry.LocalMachine,
                @"Software\WOW6432Node\Valve\Steam");
            steamRoots.Add(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            if (Global.UseCustomSteamFolder &&
                !string.IsNullOrWhiteSpace(Global.CustomSteamFolder))
            {
                string custom = Global.CustomSteamFolder;
                DirectoryInfo info = new(custom);
                steamRoots.Add(info.Name.Equals("common",
                    StringComparison.OrdinalIgnoreCase) ?
                    info.Parent?.Parent?.FullName ?? custom : custom);
            }

            foreach (string steamRoot in steamRoots.ToArray())
            {
                string librariesFile = Path.Combine(steamRoot, "steamapps",
                    "libraryfolders.vdf");
                try
                {
                    string text = File.ReadAllText(librariesFile);
                    foreach (Match match in Regex.Matches(text,
                        "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\""))
                    {
                        steamRoots.Add(match.Groups["path"].Value
                            .Replace("\\\\", "\\"));
                    }
                }
                catch { }
            }

            foreach (string root in steamRoots)
            {
                string steamApps = Directory.Exists(Path.Combine(root,
                    "steamapps")) ? Path.Combine(root, "steamapps") : root;
                try
                {
                    foreach (string manifest in Directory.EnumerateFiles(
                        steamApps, "appmanifest_*.acf",
                        SearchOption.TopDirectoryOnly))
                    {
                        string text = File.ReadAllText(manifest);
                        string installDir = VdfValue(text, "installdir");
                        string name = VdfValue(text, "name");
                        if (!string.IsNullOrWhiteSpace(installDir))
                        {
                            AddRoot(Path.Combine(steamApps, "common",
                                installDir), name);
                        }
                    }
                }
                catch { }
            }
        }

        private void ReadEpicManifests()
        {
            string manifestRoot = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData), "Epic",
                "EpicGamesLauncher", "Data", "Manifests");
            try
            {
                foreach (string manifest in Directory.EnumerateFiles(
                    manifestRoot, "*.item", SearchOption.TopDirectoryOnly))
                {
                    using JsonDocument document = JsonDocument.Parse(
                        File.ReadAllText(manifest));
                    JsonElement root = document.RootElement;
                    string location = JsonString(root, "InstallLocation");
                    string launchExecutable = JsonString(root,
                        "LaunchExecutable");
                    string name = JsonString(root, "DisplayName");
                    AddRoot(location, name);
                    if (!string.IsNullOrWhiteSpace(location) &&
                        !string.IsNullOrWhiteSpace(launchExecutable))
                    {
                        AddExact(Path.Combine(location, launchExecutable), name,
                            windowsRecord: false);
                    }
                }
            }
            catch { }
        }

        private void ReadGogRegistrations()
        {
            ReadGogRoot(Registry.LocalMachine,
                @"Software\WOW6432Node\GOG.com\Games");
            ReadGogRoot(Registry.LocalMachine, @"Software\GOG.com\Games");
            ReadGogRoot(Registry.CurrentUser, @"Software\GOG.com\Games");
        }

        private void ReadGogRoot(RegistryKey hive, string keyPath)
        {
            try
            {
                using RegistryKey games = hive.OpenSubKey(keyPath);
                foreach (string childName in games?.GetSubKeyNames() ??
                    Array.Empty<string>())
                {
                    using RegistryKey game = games.OpenSubKey(childName);
                    string path = game?.GetValue("path") as string;
                    string name = game?.GetValue("gameName") as string;
                    AddRoot(path, name);
                }
            }
            catch { }
        }

        private static void AddSteamRegistryRoot(HashSet<string> roots,
            RegistryKey hive, string keyPath)
        {
            try
            {
                using RegistryKey steam = hive.OpenSubKey(keyPath);
                string path = steam?.GetValue("SteamPath") as string ??
                    steam?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
            catch { }
        }

        private void AddExact(string path, string name, bool windowsRecord)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized)) return;
            string executable = Path.GetFileNameWithoutExtension(normalized);
            string displayName = string.IsNullOrWhiteSpace(name) ? executable :
                name.Trim();
            GameDetectionEvidence evidence = windowsRecord
                ? GameDetectionEvidence.WindowsGameRecord
                : GameDetectionEvidence.InstalledGameManifest;
            exactPaths[normalized] = (displayName, evidence);
            if (executableNames.TryGetValue(executable, out var existing) &&
                !string.Equals(existing.Name, displayName,
                    StringComparison.OrdinalIgnoreCase))
            {
                ambiguousExecutables.Add(executable);
            }
            else
            {
                if (!executableNames.TryGetValue(executable,
                        out existing) ||
                    existing.Evidence !=
                        GameDetectionEvidence.WindowsGameRecord)
                {
                    executableNames[executable] = (displayName, evidence);
                }
            }
            AddWindowTitle(displayName);
        }

        private void AddRoot(string path, string name)
        {
            string normalized = NormalizePath(path).TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(normalized) ||
                installRoots.Any(item => string.Equals(item.Root, normalized,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            installRoots.Add((normalized,
                string.IsNullOrWhiteSpace(name) ?
                    new DirectoryInfo(normalized).Name : name.Trim()));
            AddWindowTitle(string.IsNullOrWhiteSpace(name)
                ? new DirectoryInfo(normalized).Name : name.Trim());
        }

        private void AddWindowTitle(string displayName)
        {
            string searchName = NormalizeTitle(displayName);
            if (searchName.Length < 5 || windowTitles.Any(item =>
                string.Equals(item.SearchName, searchName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            windowTitles.Add((searchName, displayName));
        }

        private static string NormalizeTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ")
                .Trim();
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return Path.GetFullPath(path.Trim()).TrimEnd('\\'); }
            catch { return path.Trim().TrimEnd('\\'); }
        }

        private static bool IsUnderRoot(string path, string root) =>
            !string.IsNullOrWhiteSpace(path) &&
            (string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));

        private static string VdfValue(string text, string key)
        {
            Match match = Regex.Match(text ?? string.Empty,
                $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"",
                RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["value"].Value : string.Empty;
        }

        private static string JsonString(JsonElement element,
            string propertyName) => element.TryGetProperty(propertyName,
            out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;
    }
}
