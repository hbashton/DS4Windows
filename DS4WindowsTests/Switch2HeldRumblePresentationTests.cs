using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2HeldRumblePresentationTests
{
    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    [DataRow(Switch2ControllerModel.ProController2)]
    public void ActualBluetoothFactoryUsesFirstActiveFrameWithoutLabContext(Switch2ControllerModel model)
    {
        var lease = new Lease(model);
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease, model, 7, 11, out var owner));
        try
        {
            Assert.IsTrue(owner.TryActivate());
            Assert.IsTrue(owner.TryCreateLane(ControllerFeedbackPublicationOrigin.TestPreview,
                ControllerFeedbackSource.Xbox360VirtualDevice, 19, 250_000, 100_000, out var lane));
            Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
            Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(40_000, 30_000, 0, 0), now));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, owner.TryPumpOnce(now, out _));
            Assert.AreEqual(1, lease.Payloads.Count);
            AssertHeldPacket(model, lease.Payloads[0], 40_000, 30_000);
        }
        finally { Assert.IsTrue(owner.TryStopAndRetireUntil(Environment.TickCount64 + 1000, 3)); }
    }

    [TestMethod]
    public void ActualJoinedBluetoothFactoryUsesFirstActiveFrameOnBothPhysicalLeases()
    {
        var left = new Lease(Switch2ControllerModel.JoyCon2Left);
        var right = new Lease(Switch2ControllerModel.JoyCon2Right);
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreateJoined(left, right,
            31, 41, 7, 11, 7, 11, out var owner));
        try
        {
            Assert.IsTrue(owner.TryActivate());
            Assert.IsTrue(owner.TryCreateLane(ControllerFeedbackPublicationOrigin.TestPreview,
                ControllerFeedbackSource.Xbox360VirtualDevice, 19, 250_000, 100_000, out var lane));
            Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
            Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(40_000, 30_000, 0, 0), now));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, owner.TryPumpOnce(now, out _));
            Assert.AreEqual(1, left.Payloads.Count);
            Assert.AreEqual(1, right.Payloads.Count);
            AssertHeldPacket(Switch2ControllerModel.JoyCon2Left, left.Payloads[0], 40_000, 30_000);
            AssertHeldPacket(Switch2ControllerModel.JoyCon2Right, right.Payloads[0], 40_000, 30_000);
            Assert.AreEqual(left.Payloads[0][1], right.Payloads[0][1]);
        }
        finally { Assert.IsTrue(owner.TryStopAndRetireUntil(Environment.TickCount64 + 1000, 3)); }
        AssertNeutralPacket(Switch2ControllerModel.JoyCon2Left, left.Payloads[^1]);
        AssertNeutralPacket(Switch2ControllerModel.JoyCon2Right, right.Payloads[^1]);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, true)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, true)]
    [DataRow(Switch2ControllerModel.ProController2, false)]
    [DataRow(Switch2ControllerModel.ProController2, true)]
    public void HeldPreviewChangesOnlyTailAmplitudesWithoutChangingCadenceOrCounter(
        Switch2ControllerModel model, bool light)
    {
        using var fixture = new Fixture(model);
        var delivery = Delivery(1, light ? (ushort)0 : ushort.MaxValue,
            light ? ushort.MaxValue : (ushort)0);
        Assert.IsTrue(fixture.Sink.TryDeliver(delivery));
        Assert.AreEqual(1, fixture.Lease.Payloads.Count);
        AssertHeldPacket(model, fixture.Lease.Payloads[0], delivery.Frame.BodyLow, delivery.Frame.BodyHigh);
        fixture.HostNow += fixture.Interval - 1;
        Assert.IsTrue(fixture.Sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(1, fixture.Lease.Payloads.Count);
        fixture.HostNow++;
        Assert.IsTrue(fixture.Sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(2, fixture.Lease.Payloads.Count);
        AssertWaveformsEqual(model, fixture.Lease.Payloads[0], fixture.Lease.Payloads[1]);
        Assert.AreEqual((fixture.Lease.Payloads[0][1] + 1) & 15, fixture.Lease.Payloads[1][1] & 15);
        Assert.IsTrue(fixture.Sink.NeedsSustainedRefresh);
    }

    [DataTestMethod]
    [DataRow((int)ControllerFeedbackPublicationOrigin.TestPreview)]
    [DataRow((int)ControllerFeedbackPublicationOrigin.NativeGame)]
    [DataRow((int)ControllerFeedbackPublicationOrigin.ProfileEffect)]
    [DataRow((int)ControllerFeedbackPublicationOrigin.AudioHaptics)]
    public void EveryOrdinaryCanonicalOriginUsesFirstActivePresentation(int originValue)
    {
        var origin = (ControllerFeedbackPublicationOrigin)originValue;
        using var fixture = new Fixture(Switch2ControllerModel.JoyCon2Left, origin: origin);
        Assert.IsTrue(fixture.Sink.TryDeliver(Delivery(1, 40_000, 30_000, origin)));
        AssertHeldPacket(Switch2ControllerModel.JoyCon2Left, fixture.Lease.Payloads[0], 40_000, 30_000);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    [DataRow(Switch2ControllerModel.ProController2)]
    public void UncertainPreviewKeepsExactPacketAndStopCannotResurrectIt(Switch2ControllerModel model)
    {
        using var fixture = new Fixture(model);
        var delivery = Delivery(1, 40_000, 30_000);
        fixture.Lease.Uncertain = true;
        Assert.IsFalse(fixture.Sink.TryDeliver(delivery));
        AssertHeldPacket(model, fixture.Lease.Payloads[0], 40_000, 30_000);
        fixture.Lease.Uncertain = false;
        Assert.IsTrue(fixture.Sink.TryDeliver(delivery));
        CollectionAssert.AreEqual(fixture.Lease.Payloads[0], fixture.Lease.Payloads[1]);
        Assert.IsTrue(fixture.Sink.TryDeliver(Stop()));
        AssertNeutralPacket(model, fixture.Lease.Payloads[2]);
        int writes = fixture.Lease.Payloads.Count;
        fixture.HostNow += 100_000;
        Assert.IsFalse(fixture.Sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(writes, fixture.Lease.Payloads.Count);
        Assert.IsFalse(fixture.Sink.NeedsSustainedRefresh);
    }

    [DataTestMethod]
    [DataRow((int)Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough, false)]
    [DataRow((int)Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand, false)]
    [DataRow((int)Switch2HdRumbleFeedbackFidelity.DualSenseAdaptiveTriggerApproximation, false)]
    [DataRow((int)Switch2HdRumbleFeedbackFidelity.NativeSwitch2ProfileEffect, false)]
    [DataRow((int)Switch2HdRumbleFeedbackFidelity.NativeSwitch2TestPreview, false)]
    [DataRow((int)Switch2HdRumbleFeedbackFidelity.NativeSwitch2ProfileEffect, true)]
    [DataRow((int)Switch2HdRumbleFeedbackFidelity.NativeSwitch2TestPreview, true)]
    public void RichSourcesKeepExactSubframesUncertainRetryAndRepeatPolicy(int fidelityValue, bool oneShot)
    {
        var fidelity = (Switch2HdRumbleFeedbackFidelity)fidelityValue;
        var source = fidelity switch
        {
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough => ControllerFeedbackSource.Switch2VirtualDevice,
            Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand or
                Switch2HdRumbleFeedbackFidelity.DualSenseAdaptiveTriggerApproximation => ControllerFeedbackSource.DualSenseVirtualDevice,
            _ => ControllerFeedbackSource.Xbox360VirtualDevice,
        };
        foreach (var model in new[] { Switch2ControllerModel.JoyCon2Left, Switch2ControllerModel.JoyCon2Right, Switch2ControllerModel.ProController2 })
        {
            using var fixture = new Fixture(model);
            var delivery = Delivery(1, 1, 1, source: source);
            var left = RichGroup(100);
            var right = RichGroup(300);
            Assert.IsTrue(fixture.Sink.TryStageSourcePreservedSynthesis(delivery.Frame, fidelity, left, right,
                oneShot ? Switch2HdRumbleRepeatPolicy.OneShot : Switch2HdRumbleRepeatPolicy.SustainWhileFresh));
            fixture.Lease.Uncertain = true;
            Assert.IsFalse(fixture.Sink.TryDeliver(delivery));
            AssertGroups(model, fixture.Lease.Payloads[0], left, right);
            fixture.Lease.Uncertain = false;
            Assert.IsTrue(fixture.Sink.TryDeliver(delivery));
            CollectionAssert.AreEqual(fixture.Lease.Payloads[0], fixture.Lease.Payloads[1]);
            bool repeat = !oneShot && fidelity is not (Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough or
                Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand);
            Assert.AreEqual(repeat, fixture.Sink.NeedsSustainedRefresh);
            fixture.HostNow += fixture.Interval;
            Assert.IsTrue(fixture.Sink.MaintenanceSink.TryDeliver(delivery));
            Assert.AreEqual(repeat ? 3 : 2, fixture.Lease.Payloads.Count);
            if (repeat) AssertGroups(model, fixture.Lease.Payloads[2], left, right);
        }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void ImpulseReleaseAndIgnoredRawTriggersRemainByteIdentical(bool release, bool bodyOnlyPolicy)
    {
        var policy = bodyOnlyPolicy ? Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility :
            Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating;
        using var fixture = new Fixture(Switch2ControllerModel.ProController2, policy);
        var delivery = Delivery(1, 30_000, 20_000, leftTrigger: release ? (ushort)0 : (ushort)40_000,
            rightTrigger: release ? (ushort)0 : (ushort)10_000);
        if (release)
            Assert.IsTrue(fixture.Sink.TryStageImpulseReleasePresentation(delivery.Frame, 40_000, 10_000, 1));
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(delivery.Frame.Source, ControllerFeedbackCommand.Apply,
            delivery.Frame.Actuators, 30_000, 20_000, 40_000, 10_000, delivery.Frame.Sequence, 7, 11, 19,
            delivery.Frame.TimestampMicroseconds, delivery.Frame.TimeToLiveMicroseconds, out var renderedFrame));
        Assert.IsTrue(Switch2HdRumbleFeedbackTranslator.TryTranslate(renderedFrame,
            renderedFrame.TimestampMicroseconds, policy, Switch2HdRumbleImpulseTuning.Default,
            Switch2HdRumbleBodyTuning.Default, out var expected));
        Assert.IsTrue(fixture.Sink.TryDeliver(delivery));
        AssertGroups(Switch2ControllerModel.ProController2, fixture.Lease.Payloads[0], expected.Left, expected.Right);
    }

    [TestMethod]
    public void ProUsbPhysicalWriterPreservesFirstFrameCadenceRetryCounterAndTerminalStop()
    {
        var lease = new UsbLease();
        var writer = new Switch2ProUsbHdRumblePhysicalWriter(lease, 7, 11, initialCounter: 15);
        ulong hostNow = 1_000;
        var sink = new Switch2HdRumbleDeliverySink(writer, 7, 11,
            minimumMaintenanceIntervalMicroseconds: 12_000, hostWriteStartClock: () => hostNow);
        try
        {
            var delivery = Delivery(1, 40_000, 30_000);
            lease.Uncertain = true;
            Assert.IsFalse(sink.TryDeliver(delivery));
            Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(lease.Reports[0], out byte counter,
                out var left, out var right, out _));
            Assert.AreEqual((byte)15, counter);
            AssertHeldGroup(left, 40_000, 30_000);
            AssertHeldGroup(right, 40_000, 30_000);
            lease.Uncertain = false;
            Assert.IsTrue(sink.TryDeliver(delivery));
            CollectionAssert.AreEqual(lease.Reports[0], lease.Reports[1]);
            hostNow += 11_999;
            Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
            Assert.AreEqual(2, lease.Reports.Count);
            hostNow++;
            Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
            Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(lease.Reports[2], out counter,
                out var refreshedLeft, out var refreshedRight, out _));
            Assert.AreEqual((byte)0, counter);
            Assert.AreEqual(left, refreshedLeft);
            Assert.AreEqual(right, refreshedRight);
            Assert.IsTrue(sink.TryDeliver(Stop()));
            Assert.IsTrue(Switch2UsbHdRumbleCodec.TryDecodeProController(lease.Reports[3], out _, out left, out right, out _));
            Assert.AreEqual(default(Switch2HdRumbleGroup), left);
            Assert.AreEqual(left, right);
            Assert.IsFalse(sink.NeedsSustainedRefresh);
            Assert.IsFalse(sink.MaintenanceSink.TryDeliver(delivery));
            Assert.AreEqual(4, lease.Reports.Count);
        }
        finally { lease.Uncertain = false; Assert.IsTrue(sink.TryDeliver(Stop())); Assert.IsTrue(sink.TryRetire()); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WarmedActualSinkAndPhysicalEncoderPreserveStrictAllocationGate(bool capturePositiveControl)
    {
        using var fixture = new Fixture(Switch2ControllerModel.JoyCon2Left);
        fixture.Lease.Capture = false;
        ulong sequence = 1;
        for (int index = 0; index < 2_000; index++)
            Assert.IsTrue(fixture.Sink.TryDeliver(Delivery(sequence++, 40_000, 30_000)));
        long allocated;
        bool valid = true;
        fixture.Lease.Capture = capturePositiveControl;
        int count = capturePositiveControl ? 1 : 20_000;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < count; index++)
                valid &= fixture.Sink.TryDeliver(Delivery(sequence++, 40_000, 30_000));
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.IsTrue(valid);
        Assert.AreEqual(2_000 + count, fixture.Lease.Writes);
        if (capturePositiveControl)
        {
            Assert.IsTrue(allocated >= Switch2BluetoothHdRumbleCodec.JoyConPayloadLength,
                "The same physical-writer packet clone must be detected as real allocation.");
            Assert.AreEqual(1, fixture.Lease.Payloads.Count);
        }
        else Assert.AreEqual(0L, allocated);
    }

    private static Switch2HdRumbleGroup RichGroup(int basis) => new(
        new((ushort)(basis + 1), 11, (ushort)(basis + 101), 21),
        new((ushort)(basis + 2), 12, (ushort)(basis + 102), 22),
        new((ushort)(basis + 3), 13, (ushort)(basis + 103), 23));

    private static void AssertHeldGroup(in Switch2HdRumbleGroup actual, ushort low, ushort high)
    {
        var expected = Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(low, high);
        Assert.AreEqual(expected.First, actual.First);
        AssertTailAmplitudesOnly(expected.Second, actual.Second);
        AssertTailAmplitudesOnly(expected.Third, actual.Third);
    }

    private static void AssertHeldPacket(Switch2ControllerModel model, byte[] payload, ushort low, ushort high)
    {
        DecodeGroups(model, payload, out var left, out var right);
        AssertHeldGroup(left, low, high);
        AssertHeldGroup(right, low, high);
    }

    private static void DecodeGroups(Switch2ControllerModel model, byte[] payload,
        out Switch2HdRumbleGroup left, out Switch2HdRumbleGroup right)
    {
        if (model == Switch2ControllerModel.ProController2)
            Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(payload, out _, out left, out right, out _));
        else
        {
            Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(payload, out _, out left, out _));
            right = left;
        }
    }

    private static void AssertGroups(Switch2ControllerModel model, byte[] payload,
        in Switch2HdRumbleGroup expectedLeft, in Switch2HdRumbleGroup expectedRight)
    {
        DecodeGroups(model, payload, out var left, out var right);
        Assert.AreEqual(model == Switch2ControllerModel.JoyCon2Right ? expectedRight : expectedLeft, left);
        Assert.AreEqual(model == Switch2ControllerModel.JoyCon2Left ? expectedLeft : expectedRight, right);
    }

    private static void AssertNeutralPacket(Switch2ControllerModel model, byte[] payload)
    {
        var neutral = default(Switch2HdRumbleGroup);
        AssertGroups(model, payload, neutral, neutral);
    }

    private static void AssertWaveformsEqual(Switch2ControllerModel model, byte[] first, byte[] second)
    {
        DecodeGroups(model, first, out var left, out var right);
        AssertGroups(model, second, left, right);
    }

    private static void AssertTailAmplitudesOnly(in Switch2HdRumbleSubframe original, in Switch2HdRumbleSubframe actual)
    {
        Assert.AreEqual(original.Oscillator0ControlCode, actual.Oscillator0ControlCode);
        Assert.AreEqual(original.Oscillator1ControlCode, actual.Oscillator1ControlCode);
        Assert.AreEqual((ushort)0, actual.Oscillator0AmplitudeCode);
        Assert.AreEqual((ushort)0, actual.Oscillator1AmplitudeCode);
    }

    private static ControllerFeedbackDelivery Delivery(ulong sequence, ushort low, ushort high,
        ControllerFeedbackPublicationOrigin origin = ControllerFeedbackPublicationOrigin.TestPreview,
        ushort leftTrigger = 0, ushort rightTrigger = 0,
        ControllerFeedbackSource source = ControllerFeedbackSource.XboxOneVirtualDevice)
    {
        if (!ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now) ||
            !ControllerFeedbackFrame.TryCreate(source, ControllerFeedbackCommand.Apply, ControllerFeedbackActuators.All,
                low, high, leftTrigger, rightTrigger, sequence, 7, 11, 19, now, 250_000, out var frame))
            throw new InvalidOperationException("Invalid test feedback frame.");
        return new ControllerFeedbackDelivery(ControllerFeedbackDeliveryDisposition.Frame, origin, frame, 7, 11, 31);
    }

    private static ControllerFeedbackDelivery Stop(ControllerFeedbackPublicationOrigin origin = ControllerFeedbackPublicationOrigin.TestPreview) =>
        new(ControllerFeedbackDeliveryDisposition.Stop, origin, default, 7, 11, 31);

    private sealed class Fixture : IDisposable
    {
        internal readonly Lease Lease;
        internal readonly Switch2HdRumbleDeliverySink Sink;
        internal readonly ulong Interval;
        private readonly ControllerFeedbackPublicationOrigin origin;
        internal ulong HostNow = 1_000;

        internal Fixture(Switch2ControllerModel model,
            Switch2HdRumbleFeedbackPolicy policy = Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            ControllerFeedbackPublicationOrigin origin = ControllerFeedbackPublicationOrigin.TestPreview)
        {
            this.origin = origin;
            Interval = (ulong)Switch2RumbleMaintenanceWorker.BluetoothIntervalFor(model, joinedPair: false) * 1000;
            Lease = new Lease(model);
            var writer = new Switch2BluetoothHdRumblePhysicalWriter(Lease, model, 7, 11);
            Sink = new Switch2HdRumbleDeliverySink(writer, 7, 11, policy,
                minimumMaintenanceIntervalMicroseconds: Interval, hostWriteStartClock: () => HostNow);
        }

        public void Dispose()
        {
            Lease.Uncertain = false;
            Assert.IsTrue(Sink.TryDeliver(Stop(origin)));
            Assert.IsTrue(Sink.TryRetire());
        }
    }

    private sealed class Lease(Switch2ControllerModel expectedModel) : ISwitch2BluetoothHdRumbleBindableTransportLease
    {
        internal readonly List<byte[]> Payloads = new();
        internal bool Capture = true;
        internal bool Uncertain;
        internal int Writes;
        public bool HasHdRumbleOutput => true;
        public bool Authenticates(Switch2ControllerModel model, ulong device, ulong transport) => model == expectedModel && device == 7 && transport == 11;
        public bool TryBindHdRumbleLifetime(Switch2ControllerModel model, ulong device, ulong transport) => Authenticates(model, device, transport);
        public Switch2BluetoothHdRumbleTransportWriteResult TryWritePayload(ReadOnlySpan<byte> payload,
            Switch2ControllerModel model, ulong device, ulong transport)
        {
            Writes++;
            if (Capture) Payloads.Add(payload.ToArray());
            return Uncertain ? Switch2BluetoothHdRumbleTransportWriteResult.Uncertain(model, device, transport,
                Switch2BluetoothHdRumbleTransportWriteFailure.DependencyThrew) :
                Switch2BluetoothHdRumbleTransportWriteResult.Complete(model, device, transport, payload.Length);
        }
    }

    private sealed class UsbLease : ISwitch2ProUsbHdRumbleTransportLease
    {
        internal readonly List<byte[]> Reports = new();
        internal bool Uncertain;
        public bool Authenticates(Switch2ControllerModel model, ulong device, ulong transport) =>
            model == Switch2ControllerModel.ProController2 && device == 7 && transport == 11;
        public Switch2ProUsbHdRumbleTransportWriteResult TryWriteReport(ReadOnlySpan<byte> report,
            Switch2ControllerModel model, ulong device, ulong transport)
        {
            Reports.Add(report.ToArray());
            return Uncertain ? Switch2ProUsbHdRumbleTransportWriteResult.Uncertain(model, device, transport,
                Switch2ProUsbHdRumbleTransportWriteFailure.DependencyThrew) :
                Switch2ProUsbHdRumbleTransportWriteResult.Complete(model, device, transport, report.Length);
        }
    }
}
