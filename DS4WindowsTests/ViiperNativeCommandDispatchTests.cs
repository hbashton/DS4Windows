using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ViiperNativeCommandDispatchTests
{
    [TestMethod]
    public void GamepadOnlyNativeReaderHasControlConsumerWithoutSpeakerWorker()
    {
        using var fixture = new AdmissionFixture();
        SetField(fixture.Output, "gamepadOnly", true);
        byte[] envelope = Envelope(Burst(false, 2)[0]);
        Assert.IsTrue(fixture.Output.TryCaptureNativeCommandContext(envelope,
            envelope.Length, 0, out var context));
        try
        {
            Invoke(fixture.Output, "StartFeedbackDispatchWorkers");
            Assert.IsNull(GetField(fixture.Output, "feedbackSpeakerDispatchThread"));
            Assert.IsNotNull(GetField(fixture.Output, "feedbackControlDispatchThread"));
            Assert.IsTrue(fixture.Output.EnqueueNativeCommandUntilCancelled(null, 0,
                envelope, envelope.Length, 0, context));
            ((AutoResetEvent)GetField(fixture.Output, "feedbackControlSignal")!).Set();
            Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Buffer.OrderedControlDequeued == 1, 2000),
                "Raw/gamepad-only feedback has no control consumer.");
            Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
                fixture.Device.ProcessNextPhysicalOutputCommand());
            Assert.AreEqual((byte)90, fixture.Reports.Single()[3]);
        }
        finally
        {
            SetField(fixture.Output, "feedbackDispatchStopRequested", true);
            Invoke(fixture.Output, "StopFeedbackDispatchWorkers");
        }
    }

    [DataTestMethod]
    [DataRow("clear")]
    [DataRow("stop")]
    [DataRow("stream")]
    [DataRow("profile")]
    [DataRow("replacement")]
    public void BlockedActualReaderAdmissionTerminatesAtLifecycleBoundary(string boundary)
    {
        using var fixture = new AdmissionFixture();
        byte[] envelope = Envelope(Led(31));
        Assert.IsTrue(fixture.Output.TryCaptureNativeCommandContext(envelope,
            envelope.Length, 0, out var context));
        for (int index = 0; index < ViiperOutDevice.FeedbackOrderedControlQueueCapacity; index++)
            Assert.IsTrue(fixture.Output.EnqueueNativeCommandUntilCancelled(null, 0,
                envelope, envelope.Length, 0, context));
        bool? accepted = null;
        Exception failure = null;
        using var started = new ManualResetEventSlim();
        var reader = new Thread(() =>
        {
            started.Set();
            try { accepted = fixture.Output.EnqueueNativeCommandUntilCancelled(null, 0,
                envelope, envelope.Length, 0, context); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        reader.Start();
        try
        {
            Assert.IsTrue(started.Wait(2000));
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                (reader.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 2000));
            Assert.AreEqual(4, fixture.Buffer.PendingOrderedControlCount);
            switch (boundary)
            {
                case "clear": fixture.Buffer.ClearPending(); break;
                case "stop": SetField(fixture.Output, "feedbackDispatchStopRequested", true); break;
                case "stream": SetField(fixture.Output, "streamGeneration", 1L); break;
                case "profile": Global.BeginProfileSwitchRevision(0); break;
                case "replacement": DS4Windows.Program.rootHub.DS4Controllers[0] = fixture.CreateDevice(); break;
            }
            Assert.IsTrue(reader.Join(2000), "Lifecycle cancellation failed to release the feedback reader.");
            Assert.IsNull(failure);
            Assert.AreEqual(false, accepted);
            Assert.AreEqual(4L, fixture.Buffer.OrderedControlEnqueued,
                "The cancelled producer must not admit a fifth command into a successor boundary.");
        }
        finally
        {
            SetField(fixture.Output, "feedbackDispatchStopRequested", true);
            fixture.Buffer.ClearPending();
            Assert.IsTrue(reader.Join(2000), "Test cleanup could not drain the reader.");
        }
    }

    [TestMethod]
    public void FullPhysicalOwnerRetainsCommandUntilOneExactAdmissionWithoutScalarFallback()
    {
        using var fixture = new AdmissionFixture();
        byte[] envelope = Envelope(Burst(false, 2)[0]);
        envelope[0] = 43; // A cumulative scalar must never become a fallback.
        fixture.FillPhysicalQueue();
        Assert.IsTrue(fixture.Output.TryCaptureNativeCommandContext(envelope,
            envelope.Length, 0, out var context));
        Assert.IsTrue(fixture.Buffer.TryEnqueueOrderedControl(envelope, envelope.Length,
            0, 0, nativeCommand: true, nativeContext: context));
        Assert.IsTrue(fixture.Buffer.TryPeekNativeCommand(fixture.Scratch,
            out int length, out long generation, out int index, out long receipt, out context));
        Assert.IsFalse(fixture.Output.TryApplyRetainedNativeCommand(fixture.Scratch,
            length, index, generation, context, new byte[48]));
        Assert.AreEqual(1, fixture.Buffer.PendingOrderedControlCount);
        Assert.AreEqual(0, fixture.Device.CompatibilityRumbleCalls);

        // The direct compatibility entry point must not reinterpret rejection
        // either. This invokes the same production method as non-retained input.
        typeof(ViiperOutDevice).GetMethod("ApplyFeedback", PrivateInstance)!
            .Invoke(fixture.Output, new object[] { envelope, envelope.Length, 0, true, new byte[48], 0L });
        Assert.AreEqual(0, fixture.Device.CompatibilityRumbleCalls);
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
            fixture.Device.ProcessNextPhysicalOutputCommand());
        Assert.IsTrue(fixture.Output.TryApplyRetainedNativeCommand(fixture.Scratch,
            length, index, generation, context, new byte[48]));
        Assert.IsTrue(fixture.Buffer.TryCommitNativeCommand(receipt));
        Assert.IsFalse(fixture.Buffer.TryCommitNativeCommand(receipt));
        fixture.DrainPhysicalQueue();
        Assert.AreEqual(65, fixture.Reports.Count);
        Assert.AreEqual((byte)90, fixture.Reports[^1][3]);
        Assert.AreEqual((byte)120, fixture.Reports[^1][4]);
        Assert.AreEqual(0, fixture.Device.CompatibilityRumbleCalls);
    }

    [DataTestMethod]
    [DataRow("replacement")]
    [DataRow("profile")]
    [DataRow("binding-round-trip")]
    [DataRow("stream")]
    [DataRow("stop")]
    [DataRow("disconnect")]
    [DataRow("clear")]
    public void RetainedCommandCannotCrossItsOriginalOwnershipBoundary(string boundary)
    {
        using var fixture = new AdmissionFixture();
        byte[] envelope = Envelope(Burst(false, 2)[0]);
        Assert.IsTrue(fixture.Output.TryCaptureNativeCommandContext(envelope,
            envelope.Length, 0, out var context));
        switch (boundary)
        {
            case "replacement": DS4Windows.Program.rootHub.DS4Controllers[0] = fixture.CreateDevice(); break;
            case "profile": Global.BeginProfileSwitchRevision(0); break;
            case "binding-round-trip":
                SetField(fixture.Output, "connected", false);
                fixture.Output.BindPhysicalController(-1);
                fixture.Output.BindPhysicalController(0);
                SetField(fixture.Output, "connected", true);
                break;
            case "stream": SetField(fixture.Output, "streamGeneration", 1L); break;
            case "stop": SetField(fixture.Output, "feedbackDispatchStopRequested", true); break;
            case "disconnect": typeof(DS4Device).GetField("isDisconnecting", PrivateInstance)!
                .SetValue(fixture.Device, true); break;
            case "clear": fixture.Buffer.ClearPending(); break;
        }
        Assert.IsTrue(fixture.Output.TryApplyRetainedNativeCommand(envelope,
            envelope.Length, 0, 0, context, new byte[48]), "A stale command is retired, not retried.");
        Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.None,
            fixture.Device.ProcessNextPhysicalOutputCommand());
        Assert.AreEqual(0, fixture.Reports.Count);
        Assert.AreEqual(0, fixture.Device.CompatibilityRumbleCalls);
    }

    [TestMethod]
    public void NativeReceiptAndBlockedAdmissionAreCancelledByClearWithoutErasingSuccessor()
    {
        var buffer = new ViiperFeedbackDispatchBuffer(1, 8, 8, 1);
        byte[] payload = { 1, 2 };
        long boundary = buffer.PendingBoundaryRevision;
        Assert.IsTrue(buffer.TryEnqueueOrderedControl(payload, 2, 1, 0,
            nativeCommand: true, expectedBoundaryRevision: boundary));
        Assert.IsTrue(buffer.TryPeekNativeCommand(new byte[8], out _, out _, out _,
            out long receipt, out _));
        buffer.ClearPending();
        Assert.IsFalse(buffer.WaitForOrderedControlSpace(boundary, 10));
        Assert.IsFalse(buffer.TryEnqueueOrderedControl(payload, 2, 1, 0,
            nativeCommand: true, expectedBoundaryRevision: boundary));
        Assert.IsTrue(buffer.TryEnqueueOrderedControl(payload, 2, 2, 1, nativeCommand: true));
        Assert.IsFalse(buffer.TryCommitNativeCommand(receipt));
        Assert.AreEqual(1, buffer.PendingOrderedControlCount);
    }

    [TestMethod]
    public void NativeHeadCannotBeErasedByLatestMailboxAndDoesNotBlockQueuedMedia()
    {
        var buffer = new ViiperFeedbackDispatchBuffer(1, 8, 8, 1);
        byte[] payload = { 1, 2 };
        Assert.IsTrue(buffer.TryEnqueueOrderedControl(payload, 2, 1, 0, nativeCommand: true));
        Assert.IsTrue(buffer.QueueControl(payload, 2, 1, 0));
        Assert.IsFalse(buffer.TryTakeControl(new byte[8], out _, out _, out _));
        Assert.IsTrue(buffer.TryEnqueueSpeaker(payload, 2, 1));
        Assert.IsTrue(buffer.TryDequeueSpeaker(new byte[8], out _, out _));
        Assert.AreEqual(1, buffer.PendingOrderedControlCount);
        Assert.IsTrue(buffer.TryPeekNativeCommand(new byte[8], out _, out _, out _, out long receipt, out _));
        Assert.IsTrue(buffer.TryCommitNativeCommand(receipt));
        Assert.IsTrue(buffer.TryTakeControl(new byte[8], out _, out _, out _));
    }

    [DataTestMethod]
    [DataRow(0, true)]
    [DataRow(0x36, false)]
    [DataRow(0x32, false)]
    public void OnlyExactNativeControlUsesRetainedAdmission(int carrier, bool expected)
    {
        using var fixture = new AdmissionFixture();
        byte[] envelope = Envelope(Led(31));
        envelope[76] = (byte)carrier;
        Assert.AreEqual(expected, fixture.Output.TryCaptureNativeCommandContext(envelope,
            envelope.Length, 0, out _));
        // An unverified/unsupported physical target retains its previous path.
        SetField(fixture.Output, "physicalDualSenseIdentityVerified", false);
        Assert.IsFalse(fixture.Output.TryCaptureNativeCommandContext(envelope, envelope.Length, 0, out _));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeCommandBurstRetainsPulseOrTriggerBeforeItsSuccessor(bool trigger)
    {
        var fixture = new NativeDispatchFixture();
        byte[][] commands = Burst(trigger,
            ViiperOutDevice.FeedbackOrderedControlQueueCapacity + 1);

        // Model a producer that retains a rejected item and retries it after
        // one consumer admission. A full bounded queue must not report success
        // while silently evicting an earlier accepted native command.
        foreach (byte[] command in commands)
        {
            if (!fixture.Enqueue(command))
            {
                Assert.IsTrue(fixture.DispatchOne());
                Assert.IsTrue(fixture.Enqueue(command));
            }
        }
        fixture.Drain();

        AssertCommands(commands, fixture.NativeReports,
            "Native HID deltas are commands, not replaceable waveform samples.");
        AssertCommands(commands, fixture.PhysicalReports,
            "The original pulse/trigger must reach the actual physical owner before stop/LED.", physical: true);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AcceptedNativeStopOrLedDoesNotExpireBehindAConsumerPause(bool trigger)
    {
        var fixture = new NativeDispatchFixture(useProductionAgeLimit: true);
        byte[][] commands = Burst(trigger, 2);
        fixture.PublishPrecursor(commands[0]);
        Assert.IsTrue(fixture.Enqueue(commands[1]));

        // Advance only the queue's recorded age, not wall-clock time. This
        // exercises the production dequeue expiry branch without sleeping,
        // controller I/O, a worker thread, or a scheduling-dependent test.
        fixture.AgePendingCommands(milliseconds: 40);
        fixture.Drain();

        AssertCommands(commands, fixture.NativeReports,
            "An accepted stop/LED command cannot be discarded as stale audio.");
        AssertCommands(commands, fixture.PhysicalReports,
            "An already-published pulse/trigger must still receive its accepted successor.", physical: true);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnpressuredNativeCommandPairReachesActualPhysicalOwnerInOrder(bool trigger)
    {
        var fixture = new NativeDispatchFixture();
        byte[][] commands = Burst(trigger, 2);
        foreach (byte[] command in commands)
        {
            Assert.IsTrue(fixture.Enqueue(command));
            Assert.IsTrue(fixture.DispatchOne());
        }
        Assert.IsFalse(fixture.DispatchOne());
        AssertCommands(commands, fixture.NativeReports, "Native report copy must preserve the command.");
        AssertCommands(commands, fixture.PhysicalReports, "The fixture must exercise the real physical command owner.", physical: true);
    }

    [TestMethod]
    public void IdenticalNativeCommandsAreNotDeduplicated()
    {
        var fixture = new NativeDispatchFixture();
        byte[] command = Burst(false, 2)[0];
        Assert.IsTrue(fixture.Enqueue(command));
        Assert.IsTrue(fixture.Enqueue(command));
        fixture.Drain();
        AssertCommands(new[] { command, command }, fixture.NativeReports,
            "Identical HID reports are still separately admitted commands.");
    }

    private static byte[][] Burst(bool trigger, int count)
    {
        Assert.IsTrue(count >= 2);
        var commands = new byte[count][];
        commands[0] = new byte[48];
        commands[0][0] = 0x02;
        commands[0][1] = trigger ? (byte)0x04 : (byte)0x03;
        if (trigger)
        {
            commands[0][11] = 0x25;
            commands[0][12] = 0x17;
        }
        else
        {
            commands[0][3] = 90;
            commands[0][4] = 120;
        }
        commands[1] = trigger ? Led(31) : new byte[48];
        commands[1][0] = 0x02;
        if (!trigger) commands[1][1] = 0x03;
        for (int index = 2; index < count; index++)
            commands[index] = Led((byte)(31 + index));
        return commands;
    }

    private static byte[] Led(byte red)
    {
        var report = new byte[48];
        report[0] = 0x02;
        report[2] = 0x04;
        report[45] = red;
        report[46] = 63;
        report[47] = 95;
        return report;
    }

    private static void AssertCommands(byte[][] expected, List<byte[]> actual, string message, bool physical = false)
    {
        Assert.AreEqual(expected.Length, actual.Count, message);
        for (int index = 0; index < expected.Length; index++)
        {
            if (!physical)
            {
                CollectionAssert.AreEqual(expected[index], actual[index], message);
                continue;
            }
            // The physical owner may add unrelated local mute/audio fields.
            // These are the exact game-authored strobes and command payloads.
            foreach (int offset in new[] { 0, 3, 4, 11, 12, 45, 46, 47 })
            {
                if (offset >= 45 && (expected[index][2] & 0x04) == 0) continue;
                Assert.AreEqual(expected[index][offset], actual[index][offset],
                    $"{message} Command {index}, byte {offset}.");
            }
            Assert.AreEqual(expected[index][1] & 0x0F, actual[index][1] & 0x0F,
                $"{message} Command {index}, controller-command validity.");
            if ((expected[index][2] & 0x04) != 0)
                Assert.AreEqual(0x04, actual[index][2] & 0x04,
                    $"{message} Command {index}, LED validity.");
        }
    }

    private sealed class NativeDispatchFixture
    {
        private const int NativeOffset = 28;
        private const long Generation = 41;
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly ViiperFeedbackDispatchBuffer buffer;
        private readonly byte[] feedback = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
        private readonly byte[] native = new byte[48];
        private readonly DualSenseDevice device;

        internal List<byte[]> NativeReports { get; } = new();
        internal List<byte[]> PhysicalReports { get; } = new();

        internal NativeDispatchFixture(bool useProductionAgeLimit = false)
        {
            // Isolate capacity/ordering from expiry unless expiry is the
            // subject of the test; cold JIT time is not a command-loss oracle.
            buffer = new(1, 4096, ViiperOutDevice.DualSenseAtomicFeedbackLength,
                ViiperOutDevice.FeedbackOrderedControlQueueCapacity,
                orderedControlMaximumAgeMilliseconds: useProductionAgeLimit ?
                    ViiperOutDevice.FeedbackOrderedControlMaximumAgeMilliseconds : 0);
            // No worker is started. The existing final physical-write hook is
            // the only transport boundary, so this cannot reach a HID handle.
            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            device = new DualSenseDevice(hid, "Native feedback dispatch ordering test");
            typeof(DS4Device).GetField("conType", InstanceFields)!.SetValue(device, ConnectionType.USB);
            typeof(DS4Device).GetField("outputReport", InstanceFields)!.SetValue(device, new byte[48]);
            device.PhysicalRawOutputWriteTestHook = report =>
            {
                PhysicalReports.Add((byte[])report.Clone());
                return true;
            };
        }

        internal bool Enqueue(byte[] raw)
        {
            var envelope = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
            raw.CopyTo(envelope, NativeOffset);
            // Report0x02 is the command. No0x36/PCM carrier is present.
            return buffer.TryEnqueueOrderedControl(envelope, envelope.Length, Generation, -1,
                nativeCommand: true);
        }

        internal bool DispatchOne()
        {
            if (!buffer.TryPeekNativeCommand(feedback, out int length,
                    out long generation, out int deviceIndex, out long receipt, out _)) return false;
            Assert.AreEqual(Generation, generation);
            Assert.AreEqual(-1, deviceIndex);
            Assert.AreEqual(feedback.Length, length);
            PublishNativeFeedback(deviceIndex);
            Assert.IsTrue(buffer.TryCommitNativeCommand(receipt));
            return true;
        }

        internal void PublishPrecursor(byte[] raw)
        {
            // Establish the already-published state outside the aged queue.
            // The positive pair rows independently cover buffer admission;
            // cold JIT cannot expire this prerequisite before the target stop.
            Array.Clear(feedback);
            raw.CopyTo(feedback, NativeOffset);
            PublishNativeFeedback(deviceIndex: -1);
        }

        private void PublishNativeFeedback(int deviceIndex)
        {
            ViiperOutDevice.PrepareNativeDualSenseOutputReportForProfileInto(feedback, deviceIndex, native);
            NativeReports.Add((byte[])native.Clone());
            Assert.IsTrue(device.WriteRawOutputReportFromGame(native, 0, native.Length, out long revision));
            Assert.IsTrue(revision > 0);
            Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
                device.ProcessNextPhysicalOutputCommand());
            Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.None,
                device.ProcessNextPhysicalOutputCommand());
        }

        internal void Drain()
        {
            int maximum = ViiperOutDevice.FeedbackOrderedControlQueueCapacity + 1;
            for (int index = 0; index < maximum; index++)
                if (!DispatchOne()) return;
            Assert.IsFalse(DispatchOne(), "The bounded fixture failed to drain.");
        }

        internal void AgePendingCommands(int milliseconds)
        {
            var timestamps = (long[])typeof(ViiperFeedbackDispatchBuffer)
                .GetField("orderedControlEnqueueTimestamps", InstanceFields)!.GetValue(buffer)!;
            long aged = Stopwatch.GetTimestamp() - Stopwatch.Frequency * milliseconds / 1000;
            for (int index = 0; index < timestamps.Length; index++)
                if (timestamps[index] != 0) timestamps[index] = aged;
        }
    }

    private static byte[] Envelope(byte[] raw)
    {
        var result = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
        raw.CopyTo(result, 28);
        return result;
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void SetField(object target, string name, object value) =>
        typeof(ViiperOutDevice).GetField(name, PrivateInstance)!.SetValue(target, value);
    private static object GetField(object target, string name) =>
        typeof(ViiperOutDevice).GetField(name, PrivateInstance)!.GetValue(target);
    private static void Invoke(object target, string name) =>
        typeof(ViiperOutDevice).GetMethod(name, PrivateInstance)!.Invoke(target, null);

    private sealed class RecordingDualSense(HidDevice hid) : DualSenseDevice(hid, "Native admission regression")
    {
        internal int CompatibilityRumbleCalls { get; private set; }
        public override void setRumble(byte right, byte left)
        {
            CompatibilityRumbleCalls++;
            base.setRumble(right, left);
        }
    }

    private sealed class AdmissionFixture : IDisposable
    {
        private readonly ControlService previousHub = DS4Windows.Program.rootHub;
        private readonly bool previousOutput = Global.EnableOutputDataToDS4[0];
        private readonly long previousProfileRevision = Global.ReadProfileSwitchRevision(0);
        private readonly AudioHapticsService audio = new();
        internal ViiperOutDevice Output { get; }
        internal ViiperFeedbackDispatchBuffer Buffer { get; }
        internal RecordingDualSense Device { get; }
        internal List<byte[]> Reports { get; } = new();
        internal byte[] Scratch { get; } = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];

        internal AdmissionFixture()
        {
            Device = CreateDevice();
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            hub.DS4Controllers = new DS4Device[4];
            hub.DS4Controllers[0] = Device;
            typeof(ControlService).GetField("audioHapticsService", PrivateInstance)!.SetValue(hub, audio);
            DS4Windows.Program.rootHub = hub;
            Global.EnableOutputDataToDS4[0] = true;
            Output = new(OutContType.None, ViiperVirtualDeviceType.DualSense);
            SetField(Output, "physicalDualSenseIdentityPath", string.Empty);
            SetField(Output, "physicalDualSenseIdentityVerified", true);
            Output.BindPhysicalController(0);
            SetField(Output, "connected", true);
            SetField(Output, "feedbackDispatchStopRequested", false);
            Buffer = (ViiperFeedbackDispatchBuffer)typeof(ViiperOutDevice)
                .GetField("feedbackDispatchBuffer", PrivateInstance)!.GetValue(Output)!;
        }

        internal RecordingDualSense CreateDevice()
        {
            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            typeof(HidDevice).GetField("_deviceAttributes", PrivateInstance)!.SetValue(hid,
                new HidDeviceAttributes(new NativeMethods.HIDD_ATTRIBUTES { VendorID = 0x054C, ProductID = 0x0CE6 }));
            var device = new RecordingDualSense(hid);
            typeof(DS4Device).GetField("conType", PrivateInstance)!.SetValue(device, ConnectionType.USB);
            typeof(DS4Device).GetField("outputReport", PrivateInstance)!.SetValue(device, new byte[48]);
            device.PhysicalRawOutputWriteTestHook = report => { Reports.Add((byte[])report.Clone()); return true; };
            return device;
        }

        internal void FillPhysicalQueue()
        {
            byte[] raw = new byte[48];
            raw[0] = 0x02;
            for (int index = 0; index < 64; index++)
                Assert.IsTrue(Device.WriteRawOutputReportFromGame(raw, 0, raw.Length, out _));
            Assert.IsFalse(Device.WriteRawOutputReportFromGame(raw, 0, raw.Length, out _));
        }

        internal void DrainPhysicalQueue()
        {
            for (int index = 0; index < 65; index++)
                if (Device.ProcessNextPhysicalOutputCommand() == DualSenseDevice.PhysicalOutputCommandProcessResult.None) return;
            Assert.Fail("The bounded physical command owner failed to drain.");
        }

        public void Dispose()
        {
            SetField(Output, "connected", false);
            DS4Windows.Program.rootHub = previousHub;
            Global.EnableOutputDataToDS4[0] = previousOutput;
            var revisions = (long[])typeof(Global).GetField("profileSwitchRevisions",
                BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Interlocked.Exchange(ref revisions[0], previousProfileRevision);
            audio.Dispose();
        }
    }
}
