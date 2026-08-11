using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;

namespace DS4Windows.SetupActions
{
    internal static partial class Program
    {
        private const string NativeReceiptKeyPath =
            @"SOFTWARE\DS4Windows\NativeVIIPER";
        private const string NativeBundleDirectoryName =
            "viiper-native-udecx";
        private const string NativeLockFileName =
            "viiper-native-udecx.lock.json";
        private const string NativeBrokerServiceName =
            "VIIPERNativeBroker";
        private const string NativeDriverServiceName = "ViiperUde";
        private const uint InfiniteWait = 0xffffffff;
        private const uint WaitObject0 = 0;

        private static readonly string[] NativeRootEntries =
        {
            "viiper.exe",
            "ViiperUdeCtl.exe",
            "submission-manifest.json",
            "driver",
        };

        private static readonly string[] NativeDriverEntries =
        {
            "ViiperUde.inf",
            "ViiperUde.sys",
            "ViiperUde.cat",
        };

        private static int NativeInstallOrRepair(string installRoot,
            string[] args)
        {
            var pins = NativePackagePins.Load();
            var targetSid = ResolveInteractiveUser(args).Sid;
            var bundleRoot = RequireArgument(args, "--native-bundle-root");
            var lockPath = RequireArgument(args, "--native-lock-path");
            var desktopShortcut = !string.Equals(
                ReadArgument(args, "--desktop-shortcut"), "0",
                StringComparison.OrdinalIgnoreCase);
            var ds4Path = Path.Combine(installRoot, "DS4Windows.exe");
            if (!File.Exists(ds4Path))
            {
                throw new FileNotFoundException(
                    "The managed DS4Windows installation is incomplete.",
                    ds4Path);
            }

            using (var media = NativeBundleMedia.Open(bundleRoot, lockPath,
                       pins))
            {
                var arguments = new[]
                {
                    "native-package-install",
                    "--package-directory", media.DriverDirectory,
                    "--submission-manifest", media.ManifestPath,
                    "--source-revision", pins.SourceRevision,
                    "--driver-helper", media.HelperPath,
                    "--expected-broker-sha256", pins.BrokerSha256,
                    "--expected-helper-sha256", pins.HelperSha256,
                    "--expected-manifest-sha256", pins.ManifestSha256,
                    "--expected-inf-sha256", pins.InfSha256,
                    "--expected-sys-sha256", pins.SysSha256,
                    "--expected-cat-sha256", pins.CatSha256,
                    "--target-user-sid", targetSid,
                };
                var exitCode = RunNativePackageProcess(media.BrokerPath,
                    arguments, out var output);
                WriteInfrastructureLog(output);
                WriteFallbackLog("Native VIIPER package install/repair exited " +
                    "with code " + exitCode + ".");
                if (exitCode == 0)
                {
                    try
                    {
                        // The receipt is only a future scheduling hint. The
                        // child already returned authoritative authenticated
                        // success, so receipt I/O must not retroactively make
                        // Burn roll back the application around a committed
                        // driver/service transaction.
                        CommitNativeReceipt(pins, targetSid);
                    }
                    catch (Exception ex)
                    {
                        WriteFallbackLog("Native VIIPER committed, but its " +
                            "scheduling receipt could not be written: " +
                            ex.Message);
                    }
                    try
                    {
                        ConfigureCommonShortcuts(ds4Path, desktopShortcut);
                    }
                    catch (Exception ex)
                    {
                        WriteFallbackLog("Common shortcut setup was skipped: " +
                            ex.Message);
                    }
                }
                return exitCode;
            }
        }

