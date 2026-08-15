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
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;

namespace DS4Windows
{
    public sealed class ViiperPrerequisiteStatus
    {
        public bool MetadataFound { get; set; }
        public bool MetadataEligible { get; set; }
        public bool LocalTestMetadata { get; set; }
        public bool PackageBundleFound { get; set; }
        public bool BrokerInstalled { get; set; }
        public bool BrokerHashMatches { get; set; }
        public bool BrokerServiceInstalled { get; set; }
        public bool BrokerServiceConfigured { get; set; }
        public bool BrokerServiceRunning { get; set; }
        public bool CredentialReadable { get; set; }
        public bool AuthenticatedPingSucceeded { get; set; }
        public bool RuntimeContractCompatible { get; set; }
        public bool SetupScriptFound { get; set; }
        public string BrokerPath { get; set; }
        public string CredentialPath { get; set; }
        public string MetadataPath { get; set; }
        public string SetupScriptPath { get; set; }
        public string Detail { get; set; }

        // Source-compatible diagnostic aliases. USB/IP is deliberately not a
        // prerequisite for native output and is never reported as installed.
        public bool ViiperInstalled => BrokerInstalled;
        public bool ServerRunning =>
            BrokerServiceRunning && AuthenticatedPingSucceeded;
        public bool UsbipInstalled => false;
        public string ViiperPath => BrokerPath;

        public bool Ready =>
            MetadataEligible &&
            BrokerInstalled &&
            BrokerHashMatches &&
            BrokerServiceInstalled &&
            BrokerServiceConfigured &&
            BrokerServiceRunning &&
            CredentialReadable &&
            AuthenticatedPingSucceeded &&
            RuntimeContractCompatible;

        public string DisplayText
        {
            get
            {
                if (Ready)
                {
                    return LocalTestMetadata
                        ? "VIIPER native UDE ready (disposable-VM local test)"
                        : "VIIPER native UDE ready";
                }
                if (!MetadataFound)
                {
                    return "VIIPER native runtime metadata missing";
                }
                if (!MetadataEligible)
                {
                    return LocalTestMetadata
                        ? "VIIPER bundle is local-test evidence only"
                        : "VIIPER native runtime is not production eligible";
                }
                if (!BrokerInstalled || !BrokerServiceInstalled)
                {
                    return "VIIPER native UDE package is not installed";
                }
                if (!BrokerHashMatches || !BrokerServiceConfigured)
                {
                    return "VIIPER native broker installation does not match this build";
                }
                if (!CredentialReadable)
                {
                    return "VIIPER protected API credential is unavailable";
                }
                if (!BrokerServiceRunning)
                {
                    return "VIIPERNativeBroker service is not running";
                }
                if (!AuthenticatedPingSucceeded)
                {
                    return "VIIPER native broker authentication failed";
                }
                if (!RuntimeContractCompatible)
                {
                    return "VIIPER native driver contract is incompatible";
                }
                return string.IsNullOrWhiteSpace(Detail)
                    ? "VIIPER native UDE status unknown"
                    : Detail;
            }
        }
    }

    public static class ViiperSetupManager
    {
        public const string ApiHost = "127.0.0.1";
        public const int ApiPort = 3242;
        public const string NativeBrokerServiceName = "VIIPERNativeBroker";
        public const string LocalTestOptInEnvironment =
            "DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST";

        private const string InstallerScriptName =
            "manage-viiper-native-package.ps1";
        private const string MetadataFileName =
            "ViiperNativeRuntimeMetadata.json";
        private static int promptShownThisSession;

        public static bool IsViiperOutputType(OutContType type) =>
            ViiperOutDevice.IsViiperType(type);

