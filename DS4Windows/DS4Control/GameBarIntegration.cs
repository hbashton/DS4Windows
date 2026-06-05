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
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace DS4Windows
{
    public class GameBarIntegration
    {
        private const byte VK_LWIN = 0x5B;
        private const byte VK_G = 0x47;
        private const int KEYEVENTF_KEYUP = 0x0002;
        private const int DWMWA_CLOAKED = 14;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
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
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_G, 0, 0, UIntPtr.Zero);
            keybd_event(VK_G, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            return "keybd_event Win+G sent";
        }

        public bool IsGameBarVisible()
        {
            bool visible = false;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsInspectableWindow(hWnd))
                {
                    return true;
                }

                if (LooksLikeGameBarWindow(hWnd) || HasGameBarChildWindow(hWnd))
                {
                    visible = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return visible || IsGameBarVisibleByAutomation();
        }

        public string GetGameBarWindowDiagnostics()
        {
            StringBuilder output = new StringBuilder();
            output.AppendLine($"GameBarVisible={IsGameBarVisible()} Elevated={IsRunningElevated()}");

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

        private static bool IsGameBarVisibleByAutomation()
        {
            try
            {
                AutomationElementCollection elements = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement element in elements)
                {
                    if (LooksLikeVisibleGameBarAutomationElement(element))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void AppendAutomationDiagnostics(StringBuilder output)
        {
            try
            {
                List<string> rows = new List<string>();
                AutomationElementCollection elements = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement element in elements)
                {
                    AddAutomationDiagnosticRow(rows, element, "uia-top");

                    if (rows.Count >= 40)
                    {
                        break;
                    }
                }

                if (rows.Count == 0)
                {
                    output.AppendLine("No Game Bar-like UI Automation elements were found.");
                    return;
                }

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

        private static void AddAutomationDiagnosticRow(List<string> rows, AutomationElement element, string scope)
        {
            string name = GetAutomationName(element);
            string className = GetAutomationClassName(element);
            string automationId = GetAutomationId(element);
            string controlType = GetAutomationControlTypeName(element);
            string processName = GetAutomationProcessName(element);

            if (!IsAutomationDiagnosticCandidate(processName, name, className, automationId))
            {
                return;
            }

            Rect rect = GetAutomationBoundingRectangle(element);
            bool offscreen = GetAutomationIsOffscreen(element);
            bool hasSize = rect.Width > 1 && rect.Height > 1;
            bool inspectable = !offscreen && hasSize;
            bool match = inspectable && LooksLikeGameBarAutomationElement(element);

            rows.Add($"[{scope}] match={match} inspectable={inspectable} offscreen={offscreen} size={(int)rect.Width}x{(int)rect.Height} pos={(int)rect.X},{(int)rect.Y} proc='{processName}' class='{className}' control='{controlType}' automationId='{automationId}' name='{name}'");
        }

        private static bool LooksLikeVisibleGameBarAutomationElement(AutomationElement element)
        {
            if (!LooksLikeGameBarAutomationElement(element))
            {
                return false;
            }

            Rect rect = GetAutomationBoundingRectangle(element);
            return !GetAutomationIsOffscreen(element) && rect.Width > 1 && rect.Height > 1;
        }

        private static bool LooksLikeGameBarAutomationElement(AutomationElement element)
        {
            string name = GetAutomationName(element);
            string className = GetAutomationClassName(element);
            string automationId = GetAutomationId(element);
            string processName = GetAutomationProcessName(element);

            bool processLooksRight =
                processName.Equals("GameBar", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("XboxGameBar", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarFTServer", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarWidgets", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarElevatedFT_Alias", StringComparison.OrdinalIgnoreCase);

            bool textLooksRight =
                name.IndexOf("Xbox Game Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Game Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                automationId.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                automationId.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0;

            return processLooksRight || textLooksRight;
        }

        private static bool IsAutomationDiagnosticCandidate(string processName, string name, string className, string automationId)
        {
            return processName.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                processName.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("Game Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                automationId.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                automationId.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasGameBarChildWindow(IntPtr parentWindow)
        {
            bool found = false;

            EnumChildWindows(parentWindow, (hWnd, lParam) =>
            {
                if (!IsInspectableWindow(hWnd))
                {
                    return true;
                }

                if (LooksLikeGameBarWindow(hWnd))
                {
                    found = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static bool LooksLikeGameBarWindow(IntPtr hWnd)
        {
            string title = GetWindowTitle(hWnd);
            string className = GetWindowClassName(hWnd);
            string processName = GetProcessName(hWnd);

            bool processLooksRight =
                processName.Equals("GameBar", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("XboxGameBar", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarFTServer", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarWidgets", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("GameBarElevatedFT_Alias", StringComparison.OrdinalIgnoreCase);

            bool titleLooksRight =
                title.IndexOf("Xbox Game Bar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("Game Bar", StringComparison.OrdinalIgnoreCase) >= 0;

            bool classLooksRight =
                className.IndexOf("GameBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Xaml", StringComparison.OrdinalIgnoreCase) >= 0;

            return (processLooksRight && (titleLooksRight || classLooksRight)) ||
                titleLooksRight;
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

        private static Rect GetAutomationBoundingRectangle(AutomationElement element)
        {
            try
            {
                return element.Current.BoundingRectangle;
            }
            catch
            {
                return Rect.Empty;
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
