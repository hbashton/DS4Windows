using System.Reflection;
using System.Security.Cryptography;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2HeldSubframeExperimentTests
{
    [DataTestMethod]
    [DataRow(false, "neutral-tails", Switch2ControllerModel.JoyCon2Left, Switch2Transport.BluetoothLe, false, false)]
    [DataRow(true, null, Switch2ControllerModel.JoyCon2Left, Switch2Transport.BluetoothLe, false, false)]
    [DataRow(true, "1", Switch2ControllerModel.JoyCon2Left, Switch2Transport.BluetoothLe, false, false)]
    [DataRow(true, "Neutral-Tails", Switch2ControllerModel.JoyCon2Left, Switch2Transport.BluetoothLe, false, false)]
    [DataRow(true, "neutral-tails ", Switch2ControllerModel.JoyCon2Left, Switch2Transport.BluetoothLe, false, false)]
    [DataRow(true, "neutral-tails", Switch2ControllerModel.JoyCon2Left, Switch2Transport.BluetoothLe, false, true)]
    [DataRow(true, "neutral-tails", Switch2ControllerModel.JoyCon2Right, Switch2Transport.BluetoothLe, false, true)]
    [DataRow(true, "neutral-tails", Switch2ControllerModel.JoyCon2Left, Switch2Transport.BluetoothLe, true, false)]
    [DataRow(true, "neutral-tails", Switch2ControllerModel.ProController2, Switch2Transport.BluetoothLe, false, false)]
    [DataRow(true, "neutral-tails", Switch2ControllerModel.JoyCon2Left, Switch2Transport.Usb, false, false)]
    [DataRow(true, "neutral-tails", Switch2ControllerModel.Unknown, Switch2Transport.BluetoothLe, false, false)]
    public void EligibilityRequiresExactPortableStandaloneBluetoothOptIn(bool lab, string value,
        Switch2ControllerModel model, Switch2Transport transport, bool joined, bool expected) =>
        Assert.AreEqual(expected, Switch2HeldSubframeExperiment.ShouldEnable(lab, value, model, transport, joined));

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void ActualBluetoothFactoryCapturesOptInOnceForThisLifetime(Switch2ControllerModel model)
    {
        using var lab = new LabScope();
        var lease = new Lease(model);
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease, model, 7, 11, out var owner));
        try
        {
            Assert.IsTrue(owner.TryActivate());
            Environment.SetEnvironmentVariable(Switch2HeldSubframeExperiment.EnvironmentVariable, null);
            Assert.IsFalse(Switch2HeldSubframeExperiment.ReadColdConfiguration(model, Switch2Transport.BluetoothLe, false));
            Assert.IsTrue(owner.TryCreateLane(ControllerFeedbackPublicationOrigin.TestPreview,
                ControllerFeedbackSource.Xbox360VirtualDevice, 19, 250_000, 100_000, out var lane));
            Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
            Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(40_000, 30_000, 0, 0), now));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, owner.TryPumpOnce(now, out _));
            Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(lease.Payloads[0], out _, out var group, out _));
            Assert.IsTrue(group.First.HasNonzeroAmplitude);
            Assert.IsFalse(group.Second.HasNonzeroAmplitude || group.Third.HasNonzeroAmplitude,
                "Changing the environment after construction must not mutate the lifetime's chosen bytes.");
        }
        finally { Assert.IsTrue(owner.TryStopAndRetireUntil(Environment.TickCount64 + 1000, 3)); }
    }

    [TestMethod]
    public void ActualProAndJoinedFactoriesKeepExperimentDisabledEvenInsideOptedInLab()
    {
        using var lab = new LabScope();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(new Lease(Switch2ControllerModel.ProController2),
            Switch2ControllerModel.ProController2, 7, 11, out var pro));
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreateJoined(new Lease(Switch2ControllerModel.JoyCon2Left),
            new Lease(Switch2ControllerModel.JoyCon2Right), 31, 41, 7, 11, 7, 11, out var joined));
        try
        {
            foreach (var owner in new[] { pro, joined })
            {
                var sink = typeof(Switch2BluetoothFeedbackLifetime).GetField("sink", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
                Assert.IsFalse((bool)typeof(Switch2HdRumbleDeliverySink).GetField("labNeutralPreviewTails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sink)!);
                Assert.IsTrue(owner.TryActivate());
            }
        }
        finally
        {
            Assert.IsTrue(pro.TryStopAndRetireUntil(Environment.TickCount64 + 1000, 3));
            Assert.IsTrue(joined.TryStopAndRetireUntil(Environment.TickCount64 + 1000, 3));
        }
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, true)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, true)]
    public void OptedInPreviewChangesOnlyTrailingAmplitudesAtActualPhysicalWriter(
        Switch2ControllerModel model, bool light)
    {
        using var fixture = new Fixture(model, experiment: true);
        var delivery = Delivery(1, light ? (ushort)0 : ushort.MaxValue,
            light ? ushort.MaxValue : (ushort)0);
        Assert.IsTrue(fixture.Sink.TryDeliver(delivery));
        Assert.AreEqual(1, fixture.Lease.Payloads.Count);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(fixture.Lease.Payloads[0],
            out _, out var group, out _));
        var expected = Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(
            delivery.Frame.BodyLow, delivery.Frame.BodyHigh);
        Assert.AreEqual(expected.First, group.First);
        AssertTailAmplitudesOnly(expected.Second, group.Second);
        AssertTailAmplitudesOnly(expected.Third, group.Third);

        fixture.HostNow += 9_999;
        Assert.IsTrue(fixture.Sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(1, fixture.Lease.Payloads.Count);
        fixture.HostNow++;
        Assert.IsTrue(fixture.Sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(2, fixture.Lease.Payloads.Count);
        CollectionAssert.AreEqual(fixture.Lease.Payloads[0][2..], fixture.Lease.Payloads[1][2..]);
        Assert.AreEqual((fixture.Lease.Payloads[0][1] + 1) & 15, fixture.Lease.Payloads[1][1] & 15);
        Assert.IsTrue(fixture.Sink.NeedsSustainedRefresh);
    }

    [DataTestMethod]
    [DataRow(false, (int)ControllerFeedbackPublicationOrigin.TestPreview)]
    [DataRow(true, (int)ControllerFeedbackPublicationOrigin.NativeGame)]
    [DataRow(true, (int)ControllerFeedbackPublicationOrigin.ProfileEffect)]
    public void DefaultAndNonPreviewCanonicalPacketsStayByteIdentical(
        bool experiment, int origin)
    {
        using var fixture = new Fixture(Switch2ControllerModel.JoyCon2Left, experiment,
            origin: (ControllerFeedbackPublicationOrigin)origin);
        var delivery = Delivery(1, 40_000, 30_000, (ControllerFeedbackPublicationOrigin)origin);
        Assert.IsTrue(fixture.Sink.TryDeliver(delivery));
        byte[] expected = new byte[Switch2BluetoothHdRumbleCodec.JoyConPayloadLength];
        var group = Switch2HdRumbleFeedbackTranslator.CreateCompatibilityGroup(40_000, 30_000);
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(fixture.Lease.Payloads[0],
            out byte counter, out _, out _));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryEncodeJoyCon(counter, group, expected));
        CollectionAssert.AreEqual(expected, fixture.Lease.Payloads[0]);
    }

    [TestMethod]
    public void UncertainPreviewKeepsExactPacketAndStopCannotResurrectIt()
    {
        using var fixture = new Fixture(Switch2ControllerModel.JoyCon2Left, experiment: true);
        var delivery = Delivery(1, 40_000, 30_000);
        fixture.Lease.Uncertain = true;
        Assert.IsFalse(fixture.Sink.TryDeliver(delivery));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(fixture.Lease.Payloads[0],
            out _, out var group, out _));
        Assert.IsFalse(group.Second.HasNonzeroAmplitude, "The first uncertain write must already use the experiment, not only its retry.");
        fixture.Lease.Uncertain = false;
        Assert.IsTrue(fixture.Sink.TryDeliver(delivery));
        CollectionAssert.AreEqual(fixture.Lease.Payloads[0], fixture.Lease.Payloads[1]);
        Assert.IsTrue(fixture.Sink.TryDeliver(Stop()));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(fixture.Lease.Payloads[2],
            out _, out var stopped, out _));
        Assert.IsFalse(stopped.First.HasNonzeroAmplitude || stopped.Second.HasNonzeroAmplitude || stopped.Third.HasNonzeroAmplitude);
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
        using var fixture = new Fixture(Switch2ControllerModel.JoyCon2Left, experiment: true);
        var delivery = Delivery(1, 1, 1, source: source);
        var left = new Switch2HdRumbleGroup(new(101, 11, 201, 21), new(102, 12, 202, 22), new(103, 13, 203, 23));
        var right = new Switch2HdRumbleGroup(new(301, 31, 401, 41), new(302, 32, 402, 42), new(303, 33, 403, 43));
        Assert.IsTrue(fixture.Sink.TryStageSourcePreservedSynthesis(delivery.Frame, fidelity, left, right,
            oneShot ? Switch2HdRumbleRepeatPolicy.OneShot : Switch2HdRumbleRepeatPolicy.SustainWhileFresh));
        fixture.Lease.Uncertain = true;
        Assert.IsFalse(fixture.Sink.TryDeliver(delivery));
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeJoyCon(fixture.Lease.Payloads[0], out _, out var actual, out _));
        Assert.AreEqual(left, actual);
        fixture.Lease.Uncertain = false;
        Assert.IsTrue(fixture.Sink.TryDeliver(delivery));
        CollectionAssert.AreEqual(fixture.Lease.Payloads[0], fixture.Lease.Payloads[1]);
        bool repeat = !oneShot && fidelity is not (Switch2HdRumbleFeedbackFidelity.NativeSwitch2PassThrough or
            Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand);
        Assert.AreEqual(repeat, fixture.Sink.NeedsSustainedRefresh);
        fixture.HostNow += 10_000;
        Assert.IsTrue(fixture.Sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(repeat ? 3 : 2, fixture.Lease.Payloads.Count);
        if (repeat) CollectionAssert.AreEqual(fixture.Lease.Payloads[1][2..], fixture.Lease.Payloads[2][2..]);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ImpulseAndReleasePresentationsRemainUnchanged(bool release)
    {
        using var experiment = new Fixture(Switch2ControllerModel.JoyCon2Left, true,
            Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating);
        using var baseline = new Fixture(Switch2ControllerModel.JoyCon2Left, false,
            Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating);
        var delivery = Delivery(1, 30_000, 20_000, leftTrigger: release ? (ushort)0 : (ushort)40_000,
            rightTrigger: release ? (ushort)0 : (ushort)10_000);
        if (release)
        {
            Assert.IsTrue(experiment.Sink.TryStageImpulseReleasePresentation(delivery.Frame, 40_000, 10_000, 1));
            Assert.IsTrue(baseline.Sink.TryStageImpulseReleasePresentation(delivery.Frame, 40_000, 10_000, 1));
        }
        Assert.IsTrue(experiment.Sink.TryDeliver(delivery));
        Assert.IsTrue(baseline.Sink.TryDeliver(delivery));
        CollectionAssert.AreEqual(baseline.Lease.Payloads[0], experiment.Lease.Payloads[0]);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WarmedActualSinkAndPhysicalEncoderPreserveStrictAllocationGate(bool capturePositiveControl)
    {
        using var fixture = new Fixture(Switch2ControllerModel.JoyCon2Left, true);
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

    private static void AssertTailAmplitudesOnly(in Switch2HdRumbleSubframe original,
        in Switch2HdRumbleSubframe actual)
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
            !ControllerFeedbackFrame.TryCreate(source,
                ControllerFeedbackCommand.Apply, ControllerFeedbackActuators.All,
                low, high, leftTrigger, rightTrigger, sequence, 7, 11, 19, now, 250_000, out var frame))
            throw new InvalidOperationException("Invalid test feedback frame.");
        return new ControllerFeedbackDelivery(ControllerFeedbackDeliveryDisposition.Frame,
            origin, frame, 7, 11, 31);
    }

    private static ControllerFeedbackDelivery Stop(
        ControllerFeedbackPublicationOrigin origin = ControllerFeedbackPublicationOrigin.TestPreview) =>
        new(ControllerFeedbackDeliveryDisposition.Stop, origin, default, 7, 11, 31);

    private sealed class Fixture : IDisposable
    {
        internal readonly Lease Lease;
        internal readonly Switch2HdRumbleDeliverySink Sink;
        private readonly ControllerFeedbackPublicationOrigin origin;
        internal ulong HostNow = 1_000;

        internal Fixture(Switch2ControllerModel model, bool experiment,
            Switch2HdRumbleFeedbackPolicy policy = Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            ControllerFeedbackPublicationOrigin origin = ControllerFeedbackPublicationOrigin.TestPreview)
        {
            this.origin = origin;
            Lease = new Lease(model);
            var writer = new Switch2BluetoothHdRumblePhysicalWriter(Lease, model, 7, 11);
            Sink = new Switch2HdRumbleDeliverySink(writer, 7, 11, policy,
                minimumMaintenanceIntervalMicroseconds: 10_000, hostWriteStartClock: () => HostNow,
                labNeutralPreviewTails: experiment);
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
        public bool Authenticates(Switch2ControllerModel model, ulong device, ulong transport) =>
            model == expectedModel && device == 7 && transport == 11;
        public bool TryBindHdRumbleLifetime(Switch2ControllerModel model, ulong device, ulong transport) =>
            Authenticates(model, device, transport);
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

    private sealed class LabScope : IDisposable
    {
        private static readonly FieldInfo Current = typeof(PortableLabContext).GetField("current", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object previousContext = Current.GetValue(null);
        private readonly string previousValue = Environment.GetEnvironmentVariable(Switch2HeldSubframeExperiment.EnvironmentVariable);
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "held-subframe-policy-" + Guid.NewGuid().ToString("N"));
        private PortableLabContext context;

        internal LabScope()
        {
            try
            {
                Directory.CreateDirectory(root);
                byte[] image = "cold lab policy test only; never executed"u8.ToArray();
                File.WriteAllBytes(Path.Combine(root, "viiper.exe"), image);
                context = PortableLabContext.Create(new[] { "--portable-lab", Convert.ToHexString(SHA256.HashData(image)) }, root);
                Current.SetValue(null, context);
                Environment.SetEnvironmentVariable(Switch2HeldSubframeExperiment.EnvironmentVariable, "neutral-tails");
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            try { Current.SetValue(null, previousContext); }
            finally
            {
                Environment.SetEnvironmentVariable(Switch2HeldSubframeExperiment.EnvironmentVariable, previousValue);
                context?.Dispose();
                if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
                    Path.GetFileName(root).StartsWith("held-subframe-policy-", StringComparison.Ordinal) && Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }
}