        public static ViiperPrerequisiteStatus GetStatus(
            bool tryStartServer = false)
        {
            // VIIPERNativeBroker is auto-started and owned by SCM. Never start
            // a second per-user broker in response to a status query.
            _ = tryStartServer;

            string brokerPath = GetInstalledBrokerPath();
            string credentialPath = GetCredentialPath();
            string metadataPath = GetMetadataPath();
            string setupScriptPath = GetSetupScriptPath();
            NativeMetadataStatus metadata = InspectMetadata(metadataPath,
                brokerPath);
            NativeServiceStatus service = InspectNativeBrokerService(
                brokerPath, credentialPath);
            ViiperNativeStatusProbe probe =
                ViiperNativeRuntime.GetStatusProbe(ApiHost, ApiPort);

            return new ViiperPrerequisiteStatus
            {
                MetadataFound = probe.MetadataPresent && metadata.Found,
                MetadataEligible = probe.MetadataEligible && metadata.Eligible,
                LocalTestMetadata = metadata.LocalTest,
                PackageBundleFound = metadata.PackageBundleFound,
                BrokerInstalled = File.Exists(brokerPath),
                BrokerHashMatches = metadata.BrokerHashMatches,
                BrokerServiceInstalled = service.Installed,
                BrokerServiceConfigured = service.Configured,
                BrokerServiceRunning = service.Running,
                CredentialReadable = probe.CredentialReadable,
                AuthenticatedPingSucceeded = probe.Authenticated,
                RuntimeContractCompatible = probe.IdentityValid,
                SetupScriptFound = File.Exists(setupScriptPath),
                BrokerPath = brokerPath,
                CredentialPath = credentialPath,
                MetadataPath = metadataPath,
                SetupScriptPath = setupScriptPath,
                Detail = FirstNonEmpty(probe.FailureReason, service.Detail,
                    metadata.Detail),
            };
        }

        public static bool EnsureReadyWithPrompt(Window owner,
            bool forcePrompt = false)
        {
            ViiperPrerequisiteStatus status = GetStatus();
            if (status.Ready)
            {
                return true;
            }
            if (System.Threading.Volatile.Read(ref promptShownThisSession) == 1 &&
                !forcePrompt)
            {
                return false;
            }

            System.Threading.Interlocked.Exchange(
                ref promptShownThisSession, 1);
            string message =
                "This profile uses a VIIPER native UDE virtual controller.\n\n" +
                "DS4Windows requires the signed native package, including " +
                "the UdeCx driver and the managed LocalSystem " +
                "VIIPERNativeBroker service.\n\n" +
                $"Current status: {status.DisplayText}\n\n" +
                "Install or repair it with the signed DS4Windows installer " +
                "or its installed maintenance entry, then restart " +
                "DS4Windows. The portable runtime never elevates bundled " +
                "scripts or package files.";
            ShowSetupMessage(owner, message, MessageBoxImage.Information);
            return false;
        }

        public static bool LaunchInstaller(
            ViiperPrerequisiteStatus status = null, Window owner = null)
        {
            status ??= GetStatus();
            if (status.LocalTestMetadata)
            {
                string scriptDetail = status.SetupScriptFound
                    ? "A developer may run the bundled package manager " +
                      "manually"
                    : "The source-bound package manager is absent, so " +
                      "this build must be replaced";
                ShowSetupMessage(owner,
                    "Local-test evidence is never installed by the normal UI. " +
                    scriptDetail + " " +
                    "with the environment opt-in plus -AllowLocalTest and " +
                    "-AcknowledgeDisposableTestMachine on a disposable VM.",
                    MessageBoxImage.Warning);
                return false;
            }
            if (!status.MetadataFound || !status.MetadataEligible ||
                !status.PackageBundleFound)
            {
                string detail = status.LocalTestMetadata
                    ? "This build contains verified local-test evidence, not " +
                      "production driver media. It can only be installed " +
                      "manually on a disposable VM with the explicit " +
                      $"{LocalTestOptInEnvironment}=1 developer opt-in."
                    : "This build does not contain the exact production " +
                      "HLK/WHCP runtime bundle. Installation is blocked " +
                      "instead of downloading or substituting a driver.";
                ShowSetupMessage(owner, detail, MessageBoxImage.Warning);
                return false;
            }
            ShowSetupMessage(owner,
                "The portable DS4Windows runtime never elevates its mutable " +
                "package directory. Install or repair VIIPER through the " +
                "signed DS4Windows installer or its machine-installed, " +
                "signed maintenance entry, then restart DS4Windows.",
                MessageBoxImage.Information);
            return false;
        }

