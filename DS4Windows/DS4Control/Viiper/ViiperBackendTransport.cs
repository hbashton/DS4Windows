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

namespace DS4Windows
{
    internal enum ViiperBackendMode
    {
        LegacyUsbip,
        NativeUde,
    }

    /// <summary>
    /// The live API transport selected by a successful ping. The credential is
    /// connection state rather than virtual-device identity and is therefore
    /// deliberately kept out of ViiperVirtualDeviceIdentity.
    /// </summary>
    internal sealed class ViiperBackendSelection
    {
        internal ViiperBackendSelection(ViiperBackendMode mode,
            string credential = null)
        {
            Mode = mode;
            Credential = string.IsNullOrWhiteSpace(credential)
                ? null
                : credential;
        }

        internal ViiperBackendMode Mode { get; }

        internal bool IsNative => Mode == ViiperBackendMode.NativeUde;

        internal bool UsesAuthentication => Credential != null;

        // Never include this value in diagnostics or ToString output.
        internal string Credential { get; }
    }

    internal sealed class ViiperNativeContractException : IOException
    {
        internal ViiperNativeContractException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Pins the native opt-in to the negotiated contract exposed by VIIPER
    /// commit 0e1eec2 and ABI 1.8. An unrecognised or incomplete native ping is
    /// an error, never a reason to fall back to USB/IP.
    /// </summary>
    internal static class ViiperBackendContract
    {
        internal const string NativeTransport = "native-ude";
        internal const string LegacyTransport = "usbip";
        internal const ushort NativeAbiMajor = 1;
        internal const ushort NativeAbiMinor = 8;
        internal const uint NativeCapabilities = 0x0d;
        internal const string NativeDriverPackageVersion = "0.1.0.0";
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
            if (ping.Ready != true || native == null ||
                native.AbiMajor != NativeAbiMajor ||
                native.AbiMinor != NativeAbiMinor ||
                native.Capabilities != NativeCapabilities ||
                !string.Equals(native.ExpectedDriverPackageVersion,
                    NativeDriverPackageVersion, StringComparison.Ordinal) ||
                native.MaxDevices != NativeMaxDevices ||
                native.MaxDescriptorBytes != NativeMaxDescriptorBytes ||
                native.MaxTransferBytes != NativeMaxTransferBytes ||
                native.MaxIsoPackets != NativeMaxIsoPackets ||
                native.MaxPendingOperations !=
                    NativeMaxPendingOperations)
            {
                throw new ViiperNativeContractException(
                    "VIIPER native UDE health proof does not match the required ABI 1.8 driver contract.");
            }

            return new ViiperBackendSelection(ViiperBackendMode.NativeUde,
                credential);
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

            byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), Pbkdf2Salt,
                Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
            byte[] clientNonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] clientProof;
            using (var hmac = new HMACSHA256(key))
            {
                byte[] proofInput = new byte[AuthContext.Length +
                    clientNonce.Length];
                Buffer.BlockCopy(AuthContext, 0, proofInput, 0,
                    AuthContext.Length);
                Buffer.BlockCopy(clientNonce, 0, proofInput,
                    AuthContext.Length, clientNonce.Length);
                clientProof = hmac.ComputeHash(proofInput);
                CryptographicOperations.ZeroMemory(proofInput);
            }

            byte[] handshake = new byte[HandshakeMagic.Length +
                clientNonce.Length + clientProof.Length];
            Buffer.BlockCopy(HandshakeMagic, 0, handshake, 0,
                HandshakeMagic.Length);
            Buffer.BlockCopy(clientNonce, 0, handshake,
                HandshakeMagic.Length, clientNonce.Length);
            Buffer.BlockCopy(clientProof, 0, handshake,
                HandshakeMagic.Length + clientNonce.Length,
                clientProof.Length);

