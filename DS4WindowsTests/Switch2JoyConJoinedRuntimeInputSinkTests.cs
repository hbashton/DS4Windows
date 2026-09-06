using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class Switch2JoyConJoinedRuntimeInputSinkTests
{
    private const ulong RuntimeGeneration = 301;
    private const ulong PairEpoch = 401;
    private const ulong LeftDeviceGeneration = 101;
    private const ulong LeftTransportGeneration = 201;
    private const ulong RightDeviceGeneration = 102;
    private const ulong RightTransportGeneration = 202;
    private const long QpcFrequency = 1_000_000;
    private const int TimeoutMilliseconds = 1_000;
    private const Switch2GattProperty InputProperties =
        Switch2GattProperty.Read | Switch2GattProperty.Notify;

    [TestMethod]
    public void BothHalvesObserveTheJoinedOutputTransitionScope()
    {
        CreateBound(new Switch2JoyConPairPolicy(50),
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var runtime, out _, out var left, out var right);
        runtime.StartUpdate();
        bool observed = false;
        runtime.queueEvent(() => runtime.RunVirtualOutputTransition(() =>
            observed = ((ISwitch2BluetoothCanonicalInputSink)sink).IsVirtualOutputTransitionActive));
        sink.PublishJoyCon(Frame(left, counter: 1, timestamp: 100));
        sink.PublishJoyCon(Frame(right, counter: 1, timestamp: 110));
        Assert.IsTrue(observed);
        Assert.IsFalse(((ISwitch2BluetoothCanonicalInputSink)sink).IsVirtualOutputTransitionActive);
    }

    [TestMethod]
    public void WaitingAndStaleAreSuccessfulStateOnlyTransactions()
    {
        CreateBound(new Switch2JoyConPairPolicy(50),
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var runtime, out _, out var left, out var right);
        int regular = 0;
        runtime.Report += (_, args) => regular +=
            ((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.Regular ? 1 : 0;
        runtime.StartUpdate();

        sink.PublishJoyCon(Frame(left, counter: 1, timestamp: 100));
        sink.PublishJoyCon(Frame(right, counter: 1, timestamp: 300));

        Assert.AreEqual(2L, sink.ConsumedCount);
        Assert.AreEqual(2L, sink.StateOnlyCount);
        Assert.AreEqual(0L, sink.PublishedCount);
        Assert.AreEqual(0, regular);
        Assert.AreEqual(Switch2JoyConPairDisposition.StaleHalf,
            sink.LastPairDisposition);
        Assert.IsTrue(sink.HasStagedLeft);
        Assert.IsTrue(sink.HasStagedRight);
        Assert.IsFalse(sink.MapperHasAcceptedLeft);
        Assert.IsFalse(sink.MapperHasAcceptedRight);

        sink.PublishJoyCon(Frame(left, counter: 2, timestamp: 310));

        Assert.AreEqual(3L, sink.ConsumedCount);
        Assert.AreEqual(2L, sink.StateOnlyCount);
        Assert.AreEqual(1L, sink.PublishedCount);
        Assert.AreEqual(1, regular);
        Assert.IsTrue(sink.MapperHasAcceptedLeft);
        Assert.IsTrue(sink.MapperHasAcceptedRight);
        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.None,
            sink.LastFailure);
    }

    [TestMethod]
    public void UnboundFactoryFencesBothDescriptorsAndCredentialIssuer()
    {
        Switch2InputSessionDescriptor left = Descriptor(
            Switch2ControllerModel.JoyCon2Left, LeftDeviceGeneration,
            LeftTransportGeneration);
        Switch2InputSessionDescriptor right = Descriptor(
            Switch2ControllerModel.JoyCon2Right, RightDeviceGeneration,
            RightTransportGeneration);
        Switch2RuntimeInputDevice runtime = Runtime();
        Assert.IsTrue(Switch2JoyConJoinedRuntimeInputSink.TryCreateUnbound(
            PairEpoch, LeftDeviceGeneration, LeftTransportGeneration,
            RightDeviceGeneration, RightTransportGeneration, runtime,
            new Switch2JoyConPairPolicy(1_000), TimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var binding, out _, out var createFailure),
            createFailure.ToString());
        Switch2RuntimeInputDevice otherRuntime = Runtime(
            runtimeGeneration: RuntimeGeneration + 1);
        Assert.IsTrue(Switch2JoyConJoinedRuntimeInputSink.TryCreateUnbound(
            PairEpoch, LeftDeviceGeneration, LeftTransportGeneration,
            RightDeviceGeneration, RightTransportGeneration, otherRuntime,
            new Switch2JoyConPairPolicy(1_000), TimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out _,
            out var crossBinding, out _, out _));

        Assert.IsFalse(sink.TryBindDescriptors(crossBinding, left, right,
            out var crossFailure));
        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.
            DescriptorMismatch, crossFailure);
        Assert.IsFalse(sink.DescriptorBound);

        Switch2InputSessionDescriptor forgedRight = Descriptor(
            Switch2ControllerModel.JoyCon2Right, RightDeviceGeneration,
            RightTransportGeneration + 1);
        Assert.IsFalse(sink.TryBindDescriptors(binding, left, forgedRight,
            out var forgedFailure));
        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.
            DescriptorMismatch, forgedFailure);
        Assert.IsFalse(sink.DescriptorBound);

        Assert.IsTrue(sink.TryBindDescriptors(binding, left, right,
            out var boundFailure), boundFailure.ToString());
        Assert.IsTrue(sink.DescriptorBound);
        Assert.IsTrue(sink.LeftAttached);
        Assert.IsTrue(sink.RightAttached);

        Assert.IsFalse(Switch2JoyConJoinedRuntimeInputSink.TryCreateUnbound(
            PairEpoch + 1, LeftDeviceGeneration, LeftTransportGeneration,
            RightDeviceGeneration, RightTransportGeneration,
            Runtime(runtimeGeneration: RuntimeGeneration + 2),
            new Switch2JoyConPairPolicy(1_000), TimeoutMilliseconds,
            Switch2RuntimeTerminalScheduler.Instance, out _, out _, out _,
            out var epochFailure));
        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.
            RuntimeDeviceMismatch, epochFailure);
    }

    [TestMethod]
    public void SubscriberRejectionRollsBackTheWholeJoinedCandidate()
    {
        CreateBound(new Switch2JoyConPairPolicy(1_000),
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var runtime, out _, out var left, out var right);
        DS4Device.ReportHandler<EventArgs> reject = (_, _) =>
            throw new InvalidOperationException("Synthetic rejection.");
        runtime.Report += reject;
        runtime.StartUpdate();
        Switch2CanonicalInputFrame exactRight = Frame(right, counter: 1,
            timestamp: 101);

        sink.PublishJoyCon(Frame(left, counter: 1, timestamp: 100));
        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.PublishJoyCon(exactRight));

        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.
            RuntimeSubscriberRejected, sink.LastFailure);
        Assert.AreEqual(0L, sink.RuntimeAdmissionWaitCount,
            "Subscriber failure is not retryable runtime backpressure.");
        Assert.IsTrue(sink.HasStagedLeft);
        Assert.IsFalse(sink.HasStagedRight,
            "A rejected joined candidate must not enter pair state.");
        Assert.IsFalse(sink.MapperHasAcceptedLeft);
        Assert.IsFalse(sink.MapperHasAcceptedRight);
        Assert.AreEqual(0L, sink.PublishedCount);

        runtime.Report -= reject;
        runtime.Report += (_, _) => { };
        sink.PublishJoyCon(exactRight);

        Assert.AreEqual(1L, sink.PublishedCount,
            "The exact rejected half must remain retryable after rollback.");
        Assert.IsTrue(sink.HasStagedRight);
        Assert.IsTrue(sink.MapperHasAcceptedLeft);
        Assert.IsTrue(sink.MapperHasAcceptedRight);
    }

    [TestMethod]
    public void ConcurrentLeftAndRightCallsSerializeWithoutDroppingEither()
    {
        CreateBound(new Switch2JoyConPairPolicy(1_000),
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var runtime, out _, out var left, out var right);
        using var reportEntered = new ManualResetEventSlim();
        using var releaseReport = new ManualResetEventSlim();
        int regular = 0;
        runtime.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                    Switch2RuntimeReportKind.Regular &&
                Interlocked.Increment(ref regular) == 1)
            {
                reportEntered.Set();
                releaseReport.Wait(2_000);
            }
        };
        runtime.StartUpdate();
        sink.PublishJoyCon(Frame(left, counter: 1, timestamp: 100));

        Task rightPublication = Task.Run(() => sink.PublishJoyCon(
            Frame(right, counter: 1, timestamp: 110)));
        Assert.IsTrue(reportEntered.Wait(1_000));
        Task leftPublication = Task.Run(() => sink.PublishJoyCon(
            Frame(left, counter: 2, timestamp: 120)));
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                sink.PublicationSerializationWaitCount != 0, 1_000));
            Assert.IsFalse(leftPublication.IsCompleted,
                "The second owner must wait for the joined transaction.");
        }
        finally
        {
            releaseReport.Set();
        }

        Assert.IsTrue(Task.WaitAll(new[] { rightPublication, leftPublication },
            2_000));
        Assert.AreEqual(2L, sink.PublishedCount);
        Assert.AreEqual(3L, sink.ConsumedCount);
        Assert.AreEqual(1L, sink.StateOnlyCount);
        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.None,
            sink.LastFailure);
    }

    [TestMethod]
    public void RuntimeBusyAloneUsesAvailabilityWaitAndRetries()
    {
        CreateBound(new Switch2JoyConPairPolicy(1_000),
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var runtime, out _, out var left, out var right);
        Assert.IsTrue(Switch2JoyConJoinedCoordinatorState.TryCreate(PairEpoch,
            left, right, out var externalState));
        Assert.IsTrue(Switch2JoyConJoinedCoordinator.TryProcess(externalState,
            Switch2JoyConPairEvent.Input(PairEpoch,
                Frame(left, counter: 10, timestamp: 10)),
            new Switch2JoyConPairPolicy(1_000), out externalState, out _));
        Assert.IsTrue(Switch2JoyConJoinedCoordinator.TryProcess(externalState,
            Switch2JoyConPairEvent.Input(PairEpoch,
                Frame(right, counter: 10, timestamp: 11)),
            new Switch2JoyConPairPolicy(1_000), out _,
            out var externalResult));
        Assert.IsTrue(externalResult.HasProfileFrame);

        using var reportEntered = new ManualResetEventSlim();
        using var releaseReport = new ManualResetEventSlim();
        int regular = 0;
        runtime.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                    Switch2RuntimeReportKind.Regular &&
                Interlocked.Increment(ref regular) == 1)
            {
                reportEntered.Set();
                releaseReport.Wait(2_000);
            }
        };
        runtime.StartUpdate();
        Switch2RuntimePublicationResult occupyingResult = default;
        Task occupying = Task.Run(() => occupyingResult = runtime.
            TryPublishJoinedJoyConDetailed(externalResult.ProfileFrame));
        Assert.IsTrue(reportEntered.Wait(1_000));

        sink.PublishJoyCon(Frame(left, counter: 1, timestamp: 100));
        Task joined = Task.Run(() => sink.PublishJoyCon(
            Frame(right, counter: 1, timestamp: 101)));
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                sink.RuntimeAdmissionWaitCount != 0, 1_000));
        }
        finally
        {
            releaseReport.Set();
        }

        Assert.IsTrue(Task.WaitAll(new[] { occupying, joined }, 2_000));
        Assert.AreEqual(Switch2RuntimePublicationResult.Published,
            occupyingResult);
        Assert.AreEqual(1L, sink.PublishedCount);
        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.None,
            sink.LastFailure);
    }

    [TestMethod]
    public void LossBeforeFirstFrameRetiresAndOneTerminalTaskServesBothOwners()
    {
        var scheduler = new CountingScheduler();
        CreateBound(new Switch2JoyConPairPolicy(1_000), scheduler,
            out var sink, out var runtime, out var terminalCredential,
            out _, out _);
        int terminalReports = 0;
        runtime.Report += (_, args) => terminalReports +=
            ((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.TerminalNeutral ? 1 : 0;
        runtime.StartUpdate();

        sink.LoseJoyConHalf(Switch2StickSide.Left, LeftDeviceGeneration,
            LeftTransportGeneration,
            Switch2BluetoothInputEndReason.QueueOverflow);
        Assert.IsFalse(sink.LeftAttached);
        Assert.IsTrue(sink.RightAttached);
        Assert.IsFalse(sink.HasStagedLeft);
        Assert.IsFalse(sink.HasStagedRight);
        Assert.IsTrue(sink.TerminalRequested);
        Assert.AreEqual(Switch2BluetoothInputEndReason.QueueOverflow,
            sink.TerminalReason);

        sink.LoseJoyConHalf(Switch2StickSide.Right, RightDeviceGeneration,
            RightTransportGeneration,
            Switch2BluetoothInputEndReason.Disconnected);
        Assert.IsFalse(sink.RightAttached);
        Assert.AreEqual(Switch2BluetoothInputEndReason.QueueOverflow,
            sink.TerminalReason,
            "The first exact physical reason owns the logical terminal epoch.");

        var failures = new Switch2JoyConJoinedRuntimeSinkFailure[2];
        Task<bool> first = Task.Run(() => sink.TryCompleteTerminalNeutral(
            terminalCredential, TimeoutMilliseconds, out failures[0]));
        Task<bool> second = Task.Run(() => sink.TryCompleteTerminalNeutral(
            terminalCredential, TimeoutMilliseconds, out failures[1]));
        Assert.IsTrue(Task.WaitAll(new Task[] { first, second }, 2_000));
        Assert.IsTrue(first.Result, failures[0].ToString());
        Assert.IsTrue(second.Result, failures[1].ToString());
        Assert.AreEqual(1, scheduler.ScheduleCount);
        Assert.AreEqual(1L, sink.TerminalScheduleAttemptCount);
        Assert.AreEqual(1, terminalReports);
        Assert.AreEqual(Switch2BluetoothRuntimeTerminalState.Delivered,
            sink.TerminalState);
    }

    [TestMethod]
    public void ForgedLossCannotDetachOrChooseTerminalReason()
    {
        CreateBound(new Switch2JoyConPairPolicy(1_000),
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var runtime, out _, out _, out _);
        runtime.StartUpdate();

        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.LoseJoyConHalf(Switch2StickSide.Left,
                LeftDeviceGeneration + 1, LeftTransportGeneration,
                Switch2BluetoothInputEndReason.Disconnected));
        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.LoseJoyConHalf((Switch2StickSide)0,
                LeftDeviceGeneration, LeftTransportGeneration,
                Switch2BluetoothInputEndReason.Disconnected));
        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.LoseJoyConHalf(Switch2StickSide.Left,
                LeftDeviceGeneration, LeftTransportGeneration,
                Switch2BluetoothInputEndReason.ActivationAborted));

        Assert.IsTrue(sink.LeftAttached);
        Assert.IsTrue(sink.RightAttached);
        Assert.IsFalse(sink.TerminalRequested);

        sink.LoseJoyConHalf(Switch2StickSide.Left, LeftDeviceGeneration,
            LeftTransportGeneration,
            Switch2BluetoothInputEndReason.Disconnected);
        Assert.IsFalse(sink.LeftAttached);
        Assert.IsTrue(sink.TerminalRequested);
        Assert.AreEqual(Switch2BluetoothInputEndReason.Disconnected,
            sink.TerminalReason);
    }

    [TestMethod]
    public void InlineCallbackCannotReenterAndCanFencePhysicalLoss()
    {
        CreateBound(new Switch2JoyConPairPolicy(1_000),
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var runtime, out var terminalCredential,
            out var left, out var right);
        Switch2CanonicalInputFrame reentrant = Frame(left, counter: 2,
            timestamp: 120);
        bool reentrantRejected = false;
        Switch2JoyConJoinedRuntimeSinkFailure observed = default;
        runtime.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                Switch2RuntimeReportKind.Regular)
            {
                return;
            }
            try
            {
                sink.PublishJoyCon(reentrant);
            }
            catch (InvalidOperationException)
            {
                reentrantRejected = true;
                observed = sink.LastFailure;
            }
            sink.LoseJoyConHalf(Switch2StickSide.Right,
                RightDeviceGeneration, RightTransportGeneration,
                Switch2BluetoothInputEndReason.SinkFailure);
        };
        runtime.StartUpdate();

        sink.PublishJoyCon(Frame(left, counter: 1, timestamp: 100));
        sink.PublishJoyCon(Frame(right, counter: 1, timestamp: 110));

        Assert.IsTrue(reentrantRejected);
        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.
            PublicationAlreadyInProgress, observed);
        Assert.IsTrue(sink.TerminalRequested);
        Assert.AreEqual(Switch2BluetoothInputEndReason.SinkFailure,
            sink.TerminalReason);
        Assert.IsFalse(sink.RightAttached);
        Assert.IsTrue(sink.HasStagedLeft);
        Assert.IsFalse(sink.HasStagedRight,
            "Inline loss must apply after the accepted candidate commits.");
        Assert.AreEqual(1L, sink.PublishedCount);
        Assert.IsTrue(sink.TryCompleteTerminalNeutral(terminalCredential,
            TimeoutMilliseconds, out var failure), failure.ToString());
    }

    [TestMethod]
    public void ForeignFramesAndClosedLifecycleFailClosed()
    {
        CreateBound(new Switch2JoyConPairPolicy(1_000),
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var runtime, out _, out var left, out _);
        runtime.StartUpdate();
        Switch2InputSessionDescriptor foreignLeft = Descriptor(
            Switch2ControllerModel.JoyCon2Left, LeftDeviceGeneration,
            LeftTransportGeneration + 1);

        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.PublishJoyCon(Frame(foreignLeft, counter: 1,
                timestamp: 100)));
        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.
            CanonicalFrameMismatch, sink.LastFailure);
        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.PublishPro(Frame(left, counter: 1, timestamp: 100)));

        runtime.StopUpdate();
        Assert.ThrowsException<InvalidOperationException>(() =>
            sink.PublishJoyCon(Frame(left, counter: 1, timestamp: 100)));
        Assert.AreEqual(Switch2JoyConJoinedRuntimeSinkFailure.LifecycleClosed,
            sink.LastFailure);
    }

    [TestMethod]
    public void WarmTwentyThousandCanonicalInputsAllocateNothing()
    {
        CreateBound(new Switch2JoyConPairPolicy(1_000),
            Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out var runtime, out _, out var left, out var right);
        runtime.StartUpdate();
        Switch2InputSession leftSession = Session(left);
        Switch2InputSession rightSession = Session(right);
        byte[] leftBody = CommonBody();
        byte[] rightBody = CommonBody();
        uint leftCounter = 0;
        uint rightCounter = 0;
        long timestamp = 0;
        bool valid = true;

        for (int index = 0; index < 2_000; index++)
        {
            bool useLeft = (index & 1) == 0;
            ref uint counter = ref useLeft ? ref leftCounter : ref rightCounter;
            counter++;
            byte[] body = useLeft ? leftBody : rightBody;
            BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
            Switch2InputSession session = useLeft ? leftSession : rightSession;
            Switch2InputSessionDescriptor descriptor = useLeft ? left : right;
            valid &= session.TryProcess(descriptor, body, ++timestamp,
                out var frame, out _);
            sink.PublishJoyCon(frame);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            bool useLeft = (index & 1) == 0;
            ref uint counter = ref useLeft ? ref leftCounter : ref rightCounter;
            counter++;
            byte[] body = useLeft ? leftBody : rightBody;
            BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
            Switch2InputSession session = useLeft ? leftSession : rightSession;
            Switch2InputSessionDescriptor descriptor = useLeft ? left : right;
            valid &= session.TryProcess(descriptor, body, ++timestamp,
                out var frame, out _);
            sink.PublishJoyCon(frame);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(22_000L, sink.ConsumedCount);
        Assert.AreEqual(21_999L, sink.PublishedCount);
        Assert.AreEqual(1L, sink.StateOnlyCount);
    }

    private static void CreateBound(in Switch2JoyConPairPolicy policy,
        ISwitch2RuntimeTerminalScheduler scheduler,
        out Switch2JoyConJoinedRuntimeInputSink sink,
        out Switch2RuntimeInputDevice runtime,
        out Switch2JoyConJoinedRuntimeTerminalCredential terminalCredential,
        out Switch2InputSessionDescriptor left,
        out Switch2InputSessionDescriptor right)
    {
        left = Descriptor(Switch2ControllerModel.JoyCon2Left,
            LeftDeviceGeneration, LeftTransportGeneration);
        right = Descriptor(Switch2ControllerModel.JoyCon2Right,
            RightDeviceGeneration, RightTransportGeneration);
        runtime = Runtime();
        Assert.IsTrue(Switch2JoyConJoinedRuntimeInputSink.TryCreateBound(
            PairEpoch, left, right, runtime, policy, TimeoutMilliseconds,
            scheduler, out sink, out terminalCredential, out var failure),
            failure.ToString());
    }

    private static Switch2RuntimeInputDevice Runtime(
        ulong runtimeGeneration = RuntimeGeneration)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(
            runtimeGeneration, PairEpoch, LeftDeviceGeneration,
            LeftTransportGeneration, RightDeviceGeneration,
            RightTransportGeneration, out var runtime, out var failure),
            failure.ToString());
        return runtime;
    }

    private static Switch2InputSessionDescriptor Descriptor(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, InputProperties,
            model, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, QpcFrequency,
            out var descriptor));
        return descriptor;
    }

    private static Switch2InputSession Session(
        in Switch2InputSessionDescriptor descriptor)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            descriptor.Identity.Model, descriptor.DeviceGeneration,
            out var calibration));
        return new Switch2InputSession(descriptor, calibration);
    }

    private static Switch2CanonicalInputFrame Frame(
        in Switch2InputSessionDescriptor descriptor, uint counter,
        long timestamp)
    {
        Switch2InputSession session = Session(descriptor);
        byte[] body = CommonBody();
        BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
        Assert.IsTrue(session.TryProcess(descriptor, body, timestamp,
            out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static byte[] CommonBody()
    {
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        PackStick(body, 0x0A, 0x800, 0x800);
        PackStick(body, 0x0D, 0x800, 0x800);
        return body;
    }

    private static void PackStick(byte[] destination, int offset, ushort x,
        ushort y)
    {
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[offset + 2] = (byte)(y >> 4);
    }

    private sealed class CountingScheduler : ISwitch2RuntimeTerminalScheduler
    {
        private int scheduleCount;

        internal int ScheduleCount => Volatile.Read(ref scheduleCount);

        public bool TrySchedule(
            Func<Switch2TerminalNeutralRequestResult> callback,
            out Task<Switch2TerminalNeutralRequestResult> task)
        {
            Interlocked.Increment(ref scheduleCount);
            task = Task.Run(callback);
            return true;
        }
    }
}
