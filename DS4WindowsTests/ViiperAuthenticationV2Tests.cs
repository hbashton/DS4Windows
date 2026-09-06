using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class ViiperAuthenticationV2Tests
{
    // Independently calculated with Node/OpenSSL, also consumed by the Go tests.
    private static readonly string[] Records =
    {
        "0000002400000000000000000000000018b94032d266582e05ebcfe4ba88b8a24dd1043e6dcd23fb",
        "00000024000000000000000000000001695d7eda4e8a46850d10d0c85e47680d9f125be025e25461",
        "00000024000000010000000000000000ab479fea760618c3be9f8fd13269fd4b4fc440460493639f",
        "00000024000000010000000000000001e10be70c805489fbcb0d12b623a633fd8e2ad6a2e57a58c0"
    };

    private static byte[] Key() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static byte[] Payload() => Convert.FromHexString("000102037f80feff");

    [TestMethod]
    public void SessionKeyMatchesIndependentV2ContextAndNonceOrder()
    {
        CollectionAssert.AreEqual(Convert.FromHexString("71424901662650fb5c29ce71795ba055f114d4701da55490fadd27f82398f00c"),
            ViiperAuthentication.DeriveSessionKey(Key(), Enumerable.Repeat((byte)0xa5, 32).ToArray(),
                Enumerable.Repeat((byte)0x5a, 32).ToArray()));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void BothDirectionsMatchIndependentVectors(int roleNumber)
    {
        using MemoryStream wire = new();
        using ViiperEncryptedStream writer = new(wire, Key(), (ViiperConnectionRole)roleNumber);
        for (int counter = 0; counter < 2; counter++)
        {
            wire.SetLength(0);
            writer.Write(Payload());
            CollectionAssert.AreEqual(Convert.FromHexString(Records[(roleNumber - 1) * 2 + counter]), wire.ToArray());
        }

        byte[] oppositeRecords = Convert.FromHexString(Records[(2 - roleNumber) * 2] + Records[(2 - roleNumber) * 2 + 1]);
        using MemoryStream incoming = new(oppositeRecords);
        using ViiperEncryptedStream reader = new(incoming, Key(), (ViiperConnectionRole)roleNumber);
        byte[] actual = new byte[16];
        for (int i = 0; i < actual.Length; i++) Assert.AreEqual(1, reader.Read(actual, i, 1));
        CollectionAssert.AreEqual(Payload().Concat(Payload()).ToArray(), actual);
    }

    [DataTestMethod]
    [DataRow(1)] // Client counter gap.
    [DataRow(2)] // Reflected server output.
    public void InvalidDirectionOrSequenceFaultsBothHalves(int vector)
    {
        using MemoryStream wire = new(Convert.FromHexString(Records[vector] + Records[0]));
        using ViiperEncryptedStream stream = new(wire, Key(), ViiperConnectionRole.Server);
        byte[] output = Enumerable.Repeat((byte)0x55, 8).ToArray();
        Assert.ThrowsException<InvalidDataException>(() => stream.Read(output));
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)0x55, 8).ToArray(), output);
        Assert.ThrowsException<IOException>(() => stream.Read(output));
        Assert.ThrowsException<IOException>(() => stream.Write(output));
    }

    [TestMethod]
    public void TamperedTagDeliversNothingAndCannotResume()
    {
        byte[] record = Convert.FromHexString(Records[0]);
        record[^1] ^= 1;
        using MemoryStream wire = new(record.Concat(Convert.FromHexString(Records[0])).ToArray());
        using ViiperEncryptedStream stream = new(wire, Key(), ViiperConnectionRole.Server);
        byte[] output = Enumerable.Repeat((byte)0x55, 8).ToArray();
        Assert.ThrowsException<AuthenticationTagMismatchException>(() => stream.Read(output));
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)0x55, 8).ToArray(), output);
        Assert.ThrowsException<IOException>(() => stream.Read(output));
        Assert.AreEqual(0UL, GetCounter(stream, "receiveCounter"));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(39)]
    public void TruncationIsNotCleanEof(int length)
    {
        using MemoryStream wire = new(Convert.FromHexString(Records[0]).Take(length).ToArray());
        using ViiperEncryptedStream stream = new(wire, Key(), ViiperConnectionRole.Server);
        Assert.ThrowsException<EndOfStreamException>(() => stream.Read(new byte[8]));
        Assert.ThrowsException<IOException>(() => stream.Read(new byte[8]));
    }

    [TestMethod]
    public void CounterExhaustionAndInvalidRoleAreRejected()
    {
        using MemoryStream wire = new();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ViiperEncryptedStream(wire, Key(), 0));
        using ViiperEncryptedStream stream = new(wire, Key(), ViiperConnectionRole.Client);
        typeof(ViiperEncryptedStream).GetField("sendCounter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(stream, ulong.MaxValue);
        Assert.ThrowsException<IOException>(() => stream.Write(new byte[1]));
        Assert.AreEqual(0L, wire.Length);
    }

    [TestMethod]
    public void EmptyOperationsDoNotTouchTheTransport()
    {
        using MemoryStream wire = new();
        using ViiperEncryptedStream stream = new(wire, Key(), ViiperConnectionRole.Client);
        stream.Write(Array.Empty<byte>());
        Assert.AreEqual(0, stream.Read(Array.Empty<byte>()));
        Assert.AreEqual(0L, wire.Length);
        Assert.AreEqual(0UL, GetCounter(stream, "sendCounter"));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => stream.Write(new byte[2 * 1024 * 1024]));
    }

    [TestMethod]
    public void WarmedEncryptedFeedbackReadsAllocateZero()
    {
        using MemoryStream wire = new(capacity: 128 * 1024);
        using ViiperEncryptedStream writer = new(wire, Key(), ViiperConnectionRole.Server);
        byte[] report = new byte[24];
        for (int i = 0; i < 1100; i++) writer.Write(report, 0, report.Length);
        wire.Position = 0;
        using ViiperEncryptedStream reader = new(wire, Key(), ViiperConnectionRole.Client);
        for (int i = 0; i < 100; i++) reader.Read(report, 0, report.Length);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int i = 0; i < 1000; i++) total += reader.Read(report, 0, report.Length);
        long allocations = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(24000, total);
        Assert.AreEqual(0L, allocations, "Feedback decryption must retain warmed allocation-free operation.");
    }

    [TestMethod]
    public async Task SimultaneousLoopbackDirectionsPreserveEveryRecord()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient server = await accept;
        client.NoDelay = server.NoDelay = true;
        using ViiperEncryptedStream clientStream = new(client.GetStream(), Key(), ViiperConnectionRole.Client);
        using ViiperEncryptedStream serverStream = new(server.GetStream(), Key(), ViiperConnectionRole.Server);
        static void Send(Stream stream, uint direction)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), direction);
            for (uint i = 0; i < 256; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), i);
                stream.Write(bytes);
            }
        }
        static void Receive(Stream stream, uint direction)
        {
            byte[] bytes = new byte[8];
            for (uint i = 0; i < 256; i++)
            {
                stream.ReadExactly(bytes);
                Assert.AreEqual(direction, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)));
                Assert.AreEqual(i, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)));
            }
        }
        await Task.WhenAll(Task.Run(() => Send(clientStream, 0)), Task.Run(() => Send(serverStream, 1)),
            Task.Run(() => Receive(clientStream, 1)), Task.Run(() => Receive(serverStream, 0)))
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ulong GetCounter(ViiperEncryptedStream stream, string name) =>
        (ulong)typeof(ViiperEncryptedStream).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream)!;
}
