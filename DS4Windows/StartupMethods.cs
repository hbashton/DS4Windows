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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.TaskScheduler;
using Task = Microsoft.Win32.TaskScheduler.Task;

namespace DS4WinWPF
{
    [System.Security.SuppressUnmanagedCodeSecurity]
    public static class StartupMethods
    {
        private const string RefreshTaskArgument =
            "--refresh-ds4windows-startup-task";
        public static string lnkpath = Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "\\DS4Windows.lnk";

        public static bool TryRunTaskRefreshHelper(string[] args,
            out int exitCode)
        {
            exitCode = 1;
            if (args == null || args.Length != 2 ||
                !string.Equals(args[0], RefreshTaskArgument,
                    StringComparison.Ordinal))
            {
                return false;
            }

            string targetUserSid;
            try
            {
                targetUserSid = Encoding.UTF8.GetString(
                    Convert.FromBase64String(args[1]));
            }
            catch
            {
                exitCode = 87;
                return true;
            }

            string currentUserSid = WindowsIdentity.GetCurrent().User?.Value;
            if (!DS4Windows.Global.IsAdministrator() ||
                !string.Equals(currentUserSid, targetUserSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                exitCode = 5;
                return true;
            }

            try
            {
                WriteTaskEntry();
                exitCode = 0;
            }
            catch
            {
                exitCode = 1;
            }

            return true;
        }

        public static void RetargetExistingTaskToCurrentExecutable()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            try
            {
                using TaskService ts = new TaskService();
                using Task task = ts.GetTask(@"\RunDS4Windows");
                if (task == null || TaskTargetsCurrentExecutable(task))
                {
                    return;
                }

                if (DS4Windows.Global.IsAdministrator())
                {
                    WriteTaskEntry();
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = DS4Windows.Global.exelocation,
                    UseShellExecute = true,
                    Verb = "runas",
                };
                startInfo.ArgumentList.Add(RefreshTaskArgument);
                string currentUserSid = WindowsIdentity.GetCurrent()
                    .User?.Value;
                if (string.IsNullOrWhiteSpace(currentUserSid))
                {
                    return;
                }
                startInfo.ArgumentList.Add(Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(currentUserSid)));
                using Process process = Process.Start(startInfo);
                if (process == null)
                {
                    return;
                }
                process.WaitForExit(15000);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // The portable copy remains usable when the user declines UAC;
                // only the existing startup task keeps its previous target.
            }
            catch
            {
                // Startup task repair must never prevent controller startup.
            }
        }

