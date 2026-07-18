/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Win32;
using Windows.Foundation.Metadata;
using Windows.Gaming.UI;
using WinRect = System.Windows.Rect;

namespace DS4Windows
{
    public class GameBarIntegration
    {
        private const byte VK_LWIN = 0x5B;
        private const byte VK_G = 0x47;
        private const int KEYEVENTF_KEYUP = 0x0002;
        private const int DWMWA_CLOAKED = 14;
        private const int STARTF_USESHOWWINDOW = 0x00000001;
        private const int STARTF_FORCEOFFFEEDBACK = 0x00000080;
        private const int SW_HIDE = 0;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_TIMEOUT = 0x00000102;
        private const int MaxAutomationDiagnosticRows = 20;
        private const int MaxAutomationVisitCount = 500;
        private const int MaxAutomationDepth = 5;
        private const int MaxDiagnosticTextLength = 160;
        private const int LiveGameBarApiPollMs = 1000;
        private const int LiveGameBarApiCacheMs = 1500;
        private const int LiveGameBarApiProbeTimeoutMs = 1500;
        private const int LiveGameBarApiHangMs = 2500;
        private const int LiveAutomationPollMs = 1000;
        private const int LiveAutomationCacheMs = 3000;
        public const string ProbeArgument = "--ds4windows-gamebar-probe";
        private static readonly object detectionStatusLock = new object();
        private static readonly object gameBarApiPollLock = new object();
        private static bool gameBarApiPollRunning;
        private static bool gameBarApiPollCachedVisible;
        private static DateTime gameBarApiPollLastStartedUtc = DateTime.MinValue;
        private static DateTime gameBarApiPollLastCompletedUtc = DateTime.MinValue;
        private static int gameBarApiPollGeneration;
        private static bool gameBarApiPollLastSupported;
        private static bool gameBarApiPollLastVisible;
        private static bool gameBarApiPollLastInputRedirected;
        private static long gameBarApiPollLastElapsedMs;
        private static string gameBarApiPollLastStatus = "not started";
        private static string lastDetectionSummary = "not checked";
        private static readonly object automationPollLock = new object();
        private static bool automationPollRunning;
        private static bool automationPollCachedVisible;
        private static DateTime automationPollLastStartedUtc = DateTime.MinValue;
        private static DateTime automationPollLastCompletedUtc = DateTime.MinValue;
        private static bool? gameBarProtocolRegistered;
        private static int gameBarMissingWarningLogged;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, UIntPtr dwExtraInfo);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        public static bool TryRunProbeCommand(string[] args)
        {
            if (args == null ||
                args.Length < 2 ||
                !args[0].Equals(ProbeArgument, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            RunProbeCommand(args[1]);
            return true;
        }

        private static void RunProbeCommand(string resultPath)
        {
            bool supported = false;
            bool visible = false;
            bool inputRedirected = false;
            string status = "not started";

            try
            {
                supported = TryGetGameBarApiStateCore(out visible, out inputRedirected, out status);
            }
            catch (Exception ex)
            {
                status = ex.GetType().Name + ": " + ex.Message;
            }

            try
            {
                File.WriteAllText(resultPath,
                    $"{supported}|{visible}|{inputRedirected}|{SanitizeProbeStatus(status)}",
                    Encoding.UTF8);
            }
            catch
            {
            }
        }

        public bool IsRunningElevated()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public string OpenGameBar()
        {
            if (!IsGameBarProtocolRegistered())
            {
                LogMissingGameBarWarning();
                return "Game Bar not opened: ms-gamebar protocol is not registered";
            }

            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_G, 0, 0, UIntPtr.Zero);
            keybd_event(VK_G, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            return "keybd_event Win+G sent";
        }

        private static bool IsGameBarProtocolRegistered()
        {
            if (gameBarProtocolRegistered.HasValue)
            {
                return gameBarProtocolRegistered.Value;
            }

            bool registered = RegistryProtocolExists(Registry.CurrentUser, @"Software\Classes\ms-gamebar") ||
                RegistryProtocolExists(Registry.ClassesRoot, "ms-gamebar");
            gameBarProtocolRegistered = registered;
            return registered;
        }

        private static bool RegistryProtocolExists(RegistryKey root, string subKeyName)
        {
            try
            {
                using RegistryKey key = root.OpenSubKey(subKeyName);
                if (key == null)
                {
                    return false;
                }

                object urlProtocol = key.GetValue("URL Protocol");
                return urlProtocol != null ||
                    !string.IsNullOrWhiteSpace(key.GetValue(null) as string);
            }
            catch
            {
                return false;
            }
        }

        private static void LogMissingGameBarWarning()
        {
            if (Interlocked.Exchange(ref gameBarMissingWarningLogged, 1) == 1)
            {
                return;
            }

            const string message = "Xbox Game Bar is not installed or its ms-gamebar protocol handler is not registered. Install or repair Xbox Game Bar from the Microsoft Store, then restart DS4Windows to use Game Bar profile support.";
            AppLogger.LogToGui(message, true);
            AppLogger.LogToTray(message, true, true);
        }

        public string LastDetectionSummary
        {
            get
            {
                lock (detectionStatusLock)
                {
                    return lastDetectionSummary;
                }
            }
        }

        public bool IsGameBarVisible()
        {
            bool apiVisible = IsGameBarVisibleByCachedGameBarApi(false, out _);
            bool windowVisible = IsGameBarVisibleByWindowEnumeration();
            bool visible = apiVisible || windowVisible;
            string source = apiVisible ? "api" : windowVisible ? "window" : "none";
            UpdateLastDetectionSummary(visible, source, "details=not-captured");
            return visible;
        }

        public string CaptureLastDetectionSummary()
        {
            bool apiVisible = IsGameBarVisibleByCachedGameBarApi(true, out string apiSummary);
            bool windowVisible = IsGameBarVisibleByWindowEnumeration(out string windowSummary);
            bool visible = apiVisible || windowVisible;
            string source = apiVisible ? "api" : windowVisible ? "window" : "none";
            UpdateLastDetectionSummary(visible, source,
                string.Concat("api=", apiSummary, "; window=", windowSummary, "; uia=diagnostic-only"));
            return LastDetectionSummary;
        }

        private static void UpdateLastDetectionSummary(bool visible, string source, string details)
        {
            lock (detectionStatusLock)
            {
                lastDetectionSummary = string.Concat("source=", source, " visible=", visible, " ", details);
            }
        }

        private static bool IsGameBarVisibleByWindowEnumeration()
        {
            return IsGameBarVisibleByWindowEnumeration(out _);
        }

        private static bool IsGameBarVisibleByWindowEnumeration(out string summary)
        {
            bool visible = false;
            string matchSummary = string.Empty;

            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    if (!IsInspectableWindow(hWnd))
                    {
                        return true;
                    }

                    if (LooksLikeGameBarWindow(hWnd, out string windowReason) ||
                        HasGameBarChildWindow(hWnd, out windowReason))
                    {
                        visible = true;
                        matchSummary = windowReason;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                summary = "error " + ex.GetType().Name + ": " + TruncateDiagnosticText(ex.Message);
                return false;
            }

            if (visible)
            {
                summary = matchSummary;
                return true;
            }

            summary = "no strict visible HWND match";
            return false;
        }

        private static bool IsGameBarVisibleByCachedGameBarApi(bool includeDiagnostics, out string summary)
        {
            DateTime now = DateTime.UtcNow;
            lock (gameBarApiPollLock)
            {
                bool cachedResultIsFresh = gameBarApiPollCachedVisible &&
                    now - gameBarApiPollLastCompletedUtc < TimeSpan.FromMilliseconds(LiveGameBarApiCacheMs);

                bool pollIsStale = gameBarApiPollRunning &&
                    now - gameBarApiPollLastStartedUtc > TimeSpan.FromMilliseconds(LiveGameBarApiHangMs);

                if ((!gameBarApiPollRunning || pollIsStale) &&
                    now - gameBarApiPollLastStartedUtc >= TimeSpan.FromMilliseconds(LiveGameBarApiPollMs))
                {
                    gameBarApiPollRunning = true;
                    gameBarApiPollLastStartedUtc = now;
                    int pollGeneration = ++gameBarApiPollGeneration;

                    Thread worker = new Thread(() =>
                    {
                        bool visible = false;
                        bool supported = false;
                        bool apiVisible = false;
                        bool apiInputRedirected = false;
                        string apiStatus = "not started";
                        Stopwatch stopwatch = Stopwatch.StartNew();
                        try
                        {
                            supported = TryGetGameBarApiStateOutOfProcess(LiveGameBarApiProbeTimeoutMs,
                                out apiVisible, out apiInputRedirected, out apiStatus);
                            visible = supported && (apiVisible || apiInputRedirected);
                        }
                        catch (Exception ex)
                        {
                            apiStatus = ex.GetType().Name + ": " + ex.Message;
                            visible = false;
                        }
                        finally
                        {
                            stopwatch.Stop();
                            lock (gameBarApiPollLock)
                            {
                                gameBarApiPollLastSupported = supported;
                                gameBarApiPollLastVisible = apiVisible;
                                gameBarApiPollLastInputRedirected = apiInputRedirected;
                                gameBarApiPollLastElapsedMs = stopwatch.ElapsedMilliseconds;
                                gameBarApiPollLastStatus = apiStatus;

                                if (pollGeneration == gameBarApiPollGeneration || visible)
                                {
                                    gameBarApiPollCachedVisible = visible;
                                    gameBarApiPollLastCompletedUtc = DateTime.UtcNow;
                                }

                                if (pollGeneration == gameBarApiPollGeneration)
                                {
                                    gameBarApiPollRunning = false;
                                }
                            }
                        }
                    });

                    worker.IsBackground = true;
                    worker.Name = "DS4Windows Game Bar API Poll";
                    worker.Priority = ThreadPriority.BelowNormal;
                    worker.Start();
                }

                summary = includeDiagnostics ?
                    BuildCachedGameBarApiSummary(now, cachedResultIsFresh, pollIsStale) :
                    string.Empty;
                return cachedResultIsFresh;
            }
        }

        private static string BuildCachedGameBarApiSummary(DateTime now, bool cachedResultIsFresh, bool pollIsStale)
        {
            try
            {
                StringBuilder builder = new StringBuilder(256);
                builder.Append("cachedFresh=").Append(cachedResultIsFresh)
                    .Append(" cachedVisible=").Append(gameBarApiPollCachedVisible)
                    .Append(" running=").Append(gameBarApiPollRunning)
                    .Append(" stale=").Append(pollIsStale)
                    .Append(" startedAge=").Append(FormatDiagnosticAge(now, gameBarApiPollLastStartedUtc))
                    .Append(" completedAge=").Append(FormatDiagnosticAge(now, gameBarApiPollLastCompletedUtc))
                    .Append(" lastSupported=").Append(gameBarApiPollLastSupported)
                    .Append(" lastVisible=").Append(gameBarApiPollLastVisible)
                    .Append(" lastInputRedirected=").Append(gameBarApiPollLastInputRedirected)
                    .Append(" lastElapsedMs=").Append(gameBarApiPollLastElapsedMs)
                    .Append(" lastStatus='").Append(TruncateDiagnosticText(gameBarApiPollLastStatus)).Append("'");
                return builder.ToString();
            }
            catch (Exception ex)
            {
                return string.Concat("summary unavailable: ", ex.GetType().Name);
            }
        }

        private static string FormatDiagnosticAge(DateTime now, DateTime timestampUtc)
        {
            if (timestampUtc == DateTime.MinValue)
            {
                return "never";
            }

            double totalMilliseconds = (now - timestampUtc).TotalMilliseconds;
            if (double.IsNaN(totalMilliseconds) || double.IsInfinity(totalMilliseconds))
            {
                return "unknown";
            }

            if (totalMilliseconds < 0)
            {
                totalMilliseconds = 0;
            }

            if (totalMilliseconds > int.MaxValue)
            {
                totalMilliseconds = int.MaxValue;
            }

            return string.Concat(((int)totalMilliseconds).ToString(), "ms");
        }

        private static bool IsGameBarVisibleByCachedAutomation()
        {
            DateTime now = DateTime.UtcNow;
            lock (automationPollLock)
            {
                bool cachedResultIsFresh = automationPollCachedVisible &&
                    now - automationPollLastCompletedUtc < TimeSpan.FromMilliseconds(LiveAutomationCacheMs);

                if (!automationPollRunning &&
                    now - automationPollLastStartedUtc >= TimeSpan.FromMilliseconds(LiveAutomationPollMs))
                {
                    automationPollRunning = true;
                    automationPollLastStartedUtc = now;

                    Thread worker = new Thread(() =>
                    {
                        bool visible = false;
                        try
                        {
                            visible = IsGameBarVisibleByAutomation();
                        }
                        catch
                        {
                            visible = false;
                        }
                        finally
                        {
                            lock (automationPollLock)
                            {
                                automationPollCachedVisible = visible;
                                automationPollLastCompletedUtc = DateTime.UtcNow;
                                automationPollRunning = false;
                            }
                        }
                    });

                    worker.IsBackground = true;
                    worker.Name = "DS4Windows Game Bar UIA Poll";
                    worker.SetApartmentState(ApartmentState.STA);
                    worker.Start();
                }

                return cachedResultIsFresh;
            }
        }

        public string GetGameBarWindowDiagnostics()
        {
            StringBuilder output = new StringBuilder();
            output.AppendLine(GetGameBarStateDiagnostics());

            List<string> rows = new List<string>();
            EnumWindows((hWnd, lParam) =>
            {
                AddDiagnosticRow(rows, hWnd, "top");

                EnumChildWindows(hWnd, (childHWnd, childParam) =>
                {
                    AddDiagnosticRow(rows, childHWnd, "child");
                    return rows.Count < 40;
                }, IntPtr.Zero);

                return rows.Count < 80;
            }, IntPtr.Zero);

            if (rows.Count == 0)
            {
                output.AppendLine("No Game Bar-like windows were found.");
            }
            else
            {
                foreach (string row in rows)
                {
                    output.AppendLine(row);
                }
            }

            AppendAutomationDiagnostics(output);

            return output.ToString().TrimEnd();
        }

        public string GetGameBarStateDiagnostics()
        {
            return $"GameBarVisible={IsGameBarVisible()} Elevated={IsRunningElevated()}\n{GetGameBarApiDiagnostics()}";
        }

        private static bool TryGetGameBarApiStateCore(out bool visible, out bool inputRedirected, out string status)
        {
            visible = false;
            inputRedirected = false;

            try
            {
                if (!ApiInformation.IsTypePresent("Windows.Gaming.UI.GameBar"))
                {
                    status = "not present";
                    return false;
                }

                visible = GameBar.Visible;
                inputRedirected = GameBar.IsInputRedirected;
                status = "ok";
                return true;
            }
            catch (Exception ex)
            {
                status = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string GetGameBarApiDiagnostics()
        {
            bool supported = TryGetGameBarApiStateOutOfProcess(2000, out bool visible, out bool inputRedirected, out string status);
            return $"GameBarApi supported={supported} visible={visible} inputRedirected={inputRedirected} status='{TruncateDiagnosticText(status)}'";
        }

        private static bool TryParseProbeResult(string result, out bool supported, out bool visible, out bool inputRedirected, out string status)
        {
            supported = false;
            visible = false;
            inputRedirected = false;
            status = string.Empty;

            if (string.IsNullOrEmpty(result))
            {
                return false;
            }

            string[] parts = result.Split(new[] { '|' }, 4);
            if (parts.Length < 4)
            {
                return false;
            }

            if (!bool.TryParse(parts[0], out supported) ||
                !bool.TryParse(parts[1], out visible) ||
                !bool.TryParse(parts[2], out inputRedirected))
            {
                supported = false;
                visible = false;
                inputRedirected = false;
                return false;
            }

            status = parts[3];
            return true;
        }

        private static string SanitizeProbeStatus(string status)
        {
            if (string.IsNullOrEmpty(status))
            {
                return string.Empty;
            }

            return status.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static bool TryRunProbeProcess(string exePath, string resultPath, int timeoutMs, out int exitCode, out string status)
        {
            exitCode = -1;
            status = string.Empty;

            STARTUPINFO startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = STARTF_USESHOWWINDOW | STARTF_FORCEOFFFEEDBACK,
                wShowWindow = SW_HIDE,
            };

            StringBuilder commandLine = new StringBuilder(QuoteCommandLineArgument(exePath) + " " +
                QuoteCommandLineArgument(ProbeArgument) + " " +
                QuoteCommandLineArgument(resultPath));
            string workingDirectory = Path.GetDirectoryName(exePath);

            if (!CreateProcess(exePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out PROCESS_INFORMATION processInfo))
            {
                status = "CreateProcess failed: " + Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                uint waitResult = WaitForSingleObject(processInfo.hProcess, (uint)Math.Max(1, timeoutMs));
                if (waitResult == WAIT_TIMEOUT)
                {
                    TerminateProcess(processInfo.hProcess, 1);
                    status = $"probe timeout after {timeoutMs}ms";
                    return false;
                }

                if (waitResult != WAIT_OBJECT_0)
                {
                    status = "WaitForSingleObject failed: " + waitResult;
                    return false;
                }

                if (GetExitCodeProcess(processInfo.hProcess, out uint nativeExitCode))
                {
                    exitCode = unchecked((int)nativeExitCode);
                }

                return true;
            }
            finally
            {
                if (processInfo.hThread != IntPtr.Zero)
                {
                    CloseHandle(processInfo.hThread);
                }

                if (processInfo.hProcess != IntPtr.Zero)
                {
                    CloseHandle(processInfo.hProcess);
                }
            }
        }

        private static string QuoteCommandLineArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            StringBuilder builder = new StringBuilder();
            builder.Append('"');
            int backslashCount = 0;

            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', backslashCount * 2 + 1);
                    builder.Append('"');
                    backslashCount = 0;
                    continue;
                }

                if (backslashCount > 0)
                {
                    builder.Append('\\', backslashCount);
                    backslashCount = 0;
                }

                builder.Append(c);
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static void TryDeleteProbeFile(string resultPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(resultPath) && File.Exists(resultPath))
                {
                    File.Delete(resultPath);
                }
            }
            catch
            {
            }
        }

        private static bool TryGetGameBarApiStateOutOfProcess(int timeoutMs, out bool visible, out bool inputRedirected, out string status)
        {
            visible = false;
            inputRedirected = false;

            string exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                try
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }
                catch
                {
                    exePath = string.Empty;
                }
            }

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                status = "probe exe not found";
                return false;
            }

            string resultPath = Path.Combine(Path.GetTempPath(), "DS4Windows.GameBarProbe." + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                if (!TryRunProbeProcess(exePath, resultPath, timeoutMs, out int exitCode, out string launchStatus))
                {
                    status = launchStatus;
                    return false;
                }

                if (!File.Exists(resultPath))
                {
                    status = $"probe exited {exitCode} without result";
                    return false;
                }

                string result = File.ReadAllText(resultPath, Encoding.UTF8);
                if (!TryParseProbeResult(result, out bool supported, out visible, out inputRedirected, out status))
                {
                    status = "invalid probe result: " + TruncateDiagnosticText(result);
                    return false;
                }

                return supported;
            }
            catch (Exception ex)
            {
                status = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                TryDeleteProbeFile(resultPath);
            }
        }

        private static bool IsGameBarVisibleByAutomation()
        {
            try
            {
                int visited = 0;
                return FindVisibleGameBarAutomationElement(AutomationElement.RootElement, 0, ref visited);
            }
            catch
            {
            }

            return false;
        }

        private static void AppendAutomationDiagnostics(StringBuilder output)
        {
            AppendGameBarProcessDiagnostics(output);

            try
            {
                List<string> rows = new List<string>();
                int visited = 0;
                AddAutomationDiagnosticRows(rows, AutomationElement.RootElement, "uia", 0, ref visited);

                if (rows.Count == 0)
                {
                    output.AppendLine($"No Game Bar-like UI Automation elements were found. UIAVisited={visited}");
                    return;
                }

                output.AppendLine($"UIAVisited={visited}");

                foreach (string row in rows)
                {
                    output.AppendLine(row);
                }
            }
            catch (Exception ex)
            {
                output.AppendLine($"UI Automation diagnostics failed: {ex.Message}");
            }
        }

        private static bool FindVisibleGameBarAutomationElement(AutomationElement parent, int depth, ref int visited)
        {
            if (depth > MaxAutomationDepth || visited >= MaxAutomationVisitCount)
            {
                return false;
            }

            AutomationElementCollection children;
            try
            {
                children = parent.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
            }
            catch
            {
                return false;
            }

            foreach (AutomationElement child in children)
            {
                visited++;
                if (LooksLikeVisibleGameBarAutomationElement(child))
                {
                    return true;
                }

                if (FindVisibleGameBarAutomationElement(child, depth + 1, ref visited))
                {
                    return true;
                }

                if (visited >= MaxAutomationVisitCount)
                {
                    break;
                }
            }

            return false;
        }

        private static void AddAutomationDiagnosticRows(List<string> rows, AutomationElement parent, string scope, int depth, ref int visited)
        {
            if (depth > MaxAutomationDepth || rows.Count >= MaxAutomationDiagnosticRows || visited >= MaxAutomationVisitCount)
            {
                return;
            }

            AutomationElementCollection children;
            try
            {
                children = parent.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
            }
            catch
            {
                return;
            }

            foreach (AutomationElement child in children)
            {
                visited++;
                AddAutomationDiagnosticRow(rows, child, $"{scope}-{depth}");

                if (rows.Count >= MaxAutomationDiagnosticRows || visited >= MaxAutomationVisitCount)
                {
                    return;
                }

                AddAutomationDiagnosticRows(rows, child, scope, depth + 1, ref visited);
            }
        }

        private static void AppendGameBarProcessDiagnostics(StringBuilder output)
        {
            List<string> rows = new List<string>();
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string processName = process.ProcessName ?? string.Empty;
                    if (!IsGameBarRelatedProcessName(processName))
                    {
                        continue;
                    }

                    rows.Add($"[proc] name='{TruncateDiagnosticText(processName)}' id={process.Id} mainWindowHandle=0x{process.MainWindowHandle.ToInt64():X} mainWindowTitle='{TruncateDiagnosticText(process.MainWindowTitle)}' responding={SafeGetResponding(process)}");
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (rows.Count == 0)
            {
                output.AppendLine("No Game Bar/Xbox related processes were found.");
                return;
            }

            foreach (string row in rows)
            {
                output.AppendLine(row);
            }
        }

        private static void AddAutomationDiagnosticRow(List<string> rows, AutomationElement element, string scope)
        {
            string name = GetAutomationName(element);
            string className = GetAutomationClassName(element);
            string automationId = GetAutomationId(element);
            string controlType = GetAutomationControlTypeName(element);
            string processName = GetAutomationProcessName(element);

            if (IsKnownNoisyProcessName(processName))
            {
                return;
            }

            if (!IsAutomationDiagnosticCandidate(processName, name, className, automationId))
            {
                return;
            }

            WinRect rect = GetAutomationBoundingRectangle(element);
            bool offscreen = GetAutomationIsOffscreen(element);
            bool hasSize = rect.Width > 1 && rect.Height > 1;
            bool inspectable = !offscreen && hasSize;
            bool match = inspectable && LooksLikeGameBarAutomationElement(element);

            rows.Add($"[{scope}] match={match} inspectable={inspectable} offscreen={offscreen} size={(int)rect.Width}x{(int)rect.Height} pos={(int)rect.X},{(int)rect.Y} proc='{TruncateDiagnosticText(processName)}' class='{TruncateDiagnosticText(className)}' control='{TruncateDiagnosticText(controlType)}' automationId='{TruncateDiagnosticText(automationId)}' name='{TruncateDiagnosticText(name)}'");
        }

        private static bool LooksLikeVisibleGameBarAutomationElement(AutomationElement element)
        {
            if (!LooksLikeGameBarAutomationElement(element))
            {
                return false;
            }

            WinRect rect = GetAutomationBoundingRectangle(element);
            return !GetAutomationIsOffscreen(element) && rect.Width > 1 && rect.Height > 1;
        }

        private static bool LooksLikeGameBarAutomationElement(AutomationElement element)
        {
            string name = GetAutomationName(element);
            string className = GetAutomationClassName(element);
            string automationId = GetAutomationId(element);
            string processName = GetAutomationProcessName(element);

            if (IsKnownNoisyProcessName(processName))
            {
                return false;
            }

            bool textLooksRight =
                name.IndexOf("Xbox Game Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Game Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                automationId.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                automationId.IndexOf("XboxGameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("XboxGameBar", StringComparison.OrdinalIgnoreCase) >= 0;

            if (textLooksRight)
            {
                return true;
            }

            return IsStrictGameBarProcessName(processName) &&
                (className.IndexOf("Xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 className.IndexOf("CoreWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 automationId.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsAutomationDiagnosticCandidate(string processName, string name, string className, string automationId)
        {
            return IsGameBarRelatedProcessName(processName) ||
                processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                (!IsKnownNoisyProcessName(processName) && name.IndexOf("Game Bar", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!IsKnownNoisyProcessName(processName) && name.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0) ||
                className.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                automationId.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                automationId.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsGameBarRelatedProcessName(string processName)
        {
            return processName.Equals("GameBar", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("XboxGameBar", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarFTServer", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarWidgets", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarElevatedFT_Alias", StringComparison.OrdinalIgnoreCase) ||
                processName.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                processName.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsKnownNoisyProcessName(string processName)
        {
            return processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("Codex", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("DS4Windows", StringComparison.OrdinalIgnoreCase);
        }

        private static string TruncateDiagnosticText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            int length = Math.Min(text.Length, MaxDiagnosticTextLength);
            StringBuilder builder = new StringBuilder(length + 3);
            for (int i = 0; i < length; i++)
            {
                char value = text[i];
                builder.Append(value == '\r' || value == '\n' ? ' ' : value);
            }

            if (text.Length > MaxDiagnosticTextLength)
            {
                builder.Append("...");
            }

            return builder.ToString();
        }

        private static bool SafeGetResponding(Process process)
        {
            try
            {
                return process.Responding;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasGameBarChildWindow(IntPtr parentWindow)
        {
            return HasGameBarChildWindow(parentWindow, out _);
        }

        private static bool HasGameBarChildWindow(IntPtr parentWindow, out string summary)
        {
            bool found = false;
            string matchSummary = string.Empty;

            EnumChildWindows(parentWindow, (hWnd, lParam) =>
            {
                if (!IsInspectableWindow(hWnd))
                {
                    return true;
                }

                if (LooksLikeGameBarWindow(hWnd, out string windowReason))
                {
                    found = true;
                    matchSummary = "child " + windowReason;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            summary = matchSummary;
            return found;
        }

        private static bool LooksLikeGameBarWindow(IntPtr hWnd)
        {
            return LooksLikeGameBarWindow(hWnd, out _);
        }

        private static bool LooksLikeGameBarWindow(IntPtr hWnd, out string summary)
        {
            string title = GetWindowTitle(hWnd);
            string className = GetWindowClassName(hWnd);
            string processName = GetProcessName(hWnd);
            summary = $"hwnd=0x{hWnd.ToInt64():X} proc='{TruncateDiagnosticText(processName)}' class='{TruncateDiagnosticText(className)}' title='{TruncateDiagnosticText(title)}'";

            bool trustedProcess = IsStrictGameBarProcessName(processName) ||
                processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase);

            if (!trustedProcess)
            {
                return false;
            }

            bool titleExplicitlyGameBar =
                title.IndexOf("Xbox Game Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.Equals("Game Bar", StringComparison.OrdinalIgnoreCase);

            bool classExplicitlyGameBar =
                className.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("XboxGameBar", StringComparison.OrdinalIgnoreCase) >= 0;

            if (titleExplicitlyGameBar || classExplicitlyGameBar)
            {
                return true;
            }

            bool strictProcessGenericOverlayWindow =
                IsStrictGameBarProcessName(processName) &&
                (className.IndexOf("Xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 className.IndexOf("CoreWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 className.IndexOf("ApplicationFrame", StringComparison.OrdinalIgnoreCase) >= 0);

            return strictProcessGenericOverlayWindow;
        }

        private static bool IsStrictGameBarProcessName(string processName)
        {
            return processName.Equals("GameBar", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("XboxGameBar", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarFTServer", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarWidgets", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("XboxGameBarWidgets", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarElevatedFT_Alias", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInspectableWindow(IntPtr hWnd)
        {
            return IsWindowVisible(hWnd) && !IsIconic(hWnd) && !IsWindowCloaked(hWnd) && HasVisibleSize(hWnd);
        }

        private static void AddDiagnosticRow(List<string> rows, IntPtr hWnd, string scope)
        {
            string processName = GetProcessName(hWnd);
            string title = GetWindowTitle(hWnd);
            string className = GetWindowClassName(hWnd);

            if (!IsDiagnosticCandidate(processName, title, className))
            {
                return;
            }

            RECT rect = GetWindowRect(hWnd, out RECT tempRect) ? tempRect : new RECT();
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            bool isVisible = IsWindowVisible(hWnd);
            bool isMinimized = IsIconic(hWnd);
            bool isCloaked = IsWindowCloaked(hWnd);
            bool hasSize = width > 1 && height > 1;
            bool inspectable = isVisible && !isMinimized && !isCloaked && hasSize;
            bool match = inspectable && LooksLikeGameBarWindow(hWnd);

            rows.Add($"[{scope}] match={match} inspectable={inspectable} visible={isVisible} minimized={isMinimized} cloaked={isCloaked} size={width}x{height} pos={rect.Left},{rect.Top} proc='{processName}' class='{className}' title='{title}'");
        }

        private static bool IsDiagnosticCandidate(string processName, string title, string className)
        {
            return processName.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                processName.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                title.IndexOf("Game Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("CoreWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("ApplicationFrame", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Windows.UI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                IsDiagnosticVisibleTopLevel(processName, title, className);
        }

        private static bool IsDiagnosticVisibleTopLevel(string processName, string title, string className)
        {
            return !string.IsNullOrEmpty(processName) &&
                (!string.IsNullOrEmpty(title) ||
                 className.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 className.IndexOf("Host", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsWindowCloaked(IntPtr hWnd)
        {
            try
            {
                return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasVisibleSize(IntPtr hWnd)
        {
            if (!GetWindowRect(hWnd, out RECT rect))
            {
                return false;
            }

            return rect.Right - rect.Left > 1 && rect.Bottom - rect.Top > 1;
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(length + 1);
            return GetWindowText(hWnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
        }

        private static string GetWindowClassName(IntPtr hWnd)
        {
            StringBuilder builder = new StringBuilder(256);
            return GetClassName(hWnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
        }

        private static string GetProcessName(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out uint processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAutomationName(AutomationElement element)
        {
            try
            {
                return element.Current.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAutomationClassName(AutomationElement element)
        {
            try
            {
                return element.Current.ClassName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAutomationId(AutomationElement element)
        {
            try
            {
                return element.Current.AutomationId ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAutomationControlTypeName(AutomationElement element)
        {
            try
            {
                return element.Current.ControlType?.ProgrammaticName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAutomationProcessName(AutomationElement element)
        {
            int processId;
            try
            {
                processId = element.Current.ProcessId;
            }
            catch
            {
                return string.Empty;
            }

            if (processId <= 0)
            {
                return string.Empty;
            }

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static WinRect GetAutomationBoundingRectangle(AutomationElement element)
        {
            try
            {
                return element.Current.BoundingRectangle;
            }
            catch
            {
                return WinRect.Empty;
            }
        }

        private static bool GetAutomationIsOffscreen(AutomationElement element)
        {
            try
            {
                return element.Current.IsOffscreen;
            }
            catch
            {
                return true;
            }
        }
    }
}
