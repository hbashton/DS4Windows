using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace DS4Windows.Bootstrapper
{
    // This is deliberately a scheduling/cache probe, not the authority for
    // native readiness. The vital VIIPER package transaction performs the
    // authenticated ABI/build-identity health proof on every install/repair.
    internal static class InfrastructureProbe
    {
        private const string ReceiptKey =
            @"SOFTWARE\DS4Windows\NativeVIIPER";

        internal static bool IsHealthy()
        {
            try
            {
                var pins = NativePins.Load();
                if (!ReceiptMatches(pins)) return false;

                var broker = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles), "VIIPER",
                    "viiper.exe");
                if (!HashMatches(broker, pins.BrokerSha256)) return false;
                if (!BrokerServiceMatches(broker)) return false;
                return DriverServiceHashMatches(pins.SysSha256);
            }
            catch
            {
                return false;
            }
        }

        private static bool ReceiptMatches(NativePins pins)
        {
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(ReceiptKey))
            {
                return key != null &&
                    Convert.ToInt32(key.GetValue("Schema", 0)) == 1 &&
                    ValueEquals(key, "State", "Ready") &&
                    ValueEquals(key, "PackageIdentity", pins.LockSha256) &&
                    ValueEquals(key, "SourceRevision", pins.SourceRevision) &&
                    ValueEquals(key, "DriverPackageVersion",
                        pins.DriverPackageVersion) &&
                    ValueEquals(key, "BrokerSHA256", pins.BrokerSha256) &&
                    ValueEquals(key, "HelperSHA256", pins.HelperSha256) &&
                    ValueEquals(key, "ManifestSHA256", pins.ManifestSha256) &&
                    ValueEquals(key, "InfSHA256", pins.InfSha256) &&
                    ValueEquals(key, "SysSHA256", pins.SysSha256) &&
                    ValueEquals(key, "CatSHA256", pins.CatSha256) &&
                    ValueEquals(key, "DriverBuildIdentity",
                        pins.DriverBuildIdentity);
            }
        }

        private static bool ValueEquals(RegistryKey key, string name,
            string expected)
        {
            return string.Equals(key.GetValue(name) as string, expected,
                StringComparison.Ordinal);
        }

        private static bool BrokerServiceMatches(string expectedBroker)
        {
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Services\VIIPERNativeBroker"))
            {
                if (key == null || Convert.ToInt32(key.GetValue("Start", -1)) != 2)
                    return false;
                var command = key.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(command)) return false;
                var executable = ExtractExecutablePath(command);
                return string.Equals(Path.GetFullPath(executable),
                    Path.GetFullPath(expectedBroker),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool DriverServiceHashMatches(string expectedHash)
        {
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Services\ViiperUde"))
            {
                var imagePath = key?.GetValue("ImagePath") as string;
                return !string.IsNullOrWhiteSpace(imagePath) &&
                    HashMatches(ResolveServiceImagePath(imagePath), expectedHash);
            }
        }

        private static string ExtractExecutablePath(string commandLine)
        {
            var value = Environment.ExpandEnvironmentVariables(
                commandLine.Trim());
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                var closing = value.IndexOf('\"', 1);
                if (closing <= 1) throw new InvalidDataException();
                return value.Substring(1, closing - 1);
            }
            var extension = value.IndexOf(".exe",
                StringComparison.OrdinalIgnoreCase);
            if (extension < 0) throw new InvalidDataException();
            return value.Substring(0, extension + 4);
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
            using (var algorithm = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open,
                       FileAccess.Read, FileShare.Read))
            {
                var actual = BitConverter.ToString(
                        algorithm.ComputeHash(stream))
                    .Replace("-", string.Empty).ToLowerInvariant();
                return string.Equals(actual, expected,
                    StringComparison.Ordinal);
            }
        }

        private sealed class NativePins
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

            internal static NativePins Load()
            {
                var values = new Dictionary<string, string>(
                    StringComparer.Ordinal);
                foreach (var attribute in Assembly.GetExecutingAssembly()
                             .GetCustomAttributes<AssemblyMetadataAttribute>())
                {
                    if (!values.TryAdd(attribute.Key, attribute.Value))
                        throw new InvalidDataException();
                }
                return new NativePins
                {
                    SourceRevision = RequireHex(values,
                        "ViiperNativeSourceRevision", 40, 64),
                    DriverPackageVersion = RequireVersion(values,
                        "ViiperNativeDriverPackageVersion"),
                    DriverBuildIdentity = RequireHex(values,
                        "ViiperNativeDriverBuildIdentity", 64, 64),
                    BrokerSha256 = RequireHex(values,
                        "ViiperNativeBrokerSha256", 64, 64),
                    HelperSha256 = RequireHex(values,
                        "ViiperNativeHelperSha256", 64, 64),
                    ManifestSha256 = RequireHex(values,
                        "ViiperNativeManifestSha256", 64, 64),
                    InfSha256 = RequireHex(values,
                        "ViiperNativeInfSha256", 64, 64),
                    SysSha256 = RequireHex(values,
                        "ViiperNativeSysSha256", 64, 64),
                    CatSha256 = RequireHex(values,
                        "ViiperNativeCatSha256", 64, 64),
                    LockSha256 = RequireHex(values,
                        "ViiperNativeLockSha256", 64, 64),
                };
            }

            private static string RequireHex(
                Dictionary<string, string> values, string name,
                int minimum, int maximum)
            {
                if (!values.TryGetValue(name, out var value) ||
                    value == null || value.Length < minimum ||
                    value.Length > maximum || value.Any(character =>
                        !(character >= '0' && character <= '9') &&
                        !(character >= 'a' && character <= 'f')))
                    throw new InvalidDataException();
                return value;
            }

            private static string RequireVersion(
                Dictionary<string, string> values, string name)
            {
                if (!values.TryGetValue(name, out var value) ||
                    value == null || value.Split('.').Length != 4 ||
                    value.Split('.').Any(part =>
                        !ushort.TryParse(part, out _)))
                    throw new InvalidDataException();
                return value;
            }
        }
    }
}