        public static bool HasStartProgEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return false;
            // Exception handling should not be needed here. Method handles most cases
            bool exists = File.Exists(Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "\\DS4Windows.lnk");
            return exists;
        }

        public static bool HasTaskEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return false;
            using TaskService ts = new TaskService();
            using Task tasker = ts.GetTask(@"\RunDS4Windows");
            return tasker != null && TaskTargetsCurrentExecutable(tasker);
        }

        public static bool IsRunAtStartupEnabled()
        {
            if (DS4Windows.PortableLabContext.IsActive) return false;
            if (HasStartProgEntry())
            {
                return true;
            }

            try
            {
                return HasTaskEntry();
            }
            catch
            {
                // A Task Scheduler failure must not be interpreted as an
                // affirmative startup preference by setup.
                return false;
            }
        }

        public static void WriteStartProgEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            Type t = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8")); // Windows Script Host Shell Object
            dynamic shell = Activator.CreateInstance(t);
            try
            {
                var lnk = shell.CreateShortcut(lnkpath);
                try
                {
                    string app = DS4Windows.Global.exelocation;
                    lnk.TargetPath = DS4Windows.Global.exelocation;
                    lnk.Arguments = "-m";
                    // Need to add the DS4Windows directory as cwd or
                    // language assemblies cannot be discovered
                    lnk.WorkingDirectory = DS4Windows.Global.exedirpath;

                    //lnk.TargetPath = Assembly.GetExecutingAssembly().Location;
                    //lnk.Arguments = "-m";
                    lnk.IconLocation = app.Replace('\\', '/');
                    lnk.Save();
                }
                finally
                {
                    Marshal.FinalReleaseComObject(lnk);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        public static void DeleteStartProgEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            if (File.Exists(lnkpath) && !new FileInfo(lnkpath).IsReadOnly)
            {
                File.Delete(lnkpath);
            }
        }

        public static void DeleteOldTaskEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            using TaskService ts = new TaskService();
            using Task tasker = ts.GetTask(@"\RunDS4Windows");
            if (tasker != null && !TaskTargetsCurrentExecutable(tasker))
            {
                ts.RootFolder.DeleteTask("RunDS4Windows");
            }
        }

        public static bool CanWriteStartEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return false;
            bool result = false;
            if (!new FileInfo(lnkpath).IsReadOnly)
            {
                result = true;
            }

            return result;
        }

        public static void WriteTaskEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            DeleteTaskEntry();

            TaskService ts = new TaskService();
            TaskDefinition td = ts.NewTask();
            string currentUserSid = WindowsIdentity.GetCurrent().User?.Value ??
                throw new InvalidOperationException(
                    "Windows did not provide the current account SID.");
            // Leave the trigger user-neutral and bind the principal to the
            // exact SID. This avoids Task Scheduler's ambiguous UserId name
            // lookup when the computer and local account share a name while
            // retaining the same interactive-user security boundary.
            td.Triggers.Add(new LogonTrigger());
            string dir = DS4Windows.Global.exedirpath;
            td.Actions.Add(new ExecAction(
                DS4Windows.Global.exelocation, "-m", dir));

            td.Principal.UserId = currentUserSid;
            td.Principal.LogonType = TaskLogonType.InteractiveToken;
            td.Principal.RunLevel = TaskRunLevel.Highest;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
            td.Settings.AllowDemandStart = true;
            // Task Scheduler defaults new tasks to BELOW_NORMAL (priority 7),
            // including low I/O and memory priority.  That can starve the
            // controller media producer during a CPU spike before DS4Windows
            // has a chance to raise its own process priority.
            td.Settings.Priority = ProcessPriorityClass.High;
            ts.RootFolder.RegisterTaskDefinition("RunDS4Windows", td);
        }

        public static void DeleteTaskEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            TaskService ts = new TaskService();
            Task tasker = ts.GetTask(@"\RunDS4Windows");
            if (tasker != null)
            {
                ts.RootFolder.DeleteTask("RunDS4Windows");
            }
        }

        public static bool CheckStartupExeLocation()
        {
            if (DS4Windows.PortableLabContext.IsActive) return false;
            string lnkprogpath = ResolveShortcut(lnkpath);
            return lnkprogpath != DS4Windows.Global.exelocation;
        }

        public static void LaunchOldTask()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            TaskService ts = new TaskService();
            Task tasker = ts.GetTask(@"\RunDS4Windows");
            if (tasker != null)
            {
                tasker.Run("");
            }
        }

        private static string ResolveShortcut(string filePath)
        {
            Type t = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8")); // Windows Script Host Shell Object
            dynamic shell = Activator.CreateInstance(t);
            string result;

            try
            {
                var shortcut = shell.CreateShortcut(filePath);
                result = shortcut.TargetPath;
                Marshal.FinalReleaseComObject(shortcut);
            }
            catch (COMException)
            {
                // A COMException is thrown if the file is not a valid shortcut (.lnk) file 
                result = null;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }

            return result;
        }

        private static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) ||
                string.IsNullOrWhiteSpace(second))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool TaskTargetsCurrentExecutable(Task task)
        {
            if (task.Definition.Actions.Count != 1 ||
                task.Definition.Actions[0] is not ExecAction action ||
                task.Definition.Triggers.Count != 1 ||
                task.Definition.Triggers[0] is not LogonTrigger trigger)
            {
                return false;
            }

            TaskDefinition definition = task.Definition;
            string currentUserSid = WindowsIdentity.GetCurrent().User?.Value;
            return task.Enabled && definition.Settings.Enabled &&
                definition.Principal.RunLevel == TaskRunLevel.Highest &&
                definition.Principal.LogonType ==
                    TaskLogonType.InteractiveToken &&
                AccountMatchesSid(definition.Principal.UserId,
                    currentUserSid) &&
                trigger.Enabled &&
                (string.IsNullOrWhiteSpace(trigger.UserId) ||
                 AccountMatchesSid(trigger.UserId, currentUserSid)) &&
                definition.Settings.ExecutionTimeLimit == TimeSpan.Zero &&
                definition.Settings.MultipleInstances ==
                    TaskInstancesPolicy.IgnoreNew &&
                definition.Settings.Priority == ProcessPriorityClass.High &&
                !definition.Settings.StopIfGoingOnBatteries &&
                !definition.Settings.DisallowStartIfOnBatteries &&
                PathsEqual(action.Path, DS4Windows.Global.exelocation) &&
                string.Equals(action.Arguments?.Trim(), "-m",
                    StringComparison.Ordinal) &&
                PathsEqual(action.WorkingDirectory,
                    DS4Windows.Global.exedirpath);
        }

        private static bool AccountMatchesSid(string account,
            string expectedSid)
        {
            if (string.IsNullOrWhiteSpace(account) ||
                string.IsNullOrWhiteSpace(expectedSid))
            {
                return false;
            }

            try
            {
                string actualSid = account.StartsWith("S-1-",
                        StringComparison.OrdinalIgnoreCase)
                    ? new SecurityIdentifier(account).Value
                    : ((SecurityIdentifier)new NTAccount(account).Translate(
                        typeof(SecurityIdentifier))).Value;
                return string.Equals(actualSid, expectedSid,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

    }
}
