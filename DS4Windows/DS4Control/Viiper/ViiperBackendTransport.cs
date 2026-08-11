/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DS4Windows
{
    internal enum ViiperBackendMode
    {
        LegacyUsbip,
        NativeUde,
    }

    /// <summary>
    /// Identifies one concrete Windows service process. VIIPER reuses bus and
    /// device numbers from 1 after a broker restart, so those API identifiers
    /// alone cannot distinguish a retained reconnect-grace child from a new
    /// child created by another recovering output.
    /// </summary>
    internal readonly struct ViiperNativeBrokerInstance :
        IEquatable<ViiperNativeBrokerInstance>
    {
        internal ViiperNativeBrokerInstance(uint processId,
            long processCreationFileTime)
        {
            if (processId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(processId));
            }
            if (processCreationFileTime <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(processCreationFileTime));
            }

            ProcessId = processId;
            ProcessCreationFileTime = processCreationFileTime;
        }

        internal uint ProcessId { get; }

        internal long ProcessCreationFileTime { get; }

        public bool Equals(ViiperNativeBrokerInstance other) =>
            ProcessId == other.ProcessId &&
            ProcessCreationFileTime == other.ProcessCreationFileTime;

        public override bool Equals(object obj) =>
            obj is ViiperNativeBrokerInstance other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(ProcessId, ProcessCreationFileTime);

        public static bool operator ==(ViiperNativeBrokerInstance left,
            ViiperNativeBrokerInstance right) => left.Equals(right);

        public static bool operator !=(ViiperNativeBrokerInstance left,
            ViiperNativeBrokerInstance right) => !left.Equals(right);
    }

    /// <summary>
    /// The live API transport selected by a successful ping. The credential is
    /// connection state rather than virtual-device identity and is therefore
    /// deliberately kept out of ViiperVirtualDeviceIdentity.
    /// </summary>
    internal sealed class ViiperBackendSelection
    {
        internal ViiperBackendSelection(ViiperBackendMode mode,
            string credential = null,
            ViiperNativeBrokerInstance? nativeBrokerInstance = null)
        {
            Mode = mode;
            Credential = string.IsNullOrWhiteSpace(credential)
                ? null
                : credential;
            NativeBrokerInstance = nativeBrokerInstance;
        }

        internal ViiperBackendMode Mode { get; }

        internal bool IsNative => Mode == ViiperBackendMode.NativeUde;

        internal bool UsesAuthentication => Credential != null;

        internal ViiperNativeBrokerInstance? NativeBrokerInstance { get; }

        // Never include this value in diagnostics or ToString output.
        internal string Credential { get; }
    }

    internal sealed class ViiperNativeContractException : IOException
    {
        internal ViiperNativeContractException(string message)
            : base(message)
        {
        }

        internal ViiperNativeContractException(string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Pins the native opt-in to the negotiated contract exposed by the exact
    /// accepted VIIPER package. The build identity is returned by the loaded
    /// kernel image, so a matching broker or on-disk package cannot disguise a
    /// stale driver with the same ABI and capabilities. An unrecognised or
    /// incomplete native ping is an error, never a reason to fall back to
    /// USB/IP.
    /// </summary>
    internal static class ViiperBackendContract
    {
        internal const string NativeTransport = "native-ude";
        internal const string LegacyTransport = "usbip";
        internal const string NativeServerVersion = "0.1.0";
        internal const ushort NativeAbiMajor = 1;
        internal const ushort NativeAbiMinor = 9;
        internal const uint NativeCapabilities = 0x0d;
        internal const string NativeDriverPackageVersion = "0.1.0.4";
        internal const string NativeLoadedDriverBuildIdentity =
            "114c1e4232004a328cf0e6e376c35e68ed7f314b61611084d35e6a7475a8f7c4";
        internal const uint NativeMaxDevices = 32;
        internal const uint NativeMaxDescriptorBytes = 262144;
        internal const uint NativeMaxTransferBytes = 1048576;
        internal const uint NativeMaxIsoPackets = 1024;
        internal const uint NativeMaxPendingOperations = 4096;

        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

        internal static ViiperBackendSelection ParsePing(string raw,
            string credential = null)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new IOException("VIIPER returned an empty ping response.");
            }

            ViiperPingResponse ping;
            try
            {
                ping = JsonSerializer.Deserialize<ViiperPingResponse>(raw,
                    JsonOptions);
            }
            catch (JsonException ex)
            {
                if (HasExplicitNativeTransport(raw))
                {
                    throw new ViiperNativeContractException(
                        "VIIPER returned malformed native UDE health metadata.",
                        ex);
                }

                throw new IOException(
                    "VIIPER returned an invalid ping response.", ex);
            }

            if (ping == null ||
                !string.Equals(ping.Server, "VIIPER",
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(ping.Version))
            {
                throw new IOException(
                    "The process on the VIIPER API port did not prove the VIIPER server identity.");
            }

            string transport = ping.Transport?.Trim();
            if (string.IsNullOrEmpty(transport))
            {
                // Historical VIIPER releases expose only server + version.
                // Native metadata without an explicit transport is not a
                // historical response and must not be accepted as USB/IP.
                if (ping.Ready == false || ping.NativeUde != null)
                {
                    throw new ViiperNativeContractException(
                        "VIIPER returned contradictory legacy transport health.");
                }

                return new ViiperBackendSelection(
                    ViiperBackendMode.LegacyUsbip, credential);
            }

            if (string.Equals(transport, LegacyTransport,
                    StringComparison.Ordinal))
            {
                if (ping.Ready == false || ping.NativeUde != null)
                {
                    throw new IOException(
                        "VIIPER reported an unhealthy or contradictory USB/IP transport.");
                }

                return new ViiperBackendSelection(
                    ViiperBackendMode.LegacyUsbip, credential);
            }

            if (!string.Equals(transport, NativeTransport,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"VIIPER reported unsupported transport '{transport}'.");
            }

            ViiperNativeUdeInfo native = ping.NativeUde;
            bool exactIdentityProperty =
                TryReadSingleLoadedDriverBuildIdentity(raw,
                    out string loadedDriverBuildIdentity);
            if (!string.Equals(ping.Version, NativeServerVersion,
                    StringComparison.Ordinal) ||
                ping.Ready != true || native == null ||
                native.AbiMajor != NativeAbiMajor ||
                native.AbiMinor != NativeAbiMinor ||
                native.Capabilities != NativeCapabilities ||
                !string.Equals(native.ExpectedDriverPackageVersion,
                    NativeDriverPackageVersion, StringComparison.Ordinal) ||
                !exactIdentityProperty ||
                !string.Equals(native.LoadedDriverBuildIdentity,
                    loadedDriverBuildIdentity, StringComparison.Ordinal) ||
                !MatchesLoadedDriverBuildIdentity(
                    loadedDriverBuildIdentity) ||
                native.MaxDevices != NativeMaxDevices ||
                native.MaxDescriptorBytes != NativeMaxDescriptorBytes ||
                native.MaxTransferBytes != NativeMaxTransferBytes ||
                native.MaxIsoPackets != NativeMaxIsoPackets ||
                native.MaxPendingOperations !=
                    NativeMaxPendingOperations)
            {
                throw new ViiperNativeContractException(
                    "VIIPER native UDE health proof does not match the exact required ABI 1.9 loaded-driver contract.");
            }

            if (string.IsNullOrWhiteSpace(credential))
            {
                throw new ViiperNativeContractException(
                    "VIIPER native UDE health must be authenticated.");
            }

            return new ViiperBackendSelection(ViiperBackendMode.NativeUde,
                credential);
        }

        private static bool MatchesLoadedDriverBuildIdentity(string actual)
        {
            if (!IsCanonicalLowerHexSha256(actual) ||
                !IsCanonicalLowerHexSha256(
                    NativeLoadedDriverBuildIdentity))
            {
                return false;
            }

            byte[] actualBytes = Encoding.ASCII.GetBytes(actual);
            byte[] expectedBytes = Encoding.ASCII.GetBytes(
                NativeLoadedDriverBuildIdentity);
            return CryptographicOperations.FixedTimeEquals(actualBytes,
                expectedBytes);
        }

        internal static bool IsCanonicalLowerHexSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            foreach (char character in value)
            {
                if ((character < '0' || character > '9') &&
                    (character < 'a' || character > 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasExplicitNativeTransport(string raw)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                return document.RootElement.ValueKind ==
                        JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("transport",
                        out JsonElement transport) &&
                    transport.ValueKind == JsonValueKind.String &&
                    string.Equals(transport.GetString(), NativeTransport,
                        StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadSingleLoadedDriverBuildIdentity(
            string raw, out string identity)
        {
            identity = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                if (document.RootElement.ValueKind !=
                    JsonValueKind.Object)
                {
                    return false;
                }

                JsonElement nativeUde = default;
                int nativeUdeCount = 0;
                foreach (JsonProperty property in
                    document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "nativeUde",
                        StringComparison.Ordinal))
                    {
                        nativeUde = property.Value;
                        nativeUdeCount++;
                    }
                    else if (string.Equals(property.Name, "nativeUde",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                if (nativeUdeCount != 1 || nativeUde.ValueKind !=
                    JsonValueKind.Object)
                {
                    return false;
                }

                int identityCount = 0;
                foreach (JsonProperty property in nativeUde.EnumerateObject())
                {
                    if (string.Equals(property.Name,
                        "loadedDriverBuildIdentity",
                        StringComparison.Ordinal))
                    {
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return false;
                        }

                        identity = property.Value.GetString();
                        identityCount++;
                    }
                    else if (string.Equals(property.Name,
                        "loadedDriverBuildIdentity",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return identityCount == 1;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private sealed class ViiperPingResponse
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
            public ViiperNativeUdeInfo NativeUde { get; set; }
        }

        private sealed class ViiperNativeUdeInfo
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

            [JsonPropertyName("maxDevices")]
            public uint MaxDevices { get; set; }

            [JsonPropertyName("maxDescriptorBytes")]
            public uint MaxDescriptorBytes { get; set; }

            [JsonPropertyName("maxTransferBytes")]
            public uint MaxTransferBytes { get; set; }

            [JsonPropertyName("maxIsoPackets")]
            public uint MaxIsoPackets { get; set; }

            [JsonPropertyName("maxPendingOperations")]
            public uint MaxPendingOperations { get; set; }
        }
    }

    internal static class ViiperBackendPolicy
    {
        internal static bool UsesUsbip(ViiperBackendMode mode) =>
            mode == ViiperBackendMode.LegacyUsbip;

        internal static void RunUsbipOnly(ViiperBackendMode mode,
            Action action)
        {
            if (UsesUsbip(mode))
            {
                action?.Invoke();
            }
        }
    }

    internal static class ViiperStartupDiscovery
    {
        /// <summary>
        /// Backend preparation must precede HID enumeration so a stale
        /// DS4Windows-owned USB/IP controller cannot be ingested as physical
        /// input. Discovery itself is unconditional: an unavailable native
        /// broker is a service-start failure, not a reason to hide attached
        /// physical controllers from the next recovery attempt.
        /// </summary>
        internal static bool TryPrepareBackendAndDiscover(
            Func<ViiperBackendSelection> probeBackend,
            Action prepareLegacyUsbip, Action discoverControllers,
            Action<Exception> reportFailure = null)
        {
            ArgumentNullException.ThrowIfNull(probeBackend);
            ArgumentNullException.ThrowIfNull(discoverControllers);

            bool backendReady = false;
            try
            {
                ViiperBackendSelection backend = probeBackend();
                if (backend == null)
                {
                    throw new IOException(
                        "VIIPER returned no backend selection.");
                }

                ViiperBackendPolicy.RunUsbipOnly(backend.Mode,
                    prepareLegacyUsbip);
                backendReady = true;
            }
            catch (Exception ex)
            {
                reportFailure?.Invoke(ex);
            }
            finally
            {
                discoverControllers();
            }

            return backendReady;
        }
    }

    internal static class ViiperCredentialProvider
    {
        private const string CredentialFileName = "viiper.key.txt";

        internal static IReadOnlyList<string> ReadCandidateCredentials()
        {
            var credentials = new List<string>();
            var visited = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            AddCredential(Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
                credentials, visited);
            AddCredential(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), credentials,
                visited);
            return credentials;
        }

        private static void AddCredential(string root,
            ICollection<string> credentials, ISet<string> visited)
        {
            if (string.IsNullOrWhiteSpace(root) ||
                !Path.IsPathFullyQualified(root))
            {
                return;
            }

            string path;
            try
            {
                path = Path.GetFullPath(Path.Combine(root, "VIIPER",
                    CredentialFileName));
            }
            catch
            {
                return;
            }

            if (!visited.Add(path))
            {
                return;
            }

            try
            {
                string credential = File.ReadAllText(path,
                    Encoding.UTF8).Trim();
                if (!string.IsNullOrEmpty(credential))
                {
                    credentials.Add(credential);
                }
            }
            catch
            {
                // Absence and access denial are handled by the caller as a
                // failed authenticated probe; never weaken a native response.
            }
        }
    }

    internal static class ViiperAuthentication
    {
        private const int NonceSize = 32;
        private const int Pbkdf2Iterations = 100000;
        private static readonly byte[] HandshakeMagic =
            Encoding.ASCII.GetBytes("eVI1\0");
        private static readonly byte[] AuthContext =
            Encoding.ASCII.GetBytes("VIIPER-Auth-v1");
        private static readonly byte[] Pbkdf2Salt =
            Encoding.ASCII.GetBytes("VIIPER-Key-v1");
        private static readonly byte[] SessionContext =
            Encoding.ASCII.GetBytes("VIIPER-Session-v1");

        internal static Stream Authenticate(Stream stream,
            string password)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (string.IsNullOrEmpty(password))
            {
                throw new IOException(
                    "VIIPER authentication requires a credential.");
            }

            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] key;
            try
            {
                key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, Pbkdf2Salt,
                    Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            byte[] clientNonce = Array.Empty<byte>();
            byte[] clientProof = Array.Empty<byte>();
            byte[] handshake = Array.Empty<byte>();
            try
            {
                clientNonce = RandomNumberGenerator.GetBytes(NonceSize);
                using (var hmac = new HMACSHA256(key))
                {
                    byte[] proofInput = new byte[AuthContext.Length +
                        clientNonce.Length];
                    try
                    {
                        Buffer.BlockCopy(AuthContext, 0, proofInput, 0,
                            AuthContext.Length);
                        Buffer.BlockCopy(clientNonce, 0, proofInput,
                            AuthContext.Length, clientNonce.Length);
                        clientProof = hmac.ComputeHash(proofInput);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(proofInput);
                    }
                }

                handshake = new byte[HandshakeMagic.Length +
                    clientNonce.Length + clientProof.Length];
                Buffer.BlockCopy(HandshakeMagic, 0, handshake, 0,
                    HandshakeMagic.Length);
                Buffer.BlockCopy(clientNonce, 0, handshake,
                    HandshakeMagic.Length, clientNonce.Length);
                Buffer.BlockCopy(clientProof, 0, handshake,
                    HandshakeMagic.Length + clientNonce.Length,
                    clientProof.Length);

                stream.Write(handshake, 0, handshake.Length);
                byte[] prefix = new byte[3];
                ReadExactly(stream, prefix, 0, prefix.Length);
                if (prefix[0] != (byte)'O' || prefix[1] != (byte)'K' ||
                    prefix[2] != 0)
                {
                    throw new IOException(
                        "VIIPER rejected the API credential.");
                }

                byte[] serverNonce = new byte[NonceSize];
                byte[] sessionInput = Array.Empty<byte>();
                try
                {
                    ReadExactly(stream, serverNonce, 0,
                        serverNonce.Length);
                    sessionInput = new byte[key.Length +
                        serverNonce.Length + clientNonce.Length +
                        SessionContext.Length];
                    int offset = 0;
                    Buffer.BlockCopy(key, 0, sessionInput, offset,
                        key.Length);
                    offset += key.Length;
                    Buffer.BlockCopy(serverNonce, 0, sessionInput, offset,
                        serverNonce.Length);
                    offset += serverNonce.Length;
                    Buffer.BlockCopy(clientNonce, 0, sessionInput, offset,
                        clientNonce.Length);
                    offset += clientNonce.Length;
                    Buffer.BlockCopy(SessionContext, 0, sessionInput,
                        offset, SessionContext.Length);
                    byte[] sessionKey = SHA256.HashData(sessionInput);
                    try
                    {
                        return new ViiperAuthenticatedStream(stream,
                            sessionKey);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(sessionKey);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sessionInput);
                    CryptographicOperations.ZeroMemory(serverNonce);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(clientNonce);
                CryptographicOperations.ZeroMemory(clientProof);
                CryptographicOperations.ZeroMemory(handshake);
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer,
            int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total,
                    count - total);
                if (read <= 0)
                {
                    throw new IOException(
                        "VIIPER authentication handshake closed early.");
                }
                total += read;
            }
        }
    }

    /// <summary>
    /// Matches VIIPER's authenticated connection framing: a four-byte
    /// big-endian packet length followed by a 12-byte nonce and ChaCha20-
    /// Poly1305 ciphertext/tag. Read and write counters are independent.
    /// </summary>
    internal sealed class ViiperAuthenticatedStream : Stream
    {
        private const int NonceLength = 12;
        private const int TagLength = 16;
        private const int MaximumPacketLength = 2 * 1024 * 1024;
        private readonly Stream inner;
        private readonly ChaCha20Poly1305 sendCipher;
        private readonly ChaCha20Poly1305 receiveCipher;
        private readonly object writeLock = new object();
        private readonly object readLock = new object();
        private byte[] sendPacket = Array.Empty<byte>();
        private readonly byte[] receiveHeader = new byte[sizeof(uint)];
        private byte[] receivePacket = Array.Empty<byte>();
        private byte[] receivePlaintext = Array.Empty<byte>();
        private int receiveLength;
        private int receiveOffset;
        private ulong sendCounter;
        private bool sendCounterExhausted;
        private Exception sendFault;
        private int disposeState;

        private bool IsDisposed => Volatile.Read(ref disposeState) != 0;

        internal ViiperAuthenticatedStream(Stream inner, byte[] sessionKey)
        {
            this.inner = inner ?? throw new ArgumentNullException(
                nameof(inner));
            ArgumentNullException.ThrowIfNull(sessionKey);
            // State and media writes can run concurrently with the feedback
            // reader. Separate instances avoid relying on undocumented
            // concurrent-use guarantees in the platform AEAD implementation.
            sendCipher = new ChaCha20Poly1305(sessionKey);
            try
            {
                receiveCipher = new ChaCha20Poly1305(sessionKey);
            }
            catch
            {
                sendCipher.Dispose();
                throw;
            }
        }

        public override bool CanRead => !IsDisposed && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => !IsDisposed && inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            lock (writeLock)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                ThrowIfWriteFaulted();
                inner.Flush();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException();
            }
            if (count == 0)
            {
                return 0;
            }

            lock (readLock)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                while (receiveOffset >= receiveLength)
                {
                    if (!ReadPacket())
                    {
                        return 0;
                    }
                }

                int available = receiveLength - receiveOffset;
                int copied = Math.Min(count, available);
                Buffer.BlockCopy(receivePlaintext, receiveOffset, buffer,
                    offset, copied);
                receiveOffset += copied;
                if (receiveOffset >= receiveLength)
                {
                    CryptographicOperations.ZeroMemory(
                        receivePlaintext.AsSpan(0, receiveLength));
                    receiveLength = 0;
                    receiveOffset = 0;
                }
                return copied;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException();
            }

            lock (writeLock)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                ThrowIfWriteFaulted();
                if (sendCounterExhausted)
                {
                    throw new IOException(
                        "VIIPER encrypted stream nonce space is exhausted.");
                }
                if (count > MaximumPacketLength - NonceLength - TagLength)
                {
                    throw new IOException(
                        "VIIPER encrypted packet is too large.");
                }

                int packetLength = NonceLength + count + TagLength;
                int recordLength = sizeof(uint) + packetLength;
                EnsureCapacity(ref sendPacket, recordLength);

                Span<byte> record = sendPacket.AsSpan(0, recordLength);
                BinaryPrimitives.WriteUInt32BigEndian(record,
                    (uint)packetLength);
                Span<byte> nonce = record.Slice(sizeof(uint), NonceLength);
                nonce.Clear();
                BinaryPrimitives.WriteUInt64BigEndian(
                    nonce.Slice(NonceLength - sizeof(ulong)), sendCounter);
                Span<byte> ciphertext = record.Slice(
                    sizeof(uint) + NonceLength, count);
                Span<byte> tag = record.Slice(
                    sizeof(uint) + NonceLength + count, TagLength);
                sendCipher.Encrypt(nonce, buffer.AsSpan(offset, count),
                    ciphertext, tag);

                // One authenticated record maps to one transport write. This
                // keeps an input sample from paying four independent socket
                // submissions while preserving the exact VIIPER v1 framing.
                try
                {
                    inner.Write(sendPacket, 0, recordLength);
                    if (sendCounter == ulong.MaxValue)
                    {
                        sendCounterExhausted = true;
                    }
                    else
                    {
                        sendCounter++;
                    }
                }
                catch (Exception ex)
                {
                    // Stream.Write cannot report how much of a record reached
                    // the socket. Treat every failure as terminal so a retry
                    // can never append a new record behind a truncated one.
                    sendFault = ex;
                    CryptographicOperations.ZeroMemory(record);
                    try
                    {
                        inner.Dispose();
                    }
                    catch
                    {
                        // Preserve the write failure that made this
                        // authenticated stream unusable.
                    }
                    throw;
                }
            }
        }

        private void ThrowIfWriteFaulted()
        {
            if (sendFault != null)
            {
                throw new IOException(
                    "VIIPER encrypted stream is unusable after a failed write.",
                    sendFault);
            }
        }

        private static void EnsureCapacity(ref byte[] buffer,
            int requiredLength)
        {
            if (buffer.Length >= requiredLength)
            {
                return;
            }

            byte[] previous = buffer;
            buffer = new byte[requiredLength];
            if (previous.Length != 0)
            {
                CryptographicOperations.ZeroMemory(previous);
            }
        }

        private bool ReadPacket()
        {
            if (!TryReadPacketHeader(inner, receiveHeader))
            {
                return false;
            }
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(
                receiveHeader);
            if (rawLength < NonceLength + TagLength ||
                rawLength > MaximumPacketLength)
            {
                throw new IOException(
                    "VIIPER returned an invalid encrypted packet length.");
            }

            int packetLength = checked((int)rawLength);
            EnsureCapacity(ref receivePacket, packetLength);
            int ciphertextLength = packetLength - NonceLength - TagLength;
            EnsureCapacity(ref receivePlaintext, ciphertextLength);
            try
            {
                ReadExactly(inner, receivePacket, 0, packetLength);
                receiveCipher.Decrypt(
                    receivePacket.AsSpan(0, NonceLength),
                    receivePacket.AsSpan(NonceLength, ciphertextLength),
                    receivePacket.AsSpan(NonceLength + ciphertextLength,
                        TagLength),
                    receivePlaintext.AsSpan(0, ciphertextLength));
            }
            catch (CryptographicException ex)
            {
                CryptographicOperations.ZeroMemory(
                    receivePlaintext.AsSpan(0, ciphertextLength));
                throw new IOException(
                    "VIIPER encrypted packet authentication failed.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(
                    receivePacket.AsSpan(0, packetLength));
            }

            receiveLength = ciphertextLength;
            receiveOffset = 0;
            return true;
        }

        private static bool TryReadPacketHeader(Stream stream,
            byte[] header)
        {
            int total = 0;
            while (total < header.Length)
            {
                int read = stream.Read(header, total,
                    header.Length - total);
                if (read <= 0)
                {
                    if (total == 0)
                    {
                        // VIIPER one-shot API handlers write one encrypted
                        // response packet and then close the TCP connection.
                        // EOF before the next header is therefore the normal
                        // packet-boundary terminator consumed by ReadToEnd.
                        return false;
                    }

                    throw new IOException(
                        "VIIPER encrypted connection closed during a packet header.");
                }
                total += read;
            }

            return true;
        }

        private static void ReadExactly(Stream stream, byte[] buffer,
            int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total,
                    count - total);
                if (read <= 0)
                {
                    throw new IOException(
                        "VIIPER encrypted connection closed early.");
                }
                total += read;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && Interlocked.CompareExchange(
                    ref disposeState, 1, 0) == 0)
                {
                    // Closing the transport first releases a blocked Read or
                    // Write. Buffer and cipher teardown then joins both lanes
                    // through their locks before clearing reusable memory.
                    try
                    {
                        inner.Dispose();
                    }
                    finally
                    {
                        lock (writeLock)
                        {
                            CryptographicOperations.ZeroMemory(sendPacket);
                            sendCipher.Dispose();
                        }
                        lock (readLock)
                        {
                            CryptographicOperations.ZeroMemory(receiveHeader);
                            CryptographicOperations.ZeroMemory(receivePacket);
                            CryptographicOperations.ZeroMemory(
                                receivePlaintext);
                            receiveLength = 0;
                            receiveOffset = 0;
                            receiveCipher.Dispose();
                        }
                    }
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