            try
            {
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
                ReadExactly(stream, serverNonce, 0, serverNonce.Length);
                byte[] sessionInput = new byte[key.Length +
                    serverNonce.Length + clientNonce.Length +
                    SessionContext.Length];
                int offset = 0;
                Buffer.BlockCopy(key, 0, sessionInput, offset, key.Length);
                offset += key.Length;
                Buffer.BlockCopy(serverNonce, 0, sessionInput, offset,
                    serverNonce.Length);
                offset += serverNonce.Length;
                Buffer.BlockCopy(clientNonce, 0, sessionInput, offset,
                    clientNonce.Length);
                offset += clientNonce.Length;
                Buffer.BlockCopy(SessionContext, 0, sessionInput, offset,
                    SessionContext.Length);
                byte[] sessionKey = SHA256.HashData(sessionInput);
                CryptographicOperations.ZeroMemory(sessionInput);
                CryptographicOperations.ZeroMemory(serverNonce);
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
        private byte[] receivePlaintext = Array.Empty<byte>();
        private int receiveOffset;
        private ulong sendCounter;
        private bool disposed;

        internal ViiperAuthenticatedStream(Stream inner, byte[] sessionKey)
        {
            this.inner = inner ?? throw new ArgumentNullException(
                nameof(inner));
            ArgumentNullException.ThrowIfNull(sessionKey);
            // State and media writes can run concurrently with the feedback
            // reader. Separate instances avoid relying on undocumented
            // concurrent-use guarantees in the platform AEAD implementation.
            sendCipher = new ChaCha20Poly1305(sessionKey);
            receiveCipher = new ChaCha20Poly1305(sessionKey);
        }

        public override bool CanRead => !disposed && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => !disposed && inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException();
            }
            if (count == 0)
            {
                return 0;
            }

            if (receiveOffset >= receivePlaintext.Length)
            {
                ReadPacket();
            }

            int available = receivePlaintext.Length - receiveOffset;
            int copied = Math.Min(count, available);
            Buffer.BlockCopy(receivePlaintext, receiveOffset, buffer,
                offset, copied);
            receiveOffset += copied;
            if (receiveOffset >= receivePlaintext.Length)
            {
                CryptographicOperations.ZeroMemory(receivePlaintext);
                receivePlaintext = Array.Empty<byte>();
                receiveOffset = 0;
            }
            return copied;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException();
            }

            lock (writeLock)
            {
                byte[] nonce = new byte[NonceLength];
                BinaryPrimitives.WriteUInt64BigEndian(
                    nonce.AsSpan(4), sendCounter++);
                byte[] ciphertext = new byte[count];
                byte[] tag = new byte[TagLength];
                sendCipher.Encrypt(nonce, buffer.AsSpan(offset, count),
                    ciphertext, tag);
                int packetLength = nonce.Length + ciphertext.Length +
                    tag.Length;
                byte[] header = new byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(header,
                    (uint)packetLength);
                inner.Write(header, 0, header.Length);
                inner.Write(nonce, 0, nonce.Length);
                inner.Write(ciphertext, 0, ciphertext.Length);
                inner.Write(tag, 0, tag.Length);
            }
        }

        private void ReadPacket()
        {
            byte[] header = new byte[sizeof(uint)];
            ReadExactly(inner, header, 0, header.Length);
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (rawLength < NonceLength + TagLength ||
                rawLength > MaximumPacketLength)
            {
                throw new IOException(
                    "VIIPER returned an invalid encrypted packet length.");
            }

            int packetLength = checked((int)rawLength);
            byte[] packet = new byte[packetLength];
            ReadExactly(inner, packet, 0, packet.Length);
            int ciphertextLength = packetLength - NonceLength - TagLength;
            byte[] plaintext = new byte[ciphertextLength];
            try
            {
                receiveCipher.Decrypt(packet.AsSpan(0, NonceLength),
                    packet.AsSpan(NonceLength, ciphertextLength),
                    packet.AsSpan(NonceLength + ciphertextLength,
                        TagLength), plaintext);
            }
            catch (CryptographicException ex)
            {
                throw new IOException(
                    "VIIPER encrypted packet authentication failed.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(packet);
            }

            receivePlaintext = plaintext;
            receiveOffset = 0;
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
            if (!disposed)
            {
                disposed = true;
                if (disposing)
                {
                    if (receivePlaintext.Length != 0)
                    {
                        CryptographicOperations.ZeroMemory(
                            receivePlaintext);
                    }
                    sendCipher.Dispose();
                    receiveCipher.Dispose();
                    inner.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
