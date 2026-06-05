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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

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

        public void OpenGameBar()
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_G, 0, 0, UIntPtr.Zero);
            keybd_event(VK_G, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
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

            return visible;
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
    }
}
