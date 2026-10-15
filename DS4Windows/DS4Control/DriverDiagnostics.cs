using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DS4Windows
{
    internal static class DriverDiagnostics
    {
        internal static string Describe(string serviceName)
        {
            string version;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + serviceName);
                if (key == null) return "not installed";
                string path = ViiperSetupManager.ResolveSystemDriverPath(
                    key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                version = !string.IsNullOrEmpty(path) && File.Exists(path)
                    ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
                if (!string.IsNullOrWhiteSpace(version))
                    version = "file version " + version;
                else if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    // usbip-win2's SYS projects have no VERSIONINFO resource.
                    // Read only the INF beside the registered driver image,
                    // not an unrelated/staged package or the app version.
                    string inf = Path.ChangeExtension(path, ".inf");
                    string packageVersion = File.Exists(inf) ? ReadInfVersion(File.ReadLines(inf)) : null;
                    if (packageVersion != null) version = "package version " + packageVersion + " (INF)";
                }
            }
            catch (Exception error)
            {
                return "version/status unavailable (" + error.Message + ")";
            }
            return $"{(string.IsNullOrWhiteSpace(version) ? "version metadata unavailable" : version)}; {ReadState(serviceName)}";
        }

        internal static string ReadInfVersion(System.Collections.Generic.IEnumerable<string> lines)
        {
            bool inVersion = false;
            foreach (string raw in lines)
            {
                string line = raw.Split(';')[0].Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inVersion = line.Equals("[Version]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                int equals = line.IndexOf('=');
                if (!inVersion || equals < 0 ||
                    !line.Substring(0, equals).Trim().Equals("DriverVer", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = line.Substring(equals + 1).Split(',');
                if (parts.Length == 2 && Version.TryParse(parts[1].Trim(), out var version) && version.Revision >= 0)
                    return version.ToString();
            }
            return null;
        }

        private static string ReadState(string serviceName)
        {
            IntPtr manager = IntPtr.Zero, service = IntPtr.Zero;
            try
            {
                manager = OpenSCManager(null, null, 0x0001); // SC_MANAGER_CONNECT
                if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                service = OpenService(manager, serviceName, 0x0004); // SERVICE_QUERY_STATUS
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return status.CurrentState switch
                {
                    1 => "stopped", 2 => "starting", 3 => "stopping", 4 => "running",
                    5 => "continuing", 6 => "pausing", 7 => "paused", _ => "state unknown"
                };
            }
            catch (Exception error) { return "status unavailable (" + error.Message + ")"; }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (manager != IntPtr.Zero) CloseServiceHandle(manager);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
                ServiceSpecificExitCode, CheckPoint, WaitHint;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
