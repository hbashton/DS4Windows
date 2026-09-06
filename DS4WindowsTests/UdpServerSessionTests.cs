using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using DS4Windows;

namespace DS4WindowsTests;

// Loopback sockets only. These tests do not open controllers, applications,
// configured DSU ports, or external interfaces.
[TestClass]
[DoNotParallelize]
public sealed class UdpServerSessionTests
{
    private const uint Version = 0x100000;
    private const uint Ports = 0x100001;
    private const uint Data = 0x100002;

    [DataTestMethod]
    [DataRow(Version, 24)]
    [DataRow(Ports, 32)]
    public async Task ControlReplyUsesExactDatagramLengthAndCrc(uint message, int length)
    {
        var server = new UdpServer(GetMeta);
        try
        {
            server.Start(0);
            using var client = Connect(server.CurrentSession);
            await Send(client, Request(message));
            byte[] reply = await Receive(client);
            ValidateReply(reply, length, message);
            if (message == Version)
                Assert.AreEqual((ushort)1001, BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(20)));
            else
            {
                Assert.AreEqual((byte)DsState.Connected, reply[21]);
                CollectionAssert.AreEqual(new byte[] { 2, 3, 4, 5, 6, 7 }, reply[24..30]);
            }
        }
        finally { server.Stop(); }
    }

    [TestMethod]
    public async Task StateReplyRetainsControlsMotionAndExactHundredBytePayload()
    {
        var server = new UdpServer(GetMeta);
        try
        {
            server.Start(0);
            UdpServerSession session = server.CurrentSession;
            using var client = Connect(session);
            // The actual request parser installs the subscription. Invoke it
            // synchronously here so packet publication has no timing race
            // with the subscription's (unacknowledged) network receive.
            Parse(session, Request(Data), (IPEndPoint)client.Client.LocalEndPoint);
            var state = new DS4State
            {
                Cross = true, L1 = true, LX = 37, R2 = 129,
                PacketCounter = 0x12345678, totalMicroSec = 9012,
            };
            state.Motion.accelXG = 1.25;
            state.Motion.angVelPitch = 123.5;
            DualShockPadMeta meta = default;
            GetMeta(0, ref meta);
            server.NewReportIncoming(ref meta, state, new byte[100]);
            byte[] reply = await Receive(client);
            ValidateReply(reply, 100, Data);
            Assert.AreEqual(state.PacketCounter, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(32)));
            Assert.AreEqual(state.LX, reply[40]);
            Assert.AreEqual((byte)255, reply[49]);
            Assert.AreEqual((byte)255, reply[53]);
            Assert.AreEqual(state.R2, reply[54]);
            Assert.AreEqual(state.totalMicroSec, BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(68)));
            Assert.AreEqual(1.25f, BitConverter.ToSingle(reply, 76));
            Assert.AreEqual(123.5f, BitConverter.ToSingle(reply, 88));
        }
        finally { server.Stop(); }
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(7)]
    public async Task MissingOrNonSixByteIdentityUsesZeroAddressWithoutFaultingReport(int addressLength)
    {
        var server = new UdpServer(GetMeta);
        try
        {
            server.Start(0);
            UdpServerSession session = server.CurrentSession;
            using var client = Connect(session);
            Parse(session, Request(Data), (IPEndPoint)client.Client.LocalEndPoint);
            // Also install a client interested only in a different MAC: its
            // selection must not attempt a dictionary lookup with a null key.
            using var unrelated = Connect(session);
            byte[] macRequest = Request(Data);
            macRequest[20] = 2;
            macRequest[22] = 9;
            BinaryPrimitives.WriteUInt32LittleEndian(macRequest.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(macRequest.AsSpan(8), ReferenceCrc32(macRequest));
            Parse(session, macRequest, (IPEndPoint)unrelated.Client.LocalEndPoint);
            var meta = new DualShockPadMeta
            {
                PadId = 0, PadState = DsState.Disconnected,
                PadMacAddress = addressLength < 0 ? null : new PhysicalAddress(new byte[addressLength]),
            };
            server.NewReportIncoming(ref meta, new DS4State(), new byte[100]);
            byte[] reply = await Receive(client);
            ValidateReply(reply, 100, Data);
            Assert.AreEqual((byte)DsState.Disconnected, reply[21]);
            CollectionAssert.AreEqual(new byte[6], reply[24..30]);
        }
        finally { server.Stop(); }
    }

    [TestMethod]
    public async Task RestartReplacesSessionAndDoesNotInheritClientSubscriptions()
    {
        var server = new UdpServer(GetMeta);
        try
        {
            server.Start(0);
            UdpServerSession oldSession = server.CurrentSession;
            using var oldClient = Connect(oldSession);
            Parse(oldSession, Request(Data), (IPEndPoint)oldClient.Client.LocalEndPoint);
            server.Start(0);
            UdpServerSession nextSession = server.CurrentSession;
            Assert.AreNotSame(oldSession, nextSession);
            Assert.IsFalse(oldSession.IsRunning);
            Assert.AreEqual(0, ClientCount(nextSession));

            // A delayed callback parsed against the old owner cannot register
            // that client on the new socket/session.
            Parse(oldSession, Request(Data), (IPEndPoint)oldClient.Client.LocalEndPoint);
            Assert.AreEqual(0, ClientCount(nextSession));
            DualShockPadMeta meta = default;
            GetMeta(0, ref meta);
            oldSession.NewReportIncoming(ref meta, new DS4State(), new byte[100]);

            using var nextClient = Connect(nextSession);
            await Send(nextClient, Request(Version));
            ValidateReply(await Receive(nextClient), 24, Version);
            Assert.AreEqual(0, ClientCount(nextSession));
            Assert.ThrowsException<InvalidOperationException>(() => oldSession.Start(0));
        }
        finally { server.Stop(); }
    }

    [TestMethod]
    public async Task BlockedOldReceiveHandlerCannotReplyThroughSuccessor()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        int calls = 0;
        void BlockingMeta(int index, ref DualShockPadMeta meta)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                try
                {
                    if (!release.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release old request handler.");
                }
                finally { exited.Set(); }
            }
            GetMeta(index, ref meta);
        }

        var server = new UdpServer(BlockingMeta);
        try
        {
            server.Start(0);
            UdpServerSession oldSession = server.CurrentSession;
            using var oldClient = Connect(oldSession);
            await Send(oldClient, Request(Ports));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
            server.Stop();
            server.Start(0);
            UdpServerSession nextSession = server.CurrentSession;
            using var nextClient = Connect(nextSession);
            await Send(nextClient, Request(Ports));
            ValidateReply(await Receive(nextClient), 32, Ports);
            release.Set();
            Assert.IsTrue(exited.Wait(TimeSpan.FromSeconds(3)));
            Assert.IsFalse(oldSession.IsRunning);
            Assert.AreSame(nextSession, server.CurrentSession);
            await Send(nextClient, Request(Version));
            ValidateReply(await Receive(nextClient), 24, Version);
        }
        finally
        {
            release.Set();
            server.Stop();
        }
    }

    [TestMethod]
    public async Task StopSupersedesStartStillPreparingItsSession()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        UdpServerSession created = null;
        var server = new UdpServer(GetMeta, getMeta =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Test did not release session creation.");
            return created = new UdpServerSession(getMeta);
        });
        Task start = null, stop = null;
        try
        {
            start = Task.Run(() => server.Start(0));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
            stop = Task.Run(server.Stop);
            Assert.IsTrue(SpinWait.SpinUntil(() => server.LifecycleVersion == 2, TimeSpan.FromSeconds(3)));
            release.Set();
            await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsNull(server.CurrentSession);
            Assert.IsNotNull(created);
            Assert.IsFalse(created.IsRunning);
        }
        finally
        {
            release.Set();
            if (start != null) await start.WaitAsync(TimeSpan.FromSeconds(3));
            if (stop != null) await stop.WaitAsync(TimeSpan.FromSeconds(3));
            server.Stop();
        }
    }

    [TestMethod]
    public async Task FailedBindRetiresOldSessionAndAllowsCleanRetry()
    {
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        occupied.ExclusiveAddressUse = true;
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var server = new UdpServer(GetMeta);
        try
        {
            server.Start(0);
            UdpServerSession oldSession = server.CurrentSession;
            Assert.ThrowsException<SocketException>(() => server.Start(((IPEndPoint)occupied.LocalEndPoint).Port));
            Assert.IsFalse(oldSession.IsRunning);
            Assert.IsNull(server.CurrentSession);
            server.Start(0);
            using var client = Connect(server.CurrentSession);
            await Send(client, Request(Version));
            ValidateReply(await Receive(client), 24, Version);
        }
        finally { server.Stop(); }
    }

    private static UdpClient Connect(UdpServerSession session)
    {
        var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(session.LocalEndPoint);
        return client;
    }

    private static async Task Send(UdpClient client, byte[] request) =>
        _ = await client.SendAsync(request, request.Length);

    private static async Task<byte[]> Receive(UdpClient client)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        return (await client.ReceiveAsync(cancel.Token)).Buffer;
    }

    private static byte[] Request(uint type)
    {
        byte[] packet = new byte[type == Ports ? 25 : type == Data ? 28 : 20];
        "DSUC"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), 1001);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)(packet.Length - 16));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 777);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), type);
        if (type == Ports)
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), ReferenceCrc32(packet));
        return packet;
    }

    private static void ValidateReply(byte[] packet, int length, uint type)
    {
        Assert.AreEqual(length, packet.Length);
        CollectionAssert.AreEqual("DSUS"u8.ToArray(), packet[..4]);
        Assert.AreEqual((ushort)1001, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)));
        Assert.AreEqual((ushort)(length - 16), BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)));
        Assert.AreEqual(type, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)));
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 0);
        Assert.AreEqual(crc, ReferenceCrc32(packet));
    }

    // Independent scalar reference: do not validate the production CRC using
    // the same optimized implementation or its global initialization state.
    private static uint ReferenceCrc32(ReadOnlySpan<byte> packet)
    {
        uint crc = 0xffffffff;
        foreach (byte value in packet)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        return ~crc;
    }

    private static void GetMeta(int index, ref DualShockPadMeta meta) => meta = new DualShockPadMeta
    {
        PadId = (byte)index, PadState = DsState.Connected, Model = DsModel.DS4,
        ConnectionType = DsConnection.Usb, IsActive = true,
        PadMacAddress = new PhysicalAddress(new byte[] { 2, 3, 4, 5, 6, 7 }),
        BatteryStatus = DsBattery.Full,
    };

    private static void Parse(UdpServerSession session, byte[] packet, IPEndPoint endpoint) =>
        typeof(UdpServerSession).GetMethod("ProcessIncoming", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(session, new object[] { packet, endpoint });

    private static int ClientCount(UdpServerSession session)
    {
        var clients = (System.Collections.IDictionary)typeof(UdpServerSession)
            .GetField("clients", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(session);
        lock (clients) return clients.Count;
    }
}
