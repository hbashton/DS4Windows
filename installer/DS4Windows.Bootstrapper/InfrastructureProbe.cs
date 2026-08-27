using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DS4Windows.Bootstrapper
{
    internal static class InfrastructureProbe
    {
        private const string ExpectedMarker = "VIIPER-0.1.2+USBIP-0.9.7.7";
        private const string ExpectedViiperVersion = "0.1.2";
        private const string ExpectedViiperHash = "980E4D713BF141E0A85ADF83FB234E50D2FA4C54093FCC9440E0F71FA3D9C633";
        private const string ExpectedUsbipVersion = "0.9.7.7";
        private const string ExpectedUsbipHash = "FC1660E3759D8AF4CEDE48DBE194285A5A1DE85CE6E3216724499AFD32BE92E8";
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
                    !HashMatches(viiper, ExpectedViiperHash) ||
                    !VersionMatches(usbip, ExpectedUsbipVersion) ||
                    !HashMatches(usbip, ExpectedUsbipHash))
                {
                    return false;
                }

                if (!DriverHashMatches("usbip2_ude", ExpectedUdeHash) ||
                    !DriverHashMatches("usbip2_filter", ExpectedFilterHash))
                {
                    return false;
                }

                using (var machine = RegistryKey.OpenBaseKey(
                           RegistryHive.LocalMachine,
                           RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"SOFTWARE\DS4Windows"))
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
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    process.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data != null) stdout.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null) stderr.AppendLine(e.Data);
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { }
                        return false;
                    }
                    process.WaitForExit();
                    if (!IsCompatibleUsbipProbe(process.ExitCode,
                            stdout.ToString() + Environment.NewLine +
                            stderr.ToString()))
                    {
                        return false;
                    }
                }
                return ViiperApiReady();
            }
            catch { return false; }
        }

        internal static bool IsCompatibleUsbipProbe(int exitCode,
            string diagnostic)
        {
            if (exitCode != 0) return false;
            var text = diagnostic ?? string.Empty;
            return text.IndexOf("ABI mismatch",
                       StringComparison.OrdinalIgnoreCase) < 0 &&
                   text.IndexOf("unexpected size",
                       StringComparison.OrdinalIgnoreCase) < 0 &&
                   text.IndexOf("specified conversion is not valid",
                       StringComparison.OrdinalIgnoreCase) < 0 &&
                   text.IndexOf("invalid structure size",
                       StringComparison.OrdinalIgnoreCase) < 0;
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
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine,
                       RegistryView.Registry64))
            using (var key = machine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Services\" + serviceName))
            {
                var imagePath = key?.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(imagePath)) return false;
                imagePath = Environment.ExpandEnvironmentVariables(
                    imagePath.Trim());
                if (imagePath.StartsWith("\"", StringComparison.Ordinal))
                {
                    var closingQuote = imagePath.IndexOf('"', 1);
                    if (closingQuote <= 1) return false;
                    imagePath = imagePath.Substring(1, closingQuote - 1);
                }
                else
                {
                    var extension = imagePath.IndexOf(".sys",
                        StringComparison.OrdinalIgnoreCase);
                    if (extension >= 0)
                    {
                        imagePath = imagePath.Substring(0, extension + 4);
                    }
                }
                if (imagePath.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                {
                    imagePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        imagePath.Substring(@"\SystemRoot\".Length));
                }
                else if (imagePath.StartsWith(@"\??\",
                             StringComparison.Ordinal) ||
                         imagePath.StartsWith(@"\\?\",
                             StringComparison.Ordinal))
                {
                    imagePath = imagePath.Substring(4);
                }
                else if (!Path.IsPathRooted(imagePath))
                {
                    imagePath = Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.Windows), imagePath);
                }
                return HashMatches(Path.GetFullPath(imagePath), expected);
            }
        }

        private static bool ViiperApiReady()
        {
            using (var client = new TcpClient())
            {
                client.NoDelay = true;
                client.SendTimeout = 1000;
                client.ReceiveTimeout = 1000;
                var connect = client.BeginConnect("127.0.0.1", 3242,
                    null, null);
                using (connect.AsyncWaitHandle)
                {
                    if (!connect.AsyncWaitHandle.WaitOne(1000)) return false;
                    client.EndConnect(connect);
                }

                using (var stream = client.GetStream())
                {
                    var request = Encoding.UTF8.GetBytes("ping\0");
                    stream.Write(request, 0, request.Length);
                    var response = new byte[512];
                    var total = 0;
                    var deadline = Stopwatch.StartNew();
                    while (total < response.Length &&
                           deadline.ElapsedMilliseconds < 1000)
                    {
                        stream.ReadTimeout = Math.Max(1,
                            1000 - (int)deadline.ElapsedMilliseconds);
                        var read = stream.Read(response, total,
                            response.Length - total);
                        if (read <= 0) break;
                        total += read;
                        if (Encoding.UTF8.GetString(response, 0, total)
                            .IndexOf("VIIPER",
                                StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }
        }
    }
}
