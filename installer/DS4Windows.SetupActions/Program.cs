using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Xml;

namespace DS4Windows.SetupActions
{
    internal static class Program
    {
        private const string RegistryKeyPath = @"SOFTWARE\DS4Windows";
        private const string InfrastructureVersion =
            "VIIPER-0.0.8+USBIP-0.9.7.7";
        private static readonly string InstallerLogRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DS4Windows", "Installer");
        private static readonly string InfrastructureLogPath = Path.Combine(
            InstallerLogRoot, "infrastructure-actions.log");

        [STAThread]
        private static int Main(string[] args)
        {
            var invocationId = Guid.NewGuid().ToString("N");
            WriteFallbackLog("=== DS4Windows setup invocation " +
                invocationId + " started ===");
            try
            {
                var action = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "install";
                var installRoot = ReadArgument(args, "--install-root") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DS4Windows");
                var bundleSource = ReadArgument(args, "--bundle-source");

                if (action != "preflight")
                {
                    installRoot = ValidateManagedInstallRoot(installRoot);
                }

                int exitCode;
                switch (action)
                {
                    case "preflight":
                        exitCode = Preflight();
                        break;
                    case "install":
                    case "repair":
                        exitCode = InstallOrRepair(installRoot, bundleSource, args);
                        break;
                    case "uninstall":
                        exitCode = Uninstall(installRoot);
                        break;
                    case "probe":
                        exitCode = Probe(installRoot);
                        break;
                    default:
                        throw new ArgumentException("Unknown setup action: " + action);
                }
                WriteFallbackLog("=== DS4Windows setup invocation " +
                    invocationId + " completed with exit code " +
                    exitCode + " ===");
                return exitCode;
            }
            catch (Exception ex)
            {
                WriteFallbackLog("=== DS4Windows setup invocation " +
                    invocationId + " failed ===" + Environment.NewLine + ex);
                return 1;
            }
        }

        private static int InstallOrRepair(string installRoot,
            string bundleSource, string[] args)
        {
            var ds4Path = Path.Combine(installRoot, "DS4Windows.exe");
            var extrasRoot = Path.Combine(installRoot, "extras");
            var scriptPath = Path.Combine(extrasRoot, "install-viiper-backend.ps1");
            if (!File.Exists(ds4Path) || !File.Exists(scriptPath))
            {
                throw new FileNotFoundException("The managed DS4Windows installation is incomplete.", scriptPath);
            }

            var targetUser = ResolveInteractiveUser(args);
            return RunWithSetupMutex(() => InstallOrRepairLocked(ds4Path,
                extrasRoot, scriptPath, bundleSource, targetUser));
        }

