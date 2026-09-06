using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2JoyConJoinedRuntimeRegistrationParticipantTests
{
    private const ulong RuntimeGeneration = 19_001;
    private const ulong PairEpoch = 19_101;
    private const ulong LeftDeviceGeneration = 19_201;
    private const ulong LeftTransportGeneration = 19_301;
    private const ulong RightDeviceGeneration = 19_202;
    private const ulong RightTransportGeneration = 19_302;
    private const long QpcFrequency = 10_000_000;
    private const int TimeoutMilliseconds = 2_000;

    [TestMethod]
    public void SharedCoreRunsOneExactJoinedParticipantLifecycle()
    {
        PairFixture fixture = CreateFixture(2_001);
        Assert.IsTrue(TryCreateOwner(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            out var owner, out var registration, out _));
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(core.TryOpen(51, out var openFailure),
            openFailure.Kind.ToString());
        Switch2JoyConJoinedRuntimeRegistrationParticipant participant = null;

        Assert.IsTrue(core.TryAttach(registration,
            () => participant =
                new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner),
            static (_, _, _) => { }, TimeoutMilliseconds, out var token,
            out var attachFailure), attachFailure.Kind.ToString());
        Assert.IsNotNull(participant);
        Assert.AreEqual(registration, participant.Registration);
        Assert.IsTrue(participant.HasAdoptedSlot);
        Assert.IsFalse(participant.HasPreparedCredential);
        Assert.IsTrue(participant.IsSubscribed);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Active, owner.State);

        Assert.IsTrue(core.TryRemove(token, TimeoutMilliseconds,
            out var removeFailure), removeFailure.Kind.ToString());
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Removed, owner.State);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
        Assert.IsTrue(owner.LeftReleaseProven);
        Assert.IsTrue(owner.RightReleaseProven);
        Assert.AreEqual(1, fixture.Left.ReleaseWaitCount);
        Assert.AreEqual(1, fixture.Right.ReleaseWaitCount);
        Assert.AreEqual(1L, owner.Sink.TerminalScheduleAttemptCount);
    }

    [TestMethod]
    public void ExactRetriesSucceedAndDefaultForeignCrossPairProofsFailClosed()
    {
        PairFixture fixture = CreateFixture(2_002);
        PairFixture otherFixture = CreateFixture(2_003);
        Assert.IsTrue(TryCreateOwner(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            out var owner, out var registration, out _));
        Assert.IsTrue(TryCreateOwner(otherFixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            out var otherOwner, out var otherRegistration, out _));
        var participant =
            new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner);
        var otherParticipant =
            new Switch2JoyConJoinedRuntimeRegistrationParticipant(otherOwner);
        InputControllerRegistrationTable table = OpenTable(61);
        InputControllerRegistrationTable foreignTable = OpenTable(62);
        InputControllerRegistrationTable otherTable = OpenTable(63);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out _));
        Assert.IsTrue(foreignTable.TryReserveAndBind(registration,
            out var foreignToken, out _, out _));
        Assert.IsTrue(otherTable.TryReserveAndBind(otherRegistration,
            out var otherToken, out var otherRollback, out _));

        AssertRejected(participant.TryAdoptBoundSlot(default),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot,
            Switch2RuntimeRegistrationParticipantFailureKind.
                InvalidCredential);
        AssertRejected(participant.TryAdoptBoundSlot(otherToken),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot,
            Switch2RuntimeRegistrationParticipantFailureKind.
                InvalidCredential);
        AssertSuccess(participant.TryAdoptBoundSlot(token),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        AssertSuccess(participant.TryAdoptBoundSlot(token),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        AssertRejected(participant.TryAdoptBoundSlot(foreignToken),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot,
            Switch2RuntimeRegistrationParticipantFailureKind.StaleCredential);

        var callbackState = new CallbackState(table, token);
        Switch2RuntimeRegistrationCallbacks callbacks =
            callbackState.Callbacks;
        AssertSuccess(participant.TrySubscribe(callbacks),
            Switch2RuntimeRegistrationParticipantOperation.Subscribe);
        AssertSuccess(participant.TrySubscribe(callbacks),
            Switch2RuntimeRegistrationParticipantOperation.Subscribe);
        AssertRejected(participant.TrySubscribe(new(
                static (_, _) => { }, callbackState.OnAttention)),
            Switch2RuntimeRegistrationParticipantOperation.Subscribe,
            Switch2RuntimeRegistrationParticipantFailureKind.
                InvalidCredential);
        AssertSuccess(participant.TryPrepareActivation(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation);

        Assert.IsTrue(table.TryBeginActivate(token, out var activation,
            out _));
        InputControllerActivationCommitCredential commit =
            AcquireCommit(table, activation);
        InputControllerActivationCommitCredential copiedCommit = commit;
        Assert.IsTrue(foreignTable.TryBeginActivate(foreignToken,
            out var foreignActivation, out _));
        InputControllerActivationCommitCredential foreignCommit =
            AcquireCommit(foreignTable, foreignActivation);
        AssertRejected(participant.TryCommitPrepared(foreignCommit),
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared,
            Switch2RuntimeRegistrationParticipantFailureKind.
                InvalidCredential);
        Assert.IsTrue(participant.HasPreparedCredential,
            "A foreign table proof is rejected before native pair mutation.");
        AssertSuccess(participant.TryCommitPrepared(commit),
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared);
        AssertRejected(participant.TryCommitPrepared(copiedCommit),
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared,
            Switch2RuntimeRegistrationParticipantFailureKind.AlreadyConsumed);
        Assert.IsTrue(table.TryCompleteActivate(commit, true, out _));

        Assert.IsTrue(table.TryBeginRetire(token, out var retirement, out _));
        callbackState.Retirement = retirement;
        var foreignClaim = new InputControllerRetirementClaim(foreignToken);
        AssertRejected(participant.TryArmRetirement(foreignClaim),
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement,
            Switch2RuntimeRegistrationParticipantFailureKind.StaleCredential);
        AssertSuccess(participant.TryArmRetirement(retirement),
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement);
        AssertSuccess(participant.TryArmRetirement(retirement),
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement);
        AssertSuccess(participant.TryWaitForPublicationAvailability(
                TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability);
        AssertSuccess(participant.TryStopAndQuiesce(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce);
        Assert.IsTrue(table.TryWaitForDrain(retirement, 0, out _));
        Assert.IsTrue(table.TryMarkQuiesced(retirement, out _));
        AssertSuccess(participant.TryUnsubscribe(),
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        AssertSuccess(participant.TryUnsubscribe(),
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        AssertSuccess(participant.TryRemove(),
            Switch2RuntimeRegistrationParticipantOperation.Remove);
        AssertSuccess(participant.TryRemove(),
            Switch2RuntimeRegistrationParticipantOperation.Remove);
        Assert.IsTrue(table.TryCompleteRemoval(retirement, out _));
        Assert.AreEqual(1, callbackState.TerminalCount);

        AssertSuccess(otherParticipant.TryAdoptBoundSlot(otherToken),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        AssertSuccess(otherParticipant.TryAbortUnpublished(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.AbortUnpublished);
        Assert.IsTrue(otherTable.TryRollback(otherRollback, out _));
    }

    [TestMethod]
    public void CopiedParticipantCannotDuplicatePrepareOrConsumePairCredential()
    {
        PairFixture fixture = CreateFixture(2_004);
        Assert.IsTrue(TryCreateOwner(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            out var owner, out var registration, out _));
        var first =
            new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner);
        var copied =
            new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner);
        InputControllerRegistrationTable table = OpenTable(71);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        AssertSuccess(first.TryAdoptBoundSlot(token),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        AssertSuccess(copied.TryAdoptBoundSlot(token),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        var firstState = new CallbackState(table, token);
        var copiedState = new CallbackState(table, token);
        AssertSuccess(first.TrySubscribe(firstState.Callbacks),
            Switch2RuntimeRegistrationParticipantOperation.Subscribe);
        AssertSuccess(copied.TrySubscribe(copiedState.Callbacks),
            Switch2RuntimeRegistrationParticipantOperation.Subscribe);
        AssertSuccess(first.TryPrepareActivation(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation);

        Switch2RuntimeRegistrationParticipantResult copiedPrepare =
            copied.TryPrepareActivation(TimeoutMilliseconds);
        AssertRejected(copiedPrepare,
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidState);
        Assert.IsFalse(copied.HasPreparedCredential);
        Assert.IsTrue(first.HasPreparedCredential);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Prepared,
            owner.State);

        AssertSuccess(first.TryAbortPrepared(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.AbortPrepared);
        AssertSuccess(first.TryUnsubscribe(),
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        AssertSuccess(copied.TryUnsubscribe(),
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        Assert.IsTrue(table.TryRollback(rollback, out _));
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.AbortedUnpublished,
            owner.State);
    }

    [TestMethod]
    public void ProvenPrepareFailureLetsCoreAbortAndRollbackWithoutQuarantine()
    {
        PairFixture fixture = CreateFixture(2_005);
        var rightPump = new TestPump { StartResult = false };
        Assert.IsTrue(TryCreateOwner(fixture,
            new SequentialPumpFactory(new TestPump(), rightPump),
            out var owner, out var registration, out _));
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(core.TryOpen(81, out _));

        Assert.IsFalse(core.TryAttach(registration,
            () => new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner),
            static (_, _, _) => { }, TimeoutMilliseconds, out var token,
            out var failure));
        Assert.AreEqual(Switch2RuntimeRegistrationTransactionFailureKind.
            PrepareRejected, failure.Kind);
        Assert.IsFalse(failure.RequiresQuarantine);
        Assert.AreEqual(InputControllerSlotState.Removed,
            table.GetSnapshot()[token.Slot].State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(1, fixture.Left.ReleaseWaitCount);
        Assert.AreEqual(1, fixture.Right.ReleaseWaitCount);
    }

    [TestMethod]
    public void ReleaseUncertaintyMapsToWholeSlotQuarantine()
    {
        PairFixture fixture = CreateFixture(2_006);
        Assert.IsTrue(TryCreateOwner(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            out var owner, out var registration, out _));
        var table = new InputControllerRegistrationTable(1);
        var core = new Switch2RuntimeRegistrationTransactionCore(table);
        Assert.IsTrue(core.TryOpen(91, out _));
        Assert.IsTrue(core.TryAttach(registration,
            () => new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner),
            static (_, _, _) => { }, TimeoutMilliseconds, out var token,
            out var attachFailure), attachFailure.Kind.ToString());
        fixture.Right.ReleaseResult =
            Switch2BluetoothInputLeaseReleaseResult.TimedOut;

        Assert.IsFalse(core.TryRemove(token, TimeoutMilliseconds,
            out var removeFailure));
        Assert.IsTrue(removeFailure.RequiresQuarantine);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            table.GetSnapshot()[token.Slot].State);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Quarantined,
            owner.State);
        Assert.AreEqual(1, fixture.Left.ReleaseWaitCount);
        Assert.AreEqual(1, fixture.Right.ReleaseWaitCount);
        Assert.AreEqual(Switch2BluetoothRuntimeTerminalState.Delivered,
            owner.Sink.TerminalState);
    }

    [TestMethod]
    public void ThrowingAttentionObserverCannotUnwindOwnerCallback()
    {
        PairFixture fixture = CreateFixture(2_007);
        var leftPump = new TestPump();
        var rightPump = new TestPump();
        Assert.IsTrue(TryCreateOwner(fixture,
            new SequentialPumpFactory(leftPump, rightPump), out var owner,
            out var registration, out _));
        var participant =
            new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner);
        InputControllerRegistrationTable table = OpenTable(101);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out _));
        AssertSuccess(participant.TryAdoptBoundSlot(token),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        var callbackState = new CallbackState(table, token)
        {
            ThrowAttention = true,
        };
        callbackState.AttentionAction = () =>
            Assert.IsTrue(participant.IsSubscribed,
                "The adapter gate must not be held across its callback.");
        AssertSuccess(participant.TrySubscribe(callbackState.Callbacks),
            Switch2RuntimeRegistrationParticipantOperation.Subscribe);
        AssertSuccess(participant.TryPrepareActivation(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation);
        Assert.IsTrue(table.TryBeginActivate(token, out var activation,
            out _));
        InputControllerActivationCommitCredential commit =
            AcquireCommit(table, activation);
        AssertSuccess(participant.TryCommitPrepared(commit),
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared);
        Assert.IsTrue(table.TryCompleteActivate(commit, true, out _));

        leftPump.RaiseAttention(new Switch2BluetoothInputDrainPumpAttention(
            Switch2BluetoothInputDrainPumpAttentionKind.OwnerRetired,
            LeftDeviceGeneration, LeftTransportGeneration,
            Switch2BluetoothInputEndReason.Disconnected, default));
        Assert.AreEqual(1, callbackState.AttentionCount);
        Assert.AreEqual(Switch2RuntimeRegistrationLifecycleAttentionKind.
            TransportEnded, callbackState.LastAttention.Kind);
        Assert.AreEqual(registration,
            callbackState.LastAttention.Registration);

        Assert.IsTrue(table.TryBeginRetire(token, out var retirement, out _));
        callbackState.Retirement = retirement;
        AssertSuccess(participant.TryArmRetirement(retirement),
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement);
        AssertSuccess(participant.TryStopAndQuiesce(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce);
        Assert.IsTrue(table.TryWaitForDrain(retirement, 0, out _));
        Assert.IsTrue(table.TryMarkQuiesced(retirement, out _));
        AssertSuccess(participant.TryUnsubscribe(),
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        AssertSuccess(participant.TryRemove(),
            Switch2RuntimeRegistrationParticipantOperation.Remove);
        Assert.IsTrue(table.TryCompleteRemoval(retirement, out _));
    }

    [TestMethod]
    public void ThrowAfterReportAddIsOperationTaggedUncertainAndCompensated()
    {
        PairFixture fixture = CreateFixture(2_070);
        Assert.IsTrue(TryCreateOwner(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            out var owner, out var registration, out _));
        var boundary = new ThrowAfterReportAddSubscriptions();
        var participant =
            new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner,
                boundary);
        boundary.Reenter = () =>
            Assert.IsTrue(participant.HasAdoptedSlot,
                "Subscription accessors run without the adapter gate held.");
        InputControllerRegistrationTable table = OpenTable(107);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        AssertSuccess(participant.TryAdoptBoundSlot(token),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        var state = new CallbackState(table, token);

        Switch2RuntimeRegistrationParticipantResult result =
            participant.TrySubscribe(state.Callbacks);
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantOperation.
            Subscribe, result.Operation);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantOutcome.
            OutcomeUncertain, result.Outcome);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantFailureKind.
            SubscriptionRejected, result.FailureKind);
        Assert.IsTrue(result.RequiresQuarantine);
        Assert.AreEqual(1, boundary.AddReportCount);
        Assert.AreEqual(1, boundary.RemoveReportCount);
        Assert.AreEqual(1, boundary.RemoveAttentionCount,
            "Both exact delegates are compensated after an accessor throw.");
        AssertSuccess(participant.TryAbortUnpublished(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.AbortUnpublished);
        Assert.IsTrue(table.TryQuarantine(rollback,
            InputControllerSlotQuarantineReason.ExternalLifecycleFailure,
            out _));
    }

    [TestMethod]
    public void EveryDefaultAndInvalidRequestKeepsItsExactOperationTag()
    {
        PairFixture fixture = CreateFixture(2_008);
        Assert.IsTrue(TryCreateOwner(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            out var owner, out var registration, out _));
        var participant =
            new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner);
        AssertRejected(participant.TryAdoptBoundSlot(default),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot,
            Switch2RuntimeRegistrationParticipantFailureKind.
                InvalidCredential);
        AssertRejected(participant.TrySubscribe(default),
            Switch2RuntimeRegistrationParticipantOperation.Subscribe,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidArgument);
        AssertRejected(participant.TryPrepareActivation(0),
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout);
        AssertRejected(participant.TryCommitPrepared(default),
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidState);
        AssertRejected(participant.TryAbortPrepared(-1),
            Switch2RuntimeRegistrationParticipantOperation.AbortPrepared,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout);
        AssertRejected(participant.TryAbortUnpublished(-1),
            Switch2RuntimeRegistrationParticipantOperation.AbortUnpublished,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout);
        AssertRejected(participant.TryArmRetirement(default),
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement,
            Switch2RuntimeRegistrationParticipantFailureKind.
                InvalidCredential);
        AssertRejected(participant.TryWaitForPublicationAvailability(-1),
            Switch2RuntimeRegistrationParticipantOperation.
                WaitForPublicationAvailability,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout);
        AssertRejected(participant.TryStopAndQuiesce(-1),
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidTimeout);
        AssertSuccess(participant.TryUnsubscribe(),
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        AssertRejected(participant.TryRemove(),
            Switch2RuntimeRegistrationParticipantOperation.Remove,
            Switch2RuntimeRegistrationParticipantFailureKind.InvalidState);

        InputControllerRegistrationTable table = OpenTable(111);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        AssertSuccess(participant.TryAdoptBoundSlot(token),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        AssertSuccess(participant.TryAbortUnpublished(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.AbortUnpublished);
        Assert.IsTrue(table.TryRollback(rollback, out _));
    }

    [TestMethod]
    public void WarmTwentyThousandReportsStayOnZeroAllocationDirectCallback()
    {
        PairFixture fixture = CreateFixture(2_009);
        Assert.IsTrue(TryCreateOwner(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            out var owner, out var registration, out _));
        var participant =
            new Switch2JoyConJoinedRuntimeRegistrationParticipant(owner);
        InputControllerRegistrationTable table = OpenTable(121);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out _));
        AssertSuccess(participant.TryAdoptBoundSlot(token),
            Switch2RuntimeRegistrationParticipantOperation.AdoptBoundSlot);
        var callbackState = new CallbackState(table, token);
        AssertSuccess(participant.TrySubscribe(callbackState.Callbacks),
            Switch2RuntimeRegistrationParticipantOperation.Subscribe);
        AssertSuccess(participant.TryPrepareActivation(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.PrepareActivation);
        Assert.IsTrue(table.TryBeginActivate(token, out var activation,
            out _));
        InputControllerActivationCommitCredential commit =
            AcquireCommit(table, activation);
        AssertSuccess(participant.TryCommitPrepared(commit),
            Switch2RuntimeRegistrationParticipantOperation.CommitPrepared);
        Assert.IsTrue(table.TryCompleteActivate(commit, true, out _));

        Switch2InputSessionDescriptor left = owner.LeftInputOwner.Descriptor;
        Switch2InputSessionDescriptor right = owner.RightInputOwner.Descriptor;
        Switch2InputSession leftSession = Session(left);
        Switch2InputSession rightSession = Session(right);
        byte[] leftBody = Body();
        byte[] rightBody = Body();
        uint leftCounter = 0;
        uint rightCounter = 0;
        long timestamp = 0;
        bool valid = true;
        for (int index = 0; index < 2_000; index++)
        {
            PublishOne(owner.Sink, left, right, leftSession, rightSession,
                leftBody, rightBody, ref leftCounter, ref rightCounter,
                ref timestamp, index, ref valid);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            PublishOne(owner.Sink, left, right, leftSession, rightSession,
                leftBody, rightBody, ref leftCounter, ref rightCounter,
                ref timestamp, index, ref valid);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(valid);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(21_999, callbackState.RegularCount);

        Assert.IsTrue(table.TryBeginRetire(token, out var retirement, out _));
        callbackState.Retirement = retirement;
        AssertSuccess(participant.TryArmRetirement(retirement),
            Switch2RuntimeRegistrationParticipantOperation.ArmRetirement);
        AssertSuccess(participant.TryStopAndQuiesce(TimeoutMilliseconds),
            Switch2RuntimeRegistrationParticipantOperation.StopAndQuiesce);
        Assert.IsTrue(table.TryWaitForDrain(retirement, 0, out _));
        Assert.IsTrue(table.TryMarkQuiesced(retirement, out _));
        AssertSuccess(participant.TryUnsubscribe(),
            Switch2RuntimeRegistrationParticipantOperation.Unsubscribe);
        AssertSuccess(participant.TryRemove(),
            Switch2RuntimeRegistrationParticipantOperation.Remove);
        Assert.IsTrue(table.TryCompleteRemoval(retirement, out _));
    }

    private static void PublishOne(Switch2JoyConJoinedRuntimeInputSink sink,
        in Switch2InputSessionDescriptor left,
        in Switch2InputSessionDescriptor right,
        Switch2InputSession leftSession, Switch2InputSession rightSession,
        byte[] leftBody, byte[] rightBody, ref uint leftCounter,
        ref uint rightCounter, ref long timestamp, int index, ref bool valid)
    {
        bool useLeft = (index & 1) == 0;
        ref uint counter = ref useLeft ? ref leftCounter : ref rightCounter;
        counter++;
        byte[] body = useLeft ? leftBody : rightBody;
        BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
        Switch2InputSession session = useLeft ? leftSession : rightSession;
        Switch2InputSessionDescriptor descriptor = useLeft ? left : right;
        valid &= session.TryProcess(descriptor, body, ++timestamp,
            out var frame, out _);
        sink.PublishJoyCon(frame);
    }

    private static Switch2InputSession Session(
        in Switch2InputSessionDescriptor descriptor)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            descriptor.Identity.Model, descriptor.DeviceGeneration,
            out var calibration));
        return new Switch2InputSession(descriptor, calibration);
    }

    private static byte[] Body()
    {
        var body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
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

    private static InputControllerActivationCommitCredential AcquireCommit(
        InputControllerRegistrationTable table,
        in InputControllerActivationClaim claim)
    {
        Assert.IsTrue(table.TryAcquireActivationCommit(claim,
            out var commit, out var failure), failure.ToString());
        return commit;
    }

    private static void AssertSuccess(
        in Switch2RuntimeRegistrationParticipantResult result,
        Switch2RuntimeRegistrationParticipantOperation operation)
    {
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(operation, result.Operation);
        Assert.AreEqual(Switch2RuntimeRegistrationParticipantOutcome.Succeeded,
            result.Outcome);
        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.RequiresQuarantine);
    }

    private static void AssertRejected(
        in Switch2RuntimeRegistrationParticipantResult result,
        Switch2RuntimeRegistrationParticipantOperation operation,
        Switch2RuntimeRegistrationParticipantFailureKind failure)
    {
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(operation, result.Operation);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(failure, result.FailureKind);
    }

    private static bool TryCreateOwner(PairFixture fixture,
        ISwitch2BluetoothRuntimeDrainPumpFactory pumpFactory,
        out Switch2JoyConJoinedRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2JoyConJoinedRuntimeCreateFailure failure)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Left, LeftDeviceGeneration,
            out var leftCalibration));
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            Switch2ControllerModel.JoyCon2Right, RightDeviceGeneration,
            out var rightCalibration));
        return Switch2JoyConJoinedRuntimeOwner.TryCreateCore(fixture.Admission,
            fixture.Left, fixture.Right, RuntimeGeneration, PairEpoch,
            LeftDeviceGeneration, LeftTransportGeneration, leftCalibration,
            8, RightDeviceGeneration, RightTransportGeneration,
            rightCalibration, 8, QpcFrequency,
            new Switch2JoyConPairPolicy(1_000), TimeoutMilliseconds,
            pumpFactory, Switch2RuntimeTerminalScheduler.Instance, out owner,
            out registration, out failure);
    }

    private static PairFixture CreateFixture(ulong scanGeneration)
    {
        Switch2BluetoothConnectionAdmission left = Admission(
            Switch2ControllerModel.JoyCon2Left, scanGeneration);
        Switch2BluetoothConnectionAdmission right = Admission(
            Switch2ControllerModel.JoyCon2Right, scanGeneration);
        byte[] key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        byte[] leftIdentity = Enumerable.Range(30, 16).
            Select(x => (byte)x).ToArray();
        byte[] rightIdentity = Enumerable.Range(90, 16).
            Select(x => (byte)x).ToArray();
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, leftIdentity,
            Switch2ControllerModel.JoyCon2Left,
            Switch2AdvertisementCodec.JoyCon2LeftProductId,
            out var leftPeer));
        Assert.IsTrue(Switch2PersistentPeerId.TryDerive(key, rightIdentity,
            Switch2ControllerModel.JoyCon2Right,
            Switch2AdvertisementCodec.JoyCon2RightProductId,
            out var rightPeer));
        byte[] pairBytes = new byte[Switch2JoyConPairId.EncodedLength];
        BitConverter.TryWriteBytes(pairBytes, scanGeneration);
        Assert.IsTrue(Switch2JoyConPairId.TryRead(pairBytes, out var pairId));
        Assert.IsTrue(Switch2JoyConPairRecord.TryCreate(scanGeneration,
            pairId, leftPeer, rightPeer, out var record));
        Assert.IsTrue(Switch2JoyConPairConnectionAdmission.TryCreate(record,
            leftPeer, left, rightPeer, right, out var admission));
        return new PairFixture(admission,
            new FakeLease(left, ExactGatt(scanGeneration)),
            new FakeLease(right, ExactGatt(scanGeneration)));
    }

    private static Switch2BluetoothConnectionAdmission Admission(
        Switch2ControllerModel model, ulong scanGeneration) => new(
            scanGeneration, model,
            model == Switch2ControllerModel.JoyCon2Left ?
                Switch2AdvertisementCodec.JoyCon2LeftProductId :
                Switch2AdvertisementCodec.JoyCon2RightProductId);

    private static Switch2BluetoothGattSnapshot ExactGatt(ulong scan) => new(
        scan, 1, 1, Switch2InputCodec.ServiceUuid,
        Switch2InputCodec.Common05CharacteristicUuid,
        Switch2GattProperty.Read | Switch2GattProperty.Notify);

    private static InputControllerRegistrationTable OpenTable(
        ulong generation)
    {
        var table = new InputControllerRegistrationTable(1);
        Assert.IsTrue(table.TryOpen(generation, out var failure),
            failure.ToString());
        return table;
    }

    private sealed class CallbackState
    {
        private readonly InputControllerRegistrationTable table;
        private readonly InputControllerSlotToken token;

        internal CallbackState(InputControllerRegistrationTable table,
            in InputControllerSlotToken token)
        {
            this.table = table;
            this.token = token;
            Callbacks = new Switch2RuntimeRegistrationCallbacks(OnReport,
                OnAttention);
        }

        internal Switch2RuntimeRegistrationCallbacks Callbacks { get; }
        internal InputControllerRetirementClaim Retirement { get; set; }
        internal int RegularCount { get; private set; }
        internal int TerminalCount { get; private set; }
        internal int AttentionCount { get; private set; }
        internal bool ThrowAttention { get; set; }
        internal Action AttentionAction { get; set; }
        internal Switch2RuntimeRegistrationLifecycleAttention LastAttention
        { get; private set; }

        internal void OnReport(DS4Device sender, EventArgs args)
        {
            Switch2RuntimeReportEventArgs report =
                (Switch2RuntimeReportEventArgs)args;
            if (report.Kind == Switch2RuntimeReportKind.Regular)
            {
                Assert.IsTrue(table.TryAcquireReportLease(token, sender,
                    out var lease, out _));
                lease.Dispose();
                RegularCount++;
                return;
            }
            Assert.IsTrue(table.TryAcquireTerminalReportLease(Retirement,
                sender, out var terminal, out _));
            Assert.IsTrue(terminal.TryAcknowledgeTerminalNeutral(out _));
            terminal.Dispose();
            TerminalCount++;
        }

        internal void OnAttention(
            in Switch2RuntimeRegistrationLifecycleAttention attention)
        {
            LastAttention = attention;
            AttentionCount++;
            AttentionAction?.Invoke();
            if (ThrowAttention)
            {
                throw new InvalidOperationException(
                    "Synthetic attention observer failure.");
            }
        }
    }

    private sealed class PairFixture
    {
        internal PairFixture(
            in Switch2JoyConPairConnectionAdmission admission,
            FakeLease left, FakeLease right)
        {
            Admission = admission;
            Left = left;
            Right = right;
        }

        internal Switch2JoyConPairConnectionAdmission Admission { get; }
        internal FakeLease Left { get; }
        internal FakeLease Right { get; }
    }

    private sealed class FakeLease : ISwitch2BluetoothInputLease,
        ISwitch2BluetoothInputLeaseReleaseProof
    {
        internal FakeLease(in Switch2BluetoothConnectionAdmission admission,
            in Switch2BluetoothGattSnapshot gatt)
        {
            Admission = admission;
            GattSnapshot = gatt;
        }

        public Switch2BluetoothConnectionAdmission Admission { get; }
        public Switch2BluetoothGattSnapshot GattSnapshot { get; }
        internal Switch2BluetoothInputLeaseReleaseResult ReleaseResult
        { get; set; } = Switch2BluetoothInputLeaseReleaseResult.Released;
        internal int ReleaseWaitCount { get; private set; }

        public bool TrySubscribeCccdNotify(ulong transportGeneration,
            Switch2BluetoothInputNotification notification,
            Switch2BluetoothInputDisconnected disconnected) => true;

        public bool TryUnsubscribeCccdNone(ulong transportGeneration) => true;

        public Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
            ulong transportGeneration, int timeoutMilliseconds)
        {
            ReleaseWaitCount++;
            return ReleaseResult;
        }
    }

    private sealed class TestPump : ISwitch2BluetoothRuntimeDrainPump
    {
        private Action<Switch2BluetoothInputDrainPumpAttention> attention;
        private bool currentWorker;
        internal bool StartResult { get; set; } = true;
        public Switch2BluetoothInputDrainPumpState State { get; private set; } =
            Switch2BluetoothInputDrainPumpState.Created;
        public Switch2BluetoothInputDrainPumpFailure TerminalFailure => default;
        public bool RequiresQuarantine => false;
        public bool IsCurrentWorkerThread => currentWorker;
        public long PublishedCount => 0;

        public bool TrySetLifecycleAttentionHandler(
            Action<Switch2BluetoothInputDrainPumpAttention> handler)
        {
            attention = handler;
            return true;
        }

        public bool TryStartParked(int timeoutMilliseconds,
            out Switch2BluetoothInputDrainPumpFailure failure)
        {
            State = StartResult ? Switch2BluetoothInputDrainPumpState.Parked :
                Switch2BluetoothInputDrainPumpState.StopRequested;
            failure = StartResult ? default :
                Switch2BluetoothInputDrainPumpFailure.OwnerRejected;
            return StartResult;
        }

        public bool TryStopAndJoin(int timeoutMilliseconds,
            out Switch2BluetoothInputDrainPumpFailure failure)
        {
            State = Switch2BluetoothInputDrainPumpState.Stopped;
            failure = default;
            return true;
        }

        internal void RaiseAttention(
            Switch2BluetoothInputDrainPumpAttention evidence)
        {
            currentWorker = true;
            try { attention?.Invoke(evidence); }
            finally { currentWorker = false; }
        }
    }

    private sealed class SequentialPumpFactory :
        ISwitch2BluetoothRuntimeDrainPumpFactory
    {
        private readonly ISwitch2BluetoothRuntimeDrainPump[] pumps;
        private int index;

        internal SequentialPumpFactory(
            params ISwitch2BluetoothRuntimeDrainPump[] pumps) =>
            this.pumps = pumps;

        public bool TryCreate(Switch2BluetoothInputOwner inputOwner,
            out ISwitch2BluetoothRuntimeDrainPump pump,
            out Switch2BluetoothInputDrainPumpFailure failure)
        {
            pump = pumps[index++];
            failure = default;
            return true;
        }
    }

    private sealed class ThrowAfterReportAddSubscriptions :
        ISwitch2JoyConJoinedRuntimeParticipantSubscriptions
    {
        internal Action Reenter { get; set; }
        internal int AddReportCount { get; private set; }
        internal int RemoveReportCount { get; private set; }
        internal int RemoveAttentionCount { get; private set; }

        public void AddReport(Switch2JoyConJoinedRuntimeOwner owner,
            DS4Device.ReportHandler<EventArgs> handler)
        {
            AddReportCount++;
            Reenter?.Invoke();
            owner.RuntimeDevice.Report += handler;
            throw new InvalidOperationException(
                "Synthetic post-add event accessor failure.");
        }

        public void RemoveReport(Switch2JoyConJoinedRuntimeOwner owner,
            DS4Device.ReportHandler<EventArgs> handler)
        {
            RemoveReportCount++;
            owner.RuntimeDevice.Report -= handler;
        }

        public void AddAttention(Switch2JoyConJoinedRuntimeOwner owner,
            EventHandler<
                Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
                handler) => owner.LifecycleAttention += handler;

        public void RemoveAttention(Switch2JoyConJoinedRuntimeOwner owner,
            EventHandler<
                Switch2JoyConJoinedRuntimeLifecycleAttentionEventArgs>
                handler)
        {
            RemoveAttentionCount++;
            owner.LifecycleAttention -= handler;
        }
    }
}
