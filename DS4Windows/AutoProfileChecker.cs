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
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Diagnostics; // StopWatch
using System.Threading; // Sleep
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using DS4Windows;

namespace DS4WinWPF
{
    [SuppressUnmanagedCodeSecurity]
    public class AutoProfileChecker
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private AutoProfileHolder profileHolder;
        private IntPtr prevForegroundWnd = IntPtr.Zero;
        private uint prevForegroundProcessID;
        private string prevForegroundProcessName = string.Empty;
        private string prevForegroundWndTitleName = string.Empty;
        private StringBuilder autoProfileCheckTextBuilder = new StringBuilder(1000);
        private int autoProfileDebugLogLevel = 0;
        private bool turnOffTemp;
        private AutoProfileEntity tempAutoProfile;
        private volatile bool running;

        public int AutoProfileDebugLogLevel { get => autoProfileDebugLogLevel; set => autoProfileDebugLogLevel = value; }
        public bool Running { get => running; set => running = value; }

        public delegate void ChangeServiceHandler(AutoProfileChecker sender, bool state);
        public event ChangeServiceHandler RequestServiceChange;

        public AutoProfileChecker(AutoProfileHolder holder)
        {
            profileHolder = holder;
        }

        public void Process()
        {
            string topProcessName, topWindowTitle;
            bool turnOffDS4WinApp = false;
            AutoProfileEntity matchedProfileEntity = null;
            AutoProfileEntity[] matchedControllerProfileEntities = new AutoProfileEntity[ControlService.CURRENT_DS4_CONTROLLER_LIMIT];
            List<AutoProfileEntity> matchedProfileEntities = new List<AutoProfileEntity>();

            if (GetTopWindowName(out topProcessName, out topWindowTitle))
            {
                // Find a profile match based on autoprofile program path and wnd title list.
                // The same program may set different profiles for each of the controllers, so we need an array of newProfileName[controllerIdx] values.
                AutoProfileEntity[] profileSnapshot = profileHolder.GetSnapshot();
                for (int i = 0; i < profileSnapshot.Length; i++)
                {
                    AutoProfileEntity tempEntity = profileSnapshot[i];
                    if (tempEntity.IsMatch(topProcessName, topWindowTitle))
                    {
                        if (autoProfileDebugLogLevel > 0)
                            DS4Windows.AppLogger.LogToGui($"DEBUG: Auto-Profile. Rule#{i + 1}  Path={tempEntity.path}  Title={tempEntity.title}  Device={tempEntity.DeviceOption}", false, true);

                        matchedProfileEntities.Add(tempEntity);
                    }
                }

                if (matchedProfileEntities.Count > 0)
                {
                    for (int j = 0; j < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; j++)
                    {
                        DS4Device device = Program.rootHub.DS4Controllers[j];
                        if (device == null)
                        {
                            continue;
                        }

                        AutoProfileEntity tempEntity = SelectProfileEntityForController(matchedProfileEntities, device);
                        matchedControllerProfileEntities[j] = tempEntity;
                        if (tempEntity != null)
                        {
                            matchedProfileEntity = matchedProfileEntity ?? tempEntity;
                            turnOffDS4WinApp = turnOffDS4WinApp || tempEntity.Turnoff;
                        }
                    }

                    // Rules applying to every device still own their lifecycle even
                    // when no controller is currently connected. Do not, however,
                    // preload temporary state into empty controller slots.
                    if (matchedProfileEntity == null)
                    {
                        matchedProfileEntity = matchedProfileEntities.FirstOrDefault(
                            entity => entity.DeviceOption == AutoProfileDeviceOption.Any);
                        turnOffDS4WinApp = matchedProfileEntity?.Turnoff == true;
                    }
                }

                if (matchedProfileEntity != null)
                {
                    bool forceLoadProfile = false;

                    if (!turnOffDS4WinApp && turnOffTemp)
                    {
                        // DS4Win was temporarily turned off by another auto-profile rule. Turn DS4Win on before trying to load a new profile because otherwise the new profile won't do anything.
                        // Force load the profile when DS4Win service afer waking up DS4Win service to make sure that the new profile will be active.
                        turnOffTemp = false;
                        SetAndWaitServiceStatus(true);
                        forceLoadProfile = true;
                    }

                    // Program match found. Check if the new profile is different than current profile of the controller. Load the new profile only if it is not already loaded.
                    for (int j = 0; j < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; j++)
                    {
                        AutoProfileEntity controllerProfileEntity = matchedControllerProfileEntities[j];
                        if (controllerProfileEntity == null)
                        {
                            continue;
                        }

                        string tempname = controllerProfileEntity.GetProfileNameForController(j);
                        if (tempname != string.Empty && tempname != "(none)")
                        {
                            if ((Global.useTempProfile[j] && tempname != Global.tempprofilename[j]) ||
                                (!Global.useTempProfile[j] && tempname != Global.ProfilePath[j]) ||
                                forceLoadProfile)
                            {
                                if (autoProfileDebugLogLevel > 0)
                                    DS4Windows.AppLogger.LogToGui($"DEBUG: Auto-Profile. LoadProfile Controller {j + 1}={tempname}  DeviceRule={controllerProfileEntity.DeviceOption}", false, true);

                                if (Global.autoProfileSwitchNotifyChoice !=
                                    AutoProfileDisplayProfileSwitchChoices.None)
                                {
                                    DisplayProfileChange(j, tempname);
                                }

                                DS4Device device = Program.rootHub.DS4Controllers[j];
                                if (device != null)
                                {
                                    // Wait for controller to be in a wait period
                                    int tempInd = j;
                                    device.HaltReportingRunAction(() =>
                                    {
                                        if (!Global.LoadTempProfile(tempInd, tempname, true,
                                                Program.rootHub))
                                        {
                                            AppLogger.LogToGui(
                                                $"Auto-Profile could not load '{tempname}' for controller {tempInd + 1}.",
                                                false, true);
                                        }
                                    });
                                }
                            }
                            else
                            {
                                if (autoProfileDebugLogLevel > 0)
                                    DS4Windows.AppLogger.LogToGui($"DEBUG: Auto-Profile. LoadProfile Controller {j + 1}={tempname} (already loaded)", false, true);
                            }
                        }
                    }

                    if (turnOffDS4WinApp)
                    {
                        turnOffTemp = true;
                        if (App.rootHub.running)
                        {
                            if (autoProfileDebugLogLevel > 0)
                                DS4Windows.AppLogger.LogToGui($"DEBUG: Auto-Profile. Turning DS4Windows temporarily off", false, true);

                            SetAndWaitServiceStatus(false);
                        }
                    }

                    tempAutoProfile = matchedProfileEntity;
                }
                else if (tempAutoProfile != null)
                {
                    if (turnOffTemp && DS4Windows.Global.AutoProfileRevertDefaultProfile)
                    {
                        turnOffTemp = false;
                        if (!App.rootHub.running)
                        {
                            if (autoProfileDebugLogLevel > 0)
                                DS4Windows.AppLogger.LogToGui($"DEBUG: Auto-Profile. Turning DS4Windows on before reverting to default profile", false, true);

                            SetAndWaitServiceStatus(true);
                        }
                    }

                    tempAutoProfile = null;
                    for (int j = 0; j < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; j++)
                    {
                        if (Global.useTempProfile[j])
                        {
                            if (DS4Windows.Global.AutoProfileRevertDefaultProfile)
                            {
                                if (autoProfileDebugLogLevel > 0)
                                    DS4Windows.AppLogger.LogToGui($"DEBUG: Auto-Profile. Unknown process. Reverting to default profile. Controller {j + 1}={Global.ProfilePath[j]} (default)", false, true);

                                if (Global.autoProfileSwitchNotifyChoice !=
                                    AutoProfileDisplayProfileSwitchChoices.None)
                                {
                                    DisplayProfileChange(j, "default");
                                }

                                DS4Device device = Program.rootHub.DS4Controllers[j];
                                if (device != null)
                                {
                                    // Wait for controller to be in a wait period
                                    int tempInd = j;
                                    device.HaltReportingRunAction(() =>
                                    {
                                        Global.LoadProfile(tempInd, false, Program.rootHub);
                                    });
                                }
                                else
                                {
                                    Global.LoadProfile(j, false, Program.rootHub);
                                }
                            }
                            else
                            {
                                if (autoProfileDebugLogLevel > 0)
                                    DS4Windows.AppLogger.LogToGui($"DEBUG: Auto-Profile. Unknown process. Existing profile left as active. Controller {j + 1}={Global.tempprofilename[j]}", false, true);
                            }
                        }
                    }
                }
            }
        }

