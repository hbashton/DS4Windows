using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2JoyConCounterResetTests
{
    // The counter pairs and these right-side completion times come from the
    // 2026-09-08 dump. Buttons, stick positions and successor counters are
    // synthetic. The same advancing clock fixture also exercises the left.
    private const long BeforeTime = 2_309_314_172_615;
    private const long ResetTime = 2_309_314_321_224;
    private const long Frequency = 10_000_000;
    private const ulong PairEpoch = 24;

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, 1_431_651u, 12u)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, 1_431_652u, 13u)]
    public void StandaloneRuntimePublishesReleaseAtObservedCounterResetWithoutRetiring(
        Switch2ControllerModel model, uint before, uint reset)
    {
        var session = Session(model);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(model, 7, 9,
            out var runtime, out var created), created.ToString());
        Assert.IsTrue(Switch2BluetoothRuntimeInputSink.TryCreateBound(session.Descriptor,
            runtime, 1_000, Switch2RuntimeTerminalScheduler.Instance, out var sink,
            out _, out var failure), failure.ToString());
        runtime.StartUpdate();
        sink.PublishJoyCon(Frame(session, before, BeforeTime, true));
        Assert.AreNotEqual(0u, RawButtons(runtime, model));
        var discontinuity = Frame(session, reset, ResetTime, false);
        Assert.AreEqual(Switch2CounterSequenceKind.BackwardOrOutOfOrder, discontinuity.CounterSequence);
        sink.PublishJoyCon(discontinuity);
        Assert.AreEqual(0u, RawButtons(runtime, model));
        sink.PublishJoyCon(Frame(session, reset + 15, ResetTime + 150_000, true));
        Assert.AreNotEqual(0u, RawButtons(runtime, model));
        Assert.AreEqual(3L, sink.PublishedCount);
        Assert.IsFalse(sink.TerminalRequested);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Active, runtime.RuntimeState);
    }

    [DataTestMethod]
    [DataRow(Switch2JoyConProfileMode.StandaloneHorizontalLeft)]
    [DataRow(Switch2JoyConProfileMode.StandaloneVerticalLeft)]
    [DataRow(Switch2JoyConProfileMode.StandaloneHorizontalRight)]
    [DataRow(Switch2JoyConProfileMode.StandaloneVerticalRight)]
    public void EveryStandaloneLayoutRetainsArrivalBaselineAcrossReset(Switch2JoyConProfileMode mode)
    {
        bool left = mode is Switch2JoyConProfileMode.StandaloneHorizontalLeft or
            Switch2JoyConProfileMode.StandaloneVerticalLeft;
        var session = Session(left ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right);
        Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(mode, session.Descriptor, out var mapper));
        uint reset = left ? 12u : 13u;
        uint[] counters = { left ? 1_431_651u : 1_431_652u, reset, reset + 15 };
        for (int index = 0; index < counters.Length; index++)
        {
            var canonical = Frame(session, counters[index], BeforeTime + index * 150_000, index == 0);
            Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(mapper, canonical,
                out var next, out var mapped, out var failure), failure.ToString());
            mapper = next;
            Assert.AreEqual(counters[index], left ? mapper.LastLeftCounter : mapper.LastRightCounter);
            Assert.AreEqual(index == 0, mapped.Buttons != Switch2JoyConProfileButton.None);
            if (index == 2)
            {
                Assert.AreEqual(Switch2CounterSequenceKind.Forward, canonical.CounterSequence);
                Assert.AreEqual(15u, canonical.CounterDeltaRaw);
            }
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void JoinedRuntimePublishesResetFromEitherHalfWithoutDroppingThePair(bool resetRight)
    {
        var left = Session(Switch2ControllerModel.JoyCon2Left);
        var right = Session(Switch2ControllerModel.JoyCon2Right);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(23, PairEpoch, 7, 9, 7, 9,
            out var runtime, out var created), created.ToString());
        Assert.IsTrue(Switch2JoyConJoinedRuntimeInputSink.TryCreateBound(PairEpoch,
            left.Descriptor, right.Descriptor, runtime, new Switch2JoyConPairPolicy(100_000),
            1_000, Switch2RuntimeTerminalScheduler.Instance, out var sink, out _, out var failure), failure.ToString());
        runtime.StartUpdate();
        sink.PublishJoyCon(Frame(left, 1_431_651, BeforeTime, true));
        sink.PublishJoyCon(Frame(right, 1_431_652, BeforeTime, true));
        var target = resetRight ? right : left;
        uint reset = resetRight ? 13u : 12u;
        sink.PublishJoyCon(Frame(target, reset, ResetTime, false));
        Assert.AreEqual(0u, RawButtons(runtime, target.Descriptor.Identity.Model));
        Assert.AreNotEqual(0u, RawButtons(runtime, resetRight ? Switch2ControllerModel.JoyCon2Left : Switch2ControllerModel.JoyCon2Right));
        sink.PublishJoyCon(Frame(target, reset + 15, ResetTime + 150_000, true));
        Assert.AreEqual(3L, sink.PublishedCount);
        Assert.AreEqual(4L, sink.ConsumedCount);
        Assert.IsTrue(sink.LeftAttached && sink.RightAttached);
        Assert.IsFalse(sink.TerminalRequested);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Active, runtime.RuntimeState);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, 1_431_651u, 12u)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, 1_431_652u, 13u)]
    public void ReplayAndLiveSessionPreserveResetEvidenceAndAdvanceTheNextBaseline(
        Switch2ControllerModel model, uint before, uint reset)
    {
        var session = Session(model);
        var expected = new List<(Switch2CounterSequenceKind Sequence, uint Delta)>();
        var fixtures = new List<Switch2FixtureEnvelope>();
        var source = Switch2FixtureSource.Synthetic("fact-8d8d4b81bd1f449fa640f129fb5a1a70");
        uint[] counters = { before, reset, reset + 15, reset + 15 };
        for (int index = 0; index < counters.Length; index++)
        {
            var body = Body(counters[index], model, false);
            Assert.IsTrue(session.TryProcess(session.Descriptor, body, index, out var frame, out _));
            expected.Add((frame.CounterSequence, frame.CounterDeltaRaw));
            fixtures.Add(Switch2FixtureEnvelope.CreateBluetoothLe(
                "stream-11b03d25c8824fa0a1b2544b8303d7f9", source, model, "unknown", 7, 0,
                "clock-a2920631b6c14d1d922a9ab86e235aae", Frequency, index,
                Switch2InputCodec.ServiceUuid, Switch2InputCodec.Common05CharacteristicUuid,
                Switch2GattProperty.Read | Switch2GattProperty.Notify, body));
        }
        var actual = new List<(Switch2CounterSequenceKind Sequence, uint Delta)>();
        Assert.IsTrue(Switch2ReplayEngine.TryReplay(fixtures,
            (in Switch2ReplayEvent replay) => actual.Add((replay.CounterSequence, replay.CounterDelta)),
            out var failure), failure.Kind.ToString());
        CollectionAssert.AreEqual(expected, actual);
        Assert.AreEqual((Switch2CounterSequenceKind.BackwardOrOutOfOrder, unchecked(reset - before)), actual[1]);
        Assert.AreEqual((Switch2CounterSequenceKind.Forward, 15u), actual[2]);
        Assert.AreEqual((Switch2CounterSequenceKind.Duplicate, 0u), actual[3]);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void ResetCannotBypassHostTimestampDescriptorOrSupportedReportFences(Switch2ControllerModel model)
    {
        var session = Session(model);
        _ = Frame(session, 1_431_652, BeforeTime, true);
        var body = Body(13, model, false);
        Assert.IsFalse(session.TryProcess(session.Descriptor, body, BeforeTime - 1, out _, out var clock));
        Assert.AreEqual(Switch2InputSessionFailure.TimestampRegression, clock);
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(session.Descriptor.Identity, 7, 8,
            Frequency, out var stale));
        Assert.IsFalse(session.TryProcess(stale, body, ResetTime, out _, out var lifetime));
        Assert.AreEqual(Switch2InputSessionFailure.DescriptorMismatch, lifetime);
        Assert.AreEqual(Switch2CounterSequenceKind.Duplicate,
            Frame(session, 1_431_652, ResetTime, false).CounterSequence);
        Assert.IsFalse(Switch2CounterSequence.UsesArrivalOrdering(model, Switch2Transport.Usb, Switch2InputReportKind.Common05));
        Assert.IsFalse(Switch2CounterSequence.UsesArrivalOrdering(model, Switch2Transport.BluetoothLe, Switch2InputReportKind.JoyCon2Left07));
        Assert.IsFalse(Switch2CounterSequence.UsesArrivalOrdering(model, Switch2Transport.BluetoothLe, Switch2InputReportKind.JoyCon2Right08));
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2, Switch2Transport.Usb)]
    [DataRow(Switch2ControllerModel.ProController2, Switch2Transport.BluetoothLe)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, Switch2Transport.BluetoothLe)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, Switch2Transport.BluetoothLe)]
    public void ValidSupportedCommonReportsAdmitArbitraryCounterJumpsWithoutInventingAModulus(
        Switch2ControllerModel model, Switch2Transport transport)
    {
        var session = Session(model, transport);
        bool pro = model == Switch2ControllerModel.ProController2;
        Switch2JoyConProfileMapperState mapper = default;
        if (!pro)
            Assert.IsTrue(Switch2JoyConProfileInputMapper.TryCreateStandalone(
                model == Switch2ControllerModel.JoyCon2Left ? Switch2JoyConProfileMode.StandaloneVerticalLeft :
                    Switch2JoyConProfileMode.StandaloneVerticalRight, session.Descriptor, out mapper));
        uint[] counters = { uint.MaxValue - 3, 0, 0, uint.MaxValue - 15, 4,
            1_431_652, 13, 12, 9, uint.MaxValue, 0, 42, 2, 17 };
        for (int index = 0; index < counters.Length; index++)
        {
            byte[] body = Body(counters[index], model, index % 2 == 0);
            byte[] packet = transport == Switch2Transport.Usb ? new byte[64] : body;
            if (transport == Switch2Transport.Usb)
            { packet[0] = 0x05; body.CopyTo(packet, 1); }
            Assert.IsTrue(session.TryProcess(session.Descriptor, packet, index,
                out var frame, out var sessionFailure), sessionFailure.ToString());
            Assert.AreEqual(counters[index], frame.DeviceCounterRaw);
            if (index > 0)
            {
                var expected = Switch2CounterSequence.Classify(counters[index], counters[index - 1], 32, out uint delta);
                Assert.AreEqual(expected, frame.CounterSequence);
                Assert.AreEqual(delta, frame.CounterDeltaRaw);
            }
            if (pro)
                Assert.IsTrue(Switch2ProProfileInputMapper.TryMap(frame, out _, out var proFailure), proFailure.ToString());
            else
            {
                Assert.IsTrue(Switch2JoyConProfileInputMapper.TryMapStandalone(mapper, frame,
                    out var next, out _, out var joyConFailure), joyConFailure.ToString());
                mapper = next;
            }
        }
        Assert.IsFalse(Switch2CounterSequence.UsesArrivalOrdering(Switch2ControllerModel.Unknown,
            transport, Switch2InputReportKind.Common05));
    }

    [TestMethod]
    public void RepeatedDiscontinuitiesAllocateNothingThroughJoinedRuntime()
    {
        var left = Session(Switch2ControllerModel.JoyCon2Left);
        var right = Session(Switch2ControllerModel.JoyCon2Right);
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreateJoinedJoyCon(23, PairEpoch, 7, 9, 7, 9,
            out var runtime, out _));
        Assert.IsTrue(Switch2JoyConJoinedRuntimeInputSink.TryCreateBound(PairEpoch,
            left.Descriptor, right.Descriptor, runtime, new Switch2JoyConPairPolicy(100_000),
            1_000, Switch2RuntimeTerminalScheduler.Instance, out var sink, out _, out _));
        runtime.StartUpdate();
        var leftBody = Body(0, Switch2ControllerModel.JoyCon2Left, false);
        var rightBody = Body(0, Switch2ControllerModel.JoyCon2Right, false);
        uint[] counters = { 1_431_652, 13, 12, uint.MaxValue, 0, 15 };
        int sequence = 0;
        bool valid = true;
        Publish(2_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Publish(20_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(22_000L, sink.ConsumedCount);
        Assert.AreEqual(21_999L, sink.PublishedCount);
        Assert.IsFalse(sink.TerminalRequested);

        void Publish(int count)
        {
            for (int index = 0; index < count; index++, sequence++)
            {
                var session = (sequence & 1) == 0 ? left : right;
                var body = (sequence & 1) == 0 ? leftBody : rightBody;
                BinaryPrimitives.WriteUInt32LittleEndian(body, counters[(sequence / 2) % counters.Length]);
                valid &= session.TryProcess(session.Descriptor, body, sequence, out var frame, out _);
                sink.PublishJoyCon(frame);
            }
        }
    }

    private static Switch2InputSession Session(Switch2ControllerModel model,
        Switch2Transport transport = Switch2Transport.BluetoothLe)
    {
        Switch2InputProtocolIdentity identity;
        Assert.IsTrue(transport == Switch2Transport.Usb ?
            Switch2InputProtocolIdentity.TryCreateProController2Usb(0x057E, 0x2069, 0x0201, out identity) :
            Switch2InputProtocolIdentity.TryCreateBluetoothLe(Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.Common05CharacteristicUuid, Switch2GattProperty.Read | Switch2GattProperty.Notify,
                model, out identity));
        Assert.IsTrue(Switch2InputSessionDescriptor.TryCreate(identity, 7, 9, Frequency, out var descriptor));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(model, 7, out var calibration));
        return new Switch2InputSession(descriptor, calibration);
    }

    private static Switch2CanonicalInputFrame Frame(Switch2InputSession session, uint counter, long timestamp, bool pressed)
    {
        Assert.IsTrue(session.TryProcess(session.Descriptor, Body(counter, session.Descriptor.Identity.Model, pressed),
            timestamp, out var frame, out var failure), failure.ToString());
        return frame;
    }

    private static uint RawButtons(Switch2RuntimeInputDevice runtime, Switch2ControllerModel model) =>
        model == Switch2ControllerModel.JoyCon2Left ? runtime.getRawCurrentState().Switch2JoyConRawInputStatus.LeftRawButtonBits :
            runtime.getRawCurrentState().Switch2JoyConRawInputStatus.RightRawButtonBits;

    private static byte[] Body(uint counter, Switch2ControllerModel model, bool pressed)
    {
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), pressed ? 1u <<
            (model == Switch2ControllerModel.JoyCon2Left ? 16 : 2) : 0u);
        body[11] = body[14] = 0x08;
        body[12] = body[15] = 0x80;
        return body;
    }
}
