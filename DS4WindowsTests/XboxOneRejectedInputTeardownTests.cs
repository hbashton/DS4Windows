using DS4Windows;
using DS4Windows.InputDevices;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class XboxOneRejectedInputTeardownTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExactFeedbackEofRevokesReadinessWithoutBreakingTeardownOwnership(bool intentionalStop)
    {
        using var pair = await TcpPair.CreateAsync();
        using var broker = NewBroker(pair.Client);
        await EnableBrokerAsync(broker, pair.Server.GetStream());
        var output = new ViiperOutDevice(OutContType.ViiperXboxOne, ViiperVirtualDeviceType.XboxOne);
        Set(output, "deviceStream", broker);
        Set(output, "streamGeneration", 7L);
        Set(output, "connected", true);
        Set(output, "feedbackDispatchStopRequested", false);
        Set(output, "writerStopRequested", intentionalStop);
        Set(output, "activeFeedbackLength", ControllerFeedbackFrame.SerializedLength);
        try
        {
            Invoke(output, "StartFeedbackReader");
            Thread reader = Get<Thread>(output, "feedbackThread");
            pair.Server.Client.Shutdown(SocketShutdown.Send);
            Assert.IsTrue(await Task.Run(() => reader.Join(2500)));
            Assert.AreEqual(!intentionalStop, output.HasRuntimeFault);
            Assert.AreEqual(intentionalStop, output.IsRuntimeConnected);
            Assert.IsTrue(Get<bool>(output, "connected"),
                "EOF readiness must not pre-clear ownership needed by exact terminal-Stop cleanup.");
            Set(output, "streamGeneration", 8L);
            Assert.IsFalse(output.HasRuntimeFault, "A new stream incarnation must not inherit old EOF state.");
        }
        finally
        {
            Set(output, "connected", false);
            Set(output, "deviceStream", null);
        }
    }

    [DataTestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(true, false, false, false)]
    [DataRow(false, true, false, false)]
    [DataRow(false, false, true, false)]
    [DataRow(true, false, false, true)]
    public async Task ProductionWriterRejectionPreservesExactFeedbackThroughDeviceRemoval(
        bool stopBeforeRejection, bool invalidStop, bool concurrentDisconnect,
        bool alreadyRemoved)
    {
        ControlService oldHub = DS4Windows.Program.rootHub;
        bool oldEnabled = Global.EnableOutputDataToDS4[0];
        byte oldBoost = Global.RumbleBoost[0];
        ViiperOutDevice output = null;
        ViiperDeviceStream broker = null;
        XboxOnePhysicalFeedbackSession session = null;
        Task competingDisconnect = null;
        using var removalEntered = new ManualResetEventSlim(false);
        using var stopAckObserved = new ManualResetEventSlim(false);
        using var allowRemovalExit = new ManualResetEventSlim(false);
        using var pair = await TcpPair.CreateAsync();
        int removals = 0, detaches = 0;
        bool openDuringRemoval = false, connectedDuringRemoval = false;
        bool inputStoppedDuringRemoval = false, removalFinishedAfterAck = false;
        bool preservedEarlyStop = false;
        bool removedExactIdentity = false;
        try
        {
            var target = new TestPhysicalDevice();
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            hub.DS4Controllers = new DS4Device[] { target };
            DS4Windows.Program.rootHub = hub;
            Global.EnableOutputDataToDS4[0] = true;
            Global.RumbleBoost[0] = 100;
            output = new ViiperOutDevice(OutContType.ViiperXboxOne, ViiperVirtualDeviceType.XboxOne);
            var lifetime = new ViiperVirtualDeviceLifetime(43, "10", 7,
                (busId, devId) =>
                {
                    Interlocked.Increment(ref removals);
                    removedExactIdentity = busId == 43 && devId == "10";
                    openDuringRemoval = !broker.IsTransportClosed;
                    connectedDuringRemoval = output.IsRuntimeConnected;
                    inputStoppedDuringRemoval = Get<bool>(output, "writerStopRequested");
                    preservedEarlyStop = Get<ManualResetEvent>(output,
                        "xboxOneTerminalFeedbackAcknowledged").WaitOne(0);
                    removalEntered.Set();
                    removalFinishedAfterAck = stopAckObserved.Wait(TimeSpan.FromSeconds(3));
                    allowRemovalExit.Wait(TimeSpan.FromSeconds(3));
                    if (alreadyRemoved)
                    {
                        throw new IOException("The exact one-shot registration was already removed.");
                    }
                },
                (_, _) => Interlocked.Increment(ref detaches), _ => { }, () => { });
            broker = new ViiperDeviceStream(pair.Client.GetStream(), pair.Client, lifetime);
            await EnableBrokerAsync(broker, pair.Server.GetStream());
            var binding = new XboxOneAuthorizedFeedbackBinding
            {
                Source = (byte)ControllerFeedbackSource.XboxOneVirtualDevice,
                PersonaGeneration = 1, DeviceGeneration = 5,
                TransportGeneration = 6, OwnershipEpoch = 7,
                TimeToLiveMicroseconds = 250_000,
            };
            Assert.IsTrue(output.TryCreateXboxOnePhysicalFeedbackSession(binding, target, 0, out session));
            Set(output, "lastInputDeviceIndex", 0);
            Set(output, "xboxOnePhysicalFeedbackSession", session);
            Set(output, "xboxOneFeedbackBinding", binding);
            Set(output, "deviceStream", broker);
            Set(output, "streamGeneration", 7L);
            Set(output, "stateWriterGeneration", 11L);
            Set(output, "orderedEgressOwnedPresentationGeneration", 1L);
            Set(output, "orderedEgressAdmissionGeneration", 1L);
            Set(output, "connected", true);
            Set(output, "feedbackDispatchStopRequested", false);
            Set(output, "activeFeedbackLength", ControllerFeedbackFrame.SerializedLength);
            Get<OrderedEgressWriterAdmissionGate>(output, "orderedEgressWriterAdmissionGate").Activate(11, 1, 1);
            var scheduler = Get<XboxOneEgressScheduler>(output, "xboxOneEgressScheduler");
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(scheduler.CurrentProducerEpoch,
                    new XboxOneEgressState(XboxOneEgressState.AButton, 0, 0, 0, 0, 0, 0),
                    Stopwatch.GetTimestamp()));
            Invoke(output, "StartFeedbackReader");
            Invoke(output, "StartStateWriter", 11L);
            Thread writer = Get<Thread>(output, "stateWriterThread");
            Get<AutoResetEvent>(output, "writerSignal").Set();
            NetworkStream remote = pair.Server.GetStream();
            byte[] input = await ReadExactlyAsync(remote, 16 + XboxOneEgressState.WireSize);
            AssertHeader(input, 0x02, 2, XboxOneEgressState.WireSize);
            await remote.WriteAsync(BrokerFrame(0x82, 2, new byte[] { 1 }));

            await remote.WriteAsync(BrokerFrame(0x83, 10, Feedback(1, ControllerFeedbackCommand.Apply)));
            byte[] applyAck = await ReadExactlyAsync(remote, 17);
            AssertHeader(applyAck, 0x03, 10, 1);
            Assert.AreEqual((byte)1, applyAck[16]);
            Assert.IsTrue(target.LastHeavy != 0 || target.LastLight != 0,
                "The real canonical physical-state owner must have accepted Apply.");
            Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
                scheduler.Publish(scheduler.CurrentProducerEpoch,
                    new XboxOneEgressState(XboxOneEgressState.BButton, 0, 0, 0, 0, 0, 0),
                    Stopwatch.GetTimestamp()));
            Get<AutoResetEvent>(output, "writerSignal").Set();
            input = await ReadExactlyAsync(remote, 16 + XboxOneEgressState.WireSize);
            AssertHeader(input, 0x02, 3, XboxOneEgressState.WireSize);

            if (stopBeforeRejection)
            {
                await remote.WriteAsync(BrokerFrame(0x83, 11, Feedback(2, ControllerFeedbackCommand.Stop)));
                byte[] earlyAck = await ReadExactlyAsync(remote, 17);
                AssertHeader(earlyAck, 0x03, 11, 1);
                Assert.AreEqual((byte)1, earlyAck[16]);
                Assert.IsTrue(Get<ManualResetEvent>(output, "xboxOneTerminalFeedbackAcknowledged").WaitOne(1000));
                stopAckObserved.Set();
            }

            await remote.WriteAsync(BrokerFrame(0x82, 3, new byte[] { 0 }));
            Assert.IsTrue(await Task.Run(() => removalEntered.Wait(TimeSpan.FromSeconds(2))));
            Assert.IsTrue(broker.IsXboxOneInputRejected);
            Assert.IsFalse(broker.IsTransportClosed);
            Assert.IsTrue(openDuringRemoval);
            Assert.IsTrue(connectedDuringRemoval, "Do not pre-clear connected before terminal feedback delivery.");
            Assert.IsTrue(inputStoppedDuringRemoval);
            byte[] anotherInput = new byte[XboxOneEgressState.WireSize];
            XboxOneSemanticInputRejectedException rejected = Assert.ThrowsException<XboxOneSemanticInputRejectedException>(
                () => broker.WriteXboxOneInputAndWaitForAck(anotherInput, anotherInput.Length));
            Assert.AreEqual(3ul, rejected.Revision);
            Invoke(output, "EnsureStateWriterAlive");
            Assert.AreSame(writer, Get<Thread>(output, "stateWriterThread"), "Input failure must not restart a writer.");

            if (concurrentDisconnect)
            {
                long retiringWriterGeneration = Get<long>(output, "stateWriterGeneration");
                competingDisconnect = Task.Run(output.Disconnect);
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                    Get<long>(output, "stateWriterGeneration") != retiringWriterGeneration,
                    TimeSpan.FromSeconds(2)));
                Assert.IsFalse(competingDisconnect.IsCompleted,
                    "The external lifecycle owner must join the failed writer before replacing its stream.");
                Assert.AreSame(broker, Get<ViiperDeviceStream>(output, "deviceStream"));
                Assert.IsFalse(broker.IsTransportClosed);
            }

            if (!stopBeforeRejection)
            {
                await remote.WriteAsync(BrokerFrame(0x83, 11,
                    Feedback(2, ControllerFeedbackCommand.Stop, invalidStop ? 55ul : 5ul)));
                byte[] stopAck = await ReadExactlyAsync(remote, 17);
                AssertHeader(stopAck, 0x03, 11, 1);
                Assert.AreEqual(invalidStop ? (byte)0 : (byte)1, stopAck[16],
                    "Teardown cannot bypass exact canonical ownership to claim success.");
                stopAckObserved.Set();
            }
            allowRemovalExit.Set();
            Assert.IsTrue(await Task.Run(() => writer.Join(2500)), "The failure owner must finish bounded teardown.");
            if (competingDisconnect != null)
            {
                await competingDisconnect.WaitAsync(TimeSpan.FromSeconds(2));
            }
            Assert.IsTrue(removalFinishedAfterAck);
            Assert.AreEqual(stopBeforeRejection, preservedEarlyStop,
                "An already acknowledged Stop for this incarnation must not be reset during Disconnect.");
            Assert.IsFalse(output.IsRuntimeConnected);
            Assert.IsTrue(broker.IsTransportClosed);
            Assert.AreEqual(1, Volatile.Read(ref removals));
            Assert.IsTrue(removedExactIdentity);
            Assert.AreEqual(1, Volatile.Read(ref detaches));
            lifetime.Dispose();
            Assert.AreEqual(1, Volatile.Read(ref removals),
                "An already-removed registration must not trigger another removal or discovery of a successor.");
            Assert.AreEqual((byte)0, target.LastHeavy);
            Assert.AreEqual((byte)0, target.LastLight);
            Assert.AreEqual(!invalidStop,
                Get<ManualResetEvent>(output, "xboxOneTerminalFeedbackAcknowledged").WaitOne(0));
            Assert.AreEqual(1L, Get<long>(output, "writtenPacketCount"),
                "Only the first accepted input is committed; the rejected successor is not.");
        }
        finally
        {
            stopAckObserved.Set();
            allowRemovalExit.Set();
            broker?.CloseTransport();
            output?.Disconnect();
            if (competingDisconnect != null)
            {
                await competingDisconnect.WaitAsync(TimeSpan.FromSeconds(3));
            }
            session?.TryRetire();
            broker?.Dispose();
            DS4Windows.Program.rootHub = oldHub;
            Global.EnableOutputDataToDS4[0] = oldEnabled;
            Global.RumbleBoost[0] = oldBoost;
        }
    }

    [TestMethod]
    public async Task RejectedInputKeepsBrokerAckCapableButRejectsEverySuccessorWrite()
    {
        using var pair = await TcpPair.CreateAsync();
        using var broker = NewBroker(pair.Client);
        NetworkStream remote = pair.Server.GetStream();
        await EnableBrokerAsync(broker, remote);
        byte[] input = new byte[XboxOneEgressState.WireSize];
        Task<ViiperFrameWriteTiming> write = Task.Run(() => broker.WriteXboxOneInputAndWaitForAck(input, input.Length));
        AssertHeader(await ReadExactlyAsync(remote, 16 + input.Length), 0x02, 2, input.Length);
        broker.AcceptXboxOneInputAck(2, 0);
        XboxOneSemanticInputRejectedException rejection = await Assert.ThrowsExceptionAsync<XboxOneSemanticInputRejectedException>(
            async () => await write.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(2ul, rejection.Revision);
        Assert.IsFalse(broker.IsTransportClosed);
        Assert.IsTrue(broker.IsXboxOneInputRejected);
        Assert.ThrowsException<XboxOneSemanticInputRejectedException>(() => broker.WriteXboxOneInputAndWaitForAck(input, input.Length));
        broker.AcknowledgeXboxOneFeedback(17, true);
        byte[] ack = await ReadExactlyAsync(remote, 17);
        AssertHeader(ack, 0x03, 17, 1);
        Assert.AreEqual((byte)1, ack[16], "No successor semantic-input bytes may precede the canonical ACK.");
    }

    [DataTestMethod]
    [DataRow(3ul, (byte)0)]
    [DataRow(2ul, (byte)2)]
    public async Task WrongCorrelationOrStatusStillClosesTransport(ulong correlation, byte status)
    {
        using var pair = await TcpPair.CreateAsync();
        using var broker = NewBroker(pair.Client);
        await EnableBrokerAsync(broker, pair.Server.GetStream());
        byte[] input = new byte[XboxOneEgressState.WireSize];
        Task<ViiperFrameWriteTiming> write = Task.Run(() => broker.WriteXboxOneInputAndWaitForAck(input, input.Length));
        await ReadExactlyAsync(pair.Server.GetStream(), 16 + input.Length);
        Assert.ThrowsException<IOException>(() => broker.AcceptXboxOneInputAck(correlation, status));
        await Assert.ThrowsExceptionAsync<IOException>(async () => await write.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(broker.IsTransportClosed);
        Assert.IsFalse(broker.IsXboxOneInputRejected, "Malformed ACK is not a drain-capable explicit rejection.");
    }

    [TestMethod]
    public async Task AmbiguousInputAckTimeoutStillClosesTransport()
    {
        using var pair = await TcpPair.CreateAsync();
        using var broker = NewBroker(pair.Client);
        await EnableBrokerAsync(broker, pair.Server.GetStream());
        byte[] input = new byte[XboxOneEgressState.WireSize];
        Task<ViiperFrameWriteTiming> write = Task.Run(() => broker.WriteXboxOneInputAndWaitForAck(input, input.Length));
        await ReadExactlyAsync(pair.Server.GetStream(), 16 + input.Length);
        await Assert.ThrowsExceptionAsync<IOException>(async () => await write.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(broker.IsTransportClosed);
        Assert.IsFalse(broker.IsXboxOneInputRejected);
    }

    [TestMethod]
    public async Task TransportLossStillFailsThePendingInputWithoutEnteringRejectionDrain()
    {
        using var pair = await TcpPair.CreateAsync();
        using var broker = NewBroker(pair.Client);
        await EnableBrokerAsync(broker, pair.Server.GetStream());
        byte[] input = new byte[XboxOneEgressState.WireSize];
        Task<ViiperFrameWriteTiming> write = Task.Run(() => broker.WriteXboxOneInputAndWaitForAck(input, input.Length));
        await ReadExactlyAsync(pair.Server.GetStream(), 16 + input.Length);
        broker.CloseTransport();
        await Assert.ThrowsExceptionAsync<IOException>(async () => await write.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(broker.IsTransportClosed);
        Assert.IsFalse(broker.IsXboxOneInputRejected);
    }

    [TestMethod]
    public async Task DuplicateAckAfterInputCompletionClosesTransport()
    {
        using var pair = await TcpPair.CreateAsync();
        using var broker = NewBroker(pair.Client);
        await EnableBrokerAsync(broker, pair.Server.GetStream());
        byte[] input = new byte[XboxOneEgressState.WireSize];
        Task<ViiperFrameWriteTiming> write = Task.Run(() => broker.WriteXboxOneInputAndWaitForAck(input, input.Length));
        await ReadExactlyAsync(pair.Server.GetStream(), 16 + input.Length);
        broker.AcceptXboxOneInputAck(2, 1);
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.ThrowsException<IOException>(() => broker.AcceptXboxOneInputAck(2, 0));
        Assert.IsTrue(broker.IsTransportClosed);
        Assert.IsFalse(broker.IsXboxOneInputRejected);
    }

    [TestMethod]
    public void StaleRejectedWriterAndTerminalCallbackCannotRetireOrSignalSuccessor()
    {
        var output = new ViiperOutDevice(OutContType.ViiperXboxOne, ViiperVirtualDeviceType.XboxOne);
        using var oldBytes = new MemoryStream();
        using var newBytes = new MemoryStream();
        using var oldStream = new ViiperDeviceStream(oldBytes, oldBytes, NoHardwareLifetime());
        using var successor = new ViiperDeviceStream(newBytes, newBytes, NoHardwareLifetime());
        Set(oldStream, "xboxOneRejectedInputRevision", 2L);
        Set(output, "connected", true);
        Set(output, "feedbackDispatchStopRequested", false);
        Set(output, "deviceStream", successor);
        Set(output, "streamGeneration", 8L);
        Set(output, "stateWriterGeneration", 12L);
        Set(output, "stateWriterThreadGeneration", 12L);
        Set(output, "stateWriterThread", Thread.CurrentThread);
        Invoke(output, "StopXboxOneRejectedInput", oldStream, 7L, 11L, "stale rejection");
        Invoke(output, "StopXboxOneRejectedInput", oldStream, 8L, 12L, "foreign stream rejection");
        Invoke(output, "MarkXboxOneRuntimeStreamFault", oldStream, 7L);
        Invoke(output, "MarkXboxOneRuntimeStreamFault", oldStream, 8L);
        Assert.IsFalse(output.HasRuntimeFault);
        Assert.IsTrue(output.IsRuntimeConnected);
        Assert.IsFalse(Get<bool>(output, "writerStopRequested"));
        Assert.IsFalse(successor.IsTransportClosed);
        byte[] stop = Feedback(2, ControllerFeedbackCommand.Stop);
        Invoke(output, "OnXboxOneFeedbackDispatchCompleted", oldStream, 7L,
            stop, stop.Length, 11ul, true, true);
        Invoke(output, "OnXboxOneFeedbackDispatchCompleted", oldStream, 8L,
            stop, stop.Length, 11ul, true, true);
        Assert.IsFalse(Get<ManualResetEvent>(output, "xboxOneTerminalFeedbackAcknowledged").WaitOne(0));
        Set(output, "connected", false);
        Set(output, "deviceStream", null);
    }

    private static ViiperDeviceStream NewBroker(TcpClient client) =>
        new(client.GetStream(), client, NoHardwareLifetime());

    private static ViiperVirtualDeviceLifetime NoHardwareLifetime() =>
        new(43, "10", -1, (_, _) => { }, (_, _) => { }, _ => { }, () => { });

    private static async Task EnableBrokerAsync(ViiperDeviceStream broker, Stream remote)
    {
        Task enable = Task.Run(broker.EnableXboxOneBroker);
        AssertHeader(await ReadExactlyAsync(remote, 16), 0x01, 0, 0);
        await remote.WriteAsync(BrokerFrame(0x81, 0, Array.Empty<byte>()));
        await enable.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static byte[] Feedback(ulong sequence, ControllerFeedbackCommand command, ulong generation = 5)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        bool apply = command == ControllerFeedbackCommand.Apply;
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(ControllerFeedbackSource.XboxOneVirtualDevice,
            command, ControllerFeedbackActuators.All, apply ? (ushort)2570 : (ushort)0,
            apply ? (ushort)5140 : (ushort)0, 0, 0, sequence, generation, 6, 7, now, 250_000, out var frame));
        byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
        Assert.IsTrue(frame.TryWriteTo(wire));
        return wire;
    }

    private static byte[] BrokerFrame(byte type, ulong correlation, byte[] payload)
    {
        byte[] wire = new byte[16 + payload.Length];
        "X1BR"u8.CopyTo(wire);
        wire[4] = 1;
        wire[5] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(6), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(8), correlation);
        payload.CopyTo(wire, 16);
        return wire;
    }

    private static void AssertHeader(byte[] wire, byte type, ulong correlation, int payloadLength)
    {
        CollectionAssert.AreEqual("X1BR"u8.ToArray(), wire.Take(4).ToArray());
        Assert.AreEqual((byte)1, wire[4]);
        Assert.AreEqual(type, wire[5]);
        Assert.AreEqual((ushort)payloadLength, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(6)));
        Assert.AreEqual(correlation, BinaryPrimitives.ReadUInt64LittleEndian(wire.AsSpan(8)));
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length)
    {
        byte[] result = new byte[length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await stream.ReadExactlyAsync(result, timeout.Token);
        return result;
    }

    private static T Get<T>(object instance, string field) =>
        (T)instance.GetType().GetField(field, PrivateInstance).GetValue(instance);
    private static void Set(object instance, string field, object value) =>
        instance.GetType().GetField(field, PrivateInstance).SetValue(instance, value);
    private static void Invoke(object instance, string method, params object[] values) =>
        instance.GetType().GetMethod(method, PrivateInstance).Invoke(instance, values);

    private sealed class TestPhysicalDevice : DS4Device
    {
        internal byte LastLight;
        internal byte LastHeavy;
        internal TestPhysicalDevice() : base("Xbox rejection test controller", InputDeviceType.DS4, ConnectionType.USB) { }
        public override void setRumble(byte rightLightFastMotor, byte leftHeavySlowMotor)
        {
            LastLight = rightLightFastMotor;
            LastHeavy = leftHeavySlowMotor;
        }
    }

    private sealed class TcpPair : IDisposable
    {
        internal TcpClient Client { get; private init; }
        internal TcpClient Server { get; private init; }
        internal static async Task<TcpPair> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient { NoDelay = true };
            try
            {
                Task<TcpClient> accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                TcpClient server = await accept.WaitAsync(TimeSpan.FromSeconds(2));
                server.NoDelay = true;
                return new TcpPair { Client = client, Server = server };
            }
            catch { client.Dispose(); throw; }
            finally { listener.Stop(); }
        }
        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }
}
