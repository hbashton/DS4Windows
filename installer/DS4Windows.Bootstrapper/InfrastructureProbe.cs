using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace DS4Windows.Bootstrapper
{
    internal static class InfrastructureProbe
    {
        private const string ExpectedMarker = "VIIPER-0.0.7+USBIP-0.9.7.7";
        private const string ExpectedViiperVersion = "0.0.7";
        private const string ExpectedUsbipVersion = "0.9.7.7";
        private const string ExpectedUdeHash = "51DB440065393E588A6B2585508C50EB3E1510B7B06D9AFA6C5BDE583751EA7D";
        private const string ExpectedFilterHash = "C290299FF4D0F6A597DB5CE03E15B29A5349CDCE7C587EBFBD9ECAECA04F73ED";

        internal static bool IsHealthy()
        {
            try
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var viiper = Path.Combine(programFiles, "DS4Windows", "VIIPER", "viiper.exe");
                var usbip = Path.Combine(programFiles, "USBip", "usbip.exe");
                if (!File.Exists(viiper) || !File.Exists(usbip)) return false;

                if (!VersionMatches(viiper, ExpectedViiperVersion) ||
                    !VersionMatches(usbip, ExpectedUsbipVersion))
                {
                    return false;
                }

                if (!DriverHashMatches("usbip2_ude", ExpectedUdeHash) ||
                    !DriverHashMatches("usbip2_filter", ExpectedFilterHash))
                {
                    return false;
                }

                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\DS4Windows"))
                {
                    if (!string.Equals(key?.GetValue("InfrastructureVersion") as string,
                            ExpectedMarker, StringComparison.Ordinal) ||
                        !string.Equals(key?.GetValue("InfrastructureState") as string,
                            "Ready", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                using (var process = Process.Start(new ProcessStartInfo(usbip, "port")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }))
                {
                    if (process == null) return false;

                    // usbip can emit enough diagnostics to fill a redirected
                    // pipe. Drain both streams while it runs so health probing
                    // cannot deadlock the installer UI.
                    process.OutputDataReceived += (_, __) => { };
                    process.ErrorDataReceived += (_, __) => { };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { }
                        return false;
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static bool VersionMatches(string path, string expected)
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var value = string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
            return !string.IsNullOrWhiteSpace(value) &&
                (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith(expected + ".", StringComparison.OrdinalIgnoreCase));
        }

        private static bool HashMatches(string path, string expected)
        {
            if (!File.Exists(path)) return false;
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var actual = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool DriverHashMatches(string serviceName, string expected)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName))
            {
                var imagePath = key?.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(imagePath)) return false;
                imagePath = Environment.ExpandEnvironmentVariables(imagePath.Trim().Trim('"'));
                if (imagePath.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                {
                    imagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        imagePath.Substring(@"\SystemRoot\".Length));
                }
                else if (imagePath.StartsWith(@"\??\", StringComparison.Ordinal))
                {
                    imagePath = imagePath.Substring(4);
                }
                return HashMatches(Path.GetFullPath(imagePath), expected);
            }
        }
    }
}
