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

namespace DS4Windows
{
    /// <summary>
    /// Implements the current VIIPER authentication handshake. The constants,
    /// byte order, and key schedule are shared with
    /// internal/server/api/auth in the VIIPER source tree.
    /// </summary>
    internal static class ViiperAuthProtocol
    {
        internal const int NonceSize = 32;
        internal const int SessionKeySize = 32;
        internal const int Pbkdf2Iterations = 100000;
        internal const string HandshakeMagic = "eVI1\0";
        internal const string AuthenticationContext = "VIIPER-Auth-v1";
        internal const string SessionContext = "VIIPER-Session-v1";
        internal const string Pbkdf2Salt = "VIIPER-Key-v1";

        internal static Stream AuthenticateClient(Stream transport,
            string password)
        {
            byte[] clientNonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(clientNonce);
            return AuthenticateClient(transport, password, clientNonce);
        }

        internal static Stream AuthenticateClient(Stream transport,
            string password, byte[] clientNonce)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }
            if (string.IsNullOrEmpty(password))
            {
                throw new IOException("The VIIPER API credential is empty.");
            }
            if (clientNonce == null || clientNonce.Length != NonceSize)
            {
                throw new ArgumentException(
                    $"The VIIPER client nonce must be exactly {NonceSize} bytes.",
                    nameof(clientNonce));
            }