        public static bool LaunchUninstaller(
            ViiperPrerequisiteStatus status = null, Window owner = null)
        {
            status ??= GetStatus();
            if (!status.MetadataEligible || !status.PackageBundleFound ||
                status.LocalTestMetadata)
            {
                ShowSetupMessage(owner,
                    "Exact signed VIIPER package metadata and helper media " +
                    "are required for transactional removal.",
                    MessageBoxImage.Error);
                return false;
            }

            ShowSetupMessage(owner,
                "Remove VIIPER through the signed DS4Windows installer or " +
                "its machine-installed, signed maintenance entry. The " +
                "portable runtime will not elevate bundled scripts or " +
                "helper media.", MessageBoxImage.Information);
            return false;
        }

        private static void ShowSetupMessage(Window owner, string message,
            MessageBoxImage image)
        {
            if (owner != null)
            {
                MessageBox.Show(owner, message, "VIIPER native UDE setup",
                    MessageBoxButton.OK, image);
            }
            else
            {
                MessageBox.Show(message, "VIIPER native UDE setup",
                    MessageBoxButton.OK, image);
            }
        }

        private static string GetInstalledBrokerPath()
        {
            string programFiles = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(programFiles, "VIIPER", "viiper.exe");
        }

        private static string GetCredentialPath()
        {
            string programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "VIIPER", "viiper.key.txt");
        }

        private static string GetMetadataPath() =>
            Path.Combine(Global.exedirpath, MetadataFileName);

        private static string GetSetupScriptPath() =>
            Path.Combine(Global.exedirpath, "extras", InstallerScriptName);

