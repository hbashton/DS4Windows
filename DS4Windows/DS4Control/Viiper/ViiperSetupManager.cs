/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;

namespace DS4Windows
{
    public sealed class ViiperPrerequisiteStatus
    {
        public bool ViiperInstalled { get; set; }
        public bool ServerRunning { get; set; }
        public bool UsbipInstalled { get; set; }
        public bool SetupScriptFound { get; set; }
        public string ViiperPath { get; set; }
        public string SetupScriptPath { get; set; }

        public bool Ready => ServerRunning && UsbipInstalled;

        public string DisplayText
        {
            get
            {
                if (Ready)
                {
                    return "VIIPER ready";
                }

                if (!UsbipInstalled && !ViiperInstalled)
                {
                    return "VIIPER and usbip-win2 need setup";
                }

                if (!UsbipInstalled)
                {
                    return "usbip-win2 driver missing";
                }

                if (!ViiperInstalled)
                {
                    return "VIIPER helper missing";
                }

                return ServerRunning ? "VIIPER status unknown" : "VIIPER server not running";
            }
        }
    }

    public static class ViiperSetupManager
    {
        public const string ApiHost = "127.0.0.1";
        public const int ApiPort = 3242;
        public const string UsbipWin2ReleasesUrl = "https://github.com/vadimgrn/usbip-win2/releases";
        public const string ViiperReleasesUrl = "https://github.com/hbashton/VIIPER/releases";

        private const string InstallerScriptName = "install-viiper-backend.ps1";
        private static readonly object serverStartLock = new object();
        private static DateTime lastServerStartAttemptUtc = DateTime.MinValue;
        private static int promptShownThisSession;
        private static int installerRunning;

        public static bool IsViiperOutputType(OutContType type) => ViiperOutDevice.IsViiperType(type);

        public static ViiperPrerequisiteStatus GetStatus(bool tryStartServer = false)
        {
            string viiperPath = GetViiperExePath();
            string setupScriptPath = GetSetupScriptPath();
            ViiperPrerequisiteStatus status = new ViiperPrerequisiteStatus
            {
                ViiperPath = viiperPath,
                SetupScriptPath = setupScriptPath,
                ViiperInstalled = File.Exists(viiperPath),
                SetupScriptFound = File.Exists(setupScriptPath),
                UsbipInstalled = IsUsbipWin2Installed(),
                ServerRunning = CanPingServer(),
            };

            if (tryStartServer && !status.ServerRunning && status.ViiperInstalled)
            {
                TryStartServerOnce(viiperPath);
                status.ServerRunning = CanPingServer();
            }

            return status;
        }

        public static bool EnsureReadyWithPrompt(Window owner, bool forcePrompt = false)
        {
            ViiperPrerequisiteStatus status = GetStatus(tryStartServer: true);
            if (status.Ready)
            {
                return true;
            }

            if (Volatile.Read(ref promptShownThisSession) == 1 && !forcePrompt)
            {
                return false;
            }

            Interlocked.Exchange(ref promptShownThisSession, 1);
            string message =
                "This profile uses a VIIPER virtual controller output.\n\n" +
                "DS4Windows needs two pieces installed before this can work:\n" +
                "- VIIPER helper/server\n" +
                "- usbip-win2 Windows USB/IP driver\n\n" +
                $"Current status: {status.DisplayText}\n\n" +
                "Install or repair VIIPER support now?";

            MessageBoxResult result = owner != null
                ? MessageBox.Show(owner, message, "VIIPER virtual controller setup", MessageBoxButton.YesNo, MessageBoxImage.Information)
                : MessageBox.Show(message, "VIIPER virtual controller setup", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
            {
                return false;
            }

            return LaunchInstaller(status, owner);
        }

        public static bool LaunchInstaller(ViiperPrerequisiteStatus status = null, Window owner = null)
        {
            status ??= GetStatus();
            if (!status.SetupScriptFound)
            {
                string message =
                    "DS4Windows could not find the bundled VIIPER setup script.\n\n" +
                    "Opening the VIIPER and usbip-win2 release pages instead.";
                if (owner != null)
                {
                    MessageBox.Show(owner, message, "VIIPER setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(message, "VIIPER setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                Util.StartProcessHelper(ViiperReleasesUrl);
                Util.StartProcessHelper(UsbipWin2ReleasesUrl);
                return false;
            }

            if (Interlocked.CompareExchange(ref installerRunning, 1, 0) != 0)
            {
                ShowInstallerMessage(owner,
                    "VIIPER setup is already running. Finish the open setup window, then use Refresh to verify it.",
                    "VIIPER setup", MessageBoxImage.Information);
                return true;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{status.SetupScriptPath}\" -NoPause",
                    UseShellExecute = true,
                    Verb = "runas",
                };
                Process process = Process.Start(startInfo);
                if (process == null)
                {
                    throw new InvalidOperationException(
                        "Windows did not start the setup process.");
                }

                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => InstallerProcess_Exited(process,
                    owner);
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Interlocked.Exchange(ref installerRunning, 0);
                ShowInstallerMessage(owner,
                    "VIIPER setup was canceled at the Windows administrator prompt. No changes were made.",
                    "VIIPER setup canceled", MessageBoxImage.Information);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref installerRunning, 0);
                string message = $"Could not launch VIIPER setup: {ex.Message}";
                ShowInstallerMessage(owner, message, "VIIPER setup",
                    MessageBoxImage.Error);

                return false;
            }
        }

        private static void InstallerProcess_Exited(Process process,
            Window owner)
        {
            int exitCode = -1;
            try { exitCode = process.ExitCode; } catch { }
            try { process.Dispose(); } catch { }
            Interlocked.Exchange(ref installerRunning, 0);

            Application application = Application.Current;
            if (application?.Dispatcher == null ||
                application.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            application.Dispatcher.BeginInvoke(new Action(() =>
            {
                ViiperPrerequisiteStatus refreshed = GetStatus(
                    tryStartServer: true);
                if (exitCode == 0 && refreshed.Ready)
                {
                    Interlocked.Exchange(ref promptShownThisSession, 0);
                    ShowInstallerMessage(owner,
                        "VIIPER setup finished successfully. Virtual controllers are ready.",
                        "VIIPER is ready", MessageBoxImage.Information);
                    return;
                }

                string logPath = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "VIIPER", "install.log");
                string message = exitCode == 0
                    ? "VIIPER was installed, but Windows is not reporting every component as ready yet. Restart Windows once, then click Refresh."
                    : $"VIIPER setup could not finish (exit code {exitCode}). Review the setup log and try Repair again.\n\n{logPath}";
                ShowInstallerMessage(owner, message, "VIIPER setup",
                    exitCode == 0 ? MessageBoxImage.Warning :
                        MessageBoxImage.Error);
            }));
        }

        private static void ShowInstallerMessage(Window owner, string message,
            string caption, MessageBoxImage image)
        {
            if (owner != null && owner.IsLoaded)
            {
                MessageBox.Show(owner, message, caption, MessageBoxButton.OK,
                    image);
            }
            else
            {
                MessageBox.Show(message, caption, MessageBoxButton.OK, image);
            }
        }

        private static string GetViiperExePath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "VIIPER", "viiper.exe");
        }