        private static int InstallOrRepairLocked(string ds4Path,
            string extrasRoot, string scriptPath, string bundleSource,
            InteractiveUser targetUser)
        {
            ResetInfrastructureLog(targetUser);
            WriteFallbackLog("Infrastructure setup starting for interactive user " +
                targetUser.Name + " (" + targetUser.Sid + ").");
            var arguments = new StringBuilder();
            arguments.Append("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ");
            arguments.Append(Quote(scriptPath));
            arguments.Append(" -NoPause -Yes -InstallerMode -SetupMutexAlreadyHeld");
            arguments.Append(" -TargetDs4WindowsPath ").Append(Quote(ds4Path));
            arguments.Append(" -PackageExtrasRoot ").Append(Quote(extrasRoot));
            arguments.Append(" -TargetLocalAppData ").Append(Quote(targetUser.LocalAppData));
            arguments.Append(" -TargetUserSid ").Append(Quote(targetUser.Sid));
            arguments.Append(" -TargetUserName ").Append(Quote(targetUser.Name));

            var elevatedSid = WindowsIdentity.GetCurrent().User?.Value;
            var alternateAdministrator = !string.Equals(elevatedSid,
                targetUser.Sid, StringComparison.OrdinalIgnoreCase);
            if (alternateAdministrator)
            {
                // Alternate administrator credentials are valid for a
                // per-machine install, but Windows cannot register a
                // highest-interactive task for a different standard user
                // without storing credentials. Install everything and defer
                // those two optional startup tasks instead of failing Burn.
                arguments.Append(" -SkipStartupTasks");
                WriteFallbackLog("Setup is elevated as " +
                    (WindowsIdentity.GetCurrent().Name ?? elevatedSid) +
                    "; startup task registration for " + targetUser.Name +
                    " is deferred.");
            }

            var result = RunCaptured("powershell.exe", arguments.ToString(),
                Timeout.InfiniteTimeSpan, out var scriptOutput);
            WriteInfrastructureLog(scriptOutput);
            WriteFallbackLog("Infrastructure setup exited with code " + result +
                ". Detailed log: " + InfrastructureLogPath);
            if (result == 0 && !IsInfrastructureCommitted())
            {
                WriteFallbackLog("Infrastructure script returned success without " +
                    "atomically committing the verified readiness marker.");
                result = 1;
            }
            if (result == 3010)
            {
                // Burn owns normal reboot resume. Its per-machine RunOnce entry
                // does not execute for a standard user's logon, so add one
                // target-user Startup shortcut only when setup was elevated
                // with alternate administrator credentials.
                if (alternateAdministrator)
                {
                    try
                    {
                        StageRebootResume(bundleSource, targetUser);
                    }
                    catch (Exception ex)
                    {
                        // A reboot boundary has already been reached. Do not
                        // turn optional auto-resume staging into a fatal error
                        // that makes Burn roll back the installed application.
                        WriteFallbackLog("Automatic standard-user resume could " +
                            "not be staged: " + ex.Message +
                            ". Burn remains resumable when setup is run again.");
                    }
                }
            }
            else if (result == 0)
            {
                ClearRebootResume();
            }

            return result;
        }

        private static int Preflight()
        {
            return RunWithSetupMutex(PreflightLocked);
        }

