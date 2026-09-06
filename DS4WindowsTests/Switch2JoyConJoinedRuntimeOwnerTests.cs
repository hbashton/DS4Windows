using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2JoyConJoinedRuntimeOwnerTests
{
    private const ulong RuntimeGeneration = 9_001;
    private const ulong PairEpoch = 9_101;
    private const ulong LeftDeviceGeneration = 9_201;
    private const ulong LeftTransportGeneration = 9_301;
    private const ulong RightDeviceGeneration = 9_202;
    private const ulong RightTransportGeneration = 9_302;
    private const long QpcFrequency = 10_000_000;
    private const int TimeoutMilliseconds = 2_000;

    [TestMethod]
    public void FullLifecycleOwnsOneLogicalSlotAndProvesBothPhysicalReleases()
    {
        PairFixture fixture = CreateFixture(1_001);
        Assert.IsTrue(TryCreate(fixture,
            Switch2BluetoothRuntimeDrainPumpFactory.Instance,
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out var createFailure),
            createFailure.Kind.ToString());

        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        Assert.AreEqual(RuntimeGeneration, registration.Generation);
        Assert.AreSame(owner.RuntimeDevice, registration.Device);
        Assert.AreSame(owner, registration.Owner);
        Assert.AreEqual(InputControllerOwnershipKind.Switch2Runtime,
            registration.OwnershipKind);
        Assert.IsFalse(registration.HasHidInterface);
        Assert.IsFalse(registration.HasPersistentIdentity);
        Assert.IsTrue(owner.RuntimeDevice.HasExactJoinedBluetoothBinding(
            PairEpoch, LeftDeviceGeneration, LeftTransportGeneration,
            RightDeviceGeneration, RightTransportGeneration));

        int terminalReports = 0;
        InputControllerRetirementClaim retirement = default;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                    Switch2RuntimeReportKind.TerminalNeutral)
            {
                return;
            }
            Interlocked.Increment(ref terminalReports);
            if (table.TryAcquireTerminalReportLease(retirement,
                    (DS4Device)sender, out var lease, out _))
            {
                lease.TryAcknowledgeTerminalNeutral(out _);
                lease.Dispose();
            }
        };

        Assert.IsTrue(table.TryBeginRetire(token, out retirement,
            out var tableFailure), tableFailure.ToString());
        Assert.IsTrue(owner.TryArmRetirement(retirement,
            out var armFailure), armFailure.ToString());
        Assert.IsTrue(registration.TryStopAndQuiesce(TimeoutMilliseconds,
            out var stopFailure),
            $"{stopFailure}: {owner.LastStopFailure.Kind}");

        Assert.AreEqual(1, Volatile.Read(ref terminalReports));
        Assert.IsTrue(owner.LeftReleaseProven);
        Assert.IsTrue(owner.RightReleaseProven);
        Assert.AreEqual(1, fixture.Left.UnsubscribeCount);
        Assert.AreEqual(1, fixture.Right.UnsubscribeCount);
        Assert.AreEqual(1, fixture.Left.ReleaseWaitCount);
        Assert.AreEqual(1, fixture.Right.ReleaseWaitCount);
        Assert.AreEqual(Switch2BluetoothRuntimeTerminalState.Delivered,
            owner.Sink.TerminalState);
        Assert.AreEqual(1L, owner.Sink.TerminalScheduleAttemptCount);

        Assert.IsTrue(table.TryWaitForDrain(retirement, 0,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(table.TryMarkQuiesced(retirement, out tableFailure),
            tableFailure.ToString());
        Assert.IsTrue(registration.TryRemove(out var removeFailure),
            removeFailure.ToString());
        Assert.IsTrue(table.TryCompleteRemoval(retirement, out tableFailure),
            tableFailure.ToString());
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Removed,
            owner.State);
    }

    [TestMethod]
    public void PrivateFencesRejectForeignSlotsOwnersAndMixedTableCommitProofs()
    {
        PairFixture firstFixture = CreateFixture(1_002);
        PairFixture secondFixture = CreateFixture(1_003);
        Assert.IsTrue(TryCreate(firstFixture, new SequentialPumpFactory(
                new TestPump(), new TestPump()),
            Switch2RuntimeTerminalScheduler.Instance, out var first,
            out var firstRegistration, out _));
        Assert.IsTrue(TryCreate(secondFixture, new SequentialPumpFactory(
                new TestPump(), new TestPump()),
            Switch2RuntimeTerminalScheduler.Instance, out var second,
            out var secondRegistration, out _));
        InputControllerRegistrationTable firstTable = OpenTable(1, 11);
        InputControllerRegistrationTable secondTable = OpenTable(1, 12);
        Assert.IsTrue(firstTable.TryReserveAndBind(firstRegistration,
            out var firstToken, out _, out _));
        Assert.IsTrue(secondTable.TryReserveAndBind(secondRegistration,
            out var secondToken, out _, out _));
        Assert.IsTrue(first.TryAdoptBoundSlot(firstToken,
            out var firstAdoption, out var adoptionFailure),
            adoptionFailure.ToString());
        Assert.IsTrue(second.TryAdoptBoundSlot(secondToken,
            out var secondAdoption, out adoptionFailure),
            adoptionFailure.ToString());

        Assert.IsFalse(first.TryAdoptBoundSlot(secondToken, out _,
            out var foreignSlotFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeSlotAdoptionFailure.
            InvalidToken, foreignSlotFailure);
        Assert.IsFalse(first.TryPrepareActivation(secondAdoption,
            TimeoutMilliseconds, out _, out var crossOwnerFailure));
        Assert.AreEqual(Switch2BluetoothRuntimePrepareFailure.
            InvalidSlotAdoptionCredential, crossOwnerFailure);
        Assert.IsFalse(first.TryPrepareActivation(default,
            TimeoutMilliseconds, out _, out var forgedFailure));
        Assert.AreEqual(Switch2BluetoothRuntimePrepareFailure.
            InvalidSlotAdoptionCredential, forgedFailure);

        Assert.IsTrue(first.TryPrepareActivation(firstAdoption,
            TimeoutMilliseconds, out var firstPrepared,
            out var firstPrepareFailure), firstPrepareFailure.ToString());
        Assert.IsTrue(second.TryPrepareActivation(secondAdoption,
            TimeoutMilliseconds, out var secondPrepared,
            out var secondPrepareFailure), secondPrepareFailure.ToString());
        Assert.IsTrue(firstTable.TryBeginActivate(firstToken,
            out var firstActivation, out _));
        Assert.IsTrue(secondTable.TryBeginActivate(secondToken,
            out var secondActivation, out _));
        InputControllerActivationCommitCredential firstCommit =
            AcquireActivationCommit(firstTable, firstActivation);
        InputControllerActivationCommitCredential secondCommit =
            AcquireActivationCommit(secondTable, secondActivation);

        Assert.IsFalse(first.TryCommitPrepared(firstPrepared, secondCommit,
            out var mixedFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeCommitFailure.
            InvalidActivationCommitCredential, mixedFailure);
        Assert.IsFalse(first.TryCommitPrepared(secondPrepared, firstCommit,
            out var crossPreparedFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeCommitFailure.InvalidCredential,
            crossPreparedFailure);

        Assert.IsTrue(first.TryAbortPrepared(firstPrepared,
            TimeoutMilliseconds, out var firstAbort), firstAbort.ToString());
        Assert.IsTrue(second.TryAbortPrepared(secondPrepared,
            TimeoutMilliseconds, out var secondAbort), secondAbort.ToString());
        Assert.IsFalse(firstTable.TryCompleteActivate(firstCommit, false,
            out var firstTableFailure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            ActivationCommitRejected, firstTableFailure);
        Assert.IsFalse(secondTable.TryCompleteActivate(secondCommit, false,
            out var secondTableFailure));
        Assert.AreEqual(InputControllerSlotTableFailure.
            ActivationCommitRejected, secondTableFailure);
    }

    [TestMethod]
    public void RightPumpStartFailureRollsBackBothHalvesAndJoinedRuntime()
    {
        PairFixture fixture = CreateFixture(1_004);
        var leftPump = new TestPump();
        var rightPump = new TestPump { StartResult = false };
        Assert.IsTrue(TryCreate(fixture,
            new SequentialPumpFactory(leftPump, rightPump),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out var createFailure),
            createFailure.Kind.ToString());
        InputControllerRegistrationTable table = OpenTable(1);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        Assert.IsTrue(owner.TryAdoptBoundSlot(token, out var adoption,
            out _));

        Assert.IsFalse(owner.TryPrepareActivation(adoption,
            TimeoutMilliseconds, out _, out var prepareFailure));
        Assert.AreEqual(Switch2BluetoothRuntimePrepareFailure.
            PumpStartRejected, prepareFailure);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.AbortedUnpublished,
            owner.RuntimeDevice.RuntimeState);
        Assert.AreEqual(1, leftPump.StartCount);
        Assert.AreEqual(1, rightPump.StartCount);
        Assert.AreEqual(1, leftPump.StopCount);
        Assert.AreEqual(1, rightPump.StopCount,
            "The non-starting half still receives an independent cleanup attempt.");
        Assert.AreEqual(1, fixture.Left.UnsubscribeCount);
        Assert.AreEqual(1, fixture.Right.UnsubscribeCount);
        Assert.IsTrue(owner.LeftReleaseProven);
        Assert.IsTrue(owner.RightReleaseProven);
        Assert.IsFalse(owner.Sink.TerminalRequested,
            "Unpublished rollback cannot manufacture terminal output.");
        Assert.IsTrue(table.TryRollback(rollback, out var tableFailure),
            tableFailure.ToString());
    }

    [TestMethod]
    public void SecondPumpCreationFailureRollsBackBothPhysicalSubscriptions()
    {
        PairFixture fixture = CreateFixture(1_040);
        var onlyKnownPump = new TestPump();

        Assert.IsFalse(TryCreate(fixture,
            new SequentialPumpFactory(onlyKnownPump),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out var failure));
        Assert.IsNull(owner);
        Assert.IsFalse(registration.IsValid);
        Assert.AreEqual(Switch2JoyConJoinedRuntimeCreateFailureKind.
            RightPumpRejected, failure.Kind);
        Assert.IsFalse(failure.RequiresQuarantine);
        Assert.AreEqual(1, onlyKnownPump.StopCount);
        Assert.AreEqual(1, fixture.Left.UnsubscribeCount);
        Assert.AreEqual(1, fixture.Right.UnsubscribeCount);
        Assert.AreEqual(1, fixture.Left.ReleaseWaitCount);
        Assert.AreEqual(1, fixture.Right.ReleaseWaitCount);
    }

    [TestMethod]
    public void ConsumedPrepareFailureAttemptsBothReleasesAndQuarantinesPair()
    {
        PairFixture fixture = CreateFixture(1_005);
        fixture.Right.SubscribeResult = false;
        fixture.Left.ReleaseResult =
            Switch2BluetoothInputLeaseReleaseResult.TimedOut;

        Assert.IsFalse(TryCreate(fixture,
            Switch2BluetoothRuntimeDrainPumpFactory.Instance,
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out var failure));
        Assert.IsFalse(registration.IsValid);
        Assert.IsNotNull(owner);
        Assert.AreSame(owner, failure.QuarantinedOwner);
        Assert.IsTrue(failure.RequiresQuarantine);
        Assert.AreEqual(Switch2JoyConJoinedRuntimeCreateFailureKind.
            RollbackTimedOut, failure.Kind);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Quarantined,
            owner.State);
        Assert.AreEqual(1, fixture.Left.UnsubscribeCount);
        Assert.AreEqual(1, fixture.Right.UnsubscribeCount);
        Assert.AreEqual(1, fixture.Left.ReleaseWaitCount);
        Assert.AreEqual(1, fixture.Right.ReleaseWaitCount,
            "A failed first release proof cannot suppress the second proof.");
        Assert.IsFalse(owner.LeftReleaseProven);
        Assert.IsTrue(owner.RightReleaseProven);
        Assert.IsFalse(owner.Authenticates(owner.RuntimeDevice,
            RuntimeGeneration));
    }

    [TestMethod]
    public void LossBeforeFirstFrameRaisesOneAttentionAndInlineStopIsFenced()
    {
        PairFixture fixture = CreateFixture(1_006);
        var leftPump = new TestPump();
        var rightPump = new TestPump();
        Assert.IsTrue(TryCreate(fixture,
            new SequentialPumpFactory(leftPump, rightPump),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);

        int terminalReports = 0;
        int attentionCount = 0;
        bool inlineStopResult = true;
        InputControllerOwnerOperationFailure inlineStopFailure = default;
        InputControllerRetirementClaim retirement = default;
        owner.LifecycleAttention += (_, args) =>
        {
            Interlocked.Increment(ref attentionCount);
            Assert.AreEqual(Switch2StickSide.Left, args.Side);
            Assert.AreEqual(Switch2BluetoothInputEndReason.Disconnected,
                args.EndReason);
            inlineStopResult = registration.TryStopAndQuiesce(500,
                out inlineStopFailure);
        };
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                    Switch2RuntimeReportKind.TerminalNeutral)
            {
                return;
            }
            Interlocked.Increment(ref terminalReports);
            if (table.TryAcquireTerminalReportLease(retirement,
                    (DS4Device)sender, out var lease, out _))
            {
                lease.TryAcknowledgeTerminalNeutral(out _);
                lease.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        Assert.IsTrue(owner.TryArmRetirement(retirement, out _));

        fixture.Left.Disconnect();
        Assert.IsFalse(owner.Sink.LeftAttached);
        Assert.IsTrue(owner.Sink.RightAttached);
        Assert.AreEqual(0L, owner.Sink.PublishedCount,
            "Physical loss before a first frame must still retire the pair.");
        leftPump.RaiseAttention(new Switch2BluetoothInputDrainPumpAttention(
            Switch2BluetoothInputDrainPumpAttentionKind.OwnerRetired,
            LeftDeviceGeneration, LeftTransportGeneration,
            Switch2BluetoothInputEndReason.Disconnected, default));
        rightPump.RaiseAttention(new Switch2BluetoothInputDrainPumpAttention(
            Switch2BluetoothInputDrainPumpAttentionKind.OwnerRetired,
            RightDeviceGeneration, RightTransportGeneration,
            Switch2BluetoothInputEndReason.Stopped, default));

        Assert.AreEqual(1, Volatile.Read(ref attentionCount));
        Assert.IsFalse(inlineStopResult);
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            inlineStopFailure);
        Assert.AreEqual(Switch2BluetoothRuntimeStopFailureKind.CallbackActive,
            owner.LastStopFailure.Kind);
        Assert.IsTrue(registration.TryStopAndQuiesce(TimeoutMilliseconds,
            out var stopFailure), stopFailure.ToString());
        Assert.AreEqual(1, Volatile.Read(ref terminalReports));
        Assert.AreEqual(Switch2BluetoothInputEndReason.Disconnected,
            owner.Sink.TerminalReason,
            "The first exact half-loss reason is the logical terminal reason.");
        Assert.AreEqual(1L, owner.Sink.TerminalScheduleAttemptCount);
    }

    [TestMethod]
    public void TerminalSubscriberReentrantStopReturnsWithoutDeadlock()
    {
        PairFixture fixture = CreateFixture(1_007);
        var leftPump = new TestPump();
        var rightPump = new TestPump();
        Assert.IsTrue(TryCreate(fixture,
            new SequentialPumpFactory(leftPump, rightPump),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        InputControllerRetirementClaim retirement = default;
        bool reentrantResult = true;
        InputControllerOwnerOperationFailure reentrantFailure = default;
        Switch2BluetoothRuntimeStopFailureKind callbackFailure = default;
        using var callbackReturned = new ManualResetEventSlim();
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                    Switch2RuntimeReportKind.TerminalNeutral)
            {
                return;
            }
            reentrantResult = registration.TryStopAndQuiesce(1_000,
                out reentrantFailure);
            callbackFailure = owner.LastStopFailure.Kind;
            callbackReturned.Set();
            if (table.TryAcquireTerminalReportLease(retirement,
                    (DS4Device)sender, out var lease, out _))
            {
                lease.TryAcknowledgeTerminalNeutral(out _);
                lease.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        Assert.IsTrue(owner.TryArmRetirement(retirement, out _));

        Assert.IsTrue(registration.TryStopAndQuiesce(TimeoutMilliseconds,
            out var stopFailure), stopFailure.ToString());
        Assert.IsTrue(callbackReturned.Wait(100),
            "The terminal subscriber must not wait on its own outer stop.");
        Assert.IsFalse(reentrantResult);
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            reentrantFailure);
        Assert.AreEqual(Switch2BluetoothRuntimeStopFailureKind.CallbackActive,
            callbackFailure, "Reentrant rejection must not wait for the outer stop to time out.");
        Assert.AreEqual(1L, owner.Sink.TerminalScheduleAttemptCount);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Stopped,
            owner.State);
    }

    [TestMethod]
    public void ConcurrentStopsShareOneNeutralAndBothPhysicalProofs()
    {
        PairFixture fixture = CreateFixture(1_008);
        Assert.IsTrue(TryCreate(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        InputControllerRetirementClaim retirement = default;
        int terminal = 0;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                    Switch2RuntimeReportKind.TerminalNeutral)
            {
                return;
            }
            Interlocked.Increment(ref terminal);
            if (table.TryAcquireTerminalReportLease(retirement,
                    (DS4Device)sender, out var lease, out _))
            {
                lease.TryAcknowledgeTerminalNeutral(out _);
                lease.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        Assert.IsTrue(owner.TryArmRetirement(retirement, out _));
        using Barrier start = new(2);
        bool first = false;
        bool second = false;
        InputControllerOwnerOperationFailure firstFailure = default, secondFailure = default;
        Task one = Task.Run(() =>
        {
            start.SignalAndWait();
            first = registration.TryStopAndQuiesce(TimeoutMilliseconds,
                out firstFailure);
        });
        Task two = Task.Run(() =>
        {
            start.SignalAndWait();
            second = registration.TryStopAndQuiesce(TimeoutMilliseconds,
                out secondFailure);
        });
        Assert.IsTrue(Task.WaitAll(new[] { one, two },
            TimeSpan.FromSeconds(3)));
        Assert.IsTrue(first, $"First stop: {firstFailure}; second: {secondFailure}; state: {owner.State}; terminal: {terminal}");
        Assert.IsTrue(second, $"Second stop: {secondFailure}; first: {firstFailure}; state: {owner.State}; terminal: {terminal}");
        Assert.AreEqual(1, Volatile.Read(ref terminal));
        Assert.AreEqual(1L, owner.Sink.TerminalScheduleAttemptCount);
        Assert.IsTrue(owner.LeftReleaseProven);
        Assert.IsTrue(owner.RightReleaseProven);
    }

    [TestMethod]
    public void RepeatedConcurrentStopsShareOneNeutralAndBothPhysicalProofs()
    {
        for (int iteration = 0; iteration < 200; iteration++)
            ConcurrentStopsShareOneNeutralAndBothPhysicalProofs();
    }

    [TestMethod]
    public async Task ConcurrentStopDuringOtherThreadsTerminalCallbackJoinsExistingStop()
    {
        PairFixture fixture = CreateFixture(1_089);
        Assert.IsTrue(TryCreate(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        InputControllerRetirementClaim retirement = default;
        using var terminalEntered = new ManualResetEventSlim();
        using var releaseTerminal = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        int terminal = 0;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind != Switch2RuntimeReportKind.TerminalNeutral) return;
            Interlocked.Increment(ref terminal);
            terminalEntered.Set();
            if (!releaseTerminal.Wait(3000)) throw new TimeoutException("Test terminal was not released.");
            if (table.TryAcquireTerminalReportLease(retirement, (DS4Device)sender, out var lease, out _))
            {
                lease.TryAcknowledgeTerminalNeutral(out _);
                lease.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        Assert.IsTrue(owner.TryArmRetirement(retirement, out _));
        Task<bool> first = Task.Run(() => registration.TryStopAndQuiesce(TimeoutMilliseconds, out _));
        Task<bool> second = null;
        try
        {
            Assert.IsTrue(terminalEntered.Wait(1000));
            second = Task.Run(() => { secondStarted.Set(); return registration.TryStopAndQuiesce(TimeoutMilliseconds, out _); });
            Assert.IsTrue(secondStarted.Wait(1000));
            Assert.IsFalse(second.Wait(100),
                "An independent stop must join the existing operation while its terminal callback is still running.");
            releaseTerminal.Set();
            Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.IsTrue(await second.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(1, terminal);
            Assert.AreEqual(1L, owner.Sink.TerminalScheduleAttemptCount);
            Assert.IsTrue(owner.LeftReleaseProven && owner.RightReleaseProven);
        }
        finally
        {
            releaseTerminal.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(4));
            if (second != null) await second.WaitAsync(TimeSpan.FromSeconds(4));
        }
    }

    [TestMethod]
    public void StopAndOppositeHalfAttentionCanRaceWithoutDuplicateTerminal()
    {
        PairFixture fixture = CreateFixture(1_080);
        using var stopEntered = new ManualResetEventSlim();
        using var releaseStop = new ManualResetEventSlim();
        var leftPump = new TestPump
        {
            StopEntered = stopEntered,
            ReleaseStop = releaseStop,
        };
        var rightPump = new TestPump();
        Assert.IsTrue(TryCreate(fixture,
            new SequentialPumpFactory(leftPump, rightPump),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        InputControllerRetirementClaim retirement = default;
        int attention = 0;
        int terminal = 0;
        owner.LifecycleAttention += (_, _) =>
            Interlocked.Increment(ref attention);
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                    Switch2RuntimeReportKind.TerminalNeutral)
            {
                return;
            }
            Interlocked.Increment(ref terminal);
            if (table.TryAcquireTerminalReportLease(retirement,
                    (DS4Device)sender, out var lease, out _))
            {
                lease.TryAcknowledgeTerminalNeutral(out _);
                lease.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        Assert.IsTrue(owner.TryArmRetirement(retirement, out _));

        bool stopped = false;
        Task stopping = Task.Run(() => stopped = registration.
            TryStopAndQuiesce(TimeoutMilliseconds, out _));
        Assert.IsTrue(stopEntered.Wait(500));
        rightPump.RaiseAttention(new Switch2BluetoothInputDrainPumpAttention(
            Switch2BluetoothInputDrainPumpAttentionKind.OwnerRetired,
            RightDeviceGeneration, RightTransportGeneration,
            Switch2BluetoothInputEndReason.Stopped, default));
        releaseStop.Set();
        Assert.IsTrue(stopping.Wait(2_000));
        Assert.IsTrue(stopped);
        Assert.AreEqual(1, Volatile.Read(ref attention));
        Assert.AreEqual(1, Volatile.Read(ref terminal));
        Assert.AreEqual(1L, owner.Sink.TerminalScheduleAttemptCount);
    }

    [TestMethod]
    public void ActiveReleaseUncertaintyQuarantinesWholePairAndBlocksRemoval()
    {
        PairFixture fixture = CreateFixture(1_081);
        fixture.Right.ReleaseResult =
            Switch2BluetoothInputLeaseReleaseResult.TimedOut;
        Assert.IsTrue(TryCreate(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        InputControllerRetirementClaim retirement = default;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                    Switch2RuntimeReportKind.TerminalNeutral &&
                table.TryAcquireTerminalReportLease(retirement,
                    (DS4Device)sender, out var lease, out _))
            {
                lease.TryAcknowledgeTerminalNeutral(out _);
                lease.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        Assert.IsTrue(owner.TryArmRetirement(retirement, out _));

        Assert.IsFalse(registration.TryStopAndQuiesce(TimeoutMilliseconds,
            out var stopFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            stopFailure);
        Assert.IsTrue(owner.RequiresQuarantine);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Quarantined,
            owner.State);
        Assert.IsTrue(owner.LeftReleaseProven);
        Assert.IsFalse(owner.RightReleaseProven);
        Assert.AreEqual(1, fixture.Left.ReleaseWaitCount);
        Assert.AreEqual(1, fixture.Right.ReleaseWaitCount);
        Assert.AreEqual(Switch2BluetoothRuntimeTerminalState.Delivered,
            owner.Sink.TerminalState);
        Assert.IsFalse(registration.TryRemove(out var removeFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.RemoveRejected,
            removeFailure);
    }

    [TestMethod]
    public void WarmIdentityAndCredentialChecksAllocateNothing()
    {
        PairFixture fixture = CreateFixture(1_009);
        Assert.IsTrue(TryCreate(fixture,
            new SequentialPumpFactory(new TestPump(), new TestPump()),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        Assert.IsTrue(owner.TryAdoptBoundSlot(token, out var adoption,
            out _));
        for (int index = 0; index < 2_000; index++)
        {
            _ = owner.Authenticates(owner.RuntimeDevice, RuntimeGeneration);
            _ = adoption.Equals(adoption);
            _ = owner.State;
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            if (!owner.Authenticates(owner.RuntimeDevice,
                    RuntimeGeneration) || !adoption.Equals(adoption) ||
                owner.State != Switch2BluetoothRuntimeOwnerState.Created)
            {
                Assert.Fail("Warm exact-identity check changed state.");
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.IsTrue(owner.TryAbortUnpublished(adoption,
            TimeoutMilliseconds, out var abortFailure),
            abortFailure.ToString());
        Assert.IsTrue(table.TryRollback(rollback, out var tableFailure),
            tableFailure.ToString());
    }

    private static InputControllerSlotToken Activate(
        Switch2JoyConJoinedRuntimeOwner owner,
        InputControllerRegistration registration,
        InputControllerRegistrationTable table)
    {
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out var tableFailure), tableFailure.ToString());
        Assert.IsTrue(owner.TryAdoptBoundSlot(token, out var adoption,
            out var adoptionFailure), adoptionFailure.ToString());
        Assert.IsTrue(owner.TryPrepareActivation(adoption,
            TimeoutMilliseconds, out var prepared, out var prepareFailure),
            prepareFailure.ToString());
        Assert.AreEqual(Switch2BluetoothInputDrainPumpState.Parked,
            owner.LeftDrainPump.State);
        Assert.AreEqual(Switch2BluetoothInputDrainPumpState.Parked,
            owner.RightDrainPump.State);
        Assert.IsTrue(table.TryBeginActivate(token, out var activation,
            out tableFailure), tableFailure.ToString());
        InputControllerActivationCommitCredential commit =
            AcquireActivationCommit(table, activation);
        Assert.IsTrue(owner.TryCommitPrepared(prepared, commit,
            out var commitFailure), commitFailure.ToString());
        Assert.IsTrue(table.TryCompleteActivate(commit, true,
            out tableFailure), tableFailure.ToString());
        return token;
    }

    private static InputControllerActivationCommitCredential
        AcquireActivationCommit(InputControllerRegistrationTable table,
            in InputControllerActivationClaim claim)
    {
        Assert.IsTrue(table.TryAcquireActivationCommit(claim,
            out var credential, out var failure), failure.ToString());
        return credential;
    }

    private static bool TryCreate(PairFixture fixture,
        ISwitch2BluetoothRuntimeDrainPumpFactory pumpFactory,
        ISwitch2RuntimeTerminalScheduler scheduler,
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
            leftQueueCapacity: 4, RightDeviceGeneration,
            RightTransportGeneration, rightCalibration,
            rightQueueCapacity: 4, QpcFrequency,
            new Switch2JoyConPairPolicy(1_000), TimeoutMilliseconds,
            pumpFactory, scheduler, out owner, out registration, out failure);
    }

    private static PairFixture CreateFixture(ulong scanGeneration)
    {
        Switch2BluetoothConnectionAdmission leftAdmission = Admission(
            Switch2ControllerModel.JoyCon2Left, scanGeneration);
        Switch2BluetoothConnectionAdmission rightAdmission = Admission(
            Switch2ControllerModel.JoyCon2Right, scanGeneration);
        byte[] key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        byte[] leftIdentity = Enumerable.Range(40, 16).
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
            leftPeer, leftAdmission, rightPeer, rightAdmission,
            out var pairAdmission));
        return new PairFixture(pairAdmission,
            new FakeLease(leftAdmission, ExactGatt(scanGeneration)),
            new FakeLease(rightAdmission, ExactGatt(scanGeneration)));
    }

    private static Switch2BluetoothConnectionAdmission Admission(
        Switch2ControllerModel model, ulong scanGeneration) => new(
            scanGeneration, model,
            model == Switch2ControllerModel.JoyCon2Left ?
                Switch2AdvertisementCodec.JoyCon2LeftProductId :
                Switch2AdvertisementCodec.JoyCon2RightProductId);

    private static Switch2BluetoothGattSnapshot ExactGatt(
        ulong scanGeneration) => new(scanGeneration, 1, 1,
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid,
            Switch2GattProperty.Read | Switch2GattProperty.Notify);

    private static InputControllerRegistrationTable OpenTable(int slots,
        ulong generation = 1)
    {
        var table = new InputControllerRegistrationTable(slots);
        Assert.IsTrue(table.TryOpen(generation, out var failure),
            failure.ToString());
        return table;
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
        private Switch2BluetoothInputDisconnected disconnected;

        internal FakeLease(in Switch2BluetoothConnectionAdmission admission,
            in Switch2BluetoothGattSnapshot gatt)
        {
            Admission = admission;
            GattSnapshot = gatt;
        }

        public Switch2BluetoothConnectionAdmission Admission { get; }
        public Switch2BluetoothGattSnapshot GattSnapshot { get; }
        internal bool SubscribeResult { get; set; } = true;
        internal Switch2BluetoothInputLeaseReleaseResult ReleaseResult
        { get; set; } = Switch2BluetoothInputLeaseReleaseResult.Released;
        internal int SubscribeCount { get; private set; }
        internal int UnsubscribeCount { get; private set; }
        internal int ReleaseWaitCount { get; private set; }

        public bool TrySubscribeCccdNotify(ulong transportGeneration,
            Switch2BluetoothInputNotification notification,
            Switch2BluetoothInputDisconnected disconnected)
        {
            SubscribeCount++;
            this.disconnected = disconnected;
            return SubscribeResult;
        }

        public bool TryUnsubscribeCccdNone(ulong transportGeneration)
        {
            UnsubscribeCount++;
            return true;
        }

        public Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
            ulong transportGeneration, int timeoutMilliseconds)
        {
            ReleaseWaitCount++;
            return timeoutMilliseconds >= 0 ? ReleaseResult :
                Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }

        internal void Disconnect() => disconnected?.Invoke(
            Admission.Model == Switch2ControllerModel.JoyCon2Left ?
                LeftTransportGeneration : RightTransportGeneration);
    }

    private sealed class TestPump : ISwitch2BluetoothRuntimeDrainPump
    {
        private Action<Switch2BluetoothInputDrainPumpAttention> attention;
        private bool currentWorker;
        internal bool StartResult { get; set; } = true;
        internal bool StopResult { get; set; } = true;
        internal ManualResetEventSlim StopEntered { get; set; }
        internal ManualResetEventSlim ReleaseStop { get; set; }
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        public Switch2BluetoothInputDrainPumpState State { get; private set; } =
            Switch2BluetoothInputDrainPumpState.Created;
        public Switch2BluetoothInputDrainPumpFailure TerminalFailure =>
            StopResult ? default :
                Switch2BluetoothInputDrainPumpFailure.WorkerExitTimedOut;
        public bool RequiresQuarantine => !StopResult;
        public bool IsCurrentWorkerThread => currentWorker;
        public long PublishedCount => 0;

        public bool TrySetLifecycleAttentionHandler(
            Action<Switch2BluetoothInputDrainPumpAttention> handler)
        {
            attention = handler;
            return handler != null;
        }

        public bool TryStartParked(int timeoutMilliseconds,
            out Switch2BluetoothInputDrainPumpFailure failure)
        {
            StartCount++;
            State = StartResult ? Switch2BluetoothInputDrainPumpState.Parked :
                Switch2BluetoothInputDrainPumpState.StopRequested;
            failure = StartResult ? default :
                Switch2BluetoothInputDrainPumpFailure.OwnerRejected;
            return StartResult;
        }

        public bool TryStopAndJoin(int timeoutMilliseconds,
            out Switch2BluetoothInputDrainPumpFailure failure)
        {
            StopCount++;
            StopEntered?.Set();
            if (ReleaseStop != null &&
                !ReleaseStop.Wait(timeoutMilliseconds))
            {
                State = Switch2BluetoothInputDrainPumpState.Quarantined;
                failure = Switch2BluetoothInputDrainPumpFailure.
                    WorkerExitTimedOut;
                return false;
            }
            State = StopResult ? Switch2BluetoothInputDrainPumpState.Stopped :
                Switch2BluetoothInputDrainPumpState.Quarantined;
            failure = StopResult ? default :
                Switch2BluetoothInputDrainPumpFailure.WorkerExitTimedOut;
            return StopResult;
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
            pump = index < pumps.Length ? pumps[index++] : null;
            failure = pump == null ?
                Switch2BluetoothInputDrainPumpFailure.OwnerRejected : default;
            return pump != null;
        }
    }
}
