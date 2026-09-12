using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class ViiperNativeFeedbackRepeatTests
{
    private const int NativeOffset = 28;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [DataTestMethod]
    [DataRow(76)]
    [DataRow(217)]
    [DataRow(474)]
    public void TenThousandPendingRepeatsLeaveRoomForTheExplicitStop(int length)
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] pulse = Rumble(length);
        byte[] stop = Rumble(length, 0);

        for (int index = 0; index < 10000; index++)
            Assert.IsTrue(Enqueue(buffer, pulse, context), $"Repeat {index} blocked the feedback reader.");

        Assert.AreEqual(1, buffer.PendingOrderedControlCount);
        Assert.AreEqual(1L, buffer.OrderedControlEnqueued);
        Assert.AreEqual(9999L, buffer.OrderedControlRepeated);
        Assert.AreEqual(0L, buffer.OrderedControlDropped);
        Assert.IsTrue(Enqueue(buffer, stop, context));
        AssertCommand(buffer, pulse);
        AssertCommand(buffer, stop);
        Assert.AreEqual(0, buffer.PendingOrderedControlCount);
    }

    [TestMethod]
    public void PulseStopPulseEdgesSurviveEvenWhenFirstAndLastAreIdentical()
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] pulse = Rumble();
        byte[] stop = Rumble(strength: 0);
        foreach (byte[] payload in new[] { pulse, stop, pulse })
            Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
        AssertCommand(buffer, pulse);
        AssertCommand(buffer, stop);
        AssertCommand(buffer, pulse);
    }

    [TestMethod]
    public void SameCommandAfterPhysicalAdmissionIsTransmittedAgain()
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] payload = Rumble();
        for (int index = 0; index < 5; index++)
        {
            Assert.IsTrue(Enqueue(buffer, payload, context));
            AssertCommand(buffer, payload);
        }
        Assert.AreEqual(5L, buffer.OrderedControlEnqueued);
        Assert.AreEqual(5L, buffer.OrderedControlDequeued);
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
    }

    [TestMethod]
    public void FullQueueRepeatDoesNotChangeAdmissionIdentityOrOriginalAge()
    {
        var buffer = CreateBuffer(capacity: 1);
        var context = Context(buffer);
        byte[] payload = Rumble();
        Assert.IsTrue(Enqueue(buffer, payload, context));
        long revision = buffer.ControlAdmissionRevision;
        var timestamps = (long[])GetField(buffer, "orderedControlEnqueueTimestamps");
        long originalAge = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        timestamps[0] = originalAge;

        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.AreEqual(revision, buffer.ControlAdmissionRevision);
        Assert.AreEqual(originalAge, timestamps[0], "A duplicate must not refresh the accepted command's age.");
        Assert.AreEqual(1, buffer.PendingOrderedControlCount);
        Assert.AreEqual(1L, buffer.OrderedControlHighWater);
        Assert.IsTrue(buffer.TryPeekNativeCommand(new byte[474], out _, out _, out _, out long receipt, out _));
        Assert.AreEqual(revision, receipt);
        Assert.IsTrue(buffer.TryCommitNativeCommand(receipt));
        Assert.AreEqual(1L, buffer.OrderedControlRepeated);
    }

    [TestMethod]
    public void PeekedTailIsLeasedAndCannotAbsorbAnotherOccurrence()
    {
        var buffer = CreateBuffer(capacity: 1);
        var context = Context(buffer);
        byte[] payload = Rumble();
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.IsTrue(buffer.TryPeekNativeCommand(new byte[474], out _, out _, out _, out long receipt, out _));
        Assert.IsFalse(Enqueue(buffer, payload, context), "An in-flight repeat is a later occurrence, not pending redundancy.");
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
        Assert.IsTrue(buffer.TryCommitNativeCommand(receipt));
        Assert.IsTrue(Enqueue(buffer, payload, context));
        AssertCommand(buffer, payload);
    }

    [TestMethod]
    public void PeekingAnEarlierHeadDoesNotLeaseTheUnpeekedTail()
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] head = Rumble(strength: 30);
        byte[] tail = Rumble(strength: 60);
        Assert.IsTrue(Enqueue(buffer, head, context));
        Assert.IsTrue(Enqueue(buffer, tail, context));
        Assert.IsTrue(buffer.TryPeekNativeCommand(new byte[474], out _, out _, out _, out long receipt, out _));
        Assert.IsTrue(Enqueue(buffer, tail, context));
        Assert.AreEqual(2, buffer.PendingOrderedControlCount);
        Assert.AreEqual(1L, buffer.OrderedControlRepeated);
        Assert.IsTrue(buffer.TryCommitNativeCommand(receipt));
        AssertCommand(buffer, tail);
    }

    [DataTestMethod]
    [DataRow("generation")]
    [DataRow("device")]
    [DataRow("target")]
    [DataRow("binding")]
    [DataRow("profile")]
    [DataRow("boundary")]
    [DataRow("null-target")]
    public void OwnershipContextChangesNeverFold(string changed)
    {
        var buffer = CreateBuffer();
        var first = Context(buffer, new EqualTarget());
        var next = first;
        long generation = 17;
        int deviceIndex = 2;
        switch (changed)
        {
            case "generation": generation++; break;
            case "device": deviceIndex++; break;
            case "target": next = first with { Target = new EqualTarget() }; break;
            case "binding": next = first with { BindingRevision = first.BindingRevision + 1 }; break;
            case "profile": next = first with { ProfileRevision = first.ProfileRevision + 1 }; break;
            case "boundary": next = first with { PendingBoundaryRevision = first.PendingBoundaryRevision + 1 }; break;
            case "null-target": next = first with { Target = null }; break;
        }
        byte[] payload = Rumble();
        Assert.IsTrue(Enqueue(buffer, payload, first));
        Assert.IsTrue(Enqueue(buffer, payload, next, generation, deviceIndex));
        Assert.AreEqual(2, buffer.PendingOrderedControlCount);
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
    }

    [TestMethod]
    public void DefaultContextAndNonNativeFeedbackKeepExistingDeliverySemantics()
    {
        byte[] payload = Rumble();
        foreach (bool native in new[] { false, true })
        {
            var buffer = CreateBuffer();
            for (int index = 0; index < 2; index++)
                Assert.IsTrue(buffer.TryEnqueueOrderedControl(payload, payload.Length, 17, 2, nativeCommand: native));
            Assert.AreEqual(2, buffer.PendingOrderedControlCount);
            Assert.AreEqual(0L, buffer.OrderedControlRepeated);
        }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(27)]
    [DataRow(31)]
    [DataRow(32)]
    [DataRow(72)]
    [DataRow(75)]
    public void AnyEnvelopeByteDifferenceIsPreserved(int offset)
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] first = Rumble();
        byte[] next = (byte[])first.Clone();
        next[offset] ^= 1;
        Assert.IsTrue(Enqueue(buffer, first, context));
        Assert.IsTrue(Enqueue(buffer, next, context));
        Assert.AreEqual(2, buffer.PendingOrderedControlCount);
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
        AssertCommand(buffer, first);
        AssertCommand(buffer, next);
    }

    [TestMethod]
    public void EquivalentNativeBytesInDifferentEnvelopeLengthsAreNotIdentical()
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        foreach (int length in new[] { 76, 217, 474 })
            Assert.IsTrue(Enqueue(buffer, Rumble(length), context));
        Assert.AreEqual(3, buffer.PendingOrderedControlCount);
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
    }

    [DataTestMethod]
    [DataRow(75)]
    [DataRow(77)]
    [DataRow(216)]
    [DataRow(218)]
    [DataRow(473)]
    public void UnknownEnvelopeShapesRemainOrdered(int length)
    {
        AssertRepeatedCommandNotSuppressed(Rumble(length));
    }

    [DataTestMethod]
    [DataRow(217, 76, 0x32)]
    [DataRow(474, 76, 0x36)]
    [DataRow(217, 216, 1)]
    [DataRow(474, 473, 1)]
    public void AudioHapticsCarrierOrUnknownNonzeroSuffixIsNotFolded(int length, int offset, int value)
    {
        byte[] payload = Rumble(length);
        payload[offset] = (byte)value;
        AssertRepeatedCommandNotSuppressed(payload);
    }

    [DataTestMethod]
    [DataRow(1, 0x10)]
    [DataRow(1, 0x20)]
    [DataRow(1, 0x40)]
    [DataRow(1, 0x80)]
    [DataRow(2, 0x01)]
    [DataRow(2, 0x02)]
    [DataRow(2, 0x08)]
    [DataRow(2, 0x20)]
    [DataRow(2, 0x40)]
    [DataRow(2, 0x80)]
    [DataRow(39, 0x01)]
    [DataRow(39, 0x02)]
    [DataRow(39, 0x08)]
    [DataRow(39, 0x80)]
    public void UnknownOrEdgeBearingValidityBitsAreNotFolded(int nativeIndex, int flag)
    {
        byte[] payload = Rumble();
        payload[NativeOffset + nativeIndex] |= (byte)flag;
        AssertRepeatedCommandNotSuppressed(payload);
    }

    [DataTestMethod]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(33)]
    [DataRow(34)]
    [DataRow(35)]
    [DataRow(36)]
    [DataRow(37)]
    [DataRow(38)]
    [DataRow(40)]
    [DataRow(41)]
    [DataRow(42)]
    [DataRow(43)]
    public void ReservedAudioAndVendorFieldsRemainConservative(int nativeIndex)
    {
        byte[] payload = Rumble();
        payload[NativeOffset + nativeIndex] = 1;
        AssertRepeatedCommandNotSuppressed(payload);
    }

    [TestMethod]
    public void NonNativeReportIdentifierRemainsOrdered()
    {
        byte[] payload = Rumble();
        payload[NativeOffset] = 0x31;
        AssertRepeatedCommandNotSuppressed(payload);
    }

    [DataTestMethod]
    [DataRow(0x00)]
    [DataRow(0x01)]
    [DataRow(0x02)]
    [DataRow(0x05)]
    [DataRow(0x21)]
    [DataRow(0x25)]
    [DataRow(0x26)]
    public void KnownTriggerStateRepeatsAreFoldedButTheirStopIsRetained(int mode)
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] payload = Rumble();
        payload[NativeOffset + 1] |= 0x0C;
        payload[NativeOffset + 11] = (byte)mode;
        payload[NativeOffset + 22] = (byte)mode;
        payload[NativeOffset + 12] = 12;
        payload[NativeOffset + 23] = 23;
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.AreEqual(1L, buffer.OrderedControlRepeated);
        byte[] stop = (byte[])payload.Clone();
        Array.Clear(stop, NativeOffset + 11, 22);
        stop[NativeOffset + 3] = 0;
        stop[NativeOffset + 4] = 0;
        Assert.IsTrue(Enqueue(buffer, stop, context));
        AssertCommand(buffer, payload);
        AssertCommand(buffer, stop);
    }

    [DataTestMethod]
    [DataRow(11, 0x06)]
    [DataRow(22, 0x06)]
    [DataRow(11, 0xFC)]
    [DataRow(22, 0xFC)]
    [DataRow(11, 0xFF)]
    [DataRow(22, 0xFF)]
    public void UnknownOrCalibrationTriggerModesAreNotFolded(int nativeIndex, int mode)
    {
        byte[] payload = Rumble();
        payload[NativeOffset + 1] |= 0x0C;
        payload[NativeOffset + nativeIndex] = (byte)mode;
        AssertRepeatedCommandNotSuppressed(payload);
    }

    [DataTestMethod]
    [DataRow(11, 7)]
    [DataRow(11, 8)]
    [DataRow(11, 10)]
    [DataRow(22, 7)]
    [DataRow(22, 8)]
    [DataRow(22, 10)]
    public void KnownTriggerModeWithUnknownReservedDataIsNotFolded(int triggerOffset, int reservedIndex)
    {
        byte[] payload = Rumble();
        payload[NativeOffset + 1] |= 0x0C;
        payload[NativeOffset + triggerOffset] = 0x21;
        payload[NativeOffset + triggerOffset + reservedIndex] = 1;
        AssertRepeatedCommandNotSuppressed(payload);
    }

    [DataTestMethod]
    [DataRow(0x04)]
    [DataRow(0x10)]
    [DataRow(0x14)]
    public void KnownLedAndPlayerStateRepeatsAreFolded(int flag)
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] payload = Rumble();
        payload[NativeOffset + 2] = (byte)flag;
        payload[NativeOffset + 44] = 0x04;
        payload[NativeOffset + 45] = 20;
        payload[NativeOffset + 46] = 30;
        payload[NativeOffset + 47] = 40;
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.AreEqual(1, buffer.PendingOrderedControlCount);
        Assert.AreEqual(1L, buffer.OrderedControlRepeated);
    }

    [DataTestMethod]
    [DataRow("speaker")]
    [DataRow("legacy-control")]
    [DataRow("explicit-lane-boundary")]
    public void InterveningOtherLaneWorkBreaksConsecutiveRepeatIdentity(string lane)
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] payload = Rumble();
        Assert.IsTrue(Enqueue(buffer, payload, context));
        switch (lane)
        {
            case "speaker":
                Assert.IsTrue(buffer.TryEnqueueSpeaker(new byte[] { 1, 2, 3, 4 }, 4, 17));
                break;
            case "legacy-control":
                Assert.IsTrue(buffer.QueueControl(new byte[] { 8, 9 }, 2, 17, 2));
                break;
            case "explicit-lane-boundary": buffer.InvalidateNativeRepeat(); break;
        }
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.AreEqual(2, buffer.PendingOrderedControlCount);
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
    }

    [TestMethod]
    public void SpeakerPcmDuplicatesRemainTwoTimeBearingFrames()
    {
        var buffer = CreateBuffer();
        byte[] pcm = { 1, 2, 3, 4 };
        Assert.IsTrue(buffer.TryEnqueueSpeaker(pcm, pcm.Length, 17));
        Assert.IsTrue(buffer.TryEnqueueSpeaker(pcm, pcm.Length, 17));
        Assert.AreEqual(2, buffer.PendingSpeakerCount);
        Assert.AreEqual(2L, buffer.SpeakerEnqueued);
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
    }

    [TestMethod]
    public void RepeatedTailSurvivesRingWrapWithoutMergingAReusedSlot()
    {
        var buffer = CreateBuffer(capacity: 2);
        var context = Context(buffer);
        byte[] prior = Rumble(strength: 1);
        byte[] next = Rumble(strength: 2);
        byte[] wrapped = Rumble(strength: 3);
        Assert.IsTrue(Enqueue(buffer, prior, context));
        Assert.IsTrue(Enqueue(buffer, next, context));
        AssertCommand(buffer, prior);
        Assert.IsTrue(Enqueue(buffer, wrapped, context));
        Assert.IsTrue(Enqueue(buffer, wrapped, context));
        Assert.AreEqual(2, buffer.PendingOrderedControlCount);
        Assert.AreEqual(1L, buffer.OrderedControlRepeated);
        AssertCommand(buffer, next);
        AssertCommand(buffer, wrapped);
        Assert.IsTrue(Enqueue(buffer, prior, context));
        AssertCommand(buffer, prior);
        Assert.AreEqual(4L, buffer.OrderedControlEnqueued);
    }

    [TestMethod]
    public void FailedFirstAdmissionCannotSeedARepeatOrLoseTheRetriedCommand()
    {
        var buffer = CreateBuffer(capacity: 1);
        var context = Context(buffer);
        byte[] prior = Rumble(strength: 1);
        byte[] rejected = Rumble(strength: 2);
        Assert.IsTrue(Enqueue(buffer, prior, context));
        Assert.IsFalse(Enqueue(buffer, rejected, context));
        Assert.IsFalse(Enqueue(buffer, rejected, context));
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
        AssertCommand(buffer, prior);
        Assert.IsTrue(Enqueue(buffer, rejected, context));
        Assert.AreEqual(2L, buffer.OrderedControlEnqueued);
        AssertCommand(buffer, rejected);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClearAndResetFenceOldRepeatIdentityAndStaleCommitReceipts(bool reset)
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] payload = Rumble();
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.IsTrue(buffer.TryPeekNativeCommand(new byte[474], out _, out _, out _, out long oldReceipt, out _));
        long oldBoundary = buffer.PendingBoundaryRevision;
        if (reset) buffer.Reset(); else buffer.ClearPending();
        Assert.IsFalse(buffer.TryEnqueueOrderedControl(payload, payload.Length, 17, 2,
            nativeCommand: true, expectedBoundaryRevision: oldBoundary, nativeContext: context));
        context = context with { PendingBoundaryRevision = buffer.PendingBoundaryRevision };
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.IsFalse(buffer.TryCommitNativeCommand(oldReceipt));
        Assert.AreEqual(1, buffer.PendingOrderedControlCount);
        Assert.AreEqual(reset ? 0L : 1L, buffer.OrderedControlRepeated);
        Assert.AreEqual(reset ? 1L : 2L, buffer.OrderedControlEnqueued);
        AssertCommand(buffer, payload);
    }

    [TestMethod]
    public void RepeatHotPathAllocatesNothingAfterWarmup()
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        byte[] payload = Rumble(474);
        for (int index = 0; index < 512; index++)
            Assert.IsTrue(Enqueue(buffer, payload, context));
        bool accepted = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10000; index++)
            accepted &= Enqueue(buffer, payload, context);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(accepted);
        Assert.AreEqual(0L, allocated, "Pending-repeat comparison must not allocate on the feedback reader hot path.");
        Assert.AreEqual(10511L, buffer.OrderedControlRepeated);
        Assert.AreEqual(1, buffer.PendingOrderedControlCount);
    }

    [TestMethod]
    public void ActualNativeReaderAdmitsDenseRepeatsAndStopWithoutAConsumerDrain()
    {
        using var fixture = new ReaderFixture();
        byte[] pulse = Rumble(474);
        byte[] stop = Rumble(474, 0);
        Assert.IsTrue(fixture.Output.TryCaptureNativeCommandContext(pulse, pulse.Length, 0, out var context));
        bool accepted = true;
        Exception failure = null;
        var producer = new Thread(() =>
        {
            try
            {
                for (int index = 0; index < 10000; index++)
                    if (!fixture.Output.EnqueueNativeCommandUntilCancelled(null, 0, pulse, pulse.Length, 0, context))
                    {
                        accepted = false;
                        return;
                    }
                accepted = fixture.Output.EnqueueNativeCommandUntilCancelled(null, 0, stop, stop.Length, 0, context);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        producer.Start();
        try
        {
            Assert.IsTrue(producer.Join(5000), "Identical pending state updates saturated the actual framed reader admission path.");
            Assert.IsNull(failure);
            Assert.IsTrue(accepted);
            Assert.AreEqual(2, fixture.Buffer.PendingOrderedControlCount);
            Assert.AreEqual(9999L, fixture.Buffer.OrderedControlRepeated);
            fixture.DispatchOne();
            fixture.DispatchOne();
            Assert.AreEqual(2, fixture.Reports.Count);
            Assert.AreEqual((byte)90, fixture.Reports[0][3]);
            Assert.AreEqual((byte)90, fixture.Reports[0][4]);
            Assert.AreEqual((byte)0, fixture.Reports[1][3]);
            Assert.AreEqual((byte)0, fixture.Reports[1][4]);
            Assert.AreEqual(0, fixture.Buffer.PendingOrderedControlCount);
        }
        finally
        {
            SetField(fixture.Output, "feedbackDispatchStopRequested", true);
            fixture.Buffer.ClearPending();
            Assert.IsTrue(producer.Join(2000), "The hardware-free reader fixture did not cancel cleanly.");
        }
    }

    [TestMethod]
    public void OpaqueTriggerExtensionBytesReachThePhysicalOwnerUnmodified()
    {
        using var fixture = new ReaderFixture();
        byte[] payload = Rumble(474);
        payload[NativeOffset + 1] |= 0x0C;
        // Populate every byte, including the bytes outside our repeat-policy
        // allowlist. They are not invalid HID and must never be stripped by
        // an optimization that simply does not understand their semantics.
        for (int index = 0; index < 22; index++)
            payload[NativeOffset + 11 + index] = (byte)(0x80 + index);
        payload[NativeOffset + 11] = 0x21;
        payload[NativeOffset + 22] = 0x26;
        Assert.IsTrue(fixture.Output.TryCaptureNativeCommandContext(payload, payload.Length, 0, out var context));
        for (int index = 0; index < 2; index++)
            Assert.IsTrue(fixture.Output.EnqueueNativeCommandUntilCancelled(null, 0,
                payload, payload.Length, 0, context));
        Assert.AreEqual(0L, fixture.Buffer.OrderedControlRepeated);
        fixture.DispatchOne();
        fixture.DispatchOne();
        Assert.AreEqual(2, fixture.Reports.Count);
        foreach (byte[] physical in fixture.Reports)
        {
            Assert.AreEqual(0x0C, physical[1] & 0x0C);
            CollectionAssert.AreEqual(payload[(NativeOffset + 11)..(NativeOffset + 33)],
                physical[11..33], "Unrecognized trigger data must pass through, not disappear.");
        }
    }

    private static ViiperFeedbackDispatchBuffer CreateBuffer(int capacity = 4) =>
        new(2, 4096, 474, capacity, orderedControlMaximumAgeMilliseconds: 20);

    private static ViiperNativeCommandContext Context(ViiperFeedbackDispatchBuffer buffer, object target = null) =>
        new(target ?? new object(), 11, 13, buffer.PendingBoundaryRevision);

    private static byte[] Rumble(int length = 76, byte strength = 90)
    {
        var payload = new byte[length];
        payload[NativeOffset] = 0x02;
        payload[NativeOffset + 1] = 0x02;
        payload[NativeOffset + 3] = strength;
        payload[NativeOffset + 4] = strength;
        payload[NativeOffset + 39] = 0x04;
        return payload;
    }

    private static bool Enqueue(ViiperFeedbackDispatchBuffer buffer, byte[] payload,
        ViiperNativeCommandContext context, long generation = 17, int deviceIndex = 2) =>
        buffer.TryEnqueueOrderedControl(payload, payload.Length, generation, deviceIndex,
            nativeCommand: true, nativeContext: context);

    private static void AssertCommand(ViiperFeedbackDispatchBuffer buffer, byte[] expected)
    {
        byte[] actual = new byte[474];
        Assert.IsTrue(buffer.TryPeekNativeCommand(actual, out int length, out _, out _, out long receipt, out _));
        CollectionAssert.AreEqual(expected, actual[..length]);
        Assert.IsTrue(buffer.TryCommitNativeCommand(receipt));
    }

    private static void AssertRepeatedCommandNotSuppressed(byte[] payload)
    {
        var buffer = CreateBuffer();
        var context = Context(buffer);
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.IsTrue(Enqueue(buffer, payload, context));
        Assert.AreEqual(2, buffer.PendingOrderedControlCount);
        Assert.AreEqual(0L, buffer.OrderedControlRepeated);
    }

    private sealed class EqualTarget
    {
        public override bool Equals(object other) => other is EqualTarget;
        public override int GetHashCode() => 0;
    }

    private static object GetField(object target, string name) =>
        target.GetType().GetField(name, PrivateInstance)!.GetValue(target);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);

    private sealed class ReaderFixture : IDisposable
    {
        private readonly ControlService previousHub = DS4Windows.Program.rootHub;
        private readonly bool previousOutput = Global.EnableOutputDataToDS4[0];
        private readonly TriggerLabProfileSettings previousTriggerLab = Global.store.triggerLabSettings[0];
        private readonly AudioHapticsService audio = new();
        private readonly DualSenseDevice device;
        internal ViiperOutDevice Output { get; }
        internal ViiperFeedbackDispatchBuffer Buffer { get; }
        internal List<byte[]> Reports { get; } = new();

        internal ReaderFixture()
        {
            var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
            SetField(hid, "_deviceAttributes", new HidDeviceAttributes(
                new NativeMethods.HIDD_ATTRIBUTES { VendorID = 0x054C, ProductID = 0x0CE6 }));
            device = new DualSenseDevice(hid, "Pending feedback repeat regression");
            typeof(DS4Device).GetField("conType", PrivateInstance)!.SetValue(device, ConnectionType.USB);
            typeof(DS4Device).GetField("outputReport", PrivateInstance)!.SetValue(device, new byte[48]);
            device.PhysicalRawOutputWriteTestHook = report => { Reports.Add((byte[])report.Clone()); return true; };
            var hub = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
            hub.DS4Controllers = new DS4Device[4];
            hub.DS4Controllers[0] = device;
            SetField(hub, "audioHapticsService", audio);
            DS4Windows.Program.rootHub = hub;
            Global.EnableOutputDataToDS4[0] = true;
            Global.store.triggerLabSettings[0] = new TriggerLabProfileSettings();
            Output = new(OutContType.None, ViiperVirtualDeviceType.DualSense);
            SetField(Output, "physicalDualSenseIdentityPath", string.Empty);
            SetField(Output, "physicalDualSenseIdentityVerified", true);
            Output.BindPhysicalController(0);
            SetField(Output, "connected", true);
            SetField(Output, "feedbackDispatchStopRequested", false);
            Buffer = (ViiperFeedbackDispatchBuffer)GetField(Output, "feedbackDispatchBuffer");
        }

        internal void DispatchOne()
        {
            var payload = new byte[474];
            Assert.IsTrue(Buffer.TryPeekNativeCommand(payload, out int length, out long generation,
                out int index, out long receipt, out var context));
            Assert.IsTrue(Output.TryApplyRetainedNativeCommand(payload, length, index, generation, context, new byte[48]));
            Assert.IsTrue(Buffer.TryCommitNativeCommand(receipt));
            Assert.AreEqual(DualSenseDevice.PhysicalOutputCommandProcessResult.Published,
                device.ProcessNextPhysicalOutputCommand());
        }

        public void Dispose()
        {
            SetField(Output, "connected", false);
            DS4Windows.Program.rootHub = previousHub;
            Global.EnableOutputDataToDS4[0] = previousOutput;
            Global.store.triggerLabSettings[0] = previousTriggerLab;
            audio.Dispose();
        }
    }
}
