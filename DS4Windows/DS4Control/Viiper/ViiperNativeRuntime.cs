/*
 DS4Windows
 Copyright (C) 2026 hbashton

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DS4Windows
{
    internal enum ViiperTransportMode
    {
        NativeUde,
        Usbip,
    }

    internal static class ViiperTransportSettings
    {
        internal const string TransportEnvironmentVariable =
            "DS4WINDOWS_VIIPER_TRANSPORT";
        internal const string LocalTestEnvironmentVariable =
            "DS4WINDOWS_VIIPER_ALLOW_LOCAL_TEST";

        internal static ViiperTransportMode GetManagedMode()
        {
            return Parse(Environment.GetEnvironmentVariable(
                TransportEnvironmentVariable));
        }

        internal static ViiperTransportMode Parse(string value)
        {
            string normalized = value?.Trim();
            if (string.IsNullOrEmpty(normalized) ||
                string.Equals(normalized, "native-ude",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "native",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ViiperTransportMode.NativeUde;
            }
            if (string.Equals(normalized, "usbip",
                StringComparison.OrdinalIgnoreCase))
            {
                return ViiperTransportMode.Usbip;
            }

            throw new ViiperNativeMetadataException(
                $"Unsupported {TransportEnvironmentVariable} value '{normalized}'. Expected native-ude or usbip.");
        }

        internal static bool AllowsLocalTestMetadata(string value = null)
        {
            value ??= Environment.GetEnvironmentVariable(
                LocalTestEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.Ordinal);
        }
    }

    internal sealed class ViiperNativeMetadataException : IOException
    {
        internal ViiperNativeMetadataException(string message,
            Exception inner = null) : base(message, inner)
        {
        }
    }

    internal sealed class ViiperCredentialException : IOException
    {
        internal ViiperCredentialException(string message,
            Exception inner = null) : base(message, inner)
        {
        }
    }

    internal sealed class ViiperAuthenticationException : IOException
    {
        internal ViiperAuthenticationException(string message,
            Exception inner = null) : base(message, inner)
        {
        }
    }

    internal sealed class ViiperIdentityException : IOException
    {
        internal ViiperIdentityException(string message,
            Exception inner = null) : base(message, inner)
        {
        }
    }

    internal sealed class ViiperNativeControllerRegistration
    {
        internal string Type { get; init; }
        internal string DefaultVid { get; init; }
        internal string DefaultPid { get; init; }
        internal string Ds4WindowsPid { get; init; }
        internal string InterfaceProfile { get; init; }
        internal string StreamProtocol { get; init; }

        internal ushort Ds4WindowsPidValue => ushort.Parse(
            Ds4WindowsPid.AsSpan(2), NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);

        internal bool HasSameContract(
            ViiperNativeControllerRegistration other)
        {
            return other != null &&
                string.Equals(Type, other.Type, StringComparison.Ordinal) &&
                string.Equals(DefaultVid, other.DefaultVid,
                    StringComparison.Ordinal) &&
                string.Equals(DefaultPid, other.DefaultPid,
                    StringComparison.Ordinal) &&
                string.Equals(Ds4WindowsPid, other.Ds4WindowsPid,
                    StringComparison.Ordinal) &&
                string.Equals(InterfaceProfile, other.InterfaceProfile,
                    StringComparison.Ordinal) &&
                string.Equals(StreamProtocol, other.StreamProtocol,
                    StringComparison.Ordinal);
        }
    }

    internal sealed class ViiperNativeRuntimeMetadata
    {
        internal const string FileName = "ViiperNativeRuntimeMetadata.json";
        internal const string ProductionEligibility = "production";
        internal const string LocalTestEligibility =
            "local-test-evidence-only";

        internal int SchemaVersion { get; init; }
        internal string SourcePath { get; init; }
        internal string SourceRevision { get; init; }
        internal string ReleaseEligibility { get; init; }
        internal string DriverPackageVersion { get; init; }
        internal ushort AbiMajor { get; init; }
        internal ushort AbiMinor { get; init; }
        internal uint RequiredCapabilities { get; init; }
        internal string RequiredCapabilitiesHex { get; init; }
        internal string LoadedDriverBuildIdentity { get; init; }
        internal IReadOnlyDictionary<string,
            ViiperNativeControllerRegistration> ControllerApiContract
            { get; init; }

        internal static ViiperNativeRuntimeMetadata LoadBundled(
            string baseDirectory = null, string localTestOptIn = null)
        {
            baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ?
                AppContext.BaseDirectory : Path.GetFullPath(baseDirectory);
            string[] candidates =
            {
                Path.Combine(baseDirectory, "extras", FileName),
                Path.Combine(baseDirectory, FileName),
            };
            string[] existing = candidates.Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (existing.Length == 0)
            {
                throw new ViiperNativeMetadataException(
                    $"Bundled {FileName} is missing. Native VIIPER admission requires package-generated metadata.");
            }

            ViiperNativeRuntimeMetadata selected = Parse(existing[0],
                localTestOptIn);
            for (int index = 1; index < existing.Length; index++)
            {
                ViiperNativeRuntimeMetadata duplicate = Parse(existing[index],
                    localTestOptIn);
                if (!selected.HasSameContract(duplicate))
                {
                    throw new ViiperNativeMetadataException(
                        $"Conflicting native VIIPER metadata files were bundled at '{selected.SourcePath}' and '{duplicate.SourcePath}'.");
                }
            }
            return selected;
        }

        internal static ViiperNativeRuntimeMetadata Parse(string path,
            string localTestOptIn = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A metadata path is required.",
                    nameof(path));
            }

            string fullPath = Path.GetFullPath(path);
            try
            {
                using FileStream file = new FileStream(fullPath,
                    FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.SequentialScan);
                using JsonDocument document = JsonDocument.Parse(file,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 32,
                    });
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new ViiperNativeMetadataException(
                        "Native VIIPER metadata must be a JSON object.");
                }

                int schemaVersion = GetRequiredInt32(root, "schemaVersion");
                if (schemaVersion != 1)
                {
                    throw new ViiperNativeMetadataException(
                        $"Unsupported native VIIPER metadata schemaVersion={schemaVersion}.");
                }
                string eligibility = GetRequiredString(root,
                    "releaseEligibility");
                string localTestOptInEnvironment = GetRequiredString(root,
                    "localTestOptInEnvironment");
                if (!string.Equals(localTestOptInEnvironment,
                        ViiperTransportSettings.LocalTestEnvironmentVariable,
                        StringComparison.Ordinal))
                {
                    throw new ViiperNativeMetadataException(
                        "Native VIIPER metadata names an unexpected local-test opt-in boundary.");
                }
                bool eligibilityAccepted = string.Equals(eligibility,
                    ProductionEligibility, StringComparison.Ordinal);
                if (string.Equals(eligibility, LocalTestEligibility,
                        StringComparison.Ordinal))
                {
                    eligibilityAccepted =
                        ViiperTransportSettings.AllowsLocalTestMetadata(
                            localTestOptIn);
                    if (!eligibilityAccepted)
                    {
                        throw new ViiperNativeMetadataException(
                            $"Bundled native VIIPER metadata is '{LocalTestEligibility}'. Set {ViiperTransportSettings.LocalTestEnvironmentVariable}=1 only inside the disposable VM/laptop test environment.");
                    }
                }
                if (!eligibilityAccepted)
                {
                    throw new ViiperNativeMetadataException(
                        $"Native VIIPER metadata releaseEligibility='{eligibility}' is not admissible.");
                }

                JsonElement abi = GetRequiredObject(root, "driverAbi");
                int abiMajor = GetRequiredInt32(abi, "major");
                int abiMinor = GetRequiredInt32(abi, "minor");
                uint capabilities = GetRequiredUInt32(root,
                    "requiredCapabilities");
                string capabilitiesHex = GetRequiredString(root,
                    "requiredCapabilitiesHex");
                string packageVersion = GetRequiredString(root,
                    "driverPackageVersion");
                string buildIdentity = GetRequiredString(root,
                    "loadedDriverBuildIdentity");
                string sourceRevision = GetRequiredString(root,
                    "sourceRevision");
                if (abiMajor <= 0 || abiMajor > ushort.MaxValue ||
                    abiMinor < 0 || abiMinor > ushort.MaxValue)
                {
                    throw new ViiperNativeMetadataException(
                        "Native VIIPER metadata contains an invalid driver ABI.");
                }
                if (capabilities == 0)
                {
                    throw new ViiperNativeMetadataException(
                        "Native VIIPER metadata requires zero capabilities.");
                }
                string canonicalCapabilitiesHex = string.Format(
                    CultureInfo.InvariantCulture, "0x{0:x8}", capabilities);
                if (!string.Equals(capabilitiesHex,
                        canonicalCapabilitiesHex, StringComparison.Ordinal))
                {
                    throw new ViiperNativeMetadataException(
                        $"Native VIIPER metadata requiredCapabilitiesHex='{capabilitiesHex}' does not exactly encode requiredCapabilities as '{canonicalCapabilitiesHex}'.");
                }
                ValidatePackageVersion(packageVersion);
                ValidateLowercaseSha256(buildIdentity,
                    "loadedDriverBuildIdentity");
                ValidateSourceRevision(sourceRevision);
                ValidateManagedBroker(root);
                IReadOnlyDictionary<string,
                    ViiperNativeControllerRegistration> controllerApi =
                    ParseControllerApiContract(root, sourceRevision);

                return new ViiperNativeRuntimeMetadata
                {
                    SchemaVersion = schemaVersion,
                    SourcePath = fullPath,
                    SourceRevision = sourceRevision,
                    ReleaseEligibility = eligibility,
                    DriverPackageVersion = packageVersion,
                    AbiMajor = (ushort)abiMajor,
                    AbiMinor = (ushort)abiMinor,
                    RequiredCapabilities = capabilities,
                    RequiredCapabilitiesHex = capabilitiesHex,
                    LoadedDriverBuildIdentity = buildIdentity,
                    ControllerApiContract = controllerApi,
                };
            }
            catch (ViiperNativeMetadataException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException ||
                ex is UnauthorizedAccessException || ex is JsonException ||
                ex is FormatException || ex is OverflowException)
            {
                throw new ViiperNativeMetadataException(
                    $"Could not read native VIIPER metadata '{fullPath}': {ex.Message}",
                    ex);
            }
        }

        private bool HasSameContract(ViiperNativeRuntimeMetadata other)
        {
            return other != null && SchemaVersion == other.SchemaVersion &&
                string.Equals(SourceRevision, other.SourceRevision,
                    StringComparison.Ordinal) &&
                string.Equals(ReleaseEligibility, other.ReleaseEligibility,
                    StringComparison.Ordinal) &&
                string.Equals(DriverPackageVersion,
                    other.DriverPackageVersion, StringComparison.Ordinal) &&
                AbiMajor == other.AbiMajor && AbiMinor == other.AbiMinor &&
                RequiredCapabilities == other.RequiredCapabilities &&
                string.Equals(RequiredCapabilitiesHex,
                    other.RequiredCapabilitiesHex, StringComparison.Ordinal) &&
                string.Equals(LoadedDriverBuildIdentity,
                    other.LoadedDriverBuildIdentity,
                    StringComparison.Ordinal) &&
                HasSameControllerApiContract(other);
        }

        private bool HasSameControllerApiContract(
            ViiperNativeRuntimeMetadata other)
        {
            if (ControllerApiContract == null ||
                other.ControllerApiContract == null ||
                ControllerApiContract.Count !=
                    other.ControllerApiContract.Count)
            {
                return false;
            }
            foreach (KeyValuePair<string,
                ViiperNativeControllerRegistration> entry in
                ControllerApiContract)
            {
                if (!other.ControllerApiContract.TryGetValue(entry.Key,
                        out ViiperNativeControllerRegistration candidate) ||
                    !entry.Value.HasSameContract(candidate))
                {
                    return false;
                }
            }
            return true;
        }

        private static JsonElement GetRequiredObject(JsonElement parent,
            string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Object)
            {
                throw new ViiperNativeMetadataException(
                    $"Native VIIPER metadata field '{name}' must be an object.");
            }
            return value;
        }

        private static void ValidateManagedBroker(JsonElement root)
        {
            JsonElement broker = GetRequiredObject(root, "managedBroker");
            bool valid = string.Equals(GetRequiredString(broker,
                    "serviceName"), "VIIPERNativeBroker",
                    StringComparison.Ordinal) &&
                string.Equals(GetRequiredString(broker, "serviceAccount"),
                    "LocalSystem", StringComparison.Ordinal) &&
                string.Equals(GetRequiredString(broker, "startMode"),
                    "automatic", StringComparison.Ordinal) &&
                string.Equals(GetRequiredString(broker, "transport"),
                    "native-ude", StringComparison.Ordinal) &&
                string.Equals(GetRequiredString(broker, "apiHost"),
                    "127.0.0.1", StringComparison.Ordinal) &&
                GetRequiredInt32(broker, "apiPort") == 3242 &&
                string.Equals(GetRequiredString(broker, "credentialPath"),
                    "%ProgramData%/VIIPER/viiper.key.txt",
                    StringComparison.Ordinal);
            if (!valid)
            {
                throw new ViiperNativeMetadataException(
                    "Native VIIPER metadata managedBroker does not match the protected loopback service contract.");
            }
        }

        private static IReadOnlyDictionary<string,
            ViiperNativeControllerRegistration> ParseControllerApiContract(
            JsonElement root, string sourceRevision)
        {
            JsonElement contract = GetRequiredObject(root,
                "controllerApiContract");
            if (GetRequiredInt32(contract, "schemaVersion") != 1 ||
                !string.Equals(GetRequiredString(contract,
                        "sourceRevision"), sourceRevision,
                    StringComparison.Ordinal))
            {
                throw new ViiperNativeMetadataException(
                    "Native VIIPER controller API contract is not bound to the package source revision.");
            }
            _ = GetRequiredString(contract, "implementation");
            if (!contract.TryGetProperty("registrations",
                    out JsonElement registrations) ||
                registrations.ValueKind != JsonValueKind.Array ||
                registrations.GetArrayLength() == 0)
            {
                throw new ViiperNativeMetadataException(
                    "Native VIIPER controller API contract has no registrations.");
            }

            var result = new Dictionary<string,
                ViiperNativeControllerRegistration>(StringComparer.Ordinal);
            foreach (JsonElement registration in registrations.EnumerateArray())
            {
                if (registration.ValueKind != JsonValueKind.Object)
                {
                    throw new ViiperNativeMetadataException(
                        "Native VIIPER controller API registration must be an object.");
                }
                string type = GetRequiredString(registration, "type");
                string defaultVid = GetRequiredString(registration,
                    "defaultVid");
                string defaultPid = GetRequiredString(registration,
                    "defaultPid");
                string clientPid = GetRequiredString(registration,
                    "ds4WindowsPid");
                ValidateControllerType(type);
                ValidateUsbId(defaultVid, "defaultVid");
                ValidateUsbId(defaultPid, "defaultPid");
                ValidateUsbId(clientPid, "ds4WindowsPid");
                var parsed = new ViiperNativeControllerRegistration
                {
                    Type = type,
                    DefaultVid = defaultVid,
                    DefaultPid = defaultPid,
                    Ds4WindowsPid = clientPid,
                    InterfaceProfile = GetRequiredString(registration,
                        "interfaceProfile"),
                    StreamProtocol = GetRequiredString(registration,
                        "streamProtocol"),
                };
                if (!result.TryAdd(type, parsed))
                {
                    throw new ViiperNativeMetadataException(
                        $"Native VIIPER controller API type '{type}' is duplicated.");
                }
            }
            return result;
        }

        private static void ValidateControllerType(string value)
        {
            if (value.Length > 64 || value.Any(character =>
                    !(character >= 'a' && character <= 'z') &&
                    !(character >= '0' && character <= '9')))
            {
                throw new ViiperNativeMetadataException(
                    $"Native VIIPER controller API type '{value}' is not canonical.");
            }
        }

        private static void ValidateUsbId(string value, string fieldName)
        {
            if (value.Length != 6 || value[0] != '0' || value[1] != 'x' ||
                !value.AsSpan(2).ToArray().All(IsLowerHex))
            {
                throw new ViiperNativeMetadataException(
                    $"Native VIIPER controller API {fieldName} '{value}' is not canonical 0xhhhh.");
            }
        }

        private static string GetRequiredString(JsonElement parent,
            string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new ViiperNativeMetadataException(
                    $"Native VIIPER metadata field '{name}' must be a non-empty string.");
            }
            string text = value.GetString();
            if (!string.Equals(text, text.Trim(), StringComparison.Ordinal))
            {
                throw new ViiperNativeMetadataException(
                    $"Native VIIPER metadata field '{name}' contains surrounding whitespace.");
            }
            return text;
        }

        private static int GetRequiredInt32(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out int result))
            {
                throw new ViiperNativeMetadataException(
                    $"Native VIIPER metadata field '{name}' must be a 32-bit integer.");
            }
            return result;
        }

        private static uint GetRequiredUInt32(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetUInt32(out uint result))
            {
                throw new ViiperNativeMetadataException(
                    $"Native VIIPER metadata field '{name}' must be a 32-bit unsigned integer.");
            }
            return result;
        }

        private static void ValidatePackageVersion(string value)
        {
            string[] components = value.Split('.');
            if (components.Length != 4 || components.Any(component =>
                    component.Length == 0 || !uint.TryParse(component,
                        NumberStyles.None, CultureInfo.InvariantCulture,
                        out _)))
            {
                throw new ViiperNativeMetadataException(
                    "Native VIIPER metadata driverPackageVersion must contain four numeric parts.");
            }
        }

        private static void ValidateSourceRevision(string value)
        {
            if ((value.Length != 40 && value.Length != 64) ||
                !value.All(IsLowerHex))
            {
                throw new ViiperNativeMetadataException(
                    "Native VIIPER metadata sourceRevision must be 40 or 64 lowercase hexadecimal digits.");
            }
        }

        internal static void ValidateLowercaseSha256(string value,
            string fieldName)
        {
            if (value == null || value.Length != 64 ||
                !value.All(IsLowerHex) || value.All(character => character == '0'))
            {
                throw new ViiperIdentityException(
                    $"VIIPER {fieldName} must be a non-zero lowercase SHA-256 value.");
            }
        }

        private static bool IsLowerHex(char value) =>
            value >= '0' && value <= '9' || value >= 'a' && value <= 'f';
    }

    internal sealed class ViiperNativeBackendIdentity
    {
        internal string Server { get; init; }
        internal string Version { get; init; }
        internal string Transport { get; init; }
        internal ushort AbiMajor { get; init; }
        internal ushort AbiMinor { get; init; }
        internal uint Capabilities { get; init; }
        internal string DriverPackageVersion { get; init; }
        internal string DriverBuildIdentity { get; init; }
        internal string ControllerInstanceId { get; init; }
        internal ulong ControllerSessionId { get; init; }

        internal bool HasSameGeneration(
            ViiperNativeBackendIdentity other)
        {
            return other != null &&
                string.Equals(Server, other.Server, StringComparison.Ordinal) &&
                string.Equals(Version, other.Version, StringComparison.Ordinal) &&
                string.Equals(Transport, other.Transport,
                    StringComparison.Ordinal) &&
                AbiMajor == other.AbiMajor && AbiMinor == other.AbiMinor &&
                Capabilities == other.Capabilities &&
                string.Equals(DriverPackageVersion,
                    other.DriverPackageVersion, StringComparison.Ordinal) &&
                string.Equals(DriverBuildIdentity,
                    other.DriverBuildIdentity, StringComparison.Ordinal) &&
                string.Equals(ControllerInstanceId,
                    other.ControllerInstanceId, StringComparison.Ordinal) &&
                ControllerSessionId == other.ControllerSessionId;
        }
    }

    internal sealed class ViiperNativePingResponse
    {
        [JsonPropertyName("server")]
        public string Server { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("transport")]
        public string Transport { get; set; }

        [JsonPropertyName("ready")]
        public bool? Ready { get; set; }

        [JsonPropertyName("nativeUde")]
        public ViiperNativeUdePingInfo NativeUde { get; set; }
    }

    internal sealed class ViiperNativeUdePingInfo
    {
        [JsonPropertyName("abiMajor")]
        public ushort AbiMajor { get; set; }

        [JsonPropertyName("abiMinor")]
        public ushort AbiMinor { get; set; }

        [JsonPropertyName("capabilities")]
        public uint Capabilities { get; set; }

        [JsonPropertyName("expectedDriverPackageVersion")]
        public string ExpectedDriverPackageVersion { get; set; }

        [JsonPropertyName("loadedDriverBuildIdentity")]
        public string LoadedDriverBuildIdentity { get; set; }

        [JsonPropertyName("controllerInstanceId")]
        public string ControllerInstanceId { get; set; }

        [JsonPropertyName("controllerSessionId")]
        public string ControllerSessionId { get; set; }
    }

    internal sealed class ViiperNativeRuntimeContract
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            };

        internal ViiperNativeRuntimeContract(
            ViiperNativeRuntimeMetadata metadata)
        {
            Metadata = metadata ??
                throw new ArgumentNullException(nameof(metadata));
        }

        internal ViiperNativeRuntimeMetadata Metadata { get; }

        internal ViiperNativeControllerRegistration GetControllerRegistration(
            string type)
        {
            if (type == null || Metadata.ControllerApiContract == null ||
                !Metadata.ControllerApiContract.TryGetValue(type,
                    out ViiperNativeControllerRegistration registration))
            {
                throw new ViiperIdentityException(
                    $"VIIPER native controller type '{type ?? "<missing>"}' is not in the source-bound controller API contract.");
            }
            return registration;
        }

        internal ushort ValidateControllerRequest(string type,
            ushort? requestedPid)
        {
            ViiperNativeControllerRegistration registration =
                GetControllerRegistration(type);
            ushort expected = registration.Ds4WindowsPidValue;
            if (requestedPid.HasValue && requestedPid.Value != expected)
            {
                throw new ViiperIdentityException(
                    $"VIIPER native controller type '{type}' cannot override its source-bound DS4Windows product ID.");
            }
            return expected;
        }

        internal bool HasExactControllerIdentity(string type, string vid,
            string pid)
        {
            ViiperNativeControllerRegistration registration =
                GetControllerRegistration(type);
            return string.Equals(vid, registration.DefaultVid,
                       StringComparison.Ordinal) &&
                string.Equals(pid, registration.Ds4WindowsPid,
                    StringComparison.Ordinal);
        }

        internal ViiperNativeBackendIdentity ValidatePing(string raw)
        {
            ViiperNativePingResponse ping;
            try
            {
                ValidateNoDuplicateJsonProperties(raw, "ping");
                ping = JsonSerializer.Deserialize<ViiperNativePingResponse>(
                    raw, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new ViiperIdentityException(
                    "VIIPER ping was not valid JSON.", ex);
            }
            if (ping == null)
            {
                throw new ViiperIdentityException(
                    "VIIPER returned an empty ping identity.");
            }
            if (!string.Equals(ping.Server, "VIIPER",
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"Unexpected VIIPER server identity '{ping.Server ?? "<missing>"}'.");
            }
            if (string.IsNullOrWhiteSpace(ping.Version))
            {
                throw new ViiperIdentityException(
                    "VIIPER ping omitted the broker version.");
            }
            if (!string.Equals(ping.Transport, "native-ude",
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"VIIPER transport is '{ping.Transport ?? "<missing>"}', expected native-ude. USB/IP responses are not admitted on the managed native path.");
            }
            if (ping.Ready != true)
            {
                throw new ViiperIdentityException(
                    "VIIPER native-ude transport is not ready.");
            }
            if (ping.NativeUde == null)
            {
                throw new ViiperIdentityException(
                    "VIIPER ping omitted the nativeUde contract.");
            }

            ViiperNativeUdePingInfo native = ping.NativeUde;
            if (native.AbiMajor != Metadata.AbiMajor ||
                native.AbiMinor != Metadata.AbiMinor)
            {
                throw new ViiperIdentityException(
                    $"VIIPER native ABI is {native.AbiMajor}.{native.AbiMinor}, expected {Metadata.AbiMajor}.{Metadata.AbiMinor} from bundled metadata.");
            }
            if (native.Capabilities != Metadata.RequiredCapabilities)
            {
                throw new ViiperIdentityException(
                    $"VIIPER native capabilities are 0x{native.Capabilities:x}, expected exact 0x{Metadata.RequiredCapabilities:x} from bundled metadata.");
            }
            if (!string.Equals(native.ExpectedDriverPackageVersion,
                    Metadata.DriverPackageVersion, StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"VIIPER expects driver package '{native.ExpectedDriverPackageVersion ?? "<missing>"}', bundled metadata expects '{Metadata.DriverPackageVersion}'.");
            }
            ViiperNativeRuntimeMetadata.ValidateLowercaseSha256(
                native.LoadedDriverBuildIdentity,
                "loadedDriverBuildIdentity");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(native.LoadedDriverBuildIdentity),
                    Encoding.ASCII.GetBytes(
                        Metadata.LoadedDriverBuildIdentity)))
            {
                throw new ViiperIdentityException(
                    "VIIPER loaded-driver build identity does not match bundled metadata.");
            }
            ValidateControllerInstanceId(native.ControllerInstanceId);
            ulong controllerSessionId = ParseCanonicalNonZeroUInt64(
                native.ControllerSessionId,
                "nativeUde.controllerSessionId");

            return new ViiperNativeBackendIdentity
            {
                Server = ping.Server,
                Version = ping.Version,
                Transport = ping.Transport,
                AbiMajor = native.AbiMajor,
                AbiMinor = native.AbiMinor,
                Capabilities = native.Capabilities,
                DriverPackageVersion = native.ExpectedDriverPackageVersion,
                DriverBuildIdentity = native.LoadedDriverBuildIdentity,
                ControllerInstanceId = native.ControllerInstanceId,
                ControllerSessionId = controllerSessionId,
            };
        }

        internal static void ValidateNoDuplicateJsonProperties(string raw,
            string responseName)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 32,
                    });
                ValidateNoDuplicateJsonProperties(document.RootElement,
                    responseName ?? "response");
            }
            catch (JsonException ex)
            {
                throw new ViiperIdentityException(
                    $"VIIPER {responseName ?? "response"} was not valid JSON.",
                    ex);
            }
        }

        private static void ValidateNoDuplicateJsonProperties(
            JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new ViiperIdentityException(
                            $"VIIPER {path} contains duplicate JSON property '{property.Name}'.");
                    }
                    ValidateNoDuplicateJsonProperties(property.Value,
                        path + "." + property.Name);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateNoDuplicateJsonProperties(item,
                        $"{path}[{index++}]");
                }
            }
        }

        internal static ulong ParseCanonicalNonZeroUInt64(string value,
            string fieldName)
        {
            if (!ulong.TryParse(value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong parsed) ||
                parsed == 0 ||
                !string.Equals(parsed.ToString(CultureInfo.InvariantCulture),
                    value, StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"VIIPER {fieldName} is not canonical non-zero decimal uint64 text.");
            }
            return parsed;
        }

        internal static void ValidateControllerInstanceId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                value.IndexOf('/') >= 0 || value.IndexOf('\0') >= 0 ||
                !value.StartsWith(@"ROOT\VIIPERUDE\",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(value, value.ToUpperInvariant(),
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    "VIIPER nativeUde.controllerInstanceId is not a canonical VIIPER UdeCx controller instance ID.");
            }
        }
    }

    internal sealed class ViiperCredential : IDisposable
    {
        private int disposed;

        internal ViiperCredential(string password, byte[] fingerprint)
        {
            Password = password ?? throw new ArgumentNullException(
                nameof(password));
            Fingerprint = fingerprint ?? throw new ArgumentNullException(
                nameof(fingerprint));
        }

        internal string Password { get; private set; }
        internal byte[] Fingerprint { get; private set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            if (Fingerprint != null)
            {
                CryptographicOperations.ZeroMemory(Fingerprint);
                Fingerprint = null;
            }
            Password = null;
        }
    }

    internal interface IViiperCredentialProvider
    {
        ViiperCredential Read();
    }

    internal sealed class ViiperProgramDataCredentialProvider :
        IViiperCredentialProvider
    {
        internal const string CredentialFileName = "viiper.key.txt";
        internal const int ManagedCredentialLength = 16;

        internal ViiperProgramDataCredentialProvider(string path = null)
        {
            CredentialPath = string.IsNullOrWhiteSpace(path) ?
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                    "VIIPER", CredentialFileName) : Path.GetFullPath(path);
        }

        internal string CredentialPath { get; }

        public ViiperCredential Read()
        {
            byte[] bytes = null;
            try
            {
                FileAttributes attributes = File.GetAttributes(CredentialPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) != 0)
                {
                    throw new ViiperCredentialException(
                        "The managed VIIPER credential is not a regular file.");
                }
                using FileStream file = new FileStream(CredentialPath,
                    FileMode.Open, FileAccess.Read, FileShare.Read,
                    ManagedCredentialLength,
                    FileOptions.SequentialScan);
                if (file.Length != ManagedCredentialLength)
                {
                    throw new ViiperCredentialException(
                        $"The managed VIIPER credential must be exactly {ManagedCredentialLength} bytes.");
                }
                bytes = new byte[ManagedCredentialLength];
                int total = 0;
                while (total < bytes.Length)
                {
                    int read = file.Read(bytes, total, bytes.Length - total);
                    if (read == 0)
                    {
                        throw new ViiperCredentialException(
                            "The managed VIIPER credential changed while it was read.");
                    }
                    total += read;
                }
                if (file.ReadByte() != -1)
                {
                    throw new ViiperCredentialException(
                        "The managed VIIPER credential changed while it was read.");
                }
                if (bytes.Any(value => !IsBase62(value)))
                {
                    throw new ViiperCredentialException(
                        "The managed VIIPER credential is not canonical base62.");
                }
                string password = Encoding.ASCII.GetString(bytes);
                byte[] fingerprint = SHA256.HashData(bytes);
                return new ViiperCredential(password, fingerprint);
            }
            catch (ViiperCredentialException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException ||
                ex is UnauthorizedAccessException)
            {
                throw new ViiperCredentialException(
                    $"The managed VIIPER credential is missing or unreadable at '{CredentialPath}'.",
                    ex);
            }
            finally
            {
                if (bytes != null)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        private static bool IsBase62(byte value) =>
            value >= (byte)'0' && value <= (byte)'9' ||
            value >= (byte)'A' && value <= (byte)'Z' ||
            value >= (byte)'a' && value <= (byte)'z';
    }

    internal sealed class ViiperNativeSession
    {
        private readonly object sessionLock = new object();
        private readonly object authenticationLock = new object();
        private readonly IViiperCredentialProvider credentialProvider;
        private readonly ViiperNativeRuntimeContract contract;
        private byte[] credentialFingerprint;
        private ViiperNativeBackendIdentity identity;
        private Exception fatalError;
        private bool hasAuthenticatedConnection;

        internal ViiperNativeSession(ViiperNativeRuntimeContract contract,
            IViiperCredentialProvider credentialProvider)
        {
            this.contract = contract ??
                throw new ArgumentNullException(nameof(contract));
            this.credentialProvider = credentialProvider ??
                throw new ArgumentNullException(nameof(credentialProvider));
        }

        internal ViiperNativeBackendIdentity Identity
        {
            get
            {
                lock (sessionLock)
                {
                    return identity;
                }
            }
        }

        internal ViiperNativeRuntimeContract Contract => contract;

        internal bool HasAuthenticatedConnection
        {
            get
            {
                lock (sessionLock)
                {
                    return hasAuthenticatedConnection;
                }
            }
        }

        internal Stream Authenticate(Stream transport)
        {
            lock (authenticationLock)
            {
                return AuthenticateSerialized(transport);
            }
        }

        private Stream AuthenticateSerialized(Stream transport)
        {
            lock (sessionLock)
            {
                ThrowIfFatal();
            }
            using ViiperCredential credential = credentialProvider.Read();
            lock (sessionLock)
            {
                ThrowIfFatal();
                if (credentialFingerprint == null)
                {
                    credentialFingerprint =
                        (byte[])credential.Fingerprint.Clone();
                }
                else if (!CryptographicOperations.FixedTimeEquals(
                             credentialFingerprint,
                             credential.Fingerprint))
                {
                    ViiperAuthenticationException changed = new
                        ViiperAuthenticationException(
                            "The managed VIIPER credential generation changed during an active controller lifetime.");
                    fatalError = changed;
                    throw changed;
                }
            }

            try
            {
                Stream authenticated =
                    ViiperAuthProtocol.AuthenticateClient(transport,
                    credential.Password);
                lock (sessionLock)
                {
                    try
                    {
                        ThrowIfFatal();
                        hasAuthenticatedConnection = true;
                    }
                    catch
                    {
                        authenticated.Dispose();
                        throw;
                    }
                }
                return authenticated;
            }
            catch (ViiperAuthenticationException ex)
            {
                lock (sessionLock)
                {
                    fatalError ??= ex;
                }
                throw;
            }
            catch (Exception ex) when (ex is IOException ||
                ex is CryptographicException ||
                ex is UnauthorizedAccessException)
            {
                ViiperAuthenticationException wrapped = new
                    ViiperAuthenticationException(
                        "VIIPER connection authentication failed.", ex);
                throw wrapped;
            }
        }

        internal void InvalidateIdentity(Exception failure)
        {
            if (failure == null)
            {
                throw new ArgumentNullException(nameof(failure));
            }
            lock (sessionLock)
            {
                fatalError ??= failure;
            }
        }

        internal ViiperNativeBackendIdentity AdmitPing(string raw,
            bool reconnect)
        {
            ViiperNativeBackendIdentity candidate;
            try
            {
                candidate = contract.ValidatePing(raw);
            }
            catch (Exception ex) when (ex is ViiperIdentityException ||
                ex is JsonException)
            {
                lock (sessionLock)
                {
                    fatalError ??= ex;
                }
                throw;
            }

            lock (sessionLock)
            {
                ThrowIfFatal();
                if (identity == null)
                {
                    identity = candidate;
                }
                else if (!identity.HasSameGeneration(candidate))
                {
                    ViiperIdentityException changed = new
                        ViiperIdentityException(
                            "VIIPER backend identity changed during an active controller lifetime.");
                    fatalError = changed;
                    throw changed;
                }
                else if (reconnect && credentialFingerprint == null)
                {
                    ViiperIdentityException changed = new
                        ViiperIdentityException(
                            "VIIPER reconnect has no pinned credential generation.");
                    fatalError = changed;
                    throw changed;
                }
                return identity;
            }
        }

        private void ThrowIfFatal()
        {
            if (fatalError != null)
            {
                throw new ViiperIdentityException(
                    "The VIIPER native session is permanently invalid after a credential, authentication, or identity failure.",
                    fatalError);
            }
        }
    }

    internal sealed class ViiperNativeStatusProbe
    {
        internal bool Ready { get; init; }
        internal bool MetadataPresent { get; init; }
        internal bool MetadataEligible { get; init; }
        internal bool CredentialReadable { get; init; }
        internal bool Authenticated { get; init; }
        internal bool IdentityValid { get; init; }
        internal string FailureReason { get; init; }
        internal ViiperNativeBackendIdentity Identity { get; init; }
    }

    internal static class ViiperNativeRuntime
    {
        internal static ViiperNativeStatusProbe GetStatusProbe(string host,
            int port)
        {
            ViiperNativeRuntimeMetadata metadata;
            bool metadataPresent = MetadataExists();
            try
            {
                metadata = ViiperNativeRuntimeMetadata.LoadBundled();
            }
            catch (Exception ex)
            {
                return new ViiperNativeStatusProbe
                {
                    MetadataPresent = metadataPresent,
                    MetadataEligible = false,
                    FailureReason = ex.Message,
                };
            }

            IViiperCredentialProvider credentialProvider =
                new ViiperProgramDataCredentialProvider();
            try
            {
                using ViiperCredential ignored = credentialProvider.Read();
            }
            catch (Exception ex)
            {
                return new ViiperNativeStatusProbe
                {
                    MetadataPresent = true,
                    MetadataEligible = true,
                    FailureReason = ex.Message,
                };
            }

            ViiperClient client = null;
            try
            {
                client = new ViiperClient(host, port,
                    ViiperTransportMode.NativeUde, metadata,
                    credentialProvider);
                ViiperNativeBackendIdentity identity =
                    client.ValidateNativeBackend();
                return new ViiperNativeStatusProbe
                {
                    Ready = true,
                    MetadataPresent = true,
                    MetadataEligible = true,
                    CredentialReadable = true,
                    Authenticated = true,
                    IdentityValid = true,
                    Identity = identity,
                };
            }
            catch (ViiperAuthenticationException ex)
            {
                return new ViiperNativeStatusProbe
                {
                    MetadataPresent = true,
                    MetadataEligible = true,
                    CredentialReadable = true,
                    FailureReason = ex.Message,
                };
            }
            catch (Exception ex)
            {
                return new ViiperNativeStatusProbe
                {
                    MetadataPresent = true,
                    MetadataEligible = true,
                    CredentialReadable = true,
                    Authenticated = client?.HasAuthenticatedNativeConnection ==
                        true,
                    FailureReason = ex.Message,
                };
            }
        }

        private static bool MetadataExists()
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory,
                       ViiperNativeRuntimeMetadata.FileName)) ||
                File.Exists(Path.Combine(AppContext.BaseDirectory, "extras",
                    ViiperNativeRuntimeMetadata.FileName));
        }
    }

    internal sealed class ViiperNativePnpAnchor
    {
        internal ulong NativeDeviceId { get; init; }
        internal uint NativeDeviceGeneration { get; init; }
        internal ulong ControllerSessionId { get; init; }
        internal string PnpRootInstanceId => ControllerInstanceId;
        internal string PnpUsbDeviceInstanceId { get; init; }
        internal string ControllerInstanceId { get; init; }
        internal uint Usb20PortNumber { get; init; }
        internal uint Usb30PortNumber { get; init; }
        internal uint UdecxUsbPortNumber => Usb20PortNumber != 0 ?
            Usb20PortNumber : Usb30PortNumber;

        internal bool IsExact => NativeDeviceId != 0 &&
            NativeDeviceGeneration != 0 &&
            ControllerSessionId != 0 &&
            !string.IsNullOrWhiteSpace(ControllerInstanceId) &&
            (Usb20PortNumber != 0 ^ Usb30PortNumber != 0);
    }

    internal sealed class ViiperVirtualDeviceIdentity
    {
        internal ViiperTransportMode TransportMode { get; init; }
        internal uint BusId { get; init; }
        internal string DevId { get; init; }
        internal string DeviceType { get; init; }
        internal string Vid { get; init; }
        internal string Pid { get; init; }
        internal string DeviceSerialNumber { get; init; }
        internal string BrokerBuildIdentity { get; init; }
        internal string LogicalLifetimeId { get; init; }
        internal long StreamGeneration { get; init; }
        internal int LegacyUsbipPort { get; init; } = -1;
        internal string LegacyUsbipOwnerSerial { get; init; }
        internal ViiperNativePnpAnchor NativePnpAnchor { get; init; }

        internal ViiperVirtualDeviceIdentity WithStreamGeneration(
            long streamGeneration)
        {
            return new ViiperVirtualDeviceIdentity
            {
                TransportMode = TransportMode,
                BusId = BusId,
                DevId = DevId,
                DeviceType = DeviceType,
                Vid = Vid,
                Pid = Pid,
                DeviceSerialNumber = DeviceSerialNumber,
                BrokerBuildIdentity = BrokerBuildIdentity,
                LogicalLifetimeId = LogicalLifetimeId,
                StreamGeneration = streamGeneration,
                LegacyUsbipPort = LegacyUsbipPort,
                LegacyUsbipOwnerSerial = LegacyUsbipOwnerSerial,
                NativePnpAnchor = NativePnpAnchor,
            };
        }
    }
}
