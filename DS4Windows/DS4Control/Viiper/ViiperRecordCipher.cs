using System;
using System.Security.Cryptography;
using System.Threading;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using ManagedChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace DS4Windows;

internal enum ViiperCipherImplementation
{
    Automatic,
    Platform,
    Managed
}

/// <summary>
/// Same RFC 8439 AEAD and wire bytes on every supported Windows version.
/// Windows 10 19045 lacks the native provider; this is an implementation
/// choice, never authentication negotiation or a protocol downgrade.
/// The stream serializes each direction independently, so mutable managed
/// cipher state and scratch must also remain separate for full-duplex I/O.
/// </summary>
internal sealed class ViiperRecordCipher : IDisposable
{
    private readonly ChaCha20Poly1305 platform;
    private readonly ManagedChaCha20Poly1305 encryptor;
    private readonly ManagedChaCha20Poly1305 decryptor;
    private readonly byte[] key;
    private readonly object encryptionGate = new();
    private readonly object decryptionGate = new();
    private byte[] encryptionScratch;
    private byte[] decryptionScratch;
    private bool encryptionInitialized;
    private bool decryptionInitialized;
    private int disposed;

    internal ViiperCipherImplementation Implementation { get; }

    internal ViiperRecordCipher(byte[] key,
        ViiperCipherImplementation implementation = ViiperCipherImplementation.Automatic)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("ChaCha20-Poly1305 requires a 32-byte key.", nameof(key));
        if (implementation is < ViiperCipherImplementation.Automatic or > ViiperCipherImplementation.Managed)
            throw new ArgumentOutOfRangeException(nameof(implementation));
        this.key = key; // Connection-owned; ViiperEncryptedStream clears it.
        Implementation = implementation == ViiperCipherImplementation.Automatic ?
            SelectImplementation(ChaCha20Poly1305.IsSupported) : implementation;
        if (Implementation == ViiperCipherImplementation.Platform)
            platform = new ChaCha20Poly1305(key);
        else
        {
            encryptor = new ManagedChaCha20Poly1305();
            decryptor = new ManagedChaCha20Poly1305();
            encryptionScratch = new byte[512];
            decryptionScratch = new byte[512];
        }
    }

    internal static ViiperCipherImplementation SelectImplementation(bool platformSupported) =>
        platformSupported ? ViiperCipherImplementation.Platform : ViiperCipherImplementation.Managed;

    internal void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData = default)
    {
        lock (encryptionGate) EncryptCore(nonce, plaintext, ciphertext, tag, associatedData);
    }

    private void EncryptCore(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData)
    {
        ThrowIfDisposed();
        Validate(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        if (platform != null)
        {
            platform.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return;
        }

        EnsureCapacity(ref encryptionScratch, checked(plaintext.Length + 16));
        Init(encryptor, true, nonce, ref encryptionInitialized);
        if (!associatedData.IsEmpty) encryptor.ProcessAadBytes(associatedData);
        Span<byte> combined = encryptionScratch.AsSpan(0, plaintext.Length + 16);
        int written = encryptor.ProcessBytes(plaintext, combined);
        written += encryptor.DoFinal(combined.Slice(written));
        if (written != combined.Length) throw new CryptographicException("Invalid managed AEAD output length.");
        combined.Slice(0, plaintext.Length).CopyTo(ciphertext);
        combined.Slice(plaintext.Length, 16).CopyTo(tag);
    }

    internal void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        lock (decryptionGate) DecryptCore(nonce, ciphertext, tag, plaintext, associatedData);
    }

    private void DecryptCore(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        ThrowIfDisposed();
        Validate(nonce, ciphertext.Length, plaintext.Length, tag.Length);
        if (platform != null)
        {
            platform.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return;
        }

        EnsureCapacity(ref decryptionScratch, checked(ciphertext.Length + 16));
        Span<byte> combined = decryptionScratch.AsSpan(0, ciphertext.Length + 16);
        ciphertext.CopyTo(combined);
        tag.CopyTo(combined.Slice(ciphertext.Length));
        try
        {
            Init(decryptor, false, nonce, ref decryptionInitialized);
            if (!associatedData.IsEmpty) decryptor.ProcessAadBytes(associatedData);
            int written = decryptor.ProcessBytes(combined, plaintext);
            written += decryptor.DoFinal(plaintext.Slice(written));
            if (written != plaintext.Length) throw new CryptographicException("Invalid managed AEAD plaintext length.");
        }
        catch (InvalidCipherTextException exception)
        {
            // BC may write unauthenticated bytes before DoFinal verifies the
            // tag. Never leave those bytes in the stream's plaintext buffer.
            CryptographicOperations.ZeroMemory(plaintext);
            throw new AuthenticationTagMismatchException("VIIPER encrypted record authentication failed.", exception);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private void Init(ManagedChaCha20Poly1305 cipher, bool encrypting,
        ReadOnlySpan<byte> nonce, ref bool initialized)
    {
        // BC copies nonce parameters and allocates MAC bookkeeping per record.
        // Keep its public API and nonce checks; do not reach into crypto
        // internals merely to claim zero allocations. Native mode remains
        // allocation-free, and warmed managed record allocation is bounded.
        cipher.Init(encrypting, new ParametersWithIV(initialized ? null : new KeyParameter(key), nonce));
        initialized = true;
    }

    private static void Validate(ReadOnlySpan<byte> nonce, int inputLength, int outputLength, int tagLength)
    {
        if (nonce.Length != 12 || tagLength != 16 || inputLength != outputLength)
            throw new ArgumentException("Invalid ChaCha20-Poly1305 nonce, tag, or output length.");
    }

    private static void EnsureCapacity(ref byte[] buffer, int required)
    {
        if (buffer.Length >= required) return;
        int capacity = buffer.Length;
        while (capacity < required) capacity = checked(capacity * 2);
        byte[] replacement = new byte[capacity];
        CryptographicOperations.ZeroMemory(buffer);
        buffer = replacement;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(ViiperRecordCipher));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // The stream closes its transport first, then this drains admitted
        // crypto calls. Rekeying mutable managed state cannot race either
        // direction; send and receive remain independent during normal I/O.
        lock (encryptionGate)
        lock (decryptionGate)
            DisposeCore();
    }

    private void DisposeCore()
    {
        try { platform?.Dispose(); }
        finally
        {
            if (encryptor != null)
            {
                // BC has no IDisposable/zeroization API. Rekey the retained
                // instances with zeros through its public API, clearing their
                // active key/MAC/data state. Temporary library key copies are
                // GC-managed; this is not a guarantee of whole-heap erasure.
                try { RekeyWithZeros(encryptor); }
                finally
                {
                    try { RekeyWithZeros(decryptor); }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(encryptionScratch);
                        CryptographicOperations.ZeroMemory(decryptionScratch);
                    }
                }
            }
        }
    }

    private static void RekeyWithZeros(ManagedChaCha20Poly1305 cipher)
    {
        Span<byte> zeroKey = stackalloc byte[32];
        Span<byte> zeroNonce = stackalloc byte[12];
        zeroKey.Clear();
        zeroNonce.Clear();
        cipher.Init(false, new ParametersWithIV(new KeyParameter(zeroKey), zeroNonce));
    }
}
