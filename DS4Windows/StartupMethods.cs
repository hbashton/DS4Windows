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
        private const string RemoveTaskArgument =
            "--remove-ds4windows-startup-task";
        public static string lnkpath = Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "\\DS4Windows.lnk";

        public static bool TryRunTaskRefreshHelper(string[] args,
            out int exitCode)
        {
            exitCode = 1;
            if (args == null || args.Length != 2 ||
                (!string.Equals(args[0], RefreshTaskArgument, StringComparison.Ordinal) &&
                 !string.Equals(args[0], RemoveTaskArgument, StringComparison.Ordinal)))
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
            if (DS4Windows.PortableLabContext.IsActive ||
                !StartupRegistrationPolicy.AuthorizesTaskHelper(
                    DS4Windows.Global.IsAdministrator(), currentUserSid, targetUserSid))
            {
                exitCode = 5;
                return true;
            }

            try
            {
                if (string.Equals(args[0], RemoveTaskArgument, StringComparison.Ordinal))
                    DeleteTaskEntry();
                else
                {
                    // Reinspect after elevation. A task disabled or removed
                    // while the approval prompt was open must stay that way.
                    using TaskService service = new TaskService();
                    using Task task = service.GetTask(@"\RunDS4Windows");
                    if (TaskNeedsRepair(task)) WriteTaskEntry();
                }
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
                if (!TaskNeedsRepair(task))
                {
                    return;
                }

                if (DS4Windows.Global.IsAdministrator())
                {
                    WriteTaskEntry();
                    return;
                }

                RunTaskHelper(RefreshTaskArgument);
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
            return File.Exists(lnkpath) && IsOwnedShortcut(lnkpath);
        }

        public static bool HasTaskEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return false;
            using TaskService ts = new TaskService();
            using Task tasker = ts.GetTask(@"\RunDS4Windows");
            return TaskIsEnabled(tasker) && IsOwnedTask(tasker);
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
            EnsureShortcutOwnership();
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
                    lnk.Description = StartupRegistrationPolicy.ManagedTaskDescription;
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
            if (!HasStartProgEntry())
                throw new IOException("Windows did not save the DS4Windows startup shortcut.");
        }

        public static void DeleteStartProgEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            StartupRegistrationPolicy.RemoveShortcut(lnkpath, IsOwnedShortcut);
        }

        public static void DeleteOldTaskEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            using TaskService ts = new TaskService();
            using Task tasker = ts.GetTask(@"\RunDS4Windows");
            if (TaskNeedsRepair(tasker))
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
            using TaskService ts = new TaskService();
            using Task existing = ts.GetTask(@"\RunDS4Windows");
            EnsureTaskOwnership(existing);
            TaskDefinition td = ts.NewTask();
            td.RegistrationInfo.Description = StartupRegistrationPolicy.ManagedTaskDescription;
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
            // Replace in place; a failed registration must not first delete
            // the user's previous working startup entry.
            using Task registered = ts.RootFolder.RegisterTaskDefinition("RunDS4Windows", td);
            if (registered == null || !TaskTargetsCurrentExecutable(registered))
                throw new IOException("Windows did not confirm the DS4Windows startup task.");
        }

        public static void DeleteTaskEntry()
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            using TaskService ts = new TaskService();
            using Task tasker = ts.GetTask(@"\RunDS4Windows");
            if (tasker != null)
            {
                EnsureTaskOwnership(tasker);
                if (DS4Windows.Global.IsAdministrator())
                    ts.RootFolder.DeleteTask("RunDS4Windows");
                else
                    RunTaskHelper(RemoveTaskArgument);
                using Task remaining = ts.GetTask(@"\RunDS4Windows");
                if (remaining != null)
                    throw new IOException("The DS4Windows startup task remained after removal.");
            }
        }

        internal static StartupRegistrationState ReadRegistrationState() =>
            new(HasStartProgEntry(), HasTaskEntry());

        internal static void SetRegistrationMode(StartupRegistrationMode mode)
        {
            if (DS4Windows.PortableLabContext.IsActive) return;
            // Check both owners before changing either registration. A name
            // collision is not permission to remove another program's task.
            EnsureShortcutOwnership();
            using (TaskService service = new TaskService())
            using (Task task = service.GetTask(@"\RunDS4Windows"))
                EnsureTaskOwnership(task);

            switch (mode)
            {
                case StartupRegistrationMode.Disabled:
                    DeleteTaskEntry();
                    DeleteStartProgEntry();
                    break;
                case StartupRegistrationMode.Program:
                    WriteStartProgEntry();
                    DeleteTaskEntry();
                    break;
                case StartupRegistrationMode.Task:
                    WriteTaskEntry();
                    DeleteStartProgEntry();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        private static void RunTaskHelper(string command)
        {
            string currentUserSid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(currentUserSid))
                throw new InvalidOperationException("Windows did not provide the current account SID.");
            ProcessStartInfo startInfo = new()
            {
                FileName = DS4Windows.Global.exelocation,
                UseShellExecute = true,
                Verb = "runas",
            };
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(currentUserSid)));
            using Process process = Process.Start(startInfo);
            if (process == null || !process.WaitForExit(15000) || process.ExitCode != 0)
                throw new IOException("Windows did not confirm the elevated startup-task change.");
        }

        private static bool TaskIsEnabled(Task task) => task != null &&
            task.Enabled && task.Definition.Settings.Enabled &&
            task.Definition.Triggers.Count == 1 && task.Definition.Triggers[0].Enabled;

        private static bool TaskNeedsRepair(Task task) => StartupRegistrationPolicy.ShouldRepairTask(
            task != null, TaskIsEnabled(task), IsOwnedTask(task),
            task != null && TaskTargetsCurrentExecutable(task));

        private static bool IsOwnedTask(Task task)
        {
            if (task == null) return false;
            TaskDefinition definition = task.Definition;
            if (definition.Actions.Count != 1 || definition.Actions[0] is not ExecAction action ||
                definition.Triggers.Count != 1 || definition.Triggers[0] is not LogonTrigger trigger)
                return false;
            string sid = WindowsIdentity.GetCurrent().User?.Value;
            bool contract = definition.Principal.LogonType == TaskLogonType.InteractiveToken &&
                definition.Principal.RunLevel == TaskRunLevel.Highest &&
                (string.IsNullOrWhiteSpace(trigger.UserId) || AccountMatchesSid(trigger.UserId, sid)) &&
                string.Equals(action.Arguments?.Trim(), "-m", StringComparison.Ordinal) &&
                PathsEqual(action.WorkingDirectory, Path.GetDirectoryName(action.Path));
            return StartupRegistrationPolicy.OwnsTask(definition.RegistrationInfo.Description,
                AccountMatchesSid(definition.Principal.UserId, sid), contract,
                IsRecognizedExecutable(action.Path));
        }

        private static void EnsureTaskOwnership(Task task)
        {
            if (task != null && !IsOwnedTask(task))
                throw new InvalidOperationException(
                    "The RunDS4Windows task belongs to another owner or has an unrecognized configuration. It was not changed.");
        }

        private static void EnsureShortcutOwnership()
        {
            if (File.Exists(lnkpath) && !IsOwnedShortcut(lnkpath))
                throw new InvalidOperationException("The DS4Windows startup shortcut belongs to another application. It was not changed.");
        }

        private static bool IsRecognizedExecutable(string path)
        {
            if (PathsEqual(path, DS4Windows.Global.exelocation)) return true;
            try
            {
                return File.Exists(path) && string.Equals(
                    FileVersionInfo.GetVersionInfo(path).ProductName,
                    "DS4Windows", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool IsOwnedShortcut(string path)
        {
            if (!File.Exists(path)) return false;
            Type type = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"));
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                dynamic shortcut = shell.CreateShortcut(path);
                try
                {
                    string target = shortcut.TargetPath;
                    string arguments = shortcut.Arguments;
                    return IsRecognizedExecutable(target) &&
                        string.Equals(arguments?.Trim(), "-m", StringComparison.Ordinal);
                }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
            catch (COMException) { return false; }
            finally { Marshal.FinalReleaseComObject(shell); }
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
            using TaskService ts = new TaskService();
            using Task tasker = ts.GetTask(@"\RunDS4Windows");
            if (TaskIsEnabled(tasker) && IsOwnedTask(tasker))
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
