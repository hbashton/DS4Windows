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
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    /// <summary>
    /// Client half of VIIPER's documented authenticated transport. The wire
    /// contract is adapted from VIIPER's generated MIT-licensed C# client;
    /// this implementation caches the expensive deployment-key derivation and
    /// reuses fixed buffers on the controller stream hot path.
    /// </summary>
    internal static class ViiperAuthentication
    {
        private static readonly byte[] HandshakeMagic =
            Encoding.ASCII.GetBytes("eVI2\0");
        private static readonly byte[] AuthenticationContext =
            Encoding.ASCII.GetBytes("VIIPER-Auth-v2");
        private static readonly byte[] SessionContext =
            Encoding.ASCII.GetBytes("VIIPER-Session-v2");
        private static readonly byte[] PasswordSalt =
            Encoding.ASCII.GetBytes("VIIPER-Key-v1");
        private const int NonceLength = 32;
        private const int Pbkdf2Iterations = 100_000;
        private const string KeyFileName = "viiper.key.txt";

        private static readonly object KeyLock = new();
        private static string cachedKeyPath;
        private static DateTime cachedWriteTimeUtc;
        private static long cachedLength;
        private static byte[] cachedDerivedKey;

        internal static string DefaultKeyFilePath => PortableLabContext.Current?.KeyPath ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "VIIPER", KeyFileName);

        internal static Stream Authenticate(Stream transport)
        {
            ArgumentNullException.ThrowIfNull(transport);
            // Cache refresh may overlap a handshake waiting for its peer.
            // Keep one connection-owned key for both HMAC and session-key
            // derivation; the cache must never lend its mutable backing array.
            byte[] key = CopyDerivedDeploymentKey();
            byte[] clientNonce = null;
            byte[] authentication = null;
            byte[] request = null;
            byte[] response = null;
            byte[] serverNonce = null;
            byte[] sessionKey = null;
            try
            {
                clientNonce = RandomNumberGenerator.GetBytes(NonceLength);
                authentication = new byte[HMACSHA256.HashSizeInBytes];
                using (IncrementalHash hmac = IncrementalHash.CreateHMAC(
                    HashAlgorithmName.SHA256, key))
                {
                    hmac.AppendData(AuthenticationContext);
                    hmac.AppendData(clientNonce);
                    if (!hmac.TryGetHashAndReset(authentication,
                            out int written) || written != authentication.Length)
                    {
                        throw new CryptographicException(
                            "VIIPER authentication HMAC could not be created.");
                    }
                }

                request = new byte[HandshakeMagic.Length +
                    clientNonce.Length + authentication.Length];
                Buffer.BlockCopy(HandshakeMagic, 0, request, 0,
                    HandshakeMagic.Length);
                Buffer.BlockCopy(clientNonce, 0, request, HandshakeMagic.Length,
                    clientNonce.Length);
                Buffer.BlockCopy(authentication, 0, request,
                    HandshakeMagic.Length + clientNonce.Length,
                    authentication.Length);
                transport.Write(request, 0, request.Length);

                response = new byte[3 + NonceLength];
                ReadExactly(transport, response);
                if (response[0] != (byte)'O' || response[1] != (byte)'K' ||
                    response[2] != 0)
                {
                    throw new IOException(
                        "VIIPER rejected the authenticated client handshake.");
                }

                serverNonce = new byte[NonceLength];
                Buffer.BlockCopy(response, 3, serverNonce, 0,
                    serverNonce.Length);
                sessionKey = DeriveSessionKey(key, serverNonce, clientNonce);
                Stream authenticated = new ViiperEncryptedStream(transport, sessionKey,
                    ViiperConnectionRole.Client);
                sessionKey = null; // The returned stream owns and clears it.
                return authenticated;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                if (authentication != null) CryptographicOperations.ZeroMemory(authentication);
                if (clientNonce != null) CryptographicOperations.ZeroMemory(clientNonce);
                if (request != null) CryptographicOperations.ZeroMemory(request);
                if (response != null) CryptographicOperations.ZeroMemory(response);
                if (serverNonce != null) CryptographicOperations.ZeroMemory(serverNonce);
                if (sessionKey != null) CryptographicOperations.ZeroMemory(sessionKey);
            }
        }

        internal static byte[] DeriveKey(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException(
                    "VIIPER authentication password cannot be empty.",
                    nameof(password));
            }
            return Rfc2898DeriveBytes.Pbkdf2(password, PasswordSalt,
                Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        }

        internal static byte[] DeriveSessionKey(byte[] key,
            byte[] serverNonce, byte[] clientNonce)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(serverNonce);
            ArgumentNullException.ThrowIfNull(clientNonce);
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            hash.AppendData(key);
            hash.AppendData(serverNonce);
            hash.AppendData(clientNonce);
            hash.AppendData(SessionContext);
            return hash.GetHashAndReset();
        }

        private static byte[] CopyDerivedDeploymentKey()
        {
            string path = DefaultKeyFilePath;
            if (PortableLabContext.IsActive)
                PortableLabContext.ValidateNoReparsePoints(path);
            FileInfo info = new(path);
            if (!info.Exists)
            {
                throw new IOException(
                    $"VIIPER authentication key was not found at '{path}'. Start VIIPER once under this Windows account, then try again.");
            }

            lock (KeyLock)
            {
                info.Refresh();
                if (cachedDerivedKey != null &&
                    string.Equals(cachedKeyPath, info.FullName,
                        StringComparison.OrdinalIgnoreCase) &&
                    cachedWriteTimeUtc == info.LastWriteTimeUtc &&
                    cachedLength == info.Length)
                {
                    return (byte[])cachedDerivedKey.Clone();
                }

                string password = File.ReadAllText(info.FullName).Trim();
                byte[] replacement = DeriveKey(password);
                if (cachedDerivedKey != null)
                {
                    CryptographicOperations.ZeroMemory(cachedDerivedKey);
                }
                cachedDerivedKey = replacement;
                cachedKeyPath = info.FullName;
                cachedWriteTimeUtc = info.LastWriteTimeUtc;
                cachedLength = info.Length;
                return (byte[])cachedDerivedKey.Clone();
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset,
                    buffer.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "VIIPER closed during authentication.");
                }
                offset += read;
            }
        }
    }

    internal enum ViiperConnectionRole
    {
        Client = 1,
        Server = 2,
    }

    /// <summary>
    /// Allocation-free-after-warmup ChaCha20-Poly1305 v2 record stream matching
    /// VIIPER auth.Conn, with distinct nonce domains and strict sequence checking.
    /// </summary>
    internal sealed class ViiperEncryptedStream : Stream
    {
        private const int NonceLength = 12;
        private const int TagLength = 16;
        private const int HeaderLength = 4;
        private const int MaximumRecordLength = 2 * 1024 * 1024;

        private readonly Stream inner;
        private readonly ChaCha20Poly1305 cipher;
        private readonly byte[] sessionKey;
        private readonly uint sendPrefix;
        private readonly uint receivePrefix;
        private readonly object writeLock = new();
        private readonly object readLock = new();
        private byte[] writeBuffer = new byte[512];
        private byte[] encryptedReadBuffer = new byte[512];
        private byte[] plaintextReadBuffer = new byte[512];
        private ulong sendCounter;
        private ulong receiveCounter;
        private int plaintextReadOffset;
        private int plaintextReadLength;
        private int disposed;
        private int failed;

        internal ViiperEncryptedStream(Stream inner, byte[] sessionKey,
            ViiperConnectionRole role)
        {
            if (role != ViiperConnectionRole.Client && role != ViiperConnectionRole.Server)
                throw new ArgumentOutOfRangeException(nameof(role));
            sendPrefix = role == ViiperConnectionRole.Client ? 0u : 1u;
            receivePrefix = role == ViiperConnectionRole.Client ? 1u : 0u;
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.sessionKey = sessionKey ??
                throw new ArgumentNullException(nameof(sessionKey));
            if (sessionKey.Length != 32)
            {
                throw new ArgumentException(
                    "VIIPER session key must contain 32 bytes.",
                    nameof(sessionKey));
            }
            cipher = new ChaCha20Poly1305(sessionKey);
        }

        public override bool CanRead => Volatile.Read(ref disposed) == 0 &&
            inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => Volatile.Read(ref disposed) == 0 &&
            inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if ((uint)offset > (uint)buffer.Length ||
                (uint)count > (uint)(buffer.Length - offset))
            {
                throw new ArgumentOutOfRangeException();
            }
            ThrowIfDisposed();
            if (count == 0) return;
            if (count > MaximumRecordLength - NonceLength - TagLength)
                throw new ArgumentOutOfRangeException(nameof(count));
            lock (writeLock)
            {
                ThrowIfDisposed();
                try
                {
                    if (sendCounter == ulong.MaxValue)
                    {
                        throw new IOException(
                            "VIIPER encrypted send nonce was exhausted.");
                    }
                    int recordLength = NonceLength + count + TagLength;
                    int totalLength = HeaderLength + recordLength;
                    EnsureCapacity(ref writeBuffer, totalLength);
                    Span<byte> packet = writeBuffer.AsSpan(0, totalLength);
                    BinaryPrimitives.WriteUInt32BigEndian(packet,
                        (uint)recordLength);
                    Span<byte> nonce = packet.Slice(HeaderLength,
                        NonceLength);
                    BinaryPrimitives.WriteUInt32BigEndian(nonce, sendPrefix);
                    BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4),
                        sendCounter++);
                    Span<byte> ciphertext = packet.Slice(
                        HeaderLength + NonceLength, count);
                    Span<byte> tag = packet.Slice(
                        HeaderLength + NonceLength + count, TagLength);
                    cipher.Encrypt(nonce, buffer.AsSpan(offset, count),
                        ciphertext, tag);
                    inner.Write(writeBuffer, 0, totalLength);
                }
                catch { Volatile.Write(ref failed, 1); throw; }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if ((uint)offset > (uint)buffer.Length ||
                (uint)count > (uint)(buffer.Length - offset))
            {
                throw new ArgumentOutOfRangeException();
            }
            ThrowIfDisposed();
            if (count == 0)
            {
                return 0;
            }

            lock (readLock)
            {
                ThrowIfDisposed();
                try
                {
                    while (plaintextReadOffset >= plaintextReadLength)
                    {
                        if (!ReadNextRecord())
                        {
                            return 0;
                        }
                    }
                    int available = plaintextReadLength - plaintextReadOffset;
                    int copied = Math.Min(count, available);
                    Buffer.BlockCopy(plaintextReadBuffer, plaintextReadOffset,
                        buffer, offset, copied);
                    plaintextReadOffset += copied;
                    return copied;
                }
                catch { Volatile.Write(ref failed, 1); throw; }
            }
        }

        private bool ReadNextRecord()
        {
            Span<byte> header = stackalloc byte[HeaderLength];
            if (!TryReadExactly(header))
            {
                return false;
            }
            uint recordLength = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (recordLength < NonceLength + TagLength ||
                recordLength > MaximumRecordLength)
            {
                throw new InvalidDataException(
                    $"VIIPER encrypted record length {recordLength} is invalid.");
            }
            int encryptedLength = checked((int)recordLength);
            int plaintextLength = encryptedLength - NonceLength - TagLength;
            EnsureCapacity(ref encryptedReadBuffer, encryptedLength);
            EnsureCapacity(ref plaintextReadBuffer, plaintextLength);
            ReadRecordExactly(encryptedReadBuffer.AsSpan(0,
                encryptedLength));

            ReadOnlySpan<byte> nonce = encryptedReadBuffer.AsSpan(0,
                NonceLength);
            if (BinaryPrimitives.ReadUInt32BigEndian(nonce) != receivePrefix ||
                BinaryPrimitives.ReadUInt64BigEndian(nonce.Slice(4)) !=
                    receiveCounter || receiveCounter == ulong.MaxValue)
            {
                throw new InvalidDataException(
                    "VIIPER encrypted record nonce is stale or out of order.");
            }
            ReadOnlySpan<byte> ciphertext = encryptedReadBuffer.AsSpan(
                NonceLength, plaintextLength);
            ReadOnlySpan<byte> tag = encryptedReadBuffer.AsSpan(
                NonceLength + plaintextLength, TagLength);
            cipher.Decrypt(nonce, ciphertext, tag,
                plaintextReadBuffer.AsSpan(0, plaintextLength));
            receiveCounter++;
            plaintextReadOffset = 0;
            plaintextReadLength = plaintextLength;
            return true;
        }

        private bool TryReadExactly(Span<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = inner.Read(buffer.Slice(offset));
                if (read == 0)
                {
                    if (offset == 0)
                    {
                        return false;
                    }
                    throw new EndOfStreamException(
                        "VIIPER encrypted record was truncated.");
                }
                offset += read;
            }
            return true;
        }

        private void ReadRecordExactly(Span<byte> buffer)
        {
            if (!TryReadExactly(buffer))
            {
                throw new EndOfStreamException(
                    "VIIPER encrypted record was truncated.");
            }
        }

        private static void EnsureCapacity(ref byte[] buffer, int required)
        {
            if (buffer.Length >= required)
            {
                return;
            }
            int size = buffer.Length;
            while (size < required)
            {
                size = checked(size * 2);
            }
            Array.Resize(ref buffer, size);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(ViiperEncryptedStream));
            }
            if (Volatile.Read(ref failed) != 0)
                throw new IOException("VIIPER encrypted connection is faulted.");
        }

        public override void Flush()
        {
            ThrowIfDisposed();
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return inner.FlushAsync(cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset,
            int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    try
                    {
                        if (disposing)
                        {
                            try { inner.Dispose(); }
                            finally { cipher.Dispose(); }
                        }
                    }
                    finally
                    {
                        // A failing transport close must not strand key or
                        // plaintext buffers behind the already-set disposed flag.
                        CryptographicOperations.ZeroMemory(sessionKey);
                        CryptographicOperations.ZeroMemory(writeBuffer);
                        CryptographicOperations.ZeroMemory(encryptedReadBuffer);
                        CryptographicOperations.ZeroMemory(plaintextReadBuffer);
                    }
                }
            }
            finally { base.Dispose(disposing); }
        }
    }
}