        private static int NativeUninstall(string[] args)
        {
            var pins = NativePackagePins.Load();
            var targetSid = ResolveInteractiveUser(args).Sid;
            var bundleRoot = RequireArgument(args, "--native-bundle-root");
            var lockPath = RequireArgument(args, "--native-lock-path");
            using (var media = NativeBundleMedia.Open(bundleRoot, lockPath,
                       pins))
            {
                try { MarkNativeReceiptRemoving(); }
                catch (Exception ex)
                {
                    WriteFallbackLog("Native uninstall receipt could not be " +
                        "marked Removing: " + ex.Message);
                }
                var arguments = new[]
                {
                    "uninstall",
                    "--yes",
                    "--target-user-sid", targetSid,
                    "--driver-helper", media.HelperPath,
                    "--expected-helper-sha256", pins.HelperSha256,
                };
                var exitCode = RunNativePackageProcess(media.BrokerPath,
                    arguments, out var output);
                WriteInfrastructureLog(output);
                WriteFallbackLog("Native VIIPER package uninstall exited " +
                    "with code " + exitCode + ".");
                if (exitCode == 0 || exitCode == 3010)
                {
                    try { DeleteNativeReceipt(); }
                    catch (Exception ex)
                    {
                        WriteFallbackLog("Native uninstall committed, but " +
                            "its scheduling receipt could not be removed: " +
                            ex.Message);
                    }
                    try { RemoveCommonShortcuts(); }
                    catch (Exception ex)
                    {
                        WriteFallbackLog(
                            "Common shortcut cleanup was skipped: " +
                            ex.Message);
                    }
                }
                return exitCode;
            }
        }

        private static int NativeProbe()
        {
            try
            {
                var pins = NativePackagePins.Load();
                if (!NativeReceiptMatches(pins)) return 1;

                var programFiles = Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles);
                var brokerPath = Path.Combine(programFiles, "VIIPER",
                    "viiper.exe");
                if (!HashMatches(brokerPath, pins.BrokerSha256)) return 1;
                if (!NativeBrokerServiceMatches(brokerPath)) return 1;
                if (!DriverServiceHashMatches(pins.SysSha256)) return 1;
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static int RunNativePackageProcess(string fileName,
            IEnumerable<string> arguments, out string output)
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(fileName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                // VIIPER owns the transactional deadline and independent
                // rollback deadline. Once mutation may have started, this
                // wrapper must retain its media and never kill the child.
                var exitCode = WaitForNativePackageProcess(process);
                output = stdout.GetAwaiter().GetResult() +
                    Environment.NewLine + stderr.GetAwaiter().GetResult();
                return exitCode;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            SafeProcessHandle handle, uint milliseconds);

        private static int WaitForNativePackageProcess(Process process)
        {
            // Hold the Process (and therefore its exact kernel process handle)
            // until the object is signaled. A managed wait anomaly must never
            // unwind the exact media or outer installer scope while the child
            // may still be mutating SetupAPI/SCM state.
            while (WaitForSingleObject(process.SafeHandle, InfiniteWait) !=
                   WaitObject0)
            {
                Thread.Sleep(50);
            }
            return process.ExitCode;
        }

        private static string RequireArgument(string[] args, string name)
        {
            var value = ReadArgument(args, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Required argument is missing: " +
                    name);
            }
            return value;
        }

        private static void CommitNativeReceipt(NativePackagePins pins,
            string targetSid)
        {
            using (var key = CreateMachineKey64(NativeReceiptKeyPath))
            {
                if (key == null)
                    throw new InvalidOperationException(
                        "Could not create the native VIIPER receipt.");
                key.SetValue("State", "Committing", RegistryValueKind.String);
                key.SetValue("Schema", 1, RegistryValueKind.DWord);
                key.SetValue("PackageIdentity", pins.LockSha256,
                    RegistryValueKind.String);
                key.SetValue("SourceRevision", pins.SourceRevision,
                    RegistryValueKind.String);
                key.SetValue("DriverPackageVersion",
                    pins.DriverPackageVersion, RegistryValueKind.String);
                key.SetValue("BrokerSHA256", pins.BrokerSha256,
                    RegistryValueKind.String);
                key.SetValue("HelperSHA256", pins.HelperSha256,
                    RegistryValueKind.String);
                key.SetValue("ManifestSHA256", pins.ManifestSha256,
                    RegistryValueKind.String);
                key.SetValue("InfSHA256", pins.InfSha256,
                    RegistryValueKind.String);
                key.SetValue("SysSHA256", pins.SysSha256,
                    RegistryValueKind.String);
                key.SetValue("CatSHA256", pins.CatSha256,
                    RegistryValueKind.String);
                key.SetValue("DriverBuildIdentity",
                    pins.DriverBuildIdentity, RegistryValueKind.String);
                key.SetValue("TargetUserSid", targetSid,
                    RegistryValueKind.String);
                key.SetValue("InstalledUtc", DateTime.UtcNow.ToString("O"),
                    RegistryValueKind.String);
                key.Flush();
                key.SetValue("State", "Ready", RegistryValueKind.String);
                key.Flush();
            }
        }