        private static NativeMetadataStatus InspectMetadata(string path,
            string installedBrokerPath)
        {
            if (!File.Exists(path))
            {
                return new NativeMetadataStatus
                {
                    Detail = "Native runtime metadata file is absent.",
                };
            }

            try
            {
                using FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;
                string eligibility = GetRequiredString(root,
                    "releaseEligibility");
                bool localTest = string.Equals(eligibility,
                    "local-test-evidence-only", StringComparison.Ordinal);
                bool eligible = string.Equals(eligibility, "production",
                    StringComparison.Ordinal) ||
                    (localTest && string.Equals(
                        Environment.GetEnvironmentVariable(
                            LocalTestOptInEnvironment), "1",
                        StringComparison.Ordinal));
                if (root.GetProperty("schemaVersion").GetInt32() != 1)
                {
                    throw new InvalidDataException(
                        "Unsupported native metadata schema.");
                }
                if (!string.Equals(GetRequiredString(root,
                        "localTestOptInEnvironment"),
                    LocalTestOptInEnvironment, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Native metadata local-test opt-in is invalid.");
                }
                JsonElement managedBroker = root.GetProperty(
                    "managedBroker");
                if (!string.Equals(GetRequiredString(managedBroker,
                        "serviceName"), NativeBrokerServiceName,
                        StringComparison.Ordinal) ||
                    !string.Equals(GetRequiredString(managedBroker,
                        "serviceAccount"), "LocalSystem",
                        StringComparison.Ordinal) ||
                    !string.Equals(GetRequiredString(managedBroker,
                        "startMode"), "automatic",
                        StringComparison.Ordinal) ||
                    !string.Equals(GetRequiredString(managedBroker,
                        "transport"), "native-ude",
                        StringComparison.Ordinal) ||
                    !string.Equals(GetRequiredString(managedBroker,
                        "apiHost"), ApiHost,
                        StringComparison.Ordinal) ||
                    managedBroker.GetProperty("apiPort").GetInt32() !=
                        ApiPort ||
                    !string.Equals(GetRequiredString(managedBroker,
                        "credentialPath"),
                        "%ProgramData%/VIIPER/viiper.key.txt",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Native metadata managed-broker contract is invalid.");
                }
                ValidateControllerApiContract(root);

                JsonElement broker = GetUniqueArtifact(root, "broker");
                bool packageFound = true;
                foreach (string role in new[]
                {
                    "broker", "driver-helper", "submission-manifest",
                    "driver-inf", "driver-sys", "driver-cat",
                })
                {
                    JsonElement artifact = GetUniqueArtifact(root, role);
                    string packagePath = ResolveBundledArtifactPath(
                        GetRequiredString(artifact, "relativePath"));
                    packageFound &= File.Exists(packagePath) &&
                        FileMatchesArtifact(packagePath, artifact);
                }
                bool installedHashMatches = File.Exists(installedBrokerPath) &&
                    FileMatchesArtifact(installedBrokerPath, broker);

                return new NativeMetadataStatus
                {
                    Found = true,
                    Eligible = eligible,
                    LocalTest = localTest,
                    PackageBundleFound = packageFound,
                    BrokerHashMatches = installedHashMatches,
                    Detail = eligible ? string.Empty :
                        "Native metadata is not eligible for this runtime mode.",
                };
            }
            catch (Exception ex) when (ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is JsonException ||
                ex is InvalidDataException ||
                ex is CryptographicException ||
                ex is KeyNotFoundException ||
                ex is InvalidOperationException)
            {
                return new NativeMetadataStatus
                {
                    Found = true,
                    Detail = $"Native runtime metadata is invalid: {ex.Message}",
                };
            }
        }

        private static string ResolveBundledArtifactPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException(
                    "Native artifact path must be relative.");
            }
            string extrasRoot = Path.GetFullPath(Path.Combine(
                Global.exedirpath, "extras"));
            string candidate = Path.GetFullPath(Path.Combine(extrasRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = extrasRoot.TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Native artifact path escaped the extras directory.");
            }
            return candidate;
        }

        private static void ValidateControllerApiContract(JsonElement root)
        {
            Dictionary<string, string> expected = new(
                StringComparer.Ordinal)
            {
                ["xbox360"] =
                    "xbox360|0x045e|0x028e|0x028e|xusb-composite|fixed",
                ["dualshock4"] =
                    "dualshock4|0x054c|0x09cc|0x05c4|hid-audio-duplex|fixed",
                ["dualshock4audioduplexv3"] =
                    "dualshock4|0x054c|0x09cc|0x05c4|hid-audio-duplex|framed-v3",
                ["dualshock4audioonlyduplexv3"] =
                    "dualshock4|0x054c|0x09cc|0x05c4|audio-duplex-only|framed-v3",
                ["dualsensecombinedaudioduplexv5"] =
                    "dualsense|0x054c|0x0ce6|0x0ce6|hid-audio-duplex|framed-v5",
                ["dualsenseaudioonlyduplexv5"] =
                    "dualsense|0x054c|0x0ce6|0x0ce6|audio-duplex-only|framed-v5",
                ["dualsensegamepadv5"] =
                    "dualsense|0x054c|0x0ce6|0x0ce6|hid-gamepad-only|framed-v5",
                ["dualsenseedgecombinedaudioduplexv5"] =
                    "dualsense-edge|0x054c|0x0df2|0x0df2|hid-audio-duplex|framed-v5",
                ["dualsenseedgegamepadv5"] =
                    "dualsense-edge|0x054c|0x0df2|0x0df2|hid-gamepad-only|framed-v5",
                ["ns2pro"] =
                    "switch2-pro|0x057e|0x2069|0x2069|hid-vendor-bulk|fixed",
            };
            JsonElement contract = root.GetProperty(
                "controllerApiContract");
            if (contract.GetProperty("schemaVersion").GetInt32() != 1 ||
                !string.Equals(GetRequiredString(contract, "sourceRevision"),
                    GetRequiredString(root, "sourceRevision"),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Controller API contract is not source-bound.");
            }

            HashSet<string> found = new(StringComparer.Ordinal);
            foreach (JsonElement registration in contract.GetProperty(
                "registrations").EnumerateArray())
            {
                string type = GetRequiredString(registration, "type");
                string signature = string.Join("|",
                    GetRequiredString(registration, "persona"),
                    GetRequiredString(registration, "defaultVid"),
                    GetRequiredString(registration, "defaultPid"),
                    GetRequiredString(registration, "ds4WindowsPid"),
                    GetRequiredString(registration, "interfaceProfile"),
                    GetRequiredString(registration, "streamProtocol"));
                if (!found.Add(type) || !expected.TryGetValue(type,
                        out string expectedSignature) ||
                    !string.Equals(signature, expectedSignature,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Controller API type {type} does not match the " +
                        "VIIPER HID/interface implementation.");
                }
            }
            if (found.Count != expected.Count)
            {
                throw new InvalidDataException(
                    "Controller API contract omits a DS4Windows persona.");
            }
        }

        private static bool FileMatchesArtifact(string path,
            JsonElement artifact)
        {
            FileInfo info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length != artifact.GetProperty("length").GetInt64())
            {
                return false;
            }
            string expected = GetRequiredString(artifact, "sha256");
            if (expected.Length != 64)
            {
                return false;
            }
            using FileStream stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            string actual = Convert.ToHexString(SHA256.HashData(stream))
                .ToLowerInvariant();
            return string.Equals(actual, expected,
                StringComparison.Ordinal);
        }

        private static JsonElement GetUniqueArtifact(JsonElement root,
            string role)
        {
            JsonElement selected = default;
            int matches = 0;
            foreach (JsonElement artifact in
                root.GetProperty("artifacts").EnumerateArray())
            {
                if (string.Equals(GetRequiredString(artifact, "role"), role,
                    StringComparison.Ordinal))
                {
                    selected = artifact;
                    matches++;
                }
            }
            if (matches != 1)
            {
                throw new InvalidDataException(
                    $"Native metadata requires exactly one {role} artifact.");
            }
            return selected;
        }

        private static string GetRequiredString(JsonElement element,
            string name)
        {
            string value = element.GetProperty(name).GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException(
                    $"Native metadata property {name} is empty.");
            }
            return value;
        }

