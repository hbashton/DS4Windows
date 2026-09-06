using System.Buffers.Binary;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2InputSessionTests
{
    private const Switch2GattProperty InputProperties =
        Switch2GattProperty.Read | Switch2GattProperty.Notify;

    [TestMethod]
    public void ProUsbCenteredFallbackUsesObservedPhysicalTravel()
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.
            TryCreateProUsbCenteredFallback(17, out var calibration));

        Assert.AreEqual(Switch2ControllerModel.ProController2,
            calibration.Model);
        Assert.AreEqual(17UL, calibration.DeviceGeneration);
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.FallbackMissing,
            calibration.Left.Status);
        Assert.AreEqual((ushort)0x0800,
            calibration.Left.EffectiveCalibration.NeutralX);
        Assert.AreEqual((ushort)1500,
            calibration.Left.EffectiveCalibration.PositiveRangeX);
        Assert.AreEqual((ushort)1500,
            calibration.Right.EffectiveCalibration.NegativeRangeY);
        Assert.IsFalse(Switch2InputCalibrationSnapshot.
            TryCreateProUsbCenteredFallback(0, out _));
    }

    [TestMethod]
    public void UsbSessionOwnsExactBodyAndPreservesHighResolutionValues()
    {
        Switch2InputSessionDescriptor descriptor = CreateUsbDescriptor(
            deviceGeneration: 11, transportGeneration: 7,
            qpcFrequency: 10_000_000);
        Switch2InputCalibrationSnapshot calibration =
            CreateFallbackCalibration(Switch2ControllerModel.ProController2,
                11);
        var session = new Switch2InputSession(descriptor, calibration);
        byte[] packet = BuildUsbPacket(0x44332211, 0xFFEEDDCC,
            0xABC, 0x123, 0x456, 0xFED);
        for (int index = 1 + 0x10; index < packet.Length; index++)
        {
            packet[index] = (byte)(index * 37);
        }
        byte[] expectedBody = packet.AsSpan(1).ToArray();

        Assert.IsTrue(session.TryProcess(descriptor, packet, 1_234_567,
            out var frame, out var failure), failure.ToString());
        Assert.AreEqual(Switch2CanonicalInputFrame.CurrentVersion,
            frame.Version);
        Assert.AreEqual(Switch2ControllerModel.ProController2, frame.Model);
        Assert.AreEqual(Switch2Transport.Usb, frame.Transport);
        Assert.AreEqual(
            Switch2InputProtocolRevision.ProUsbCommon05Bcd0201,
            frame.ProtocolRevision);
        Assert.AreEqual(11UL, frame.DeviceGeneration);
        Assert.AreEqual(7UL, frame.TransportGeneration);
        Assert.AreEqual(1_234_567L, frame.CompletionTimestampQpc);
        Assert.AreEqual(10_000_000L, frame.QpcFrequency);
        Assert.AreEqual(0x44332211u, frame.DeviceCounterRaw);
        Assert.AreEqual(0xFFEEDDCCu, frame.RawButtonBits);
        Assert.AreEqual(Switch2CounterSequenceKind.First,
            frame.CounterSequence);
        Assert.IsFalse(frame.HasCounterDelta);
        Assert.IsTrue(frame.TryGetLeftStick(out var left));
        Assert.AreEqual((ushort)0xABC, left.Raw.X);
        Assert.AreEqual((ushort)0x123, left.Raw.Y);
        Assert.AreEqual(0xABC - 0x800, left.OffsetX);
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.FallbackMissing,
            left.CalibrationStatus);
        Assert.IsTrue(frame.TryGetRightStick(out var right));
        Assert.AreEqual((ushort)0x456, right.Raw.X);
        Assert.AreEqual((ushort)0xFED, right.Raw.Y);

        Array.Fill(packet, (byte)0x00);
        var copied = new byte[Switch2OwnedInputBody.Length];
        Assert.IsTrue(frame.TryCopyRawBody(copied));
        CollectionAssert.AreEqual(expectedBody, copied,
            "A canonical frame must own bytes independently of a reused read buffer.");
        Assert.AreEqual(expectedBody[0x3E], frame.RawBody[0x3E]);
    }

    [TestMethod]
    public void IdentityAndObservationLifetimeAreFailClosed()
    {
        Assert.IsFalse(Switch2InputProtocolIdentity.
            TryCreateProController2Usb(0x057F, 0x2069, 0x0201, out _));
        Assert.IsFalse(Switch2InputProtocolIdentity.
            TryCreateProController2Usb(0x057E, 0x2068, 0x0201, out _));
        Assert.IsFalse(Switch2InputProtocolIdentity.
            TryCreateProController2Usb(0x057E, 0x2069, 0x0200, out _));
        Assert.IsFalse(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Guid.Empty, Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
            InputProperties, Switch2ControllerModel.JoyCon2Left, out _));
        Assert.IsFalse(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
            Switch2GattProperty.Read, Switch2ControllerModel.JoyCon2Left,
            out _));
        Assert.IsFalse(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid,
            InputProperties, Switch2ControllerModel.JoyCon2Right, out _));

        Switch2InputSessionDescriptor descriptor = CreateUsbDescriptor(1, 1,
            10_000_000);
        Switch2InputSessionDescriptor otherLifetime = CreateUsbDescriptor(1,
            2, 10_000_000);
        var session = new Switch2InputSession(descriptor,
            CreateFallbackCalibration(Switch2ControllerModel.ProController2));
        byte[] packet = BuildUsbPacket(7, 0, 1, 2, 3, 4);

        Assert.IsFalse(session.TryProcess(otherLifetime, packet, 100,
            out _, out var lifetimeFailure));
        Assert.AreEqual(Switch2InputSessionFailure.DescriptorMismatch,
            lifetimeFailure);
        Assert.IsFalse(session.TryProcess(descriptor,
            packet.AsSpan(0, packet.Length - 1), 100, out _,
            out var framingFailure));
        Assert.AreEqual(Switch2InputSessionFailure.InvalidFramingOrReport,
            framingFailure);
        Assert.IsTrue(session.TryProcess(descriptor, packet, 100,
            out var first, out _));
        Assert.AreEqual(Switch2CounterSequenceKind.First,
            first.CounterSequence,
            "Rejected observations must not advance session state.");
        Assert.IsFalse(session.TryProcess(descriptor, packet, 99,
            out _, out var timestampFailure));
        Assert.AreEqual(Switch2InputSessionFailure.TimestampRegression,
            timestampFailure);
    }

    [TestMethod]
    public void CounterClassificationHandlesWrapDuplicateAndBackward()
    {
        Switch2InputSessionDescriptor descriptor = CreateUsbDescriptor(1, 1,
            10_000_000);
        var session = new Switch2InputSession(descriptor,
            CreateFallbackCalibration(Switch2ControllerModel.ProController2));

        Assert.IsTrue(Process(session, descriptor, 0xFFFFFFFC, 100,
            out var first));
        Assert.AreEqual(Switch2CounterSequenceKind.First,
            first.CounterSequence);
        Assert.IsTrue(Process(session, descriptor, 0, 101,
            out var wrapped));
        Assert.AreEqual(Switch2CounterSequenceKind.Forward,
            wrapped.CounterSequence);
        Assert.AreEqual(4u, wrapped.CounterDeltaRaw);
        Assert.IsTrue(Process(session, descriptor, 0, 102,
            out var duplicate));
        Assert.AreEqual(Switch2CounterSequenceKind.Duplicate,
            duplicate.CounterSequence);
        Assert.AreEqual(0u, duplicate.CounterDeltaRaw);
        Assert.IsTrue(Process(session, descriptor, 0xFFFFFFF0, 103,
            out var backward));
        Assert.AreEqual(Switch2CounterSequenceKind.BackwardOrOutOfOrder,
            backward.CounterSequence);
        Assert.IsTrue(Process(session, descriptor, 4, 104,
            out var afterBackward));
        Assert.AreEqual(Switch2CounterSequenceKind.Forward,
            afterBackward.CounterSequence);
        Assert.AreEqual(20u, afterBackward.CounterDeltaRaw,
            "USB raw-counter diagnostics follow each valid arrival, including discontinuities.");
    }

    [TestMethod]
    public void ResetRequiresGenerationAdvanceAndClearsSequenceBaseline()
    {
        Switch2InputSessionDescriptor initial = CreateUsbDescriptor(5, 9,
            10_000_000);
        Switch2InputCalibrationSnapshot calibration =
            CreateFallbackCalibration(Switch2ControllerModel.ProController2,
                5);
        var session = new Switch2InputSession(initial, calibration);
        Assert.IsTrue(Process(session, initial, 10, 500, out _));

        Assert.IsFalse(session.TryReset(initial, calibration,
            out var sameFailure));
        Assert.AreEqual(Switch2InputSessionFailure.GenerationNotAdvanced,
            sameFailure);
        Switch2InputSessionDescriptor regression = CreateUsbDescriptor(5, 8,
            10_000_000);
        Assert.IsFalse(session.TryReset(regression, calibration,
            out var regressionFailure));
        Assert.AreEqual(Switch2InputSessionFailure.GenerationRegression,
            regressionFailure);
        Switch2InputSessionDescriptor wrongClock = CreateUsbDescriptor(5, 10,
            1_000_000);
        Assert.IsFalse(session.TryReset(wrongClock, calibration,
            out var clockFailure));
        Assert.AreEqual(Switch2InputSessionFailure.ClockMismatch,
            clockFailure);

        Switch2InputSessionDescriptor transportAdvance = CreateUsbDescriptor(
            5, 10, 10_000_000);
        byte[] factoryRecord = BuildCalibration(0x800, 0x800, 0x700,
            0x700, 0x700, 0x700);
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 5, factoryRecord,
            factoryRecord, out var changedSameDeviceCalibration));
        Assert.IsFalse(session.TryReset(transportAdvance,
            changedSameDeviceCalibration, out var calibrationMutation));
        Assert.AreEqual(Switch2InputSessionFailure.InvalidCalibration,
            calibrationMutation,
            "A transport reconnect cannot replace a device-generation snapshot.");

        var factorySession = new Switch2InputSession(initial,
            changedSameDeviceCalibration);
        Assert.IsFalse(factorySession.TryReset(transportAdvance, calibration,
            out var factoryToFallback));
        Assert.AreEqual(Switch2InputSessionFailure.InvalidCalibration,
            factoryToFallback);
        byte[] differentFactoryRecord = BuildCalibration(0x801, 0x7FF,
            0x600, 0x601, 0x602, 0x603);
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 5,
            differentFactoryRecord, differentFactoryRecord,
            out var differentFactoryCalibration));
        Assert.IsFalse(factorySession.TryReset(transportAdvance,
            differentFactoryCalibration, out var differentRanges));
        Assert.AreEqual(Switch2InputSessionFailure.InvalidCalibration,
            differentRanges);
        Assert.IsTrue(factorySession.TryReset(transportAdvance,
            changedSameDeviceCalibration, out _));

        Switch2InputCalibrationSnapshot equivalentFallback =
            CreateFallbackCalibration(Switch2ControllerModel.ProController2,
                5);
        Assert.IsTrue(session.TryReset(transportAdvance, equivalentFallback,
            out _));
        Assert.IsFalse(Process(session, transportAdvance, 10, 1,
            out _), "A reconnect cannot reset the absolute QPC chronology.");
        Assert.IsTrue(Process(session, transportAdvance, 10, 501,
            out var restarted));
        Assert.AreEqual(Switch2CounterSequenceKind.First,
            restarted.CounterSequence);

        Switch2InputSessionDescriptor deviceAdvance = CreateUsbDescriptor(6,
            1, 10_000_000);
        Assert.IsFalse(session.TryReset(deviceAdvance, calibration,
            out var staleCalibration));
        Assert.AreEqual(Switch2InputSessionFailure.InvalidCalibration,
            staleCalibration);
        Switch2InputCalibrationSnapshot nextCalibration =
            CreateFallbackCalibration(Switch2ControllerModel.ProController2,
                6);
        Assert.IsTrue(session.TryReset(deviceAdvance, nextCalibration, out _),
            "A new physical-device generation may restart transport generation.");
        Assert.IsTrue(Process(session, deviceAdvance, 10, 502,
            out var replacedDevice));
        Assert.AreEqual(Switch2CounterSequenceKind.First,
            replacedDevice.CounterSequence);
    }

    [TestMethod]
    public void CalibrationAdoptionIsExplicitAndFailClosed()
    {
        byte[] valid = BuildCalibration(0x800, 0x801, 0x700, 0x6FF,
            0x700, 0x701);
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 1, valid, valid,
            out var adopted));
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            adopted.Left.Status);
        Assert.AreEqual(1UL, adopted.DeviceGeneration);
        Assert.AreEqual(Switch2CalibrationAdoptionFailure.None,
            adopted.Left.Failure);
        Assert.AreEqual((ushort)0x800,
            adopted.Left.EffectiveCalibration.NeutralX);

        byte[] user = BuildUserCalibration(0x810, 0x811, 0x6F0, 0x6EF,
            0x700, 0x701);
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 1, valid, valid, user,
            Enumerable.Repeat((byte)0xFF,
                Switch2CalibrationCodec.UserStickCalibrationLength).ToArray(),
            out var userPreferred));
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedUser,
            userPreferred.Left.Status);
        Assert.IsTrue(userPreferred.Left.IsUserAdopted);
        Assert.AreEqual((ushort)0x810,
            userPreferred.Left.EffectiveCalibration.NeutralX);
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            userPreferred.Right.Status,
            "An unmarked user slot must preserve the validated factory record.");

        byte[] invalidUser = BuildUserCalibration(0, 0, 0, 0, 0, 0);
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 1, valid, valid,
            invalidUser, ReadOnlySpan<byte>.Empty,
            out var invalidUserFallback));
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            invalidUserFallback.Left.Status,
            "A marked but unadoptable user record must not erase usable factory calibration.");

        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 1, new byte[9], valid,
            out var sentinel));
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.FallbackUnadoptable,
            sentinel.Left.Status);
        Assert.AreEqual(Switch2CalibrationAdoptionFailure.SentinelOrErased,
            sentinel.Left.Failure);
        Assert.AreEqual((ushort)0x800,
            sentinel.Left.EffectiveCalibration.NeutralX);

        byte[] negativeEndpoint = BuildCalibration(0x100, 0x800, 0x100,
            0x100, 0x101, 0x100);
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 1, negativeEndpoint, valid,
            out var negative));
        Assert.AreEqual(
            Switch2CalibrationAdoptionFailure.NegativeEndpointOutOfRange,
            negative.Left.Failure);

        byte[] positiveEndpoint = BuildCalibration(0x800, 0x800, 0x801,
            0x100, 0x100, 0x100);
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 1, positiveEndpoint, valid,
            out var positive));
        Assert.AreEqual(
            Switch2CalibrationAdoptionFailure.PositiveEndpointOutOfRange,
            positive.Left.Failure);

        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 1, new byte[8], valid,
            out var malformed));
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.FallbackMalformed,
            malformed.Left.Status);
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            malformed.Right.Status);

        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.JoyCon2Left, 1, valid,
            new byte[] { 1 },
            out var joyCon));
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.AdoptedFactory,
            joyCon.Left.Status);
        Assert.AreEqual(Switch2CalibrationAdoptionStatus.NotApplicable,
            joyCon.Right.Status,
            "Bytes for a physically absent side must never be adopted.");
        Assert.IsFalse(Switch2InputCalibrationSnapshot.TryCreate(
            Switch2ControllerModel.ProController2, 0, valid, valid,
            out _), "Calibration without a physical-device generation is unsafe.");
    }

    [TestMethod]
    public void BluetoothFrameOwnsOpaqueMotionAndAbsentStickStaysAbsent()
    {
        Switch2InputSessionDescriptor descriptor = CreateJoyConDescriptor(
            Switch2ControllerModel.JoyCon2Left, 3, 4, 1_000_000);
        var session = new Switch2InputSession(descriptor,
            CreateFallbackCalibration(Switch2ControllerModel.JoyCon2Left,
                3));
        byte[] body = BuildJoyConBody(0xFE, 0x345, 0xABC,
            motionLength: 30);
        for (int index = 0; index < 30; index++)
        {
            body[0x10 + index] = (byte)(0x80 + index);
        }
        byte[] expectedMotion = body.AsSpan(0x10, 30).ToArray();

        Assert.IsTrue(session.TryProcess(descriptor, body, 1000,
            out var frame, out var failure), failure.ToString());
        Assert.AreEqual((byte)0xFE, frame.DeviceCounterRaw);
        Assert.AreEqual((byte)8, frame.CounterWidthBits);
        Assert.IsTrue(frame.TryGetLeftStick(out var left));
        Assert.AreEqual((ushort)0x345, left.Raw.X);
        Assert.AreEqual((ushort)0xABC, left.Raw.Y);
        Assert.IsFalse(frame.TryGetRightStick(out _));

        Array.Fill(body, (byte)0);
        var motion = new byte[30];
        Assert.IsTrue(frame.TryCopyOpaqueMotion(motion, out int written));
        Assert.AreEqual(30, written);
        CollectionAssert.AreEqual(expectedMotion, motion);
        Assert.IsFalse(frame.TryCopyOpaqueMotion(new byte[29], out written));
        Assert.AreEqual(0, written);
    }

    [TestMethod]
    public void InputSessionSteadyStateAllocatesNoManagedMemory()
    {
        Switch2InputSessionDescriptor descriptor = CreateUsbDescriptor(1, 1,
            10_000_000);
        var session = new Switch2InputSession(descriptor,
            CreateFallbackCalibration(Switch2ControllerModel.ProController2));
        byte[] packet = BuildUsbPacket(123, 0x81000001,
            0xAAA, 0xBBB, 0xCCC, 0xDDD);
        bool succeeded = true;
        for (int warmup = 0; warmup < 2000; warmup++)
        {
            succeeded &= session.TryProcess(descriptor, packet, warmup,
                out _, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            succeeded &= session.TryProcess(descriptor, packet,
                2_000 + iteration, out _, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void JoyConPairJoinsWithinSkewAndIdentifiesStaleHalf()
    {
        Assert.IsTrue(Switch2JoyConPairState.TryCreate(77, out var state));
        var policy = new Switch2JoyConPairPolicy(1_000);
        Switch2CanonicalInputFrame left = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Left, 1, 1_000);
        Switch2CanonicalInputFrame right = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Right, 1, 1_500);

        Assert.IsTrue(Reduce(state,
            Switch2JoyConPairEvent.Input(77, left), policy,
            out state, out var waiting));
        Assert.AreEqual(Switch2JoyConPairDisposition.WaitingForOtherHalf,
            waiting.Disposition);
        Assert.IsTrue(Reduce(state,
            Switch2JoyConPairEvent.Input(77, right), policy,
            out state, out var joined));
        Assert.IsTrue(joined.HasSnapshot);
        Assert.AreEqual(500UL, joined.Snapshot.SkewQpcTicks);
        Assert.AreEqual(500UL, joined.Snapshot.SkewMicroseconds);
        Assert.AreEqual(1_500L,
            joined.Snapshot.CompletionTimestampQpc);

        right = CreateJoyConFrame(Switch2ControllerModel.JoyCon2Right,
            2, 5_000);
        Assert.IsTrue(Reduce(state,
            Switch2JoyConPairEvent.Input(77, right), policy,
            out state, out var stale));
        Assert.AreEqual(Switch2JoyConPairDisposition.StaleHalf,
            stale.Disposition);
        Assert.AreEqual(Switch2JoyConStaleSide.Left, stale.StaleSide);

        left = CreateJoyConFrame(Switch2ControllerModel.JoyCon2Left,
            2, 4_500);
        Assert.IsTrue(Reduce(state,
            Switch2JoyConPairEvent.Input(77, left), policy,
            out state, out var refreshed));
        Assert.IsTrue(refreshed.HasSnapshot);
        Assert.AreEqual(500UL, refreshed.Snapshot.SkewQpcTicks);
    }

    [TestMethod]
    public void JoyConPairLossAndReplacementAreGenerationFenced()
    {
        Assert.IsTrue(Switch2JoyConPairState.TryCreate(9, out var state));
        var policy = new Switch2JoyConPairPolicy(2_000);
        Switch2CanonicalInputFrame left = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Left, 1, 100,
            deviceGeneration: 2, transportGeneration: 3);
        Switch2CanonicalInputFrame right = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Right, 1, 101);
        Assert.IsTrue(Reduce(state, Switch2JoyConPairEvent.Input(9, left),
            policy, out state, out _));
        Assert.IsTrue(Reduce(state, Switch2JoyConPairEvent.Input(9, right),
            policy, out state, out _));

        Assert.IsFalse(Reduce(state, Switch2JoyConPairEvent.HalfLost(9,
            Switch2StickSide.Left, 2, 2), policy, out var unchanged,
            out var staleLoss));
        Assert.AreEqual(Switch2JoyConPairRejection.StaleGeneration,
            staleLoss.Rejection);
        Assert.IsTrue(unchanged.HasLeft);

        Assert.IsTrue(Reduce(state, Switch2JoyConPairEvent.HalfLost(9,
            Switch2StickSide.Left, 2, 3), policy, out state,
            out var lost));
        Assert.AreEqual(Switch2JoyConPairDisposition.HalfLost,
            lost.Disposition);
        Assert.IsFalse(state.HasLeft);
        Assert.IsTrue(state.HasLeftLifetimeFence);
        Assert.IsTrue(state.HasRight);

        Switch2CanonicalInputFrame delayedSameLifetime = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Left, 2, 103,
            deviceGeneration: 2, transportGeneration: 3);
        Assert.IsFalse(Reduce(state,
            Switch2JoyConPairEvent.Input(9, delayedSameLifetime), policy,
            out var afterDelayed, out var delayed));
        Assert.AreEqual(Switch2JoyConPairRejection.StaleGeneration,
            delayed.Rejection);
        Assert.IsFalse(afterDelayed.HasLeft);
        Assert.IsTrue(afterDelayed.HasLeftLifetimeFence);

        Switch2CanonicalInputFrame delayedOlderLifetime = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Left, 3, 104,
            deviceGeneration: 1, transportGeneration: 9);
        Assert.IsFalse(Reduce(state,
            Switch2JoyConPairEvent.Input(9, delayedOlderLifetime), policy,
            out _, out var older));
        Assert.AreEqual(Switch2JoyConPairRejection.StaleGeneration,
            older.Rejection);

        Switch2CanonicalInputFrame replacement = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Left, 1, 102,
            deviceGeneration: 3, transportGeneration: 1);
        Assert.IsTrue(Reduce(state,
            Switch2JoyConPairEvent.Input(9, replacement), policy,
            out state, out var rejoined));
        Assert.IsTrue(rejoined.HasSnapshot);
        Assert.AreEqual(3UL, rejoined.Snapshot.Left.DeviceGeneration);
    }

    [TestMethod]
    public void JoyConPairEpochClockTimestampAndSplitAreFailClosed()
    {
        Assert.IsTrue(Switch2JoyConPairState.TryCreate(21, out var state));
        var policy = new Switch2JoyConPairPolicy(2_000);
        Switch2CanonicalInputFrame left = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Left, 1, 100);
        Assert.IsFalse(Reduce(state,
            Switch2JoyConPairEvent.Input(22, left), policy,
            out _, out var epoch));
        Assert.AreEqual(Switch2JoyConPairRejection.PairEpochMismatch,
            epoch.Rejection);
        Assert.IsTrue(Reduce(state,
            Switch2JoyConPairEvent.Input(21, left), policy,
            out state, out _));

        Switch2CanonicalInputFrame oldLeft = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Left, 2, 99);
        Assert.IsFalse(Reduce(state,
            Switch2JoyConPairEvent.Input(21, oldLeft), policy,
            out _, out var staleTimestamp));
        Assert.AreEqual(Switch2JoyConPairRejection.StaleTimestamp,
            staleTimestamp.Rejection);

        Switch2CanonicalInputFrame newGenerationOldTimestamp =
            CreateJoyConFrame(Switch2ControllerModel.JoyCon2Left, 2, 99,
                deviceGeneration: 2, transportGeneration: 1);
        Assert.IsFalse(Reduce(state,
            Switch2JoyConPairEvent.Input(21, newGenerationOldTimestamp),
            policy, out _, out var newGenerationRegression));
        Assert.AreEqual(Switch2JoyConPairRejection.StaleTimestamp,
            newGenerationRegression.Rejection,
            "A generation claim cannot reset absolute QPC chronology.");

        Switch2CanonicalInputFrame wrongClockRight = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Right, 1, 101,
            qpcFrequency: 10_000_000);
        Assert.IsFalse(Reduce(state,
            Switch2JoyConPairEvent.Input(21, wrongClockRight), policy,
            out var unchanged, out var clock));
        Assert.AreEqual(Switch2JoyConPairRejection.ClockMismatch,
            clock.Rejection);
        Assert.IsFalse(unchanged.HasRight,
            "A rejected half must not partially mutate pair state.");

        Assert.IsTrue(Reduce(state, Switch2JoyConPairEvent.Split(21), policy,
            out state, out var split));
        Assert.AreEqual(Switch2JoyConPairDisposition.Split,
            split.Disposition);
        Assert.IsTrue(state.IsSplit);
        Assert.IsFalse(state.HasLeft);
        Assert.IsFalse(state.HasRight);
        Assert.IsFalse(Reduce(state,
            Switch2JoyConPairEvent.Input(21, left), policy,
            out _, out var afterSplit));
        Assert.AreEqual(Switch2JoyConPairRejection.AlreadySplit,
            afterSplit.Rejection);
    }

    [TestMethod]
    public void JoyConPairReducerAllocatesNoManagedMemory()
    {
        Assert.IsTrue(Switch2JoyConPairState.TryCreate(44, out var state));
        var policy = new Switch2JoyConPairPolicy(1_000);
        Switch2CanonicalInputFrame left = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Left, 1, 100);
        Switch2CanonicalInputFrame right = CreateJoyConFrame(
            Switch2ControllerModel.JoyCon2Right, 1, 100);
        bool succeeded = Reduce(state,
            Switch2JoyConPairEvent.Input(44, left), policy,
            out state, out _);
        succeeded &= Reduce(state,
            Switch2JoyConPairEvent.Input(44, right), policy,
            out state, out _);
        Switch2JoyConPairEvent rightInput =
            Switch2JoyConPairEvent.Input(44, right);
        for (int warmup = 0; warmup < 2000; warmup++)
        {
            succeeded &= Reduce(state, rightInput, policy, out state, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            succeeded &= Reduce(state, rightInput, policy,
                out state, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsTrue(succeeded);
        Assert.AreEqual(0L, allocated);
    }

    private static bool Process(Switch2InputSession session,
        in Switch2InputSessionDescriptor descriptor, uint counter,
        long timestamp, out Switch2CanonicalInputFrame frame) =>
        session.TryProcess(descriptor,
            BuildUsbPacket(counter, 0, 1, 2, 3, 4), timestamp,
            out frame, out _);

    private static bool Reduce(in Switch2JoyConPairState state,
        in Switch2JoyConPairEvent pairEvent,
        in Switch2JoyConPairPolicy policy,
        out Switch2JoyConPairState next,
        out Switch2JoyConPairResult result) =>
        Switch2JoyConPairReducer.TryReduce(state, pairEvent, policy,
            out next, out result);

    private static Switch2InputSessionDescriptor CreateUsbDescriptor(
        ulong deviceGeneration, ulong transportGeneration,
        long qpcFrequency)
    {
        Assert.IsTrue(Switch2InputProtocolIdentity.
            TryCreateProController2Usb(0x057E, 0x2069, 0x0201,
                out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, qpcFrequency,
            out var descriptor));
        return descriptor;
    }

    private static Switch2InputSessionDescriptor CreateJoyConDescriptor(
        Switch2ControllerModel model, ulong deviceGeneration,
        ulong transportGeneration, long qpcFrequency)
    {
        Guid characteristic = model == Switch2ControllerModel.JoyCon2Left ?
            Switch2InputCodec.JoyCon2Left07CharacteristicUuid :
            Switch2InputCodec.JoyCon2Right08CharacteristicUuid;
        Assert.IsTrue(Switch2InputProtocolIdentity.TryCreateBluetoothLe(
            Switch2InputCodec.ServiceUuid, characteristic, InputProperties,
            model, out var identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity,
            deviceGeneration, transportGeneration, qpcFrequency,
            out var descriptor));
        return descriptor;
    }

    private static Switch2InputCalibrationSnapshot CreateFallbackCalibration(
        Switch2ControllerModel model, ulong deviceGeneration = 1)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(model,
            deviceGeneration, out var calibration));
        return calibration;
    }

    private static Switch2CanonicalInputFrame CreateJoyConFrame(
        Switch2ControllerModel model, byte counter, long timestamp,
        ulong deviceGeneration = 1, ulong transportGeneration = 1,
        long qpcFrequency = 1_000_000)
    {
        Switch2InputSessionDescriptor descriptor = CreateJoyConDescriptor(
            model, deviceGeneration, transportGeneration, qpcFrequency);
        var session = new Switch2InputSession(descriptor,
            CreateFallbackCalibration(model, deviceGeneration));
        Assert.IsTrue(session.TryProcess(descriptor,
            BuildJoyConBody(counter, 0x123, 0xABC, 0), timestamp,
            out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static byte[] BuildUsbPacket(uint counter, uint buttons,
        ushort leftX, ushort leftY, ushort rightX, ushort rightY)
    {
        var packet = new byte[Switch2InputCodec.UsbPacketLength];
        packet[0] = (byte)Switch2InputReportKind.Common05;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4),
            counter);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1 + 0x04, 4),
            buttons);
        PackStick(packet, 1 + 0x0A, leftX, leftY);
        PackStick(packet, 1 + 0x0D, rightX, rightY);
        return packet;
    }

    private static byte[] BuildJoyConBody(byte counter, ushort x, ushort y,
        byte motionLength)
    {
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        body[0] = counter;
        body[1] = 0x42;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2, 2), 0xA55A);
        PackStick(body, 0x05, x, y);
        body[0x0F] = motionLength;
        return body;
    }

    private static byte[] BuildCalibration(ushort neutralX, ushort neutralY,
        ushort positiveX, ushort positiveY, ushort negativeX,
        ushort negativeY)
    {
        var record = new byte[Switch2CalibrationCodec.StickCalibrationLength];
        PackStick(record, 0, neutralX, neutralY);
        PackStick(record, 3, positiveX, positiveY);
        PackStick(record, 6, negativeX, negativeY);
        return record;
    }

    private static byte[] BuildUserCalibration(ushort neutralX,
        ushort neutralY, ushort positiveX, ushort positiveY,
        ushort negativeX, ushort negativeY)
    {
        var record = new byte[
            Switch2CalibrationCodec.UserStickCalibrationLength];
        record[0] = 0xB2;
        record[1] = 0xA1;
        BuildCalibration(neutralX, neutralY, positiveX, positiveY, negativeX,
            negativeY).CopyTo(record, 2);
        return record;
    }

    private static void PackStick(byte[] destination, int offset,
        ushort x, ushort y)
    {
        Assert.IsTrue(x <= 0x0FFF && y <= 0x0FFF);
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(((x >> 8) & 0x0F) |
            ((y & 0x0F) << 4));
        destination[offset + 2] = (byte)(y >> 4);
    }
}