        private static void MarkNativeReceiptRemoving()
        {
            using (var key = CreateMachineKey64(NativeReceiptKeyPath))
            {
                if (key == null)
                    throw new InvalidOperationException(
                        "Could not update the native VIIPER receipt.");
                key.SetValue("State", "Removing", RegistryValueKind.String);
                key.SetValue("StateUtc", DateTime.UtcNow.ToString("O"),
                    RegistryValueKind.String);
                key.Flush();
            }
        }

        private static void DeleteNativeReceipt()
        {
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                machine.DeleteSubKeyTree(NativeReceiptKeyPath,
                    throwOnMissingSubKey: false);
            }
        }

        private static bool NativeReceiptMatches(NativePackagePins pins)
        {
            using (var key = OpenMachineKey64(NativeReceiptKeyPath))
            {
                return key != null &&
                    Convert.ToInt32(key.GetValue("Schema", 0)) == 1 &&
                    ReceiptValueEquals(key, "State", "Ready") &&
                    ReceiptValueEquals(key, "PackageIdentity",
                        pins.LockSha256) &&
                    ReceiptValueEquals(key, "SourceRevision",
                        pins.SourceRevision) &&
                    ReceiptValueEquals(key, "DriverPackageVersion",
                        pins.DriverPackageVersion) &&
                    ReceiptValueEquals(key, "BrokerSHA256",
                        pins.BrokerSha256) &&
                    ReceiptValueEquals(key, "HelperSHA256",
                        pins.HelperSha256) &&
                    ReceiptValueEquals(key, "ManifestSHA256",
                        pins.ManifestSha256) &&
                    ReceiptValueEquals(key, "InfSHA256", pins.InfSha256) &&
                    ReceiptValueEquals(key, "SysSHA256", pins.SysSha256) &&
                    ReceiptValueEquals(key, "CatSHA256", pins.CatSha256) &&
                    ReceiptValueEquals(key, "DriverBuildIdentity",
                        pins.DriverBuildIdentity);
            }
        }

        private static bool ReceiptValueEquals(RegistryKey key, string name,
            string expected)
        {
            return string.Equals(key.GetValue(name) as string, expected,
                StringComparison.Ordinal);
        }

