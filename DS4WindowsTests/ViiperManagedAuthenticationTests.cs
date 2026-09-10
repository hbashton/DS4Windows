using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class ViiperManagedAuthenticationTests
{
    // Independent Node/OpenSSL records shared with the Go v2 contract tests.
    private static readonly string[] Records =
    {
        "0000002400000000000000000000000018b94032d266582e05ebcfe4ba88b8a24dd1043e6dcd23fb",
        "00000024000000000000000000000001695d7eda4e8a46850d10d0c85e47680d9f125be025e25461",
        "00000024000000010000000000000000ab479fea760618c3be9f8fd13269fd4b4fc440460493639f",
        "00000024000000010000000000000001e10be70c805489fbcb0d12b623a633fd8e2ad6a2e57a58c0"
    };
    private static byte[] Key() => Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

    [TestMethod]
    public void UnsupportedPlatformSelectsSameAlgorithmManagedImplementation()
    {
        Assert.AreEqual(ViiperCipherImplementation.Managed, ViiperRecordCipher.SelectImplementation(false));
        Assert.AreEqual(ViiperCipherImplementation.Platform, ViiperRecordCipher.SelectImplementation(true));
        using var automatic = new ViiperRecordCipher(Key());
        Assert.AreEqual(ViiperRecordCipher.SelectImplementation(ChaCha20Poly1305.IsSupported), automatic.Implementation);
    }

    [TestMethod]
    public void ManagedCipherMatchesRfc8439Section282WithAssociatedData()
    {
        // https://www.rfc-editor.org/rfc/rfc8439#section-2.8.2
        byte[] key = Enumerable.Range(0x80, 32).Select(index => (byte)index).ToArray();
        byte[] nonce = Convert.FromHexString("070000004041424344454647");
        byte[] aad = Convert.FromHexString("50515253c0c1c2c3c4c5c6c7");
        byte[] plain = Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        byte[] ciphertext = new byte[plain.Length], tag = new byte[16], decoded = new byte[plain.Length];
        using var cipher = new ViiperRecordCipher(key, ViiperCipherImplementation.Managed);
        cipher.Encrypt(nonce, plain, ciphertext, tag, aad);
        CollectionAssert.AreEqual(Convert.FromHexString(
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6" +
            "3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36" +
            "92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc" +
            "3ff4def08e4b7a9de576d26586cec64b6116"), ciphertext);
        CollectionAssert.AreEqual(Convert.FromHexString("1ae10b594f09e26a7e902ecbd0600691"), tag);
        cipher.Decrypt(nonce, ciphertext, tag, decoded, aad);
        CollectionAssert.AreEqual(plain, decoded);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void ForcedManagedStreamMatchesBothDirectionalV2Vectors(int role)
    {
        byte[] payload = Convert.FromHexString("000102037f80feff");
        using var wire = new MemoryStream();
        using var writer = new ViiperEncryptedStream(wire, Key(), (ViiperConnectionRole)role,
            ViiperCipherImplementation.Managed);
        for (int counter = 0; counter < 2; counter++)
        {
            wire.SetLength(0);
            writer.Write(payload);
            CollectionAssert.AreEqual(Convert.FromHexString(Records[(role - 1) * 2 + counter]), wire.ToArray());
        }
        using var incoming = new MemoryStream(Convert.FromHexString(Records[(2 - role) * 2] + Records[(2 - role) * 2 + 1]));
        using var reader = new ViiperEncryptedStream(incoming, Key(), (ViiperConnectionRole)role,
            ViiperCipherImplementation.Managed);
        byte[] actual = new byte[16];
        for (int index = 0; index < actual.Length; index++) Assert.AreEqual(1, reader.Read(actual, index, 1));
        CollectionAssert.AreEqual(payload.Concat(payload).ToArray(), actual);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(15)]
    [DataRow(16)]
    [DataRow(17)]
    [DataRow(63)]
    [DataRow(64)]
    [DataRow(65)]
    [DataRow(511)]
    [DataRow(512)]
    [DataRow(4096)]
    [DataRow(65536)]
    public void ManagedAndPlatformCipherOutputsAreIdenticalAcrossBufferBoundaries(int length)
    {
        if (!ChaCha20Poly1305.IsSupported) Assert.Inconclusive("Independent platform provider unavailable on this OS; RFC and OpenSSL vectors still run.");
        byte[] plain = Enumerable.Range(0, length).Select(index => (byte)(index * 31)).ToArray();
        byte[] nonce = new byte[12], nativeCiphertext = new byte[length], managedCiphertext = new byte[length];
        byte[] nativeTag = new byte[16], managedTag = new byte[16], decoded = new byte[length];
        using var native = new ViiperRecordCipher(Key(), ViiperCipherImplementation.Platform);
        using var managed = new ViiperRecordCipher(Key(), ViiperCipherImplementation.Managed);
        native.Encrypt(nonce, plain, nativeCiphertext, nativeTag);
        managed.Encrypt(nonce, plain, managedCiphertext, managedTag);
        CollectionAssert.AreEqual(nativeCiphertext, managedCiphertext);
        CollectionAssert.AreEqual(nativeTag, managedTag);
        managed.Decrypt(nonce, nativeCiphertext, nativeTag, decoded);
        CollectionAssert.AreEqual(plain, decoded);
        Array.Clear(decoded);
        native.Decrypt(nonce, managedCiphertext, managedTag, decoded);
        CollectionAssert.AreEqual(plain, decoded);
    }

    [DataTestMethod]
    [DataRow(16)] // Ciphertext.
    [DataRow(39)] // Tag.
    public void ManagedAuthenticationFailureReleasesNoPlaintextAndPoisonsStream(int tamperOffset)
    {
        byte[] record = Convert.FromHexString(Records[0]);
        record[tamperOffset] ^= 1;
        using var wire = new MemoryStream(record.Concat(Convert.FromHexString(Records[0])).ToArray());
        using var stream = new ViiperEncryptedStream(wire, Key(), ViiperConnectionRole.Server, ViiperCipherImplementation.Managed);
        byte[] output = Enumerable.Repeat((byte)0x55, 8).ToArray();
        Assert.ThrowsException<AuthenticationTagMismatchException>(() => stream.Read(output));
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)0x55, 8).ToArray(), output);
        byte[] internalPlaintext = (byte[])typeof(ViiperEncryptedStream).GetField("plaintextReadBuffer",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(stream);
        Assert.IsTrue(internalPlaintext.All(value => value == 0));
        Assert.ThrowsException<IOException>(() => stream.Read(output));
        Assert.ThrowsException<IOException>(() => stream.Write(output));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void ManagedStreamStillRejectsCounterGapsAndReflectedDirection(int vector)
    {
        using var wire = new MemoryStream(Convert.FromHexString(Records[vector]));
        using var stream = new ViiperEncryptedStream(wire, Key(), ViiperConnectionRole.Server, ViiperCipherImplementation.Managed);
        Assert.ThrowsException<InvalidDataException>(() => stream.Read(new byte[8]));
        Assert.ThrowsException<IOException>(() => stream.Write(new byte[8]));
    }

    [TestMethod]
    public void ManagedDirectionsCanOperateConcurrentlyWithoutSharedCipherState()
    {
        using var cipher = new ViiperRecordCipher(Key(), ViiperCipherImplementation.Managed);
        Task encrypt = Task.Run(() =>
        {
            byte[] nonce = new byte[12], plain = new byte[24], encrypted = new byte[24], tag = new byte[16];
            using var independent = new ViiperRecordCipher(Key(), ViiperCipherImplementation.Managed);
            byte[] decoded = new byte[24];
            for (ulong index = 0; index < 256; index++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), index);
                plain.AsSpan().Fill((byte)index);
                cipher.Encrypt(nonce, plain, encrypted, tag);
                independent.Decrypt(nonce, encrypted, tag, decoded);
                CollectionAssert.AreEqual(plain, decoded);
            }
        });
        Task decrypt = Task.Run(() =>
        {
            byte[] nonce = new byte[12], plain = new byte[24], encrypted = new byte[24], tag = new byte[16], decoded = new byte[24];
            nonce[3] = 1;
            using var independent = new ViiperRecordCipher(Key(), ViiperCipherImplementation.Managed);
            for (ulong index = 0; index < 256; index++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), index);
                plain.AsSpan().Fill((byte)(index + 10));
                independent.Encrypt(nonce, plain, encrypted, tag);
                cipher.Decrypt(nonce, encrypted, tag, decoded);
                CollectionAssert.AreEqual(plain, decoded);
            }
        });
        Assert.IsTrue(Task.WaitAll(new[] { encrypt, decrypt }, 5000));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ManagedStreamAllocationIsBoundedAfterWarmup(bool reading)
    {
        using var wire = new MemoryStream(capacity: 128 * 1024);
        using var writer = new ViiperEncryptedStream(wire, Key(), ViiperConnectionRole.Client, ViiperCipherImplementation.Managed);
        byte[] payload = new byte[24];
        for (int index = 0; index < 1100; index++) writer.Write(payload, 0, payload.Length);
        wire.Position = 0;
        using var reader = new ViiperEncryptedStream(wire, Key(), ViiperConnectionRole.Server, ViiperCipherImplementation.Managed);
        if (reading) for (int index = 0; index < 100; index++) reader.Read(payload, 0, payload.Length);
        else wire.SetLength(0);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            if (reading) reader.Read(payload, 0, payload.Length);
            else writer.Write(payload, 0, payload.Length);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double elapsedMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Console.WriteLine($"Forced managed {(reading ? "read" : "write")}: {allocated} bytes / 1000 warmed 24-byte records " +
            $"({allocated / 1000.0:F2} bytes/record); {elapsedMilliseconds:F3} ms elapsed. " +
            "In-process diagnostic only, not controller latency or a cross-machine benchmark.");
        Assert.IsTrue(allocated <= 1024L * 1000,
            $"Managed provider allocated {allocated} bytes for 1000 warmed 24-byte records; retain bounded library bookkeeping, not whole-cipher/buffer allocation.");
    }

    [TestMethod]
    public void ManagedDisposeClearsOwnedKeyAndBuffersEvenWhenTransportThrows()
    {
        byte[] key = Key();
        var wire = new ThrowingDisposeStream();
        var stream = new ViiperEncryptedStream(wire, key, ViiperConnectionRole.Client, ViiperCipherImplementation.Managed);
        stream.Write(new byte[] { 1, 2, 3 });
        Assert.ThrowsException<IOException>(() => stream.Dispose());
        CollectionAssert.AreEqual(new byte[32], key);
        foreach (string field in new[] { "writeBuffer", "encryptedReadBuffer", "plaintextReadBuffer" })
            Assert.IsTrue(((byte[])typeof(ViiperEncryptedStream).GetField(field,
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(stream)).All(value => value == 0));
        stream.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => stream.Write(new byte[1]));
    }

    [DataTestMethod]
    [DataRow("encryptionGate")]
    [DataRow("decryptionGate")]
    public void ManagedDisposeDrainsEachDirectionBeforeRekeyingRetainedState(string direction)
    {
        using var cipher = new ViiperRecordCipher(Key(), ViiperCipherImplementation.Managed);
        object gate = typeof(ViiperRecordCipher).GetField(direction,
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cipher);
        FieldInfo disposed = typeof(ViiperRecordCipher).GetField("disposed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Task disposal = null;
        Monitor.Enter(gate);
        try
        {
            disposal = Task.Run(cipher.Dispose);
            Assert.IsTrue(SpinWait.SpinUntil(() => (int)disposed.GetValue(cipher) == 1, 2000));
            Assert.IsFalse(disposal.IsCompleted,
                "Admission seals first; key-state replacement must wait for the active direction.");
        }
        finally { Monitor.Exit(gate); }
        Assert.IsTrue(disposal.Wait(2000));
        Assert.ThrowsException<ObjectDisposedException>(() =>
            cipher.Encrypt(new byte[12], new byte[1], new byte[1], new byte[16]));
        Assert.ThrowsException<ObjectDisposedException>(() =>
            cipher.Decrypt(new byte[12], new byte[1], new byte[16], new byte[1]));
    }

    private sealed class ThrowingDisposeStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new IOException("Synthetic transport close failure.");
        }
    }
}
