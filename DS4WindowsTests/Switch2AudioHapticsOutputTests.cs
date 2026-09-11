using System.Diagnostics;
using System.Reflection;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2AudioHapticsOutputTests
{
    [TestMethod]
    public void LocalAudioIdentityCannotEnterOrLeaveCfbkWire()
    {
        using var f = new CompositionFixture();
        Assert.IsTrue(f.Audio.TryPublish(new(1, 1, 0, 0), f.Now, out var audio));
        Assert.IsTrue(audio.HasValidInvariants());
        Assert.IsFalse(ControllerFeedbackPublication.TryCreate(
            ControllerFeedbackPublicationOrigin.NativeGame, audio, out _));
        Assert.IsFalse(ControllerFeedbackPublication.TryCreate(
            ControllerFeedbackPublicationOrigin.TestPreview, audio, out _));
        byte[] wire = new byte[ControllerFeedbackFrame.SerializedLength];
        Assert.IsFalse(audio.TryWriteTo(wire));
        Assert.IsTrue(f.Native.TryPublish(new(1, 0, 0, 0), f.Now, out var native));
        Assert.IsTrue(native.TryWriteTo(wire));
        wire[8] = (byte)ControllerFeedbackSource.LocalAudioHaptics;
        Assert.IsFalse(ControllerFeedbackFrame.TryReadFrom(wire, out _));
        Assert.IsFalse(f.Pump.TryCreateBrokerIngress(ControllerFeedbackSource.LocalAudioHaptics, 99, out _));
    }

    [TestMethod]
    public void DefaultRuntimeKeepsNativePriorityWithoutExplicitCompositionOptIn()
    {
        Assert.IsTrue(ControllerFeedbackStateLanePump.TryCreate(7, 11, out var pump));
        var writer = new Writer();
        var sink = new Switch2HdRumbleDeliverySink(writer, 7, 11, feedbackClock: () => 1_000_000);
        Assert.IsTrue(pump.TryCreateLane(ControllerFeedbackPublicationOrigin.AudioHaptics,
            ControllerFeedbackSource.LocalAudioHaptics, 1, 24_000, 12_000, out var audio));
        Assert.IsTrue(pump.TryCreateLane(ControllerFeedbackPublicationOrigin.NativeGame,
            ControllerFeedbackSource.Xbox360VirtualDevice, 2, 250_000, 100_000, out var native));
        try
        {
            Assert.IsTrue(audio.TryPublish(new(1, 1, 0, 0), 1_000_000));
            Assert.IsTrue(native.TryPublish(new(30_000, 0, 0, 0), 1_000_000));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered,
                pump.PumpOnce(1_000_000, sink, out var delivered));
            Assert.AreEqual(ControllerFeedbackPublicationOrigin.NativeGame, delivered.Origin);
        }
        finally { Assert.IsTrue(pump.TryStopAndRetire(1_000_000, sink, 5)); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WarmCompositionAndActualWriterAdmissionHaveAnExactAllocationGate(bool positiveControl)
    {
        using var f = new CompositionFixture();
        for (int i = 0; i < 256; i++)
        {
            f.Now++;
            f.PublishAudio(AudioHapticsMode.Mix);
        }
        f.Writer.Allocate = positiveControl;
        long bytes;
        bool accepted = true;
        int beforeCalls = f.Writer.Calls;
        int iterations = positiveControl ? 1 : 20_000;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                f.Now++;
                accepted &= Switch2AudioHapticsLifetime.Publish(f.Pump, f.Sink, f.Audio,
                    f.Left, f.Right, AudioHapticsMode.Mix, f.Now, f.Now);
            }
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.IsTrue(accepted);
        Assert.AreEqual(iterations, f.Writer.Calls - beforeCalls);
        if (positiveControl)
        {
            Assert.IsTrue(bytes >= 128);
            Assert.IsNotNull(f.Writer.Captured);
        }
        else Assert.AreEqual(0L, bytes);
    }

    [TestMethod]
    public void NativeNeutralDoesNotMaskFreshAudioAndPreviewStillWins()
    {
        using var f = new CompositionFixture();
        f.PublishNative(default);
        f.PublishAudio(AudioHapticsMode.Mix);
        Assert.AreEqual(ControllerFeedbackSource.LocalAudioHaptics, f.Writer.Last.Source);
        Assert.AreEqual(f.Left, f.Writer.Last.Left);
        Assert.AreEqual(f.Right, f.Writer.Last.Right);
        Assert.IsTrue(f.Pump.TryCreateLane(ControllerFeedbackPublicationOrigin.TestPreview,
            ControllerFeedbackSource.Xbox360VirtualDevice, 8, 250_000, 100_000, out var preview));
        Assert.IsTrue(preview.TryPublish(new(40_000, 0, 0, 0), f.Now));
        f.Drain();
        Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview, f.LastDelivery.Origin);
    }

    [DataTestMethod]
    [DataRow(AudioHapticsMode.Mix)]
    [DataRow(AudioHapticsMode.Replace)]
    public void XboxFeedbackAndAudioUseExplicitMixOrReplaceWithoutChangingNativeState(AudioHapticsMode mode)
    {
        using var f = new CompositionFixture(ControllerFeedbackSource.Xbox360VirtualDevice);
        f.PublishNative(new(30_000, 8_000, 0, 0));
        var original = f.NativeFrame;
        var nativeLeft = f.Writer.Last.Left;
        var nativeRight = f.Writer.Last.Right;
        f.PublishAudio(mode);
        Assert.AreEqual(mode == AudioHapticsMode.Mix ?
            DualSenseHapticsTranslator.MixSwitch2AudioGroups(nativeLeft, f.Left) : f.Left, f.Writer.Last.Left);
        Assert.AreEqual(mode == AudioHapticsMode.Mix ?
            DualSenseHapticsTranslator.MixSwitch2AudioGroups(nativeRight, f.Right) : f.Right, f.Writer.Last.Right);
        Assert.IsTrue(f.Pump.TryReadNativeFrame(f.Now, out var retained));
        Assert.AreEqual(original, retained);
        Assert.IsTrue(Switch2AudioHapticsLifetime.Withdraw(f.Pump, f.Sink, f.Audio, f.Now));
        Assert.AreEqual(original.Source, f.Writer.Last.Source);
        Assert.AreEqual(nativeLeft, f.Writer.Last.Left);
    }

    [TestMethod]
    public void AudioWindowIsFiniteAndExpiryDoesNotReplayConsumedNativePcm()
    {
        using var f = new CompositionFixture();
        f.PublishNative(new(1, 1, 0, 0), f.NativeGroup);
        f.PublishAudio(AudioHapticsMode.Mix);
        Assert.AreEqual(f.Left, f.Writer.Last.Left, "Previously consumed native PCM is not mixed again.");
        int calls = f.Writer.Calls;
        for (int i = 1; i < 24; i++)
        {
            f.Now++;
            f.Pump.TryRefreshCurrentPresentation(f.Now, allowNoFrame: true, applyOnly: true);
            f.Pump.PumpOnce(f.Now, f.Sink.MaintenanceSink, out _);
        }
        Assert.AreEqual(calls, f.Writer.Calls, "Maintenance cannot sustain a consumed audio slice.");
        f.Now = 1_024_000;
        f.Drain();
        Assert.AreEqual(calls + 1, f.Writer.Calls, "Only the required audio Stop is written; old native PCM stays consumed.");
        Assert.IsTrue(f.Writer.Last.IsStop);
        Assert.IsFalse(f.Pump.HasLocalAudioOwner);
        var current = f.LastDelivery;
        Assert.AreEqual(ControllerFeedbackPublicationOrigin.NativeGame, current.Origin);
        var forged = new ControllerFeedbackDelivery(current.Disposition, current.Origin,
            current.Frame, 7, 11, current.DeliveryEpoch - 1);
        Assert.IsFalse(f.Sink.TryDeliver(forged), "Consumed suppression still authenticates epochs.");
        forged = new ControllerFeedbackDelivery(current.Disposition, current.Origin,
            current.Frame, 7, 11, current.DeliveryEpoch + 1);
        Assert.IsFalse(f.Sink.TryDeliver(forged), "A new epoch still requires the current owner's Stop.");
    }

    [DataTestMethod]
    [DataRow(AudioHapticsMode.Mix)]
    [DataRow(AudioHapticsMode.Replace)]
    public void FreshNativePcmDuringAudioIsConsumedOnceAndNotReplayedOnWithdrawal(AudioHapticsMode mode)
    {
        using var f = new CompositionFixture();
        f.PublishAudio(mode);
        f.Now++;
        f.PublishNative(new(1, 1, 0, 0), f.NativeGroup);
        Assert.AreEqual(mode == AudioHapticsMode.Mix ? f.NativeGroup : default, f.Writer.Last.Left,
            "The app slice was already consumed; a native update cannot repeat it.");
        int calls = f.Writer.Calls;
        Assert.IsTrue(Switch2AudioHapticsLifetime.Withdraw(f.Pump, f.Sink, f.Audio, f.Now));
        Assert.AreEqual(calls + 1, f.Writer.Calls);
        Assert.IsTrue(f.Writer.Last.IsStop);
        f.Now++;
        f.PublishNative(new(1, 1, 0, 0), f.NativeGroup);
        Assert.AreEqual(f.NativeGroup, f.Writer.Last.Left, "A new native sequence is not suppressed.");
    }

    [TestMethod]
    public void UncertainCompositeRetriesExactSamplesAndOriginalDeadline()
    {
        using var f = new CompositionFixture();
        f.Writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(Switch2HdRumblePhysicalWriteFailure.Busy);
        f.PublishAudio(AudioHapticsMode.Mix);
        var first = f.Writer.Last;
        f.Now += 1_000;
        Assert.IsFalse(Switch2AudioHapticsLifetime.Publish(f.Pump, f.Sink, f.Audio,
            f.Right, f.Left, AudioHapticsMode.Replace, f.Now, f.Now));
        f.Writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        f.Drain();
        Assert.AreEqual(first, f.Writer.Last);
        Assert.AreEqual(1_000_000UL, f.Writer.Last.TimestampMicroseconds);
        Assert.AreEqual(24_000UL, f.Writer.Last.TimeToLiveMicroseconds);
    }

    [TestMethod]
    public void QueuedSamplesKeepCaptureDeadlineAndExpiredOrFutureSamplesAreRejected()
    {
        using var f = new CompositionFixture();
        Assert.IsFalse(Switch2AudioHapticsLifetime.Publish(f.Pump, f.Sink, f.Audio,
            f.Left, f.Right, AudioHapticsMode.Mix, f.Now - 24_000, f.Now));
        Assert.IsFalse(Switch2AudioHapticsLifetime.Publish(f.Pump, f.Sink, f.Audio,
            f.Left, f.Right, AudioHapticsMode.Mix, f.Now + 1, f.Now));
        Assert.IsTrue(Switch2AudioHapticsLifetime.Publish(f.Pump, f.Sink, f.Audio,
            f.Left, f.Right, AudioHapticsMode.Mix, f.Now - 20_000, f.Now));
        Assert.AreEqual(f.Now - 20_000, f.Writer.Last.TimestampMicroseconds);
        Assert.AreEqual(24_000UL, f.Writer.Last.TimeToLiveMicroseconds);
        Assert.AreEqual(f.Now + 4_000,
            f.Writer.Last.TimestampMicroseconds + f.Writer.Last.TimeToLiveMicroseconds);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2)]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void ActualHandleFencesReplacementDisableSilenceAndTerminal(Switch2ControllerModel model)
    {
        using var f = new RuntimeFixture(model);
        Assert.IsTrue(f.Runtime.TryCreateAudioHapticsOutput(out var old));
        Assert.IsFalse(old.IsReady);
        Assert.IsFalse(old.TryWrite(f.Samples, AudioHapticsMode.Mix, f.CaptureTimestamp));
        f.Activate();
        Assert.IsTrue(old.IsReady);
        Assert.IsTrue(old.TryWrite(f.Samples, AudioHapticsMode.Mix, f.CaptureTimestamp));
        Assert.IsTrue(f.Lease.Calls > 0);
        Assert.IsTrue(f.Runtime.TryCreateAudioHapticsOutput(out var current));
        old.Dispose();
        Assert.IsFalse(old.TryWrite(f.Samples, AudioHapticsMode.Mix, f.CaptureTimestamp));
        Assert.IsTrue(current.IsReady);
        Assert.IsTrue(current.TryWrite(f.Samples, AudioHapticsMode.Mix, f.CaptureTimestamp));
        Assert.IsTrue(current.TryWrite(new byte[64], AudioHapticsMode.Mix, f.CaptureTimestamp));
        Global.EnableOutputDataToDS4[0] = false;
        Assert.IsFalse(current.IsReady);
        Assert.IsFalse(current.TryWrite(f.Samples, AudioHapticsMode.Mix, f.CaptureTimestamp));
        Global.EnableOutputDataToDS4[0] = true;
        f.Runtime.TryPublishTerminalNeutral();
        Assert.IsFalse(current.IsReady);
        Assert.IsFalse(current.TryWrite(f.Samples, AudioHapticsMode.Mix, f.CaptureTimestamp));
        current.Dispose();
    }

    [TestMethod]
    public void PublicationLockContentionCannotTrapWriterAheadOfProfileRevocation()
    {
        using var f = new RuntimeFixture();
        f.Activate();
        Assert.IsTrue(f.Runtime.TryCreateAudioHapticsOutput(out var output));
        object publicationGate = Field(f.Runtime, "publicationGate");
        Task<bool> writer;
        lock (publicationGate)
        {
            writer = Task.Run(() => output.TryWrite(f.Samples, AudioHapticsMode.Mix, f.CaptureTimestamp));
            Assert.IsTrue(writer.Wait(TimeSpan.FromSeconds(2)), "Writer must not block on a publication held by profile processing.");
            Assert.IsFalse(writer.Result);
            output.Dispose();
            Assert.IsTrue(f.Runtime.TryCreateAudioHapticsOutput(out var replacement));
            replacement.Dispose();
        }
    }

    internal sealed class RuntimeFixture : IDisposable
    {
        private readonly bool previousOutput = Global.EnableOutputDataToDS4[0];
        internal readonly Switch2RuntimeInputDevice Runtime;
        internal readonly Switch2BluetoothFeedbackLifetime Feedback;
        internal readonly Lease Lease;
        private readonly bool useManualAudioClock;
        // A whole-second initial capture is exact for every QPC frequency.
        // Advance logical microseconds directly, avoiding fractional-tick
        // rounding across the 23,999 / 24,000 microsecond boundary assertion.
        internal ulong NowMicroseconds = (ulong)(Stopwatch.GetTimestamp() / Stopwatch.Frequency) * 1_000_000;
        internal long CaptureTimestamp => useManualAudioClock ? checked((long)
            ((UInt128)NowMicroseconds * (ulong)Stopwatch.Frequency / 1_000_000)) : Stopwatch.GetTimestamp();
        internal void AdvanceMicroseconds(ulong microseconds) => NowMicroseconds = checked(NowMicroseconds + microseconds);
        internal readonly byte[] Samples = Enumerable.Range(0, 64).Select(i => unchecked((byte)(sbyte)(i % 4 < 2 ? 80 : -80))).ToArray();
        internal RuntimeFixture(Switch2ControllerModel model = Switch2ControllerModel.ProController2,
            bool useManualAudioClock = true)
        {
            this.useManualAudioClock = useManualAudioClock;
            Global.EnableOutputDataToDS4[0] = true;
            Lease = new Lease(model);
            Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(Lease, model, 7, 11, out Feedback));
            bool created = model == Switch2ControllerModel.ProController2 ?
                Switch2RuntimeInputDevice.TryCreatePro(7, 11, Switch2Transport.BluetoothLe, out Runtime, out _) :
                Switch2RuntimeInputDevice.TryCreateStandaloneJoyCon(model, 7, 11, out Runtime, out _);
            Assert.IsTrue(created);
            if (useManualAudioClock)
            {
                Func<ulong> clock = () => NowMicroseconds;
                typeof(Switch2RuntimeInputDevice).GetField("audioHapticsClock",
                    BindingFlags.NonPublic | BindingFlags.Instance).SetValue(Runtime, clock);
                var sink = (Switch2HdRumbleDeliverySink)Field(Feedback, "sink");
                typeof(Switch2HdRumbleDeliverySink).GetField("feedbackClock",
                    BindingFlags.NonPublic | BindingFlags.Instance).SetValue(sink, clock);
                var writer = (Switch2BluetoothHdRumblePhysicalWriter)Field(sink, "writer");
                typeof(Switch2BluetoothHdRumblePhysicalWriter).GetField("submissionClock",
                    BindingFlags.NonPublic | BindingFlags.Instance).SetValue(writer, clock);
            }
            Assert.IsTrue(Runtime.TryAttachBluetoothFeedbackLifetime(model, 7, 11, Feedback));
            Runtime.DeviceSlotNumber = 0;
        }
        internal void Activate()
        {
            ((DS4Device)Runtime).StartUpdate();
            // Manual scheduling keeps this no-hardware fixture deterministic.
            ((Switch2RumbleMaintenanceWorker)Field(Runtime, "rumbleMaintenanceWorker")).Stop();
            Feedback.SetRumbleMaintenanceWake(null);
        }
        public void Dispose()
        {
            try
            {
                Runtime.TryPublishTerminalNeutral();
                Assert.IsTrue(Feedback.TryStopAndRetireUntil(Environment.TickCount64 + 1_000, 4));
            }
            finally { Global.EnableOutputDataToDS4[0] = previousOutput; }
        }
    }

    internal sealed class Lease(Switch2ControllerModel expected) : ISwitch2BluetoothHdRumbleBindableTransportLease
    {
        internal int Calls;
        public bool HasHdRumbleOutput => true;
        public bool Authenticates(Switch2ControllerModel model, ulong device, ulong transport) => model == expected && device == 7 && transport == 11;
        public bool TryBindHdRumbleLifetime(Switch2ControllerModel model, ulong device, ulong transport) => Authenticates(model, device, transport);
        public Switch2BluetoothHdRumbleTransportWriteResult TryWritePayload(ReadOnlySpan<byte> payload,
            Switch2ControllerModel model, ulong device, ulong transport)
        {
            Calls++;
            return Switch2BluetoothHdRumbleTransportWriteResult.Complete(model, device, transport, payload.Length);
        }
    }

    private sealed class CompositionFixture : IDisposable
    {
        internal ulong Now = 1_000_000;
        internal readonly ControllerFeedbackStateLanePump Pump;
        internal readonly Switch2HdRumbleDeliverySink Sink;
        internal readonly Writer Writer = new();
        internal readonly ControllerFeedbackStateLanePump.Lane Audio, Native;
        internal ControllerFeedbackFrame NativeFrame;
        internal ControllerFeedbackDelivery LastDelivery;
        internal readonly Switch2HdRumbleGroup Left = Group(300, 140), Right = Group(360, 220), NativeGroup = Group(400, 180);
        internal CompositionFixture(ControllerFeedbackSource source = ControllerFeedbackSource.DualSenseVirtualDevice)
        {
            Assert.IsTrue(ControllerFeedbackStateLanePump.TryCreate(7, 11, out Pump));
            Sink = new(Writer, 7, 11, feedbackClock: () => Now);
            Sink.EnableAudioComposition(Pump);
            Assert.IsTrue(Pump.TryCreateLane(ControllerFeedbackPublicationOrigin.AudioHaptics,
                ControllerFeedbackSource.LocalAudioHaptics, 1, 24_000, 12_000, out Audio));
            Assert.IsTrue(Pump.TryCreateLane(ControllerFeedbackPublicationOrigin.NativeGame,
                source, 2, 250_000, 100_000, out Native));
        }
        internal void PublishNative(ControllerFeedbackActuatorState state, Switch2HdRumbleGroup? group = null)
        {
            Assert.IsTrue(Native.TryPublish(state, Now, out NativeFrame));
            if (group.HasValue) Assert.IsTrue(Sink.TryStageSourcePreservedSynthesis(NativeFrame,
                Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand, group.Value, group.Value));
            Pump.TryRefreshCurrentPresentation(Now, allowNoFrame: true);
            Drain();
        }
        internal void PublishAudio(AudioHapticsMode mode) => Assert.IsTrue(
            Switch2AudioHapticsLifetime.Publish(Pump, Sink, Audio, Left, Right, mode, Now, Now));
        internal void Drain()
        {
            var disposition = Pump.PumpOnce(Now, Sink, out LastDelivery);
            if (disposition == ControllerFeedbackPumpDisposition.Delivered && LastDelivery.Disposition == ControllerFeedbackDeliveryDisposition.Stop)
                Pump.PumpOnce(Now, Sink, out LastDelivery);
        }
        public void Dispose() => Assert.IsTrue(Pump.TryStopAndRetire(Now, Sink, 5));
    }

    private sealed class Writer : ISwitch2HdRumblePhysicalWriter
    {
        internal int Calls;
        internal bool Allocate;
        internal byte[] Captured;
        internal Switch2HdRumblePhysicalSubmission Last;
        internal Switch2HdRumblePhysicalWriteResult Result = Switch2HdRumblePhysicalWriteResult.Success();
        public bool Authenticates(ulong device, ulong transport) => device == 7 && transport == 11;
        public Switch2HdRumblePhysicalWriteResult TryWrite(in Switch2HdRumblePhysicalSubmission submission)
        {
            Calls++;
            if (Allocate) Captured = new byte[128];
            Last = submission;
            return Result;
        }
    }

    private static Switch2HdRumbleGroup Group(ushort control, ushort amplitude)
    {
        var slice = new Switch2HdRumbleSubframe(control, amplitude, 280, amplitude);
        return new(slice, slice, slice);
    }
    private static object Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}
