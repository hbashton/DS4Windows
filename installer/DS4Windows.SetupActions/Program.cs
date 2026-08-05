using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Text;

namespace DS4Windows.SetupActions
{
    internal static class Program
    {
        private const string RegistryKeyPath = @"SOFTWARE\DS4Windows";
        private const string InfrastructureVersion = "VIIPER-0.0.7+USBIP-0.9.7.7";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var action = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "install";
                var installRoot = ReadArgument(args, "--install-root") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DS4Windows");
                var bundleSource = ReadArgument(args, "--bundle-source");

                switch (action)
                {
                    case "preflight":
                        return Preflight();
                    case "install":
                    case "repair":
                        return InstallOrRepair(installRoot, bundleSource);
                    case "uninstall":
                        return Uninstall(installRoot);
                    case "probe":
                        return Probe(installRoot);
                    default:
                        throw new ArgumentException("Unknown setup action: " + action);
                }
            }
            catch (Exception ex)
            {
                WriteFallbackLog(ex.ToString());
                return 1;
            }
        }

        private static int InstallOrRepair(string installRoot, string bundleSource)
        {
            var ds4Path = Path.Combine(installRoot, "DS4Windows.exe");
            var extrasRoot = Path.Combine(installRoot, "extras");
            var scriptPath = Path.Combine(extrasRoot, "install-viiper-backend.ps1");
            if (!File.Exists(ds4Path) || !File.Exists(scriptPath))
            {
                throw new FileNotFoundException("The managed DS4Windows installation is incomplete.", scriptPath);
            }

            var targetUser = ResolveInteractiveUser();
            var arguments = new StringBuilder();
            arguments.Append("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ");
            arguments.Append(Quote(scriptPath));
            arguments.Append(" -NoPause -Yes -InstallerMode");
            arguments.Append(" -TargetDs4WindowsPath ").Append(Quote(ds4Path));
            arguments.Append(" -PackageExtrasRoot ").Append(Quote(extrasRoot));
            arguments.Append(" -TargetLocalAppData ").Append(Quote(targetUser.LocalAppData));
            arguments.Append(" -TargetUserSid ").Append(Quote(targetUser.Sid));
            arguments.Append(" -TargetUserName ").Append(Quote(targetUser.Name));

            var result = RunHidden("powershell.exe", arguments.ToString(), TimeSpan.FromMinutes(12));
            if (result == 0 || result == 3010)
            {
                using (var key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath))
                {
                    key?.SetValue("InfrastructureVersion", InfrastructureVersion, RegistryValueKind.String);
                }
            }
            if (result == 3010)
            {
                StageRebootResume(bundleSource);
            }
            else if (result == 0)
            {
                ClearRebootResume();
            }

            return result;
        }

        private static int Preflight()
        {
            foreach (var processName in new[] { "DS4Windows", "viiper" })
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        if (process.HasExited) continue;
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

        private static int Probe(string installRoot)
        {
            var viiper = Path.Combine(installRoot, "VIIPER", "viiper.exe");
            var usbip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe");
            var healthy = File.Exists(Path.Combine(installRoot, "DS4Windows.exe")) && File.Exists(viiper) && File.Exists(usbip);
            return healthy ? 0 : 1;
        }

        private static int Uninstall(string installRoot)
        {
            StopManagedProcesses(installRoot);
            RemoveOwnedTask("RunVIIPER", Path.Combine(installRoot, "VIIPER", "viiper.exe"));
            RemoveOwnedTask("RunDS4Windows", Path.Combine(installRoot, "DS4Windows.exe"));

            var viiperRoot = Path.GetFullPath(Path.Combine(installRoot, "VIIPER"));
            var expectedRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (viiperRoot.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(viiperRoot))
            {
                Directory.Delete(viiperRoot, true);
            }

            using (var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: true))
            {
                key?.DeleteValue("InfrastructureVersion", false);
            }
            ClearRebootResume();

            // USB-IP, HidHide, and FakerInput are shared system drivers. They are
            // deliberately not removed with DS4Windows; each has its own ARP entry.
            return 0;
        }

        private static InteractiveUser ResolveInteractiveUser()
        {
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
                        using (var profile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid))
                        {
                            var profilePath = Environment.ExpandEnvironmentVariables(profile?.GetValue("ProfileImagePath") as string ?? string.Empty);
                            var localAppData = Path.Combine(profilePath, "AppData", "Local");
                            if (Directory.Exists(localAppData)) return new InteractiveUser(name, sid, localAppData);
                        }
                    }
                }
            }
            catch { }

            var identity = WindowsIdentity.GetCurrent();
            return new InteractiveUser(identity.Name ?? Environment.UserName,
                identity.User?.Value ?? string.Empty,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        private static void StageRebootResume(string bundleSource)
        {
            if (string.IsNullOrWhiteSpace(bundleSource) || !File.Exists(bundleSource)) return;
            var resumeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DS4Windows", "Installer", "resume");
            Directory.CreateDirectory(resumeRoot);
            var stagedBundle = Path.Combine(resumeRoot, "DS4Windows_Setup_x64.exe");
            File.Copy(bundleSource, stagedBundle, true);
            using (var runOnce = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"))
            {
                runOnce?.SetValue("DS4WindowsSetupResume", Quote(stagedBundle) + " /repair", RegistryValueKind.String);
            }
        }

        private static void ClearRebootResume()
        {
            using (var runOnce = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", writable: true))
            {
                runOnce?.DeleteValue("DS4WindowsSetupResume", false);
            }
            var stagedBundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DS4Windows", "Installer", "resume", "DS4Windows_Setup_x64.exe");
            try { if (File.Exists(stagedBundle)) File.Delete(stagedBundle); } catch { }
        }

        private static void StopManagedProcesses(string installRoot)
        {
            foreach (var processName in new[] { "DS4Windows", "viiper" })
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(installRoot), StringComparison.OrdinalIgnoreCase))
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

        private static void RemoveOwnedTask(string taskName, string expectedExecutable)
        {
            var query = RunCaptured("schtasks.exe", "/Query /TN " + Quote(taskName) + " /XML", TimeSpan.FromSeconds(10), out var output);
            if (query != 0 || output.IndexOf(expectedExecutable, StringComparison.OrdinalIgnoreCase) < 0)
            {
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
                process.StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                process.Start();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException(fileName + " did not finish within " + timeout + ".");
                }
                output = stdout + Environment.NewLine + stderr;
                WriteFallbackLog(output);
                return process.ExitCode;
            }
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
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DS4Windows", "Installer");
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "setup-actions.log"), DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private sealed class InteractiveUser
        {
            internal InteractiveUser(string name, string sid, string localAppData)
            {
                Name = name;
                Sid = sid;
                LocalAppData = localAppData;
            }

            internal string Name { get; }
            internal string Sid { get; }
            internal string LocalAppData { get; }
        }
    }
}
