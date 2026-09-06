using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class ViiperAuthenticationTests
{
    [TestMethod]
    public void DeriveKeyMatchesViiperGoServerVector()
    {
        byte[] expected =
        {
            0x94, 0x50, 0x29, 0x55, 0x01, 0xd7, 0x03, 0x0f,
            0x04, 0x61, 0x0f, 0x81, 0x6a, 0xdf, 0x43, 0x1c,
            0xaf, 0x8f, 0xc8, 0x21, 0xd4, 0xc1, 0x2f, 0x2f,
            0x21, 0x2c, 0x1b, 0xf8, 0x64, 0x46, 0x09, 0x82,
        };

        CollectionAssert.AreEqual(expected,
            ViiperAuthentication.DeriveKey("password123"));
    }

    [TestMethod]
    public void EncryptedStreamRoundTripsOrderedRecords()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        using MemoryStream wire = new(capacity: 4096);
        using (ViiperEncryptedStream writer = new(wire,
            (byte[])key.Clone(), ViiperConnectionRole.Client))
        {
            writer.Write(new byte[] { 1, 2, 3 }, 0, 3);
            writer.Write(new byte[] { 4, 5, 6, 7 }, 0, 4);
            wire.Position = 0;

            // Do not dispose the reader before the assertions: both wrappers
            // intentionally own the same synthetic transport in this test.
            ViiperEncryptedStream reader = new(wire, (byte[])key.Clone(), ViiperConnectionRole.Server);
            byte[] actual = new byte[7];
            int offset = 0;
            while (offset < actual.Length)
            {
                offset += reader.Read(actual, offset,
                    actual.Length - offset);
            }
            CollectionAssert.AreEqual(
                new byte[] { 1, 2, 3, 4, 5, 6, 7 }, actual);
        }
        CryptographicOperations.ZeroMemory(key);
    }

    [TestMethod]
    public void EncryptedStreamRejectsReplayedNonce()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] firstRecord;
        using (MemoryStream firstWire = new())
        {
            using ViiperEncryptedStream writer = new(firstWire,
                (byte[])key.Clone(), ViiperConnectionRole.Client);
            writer.Write(new byte[] { 0x5a }, 0, 1);
            firstRecord = firstWire.ToArray();
        }

        using MemoryStream replayedWire = new(capacity:
            firstRecord.Length * 2);
        replayedWire.Write(firstRecord);
        replayedWire.Write(firstRecord);
        replayedWire.Position = 0;
        using ViiperEncryptedStream reader = new(replayedWire,
            (byte[])key.Clone(), ViiperConnectionRole.Server);
        byte[] value = new byte[1];
        Assert.AreEqual(1, reader.Read(value, 0, 1));
        Assert.ThrowsException<InvalidDataException>(() =>
            reader.Read(value, 0, 1));
        CryptographicOperations.ZeroMemory(key);
    }

    [TestMethod]
    public void EncryptedControllerWritesAllocateZeroAfterWarmup()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        using MemoryStream wire = new(capacity: 128 * 1024);
        using ViiperEncryptedStream writer = new(wire,
            (byte[])key.Clone(), ViiperConnectionRole.Client);
        byte[] input = new byte[24];
        for (int index = 0; index < 100; index++)
        {
            writer.Write(input, 0, input.Length);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
        {
            writer.Write(input, 0, input.Length);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated,
            "Authenticated 24-byte Xbox input writes must remain allocation-free after warm-up.");
        CryptographicOperations.ZeroMemory(key);
    }

    [TestMethod]
    public void EncryptedStreamClearsOwnedKeyEvenWhenTransportDisposeThrows()
    {
        byte[] key = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        ThrowingDisposeStream transport = new();
        ViiperEncryptedStream stream = new(transport, key, ViiperConnectionRole.Client);

        Assert.ThrowsException<IOException>(() => stream.Dispose());
        CollectionAssert.AreEqual(new byte[32], key,
            "A failed transport close must not retain the connection-owned session key.");
        stream.Dispose();
        Assert.AreEqual(1, transport.DisposeCalls);
        Assert.ThrowsException<ObjectDisposedException>(() =>
            stream.Write(new byte[1], 0, 1));
    }

    private sealed class ThrowingDisposeStream : MemoryStream
    {
        internal int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCalls++;
            base.Dispose(disposing);
            throw new IOException("Synthetic transport close failure");
        }
    }
}