        private static NativeServiceStatus InspectNativeBrokerService(
            string brokerPath, string credentialPath)
        {
            try
            {
                using RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{NativeBrokerServiceName}");
                if (key == null)
                {
                    return new NativeServiceStatus();
                }

                string imagePath = key.GetValue("ImagePath") as string;
                string objectName = key.GetValue("ObjectName") as string;
                int start = Convert.ToInt32(key.GetValue("Start", -1));
                int type = Convert.ToInt32(key.GetValue("Type", -1));
                string logPath = Path.Combine(
                    Path.GetDirectoryName(credentialPath),
                    "viiper-native-broker.log");
                bool configured = start == 2 &&
                    type == NativeMethods.ServiceWin32OwnProcess &&
                    IsLocalSystemAccount(objectName) &&
                    NativeMethods.CommandLineMatches(imagePath,
                        brokerPath, credentialPath, logPath);
                return new NativeServiceStatus
                {
                    Installed = true,
                    Configured = configured,
                    Running = NativeMethods.IsServiceRunning(
                        NativeBrokerServiceName),
                    Detail = configured ? string.Empty :
                        "VIIPERNativeBroker SCM configuration is not exact.",
                };
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException ||
                ex is IOException || ex is InvalidOperationException ||
                ex is System.ComponentModel.Win32Exception)
            {
                return new NativeServiceStatus
                {
                    Detail = $"Could not inspect VIIPERNativeBroker: {ex.Message}",
                };
            }
        }