            byte[] passwordKey = DerivePasswordKey(password);
            byte[] authenticationData = new byte[
                Encoding.UTF8.GetByteCount(AuthenticationContext) + NonceSize];
            byte[] handshake = new byte[
                Encoding.UTF8.GetByteCount(HandshakeMagic) + NonceSize +
                SessionKeySize];
            byte[] serverNonce = new byte[NonceSize];
            byte[] sessionKey = null;
            try
            {
                int contextLength = Encoding.UTF8.GetBytes(
                    AuthenticationContext, authenticationData);
                Buffer.BlockCopy(clientNonce, 0, authenticationData,
                    contextLength, NonceSize);

                byte[] authenticationTag = HMACSHA256.HashData(passwordKey,
                    authenticationData);
                try
                {
                    int magicLength = Encoding.UTF8.GetBytes(HandshakeMagic,
                        handshake);
                    Buffer.BlockCopy(clientNonce, 0, handshake, magicLength,
                        NonceSize);
                    Buffer.BlockCopy(authenticationTag, 0, handshake,
                        magicLength + NonceSize, authenticationTag.Length);
                    transport.Write(handshake, 0, handshake.Length);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(authenticationTag);
                }

                byte[] responsePrefix = new byte[3];
                ReadExactly(transport, responsePrefix, 0,
                    responsePrefix.Length,
                    "VIIPER closed during the authentication response.");
                if (responsePrefix[0] != (byte)'O' ||
                    responsePrefix[1] != (byte)'K' || responsePrefix[2] != 0)
                {
                    throw ReadAuthenticationError(transport, responsePrefix);
                }

                ReadExactly(transport, serverNonce, 0, serverNonce.Length,
                    "VIIPER closed before returning its server nonce.");
                sessionKey = DeriveSessionKey(passwordKey, serverNonce,
                    clientNonce);
                Stream encrypted = new ViiperEncryptedStream(transport,
                    sessionKey);
                CryptographicOperations.ZeroMemory(sessionKey);
                sessionKey = null;
                return encrypted;
            }
            catch
            {
                transport.Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordKey);
                CryptographicOperations.ZeroMemory(authenticationData);
                CryptographicOperations.ZeroMemory(handshake);
                CryptographicOperations.ZeroMemory(serverNonce);
                if (sessionKey != null)
                {
                    CryptographicOperations.ZeroMemory(sessionKey);
                }
            }
        }

        internal static byte[] DerivePasswordKey(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Password cannot be empty.",
                    nameof(password));
            }

            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                Encoding.UTF8.GetBytes(Pbkdf2Salt),
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                SessionKeySize);
        }

        internal static byte[] DeriveSessionKey(byte[] passwordKey,
            byte[] serverNonce, byte[] clientNonce)
        {
            if (passwordKey == null || passwordKey.Length != SessionKeySize)
            {
                throw new ArgumentException(
                    $"The VIIPER password key must be {SessionKeySize} bytes.",
                    nameof(passwordKey));
            }
            if (serverNonce == null || serverNonce.Length != NonceSize)
            {
                throw new ArgumentException(
                    $"The VIIPER server nonce must be {NonceSize} bytes.",
                    nameof(serverNonce));
            }
            if (clientNonce == null || clientNonce.Length != NonceSize)
            {
                throw new ArgumentException(
                    $"The VIIPER client nonce must be {NonceSize} bytes.",
                    nameof(clientNonce));
            }

            byte[] context = Encoding.UTF8.GetBytes(SessionContext);
            byte[] material = new byte[passwordKey.Length +
                serverNonce.Length + clientNonce.Length + context.Length];
            try
            {
                int offset = 0;
                Buffer.BlockCopy(passwordKey, 0, material, offset,
                    passwordKey.Length);
                offset += passwordKey.Length;
                Buffer.BlockCopy(serverNonce, 0, material, offset,
                    serverNonce.Length);
                offset += serverNonce.Length;
                Buffer.BlockCopy(clientNonce, 0, material, offset,
                    clientNonce.Length);
                offset += clientNonce.Length;
                Buffer.BlockCopy(context, 0, material, offset,
                    context.Length);
                return SHA256.HashData(material);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }

        private static ViiperAuthenticationException ReadAuthenticationError(
            Stream transport,
            byte[] prefix)
        {
            const int maximumErrorBytes = 64 * 1024;
            using MemoryStream response = new MemoryStream();
            response.Write(prefix, 0, prefix.Length);
            byte[] buffer = new byte[1024];
            try
            {
                while (response.Length < maximumErrorBytes)
                {
                    int read = transport.Read(buffer, 0, Math.Min(buffer.Length,
                        maximumErrorBytes - (int)response.Length));
                    if (read == 0)
                    {
                        break;
                    }
                    response.Write(buffer, 0, read);
                }
            }
            catch (IOException)
            {
                // Preserve the server bytes already received. Authentication
                // still fails closed even if the diagnostic tail was cut off.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }

            string detail = Encoding.UTF8.GetString(response.ToArray())
                .TrimEnd('\0', '\r', '\n');
            return new ViiperAuthenticationException(
                string.IsNullOrEmpty(detail) ?
                "VIIPER rejected the authentication handshake." :
                $"VIIPER rejected the authentication handshake: {detail}");
        }

        private static void ReadExactly(Stream stream, byte[] buffer,
            int offset, int count, string closedMessage)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    throw new IOException(closedMessage);
                }
                total += read;
            }
        }
    }

    /// <summary>
    /// VIIPER authenticated record stream. Client records use nonce domain 0;
    /// server records must use nonce domain 1. Both directions require an
    /// exact monotonically increasing 64-bit counter.
    /// </summary>
    internal sealed class ViiperEncryptedStream : Stream
    {
        internal const int MaximumRecordSize = 2 * 1024 * 1024;
        internal const int NonceSize = 12;
        internal const int TagSize = 16;
        internal const int RecordOverhead = NonceSize + TagSize;
        internal const int MaximumPlaintextSize = MaximumRecordSize -
            RecordOverhead;
        internal const uint ClientNoncePrefix = 0;
        internal const uint ServerNoncePrefix = 1;

        private readonly Stream transport;
        private readonly byte[] sessionKey;
        private readonly object sendLock = new object();
        private readonly object receiveLock = new object();
        private ChaCha20Poly1305 sendCipher;
        private ChaCha20Poly1305 receiveCipher;
        private byte[] sendRecord = Array.Empty<byte>();
        private readonly byte[] receiveHeader = new byte[4];
        private byte[] receiveRecord = Array.Empty<byte>();
        private byte[] receivePlaintext = Array.Empty<byte>();
        private int receiveHeaderRead;
        private int receiveRecordRead;
        private int receiveRecordLength;
        private int receivePlaintextOffset;
        private int receivePlaintextLength;
        private ulong sendCounter;
        private ulong receiveCounter;
        private bool sendExhausted;
        private bool receiveExhausted;
        private Exception sendError;
        private Exception receiveError;
        private int disposed;

        internal ViiperEncryptedStream(Stream transport, byte[] sessionKey)
            : this(transport, sessionKey, 0, 0)
        {
        }

        internal ViiperEncryptedStream(Stream transport, byte[] sessionKey,
            ulong sendCounter, ulong receiveCounter)
        {
            this.transport = transport ??
                throw new ArgumentNullException(nameof(transport));
            if (sessionKey == null ||
                sessionKey.Length != ViiperAuthProtocol.SessionKeySize)
            {
                throw new ArgumentException(
                    $"The VIIPER session key must be {ViiperAuthProtocol.SessionKeySize} bytes.",
                    nameof(sessionKey));
            }

            this.sessionKey = (byte[])sessionKey.Clone();
            this.sendCounter = sendCounter;
            this.receiveCounter = receiveCounter;
            sendCipher = new ChaCha20Poly1305(this.sessionKey);
            receiveCipher = new ChaCha20Poly1305(this.sessionKey);
        }

        public override bool CanRead => Volatile.Read(ref disposed) == 0 &&
            transport.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => Volatile.Read(ref disposed) == 0 &&
            transport.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            ThrowIfDisposed();
            transport.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateArrayArguments(buffer, offset, count);
            if (count == 0)
            {
                return 0;
            }

            lock (receiveLock)
            {
                ThrowIfDisposed();
                if (receiveError != null)
                {
                    throw new IOException(
                        "The VIIPER authenticated receive lane has failed.",
                        receiveError);
                }

                while (receivePlaintextOffset == receivePlaintextLength)
                {
                    if (!ReadRecord())
                    {
                        return 0;
                    }
                }

                int copied = Math.Min(count,
                    receivePlaintextLength - receivePlaintextOffset);
                Buffer.BlockCopy(receivePlaintext, receivePlaintextOffset,
                    buffer, offset, copied);
                CryptographicOperations.ZeroMemory(
                    receivePlaintext.AsSpan(receivePlaintextOffset, copied));
                receivePlaintextOffset += copied;
                if (receivePlaintextOffset == receivePlaintextLength)
                {
                    receivePlaintextOffset = 0;
                    receivePlaintextLength = 0;
                }
                return copied;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateArrayArguments(buffer, offset, count);
            lock (sendLock)
            {
                ThrowIfDisposed();
                if (sendError != null)
                {
                    throw new IOException(
                        "The VIIPER authenticated send lane has failed.",
                        sendError);
                }
                if (sendExhausted)
                {
                    throw new IOException(
                        "The VIIPER authenticated send nonce space is exhausted.");
                }
                if (count > MaximumPlaintextSize)
                {
                    throw new IOException(
                        "The VIIPER authenticated stream packet is too large.");
                }

                int recordLength = RecordOverhead + count;
                int wireLength = sizeof(uint) + recordLength;
                if (sendRecord.Length < wireLength)
                {
                    sendRecord = new byte[wireLength];
                }
                Span<byte> record = sendRecord.AsSpan(0, wireLength);
                BinaryPrimitives.WriteUInt32BigEndian(record,
                    (uint)recordLength);
                Span<byte> nonce = record.Slice(sizeof(uint), NonceSize);
                BinaryPrimitives.WriteUInt32BigEndian(nonce,
                    ClientNoncePrefix);
                BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4),
                    sendCounter);
                Span<byte> ciphertext = record.Slice(sizeof(uint) + NonceSize,
                    count);
                Span<byte> tag = record.Slice(sizeof(uint) + NonceSize + count,
                    TagSize);
                try
                {
                    sendCipher.Encrypt(nonce,
                        buffer.AsSpan(offset, count), ciphertext, tag);
                    transport.Write(sendRecord, 0, wireLength);
                    AdvanceSendCounter();
                }
                catch (Exception ex) when (ex is IOException ||
                    ex is ObjectDisposedException ||
                    ex is CryptographicException)
                {
                    throw LatchSendFailure(ex);
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        private bool ReadRecord()
        {
            try
            {
                if (receiveHeaderRead < receiveHeader.Length &&
                    !FillReceiveBuffer(receiveHeader,
                        ref receiveHeaderRead, receiveHeader.Length,
                        allowCleanEndOfStream: receiveHeaderRead == 0))
                {
                    return false;
                }

                if (receiveRecordLength == 0)
                {
                    uint encodedLength =
                        BinaryPrimitives.ReadUInt32BigEndian(receiveHeader);
                    if (encodedLength < RecordOverhead)
                    {
                        throw new InvalidDataException(
                            "The VIIPER authenticated stream packet is too short.");
                    }
                    if (encodedLength > MaximumRecordSize)
                    {
                        throw new InvalidDataException(
                            "The VIIPER authenticated stream packet is too large.");
                    }

                    receiveRecordLength = checked((int)encodedLength);
                    if (receiveRecord.Length < receiveRecordLength)
                    {
                        if (receiveRecord.Length != 0)
                        {
                            CryptographicOperations.ZeroMemory(receiveRecord);
                        }
                        receiveRecord = new byte[receiveRecordLength];
                    }
                }
                FillReceiveBuffer(receiveRecord, ref receiveRecordRead,
                    receiveRecordLength, allowCleanEndOfStream: false);

                int plaintextLength = receiveRecordLength - RecordOverhead;
                if (receivePlaintext.Length < plaintextLength)
                {
                    if (receivePlaintext.Length != 0)
                    {
                        CryptographicOperations.ZeroMemory(receivePlaintext);
                    }
                    receivePlaintext = new byte[plaintextLength];
                }
                ReadOnlySpan<byte> nonce = receiveRecord.AsSpan(0,
                    NonceSize);
                ReadOnlySpan<byte> ciphertext = receiveRecord.AsSpan(
                    NonceSize, plaintextLength);
                ReadOnlySpan<byte> tag = receiveRecord.AsSpan(
                    NonceSize + plaintextLength, TagSize);
                receiveCipher.Decrypt(nonce, ciphertext, tag,
                    receivePlaintext.AsSpan(0, plaintextLength));

                uint prefix = BinaryPrimitives.ReadUInt32BigEndian(nonce);
                ulong counter = BinaryPrimitives.ReadUInt64BigEndian(
                    nonce.Slice(4));
                if (prefix != ServerNoncePrefix)
                {
                    throw new InvalidDataException(
                        $"VIIPER authenticated stream nonce direction={prefix}, expected {ServerNoncePrefix}.");
                }
                if (receiveExhausted)
                {
                    throw new InvalidDataException(
                        "The VIIPER authenticated receive nonce space is exhausted.");
                }
                if (counter != receiveCounter)
                {
                    throw new InvalidDataException(
                        $"VIIPER authenticated stream nonce counter={counter}, expected {receiveCounter}.");
                }

                AdvanceReceiveCounter();
                receiveHeaderRead = 0;
                receiveRecordRead = 0;
                receiveRecordLength = 0;
                receivePlaintextOffset = 0;
                receivePlaintextLength = plaintextLength;
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException ||
                ex is CryptographicException || ex is OverflowException)
            {
                if (receivePlaintext.Length != 0)
                {
                    CryptographicOperations.ZeroMemory(receivePlaintext);
                }
                receivePlaintextOffset = 0;
                receivePlaintextLength = 0;
                receiveError = ex;
                throw new IOException(
                    "The VIIPER authenticated receive record was rejected.",
                    ex);
            }
        }

        private bool FillReceiveBuffer(byte[] buffer, ref int offset,
            int count, bool allowCleanEndOfStream)
        {
            while (offset < count)
            {
                int read = transport.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    if (allowCleanEndOfStream && offset == 0)
                    {
                        return false;
                    }
                    throw new EndOfStreamException(
                        "VIIPER closed in the middle of an authenticated record.");
                }
                offset += read;
            }
            return true;
        }

        private void AdvanceSendCounter()
        {
            if (sendCounter == ulong.MaxValue)
            {
                sendExhausted = true;
            }
            else
            {
                sendCounter++;
            }
        }

        private void AdvanceReceiveCounter()
        {
            if (receiveCounter == ulong.MaxValue)
            {
                receiveExhausted = true;
            }
            else
            {
                receiveCounter++;
            }
        }

        private IOException LatchSendFailure(Exception failure)
        {
            sendError ??= failure;
            try
            {
                transport.Dispose();
            }
            catch
            {
            }
            return failure as IOException ?? new IOException(
                "The VIIPER authenticated send record failed.", failure);
        }

        private static void ValidateArrayArguments(byte[] buffer, int offset,
            int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException(
                    offset < 0 ? nameof(offset) : nameof(count));
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(ViiperEncryptedStream));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing || Interlocked.Exchange(ref disposed, 1) != 0)
            {
                base.Dispose(disposing);
                return;
            }

            // Close the transport before joining the lanes. This wakes a
            // blocked network read/write without racing cipher cleanup.
            try
            {
                transport.Dispose();
            }
            catch
            {
            }

            lock (sendLock)
            {
                lock (receiveLock)
                {
                    sendCipher?.Dispose();
                    receiveCipher?.Dispose();
                    sendCipher = null;
                    receiveCipher = null;
                    CryptographicOperations.ZeroMemory(sessionKey);
                    CryptographicOperations.ZeroMemory(sendRecord);
                    CryptographicOperations.ZeroMemory(receiveHeader);
                    CryptographicOperations.ZeroMemory(receiveRecord);
                    CryptographicOperations.ZeroMemory(receivePlaintext);
                    sendRecord = Array.Empty<byte>();
                    receiveRecord = Array.Empty<byte>();
                    receivePlaintext = Array.Empty<byte>();
                    receiveHeaderRead = 0;
                    receiveRecordRead = 0;
                    receiveRecordLength = 0;
                    sendError ??= new ObjectDisposedException(
                        nameof(ViiperEncryptedStream));
                    receiveError ??= new ObjectDisposedException(
                        nameof(ViiperEncryptedStream));
                }
            }
            base.Dispose(disposing);
        }
    }
}
