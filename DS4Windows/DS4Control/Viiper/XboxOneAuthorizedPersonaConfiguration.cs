/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS4Windows
{
    /// <summary>
    /// Explicit deployment-owned identity for the VIIPER Xbox One persona.
    /// There is deliberately no Microsoft or project VID/PID fallback. Merely
    /// selecting Xbox One output cannot manufacture identity authorization.
    /// </summary>
    internal sealed class XboxOneAuthorizedPersonaConfiguration
    {
        internal const string PathEnvironmentVariable =
            "DS4W_VIIPER_XBOXONE_AUTHORIZED_PERSONA_PATH";
        internal const string PortableFileName =
            "xbox-one-authorized-persona.json";

        private const ulong PrimaryDeviceIdPrefix =
            0x0000fffb00000000UL;
        private const ulong PrimaryDeviceIdPrefixMask =
            0xffffffff00000000UL;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

        [JsonPropertyName("version")]
        public ushort Version { get; set; }

        [JsonPropertyName("identityAuthorizationGranted")]
        public bool IdentityAuthorizationGranted { get; set; }

        [JsonPropertyName("identity")]
        public XboxOneAuthorizedIdentity Identity { get; set; }

        [JsonPropertyName("usb")]
        public XboxOneAuthorizedUsbConfiguration Usb { get; set; }

        [JsonPropertyName("strings")]
        public XboxOneAuthorizedIdentityStrings Strings { get; set; }

        /// <summary>
        /// Explicit deployment permission to give each virtual registration a
        /// distinct USB serial. Omitted/false preserves the authorized serial
        /// exactly. VID/PID, GIP identity, firmware and other strings never vary.
        /// </summary>
        [JsonPropertyName("derivePerRegistrationSerial")]
        public bool DerivePerRegistrationSerial { get; set; }

        internal static XboxOneAuthorizedPersonaConfiguration LoadExplicit()
        {
            string configuredPath = Environment.GetEnvironmentVariable(
                PathEnvironmentVariable)?.Trim();
            string path = string.IsNullOrEmpty(configuredPath) ?
                Path.Combine(AppContext.BaseDirectory, PortableFileName) :
                Path.GetFullPath(configuredPath);
            if (!File.Exists(path))
            {
                throw new IOException(
                    "Xbox One output requires an explicitly authorized USB identity. " +
                    $"Place {PortableFileName} beside DS4Windows or set " +
                    $"{PathEnvironmentVariable} to an authorized identity bundle. " +
                    "DS4Windows does not impersonate a Microsoft VID/PID by default.");
            }

            try
            {
                return ParseExplicit(File.ReadAllText(path), path);
            }
            catch (Exception ex) when (ex is JsonException ||
                ex is NotSupportedException || ex is IOException ||
                ex is UnauthorizedAccessException)
            {
                throw new IOException(
                    $"The Xbox One authorized identity bundle at '{path}' could not be read: {ex.Message}",
                    ex);
            }

        }

        internal static XboxOneAuthorizedPersonaConfiguration ParseExplicit(
            string json, string source)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw Invalid(source ?? "<memory>", "JSON is required");
            }
            XboxOneAuthorizedPersonaConfiguration configuration =
                JsonSerializer.Deserialize<
                    XboxOneAuthorizedPersonaConfiguration>(json, JsonOptions);
            if (configuration == null)
            {
                throw Invalid(source ?? "<memory>",
                    "the root value must be an object");
            }
            configuration.Validate(source ?? "<memory>");
            return configuration;
        }

        private void Validate(string path)
        {
            if (Version != 1 || !IdentityAuthorizationGranted ||
                Identity == null || Usb == null || Strings == null)
            {
                throw Invalid(path,
                    "version 1, identityAuthorizationGranted=true, identity, usb, and strings are required");
            }
            if (Identity.VendorId == 0 || Identity.ProductId == 0)
            {
                throw Invalid(path, "vendorId and productId must be non-zero");
            }
            if ((Identity.DeviceId & PrimaryDeviceIdPrefixMask) !=
                PrimaryDeviceIdPrefix)
            {
                throw Invalid(path,
                    "deviceId is not a primary GIP controller Device ID");
            }
            if (!IsPackedBcd(Identity.DeviceReleaseBcd))
            {
                throw Invalid(path, "deviceReleaseBcd is not packed BCD");
            }
            if ((Identity.FirmwareMajor | Identity.FirmwareMinor |
                    Identity.FirmwareBuild | Identity.FirmwareRevision) == 0)
            {
                throw Invalid(path, "the firmware version cannot be all zero");
            }
            if (Usb.MaxPower2mA is 0 or > 250 ||
                Usb.OutIntervalMs is < 4 or > 255 ||
                Usb.InIntervalMs is < 4 or > 255)
            {
                throw Invalid(path,
                    "USB power must be 1..250 units and endpoint intervals must be 4..255 ms");
            }
            if (string.IsNullOrWhiteSpace(Strings.Manufacturer) ||
                string.IsNullOrWhiteSpace(Strings.Product) ||
                Strings.Serial == null || Strings.Serial.Length != 32)
            {
                throw Invalid(path,
                    "manufacturer, product, and an exact 32-hex-digit serial are required");
            }
            foreach (char value in Strings.Serial)
            {
                if (!Uri.IsHexDigit(value))
                {
                    throw Invalid(path,
                        "serial must contain exactly 32 hexadecimal digits");
                }
            }
            if (Strings.Serial.IndexOf(Identity.DeviceId.ToString("x16"),
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw Invalid(path,
                    "serial must contain the exact primary Device ID");
            }
        }

        private static bool IsPackedBcd(ushort value)
        {
            for (int shift = 0; shift < 16; shift += 4)
            {
                if (((value >> shift) & 0x0f) > 9)
                {
                    return false;
                }
            }
            return true;
        }

        private static IOException Invalid(string path, string detail) =>
            new($"The Xbox One authorized identity bundle at '{path}' is invalid: {detail}.");
    }

    internal sealed class XboxOneAuthorizedIdentity
    {
        [JsonPropertyName("vendorId")]
        public ushort VendorId { get; set; }

        [JsonPropertyName("productId")]
        public ushort ProductId { get; set; }

        [JsonPropertyName("deviceReleaseBcd")]
        public ushort DeviceReleaseBcd { get; set; }

        [JsonPropertyName("deviceId")]
        public ulong DeviceId { get; set; }

        [JsonPropertyName("firmwareMajor")]
        public ushort FirmwareMajor { get; set; }

        [JsonPropertyName("firmwareMinor")]
        public ushort FirmwareMinor { get; set; }

        [JsonPropertyName("firmwareBuild")]
        public ushort FirmwareBuild { get; set; }

        [JsonPropertyName("firmwareRevision")]
        public ushort FirmwareRevision { get; set; }

        [JsonPropertyName("hardwareMajor")]
        public byte HardwareMajor { get; set; }

        [JsonPropertyName("hardwareMinor")]
        public byte HardwareMinor { get; set; }
    }

    internal sealed class XboxOneAuthorizedUsbConfiguration
    {
        [JsonPropertyName("maxPower2mA")]
        public ushort MaxPower2mA { get; set; }

        [JsonPropertyName("outIntervalMs")]
        public ushort OutIntervalMs { get; set; }

        [JsonPropertyName("inIntervalMs")]
        public ushort InIntervalMs { get; set; }
    }

    internal sealed class XboxOneAuthorizedIdentityStrings
    {
        [JsonPropertyName("manufacturer")]
        public string Manufacturer { get; set; }

        [JsonPropertyName("product")]
        public string Product { get; set; }

        [JsonPropertyName("serial")]
        public string Serial { get; set; }
    }

    /// <summary>
    /// Exact API v1 request. Lifecycle identifiers are generated on the cold
    /// connect path and are never mutable input-hot-path configuration.
    /// </summary>
    internal sealed class XboxOneAuthorizedCreateRequestV1
    {
        private static readonly XboxOneRetainedImportIdentitySource ImportIdentities =
            new(NextNonZeroUInt64());

        [JsonPropertyName("version")]
        public ushort Version { get; set; }

        [JsonPropertyName("identityAuthorizationGranted")]
        public bool IdentityAuthorizationGranted { get; set; }

        [JsonPropertyName("identity")]
        public XboxOneAuthorizedIdentity Identity { get; set; }

        [JsonPropertyName("usb")]
        public XboxOneAuthorizedUsbConfiguration Usb { get; set; }

        [JsonPropertyName("strings")]
        public XboxOneAuthorizedIdentityStrings Strings { get; set; }

        [JsonPropertyName("feedback")]
        public XboxOneAuthorizedFeedbackBinding Feedback { get; set; }

        [JsonPropertyName("importDeviceId")]
        public ulong ImportDeviceId { get; set; }

        [JsonPropertyName("localTimeoutMilliseconds")]
        public uint LocalTimeoutMilliseconds { get; set; }

        internal static XboxOneAuthorizedCreateRequestV1 Create(
            XboxOneAuthorizedPersonaConfiguration configuration)
            => CreateForFeedbackTarget(configuration, NextNonZeroUInt64(),
                NextNonZeroUInt64());

        /// <summary>
        /// Creates one persona request whose CFBK target generations are the
        /// exact physical controller lifetime selected before stream open.
        /// The persona generation is owned by VIIPER's canonical Xbox persona
        /// and begins at generation one. Ownership remains fresh per virtual-
        /// device lifetime; callers may not mutate the binding after creation.
        /// </summary>
        internal static XboxOneAuthorizedCreateRequestV1
            CreateForFeedbackTarget(
                XboxOneAuthorizedPersonaConfiguration configuration,
                ulong deviceGeneration, ulong transportGeneration)
            => CreateForFeedbackTarget(configuration, deviceGeneration,
                transportGeneration, NextNonZeroUInt64());

        internal static XboxOneAuthorizedCreateRequestV1
            CreateForFeedbackTarget(
                XboxOneAuthorizedPersonaConfiguration configuration,
                ulong deviceGeneration, ulong transportGeneration,
                ulong ownershipEpoch)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            if (deviceGeneration == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deviceGeneration));
            }
            if (transportGeneration == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transportGeneration));
            }
            if (ownershipEpoch == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ownershipEpoch));
            }
            // This is VIIPER's server-wide retained-session key, not the GIP
            // device identity and not an authorization capability. Two outputs
            // using one authorized deployment bundle must not claim one import.
            ulong importDeviceId = ImportIdentities.Next();
            XboxOneAuthorizedIdentityStrings strings = configuration.Strings;
            if (configuration.DerivePerRegistrationSerial)
            {
                strings = new XboxOneAuthorizedIdentityStrings
                {
                    Manufacturer = configuration.Strings.Manufacturer,
                    Product = configuration.Strings.Product,
                    Serial = configuration.Identity.DeviceId.ToString("x16",
                        CultureInfo.InvariantCulture) + importDeviceId.ToString(
                        "x16", CultureInfo.InvariantCulture),
                };
            }
            return new XboxOneAuthorizedCreateRequestV1
            {
                Version = 1,
                IdentityAuthorizationGranted = true,
                Identity = configuration.Identity,
                Usb = configuration.Usb,
                Strings = strings,
                Feedback = new XboxOneAuthorizedFeedbackBinding
                {
                    Source = (byte)ControllerFeedbackSource.XboxOneVirtualDevice,
                    // VIIPER constructs a new canonical persona in generation
                    // one. This is a local lifecycle generation, not a random
                    // cross-process nonce; OwnershipEpoch provides the fresh
                    // per-lease identity for the feedback binding.
                    PersonaGeneration = 1,
                    DeviceGeneration = deviceGeneration,
                    TransportGeneration = transportGeneration,
                    OwnershipEpoch = ownershipEpoch,
                    TimeToLiveMicroseconds =
                        ControllerFeedbackFrame.MaxTimeToLiveMicroseconds,
                },
                ImportDeviceId = importDeviceId,
                LocalTimeoutMilliseconds = 100,
            };
        }

        private static ulong NextNonZeroUInt64()
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            ulong value;
            do
            {
                RandomNumberGenerator.Fill(bytes);
                value = BitConverter.ToUInt64(bytes);
            }
            while (value == 0);
            return value;
        }
    }

    /// <summary>
    /// Cold-path, process-unique import identities with a fresh cryptographic
    /// starting point per process. No wrap/reuse is permitted. Identity is not
    /// authority: VIIPER still checks the exact registration, owner and leases.
    /// </summary>
    internal sealed class XboxOneRetainedImportIdentitySource
    {
        private readonly object gate = new();
        private ulong next;
        private bool exhausted;

        internal XboxOneRetainedImportIdentitySource(ulong first)
        {
            if (first == 0)
                throw new ArgumentOutOfRangeException(nameof(first));
            next = first;
        }

        internal ulong Next()
        {
            lock (gate)
            {
                if (exhausted)
                    throw new IOException("Xbox One retained import identity space is exhausted.");
                ulong result = next;
                if (next == ulong.MaxValue)
                    exhausted = true;
                else
                    next++;
                return result;
            }
        }
    }

    internal sealed class XboxOneAuthorizedFeedbackBinding
    {
        [JsonPropertyName("source")]
        public byte Source { get; set; }

        [JsonPropertyName("personaGeneration")]
        public ulong PersonaGeneration { get; set; }

        [JsonPropertyName("deviceGeneration")]
        public ulong DeviceGeneration { get; set; }

        [JsonPropertyName("transportGeneration")]
        public ulong TransportGeneration { get; set; }

        [JsonPropertyName("ownershipEpoch")]
        public ulong OwnershipEpoch { get; set; }

        [JsonPropertyName("timeToLiveMicroseconds")]
        public ulong TimeToLiveMicroseconds { get; set; }
    }
}