        private AutoProfileEntity SelectProfileEntityForController(List<AutoProfileEntity> matchedProfileEntities, DS4Device device)
        {
            AutoProfileEntity fallbackEntity = matchedProfileEntities.FirstOrDefault(entity =>
                entity.DeviceOption == AutoProfileDeviceOption.Any);

            if (device != null)
            {
                AutoProfileEntity deviceEntity = matchedProfileEntities.FirstOrDefault(entity =>
                    entity.DeviceOption != AutoProfileDeviceOption.Any &&
                    entity.IsDeviceMatch(device.DeviceType));

                if (deviceEntity != null)
                {
                    return deviceEntity;
                }
            }

            return fallbackEntity;
        }

        private bool GetTopWindowName(out string topProcessName, out string topWndTitleName)
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                // Top window unknown or cannot acquire a handle. Return FALSE and return unknown process and wndTitle values
                prevForegroundWnd = IntPtr.Zero;
                prevForegroundProcessID = 0;
                topProcessName = topWndTitleName = String.Empty;
                return false;
            }

            //
            // If this function was called from "auto-profile watcher timer" then check cached "previous hWnd handle". If the current hWnd is the same
            // as during the previous check then return cached previous wnd and name values (ie. foreground app and window are assumed to be the same, so no need to re-query names).
            // This should optimize the auto-profile timer check process and causes less burden to .NET GC collector because StringBuffer is not re-allocated every second.
            //
            // Note! hWnd handles may be re-cycled but not during the lifetime of the window. This "cache" optimization still works because when an old window is closed
            // then foreground window changes to something else and the cached prevForgroundWnd variable is updated to store the new hWnd handle.
            // It doesn't matter even when the previously cached handle is recycled by WinOS to represent some other window (it is no longer used as a cached value anyway).
            //
            if (hWnd == prevForegroundWnd)
            {
                // Use cached process data, but still let the matcher run. Auto-profile
                // rules can be added or edited while the foreground window is unchanged.
                topProcessName = prevForegroundProcessName;
                var title = GetWindowTitle((HWND)hWnd).ToLower();
                if (title != prevForegroundWndTitleName)
                {
                    prevForegroundWndTitleName = topWndTitleName = title;
                    return true;
                }
                topWndTitleName = prevForegroundWndTitleName;
                return true;
            }

            prevForegroundWnd = hWnd;

            uint lpdwProcessId = 0;
            GetWindowThreadProcessId(hWnd, out lpdwProcessId);

            if (lpdwProcessId == prevForegroundProcessID)
            {
                topProcessName = prevForegroundProcessName;
            }
            else
            {
                prevForegroundProcessID = lpdwProcessId;
                prevForegroundProcessName = topProcessName = GetProcessExecutablePath(lpdwProcessId)
                    .Replace('/', '\\')
                    .ToLower();
            }

            GetWindowText(hWnd, autoProfileCheckTextBuilder, autoProfileCheckTextBuilder.Capacity);
            prevForegroundWndTitleName = topWndTitleName = autoProfileCheckTextBuilder.ToString().ToLower();


            if (autoProfileDebugLogLevel > 0)
                DS4Windows.AppLogger.LogToGui($"DEBUG: Auto-Profile. PID={lpdwProcessId}  Path={topProcessName} | WND={hWnd}  Title={topWndTitleName}", false, true);

            return true;
        }

        private static string GetProcessExecutablePath(uint processId)
        {
            if (processId == 0)
            {
                return string.Empty;
            }

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    StringBuilder builder = new StringBuilder(1000);
                    int size = builder.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, builder, ref size) && size > 0)
                    {
                        return builder.ToString();
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById((int)processId))
                {
                    return process.ProcessName + ".exe";
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static unsafe string GetWindowTitle(HWND handle)
        {
            var strLength = PInvoke.GetWindowTextLength(handle) + 1;
            var buffer = stackalloc char[strLength];
            if (PInvoke.GetWindowText(handle, buffer, strLength) > 0)
                return Marshal.PtrToStringAuto((IntPtr)buffer) ?? string.Empty;
            return string.Empty;
        }

        private void SetAndWaitServiceStatus(bool serviceRunningStatus)
        {
            // Start or Stop the service only if it is not already in the requested state
            if (App.rootHub.running != serviceRunningStatus)
            {
                RequestServiceChange?.Invoke(this, serviceRunningStatus);

                // Wait until DS4Win app service is running or stopped (as requested by serviceRunningStatus value) or timeout.
                // LoadProfile call fails if a new profile is loaded while DS4Win service is still in stopped state (ie the loaded temp profile doesn't do anything).
                Stopwatch sw = new Stopwatch();
                sw.Start();
                while (App.rootHub.running != serviceRunningStatus && sw.Elapsed.TotalSeconds < 10)
                {
                    Thread.Sleep(10);
                }
            }
        }

        private void DisplayProfileChange(int ind, string profile)
        {
            switch (Global.autoProfileSwitchNotifyChoice)
            {
                case AutoProfileDisplayProfileSwitchChoices.Log:
                    {
                        string prolog = string.Format(DS4WinWPF.Properties.Resources.UsingAutoTempProfile, (ind + 1).ToString(), profile);
                        DS4Windows.AppLogger.LogToGui(prolog, false);
                    }

                    break;
                case AutoProfileDisplayProfileSwitchChoices.Notification:
                    {
                        string prolog = string.Format(DS4WinWPF.Properties.Resources.UsingAutoTempProfile, (ind + 1).ToString(), profile);
                        DS4Windows.AppLogger.LogToTray(prolog);
                    }

                    break;
                case AutoProfileDisplayProfileSwitchChoices.LogAndNotification:
                    {
                        string prolog = string.Format(DS4WinWPF.Properties.Resources.UsingAutoTempProfile, (ind + 1).ToString(), profile);
                        DS4Windows.AppLogger.LogToGui(prolog, false);
                        DS4Windows.AppLogger.LogToTray(prolog);
                    }

                    break;
                default:
                    break;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nSize);

        private const int WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int EVENT_OBJECT_NAMECHANGE = 0x800C;
    }
}