        private static int PreflightLocked()
        {
            foreach (var processName in new[] { "DS4Windows", "viiper" })
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        if (process.HasExited) continue;
                        if (!IsRecognizedProductProcess(process, processName,
                                out var executablePath))
                        {
                            throw new InvalidOperationException(
                                "A process named " + processName + " (PID " +
                                process.Id + ") is not a verified DS4Windows " +
                                "package executable. Close it manually before " +
                                "setup continues. Observed path: " +
                                (executablePath ?? "<unavailable>"));
                        }
                        var closedGracefully = process.CloseMainWindow() && process.WaitForExit(5000);
                        if (!closedGracefully && !process.HasExited)
                        {
                            process.Kill();
                            if (!process.WaitForExit(5000))
                            {
                                throw new InvalidOperationException("Could not close " + processName + " (PID " + process.Id + ").");
                            }
                        }
                    }
                    finally { process.Dispose(); }
                }
            }
            return 0;
        }

        private static bool IsRecognizedProductProcess(Process process,
            string processName, out string executablePath)
        {
            executablePath = null;
            try
            {
                executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath) ||
                    !File.Exists(executablePath))
                {
                    return false;
                }

                var version = FileVersionInfo.GetVersionInfo(executablePath);
                if (string.Equals(processName, "DS4Windows",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(version.ProductName, "DS4Windows",
                               StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(version.FileDescription, "DS4Windows",
                               StringComparison.OrdinalIgnoreCase);
                }
                return string.Equals(version.ProductName, "VIIPER",
                           StringComparison.OrdinalIgnoreCase) ||
                       (version.FileDescription?.StartsWith("VIIPER",
                            StringComparison.OrdinalIgnoreCase) ?? false);
            }
            catch
            {
                return false;
            }
        }

        private static int Probe(string installRoot)
        {
            var viiper = Path.Combine(installRoot, "VIIPER", "viiper.exe");
            var usbip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe");
            var healthy = File.Exists(Path.Combine(installRoot, "DS4Windows.exe")) && File.Exists(viiper) && File.Exists(usbip);
            return healthy ? 0 : 1;
        }

        private static int Uninstall(string installRoot)
        {
            return RunWithSetupMutex(() => UninstallLocked(installRoot));
        }

        private static int UninstallLocked(string installRoot)
        {
            StopManagedProcesses(installRoot);
            RemoveOwnedTask("RunVIIPER", Path.Combine(installRoot, "VIIPER", "viiper.exe"));
            RemoveOwnedTask("RunDS4Windows", Path.Combine(installRoot, "DS4Windows.exe"));

            var viiperRoot = Path.GetFullPath(Path.Combine(installRoot, "VIIPER"));
            var expectedRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (viiperRoot.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(viiperRoot))
            {
                EnsureTreeHasNoReparsePoints(viiperRoot);
                Directory.Delete(viiperRoot, true);
            }

            using (var key = OpenMachineKey64(RegistryKeyPath, writable: true))
            {
                key?.DeleteValue("InfrastructureVersion", false);
                key?.SetValue("InfrastructureState", "Uninstalled",
                    RegistryValueKind.String);
                key?.SetValue("InfrastructureStateUtc",
                    DateTime.UtcNow.ToString("O"), RegistryValueKind.String);
            }
            ClearRebootResume();

            // USB-IP, HidHide, and FakerInput are shared system drivers. They are
            // deliberately not removed with DS4Windows; each has its own ARP entry.
            return 0;
        }

        private static int RunWithSetupMutex(Func<int> action)
        {
            using (var setupMutex = new Mutex(false,
                       @"Global\DS4Windows-VIIPER-Setup"))
            {
                var mutexOwned = false;
                try
                {
                    try
                    {
                        mutexOwned = setupMutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        mutexOwned = true;
                    }
                    if (!mutexOwned)
                    {
                        WriteFallbackLog(
                            "Another DS4Windows VIIPER setup owns the global " +
                            "setup mutex; returning Windows Installer busy (1618).");
                        return 1618;
                    }
                    return action();
                }
                finally
                {
                    if (mutexOwned)
                    {
                        try { setupMutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        private static InteractiveUser ResolveInteractiveUser(string[] args)
        {
            var suppliedSid = ReadArgument(args, "--target-user-sid");
            var suppliedName = ReadArgument(args, "--target-user-name");
            var suppliedLocal = ReadArgument(args, "--target-local-appdata");
            var suppliedRoaming = ReadArgument(args,
                "--target-roaming-appdata");
            var supplied = new[] { suppliedSid, suppliedName, suppliedLocal,
                suppliedRoaming };
            if (supplied.Any(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (supplied.Any(string.IsNullOrWhiteSpace))
                {
                    throw new InvalidOperationException(
                        "Installer user context is incomplete.");
                }
                return ValidateSuppliedInteractiveUser(suppliedSid,
                    suppliedName, suppliedLocal, suppliedRoaming);
            }

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem"))
                using (var results = searcher.Get())
                {
                    var name = results.Cast<ManagementObject>()
                        .Select(value => value["UserName"] as string)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var sid = ((SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier))).Value;
                        using (var profile = OpenMachineKey64(
                                   @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid))
                        {
                            var profilePath = Environment.ExpandEnvironmentVariables(profile?.GetValue("ProfileImagePath") as string ?? string.Empty);
                            var localAppData = Path.Combine(profilePath, "AppData", "Local");
                            var roamingAppData = Path.Combine(profilePath,
                                "AppData", "Roaming");
                            if (Directory.Exists(localAppData) &&
                                Directory.Exists(roamingAppData))
                            {
                                return new InteractiveUser(name, sid,
                                    localAppData, roamingAppData);
                            }
                        }
                    }
                }
            }
            catch { }

            var identity = WindowsIdentity.GetCurrent();
            return new InteractiveUser(identity.Name ?? Environment.UserName,
                identity.User?.Value ?? string.Empty,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        }

        private static InteractiveUser ValidateSuppliedInteractiveUser(
            string sid, string name, string localAppData,
            string roamingAppData)
        {
            var securityIdentifier = new SecurityIdentifier(sid);
            using (var profile = OpenMachineKey64(
                       @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" +
                       securityIdentifier.Value))
            {
                var profileValue = profile?.GetValue("ProfileImagePath") as string;
                if (string.IsNullOrWhiteSpace(profileValue))
                {
                    throw new InvalidOperationException(
                        "Windows could not validate the installer user's profile.");
                }

                var profilePath = Environment.ExpandEnvironmentVariables(
                    profileValue);
                var expectedLocal = Path.GetFullPath(Path.Combine(profilePath,
                    "AppData", "Local"));
                var expectedRoaming = Path.GetFullPath(Path.Combine(profilePath,
                    "AppData", "Roaming"));
                if (!PathsEqual(expectedLocal, localAppData) ||
                    !PathsEqual(expectedRoaming, roamingAppData))
                {
                    throw new InvalidOperationException(
                        "Installer user folders did not match the registered " +
                        "Windows profile.");
                }

                var registeredName = ((NTAccount)securityIdentifier.Translate(
                    typeof(NTAccount))).Value;
                if (!string.Equals(registeredName, name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Installer user identity did not match its Windows SID.");
                }
                return new InteractiveUser(registeredName,
                    securityIdentifier.Value, expectedLocal, expectedRoaming);
            }
        }

        private static bool PathsEqual(string expected, string actual)
        {
            if (string.IsNullOrWhiteSpace(actual)) return false;
            return string.Equals(Path.GetFullPath(expected)
                    .TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void StageRebootResume(string bundleSource,
            InteractiveUser targetUser)
        {
            if (string.IsNullOrWhiteSpace(bundleSource) ||
                !File.Exists(bundleSource))
            {
                throw new FileNotFoundException(
                    "The original installer is unavailable for reboot resume.",
                    bundleSource);
            }
            var resumeRoot = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                "DS4Windows", "Installer", "resume");
            EnsureDirectoryPathHasNoReparsePoints(resumeRoot);
            Directory.CreateDirectory(resumeRoot);
            EnsureDirectoryPathHasNoReparsePoints(resumeRoot);
            ProtectResumeDirectory(resumeRoot, targetUser.Sid);
            var stagedBundle = Path.Combine(resumeRoot, "DS4Windows_Setup_x64.exe");
            if (!string.Equals(Path.GetFullPath(bundleSource),
                    Path.GetFullPath(stagedBundle),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(bundleSource, stagedBundle, true);
            }
            if (!HashesEqual(bundleSource, stagedBundle))
            {
                throw new InvalidOperationException(
                    "The reboot-resume installer copy failed verification.");
            }

            var startupDirectory = Path.Combine(targetUser.RoamingAppData,
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            EnsureDirectoryPathHasNoReparsePoints(startupDirectory);
            Directory.CreateDirectory(startupDirectory);
            EnsureDirectoryPathHasNoReparsePoints(startupDirectory);
            var shortcutPath = Path.Combine(startupDirectory,
                "DS4Windows Setup Resume.lnk");
            try
            {
                CreateShortcut(shortcutPath, stagedBundle, "/repair",
                    resumeRoot);
                using (var key = CreateMachineKey64(RegistryKeyPath))
                {
                    key?.SetValue("SetupResumeShortcut", shortcutPath,
                        RegistryValueKind.String);
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                }
                catch { }
                throw;
            }
        }

        private static void ClearRebootResume()
        {
            // Remove the obsolete custom RunOnce value from earlier builds.
            // Burn owns its own GUID-named resume entry.
            try
            {
                using (var runOnce = OpenMachineKey64(
                           @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                           writable: true))
                {
                    runOnce?.DeleteValue("DS4WindowsSetupResume", false);
                }
            }
            catch (Exception ex)
            {
                WriteFallbackLog("Could not remove the legacy resume value: " +
                    ex.Message);
            }

            string shortcutPath = null;
            try
            {
                using (var key = OpenMachineKey64(
                           RegistryKeyPath, writable: true))
                {
                    shortcutPath = key?.GetValue("SetupResumeShortcut") as string;
                    key?.DeleteValue("SetupResumeShortcut", false);
                }
            }
            catch (Exception ex)
            {
                WriteFallbackLog("Could not clear the resume shortcut marker: " +
                    ex.Message);
            }
            try
            {
                if (IsOwnedResumeShortcut(shortcutPath) &&
                    File.Exists(shortcutPath))
                {
                    File.Delete(shortcutPath);
                }
            }
            catch { }

            var resumeRoot = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                "DS4Windows", "Installer", "resume");
            try
            {
                EnsureDirectoryPathHasNoReparsePoints(resumeRoot);
                var stagedBundle = Path.Combine(resumeRoot,
                    "DS4Windows_Setup_x64.exe");
                if (File.Exists(stagedBundle) &&
                    (File.GetAttributes(stagedBundle) &
                     FileAttributes.ReparsePoint) == 0)
                {
                    File.Delete(stagedBundle);
                }
            }
            catch (Exception ex)
            {
                WriteFallbackLog("Refused unsafe resume-cache cleanup: " +
                    ex.Message);
            }
        }

        private static void EnsureDirectoryPathHasNoReparsePoints(string path)
        {
            var resolved = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(resolved);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException(
                    "A rooted directory path is required.");
            }

            var cursor = root;
            var relative = resolved.Substring(root.Length);
            foreach (var component in relative.Split(new[]
                     { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, component);
                if (!Directory.Exists(cursor) && !File.Exists(cursor)) continue;
                var attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Directory path traverses a reparse point: " + cursor);
                }
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new InvalidOperationException(
                        "Directory path traverses a file: " + cursor);
                }
            }
        }

        private static void ProtectResumeDirectory(string resumeRoot,
            string targetUserSid)
        {
            var ownerExit = RunCaptured("icacls.exe",
                Quote(resumeRoot) + " /setowner " +
                Quote("*S-1-5-32-544") + " /Q",
                TimeSpan.FromSeconds(15), out var ownerOutput);
            if (ownerExit != 0)
            {
                throw new InvalidOperationException(
                    "Could not secure ownership of the reboot-resume " +
                    "directory: " + ownerOutput.Trim());
            }
            var arguments = Quote(resumeRoot) +
                " /inheritance:r /grant:r " +
                Quote("*S-1-5-18:(OI)(CI)(F)") + " " +
                Quote("*S-1-5-32-544:(OI)(CI)(F)") + " " +
                Quote("*" + targetUserSid + ":(OI)(CI)(RX)") + " /Q";
            var exitCode = RunCaptured("icacls.exe", arguments,
                TimeSpan.FromSeconds(15), out var output);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    "Could not protect the reboot-resume directory: " +
                    output.Trim());
            }
        }

        private static bool HashesEqual(string first, string second)
        {
            using (var algorithm = SHA256.Create())
            using (var firstStream = File.OpenRead(first))
            using (var secondStream = File.OpenRead(second))
            {
                var firstHash = algorithm.ComputeHash(firstStream);
                algorithm.Initialize();
                var secondHash = algorithm.ComputeHash(secondStream);
                return firstHash.SequenceEqual(secondHash);
            }
        }

        private static bool IsOwnedResumeShortcut(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !string.Equals(Path.GetFileName(path),
                    "DS4Windows Setup Resume.lnk",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path))?
                .TrimEnd(Path.DirectorySeparatorChar);
            var expectedSuffix = Path.Combine("Microsoft", "Windows",
                "Start Menu", "Programs", "Startup");
            return directory != null && directory.EndsWith(expectedSuffix,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void CreateShortcut(string shortcutPath,
            string targetPath, string arguments, string workingDirectory)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell", true);
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell,
                    new object[] { shortcutPath });
                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath",
                    BindingFlags.SetProperty, null, shortcut,
                    new object[] { targetPath });
                shortcutType.InvokeMember("Arguments",
                    BindingFlags.SetProperty, null, shortcut,
                    new object[] { arguments });
                shortcutType.InvokeMember("WorkingDirectory",
                    BindingFlags.SetProperty, null, shortcut,
                    new object[] { workingDirectory });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod,
                    null, shortcut, null);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
        }

        private static bool IsInfrastructureCommitted()
        {
            string observedVersion;
            string observedState;
            // The PowerShell child and WiX registry searches use the 64-bit
            // machine view. Select it explicitly here as well so WOW64 cannot
            // turn a successful install into a false failure and rollback.
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine,
                       RegistryView.Registry64))
            using (var key = machine.OpenSubKey(RegistryKeyPath))
            {
                observedVersion = key?.GetValue(
                    "InfrastructureVersion") as string;
                observedState = key?.GetValue(
                    "InfrastructureState") as string;
            }

            if (string.Equals(observedVersion, InfrastructureVersion,
                    StringComparison.Ordinal) &&
                string.Equals(observedState, "Ready",
                    StringComparison.Ordinal))
            {
                return true;
            }

            WriteFallbackLog("Infrastructure readiness postcondition failed. " +
                "Expected " + InfrastructureVersion + "/Ready; observed " +
                (observedVersion ?? "<missing>") + "/" +
                (observedState ?? "<missing>") + " in Registry64.");
            return false;
        }

        private static string ValidateManagedInstallRoot(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                throw new InvalidOperationException(
                    "The managed installation path is empty.");
            }

            var expected = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "DS4Windows")).TrimEnd(Path.DirectorySeparatorChar);
            var actual = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(actual, expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The standard installer only manages " + expected + ".");
            }

            var current = new DirectoryInfo(actual);
            while (current != null &&
                   current.FullName.StartsWith(
                       Environment.GetFolderPath(
                           Environment.SpecialFolder.ProgramFiles),
                       StringComparison.OrdinalIgnoreCase))
            {
                if (current.Exists &&
                    (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "The managed installation path contains a reparse point: " +
                        current.FullName);
                }
                current = current.Parent;
            }
            return actual;
        }

        private static RegistryKey OpenMachineKey64(string path,
            bool writable = false)
        {
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                return machine.OpenSubKey(path, writable);
            }
        }

        private static RegistryKey CreateMachineKey64(string path)
        {
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                return machine.CreateSubKey(path, writable: true);
            }
        }

        private static void StopManagedProcesses(string installRoot)
        {
            var managedPrefix = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            foreach (var processName in new[] { "DS4Windows", "viiper" })
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path != null && Path.GetFullPath(path).StartsWith(
                                managedPrefix,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            if (!process.CloseMainWindow() || !process.WaitForExit(3000))
                            {
                                process.Kill();
                                process.WaitForExit(3000);
                            }
                        }
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
        }

        private static void EnsureTreeHasNoReparsePoints(string root)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(root));
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Refusing to remove a reparse-point installation directory: " +
                    directory.FullName);
            }

            var pending = new Stack<DirectoryInfo>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                foreach (var entry in pending.Pop().GetFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            "Refusing to remove an installation tree containing " +
                            "a reparse point: " + entry.FullName);
                    }
                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child);
                    }
                }
            }
        }

        private static void RemoveOwnedTask(string taskName, string expectedExecutable)
        {
            var query = RunCaptured("schtasks.exe", "/Query /TN " + Quote(taskName) + " /XML", TimeSpan.FromSeconds(10), out var output);
            if (query != 0)
            {
                return;
            }

            try
            {
                var document = new XmlDocument { XmlResolver = null };
                document.LoadXml(output.Trim());
                var command = document.SelectSingleNode(
                    "//*[local-name()='Exec']/*[local-name()='Command']")?
                    .InnerText?.Trim();
                if (!PathsEqual(expectedExecutable, command)) return;
            }
            catch
            {
                // Never delete a task whose action could not be parsed and
                // compared exactly to the installer-owned executable.
                return;
            }

            RunHidden("schtasks.exe", "/Delete /TN " + Quote(taskName) + " /F", TimeSpan.FromSeconds(10));
        }

        private static int RunHidden(string fileName, string arguments, TimeSpan timeout)
        {
            return RunCaptured(fileName, arguments, timeout, out _);
        }

        private static int RunCaptured(string fileName, string arguments, TimeSpan timeout, out string output)
        {
            using (var process = new Process())
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                process.OutputDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null) stdout.AppendLine(eventArgs.Data);
                };
                process.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data != null) stderr.AppendLine(eventArgs.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var completed = timeout == Timeout.InfiniteTimeSpan
                    ? WaitWithoutTimeout(process)
                    : process.WaitForExit((int)timeout.TotalMilliseconds);
                if (!completed)
                {
                    KillProcessTree(process);
                    throw new TimeoutException(fileName + " did not finish within " + timeout + ".");
                }
                // Flush the final asynchronous output events after process exit.
                process.WaitForExit();
                output = stdout.ToString() + Environment.NewLine +
                    stderr.ToString();
                WriteFallbackLog(output);
                return process.ExitCode;
            }
        }

        private static bool WaitWithoutTimeout(Process process)
        {
            // Infrastructure setup may be waiting inside a signed kernel-driver
            // installer. Keep this process and the global setup mutex alive
            // until it exits; automatically killing that tree is unsafe.
            process.WaitForExit();
            return true;
        }

        private static void KillProcessTree(Process process)
        {
            try
            {
                using (var taskkill = Process.Start(new ProcessStartInfo(
                    "taskkill.exe", "/PID " + process.Id + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                }))
                {
                    taskkill?.WaitForExit(10000);
                }
            }
            catch { }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch { }
        }

        private static string ReadArgument(string[] args, string name)
        {
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static void WriteFallbackLog(string message)
        {
            try
            {
                EnsureDirectoryPathHasNoReparsePoints(InstallerLogRoot);
                Directory.CreateDirectory(InstallerLogRoot);
                EnsureDirectoryPathHasNoReparsePoints(InstallerLogRoot);
                AppendLogWithRetry(Path.Combine(InstallerLogRoot,
                    "setup-actions.log"), DateTime.Now.ToString("O") +
                    " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static void WriteInfrastructureLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                EnsureDirectoryPathHasNoReparsePoints(InstallerLogRoot);
                Directory.CreateDirectory(InstallerLogRoot);
                EnsureDirectoryPathHasNoReparsePoints(InstallerLogRoot);
                AppendLogWithRetry(InfrastructureLogPath,
                    Environment.NewLine + message + Environment.NewLine);
            }
            catch { }
        }

        private static void AppendLogWithRetry(string path, string message)
        {
            var bytes = new UTF8Encoding(false).GetBytes(message);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Append,
                               FileAccess.Write, FileShare.Read))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                    return;
                }
                catch (IOException) when (attempt < 9)
                {
                    Thread.Sleep(25);
                }
            }
        }

        private static void ResetInfrastructureLog(InteractiveUser targetUser)
        {
            try
            {
                EnsureDirectoryPathHasNoReparsePoints(InstallerLogRoot);
                Directory.CreateDirectory(InstallerLogRoot);
                EnsureDirectoryPathHasNoReparsePoints(InstallerLogRoot);
                File.WriteAllText(InfrastructureLogPath,
                    DateTime.Now.ToString("O") +
                    " Infrastructure setup started for " + targetUser.Name +
                    " (" + targetUser.Sid + ")." + Environment.NewLine);
            }
            catch { }
        }

        private sealed class InteractiveUser
        {
            internal InteractiveUser(string name, string sid,
                string localAppData, string roamingAppData)
            {
                Name = name;
                Sid = sid;
                LocalAppData = localAppData;
                RoamingAppData = roamingAppData;
            }

            internal string Name { get; }
            internal string Sid { get; }
            internal string LocalAppData { get; }
            internal string RoamingAppData { get; }
        }
    }
}
