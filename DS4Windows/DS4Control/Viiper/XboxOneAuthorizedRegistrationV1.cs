using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DS4Windows
{
    /// <summary>
    /// Cold-path capability for one authenticated Xbox factory registration.
    /// Numeric addresses locate candidates; only the server can authenticate
    /// this capability against the exact registration. Never log/persist it or
    /// fall back to address-only removal when a response cannot be verified.
    /// </summary>
    internal sealed class XboxOneAuthorizedRegistrationV1
    {
        private readonly string removalToken;

        private XboxOneAuthorizedRegistrationV1(uint busId, string devId,
            string removalToken, string usbipBusId, int removalTimeoutMilliseconds)
        {
            BusId = busId;
            DevId = devId;
            this.removalToken = removalToken;
            UsbipBusId = usbipBusId;
            RemovalTimeoutMilliseconds = removalTimeoutMilliseconds;
        }

        internal uint BusId { get; }
        internal string DevId { get; }
        internal string UsbipBusId { get; }
        internal int RemovalTimeoutMilliseconds { get; }
        internal int RemovalResponseTimeoutMilliseconds =>
            RemovalTimeoutMilliseconds + 2000;
        internal string StreamPath =>
            $"bus/{BusId}/{DevId}/stream-authorized-xboxone";
        internal string ActivationPath =>
            $"bus/{BusId}/{DevId}/activate-authorized-xboxone";
        internal string RemovalPath =>
            $"bus/{BusId}/{DevId}/remove-authorized-xboxone";

        internal string SerializeRemovalRequest() => JsonSerializer.Serialize(
            new { version = 1, removalToken });

        internal static uint ParseBusCreateResponse(JsonElement response)
        {
            RequireObject(response);
            uint busId = 0;
            foreach (JsonProperty property in response.EnumerateObject())
            {
                if (property.Name != "busId" || busId != 0 ||
                    property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetUInt32(out busId) ||
                    busId == 0 || busId > ushort.MaxValue)
                    throw InvalidResponse();
            }
            if (busId == 0)
                throw InvalidResponse();
            return busId;
        }

        internal static XboxOneAuthorizedRegistrationV1 ParseCreateResponse(
            JsonElement response, uint expectedBusId,
            ushort expectedVendorId, ushort expectedProductId)
        {
            RequireObject(response);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            uint busId = 0;
            string devId = null;
            string token = null;
            string usbipBusId = null;
            int removalTimeoutMilliseconds = 0;
            bool typeMatches = false;
            bool vendorMatches = false;
            bool productMatches = false;
            bool hasMetadata = false;
            foreach (JsonProperty property in response.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw InvalidResponse();
                JsonElement value = property.Value;
                switch (property.Name)
                {
                    case "busId":
                        if (value.ValueKind != JsonValueKind.Number ||
                            !value.TryGetUInt32(out busId))
                            throw InvalidResponse();
                        break;
                    case "devId":
                        devId = ReadString(value);
                        if (!ushort.TryParse(devId, NumberStyles.None,
                            CultureInfo.InvariantCulture, out ushort deviceId) ||
                            deviceId == 0 || !string.Equals(devId,
                                deviceId.ToString(CultureInfo.InvariantCulture),
                                StringComparison.Ordinal))
                            throw InvalidResponse();
                        break;
                    case "type":
                        typeMatches = ReadString(value) == "xboxone";
                        break;
                    case "vid":
                        vendorMatches = ReadString(value) ==
                            "0x" + expectedVendorId.ToString("x4",
                                CultureInfo.InvariantCulture);
                        break;
                    case "pid":
                        productMatches = ReadString(value) ==
                            "0x" + expectedProductId.ToString("x4",
                                CultureInfo.InvariantCulture);
                        break;
                    case "deviceSpecific":
                        hasMetadata = value.ValueKind == JsonValueKind.Object;
                        break;
                    case "usbipPort":
                        // The Go create DTO omits these fields when dormant.
                        if (value.ValueKind != JsonValueKind.Number ||
                            !value.TryGetInt32(out int port) || port != 0)
                            throw InvalidResponse();
                        break;
                    case "usbipOwnerSerial":
                        if (ReadString(value).Length != 0)
                            throw InvalidResponse();
                        break;
                    case "removalToken":
                        token = ReadString(value);
                        if (!IsCanonicalToken(token))
                            throw InvalidResponse();
                        break;
                    case "usbipBusId":
                        usbipBusId = ReadString(value);
                        if (!IsCanonicalUsbipBusId(usbipBusId))
                            throw InvalidResponse();
                        break;
                    case "removalTimeoutMilliseconds":
                        if (value.ValueKind != JsonValueKind.Number ||
                            !value.TryGetInt32(out removalTimeoutMilliseconds) ||
                            removalTimeoutMilliseconds < 1 ||
                            removalTimeoutMilliseconds > 300000)
                            throw InvalidResponse();
                        break;
                    default:
                        throw InvalidResponse();
                }
            }

            if (expectedBusId == 0 || expectedBusId > ushort.MaxValue ||
                busId != expectedBusId || devId == null || token == null ||
                usbipBusId == null || removalTimeoutMilliseconds == 0 ||
                !typeMatches || !vendorMatches || !productMatches || !hasMetadata)
                throw InvalidResponse();

            return new XboxOneAuthorizedRegistrationV1(busId, devId, token,
                usbipBusId, removalTimeoutMilliseconds);
        }

        internal int ParseActivationResponse(JsonElement response)
        {
            RequireObject(response);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool versionMatches = false;
            bool aliasMatches = false;
            int port = 0;
            foreach (JsonProperty property in response.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw InvalidResponse();
                switch (property.Name)
                {
                    case "version":
                        versionMatches = property.Value.ValueKind == JsonValueKind.Number &&
                            property.Value.TryGetUInt16(out ushort version) && version == 1;
                        break;
                    case "usbipBusId":
                        aliasMatches = ReadString(property.Value) == UsbipBusId;
                        break;
                    case "usbipPort":
                        if (property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out port) || port <= 0)
                            throw InvalidResponse();
                        break;
                    case "usbipOwnerSerial":
                        string serial = ReadString(property.Value);
                        if (serial.Length != 0 &&
                            !ViiperUsbipPortManager.IsDs4WindowsOwnershipSerial(serial))
                            throw InvalidResponse();
                        break;
                    default:
                        throw InvalidResponse();
                }
            }
            if (!versionMatches || !aliasMatches || port <= 0)
                throw InvalidResponse();
            return port;
        }

        internal static bool IsCanonicalUsbipBusId(string value)
        {
            if (value == null || value.Length != 29 ||
                !value.StartsWith("x1-", StringComparison.Ordinal))
                return false;
            for (int index = 3; index < value.Length; index++)
                if (!((value[index] >= 'a' && value[index] <= 'z') ||
                    (value[index] >= '2' && value[index] <= '7')))
                    return false;
            // Sixteen bytes contain 128 bits; the final base32 character has
            // two zero padding bits. Noncanonical aliases must not be folded.
            return "aeimquy4".IndexOf(value[^1]) >= 0;
        }

        internal static bool ParseRemovalResponse(JsonElement response)
        {
            RequireObject(response);
            bool hasVersion = false;
            bool hasRemoved = false;
            bool removed = false;
            foreach (JsonProperty property in response.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "version" when !hasVersion:
                        if (property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetUInt16(out ushort version) ||
                            version != 1)
                            throw InvalidResponse();
                        hasVersion = true;
                        break;
                    case "removed" when !hasRemoved:
                        if (property.Value.ValueKind != JsonValueKind.True &&
                            property.Value.ValueKind != JsonValueKind.False)
                            throw InvalidResponse();
                        removed = property.Value.GetBoolean();
                        hasRemoved = true;
                        break;
                    default:
                        throw InvalidResponse();
                }
            }
            if (!hasVersion || !hasRemoved)
                throw InvalidResponse();
            // false is only "this request removed nothing", not proof that
            // any local USB/IP port is gone or that another owner is neutral.
            return removed;
        }

        private static bool IsCanonicalToken(string token)
        {
            if (token.Length != 64)
                return false;
            foreach (char value in token)
                if (!((value >= '0' && value <= '9') ||
                    (value >= 'a' && value <= 'f')))
                    return false;
            return true;
        }

        private static string ReadString(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String)
                throw InvalidResponse();
            return value.GetString();
        }

        private static void RequireObject(JsonElement response)
        {
            if (response.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();
        }

        // No response fragments/property values: capabilities must not leak
        // through ordinary exception logging or user-visible error messages.
        private static IOException InvalidResponse() => new IOException(
            "VIIPER returned an invalid Xbox One registration capability response.");
    }
}
