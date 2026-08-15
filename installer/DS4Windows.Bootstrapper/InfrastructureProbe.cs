using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Win32;

namespace DS4Windows.Bootstrapper
{
    internal static class InfrastructureProbe
    {
        private const string RegistryKeyPath = @"SOFTWARE\DS4Windows";
        private const string ServiceName = "VIIPERNativeBroker";

        internal static bool IsHealthy()
        {
            try
            {
                var installRoot = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ProgramFiles),
                    "DS4Windows"));
                var metadataPath = Path.Combine(installRoot,
                    "ViiperNativeRuntimeMetadata.json");
                if (!IsOrdinaryFile(metadataPath) ||
                    !DirectoryPathHasNoReparsePoints(installRoot))
                {
                    return false;
                }

                string receipt;
                string targetSid;
                string expectedMetadataHash;
                using (var machine = RegistryKey.OpenBaseKey(
                           RegistryHive.LocalMachine,
                           RegistryView.Registry64))
                using (var key = machine.OpenSubKey(RegistryKeyPath))
                {
                    receipt = key?.GetValue(
                        "NativePackageReceipt") as string;
                    targetSid = key?.GetValue(
                        "NativePackageTargetUserSid") as string;
                    expectedMetadataHash = key?.GetValue(
                        "NativePackageMetadataSha256") as string;
                }
                if (!string.Equals(receipt, "Installed",
                        StringComparison.Ordinal) ||
                    !IsValidInteractiveSid(targetSid) ||
                    string.IsNullOrWhiteSpace(expectedMetadataHash) ||
                    !string.Equals(ComputeSha256(metadataPath),
                        expectedMetadataHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                NativeMetadata metadata;
                var serializer = new DataContractJsonSerializer(
                    typeof(NativeMetadata));
                using (var stream = new FileStream(metadataPath,
                           FileMode.Open, FileAccess.Read,
                           FileShare.Read))
                {
                    metadata = (NativeMetadata)
                        serializer.ReadObject(stream);
                }
                if (metadata == null || metadata.schemaVersion != 1 ||
                    !string.Equals(metadata.releaseEligibility,
                        "production", StringComparison.Ordinal) ||
                    metadata.managedBroker == null ||
                    !string.Equals(metadata.managedBroker.serviceName,
                        ServiceName, StringComparison.Ordinal) ||
                    !string.Equals(metadata.managedBroker.serviceAccount,
                        "LocalSystem", StringComparison.Ordinal) ||
                    !string.Equals(metadata.managedBroker.startMode,
                        "automatic", StringComparison.Ordinal) ||
                    !string.Equals(metadata.managedBroker.transport,
                        "native-ude", StringComparison.Ordinal) ||
                    !string.Equals(metadata.managedBroker.apiHost,
                        "127.0.0.1", StringComparison.Ordinal) ||
                    metadata.managedBroker.apiPort != 3242 ||
                    !string.Equals(metadata.managedBroker.credentialPath,
                        "%ProgramData%/VIIPER/viiper.key.txt",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var brokers = (metadata.artifacts ??
                        new List<NativeArtifact>())
                    .Where(artifact => artifact != null &&
                        string.Equals(artifact.role, "broker",
                            StringComparison.Ordinal))
                    .ToList();
                if (brokers.Count != 1 ||
                    !string.Equals(brokers[0].relativePath,
                        "viiper-native-package/viiper.exe",
                        StringComparison.Ordinal) ||
                    brokers[0].length <= 0 ||
                    string.IsNullOrWhiteSpace(brokers[0].sha256))
                {
                    return false;
                }

                var brokerPath = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ProgramFiles),
                    "VIIPER", "viiper.exe");
                var credentialPath = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "VIIPER", "viiper.key.txt");
                var logPath = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "VIIPER", "viiper-native-broker.log");
                if (!IsOrdinaryFile(brokerPath) ||
                    !IsOrdinaryFile(credentialPath) ||
                    new FileInfo(brokerPath).Length != brokers[0].length ||
                    !string.Equals(ComputeSha256(brokerPath),
                        brokers[0].sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return IsServiceConfiguredAndRunning(brokerPath,
                    credentialPath, logPath);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsServiceConfiguredAndRunning(
            string brokerPath, string credentialPath, string logPath)
        {
            using (var machine = RegistryKey.OpenBaseKey(
                       RegistryHive.LocalMachine,
                       RegistryView.Registry64))
            using (var key = machine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Services\" +
                       ServiceName))
            {
                if (key == null ||
                    Convert.ToInt32(key.GetValue("Start", -1)) != 2 ||
                    Convert.ToInt32(key.GetValue("Type", -1)) != 16 ||
                    !IsLocalSystem(key.GetValue("ObjectName") as string) ||
                    !CommandLineMatches(
                        key.GetValue("ImagePath") as string,
                        brokerPath, credentialPath, logPath))
                {
                    return false;
                }
            }

            using (var service = new ServiceController(ServiceName))
            {
                return service.Status ==
                    ServiceControllerStatus.Running;
            }
        }

        private static bool IsLocalSystem(string account)
        {
            return string.Equals(account, "LocalSystem",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(account, @"NT AUTHORITY\SYSTEM",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(account, @".\LocalSystem",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool CommandLineMatches(string commandLine,
            string brokerPath, string credentialPath, string logPath)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return false;
            var arguments = CommandLineToArgvW(commandLine, out var count);
            if (arguments == IntPtr.Zero) return false;
            try
            {
                var expected = new[]
                {
                    brokerPath,
                    "service",
                    "--transport",
                    "native-ude",
                    "--key-file",
                    credentialPath,
                    "--log.file",
                    logPath,
                };
                if (count != expected.Length) return false;
                for (var index = 0; index < expected.Length; index++)
                {
                    var pointer = Marshal.ReadIntPtr(arguments,
                        index * IntPtr.Size);
                    var actual = Marshal.PtrToStringUni(pointer);
                    var comparison = index == 0 || index == 5 ||
                        index == 7
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal;
                    if (!string.Equals(actual, expected[index],
                            comparison))
                    {
                        return false;
                    }
                }
                return true;
            }
            finally
            {
                LocalFree(arguments);
            }
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(
            [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
            out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static bool IsValidInteractiveSid(string value)
        {
            try
            {
                var sid = new SecurityIdentifier(value);
                return !sid.IsWellKnown(
                           WellKnownSidType.LocalSystemSid) &&
                       !sid.IsWellKnown(
                           WellKnownSidType.BuiltinAdministratorsSid) &&
                       !sid.IsWellKnown(
                           WellKnownSidType.LocalServiceSid) &&
                       !sid.IsWellKnown(
                           WellKnownSidType.NetworkServiceSid);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsOrdinaryFile(string path)
        {
            if (!File.Exists(path)) return false;
            if (!DirectoryPathHasNoReparsePoints(
                    Path.GetDirectoryName(Path.GetFullPath(path))))
            {
                return false;
            }
            return (File.GetAttributes(path) &
                    FileAttributes.ReparsePoint) == 0;
        }

        private static bool DirectoryPathHasNoReparsePoints(string path)
        {
            var resolved = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(resolved);
            if (string.IsNullOrWhiteSpace(root)) return false;
            var cursor = root;
            foreach (var component in resolved.Substring(root.Length)
                         .Split(new[]
                         {
                             Path.DirectorySeparatorChar,
                             Path.AltDirectorySeparatorChar,
                         }, StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, component);
                if (!Directory.Exists(cursor)) return false;
                var attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static string ComputeSha256(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open,
                       FileAccess.Read, FileShare.Read))
            {
                return BitConverter.ToString(
                        algorithm.ComputeHash(stream))
                    .Replace("-", string.Empty);
            }
        }

        [DataContract]
        private sealed class NativeMetadata
        {
            [DataMember(Name = "schemaVersion", IsRequired = true)]
            public int schemaVersion { get; set; }
            [DataMember(Name = "releaseEligibility", IsRequired = true)]
            public string releaseEligibility { get; set; }
            [DataMember(Name = "managedBroker", IsRequired = true)]
            public ManagedBroker managedBroker { get; set; }
            [DataMember(Name = "artifacts", IsRequired = true)]
            public List<NativeArtifact> artifacts { get; set; }
        }

        [DataContract]
        private sealed class ManagedBroker
        {
            [DataMember(Name = "serviceName", IsRequired = true)]
            public string serviceName { get; set; }
            [DataMember(Name = "serviceAccount", IsRequired = true)]
            public string serviceAccount { get; set; }
            [DataMember(Name = "startMode", IsRequired = true)]
            public string startMode { get; set; }
            [DataMember(Name = "transport", IsRequired = true)]
            public string transport { get; set; }
            [DataMember(Name = "apiHost", IsRequired = true)]
            public string apiHost { get; set; }
            [DataMember(Name = "apiPort", IsRequired = true)]
            public int apiPort { get; set; }
            [DataMember(Name = "credentialPath", IsRequired = true)]
            public string credentialPath { get; set; }
        }

        [DataContract]
        private sealed class NativeArtifact
        {
            [DataMember(Name = "role", IsRequired = true)]
            public string role { get; set; }
            [DataMember(Name = "relativePath", IsRequired = true)]
            public string relativePath { get; set; }
            [DataMember(Name = "length", IsRequired = true)]
            public long length { get; set; }
            [DataMember(Name = "sha256", IsRequired = true)]
            public string sha256 { get; set; }
        }
    }
}
