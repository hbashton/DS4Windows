using System.Buffers.Binary;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2JoyConJoinedCoordinatorTests
{
    private const Switch2GattProperty InputProperties =
        Switch2GattProperty.Read | Switch2GattProperty.Notify;
    private const ulong PairEpoch = 71;
    private const long QpcFrequency = 1_000_000;

    [TestMethod]
    public void InvalidFirstHalfIsNotRetained()
    {
        CreateCoordinator(out var initial, out _, out _);
        Switch2CanonicalInputFrame dedicated = CreateDedicatedFrame(
            Switch2ControllerModel.JoyCon2Left, 1, 1, 100);

        Assert.IsFalse(Process(initial,
            Switch2JoyConPairEvent.Input(PairEpoch, dedicated),
            out var next, out var result));

        Assert.AreEqual(
            Switch2JoyConJoinedCoordinatorFailure.ProfileAdmissionRejected,
            result.Failure);
        Assert.AreEqual(Switch2JoyConProfileInputFailure.UnsupportedReport,
            result.ProfileFailure);
        AssertCoordinatorStateEqual(initial, next);
        Assert.IsFalse(next.PairState.HasLeft);
        Assert.IsFalse(next.PairState.HasRight);
    }

    [TestMethod]
    public void InvalidSecondHalfLeavesPairAndMapperUnchanged()
    {
        CreateCoordinator(out var initial, out var leftDescriptor, out _);
        Switch2CanonicalInputFrame left = CreateCommonFrame(leftDescriptor,
            10, 100);
        Assert.IsTrue(Process(initial,
            Switch2JoyConPairEvent.Input(PairEpoch, left),
            out var waiting, out var waitingResult));
        Assert.AreEqual(Switch2JoyConPairDisposition.WaitingForOtherHalf,
            waitingResult.PairResult.Disposition);
        Assert.IsTrue(waiting.PairState.HasLeft);
        Assert.IsFalse(waiting.MapperState.HasAcceptedLeft);

        Switch2CanonicalInputFrame dedicated = CreateDedicatedFrame(
            Switch2ControllerModel.JoyCon2Right, 1, 1, 101);
        Assert.IsFalse(Process(waiting,
            Switch2JoyConPairEvent.Input(PairEpoch, dedicated),
            out var next, out var result));

        Assert.AreEqual(
            Switch2JoyConJoinedCoordinatorFailure.ProfileAdmissionRejected,
            result.Failure);
        Assert.AreEqual(Switch2JoyConProfileInputFailure.UnsupportedReport,
            result.ProfileFailure);
        AssertCoordinatorStateEqual(waiting, next);
        Assert.IsFalse(next.PairState.HasRight);
    }

    [TestMethod]
    public void WaitingThenJoinAdvancesMapperOnlyAtJoinedEmission()
    {
        CreateCoordinator(out var initial, out var leftDescriptor,
            out var rightDescriptor);
        Switch2CanonicalInputFrame left = CreateCommonFrame(leftDescriptor,
            10, 100, buttons: 1u << 21);
        Switch2CanonicalInputFrame right = CreateCommonFrame(rightDescriptor,
            20, 120, buttons: 1u << 14);

        Assert.IsTrue(Process(initial,
            Switch2JoyConPairEvent.Input(PairEpoch, left),
            out var waiting, out var waitingResult));
        Assert.AreEqual(Switch2JoyConPairDisposition.WaitingForOtherHalf,
            waitingResult.PairResult.Disposition);
        Assert.IsFalse(waitingResult.HasProfileFrame);
        Assert.IsFalse(waiting.MapperState.HasAcceptedLeft);
        Assert.IsFalse(waiting.MapperState.HasAcceptedRight);

        Assert.IsTrue(Process(waiting,
            Switch2JoyConPairEvent.Input(PairEpoch, right),
            out var joined, out var joinedResult));
        Assert.AreEqual(Switch2JoyConPairDisposition.JoinedSnapshot,
            joinedResult.PairResult.Disposition);
        Assert.IsTrue(joinedResult.HasProfileFrame);
        Assert.IsTrue(joined.MapperState.HasAcceptedLeft);
        Assert.IsTrue(joined.MapperState.HasAcceptedRight);
        Assert.AreEqual(10u, joined.MapperState.LastLeftCounter);
        Assert.AreEqual(20u, joined.MapperState.LastRightCounter);
        Assert.IsTrue((joinedResult.ProfileFrame.LeftSource.Buttons &
            Switch2JoyConProfileButton.LeftRailSL) != 0);
        Assert.IsTrue(joinedResult.ProfileFrame.CButton);
    }

    [TestMethod]
    public void StaleHalfCanRecoverWithoutPrematureMapperAdvance()
    {
        CreateCoordinator(out var initial, out var leftDescriptor,
            out var rightDescriptor);
        var narrowPolicy = new Switch2JoyConPairPolicy(50);
        Switch2CanonicalInputFrame oldLeft = CreateCommonFrame(leftDescriptor,
            1, 100);
        Switch2CanonicalInputFrame right = CreateCommonFrame(rightDescriptor,
            1, 300);

        Assert.IsTrue(Process(initial,
            Switch2JoyConPairEvent.Input(PairEpoch, oldLeft), narrowPolicy,
            out var waiting, out _));
        Assert.IsTrue(Process(waiting,
            Switch2JoyConPairEvent.Input(PairEpoch, right), narrowPolicy,
            out var stale, out var staleResult));
        Assert.AreEqual(Switch2JoyConPairDisposition.StaleHalf,
            staleResult.PairResult.Disposition);
        Assert.AreEqual(Switch2JoyConStaleSide.Left,
            staleResult.PairResult.StaleSide);
        Assert.IsFalse(staleResult.HasProfileFrame);
        Assert.IsFalse(stale.MapperState.HasAcceptedLeft);
        Assert.IsFalse(stale.MapperState.HasAcceptedRight);

        Switch2CanonicalInputFrame freshLeft = CreateCommonFrame(
            leftDescriptor, 2, 310);
        Assert.IsTrue(Process(stale,
            Switch2JoyConPairEvent.Input(PairEpoch, freshLeft), narrowPolicy,
            out var recovered, out var recoveredResult));
        Assert.IsTrue(recoveredResult.HasProfileFrame);
        Assert.AreEqual(2u, recovered.MapperState.LastLeftCounter);
        Assert.AreEqual(1u, recovered.MapperState.LastRightCounter);
    }

    [TestMethod]
    public void CounterTimestampAndDescriptorChangesFailWithoutMutation()
    {
        CreateJoinedState(out var joined, out var leftDescriptor,
            out var rightDescriptor);

        Switch2CanonicalInputFrame backward = CreateBackwardCommonFrame(
            leftDescriptor, 110, 90, 1_010, 1_011);
        AssertAdmissionRejected(joined, backward,
            Switch2JoyConProfileInputFailure.BackwardOrOutOfOrder);

        Switch2CanonicalInputFrame staleTimestamp = CreateCommonFrame(
            rightDescriptor, 201, 999);
        AssertAdmissionRejected(joined, staleTimestamp,
            Switch2JoyConProfileInputFailure.StaleObservation);

        Switch2InputSessionDescriptor replacementDescriptor =
            CreateCommonDescriptor(Switch2ControllerModel.JoyCon2Left,
                deviceGeneration: 2, transportGeneration: 1);
        Switch2CanonicalInputFrame replacement = CreateCommonFrame(
            replacementDescriptor, 101, 1_020);
        AssertAdmissionRejected(joined, replacement,
            Switch2JoyConProfileInputFailure.LifetimeMismatch);

        Assert.IsTrue(Switch2JoyConJoinedCoordinatorState.TryCreate(
            PairEpoch + 1, replacementDescriptor, rightDescriptor,
            out var recreated));
        Assert.IsTrue(recreated.IsValid,
            "A descriptor change is admitted only by explicit recreation.");
    }

    [TestMethod]
    public void LossAndSplitSignalClearAndPreserveMapperBaselines()
    {
        CreateJoinedState(out var joined, out var leftDescriptor, out _);
        Switch2JoyConProfileMapperState acceptedMapper = joined.MapperState;

        Switch2JoyConPairEvent loss = Switch2JoyConPairEvent.HalfLost(
            PairEpoch, Switch2StickSide.Left,
            leftDescriptor.DeviceGeneration,
            leftDescriptor.TransportGeneration);
        Assert.IsTrue(Process(joined, loss, out var lost,
            out var lossResult));
        Assert.AreEqual(Switch2JoyConPairDisposition.HalfLost,
            lossResult.PairResult.Disposition);
        Assert.IsTrue(lossResult.ClearsProfileOutput);
        Assert.IsFalse(lossResult.HasProfileFrame);
        Assert.IsFalse(lost.PairState.HasLeft);
        AssertMapperStateEqual(acceptedMapper, lost.MapperState);

        Switch2CanonicalInputFrame sameLifetime = CreateCommonFrame(
            leftDescriptor, 101, 1_020);
        Assert.IsFalse(Process(lost,
            Switch2JoyConPairEvent.Input(PairEpoch, sameLifetime),
            out var afterRejectedReplacement, out var replacementResult));
        Assert.AreEqual(Switch2JoyConJoinedCoordinatorFailure.PairRejected,
            replacementResult.Failure);
        Assert.AreEqual(Switch2JoyConPairRejection.StaleGeneration,
            replacementResult.PairResult.Rejection);
        AssertCoordinatorStateEqual(lost, afterRejectedReplacement);

        CreateJoinedState(out var joinedForSplit, out _, out _);
        Assert.IsTrue(Process(joinedForSplit,
            Switch2JoyConPairEvent.Split(PairEpoch), out var split,
            out var splitResult));
        Assert.AreEqual(Switch2JoyConPairDisposition.Split,
            splitResult.PairResult.Disposition);
        Assert.IsTrue(splitResult.ClearsProfileOutput);
        Assert.IsTrue(split.PairState.IsSplit);
        AssertMapperStateEqual(joinedForSplit.MapperState,
            split.MapperState);

        Assert.IsFalse(Process(split,
            Switch2JoyConPairEvent.Input(PairEpoch, sameLifetime),
            out var afterSplit, out var afterSplitResult));
        Assert.AreEqual(Switch2JoyConPairRejection.AlreadySplit,
            afterSplitResult.PairResult.Rejection);
        AssertCoordinatorStateEqual(split, afterSplit);
    }

    [TestMethod]
    public void MapperFailureRollsBackStagedPairAndMapperState()
    {
        CreateCoordinator(out var initial, out _, out var rightDescriptor);
        Assert.IsTrue(Switch2JoyConPairState.TryCreate(PairEpoch,
            out var broadPair));
        var policy = new Switch2JoyConPairPolicy(1_000);
        Switch2CanonicalInputFrame unsupportedLeft = CreateDedicatedFrame(
            Switch2ControllerModel.JoyCon2Left, 1, 1, 100);
        Switch2CanonicalInputFrame firstRight = CreateCommonFrame(
            rightDescriptor, 1, 101);
        Assert.IsTrue(Switch2JoyConPairReducer.TryReduce(broadPair,
            Switch2JoyConPairEvent.Input(PairEpoch, unsupportedLeft), policy,
            out broadPair, out _));
        Assert.IsTrue(Switch2JoyConPairReducer.TryReduce(broadPair,
            Switch2JoyConPairEvent.Input(PairEpoch, firstRight), policy,
            out broadPair, out _));

        // This internal construction represents corrupted/restored state that
        // the public factory and coordinator can never create. It proves the
        // final mapper gate still rolls back the staged pair update.
        var forged = new Switch2JoyConJoinedCoordinatorState(broadPair,
            initial.MapperState);
        Switch2CanonicalInputFrame secondRight = CreateCommonFrame(
            rightDescriptor, 2, 102);
        Assert.IsFalse(Process(forged,
            Switch2JoyConPairEvent.Input(PairEpoch, secondRight),
            out var next, out var result));

        Assert.AreEqual(
            Switch2JoyConJoinedCoordinatorFailure.ProfileMappingRejected,
            result.Failure);
        Assert.AreEqual(Switch2JoyConProfileInputFailure.UnsupportedReport,
            result.ProfileFailure);
        AssertCoordinatorStateEqual(forged, next);
        Assert.AreEqual(1u, next.PairState.Right.DeviceCounterRaw,
            "The staged counter 2 pair update must not be committed.");
    }

    [TestMethod]
    public void WarmJoinedCoordinatorProcessingAllocatesNothing()
    {
        CreateCoordinator(out var state, out var leftDescriptor,
            out var rightDescriptor);
        Switch2JoyConPairEvent leftEvent = Switch2JoyConPairEvent.Input(
            PairEpoch, CreateCommonFrame(leftDescriptor, 1, 100));
        Switch2JoyConPairEvent rightEvent = Switch2JoyConPairEvent.Input(
            PairEpoch, CreateCommonFrame(rightDescriptor, 1, 100));
        var policy = new Switch2JoyConPairPolicy(1_000);
        Assert.IsTrue(Process(state, leftEvent, policy, out state, out _));
        Assert.IsTrue(Process(state, rightEvent, policy, out state, out _));

        bool succeeded = true;
        for (int warmup = 0; warmup < 2_000; warmup++)
        {
            succeeded &= Process(state, rightEvent, policy, out state,
                out var result) && result.HasProfileFrame;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            succeeded &= Process(state, rightEvent, policy, out state,
                out var result) && result.HasProfileFrame;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    private static void CreateCoordinator(
        out Switch2JoyConJoinedCoordinatorState state,
        out Switch2InputSessionDescriptor leftDescriptor,
        out Switch2InputSessionDescriptor rightDescriptor)
    {
        leftDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Left, 1, 1);
        rightDescriptor = CreateCommonDescriptor(
            Switch2ControllerModel.JoyCon2Right, 1, 1);
        Assert.IsTrue(Switch2JoyConJoinedCoordinatorState.TryCreate(
            PairEpoch, leftDescriptor, rightDescriptor, out state));
    }

    private static void CreateJoinedState(
        out Switch2JoyConJoinedCoordinatorState state,
        out Switch2InputSessionDescriptor leftDescriptor,
        out Switch2InputSessionDescriptor rightDescriptor)
    {
        CreateCoordinator(out state, out leftDescriptor,
            out rightDescriptor);
        Switch2CanonicalInputFrame left = CreateCommonFrame(leftDescriptor,
            100, 1_000);
        Switch2CanonicalInputFrame right = CreateCommonFrame(rightDescriptor,
            200, 1_000);
        Assert.IsTrue(Process(state,
            Switch2JoyConPairEvent.Input(PairEpoch, left), out state, out _));
        Assert.IsTrue(Process(state,
            Switch2JoyConPairEvent.Input(PairEpoch, right), out state,
            out var result));
        Assert.IsTrue(result.HasProfileFrame);
    }

    private static bool Process(
        in Switch2JoyConJoinedCoordinatorState state,
        in Switch2JoyConPairEvent pairEvent,
        out Switch2JoyConJoinedCoordinatorState next,
        out Switch2JoyConJoinedCoordinatorResult result) =>
        Process(state, pairEvent, new Switch2JoyConPairPolicy(1_000),
            out next, out result);

    private static bool Process(
        in Switch2JoyConJoinedCoordinatorState state,
        in Switch2JoyConPairEvent pairEvent,
        in Switch2JoyConPairPolicy policy,
        out Switch2JoyConJoinedCoordinatorState next,
        out Switch2JoyConJoinedCoordinatorResult result) =>
        Switch2JoyConJoinedCoordinator.TryProcess(state, pairEvent, policy,
            out next, out result);

    private static void AssertAdmissionRejected(
        in Switch2JoyConJoinedCoordinatorState state,
        in Switch2CanonicalInputFrame frame,
        Switch2JoyConProfileInputFailure expectedFailure)
    {
        Assert.IsFalse(Process(state,
            Switch2JoyConPairEvent.Input(PairEpoch, frame),
            out var next, out var result));
        Assert.AreEqual(
            Switch2JoyConJoinedCoordinatorFailure.ProfileAdmissionRejected,
            result.Failure);
        Assert.AreEqual(expectedFailure, result.ProfileFailure);
        AssertCoordinatorStateEqual(state, next);
    }

    private static void AssertCoordinatorStateEqual(
        in Switch2JoyConJoinedCoordinatorState expected,
        in Switch2JoyConJoinedCoordinatorState actual)
    {
        Assert.AreEqual(expected.PairState.PairEpoch,
            actual.PairState.PairEpoch);
        Assert.AreEqual(expected.PairState.IsSplit,
            actual.PairState.IsSplit);
        Assert.AreEqual(expected.PairState.HasLeft,
            actual.PairState.HasLeft);
        Assert.AreEqual(expected.PairState.HasRight,
            actual.PairState.HasRight);
        Assert.AreEqual(expected.PairState.HasLeftLifetimeFence,
            actual.PairState.HasLeftLifetimeFence);
        Assert.AreEqual(expected.PairState.HasRightLifetimeFence,
            actual.PairState.HasRightLifetimeFence);
        AssertFrameEqual(expected.PairState.Left, actual.PairState.Left);
        AssertFrameEqual(expected.PairState.Right, actual.PairState.Right);
        AssertMapperStateEqual(expected.MapperState, actual.MapperState);
    }

    private static void AssertFrameEqual(
        in Switch2CanonicalInputFrame expected,
        in Switch2CanonicalInputFrame actual)
    {
        Assert.AreEqual(expected.Version, actual.Version);
        Assert.AreEqual(expected.Descriptor, actual.Descriptor);
        Assert.AreEqual(expected.CompletionTimestampQpc,
            actual.CompletionTimestampQpc);
        Assert.AreEqual(expected.DeviceCounterRaw,
            actual.DeviceCounterRaw);
        Assert.AreEqual(expected.RawButtonBits, actual.RawButtonBits);
    }

    private static void AssertMapperStateEqual(
        in Switch2JoyConProfileMapperState expected,
        in Switch2JoyConProfileMapperState actual)
    {
        Assert.AreEqual(expected.Mode, actual.Mode);
        Assert.AreEqual(expected.PairEpoch, actual.PairEpoch);
        Assert.AreEqual(expected.LeftDescriptor, actual.LeftDescriptor);
        Assert.AreEqual(expected.RightDescriptor, actual.RightDescriptor);
        Assert.AreEqual(expected.HasAcceptedLeft, actual.HasAcceptedLeft);
        Assert.AreEqual(expected.HasAcceptedRight, actual.HasAcceptedRight);
        Assert.AreEqual(expected.LastLeftTimestampQpc,
            actual.LastLeftTimestampQpc);
        Assert.AreEqual(expected.LastRightTimestampQpc,
            actual.LastRightTimestampQpc);
        Assert.AreEqual(expected.LastLeftCounter, actual.LastLeftCounter);
        Assert.AreEqual(expected.LastRightCounter, actual.LastRightCounter);
    }

    private static Switch2InputSessionDescriptor CreateCommonDescriptor(
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

    private static Switch2CanonicalInputFrame CreateCommonFrame(
        in Switch2InputSessionDescriptor descriptor, uint counter,
        long timestamp, uint buttons = 0)
    {
        Switch2InputSession session = CreateSession(descriptor);
        byte[] body = BuildCommonBody(counter, buttons);
        Assert.IsTrue(session.TryProcess(descriptor, body, timestamp,
            out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static Switch2CanonicalInputFrame CreateBackwardCommonFrame(
        in Switch2InputSessionDescriptor descriptor, uint firstCounter,
        uint backwardCounter, long firstTimestamp, long secondTimestamp)
    {
        Switch2InputSession session = CreateSession(descriptor);
        Assert.IsTrue(session.TryProcess(descriptor,
            BuildCommonBody(firstCounter, 0), firstTimestamp, out _,
            out var firstFailure), firstFailure.ToString());
        Assert.IsTrue(session.TryProcess(descriptor,
            BuildCommonBody(backwardCounter, 0), secondTimestamp,
            out var backward, out var failure), failure.ToString());
        Assert.AreEqual(Switch2CounterSequenceKind.BackwardOrOutOfOrder,
            backward.CounterSequence);
        return backward;
    }

    private static Switch2CanonicalInputFrame CreateDedicatedFrame(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, long timestamp)
    {
        Guid characteristic = model == Switch2ControllerModel.JoyCon2Left ?
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid :
            Switch2InputCodec.JoyCon2Right08CharacteristicUuid;
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid, characteristic, InputProperties,
            model, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, QpcFrequency,
            out var descriptor));
        Switch2InputSession session = CreateSession(descriptor);
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        Assert.IsTrue(session.TryProcess(descriptor, body, timestamp,
            out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static Switch2InputSession CreateSession(
        in Switch2InputSessionDescriptor descriptor)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            descriptor.Identity.Model, descriptor.DeviceGeneration,
            out var calibration));
        return new Switch2InputSession(descriptor, calibration);
    }

    private static byte[] BuildCommonBody(uint counter, uint buttons)
    {
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), buttons);
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
}