        private static string GetSetupScriptPath()
        {
            return Path.Combine(Global.exedirpath, "extras", InstallerScriptName);
        }

        private static bool TryStartServerOnce(string viiperPath)
        {
            lock (serverStartLock)
            {
                if (CanPingServer())
                {
                    return true;
                }

                DateTime now = DateTime.UtcNow;
                if ((now - lastServerStartAttemptUtc).TotalSeconds < 3)
                {
                    return false;
                }

                lastServerStartAttemptUtc = now;
                return TryStartServer(viiperPath);
            }
        }

        private static bool TryStartServer(string viiperPath)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = viiperPath,
                    Arguments = "server",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                };
                Process.Start(startInfo);
                System.Threading.Thread.Sleep(750);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool CanPingServer()
        {
            try
            {
                using TcpClient tcp = new TcpClient
                {
                    NoDelay = true,
                    SendTimeout = 500,
                    ReceiveTimeout = 1000,
                };

                IAsyncResult result = tcp.BeginConnect(ApiHost, ApiPort, null, null);
                if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(750)))
                {
                    return false;
                }

                tcp.EndConnect(result);
                NetworkStream stream = tcp.GetStream();
                byte[] request = Encoding.UTF8.GetBytes("ping\0");
                stream.Write(request, 0, request.Length);

                byte[] buffer = new byte[256];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return false;
                }

                string response = Encoding.UTF8.GetString(buffer, 0, read);
                return response.IndexOf("VIIPER", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUsbipWin2Installed()
        {
            string driverPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "usbip2_ude.sys");
            if (File.Exists(driverPath))
            {
                return true;
            }

            return RegistryUninstallContains("USB/IP") ||
                RegistryUninstallContains("USBip") ||
                RegistryServiceExists("usbip2_ude") ||
                RegistryServiceExists("usbip2_filter");
        }

        private static bool RegistryServiceExists(string serviceName)
        {
            try
            {
                using RegistryKey key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool RegistryUninstallContains(string displayName)
        {
            return RegistryHiveUninstallContains(RegistryView.Registry64, displayName) ||
                RegistryHiveUninstallContains(RegistryView.Registry32, displayName);
        }

        private static bool RegistryHiveUninstallContains(RegistryView view, string displayName)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey uninstallKey = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstallKey == null)
                {
                    return false;
                }

                return uninstallKey.GetSubKeyNames()
                    .Select(name => uninstallKey.OpenSubKey(name))
                    .Where(key => key != null)
                    .Any(key =>
                    {
                        using (key)
                        {
                            string value = key.GetValue("DisplayName") as string;
                            return value?.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                    });
            }
            catch
            {
                return false;
            }
        }
    }
}
