using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public class Switch2OrderedEgressTests
{
    [TestMethod]
    public void CanonicalStateAndProjectionUseExactProtocolCenter()
    {
        var source = new DS4State
        {
            Cross = true,
            DpadUp = true,
            LX = 0,
            LY = 128,
            RX = 255,
            RY = 128,
        };

        Switch2EgressState projected = ViiperStatePacketBuilder.
            BuildSwitch2State(source, -1);
        byte[] wire = new byte[Switch2EgressState.WireSize];
        projected.BuildInto(wire);

        Assert.AreEqual(1u | 1u << 11,
            BinaryPrimitives.ReadUInt32LittleEndian(wire));
        Assert.AreEqual((ushort)0,
            BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(4, 2)));
        Assert.AreEqual((ushort)0x0800,
            BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(6, 2)));
        Assert.AreEqual((ushort)0x0FFF,
            BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(8, 2)));
        Assert.AreEqual((ushort)0x0800,
            BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(10, 2)));

        byte[] legacy = ViiperStatePacketBuilder.Build(
            ViiperVirtualDeviceType.Switch2Pro, source, -1);
        CollectionAssert.AreEqual(wire, legacy);

        byte[] neutral = new byte[Switch2EgressState.WireSize];
        Switch2EgressState.Neutral.BuildInto(neutral);
        for (int offset = 4; offset <= 10; offset += 2)
        {
            Assert.AreEqual((ushort)0x0800,
                BinaryPrimitives.ReadUInt16LittleEndian(
                    neutral.AsSpan(offset, 2)));
        }
    }

    [TestMethod]
    public void ButtonsAreOrderedWhileAxesAndMotionCoalesce()
    {
        var scheduler = new Switch2EgressScheduler(1_000_000);
        OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;

        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedContinuous,
            scheduler.Publish(epoch, State(lx: 100), 1));
        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedContinuous,
            scheduler.Publish(epoch, State(lx: 200, gyroYaw: 8), 2));
        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
            scheduler.Publish(epoch, State(buttons: 1, lx: 200), 3));
        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
            scheduler.Publish(epoch, State(lx: 200), 4));

        OrderedEgressClaim<Switch2EgressState> continuous =
            Claim(scheduler, 5);
        Assert.AreEqual(OrderedEgressClaimKind.Ordered, continuous.Kind,
            "The latest axes/motion predecessor must be atomically promoted.");
        Assert.AreEqual((ushort)200, continuous.State.LeftStickX);
        Commit(scheduler, continuous, 5);

        OrderedEgressClaim<Switch2EgressState> press = Claim(scheduler, 5);
        Assert.AreEqual(1u, press.State.Buttons);
        Commit(scheduler, press, 5);
        OrderedEgressClaim<Switch2EgressState> release = Claim(scheduler, 5);
        Assert.AreEqual(0u, release.State.Buttons);
        Commit(scheduler, release, 5);
    }

    [TestMethod]
    public void CapacityOverflowAlwaysForcesNeutralEvenWithNoAgeLimit()
    {
        var scheduler = new Switch2EgressScheduler(0, orderedCapacity: 2);
        OrderedEgressProducerEpoch oldEpoch = scheduler.CurrentProducerEpoch;
        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
            scheduler.Publish(oldEpoch, State(buttons: 1), 10));
        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
            scheduler.Publish(oldEpoch, State(), 11));
        Assert.AreEqual(OrderedEgressPublishDisposition.FaultedOverflow,
            scheduler.Publish(oldEpoch, State(buttons: 2), 12));

        OrderedEgressSchedulerSnapshot fault = scheduler.Snapshot();
        Assert.AreEqual(1L, fault.OverflowFaults);
        Assert.IsTrue(fault.MandatoryNeutralPending);
        Assert.AreEqual(OrderedEgressAgePolicy.CompatibilityNoAgeLimit,
            fault.OrderedAgePolicy);
        Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral,
            Claim(scheduler, 12).Kind);
    }

    [TestMethod]
    public void LifecycleResetFencesOldProducerAndRequiresNeutralResync()
    {
        var scheduler = new Switch2EgressScheduler(0);
        ulong generation = scheduler.PresentationGeneration;
        OrderedEgressProducerEpoch oldEpoch = scheduler.CurrentProducerEpoch;
        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
            scheduler.Publish(oldEpoch, State(buttons: 1), 20));

        Assert.IsTrue(scheduler.BeginLifecycleReset(generation, 21,
            out OrderedEgressProducerEpoch successor));
        Assert.AreEqual(OrderedEgressPublishDisposition.
                RejectedStaleProducerEpoch,
            scheduler.Publish(oldEpoch, State(buttons: 2), 22));

        OrderedEgressClaim<Switch2EgressState> neutral = Claim(scheduler, 22);
        Assert.AreEqual(OrderedEgressClaimKind.MandatoryNeutral, neutral.Kind);
        Assert.AreEqual(Switch2EgressState.Neutral, neutral.State);
        Commit(scheduler, neutral, 22);

        OrderedEgressSchedulerSnapshot afterNeutral = scheduler.Snapshot();
        Assert.AreEqual(1L, afterNeutral.LifecycleResetFaults);
        Assert.AreEqual(1L, afterNeutral.MandatoryNeutralCommits);
        Assert.AreEqual(1L, afterNeutral.StaleProducerRejections);
        Assert.IsTrue(afterNeutral.ResynchronizationRequired);
        Assert.AreEqual(OrderedEgressPublishDisposition.
                AcceptedResynchronization,
            scheduler.Resynchronize(successor, Switch2EgressState.Neutral,
                21));
        Assert.AreEqual(1L, scheduler.Snapshot().ResynchronizationCount);
    }

    [TestMethod]
    public void SuccessiveLifecycleBoundariesEachCommitOneMandatoryNeutral()
    {
        var scheduler = new Switch2EgressScheduler(0);
        ulong generation = scheduler.PresentationGeneration;
        Assert.IsTrue(scheduler.BeginLifecycleReset(generation, 40, out _));
        OrderedEgressClaim<Switch2EgressState> first = Claim(scheduler, 40);
        Assert.IsTrue(scheduler.TryAdmit(first, 40));

        Assert.IsTrue(scheduler.BeginLifecycleReset(generation, 41, out _));
        Assert.IsTrue(scheduler.Complete(first,
            OrderedEgressCompletion.Commit));
        OrderedEgressSchedulerSnapshot between = scheduler.Snapshot();
        Assert.AreEqual(1L, between.MandatoryNeutralCommits);
        Assert.IsTrue(between.MandatoryNeutralPending,
            "The older admitted neutral must not clear its successor.");
        Assert.IsFalse(between.ResynchronizationRequired);

        OrderedEgressClaim<Switch2EgressState> second = Claim(scheduler, 41);
        Assert.AreNotEqual(first.Ordinal, second.Ordinal);
        Commit(scheduler, second, 41);
        OrderedEgressSchedulerSnapshot complete = scheduler.Snapshot();
        Assert.AreEqual(2L, complete.LifecycleResetFaults);
        Assert.AreEqual(2L, complete.MandatoryNeutralCommits);
        Assert.IsFalse(complete.MandatoryNeutralPending);
        Assert.IsTrue(complete.ResynchronizationRequired);
    }

    [TestMethod]
    public void DeferredOrderedClaimRetriesStateAndBytesExactly()
    {
        var scheduler = new Switch2EgressScheduler(1_000_000);
        OrderedEgressProducerEpoch epoch = scheduler.CurrentProducerEpoch;
        Switch2EgressState state = State(buttons: 1, lx: 123,
            gyroYaw: -456);
        Assert.AreEqual(OrderedEgressPublishDisposition.AcceptedOrdered,
            scheduler.Publish(epoch, state, 30));
        OrderedEgressClaim<Switch2EgressState> first = Claim(scheduler, 31);
        byte[] firstWire = Build(first);
        Assert.IsTrue(scheduler.Complete(first,
            OrderedEgressCompletion.Defer));

        OrderedEgressClaim<Switch2EgressState> retry = Claim(scheduler, 32);
        Assert.AreEqual(OrderedEgressClaimKind.Retry, retry.Kind);
        Assert.AreEqual(first.State, retry.State);
        CollectionAssert.AreEqual(firstWire, Build(retry));
    }

    [TestMethod]
    public void SwitchWriterFinalAdmissionUsesTheSameDisconnectFence()
    {
        var scheduler = new Switch2EgressScheduler(1_000_000);
        var gate = new OrderedEgressWriterAdmissionGate();
        const long writerGeneration = 31;
        gate.Activate(writerGeneration,
            unchecked((long)scheduler.PresentationGeneration), 41);
        scheduler.Publish(scheduler.CurrentProducerEpoch,
            State(buttons: 1), 50);
        Assert.IsTrue(gate.TryClaim(writerGeneration, scheduler, 50,
            out OrderedEgressClaim<Switch2EgressState> claim,
            out OrderedEgressWriterAdmissionLease lease,
            includeIdle: false));

        int writes = 0;
        using ManualResetEventSlim beforeFinalAdmission = new(false);
        using ManualResetEventSlim resume = new(false);
        Task<bool> writer = Task.Run(() =>
        {
            beforeFinalAdmission.Set();
            resume.Wait();
            if (!gate.TryAdmit(lease, scheduler, claim, 51))
            {
                scheduler.Complete(claim,
                    OrderedEgressCompletion.Defer);
                return false;
            }
            Interlocked.Increment(ref writes);
            scheduler.Complete(claim, OrderedEgressCompletion.Commit);
            return true;
        });

        Assert.IsTrue(beforeFinalAdmission.Wait(1_000));
        gate.Invalidate();
        resume.Set();
        Assert.IsTrue(writer.Wait(1_000));
        Assert.IsFalse(writer.Result);
        Assert.AreEqual(0, Volatile.Read(ref writes));
        Assert.IsTrue(scheduler.Snapshot().RetryPending);
    }

    [TestMethod]
    public void StrictOutputDecoderPreservesValidRumbleButTranslationFailsClosed()
    {
        var left = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(0x187, 453, 0x112, 453),
            new Switch2HdRumbleSubframe(1, 2, 3, 4), default);
        var right = new Switch2HdRumbleGroup(
            new Switch2HdRumbleSubframe(0x3FF, 0x2AA, 0x155, 1),
            default, new Switch2HdRumbleSubframe(8, 9, 10, 11));
        byte[] wire = new byte[Switch2VirtualOutputState.WireLength];
        Assert.IsTrue(Switch2HdRumbleGroupCodec.TryEncode(7, left,
            wire.AsSpan(0, 16)));
        Assert.IsTrue(Switch2HdRumbleGroupCodec.TryEncode(7, right,
            wire.AsSpan(16, 16)));
        wire[32] = (byte)Switch2VirtualOutputFlags.Rumble;

        Assert.IsTrue(Switch2VirtualOutputState.TryDecode(wire,
            out Switch2VirtualOutputState decoded, out var failure));
        Assert.AreEqual(Switch2VirtualOutputDecodeFailure.None, failure);
        Assert.AreEqual((byte)7, decoded.RumbleCounter);
        Assert.AreEqual(left, decoded.LeftRumble);
        Assert.AreEqual(right, decoded.RightRumble);
        Assert.IsFalse(ViiperOutDevice.
            TryTranslateSwitch2VirtualOutputToLegacyRumble(decoded,
                out byte lightFast, out byte heavySlow));
        Assert.AreEqual((byte)0, lightFast);
        Assert.AreEqual((byte)0, heavySlow);
    }

    [TestMethod]
    public void LedOnlyOutputCannotMasqueradeAsRumbleOrSyntheticStop()
    {
        byte[] wire = new byte[Switch2VirtualOutputState.WireLength];
        wire[32] = (byte)Switch2VirtualOutputFlags.PlayerLed;
        wire[33] = 0x06;

        Assert.IsTrue(Switch2VirtualOutputState.TryDecode(wire,
            out Switch2VirtualOutputState decoded, out _));
        Assert.IsFalse(decoded.HasRumble);
        Assert.IsTrue(decoded.HasPlayerLed);
        Assert.AreEqual((byte)0x06, decoded.PlayerLedMask);
        Assert.IsFalse(ViiperOutDevice.
            TryTranslateSwitch2VirtualOutputToLegacyRumble(decoded,
                out _, out _));

        wire[0] = 0x50;
        Assert.IsFalse(Switch2VirtualOutputState.TryDecode(wire,
            out _, out Switch2VirtualOutputDecodeFailure failure));
        Assert.AreEqual(
            Switch2VirtualOutputDecodeFailure.UnexpectedRumblePayload,
            failure);
    }

    [TestMethod]
    public void OutputDecoderRejectsUnknownFlagsInvalidGroupsAndMismatchedCounters()
    {
        byte[] wire = new byte[Switch2VirtualOutputState.WireLength];
        wire[32] = 0x80;
        Assert.IsFalse(Switch2VirtualOutputState.TryDecode(wire,
            out _, out Switch2VirtualOutputDecodeFailure unknown));
        Assert.AreEqual(Switch2VirtualOutputDecodeFailure.UnknownFlags,
            unknown);

        wire = new byte[Switch2VirtualOutputState.WireLength];
        wire[32] = (byte)Switch2VirtualOutputFlags.Rumble;
        Assert.IsFalse(Switch2VirtualOutputState.TryDecode(wire,
            out _, out Switch2VirtualOutputDecodeFailure invalid));
        Assert.AreEqual(Switch2VirtualOutputDecodeFailure.InvalidRumbleGroup,
            invalid);

        Assert.IsTrue(Switch2HdRumbleGroupCodec.TryEncode(1, default,
            wire.AsSpan(0, 16)));
        Assert.IsTrue(Switch2HdRumbleGroupCodec.TryEncode(2, default,
            wire.AsSpan(16, 16)));
        Assert.IsFalse(Switch2VirtualOutputState.TryDecode(wire,
            out _, out Switch2VirtualOutputDecodeFailure mismatch));
        Assert.AreEqual(Switch2VirtualOutputDecodeFailure.CounterMismatch,
            mismatch);
    }

    [TestMethod]
    public void OrderedAgePolicyRequiresExplicitBoundedMilliseconds()
    {
        Assert.IsTrue(Switch2PresentationPolicy.
            TryParseMaximumOrderedAgeMilliseconds(null, out int unset,
                out string unsetError));
        Assert.AreEqual(0, unset);
        Assert.IsNull(unsetError);
        Assert.IsTrue(Switch2PresentationPolicy.
            TryParseMaximumOrderedAgeMilliseconds("17", out int declared,
                out string declaredError));
        Assert.AreEqual(17, declared);
        Assert.IsNull(declaredError);
        Assert.IsTrue(Switch2PresentationPolicy.ToStopwatchTicks(declared) >
            0);
        Assert.IsFalse(Switch2PresentationPolicy.
            TryParseMaximumOrderedAgeMilliseconds("0", out _, out _));
    }

    private static Switch2EgressState State(uint buttons = 0,
        ushort lx = Switch2EgressState.NeutralAxis, short gyroYaw = 0) =>
        new(buttons, lx, Switch2EgressState.NeutralAxis,
            Switch2EgressState.NeutralAxis,
            Switch2EgressState.NeutralAxis, 0, 0, 0, gyroYaw, 0, 0);

    private static OrderedEgressClaim<Switch2EgressState> Claim(
        Switch2EgressScheduler scheduler, long selectedTimestamp)
    {
        Assert.IsTrue(scheduler.TryClaim(selectedTimestamp, out
            OrderedEgressClaim<Switch2EgressState> claim));
        return claim;
    }

    private static void Commit(Switch2EgressScheduler scheduler,
        in OrderedEgressClaim<Switch2EgressState> claim,
        long admittedTimestamp)
    {
        Assert.IsTrue(scheduler.TryAdmit(claim, admittedTimestamp));
        Assert.IsTrue(scheduler.Complete(claim,
            OrderedEgressCompletion.Commit));
    }

    private static byte[] Build(
        in OrderedEgressClaim<Switch2EgressState> claim)
    {
        byte[] wire = new byte[Switch2EgressState.WireSize];
        claim.BuildInto(wire);
        return wire;
    }
}