        private static bool IsLocalSystemAccount(string value) =>
            string.Equals(value, "LocalSystem",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, @"NT AUTHORITY\SYSTEM",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, @".\LocalSystem",
                StringComparison.OrdinalIgnoreCase);

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return string.Empty;
        }

        private sealed class NativeMetadataStatus
        {
            internal bool Found { get; set; }
            internal bool Eligible { get; set; }
            internal bool LocalTest { get; set; }
            internal bool PackageBundleFound { get; set; }
            internal bool BrokerHashMatches { get; set; }
            internal string Detail { get; set; }
        }

        private sealed class NativeServiceStatus
        {
            internal bool Installed { get; set; }
            internal bool Configured { get; set; }
            internal bool Running { get; set; }
            internal string Detail { get; set; }
        }

        private static class NativeMethods
        {
            internal const int ServiceWin32OwnProcess = 0x10;
            private const uint ScManagerConnect = 0x0001;
            private const uint ServiceQueryStatus = 0x0004;
            private const int ScStatusProcessInfo = 0;
            private const uint ServiceRunning = 0x00000004;

            [StructLayout(LayoutKind.Sequential)]
            private struct ServiceStatusProcess
            {
                internal uint ServiceType;
                internal uint CurrentState;
                internal uint ControlsAccepted;
                internal uint Win32ExitCode;
                internal uint ServiceSpecificExitCode;
                internal uint CheckPoint;
                internal uint WaitHint;
                internal uint ProcessId;
                internal uint ServiceFlags;
            }

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
                SetLastError = true)]
            private static extern IntPtr OpenSCManager(string machineName,
                string databaseName, uint desiredAccess);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
                SetLastError = true)]
            private static extern IntPtr OpenService(IntPtr manager,
                string serviceName, uint desiredAccess);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool QueryServiceStatusEx(IntPtr service,
                int infoLevel, out ServiceStatusProcess status,
                int bufferSize, out int bytesNeeded);

            [DllImport("advapi32.dll")]
            private static extern bool CloseServiceHandle(IntPtr handle);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode,
                SetLastError = true)]
            private static extern IntPtr CommandLineToArgvW(
                string commandLine, out int argumentCount);

            [DllImport("kernel32.dll")]
            private static extern IntPtr LocalFree(IntPtr memory);

            internal static bool IsServiceRunning(string serviceName)
            {
                IntPtr manager = IntPtr.Zero;
                IntPtr service = IntPtr.Zero;
                try
                {
                    manager = OpenSCManager(null, null, ScManagerConnect);
                    if (manager == IntPtr.Zero)
                    {
                        return false;
                    }
                    service = OpenService(manager, serviceName,
                        ServiceQueryStatus);
                    if (service == IntPtr.Zero)
                    {
                        return false;
                    }
                    bool ok = QueryServiceStatusEx(service,
                        ScStatusProcessInfo, out ServiceStatusProcess status,
                        Marshal.SizeOf<ServiceStatusProcess>(), out _);
                    return ok && status.CurrentState == ServiceRunning;
                }
                finally
                {
                    if (service != IntPtr.Zero)
                    {
                        CloseServiceHandle(service);
                    }
                    if (manager != IntPtr.Zero)
                    {
                        CloseServiceHandle(manager);
                    }
                }
            }

            internal static bool CommandLineMatches(string commandLine,
                string brokerPath, string credentialPath, string logPath)
            {
                if (string.IsNullOrWhiteSpace(commandLine))
                {
                    return false;
                }
                IntPtr arguments = CommandLineToArgvW(commandLine,
                    out int count);
                if (arguments == IntPtr.Zero)
                {
                    return false;
                }
                try
                {
                    if (count != 8)
                    {
                        return false;
                    }
                    string[] expected =
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
                    for (int i = 0; i < expected.Length; i++)
                    {
                        IntPtr value = Marshal.ReadIntPtr(arguments,
                            i * IntPtr.Size);
                        string actual = Marshal.PtrToStringUni(value);
                        StringComparison comparison = i == 0 || i == 5 ||
                            i == 7
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal;
                        if (!string.Equals(actual, expected[i], comparison))
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
        }
    }
}
