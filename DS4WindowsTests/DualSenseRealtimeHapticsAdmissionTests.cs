using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class DualSenseRealtimeHapticsAdmissionTests
{
    [TestMethod]
    public void HealthySamePacerLifecycleCheckDoesNotDiscardCompletedMedia()
    {
        using var fixture = new Fixture();
        byte[] frame = BuildMedia(0x61);
        Set(fixture.Device, "bluetoothAudioLifecycleTransitioning", 1);
        fixture.Publish(frame);
        CollectionAssert.AreEqual(frame[154..218], fixture.ReadOnePhysicalHaptics());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WaitingOldMediaCannotAdoptReplacementOutputGeneration(bool replacePacer)
    {
        using var fixture = new Fixture();
        byte[] oldFrame = BuildMedia(0x72);
        byte[] newFrame = BuildMedia(0x83);
        Exception error = null;
        var worker = new Thread(() =>
        {
            try { fixture.Publish(oldFrame); }
            catch (Exception caught) { error = caught; }
        }) { IsBackground = true };
        object gate = Field(fixture.Device,
            "bluetoothRealtimeHapticsPublicationLock").GetValue(fixture.Device)!;
        try
        {
            lock (gate)
            {
                worker.Start();
                fixture.WaitForCachedBlock(oldFrame);
                // The input cache is already updated and every other lock is
                // free. The only blocking seam is this exact media monitor.
                // No scheduling sleep is used to guess whether entry occurred.
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                    (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, 2000));
                if (replacePacer) fixture.ReplacePacer();
                else Set(fixture.Device, "physicalOutputGeneration", 1L);
            }
            Assert.IsTrue(worker.Join(2000));
            Assert.IsNull(error);
            Assert.AreEqual(0, fixture.ReadAllPhysicalHaptics().Count,
                "Queued media belonging to an old owner reached its successor.");
            fixture.Publish(newFrame);
            CollectionAssert.AreEqual(newFrame[154..218], fixture.ReadOnePhysicalHaptics());
        }
        finally
        {
            fixture.SignalStop();
            Assert.IsTrue(worker.Join(2000));
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BothPcmIngressFormatsPreserveStereoAndTheExplicitZeroTail(bool converted)
    {
        using var fixture = new Fixture();
        byte[] attack = BuildMedia(0xA1);
        byte[] zero = BuildMedia(0);
        Array.Clear(zero, 154, 64);
        fixture.SetControlTemplateBusy(true);
        if (converted)
        {
            Assert.IsTrue(fixture.Device.WriteBluetoothHapticsSamples(attack, 154, 64));
            Assert.IsTrue(fixture.Device.WriteBluetoothHapticsSamples(zero, 154, 64));
        }
        else
        {
            fixture.Publish(attack);
            fixture.Publish(zero);
        }
        List<byte[]> presented = fixture.ReadAllPhysicalHaptics();
        Assert.AreEqual(2, presented.Count);
        CollectionAssert.AreEqual(attack[154..218], presented[0]);
        CollectionAssert.AreEqual(new byte[64], presented[1]);
        CollectionAssert.AreEqual(fixture.OriginalControllerState,
            fixture.ReadPacerControllerState());
    }

    [TestMethod]
    public void FullRingKeepsExactPendingPcmDespiteLaterCacheAndControlUpdates()
    {
        using var fixture = new Fixture(capacity: 2);
        byte[][] frames = Enumerable.Range(1, 4)
            .Select(index => BuildMedia((byte)(index * 31))).ToArray();
        fixture.Publish(frames[0]);
        fixture.Publish(frames[1]);
        Task pending = Task.Run(() => fixture.Publish(frames[2]));
        Task later = null;
        try
        {
            fixture.WaitForPacerBlock(frames[2]);
            Assert.IsFalse(pending.IsCompleted);
            fixture.AssertControllerLocksAvailable();
            // D overwrites the latest cache while C is blocked by actual
            // shared-ring capacity. C must retain its own exact block; D must
            // follow it, not replace it or duplicate the newest cache.
            later = Task.Run(() => fixture.Publish(frames[3]));
            fixture.WaitForCachedBlock(frames[3]);
            Assert.IsFalse(later.IsCompleted);
            fixture.SetControlTemplateBusy(true);
            byte[] newerControllerState = fixture.ReplacePacerControllerState();
            var presented = new List<byte[]> { fixture.ReadOnePhysicalHaptics() };
            Assert.IsTrue(pending.Wait(2000), "C did not resume after one ring credit.");
            presented.Add(fixture.ReadOnePhysicalHaptics());
            Assert.IsTrue(later.Wait(2000), "D did not resume after the next ring credit.");
            presented.AddRange(fixture.ReadAllPhysicalHaptics());
            Assert.AreEqual(4, presented.Count);
            for (int index = 0; index < frames.Length; index++)
                CollectionAssert.AreEqual(frames[index][154..218], presented[index]);
            CollectionAssert.AreEqual(newerControllerState, fixture.ReadPacerControllerState(),
                "Later native/controller state must not be replaced by a PCM snapshot.");
        }
        finally
        {
            fixture.SignalStop();
            Assert.IsTrue(pending.Wait(2000));
            if (later != null) Assert.IsTrue(later.Wait(2000));
        }
    }

    [TestMethod]
    public void StopCancelsFullRingPublicationAndDoesNotPublishTheBlockedTail()
    {
        using var fixture = new Fixture(capacity: 2);
        byte[] attack = BuildMedia(0x21);
        byte[] held = BuildMedia(0x32);
        byte[] blocked = BuildMedia(0x43);
        fixture.Publish(attack);
        fixture.Publish(held);
        Task pending = Task.Run(() => fixture.Publish(blocked));
        try
        {
            fixture.WaitForPacerBlock(blocked);
            fixture.SignalStop();
            Assert.IsTrue(pending.Wait(2000), "The existing ring stop must unblock its producer.");
            fixture.Publish(BuildMedia(0x54));
            List<byte[]> presented = fixture.ReadAllPhysicalHaptics();
            Assert.AreEqual(2, presented.Count);
            CollectionAssert.AreEqual(attack[154..218], presented[0]);
            CollectionAssert.AreEqual(held[154..218], presented[1]);
            Assert.AreEqual(0, (int)Field(fixture.Device,
                "bluetoothAudioPacerActiveOperations").GetValue(fixture.Device)!);
        }
        finally
        {
            fixture.SignalStop();
            Assert.IsTrue(pending.Wait(2000));
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WarmedAtomicPcmPublicationAllocatesNoManagedMemory(bool positiveControl)
    {
        using var fixture = new Fixture();
        byte[] frame = BuildMedia(0x41);
        byte[] physicalReport = new byte[398];
        fixture.SetControlTemplateBusy(true);
        bool allPresented = true;
        for (int index = 0; index < 6000; index++)
        {
            fixture.Publish(frame);
            allPresented &= fixture.ConsumeInto(physicalReport);
        }
        long allocated;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 6000; index++)
            {
                fixture.Publish(frame);
                allPresented &= fixture.ConsumeInto(physicalReport);
            }
            if (positiveControl) GC.KeepAlive(new byte[128]);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.IsTrue(allPresented);
        if (positiveControl) Assert.IsTrue(allocated >= 128);
        else Assert.AreEqual(0L, allocated);
        CollectionAssert.AreEqual(frame[154..218], physicalReport[78..142]);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AtomicPcmFramesReachThePhysicalRingInOrderDuringTemplateContention(
        bool controlTemplateIsBusy)
    {
        using var fixture = new Fixture();
        byte[] attack = BuildMedia(0x31);
        byte[] tail = BuildMedia(0x79);

        // This is the exact existing CAS shared by local control, microphone
        // transitions and realtime media. Holding it models the admitted
        // control operation without a worker, sleep, helper or HID handle.
        fixture.SetControlTemplateBusy(controlTemplateIsBusy);
        fixture.Output.ApplyAtomicAudioHapticsFeedback(attack, attack.Length, 0);
        fixture.SetControlTemplateBusy(false);
        fixture.Output.ApplyAtomicAudioHapticsFeedback(tail, tail.Length, 0);

        List<byte[]> presented = fixture.ReadAllPhysicalHaptics();
        Assert.AreEqual(2, presented.Count,
            "Both complete PCM blocks must reach the lossless physical ring. " +
            "A busy control-template scratch buffer must not discard a media block. " +
            $"Device status: {fixture.Device.LastBluetoothHapticsWriteStatus}");
        CollectionAssert.AreEqual(attack[154..218], presented[0]);
        CollectionAssert.AreEqual(tail[154..218], presented[1]);
        Assert.AreEqual(0, fixture.Device.CompatibilityRumbleCalls,
            "PCM must not be reinterpreted as scalar compatibility rumble.");
        CollectionAssert.AreEqual(fixture.OriginalControllerState,
            fixture.ReadPacerControllerState(),
            "Realtime PCM cannot replace or rearm controller-state strobes.");
    }

    private static byte[] BuildMedia(byte seed)
    {
        // Actual VIIPER atomic feedback shape: 28 compatibility bytes, 48
        // native USB bytes and the 398-byte 0x36 media carrier. The realtime
        // lane owns only its 64 signed eight-bit stereo rear-channel bytes.
        byte[] feedback = new byte[ViiperOutDevice.DualSenseAtomicFeedbackLength];
        feedback[28] = 0x02;
        const int carrier = 76;
        feedback[carrier] = 0x36;
        feedback[carrier + 11] = 0x90;
        feedback[carrier + 12] = 63;
        feedback[carrier + 76] = 0x92;
        feedback[carrier + 77] = 64;
        for (int index = 0; index < 64; index++)
            feedback[carrier + 78 + index] = (byte)(seed + index * 7);
        return feedback;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ControlService previousHub = DS4Windows.Program.rootHub;
        private readonly bool previousOutputEnabled = Global.EnableOutputDataToDS4[0];
        private readonly AudioHapticsService audio = new(); // Never starts capture.
        private readonly DualSenseRealtimeHapticsSharedRing producer;
        private readonly DualSenseRealtimeHapticsSharedRing consumer;
        private readonly DualSenseBluetoothAudioPacer pacer;
        internal RecordingDualSense Device { get; }
        internal ViiperOutDevice Output { get; }
        internal byte[] OriginalControllerState { get; }

        internal Fixture(int capacity = 4)
        {
            producer = DualSenseRealtimeHapticsSharedRing.CreateOwner(
                "DS4Windows.Tests.PcmAdmission." + Guid.NewGuid().ToString("N"), capacity);
            consumer = DualSenseRealtimeHapticsSharedRing.OpenConsumer(
                producer.MapName, producer.SpaceAvailableName,
                producer.StopRequestedName, producer.Capacity);

            // Exercise the real parent-pacer media publisher and its real
            // shared ring. No helper process, pipe, transport or HID is opened.
            pacer = (DualSenseBluetoothAudioPacer)RuntimeHelpers.GetUninitializedObject(
                typeof(DualSenseBluetoothAudioPacer));
            Set(pacer, "stateLock", new object());
            Set(pacer, "realtimeHaptics", producer);
            Set(pacer, "realtimeHapticsGeneration", 1);
            byte[] template = new byte[398];
            for (int index = 13; index < 60; index++)
                template[index] = (byte)(index * 3);
            Set(pacer, "latestTemplate", template);
            OriginalControllerState = template[13..60];

            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            Set(hid, "_deviceAttributes", new HidDeviceAttributes(
                new NativeMethods.HIDD_ATTRIBUTES { VendorID = 0x054C, ProductID = 0x0CE6 }));
            Device = new RecordingDualSense(hid);
            Set(Device, "conType", ConnectionType.BT);
            Device.EnableSpeakerOutput = true;
            Set(Device, "bluetoothSpeakerClockActiveClaim", 1L);
            Set(Device, "bluetoothSpeakerClockLeaseExpiryTimestamp", long.MaxValue);
            Set(Device, "bluetoothAudioPacer", pacer);

            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            hub.DS4Controllers = new DS4Device[4];
            hub.DS4Controllers[0] = Device;
            Set(hub, "audioHapticsService", audio);
            DS4Windows.Program.rootHub = hub;
            Global.EnableOutputDataToDS4[0] = true;

            Output = new ViiperOutDevice(OutContType.None, ViiperVirtualDeviceType.DualSense);
            Set(Output, "lastInputDeviceIndex", 0);
            // An already verified synthetic Sony identity avoids PnP lookup.
            Set(Output, "physicalDualSenseIdentityPath", string.Empty);
            Set(Output, "physicalDualSenseIdentityVerified", true);
        }

        internal void SetControlTemplateBusy(bool value) =>
            Set(Device, "bluetoothCombinedTemplateUpdateClaimed", value ? 1 : 0);

        internal void Publish(byte[] frame) =>
            Output.ApplyAtomicAudioHapticsFeedback(frame, frame.Length, 0);

        internal bool ConsumeInto(byte[] physicalReport)
        {
            bool result = consumer.PrepareForPresentation(physicalReport, long.MaxValue - 1);
            if (result) consumer.CommitPrepared();
            return result;
        }

        internal byte[] ReadOnePhysicalHaptics()
        {
            byte[] physicalReport = new byte[398];
            Assert.IsTrue(ConsumeInto(physicalReport));
            return physicalReport[78..142];
        }

        internal void WaitForPacerBlock(byte[] expected) =>
            WaitForBlock(pacer, "stateLock", "latestTemplate", 78, expected);

        internal void WaitForCachedBlock(byte[] expected) =>
            WaitForBlock(Device, "bluetoothCombinedSpeakerReportLock",
                "latestBluetoothCombinedSpeakerReport", 78, expected);

        private static void WaitForBlock(object target, string gateName,
            string bufferName, int offset, byte[] expected)
        {
            object gate = Field(target, gateName).GetValue(target)!;
            byte[] buffer = (byte[])Field(target, bufferName).GetValue(target)!;
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                lock (gate)
                    return buffer.AsSpan(offset, 64).SequenceEqual(expected.AsSpan(154, 64));
            }, 2000), "The real publisher did not reach the expected pending block.");
        }

        internal void AssertControllerLocksAvailable()
        {
            foreach (string field in new[] { "bluetoothCombinedTransportWriteLock",
                "bluetoothCombinedSpeakerReportLock" })
            {
                object gate = Field(Device, field).GetValue(Device)!;
                bool entered = Monitor.TryEnter(gate);
                if (entered) Monitor.Exit(gate);
                Assert.IsTrue(entered, $"PCM capacity wait retained {field}.");
            }
            object stateGate = Field(pacer, "stateLock").GetValue(pacer)!;
            bool stateEntered = Monitor.TryEnter(stateGate);
            if (stateEntered) Monitor.Exit(stateGate);
            Assert.IsTrue(stateEntered, "PCM capacity wait retained the pacer state lock.");
        }

        internal byte[] ReplacePacerControllerState()
        {
            object gate = Field(pacer, "stateLock").GetValue(pacer)!;
            lock (gate)
            {
                byte[] template = (byte[])Field(pacer, "latestTemplate").GetValue(pacer)!;
                for (int index = 13; index < 60; index++)
                    template[index] = (byte)(255 - index);
                return template[13..60];
            }
        }

        internal void ReplacePacer()
        {
            var replacement = (DualSenseBluetoothAudioPacer)RuntimeHelpers.GetUninitializedObject(
                typeof(DualSenseBluetoothAudioPacer));
            Set(replacement, "stateLock", new object());
            Set(replacement, "realtimeHaptics", producer);
            Set(replacement, "realtimeHapticsGeneration", 1);
            Set(replacement, "latestTemplate", new byte[398]);
            Set(Device, "bluetoothAudioPacer", replacement);
        }

        internal void SignalStop()
        {
            // Use the real ring cancellation signal without starting a HID
            // lifecycle owner. Suppress recovery because this fixture retires.
            Set(Device, "bluetoothOutputTransportStopping", 1);
            producer.RequestStop();
        }

        internal List<byte[]> ReadAllPhysicalHaptics()
        {
            var result = new List<byte[]>();
            byte[] physicalReport = new byte[398];
            while (consumer.PrepareForPresentation(physicalReport, long.MaxValue - 1))
            {
                result.Add(physicalReport[78..142]);
                consumer.CommitPrepared();
                Assert.IsTrue(result.Count <= 4, "Unexpected repeated media publication.");
            }
            return result;
        }

        internal byte[] ReadPacerControllerState() =>
            ((byte[])Field(pacer, "latestTemplate").GetValue(pacer)!)[13..60];

        public void Dispose()
        {
            SignalStop();
            Set(Device, "bluetoothAudioPacer", null);
            DS4Windows.Program.rootHub = previousHub;
            Global.EnableOutputDataToDS4[0] = previousOutputEnabled;
            consumer.Dispose();
            producer.Dispose();
            audio.Dispose();
            // The uninitialized parent-pacer shell owns no process/resources;
            // only the two real ring handles above require disposal.
        }
    }

    private sealed class RecordingDualSense(HidDevice hid)
        : DualSenseDevice(hid, "Realtime PCM admission regression")
    {
        internal int CompatibilityRumbleCalls { get; private set; }
        public override void setRumble(byte rightLightFastMotor, byte leftHeavySlowMotor)
        {
            CompatibilityRumbleCalls++;
            base.setRumble(rightLightFastMotor, leftHeavySlowMotor);
        }
    }

    private static FieldInfo Field(object instance, string name)
    {
        for (Type type = instance.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo result = type.GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (result != null) return result;
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
    }

    private static void Set(object instance, string name, object value) =>
        Field(instance, name).SetValue(instance, value);
}