        private static bool NativeBrokerServiceMatches(string brokerPath)
        {
            using (var key = OpenMachineKey64(
                       @"SYSTEM\CurrentControlSet\Services\" +
                       NativeBrokerServiceName))
            {
                if (key == null || Convert.ToInt32(key.GetValue("Start", -1)) != 2)
                    return false;
                var imagePath = key.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(imagePath)) return false;
                var executable = ExtractExecutablePath(imagePath);
                return string.Equals(Path.GetFullPath(executable),
                    Path.GetFullPath(brokerPath),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool DriverServiceHashMatches(string expectedHash)
        {
            using (var key = OpenMachineKey64(
                       @"SYSTEM\CurrentControlSet\Services\" +
                       NativeDriverServiceName))
            {
                var imagePath = key?.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(imagePath)) return false;
                return HashMatches(ResolveServiceImagePath(imagePath),
                    expectedHash);
            }
        }

        private static string ExtractExecutablePath(string commandLine)
        {
            var expanded = Environment.ExpandEnvironmentVariables(
                commandLine.Trim());
            if (expanded.StartsWith("\"", StringComparison.Ordinal))
            {
                var closing = expanded.IndexOf('\"', 1);
                if (closing <= 1)
                    throw new InvalidOperationException(
                        "The service image command is malformed.");
                return expanded.Substring(1, closing - 1);
            }
            var extension = expanded.IndexOf(".exe",
                StringComparison.OrdinalIgnoreCase);
            if (extension < 0)
                throw new InvalidOperationException(
                    "The service image command has no executable.");
            return expanded.Substring(0, extension + 4);
        }

        private static string ResolveServiceImagePath(string imagePath)
        {
            var path = Environment.ExpandEnvironmentVariables(
                imagePath.Trim().Trim('\"'));
            if (path.StartsWith(@"\SystemRoot\",
                    StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.Windows),
                    path.Substring(@"\SystemRoot\".Length));
            }
            else if (path.StartsWith(@"\??\", StringComparison.Ordinal) ||
                     path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                path = path.Substring(4);
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows), path);
            }
            return Path.GetFullPath(path);
        }

        private static bool HashMatches(string path, string expected)
        {
            if (!File.Exists(path)) return false;
            using (var stream = new FileStream(path, FileMode.Open,
                       FileAccess.Read, FileShare.Read))
            {
                return string.Equals(ComputeSha256(stream), expected,
                    StringComparison.Ordinal);
            }
        }

        private static string ComputeSha256(Stream stream)
        {
            using (var algorithm = SHA256.Create())
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file, out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        private sealed class NativePackagePins
        {
            internal string SourceRevision { get; private set; }
            internal string DriverPackageVersion { get; private set; }
            internal string DriverBuildIdentity { get; private set; }
            internal string BrokerSha256 { get; private set; }
            internal string HelperSha256 { get; private set; }
            internal string ManifestSha256 { get; private set; }
            internal string InfSha256 { get; private set; }
            internal string SysSha256 { get; private set; }
            internal string CatSha256 { get; private set; }
            internal string LockSha256 { get; private set; }

            internal static NativePackagePins Load()
            {
                var metadata = new Dictionary<string, string>(
                    StringComparer.Ordinal);
                foreach (var attribute in Assembly.GetExecutingAssembly()
                             .GetCustomAttributes<AssemblyMetadataAttribute>())
                {
                    if (!metadata.TryAdd(attribute.Key, attribute.Value))
                        throw new InvalidOperationException(
                            "Duplicate native package assembly metadata: " +
                            attribute.Key);
                }
                var pins = new NativePackagePins
                {
                    SourceRevision = Require(metadata,
                        "ViiperNativeSourceRevision", 40, 64),
                    DriverPackageVersion = RequireVersion(metadata,
                        "ViiperNativeDriverPackageVersion"),
                    DriverBuildIdentity = Require(metadata,
                        "ViiperNativeDriverBuildIdentity", 64, 64),
                    BrokerSha256 = Require(metadata,
                        "ViiperNativeBrokerSha256", 64, 64),
                    HelperSha256 = Require(metadata,
                        "ViiperNativeHelperSha256", 64, 64),
                    ManifestSha256 = Require(metadata,
                        "ViiperNativeManifestSha256", 64, 64),
                    InfSha256 = Require(metadata,
                        "ViiperNativeInfSha256", 64, 64),
                    SysSha256 = Require(metadata,
                        "ViiperNativeSysSha256", 64, 64),
                    CatSha256 = Require(metadata,
                        "ViiperNativeCatSha256", 64, 64),
                    LockSha256 = Require(metadata,
                        "ViiperNativeLockSha256", 64, 64),
                };
                return pins;
            }

            private static string Require(Dictionary<string, string> values,
                string name, int minimumLength, int maximumLength)
            {
                if (!values.TryGetValue(name, out var value) ||
                    value == null || value.Length < minimumLength ||
                    value.Length > maximumLength ||
                    value.Any(character =>
                        !(character >= '0' && character <= '9') &&
                        !(character >= 'a' && character <= 'f')))
                {
                    throw new InvalidOperationException(
                        "Native package metadata is missing or invalid: " +
                        name);
                }
                return value;
            }

            private static string RequireVersion(
                Dictionary<string, string> values, string name)
            {
                if (!values.TryGetValue(name, out var value) ||
                    string.IsNullOrWhiteSpace(value) ||
                    value.Split('.').Length != 4 ||
                    value.Split('.').Any(part => !ushort.TryParse(part,
                        out _)))
                {
                    throw new InvalidOperationException(
                        "Native package version metadata is invalid.");
                }
                return value;
            }
        }

        private sealed class NativeBundleMedia : IDisposable
        {
            private readonly List<FileStream> retainedFiles =
                new List<FileStream>();

            internal string BrokerPath { get; private set; }
            internal string HelperPath { get; private set; }
            internal string ManifestPath { get; private set; }
            internal string DriverDirectory { get; private set; }

            internal static NativeBundleMedia Open(string bundleRoot,
                string lockPath, NativePackagePins pins)
            {
                var media = new NativeBundleMedia();
                try
                {
                    var root = Path.GetFullPath(bundleRoot)
                        .TrimEnd(Path.DirectorySeparatorChar);
                    var expectedLock = Path.Combine(
                        Path.GetDirectoryName(root) ?? string.Empty,
                        NativeLockFileName);
                    var actualLock = Path.GetFullPath(lockPath);
                    if (!string.Equals(Path.GetFileName(root),
                            NativeBundleDirectoryName,
                            StringComparison.Ordinal) ||
                        !string.Equals(actualLock, expectedLock,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The native VIIPER bundle or lock path is not canonical.");
                    }
                    EnsureDirectoryPathHasNoReparsePoints(root);
                    EnsureTreeHasNoReparsePoints(root);
                    RequireExactEntries(root, NativeRootEntries);
                    media.DriverDirectory = Path.Combine(root, "driver");
                    RequireExactEntries(media.DriverDirectory,
                        NativeDriverEntries);

                    media.BrokerPath = Path.Combine(root, "viiper.exe");
                    media.HelperPath = Path.Combine(root,
                        "ViiperUdeCtl.exe");
                    media.ManifestPath = Path.Combine(root,
                        "submission-manifest.json");
                    var expectedFiles = new[]
                    {
                        Tuple.Create(media.BrokerPath, pins.BrokerSha256),
                        Tuple.Create(media.HelperPath, pins.HelperSha256),
                        Tuple.Create(media.ManifestPath, pins.ManifestSha256),
                        Tuple.Create(Path.Combine(media.DriverDirectory,
                            "ViiperUde.inf"), pins.InfSha256),
                        Tuple.Create(Path.Combine(media.DriverDirectory,
                            "ViiperUde.sys"), pins.SysSha256),
                        Tuple.Create(Path.Combine(media.DriverDirectory,
                            "ViiperUde.cat"), pins.CatSha256),
                        Tuple.Create(actualLock, pins.LockSha256),
                    };
                    foreach (var expected in expectedFiles)
                    {
                        media.RetainAndVerify(expected.Item1,
                            expected.Item2);
                    }
                    return media;
                }
                catch
                {
                    media.Dispose();
                    throw;
                }
            }

            private static void RequireExactEntries(string directory,
                IEnumerable<string> expected)
            {
                if (!Directory.Exists(directory))
                    throw new DirectoryNotFoundException(directory);
                var actual = Directory.EnumerateFileSystemEntries(directory)
                    .Select(Path.GetFileName).OrderBy(value => value,
                        StringComparer.Ordinal).ToArray();
                var wanted = expected.OrderBy(value => value,
                    StringComparer.Ordinal).ToArray();
                if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        "Native VIIPER media has missing, extra, or " +
                        "case-mismatched entries: " + directory);
            }

            private void RetainAndVerify(string path, string expectedHash)
            {
                if (!File.Exists(path) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new FileNotFoundException(
                        "Native VIIPER media file is missing or unsafe.", path);
                }
                var stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                try
                {
                    if (!GetFileInformationByHandle(stream.SafeFileHandle,
                            out var information))
                    {
                        throw new InvalidOperationException(
                            "Could not identify native media file " + path +
                            ": Win32 " + Marshal.GetLastWin32Error());
                    }
                    if (information.NumberOfLinks != 1)
                        throw new InvalidOperationException(
                            "Native VIIPER media must not be hard-linked: " +
                            path);
                    if (!string.Equals(ComputeSha256(stream), expectedHash,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Native VIIPER media hash mismatch: " + path);
                    }
                    retainedFiles.Add(stream);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                for (var index = retainedFiles.Count - 1; index >= 0;
                     index--)
                {
                    retainedFiles[index].Dispose();
                }
                retainedFiles.Clear();
            }
        }
    }
}
