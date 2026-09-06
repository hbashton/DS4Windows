using System.Buffers.Binary;
using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2BluetoothRuntimeOwnerTests
{
    private const ulong DeviceGeneration = 401;
    private const ulong TransportGeneration = 607;
    private const long QpcFrequency = 10_000_000;
    private const int LifecycleTimeoutMilliseconds = 1_000;

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2)]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void DisconnectedFeedbackTargetRetiresWithoutInventingAStopReceipt(
        Switch2ControllerModel model)
    {
        var lease = CreateLease(model, 905);
        lease.HasHdRumbleOutput = true;
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration, out _));
        var table = OpenTable(1);
        var token = Activate(owner, registration, table);
        Assert.IsTrue(owner.RuntimeDevice.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.DualSenseVirtualDevice,
            DeviceGeneration, TransportGeneration, out var session));
        Assert.IsTrue(session.TryPublish(new ControllerFeedbackActuatorState(20_000, 10_000, 0, 0)));
        int writesBeforeDisconnect = lease.FeedbackWriteCount;
        lease.Disconnect();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.Sink.TerminalRequested,
            TimeSpan.FromSeconds(2)));

        StopWithoutRemoving(owner, registration, table, token);

        Assert.IsFalse(owner.RequiresQuarantine);
        Assert.AreEqual(writesBeforeDisconnect, lease.FeedbackWriteCount,
            "An ended physical target cannot acknowledge a new rumble Stop.");
        Assert.IsFalse(session.TryPublish(new ControllerFeedbackActuatorState(30_000, 0, 0, 0)));
        Assert.IsTrue(session.TryRetire(), "Output removal must be locally idempotent.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConnectedStopStillRequiresPhysicalFeedbackDelivery(bool reject)
    {
        var lease = CreateLease(Switch2ControllerModel.ProController2, 906);
        lease.HasHdRumbleOutput = true;
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration, out _));
        var table = OpenTable(1);
        var token = Activate(owner, registration, table);
        Assert.IsTrue(owner.RuntimeDevice.TryCreateVirtualFeedbackSession(
            ControllerFeedbackSource.XboxOneVirtualDevice,
            DeviceGeneration, TransportGeneration, out var session));
        Assert.IsTrue(session.TryPublish(new ControllerFeedbackActuatorState(20_000, 0, 0, 0)));
        lease.RejectFeedback = reject;
        if (!reject)
        {
            StopWithoutRemoving(owner, registration, table, token);
            Assert.IsFalse(owner.RequiresQuarantine);
        }
        else
        {
            Assert.IsTrue(table.TryBeginRetire(token, out var claim, out _));
            Assert.IsTrue(owner.TryArmRetirement(claim, out _));
            Assert.IsFalse(registration.TryStopAndQuiesce(1_000, out _));
            Assert.IsTrue(owner.RequiresQuarantine);
            Assert.AreEqual(Switch2BluetoothRuntimeStopFailureKind.TerminalDeliveryRejected,
                owner.LastStopFailure.Kind);
            // Drain the synthetic input worker after the intentionally failed stop.
            lease.Disconnect();
        }
        Assert.IsFalse(session.WasRetiredDisconnected);
    }

    [TestMethod]
    public void ProCounterResetDoesNotRetireTheLiveBluetoothRuntime()
    {
        var lease = CreateLease(Switch2ControllerModel.ProController2, 904);
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration, out _));
        var table = OpenTable(1);
        var token = Activate(owner, registration, table);
        int regular = 0;
        owner.RuntimeDevice.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind == Switch2RuntimeReportKind.Regular)
                Interlocked.Increment(ref regular);
        };
        try
        {
            // Observed b65 counter boundary, with a synthetic successor. Drain
            // each arrival separately so this cannot pass by queue coalescing.
            uint[] counters = { 1_431_640, 1, 16 };
            for (int index = 0; index < counters.Length; index++)
            {
                lease.Notify(counters[index], 100_000 + index * 150_000);
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                    owner.InputOwner.PublishedCount == index + 1, 1000),
                    $"Counter {counters[index]} must reach the profile pipeline.");
            }
            Assert.AreEqual(3, Volatile.Read(ref regular));
            Assert.AreEqual(0L, owner.InputOwner.OverflowCount);
            Assert.AreEqual(0, lease.UnsubscribeCount);
            Assert.IsFalse(owner.RequiresQuarantine);
            Assert.AreEqual(Switch2RuntimeInputDeviceState.Active, owner.RuntimeDevice.RuntimeState);
        }
        finally { StopWithoutRemoving(owner, registration, table, token); }
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2)]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void SerializedOutputReplacementSurvivesLiveNotificationBurst(Switch2ControllerModel model)
    {
        var lease = CreateLease(model, 903);
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration, out _));
        var table = OpenTable(1);
        var token = Activate(owner, registration, table);
        int regular = 0;
        using var freshReport = new ManualResetEventSlim();
        owner.RuntimeDevice.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind == Switch2RuntimeReportKind.Regular)
            {
                Interlocked.Increment(ref regular);
                freshReport.Set();
            }
        };
        try
        {
            owner.RuntimeDevice.queueEvent(() =>
            {
                Assert.IsTrue(table.TryAcquireActionLease(token, 0, out var action, out _));
                using (action)
                    owner.RuntimeDevice.RunVirtualOutputTransition(() =>
                    {
                        Assert.IsTrue(owner.Sink.IsVirtualOutputTransitionActive);
                        for (uint counter = 2; counter <= 1000; counter++)
                            lease.Notify(counter, counter);
                    });
            });
            lease.Notify(1, 1);
            Assert.IsTrue(freshReport.Wait(2000));
            Assert.IsTrue(SpinWait.SpinUntil(() => owner.InputOwner.PublishedCount == 2, 1000));
            Assert.AreEqual(0L, owner.InputOwner.OverflowCount);
            Assert.AreEqual(1, Volatile.Read(ref regular),
                "Only the current baseline is mapped after the cold handoff.");
            Assert.IsFalse(owner.RuntimeDevice.IsVirtualOutputTransitionActive);
            Assert.AreEqual(Switch2RuntimeInputDeviceState.Active, owner.RuntimeDevice.RuntimeState);
        }
        finally { StopWithoutRemoving(owner, registration, table, token); }
    }

    [TestMethod]
    public void ExactSlotAdoptionIsIdempotentAndRejectsStaleOrForeignTokens()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            7);
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration,
            out _));
        Assert.IsFalse(owner.TryAdoptBoundSlot(default, out _,
            out var invalidFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeSlotAdoptionFailure.InvalidToken,
            invalidFailure);

        InputControllerRegistrationTable winnerTable = OpenTable(1, 7);
        InputControllerRegistrationTable foreignTable = OpenTable(1, 8);
        Assert.IsTrue(winnerTable.TryReserveAndBind(registration,
            out var winnerToken, out var winnerRollback, out _));
        Assert.IsTrue(foreignTable.TryReserveAndBind(registration,
            out var foreignToken, out var foreignRollback, out _));

        Assert.IsTrue(owner.TryAdoptBoundSlot(winnerToken,
            out var firstCredential, out var firstFailure),
            firstFailure.ToString());
        Assert.IsTrue(owner.TryAdoptBoundSlot(winnerToken,
            out var retryCredential, out var retryFailure),
            retryFailure.ToString());
        Assert.AreEqual(firstCredential, retryCredential,
            "An exact retry must return the same private adoption fence.");
        Assert.IsFalse(owner.TryAdoptBoundSlot(foreignToken, out _,
            out var foreignFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeSlotAdoptionFailure.
            DifferentSlotAlreadyAdopted, foreignFailure);
        Assert.IsTrue(foreignTable.TryRollback(foreignRollback,
            out var foreignRollbackFailure),
            foreignRollbackFailure.ToString());

        Assert.IsTrue(owner.TryAbortUnpublished(firstCredential, 1_000,
            out var abortFailure), abortFailure.ToString());
        Assert.IsTrue(winnerTable.TryRollback(winnerRollback,
            out var winnerRollbackFailure),
            winnerRollbackFailure.ToString());
        Assert.IsFalse(owner.TryAdoptBoundSlot(winnerToken, out _,
            out var staleFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeSlotAdoptionFailure.InvalidToken,
            staleFailure, "An adopted token cannot start a second lifetime.");
        Assert.IsFalse(owner.TryPrepareActivation(default, 1_000, out _,
            out var defaultPrepareFailure));
        Assert.AreEqual(Switch2BluetoothRuntimePrepareFailure.
            InvalidSlotAdoptionCredential, defaultPrepareFailure);
    }

    [TestMethod]
    public void ConcurrentCrossTableAdoptionHasOneWinnerAndLocalRollbackOnly()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            8);
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration,
            out _));
        InputControllerRegistrationTable firstTable = OpenTable(1, 9);
        InputControllerRegistrationTable secondTable = OpenTable(1, 10);
        Assert.IsTrue(firstTable.TryReserveAndBind(registration,
            out var firstToken, out var firstRollback, out _));
        Assert.IsTrue(secondTable.TryReserveAndBind(registration,
            out var secondToken, out var secondRollback, out _));

        using Barrier start = new(2);
        bool firstWon = false;
        bool secondWon = false;
        Switch2BluetoothRuntimeSlotAdoptionCredential firstCredential =
            default;
        Switch2BluetoothRuntimeSlotAdoptionCredential secondCredential =
            default;
        Switch2BluetoothRuntimeSlotAdoptionFailure firstFailure = default;
        Switch2BluetoothRuntimeSlotAdoptionFailure secondFailure = default;
        Task first = Task.Run(() =>
        {
            start.SignalAndWait();
            firstWon = owner.TryAdoptBoundSlot(firstToken,
                out firstCredential, out firstFailure);
        });
        Task second = Task.Run(() =>
        {
            start.SignalAndWait();
            secondWon = owner.TryAdoptBoundSlot(secondToken,
                out secondCredential, out secondFailure);
        });
        Assert.IsTrue(Task.WaitAll(new[] { first, second },
            TimeSpan.FromSeconds(2)));
        Assert.AreNotEqual(firstWon, secondWon,
            "Exactly one distinct table token may become cleanup authority.");

        Switch2BluetoothRuntimeSlotAdoptionCredential winnerCredential =
            firstWon ? firstCredential : secondCredential;
        Switch2BluetoothRuntimeSlotAdoptionCredential loserCredential =
            firstWon ? secondCredential : firstCredential;
        Switch2BluetoothRuntimeSlotAdoptionFailure loserFailure =
            firstWon ? secondFailure : firstFailure;
        Assert.IsTrue(winnerCredential.IsValid);
        Assert.IsFalse(loserCredential.IsValid);
        Assert.AreEqual(Switch2BluetoothRuntimeSlotAdoptionFailure.
            DifferentSlotAlreadyAdopted, loserFailure);

        InputControllerRegistrationTable loserTable = firstWon ?
            secondTable : firstTable;
        InputControllerSetupRollbackClaim loserRollback = firstWon ?
            secondRollback : firstRollback;
        Assert.IsTrue(loserTable.TryRollback(loserRollback,
            out var loserRollbackFailure), loserRollbackFailure.ToString());

        InputControllerSlotToken winnerToken = firstWon ?
            firstToken : secondToken;
        Assert.IsTrue(owner.TryAdoptBoundSlot(winnerToken,
            out var exactRetry, out var exactRetryFailure),
            exactRetryFailure.ToString());
        Assert.AreEqual(winnerCredential, exactRetry,
            "The losing rollback must not replace or revoke the winner.");
        Assert.IsTrue(owner.TryAbortUnpublished(winnerCredential, 1_000,
            out var cleanupFailure), cleanupFailure.ToString());
        InputControllerRegistrationTable winnerTable = firstWon ?
            firstTable : secondTable;
        InputControllerSetupRollbackClaim winnerRollback = firstWon ?
            firstRollback : secondRollback;
        Assert.IsTrue(winnerTable.TryRollback(winnerRollback,
            out var winnerRollbackFailure),
            winnerRollbackFailure.ToString());
    }

    [TestMethod]
    public void PrePrepareAdoptionAuthorizesExactCleanupBeforeTableRollback()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            9);
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration,
            out _));
        InputControllerRegistrationTable table = OpenTable(1, 11);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        Switch2BluetoothRuntimeSlotAdoptionCredential adoption =
            Adopt(owner, token);

        Assert.IsTrue(owner.TryAbortUnpublished(adoption, 1_000,
            out var abortFailure), abortFailure.ToString());
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(Switch2BluetoothInputDrainPumpState.Stopped,
            owner.DrainPump.State);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.AbortedUnpublished,
            owner.RuntimeDevice.RuntimeState);
        Assert.IsTrue(owner.LeaseReleaseProven);
        Assert.AreEqual(1, lease.UnsubscribeCount);
        Assert.AreEqual(1, lease.ReleaseWaitCount);
        Assert.IsTrue(table.TryRollback(rollback, out var tableFailure),
            tableFailure.ToString());
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2)]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void ExactTableActivationPublishesQueuedInputAndDefersOneTerminal(
        Switch2ControllerModel model)
    {
        FakeLease lease = CreateLease(model, scanGeneration: (ulong)model + 10);
        lease.InlineNotificationCount = 1;
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration,
            out var createFailure), createFailure.Kind.ToString());
        Assert.IsTrue(owner.DependenciesComplete);
        Assert.IsNotNull(owner.InputOwner);
        Assert.IsNotNull(owner.DrainPump);
        Assert.AreEqual(model, owner.Model);
        Assert.AreEqual(DeviceGeneration,
            owner.InputOwner.Descriptor.DeviceGeneration);
        Assert.AreEqual(TransportGeneration,
            owner.InputOwner.Descriptor.TransportGeneration);
        Assert.AreEqual(Switch2Transport.BluetoothLe,
            owner.InputOwner.Descriptor.Identity.Transport);

        InputControllerRegistrationTable table = OpenTable(1);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out var tableFailure), tableFailure.ToString());
        Switch2BluetoothRuntimeSlotAdoptionCredential adoption =
            Adopt(owner, token);

        int regular = 0;
        int terminal = 0;
        using ManualResetEventSlim regularLeaseEntered = new(false);
        using ManualResetEventSlim releaseRegularLease = new(false);
        InputControllerRetirementClaim retirement = default;
        bool terminalAcknowledged = false;
        InputControllerSlotTableFailure terminalAcknowledgeFailure = default;
        bool regularLeaseAcquired = false;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            Switch2RuntimeReportKind kind =
                ((Switch2RuntimeReportEventArgs)args).Kind;
            if (kind == Switch2RuntimeReportKind.Regular)
            {
                if (table.TryAcquireReportLease(token, (DS4Device)sender,
                        out var reportLease, out _))
                {
                    regularLeaseAcquired = true;
                    Interlocked.Increment(ref regular);
                    regularLeaseEntered.Set();
                    releaseRegularLease.Wait(TimeSpan.FromSeconds(2));
                    reportLease.Dispose();
                }
                return;
            }
            Interlocked.Increment(ref terminal);
            if (table.TryAcquireTerminalReportLease(retirement,
                    (DS4Device)sender, out var terminalLease,
                    out var acquireFailure))
            {
                terminalAcknowledged = terminalLease.
                    TryAcknowledgeTerminalNeutral(
                        out terminalAcknowledgeFailure);
                terminalLease.Dispose();
            }
        };

        Assert.IsTrue(owner.TryPrepareActivation(adoption,
            LifecycleTimeoutMilliseconds, out var prepareCredential,
            out var prepareFailure), prepareFailure.ToString());
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Prepared,
            owner.State);
        Assert.AreEqual(0, regular,
            "An inline notification stays parked before table activation and input commit.");

        Assert.IsTrue(table.TryBeginActivate(token, out var activationClaim,
            out tableFailure), tableFailure.ToString());
        InputControllerActivationCommitCredential activationCommit =
            AcquireActivationCommit(table, activationClaim);
        Assert.IsTrue(owner.TryCommitPrepared(prepareCredential,
            activationCommit, out var commitFailure),
            commitFailure.ToString());
        Assert.IsTrue(regularLeaseEntered.Wait(TimeSpan.FromSeconds(2)),
            "A committed owner must be report-admissible while its exact activation epoch is pending.");
        Assert.IsTrue(regularLeaseAcquired);
        Assert.IsTrue(table.TryCompleteActivate(activationCommit,
            externalCommitSucceeded: true, out tableFailure),
            tableFailure.ToString());
        releaseRegularLease.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref regular) == 1,
            TimeSpan.FromSeconds(2)), "The queued notification was not drained.");
        Assert.IsTrue(SpinWait.SpinUntil(() =>
                !owner.Sink.PublicationInProgress,
            TimeSpan.FromSeconds(2)),
            "The exact report lease must leave the sink before retirement starts.");

        Assert.IsTrue(table.TryBeginRetire(token, out retirement,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(owner.TryArmRetirement(retirement,
            out var armFailure), armFailure.ToString());
        Assert.AreEqual(0, terminal,
            "Retiring the table alone cannot manufacture terminal output.");
        Assert.IsTrue(registration.TryStopAndQuiesce(
            LifecycleTimeoutMilliseconds, out var stopFailure),
            $"{stopFailure}: {owner.LastStopFailure.Kind}");
        Assert.AreEqual(1, terminal);
        Assert.IsTrue(terminalAcknowledged,
            terminalAcknowledgeFailure.ToString());
        Assert.IsTrue(owner.LeaseReleaseProven);
        Assert.AreEqual(Switch2BluetoothRuntimeTerminalState.Delivered,
            owner.Sink.TerminalState);
        Assert.IsTrue(table.TryWaitForDrain(retirement, 0,
            out tableFailure), tableFailure.ToString());
        Assert.IsTrue(table.TryMarkQuiesced(retirement, out tableFailure),
            tableFailure.ToString());
        Assert.IsTrue(registration.TryRemove(out var removeFailure),
            removeFailure.ToString());
        Assert.IsTrue(table.TryCompleteRemoval(retirement, out tableFailure),
            tableFailure.ToString());
        Assert.AreEqual(1, lease.UnsubscribeCount);
    }

    [TestMethod]
    public void PreparedAbortIsSilentBoundedAndNeverRequiresTerminal()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            30);
        lease.InlineNotificationCount = 1;
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration,
            out _));
        InputControllerRegistrationTable table = OpenTable(1);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        Switch2BluetoothRuntimeSlotAdoptionCredential adoption =
            Adopt(owner, token);
        int reports = 0;
        owner.RuntimeDevice.Report += (_, _) => reports++;

        Assert.IsTrue(owner.TryPrepareActivation(adoption, 1_000,
            out var credential, out _));
        Assert.IsTrue(owner.TryAbortPrepared(credential, 1_000,
            out var abortFailure), abortFailure.ToString());
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.AbortedUnpublished,
            owner.RuntimeDevice.RuntimeState);
        Assert.AreEqual(0, reports);
        Assert.IsFalse(owner.Sink.TerminalRequested);
        Assert.IsTrue(owner.LeaseReleaseProven);
        Assert.IsTrue(table.TryRollback(rollback, out var tableFailure),
            tableFailure.ToString());
    }

    [TestMethod]
    public void ActivationPendingFencesCloseRetireAndStaleClaim()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            31);
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration,
            out _));
        InputControllerRegistrationTable table = OpenTable(1);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out _));
        Switch2BluetoothRuntimeSlotAdoptionCredential adoption =
            Adopt(owner, token);
        Assert.IsTrue(owner.TryPrepareActivation(adoption, 1_000,
            out var credential, out _));
        Assert.IsTrue(table.TryBeginActivate(token, out var activationClaim,
            out _));
        InputControllerActivationClaim copiedClaim = activationClaim;
        InputControllerActivationCommitCredential activationCommit =
            AcquireActivationCommit(table, activationClaim);

        Assert.IsFalse(table.TryBeginRetire(token, out _,
            out var retireFailure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, retireFailure);
        Assert.IsFalse(table.TryClose(1, out _, out var closeFailure));
        Assert.AreEqual(InputControllerSlotTableFailure.Busy, closeFailure);
        Assert.IsFalse(table.TryCompleteActivate(copiedClaim,
            externalCommitSucceeded: false, out var copiedClaimFailure));
        Assert.AreEqual(InputControllerSlotTableFailure.StaleCredential,
            copiedClaimFailure,
            "A copied pre-commit claim cannot invalidate an acquired commit epoch.");
        Assert.IsTrue(owner.TryCommitPrepared(credential, activationCommit,
            out var commitFailure), commitFailure.ToString());
        Assert.IsTrue(table.TryCompleteActivate(activationCommit, true,
            out var completeFailure), completeFailure.ToString());
        Assert.IsFalse(owner.TryCommitPrepared(credential, activationCommit,
            out commitFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeCommitFailure.
            InvalidActivationCommitCredential, commitFailure,
            "The table commit capability expires with its exact activation epoch.");

        StopWithoutRemoving(owner, registration, table, token);
    }

    [TestMethod]
    public void ForeignTableLoserCannotAbortCommitOrRetireWinner()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            32);
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration,
            out _));
        InputControllerRegistrationTable first = OpenTable(1, 10);
        InputControllerRegistrationTable second = OpenTable(1, 20);
        Assert.IsTrue(first.TryReserveAndBind(registration, out var firstToken,
            out _, out _));
        Assert.IsTrue(second.TryReserveAndBind(registration,
            out var secondToken, out var secondRollback, out _));

        Assert.IsTrue(owner.TryAdoptBoundSlot(firstToken,
            out var winnerAdoption, out var winnerAdoptionFailure),
            winnerAdoptionFailure.ToString());
        Assert.IsFalse(owner.TryAdoptBoundSlot(secondToken,
            out var loserAdoption, out var loserAdoptionFailure));
        Assert.AreEqual(Switch2BluetoothRuntimeSlotAdoptionFailure.
            DifferentSlotAlreadyAdopted, loserAdoptionFailure);
        Assert.IsFalse(loserAdoption.IsValid);
        Assert.IsTrue(owner.TryPrepareActivation(winnerAdoption, 1_000,
            out var winnerCredential, out _));
        Assert.IsFalse(owner.TryPrepareActivation(loserAdoption, 1_000,
            out _, out var loserFailure));
        Assert.AreEqual(Switch2BluetoothRuntimePrepareFailure.
            InvalidSlotAdoptionCredential, loserFailure);
        Assert.IsFalse(owner.TryAbortUnpublished(loserAdoption, 1_000,
            out var loserAbort));
        Assert.AreEqual(Switch2BluetoothRuntimeAbortFailure.
            InvalidCredential, loserAbort);
        Assert.IsTrue(second.TryRollback(secondRollback,
            out var loserRollbackFailure), loserRollbackFailure.ToString());

        Assert.IsTrue(first.TryBeginActivate(firstToken,
            out var firstActivation, out _));
        InputControllerActivationCommitCredential firstCommit =
            AcquireActivationCommit(first, firstActivation);
        Assert.IsTrue(owner.TryCommitPrepared(winnerCredential,
            firstCommit, out _));
        Assert.IsTrue(first.TryCompleteActivate(firstCommit, true,
            out _));

        Assert.IsTrue(second.TryReserveAndBind(registration,
            out secondToken, out _, out _));
        Assert.IsTrue(second.TryActivate(secondToken, out _));
        Assert.IsTrue(second.TryBeginRetire(secondToken,
            out var foreignRetirement, out _));
        Assert.IsFalse(owner.TryArmRetirement(foreignRetirement,
            out var foreignArm));
        Assert.AreEqual(Switch2BluetoothRuntimeRetirementArmFailure.
            InvalidClaim, foreignArm);
        Assert.IsFalse(registration.TryStopAndQuiesce(20, out _));
        Assert.IsFalse(owner.Sink.TerminalRequested);

        StopWithoutRemoving(owner, registration, first, firstToken);
    }

    [TestMethod]
    public void StartFailureSelfCleanupIsIdempotentlyProvableToTableRollback()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            33);
        ConfigurablePump pump = new() { StartResult = false };
        Assert.IsTrue(TryCreate(lease, new FixedPumpFactory(pump),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out var rollback, out _));
        Switch2BluetoothRuntimeSlotAdoptionCredential adoption =
            Adopt(owner, token);

        Assert.IsFalse(owner.TryPrepareActivation(adoption, 1_000,
            out _, out var prepareFailure));
        Assert.AreEqual(Switch2BluetoothRuntimePrepareFailure.
            PumpStartRejected, prepareFailure);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.AbortedUnpublished,
            owner.State);
        Assert.IsTrue(owner.TryAbortUnpublished(adoption, 1_000,
            out var cleanupFailure), cleanupFailure.ToString());
        Assert.IsTrue(table.TryRollback(rollback, out var tableFailure),
            tableFailure.ToString());
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void ConsumedSubscriptionFailureRetainsAmbiguousReleaseSafely(
        bool throwSubscribe, bool inlineDisconnect)
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            (ulong)(40 + (throwSubscribe ? 1 : 0) +
                (inlineDisconnect ? 2 : 0)));
        lease.SubscribeResult = inlineDisconnect;
        lease.ThrowSubscribe = throwSubscribe;
        lease.InlineDisconnect = inlineDisconnect;
        lease.ReleaseResult = Switch2BluetoothInputLeaseReleaseResult.TimedOut;

        Assert.IsFalse(TryCreate(lease, out var owner,
            out var registration, out var failure));
        Assert.IsNotNull(owner,
            "Consumed admission plus unproven release must retain a cleanup graph.");
        Assert.IsFalse(registration.IsValid);
        Assert.AreSame(owner, failure.QuarantinedOwner);
        Assert.IsTrue(owner.RequiresQuarantine);
        Assert.IsFalse(owner.DependenciesComplete);
        Assert.IsFalse(owner.TryStopAndQuiesce(owner.RuntimeDevice,
            DeviceGeneration, 20, out var stopFailure));
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            stopFailure, "A partial retained graph must reject, not dereference null.");
    }

    [TestMethod]
    public void ThrowingPumpFactoryAlwaysRetainsUncertainAttachedGraph()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            50);
        Assert.IsFalse(TryCreate(lease, new ThrowingPumpFactory(),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out var failure));
        Assert.IsNotNull(owner);
        Assert.IsFalse(registration.IsValid);
        Assert.AreSame(owner, failure.QuarantinedOwner);
        Assert.AreEqual(Switch2BluetoothRuntimeCreateFailureKind.DependencyThrew,
            failure.Kind);
        Assert.IsTrue(owner.RequiresQuarantine);
        Assert.IsFalse(owner.DependenciesComplete);
        Assert.IsFalse(owner.TryStopAndQuiesce(owner.RuntimeDevice,
            DeviceGeneration, 20, out _));
    }

    [TestMethod]
    public void ReleaseFailurePreventsTerminalThenRetryCannotEraseQuarantine()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            51);
        lease.ReleaseResult = Switch2BluetoothInputLeaseReleaseResult.Rejected;
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration,
            out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        int terminal = 0;
        InputControllerRetirementClaim claim = default;
        owner.RuntimeDevice.Report += (_, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind ==
                Switch2RuntimeReportKind.TerminalNeutral)
            {
                terminal++;
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out claim, out _));
        Assert.IsTrue(owner.TryArmRetirement(claim, out _));

        Assert.IsFalse(registration.TryStopAndQuiesce(100, out _));
        Assert.AreEqual(Switch2BluetoothRuntimeStopFailureKind.
            LeaseReleaseRejected, owner.LastStopFailure.Kind);
        Assert.AreEqual(0, terminal,
            "Terminal neutral cannot precede exact platform release proof.");
        Assert.IsTrue(owner.RequiresQuarantine);

        lease.ReleaseResult = Switch2BluetoothInputLeaseReleaseResult.Released;
        Assert.IsFalse(registration.TryStopAndQuiesce(1_000, out _),
            "Later cleanup can finish but may not restore a quarantined slot.");
        Assert.AreEqual(1, terminal);
        Assert.IsTrue(owner.RequiresQuarantine);
        Assert.IsFalse(registration.TryRemove(out _));
    }

    [TestMethod]
    public void TerminalTimeoutRetryWaitsSameScheduledRequest()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            52);
        DeferredTerminalScheduler scheduler = new();
        Assert.IsTrue(TryCreate(lease,
            Switch2BluetoothRuntimeDrainPumpFactory.Instance, scheduler,
            out var owner, out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        Assert.IsTrue(table.TryBeginRetire(token, out var claim, out _));
        Assert.IsTrue(owner.TryArmRetirement(claim, out _));
        owner.RuntimeDevice.Report += (_, _) => { };

        Assert.IsFalse(registration.TryStopAndQuiesce(30, out _));
        Assert.AreEqual(1, scheduler.ScheduleCount);
        Assert.AreEqual(Switch2BluetoothRuntimeTerminalState.Requested,
            owner.Sink.TerminalState);
        scheduler.Complete();
        Assert.IsFalse(registration.TryStopAndQuiesce(1_000, out _));
        Assert.AreEqual(1, scheduler.ScheduleCount,
            "A timeout retry must wait the retained task, not reserve a second terminal epoch.");
        Assert.AreEqual(Switch2BluetoothRuntimeTerminalState.Delivered,
            owner.Sink.TerminalState);
        Assert.IsTrue(owner.RequiresQuarantine);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2)]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public void TerminalSubscriberReentrantStopRejectsWithoutWaitingOnItself(
        Switch2ControllerModel model)
    {
        FakeLease lease = CreateLease(model, 57);
        Assert.IsTrue(TryCreate(lease, new FixedPumpFactory(new ConfigurablePump()),
            Switch2RuntimeTerminalScheduler.Instance, out var owner,
            out var registration, out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        InputControllerRetirementClaim retirement = default;
        bool reentrantResult = true;
        InputControllerOwnerOperationFailure reentrantFailure = default;
        Switch2BluetoothRuntimeStopFailureKind callbackFailure = default;
        int terminal = 0;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                    Switch2RuntimeReportKind.TerminalNeutral)
            {
                return;
            }
            terminal++;
            reentrantResult = registration.TryStopAndQuiesce(200,
                out reentrantFailure);
            callbackFailure = owner.LastStopFailure.Kind;
            if (table.TryAcquireTerminalReportLease(retirement,
                    (DS4Device)sender, out var reportLease, out _))
            {
                reportLease.TryAcknowledgeTerminalNeutral(out _);
                reportLease.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        Assert.IsTrue(owner.TryArmRetirement(retirement, out _));

        Assert.IsTrue(registration.TryStopAndQuiesce(LifecycleTimeoutMilliseconds,
            out var stopFailure), $"{stopFailure}: {owner.LastStopFailure.Kind}");
        Assert.IsFalse(reentrantResult);
        Assert.AreEqual(InputControllerOwnerOperationFailure.StopRejected,
            reentrantFailure);
        Assert.AreEqual(Switch2BluetoothRuntimeStopFailureKind.CallbackActive,
            callbackFailure, "A terminal callback must not wait for its outer stop's lifecycle operation.");
        Assert.AreEqual(1, terminal);
        Assert.AreEqual(Switch2BluetoothRuntimeTerminalState.Delivered,
            owner.Sink.TerminalState);
        Assert.IsTrue(owner.LeaseReleaseProven);
        Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Stopped, owner.State);
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.ProController2)]
    [DataRow(Switch2ControllerModel.JoyCon2Left)]
    [DataRow(Switch2ControllerModel.JoyCon2Right)]
    public async Task IndependentStopJoinsOtherThreadsTerminalCallback(
        Switch2ControllerModel model)
    {
        FakeLease lease = CreateLease(model, 58);
        Assert.IsTrue(TryCreate(lease, new FixedPumpFactory(new ConfigurablePump()),
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
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                    Switch2RuntimeReportKind.TerminalNeutral)
            {
                return;
            }
            Interlocked.Increment(ref terminal);
            terminalEntered.Set();
            if (!releaseTerminal.Wait(3_000))
            {
                throw new TimeoutException("Test terminal was not released.");
            }
            if (table.TryAcquireTerminalReportLease(retirement,
                    (DS4Device)sender, out var reportLease, out _))
            {
                reportLease.TryAcknowledgeTerminalNeutral(out _);
                reportLease.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out retirement, out _));
        Assert.IsTrue(owner.TryArmRetirement(retirement, out _));
        Task<bool> first = Task.Run(() =>
            registration.TryStopAndQuiesce(3_000, out _));
        Task<bool> second = null;
        try
        {
            Assert.IsTrue(terminalEntered.Wait(1_000));
            second = Task.Run(() =>
            {
                secondStarted.Set();
                return registration.TryStopAndQuiesce(3_000, out _);
            });
            Assert.IsTrue(secondStarted.Wait(1_000));
            Assert.IsFalse(second.Wait(100),
                "An independent stop must wait for the original terminal callback.");
            releaseTerminal.Set();
            Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.IsTrue(await second.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(1, terminal);
            Assert.AreEqual(1, lease.UnsubscribeCount);
            Assert.AreEqual(1, lease.ReleaseWaitCount);
            Assert.IsTrue(owner.LeaseReleaseProven);
            Assert.AreEqual(Switch2BluetoothRuntimeOwnerState.Stopped, owner.State);
        }
        finally
        {
            releaseTerminal.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(4));
            if (second != null)
            {
                await second.WaitAsync(TimeSpan.FromSeconds(4));
            }
        }
    }

    [TestMethod]
    public void PhysicalDisconnectOnlyLatchesUntilExactTableRetirement()
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            53);
        Assert.IsTrue(TryCreate(lease, out var owner, out var registration,
            out _));
        InputControllerRegistrationTable table = OpenTable(1);
        InputControllerSlotToken token = Activate(owner, registration, table);
        InputControllerRetirementClaim claim = default;
        int terminal = 0;
        int attention = 0;
        Switch2BluetoothRuntimeLifecycleAttentionEventArgs evidence = null;
        owner.LifecycleAttention += (_, _) =>
            throw new InvalidOperationException("hostile attention subscriber");
        owner.LifecycleAttention += (_, args) =>
        {
            evidence = args;
            Interlocked.Increment(ref attention);
        };
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                Switch2RuntimeReportKind.TerminalNeutral)
            {
                return;
            }
            Interlocked.Increment(ref terminal);
            if (table.TryAcquireTerminalReportLease(claim,
                    (DS4Device)sender, out var terminalLease, out _))
            {
                terminalLease.TryAcknowledgeTerminalNeutral(out _);
                terminalLease.Dispose();
            }
        };

        lease.Disconnect();
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.Sink.TerminalRequested &&
                Volatile.Read(ref attention) == 1, TimeSpan.FromSeconds(2)));
        Assert.AreEqual(Switch2BluetoothInputEndReason.Disconnected,
            owner.Sink.TerminalReason);
        Assert.AreEqual(Switch2RuntimeInputDeviceState.Active,
            owner.RuntimeDevice.RuntimeState);
        Assert.AreEqual(0, terminal);
        Assert.AreEqual(owner.Model, evidence.Model);
        Assert.AreEqual(DeviceGeneration, evidence.DeviceGeneration);
        Assert.AreEqual(TransportGeneration, evidence.TransportGeneration);
        Assert.AreEqual(Switch2BluetoothInputEndReason.Disconnected,
            evidence.EndReason);

        Assert.IsTrue(table.TryBeginRetire(token, out claim, out _));
        Assert.IsTrue(owner.TryArmRetirement(claim, out _));
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000,
            out var stopFailure), $"{stopFailure}: {owner.LastStopFailure.Kind}");
        Assert.AreEqual(1, terminal);
        Assert.AreEqual(1, attention,
            "Lifecycle attention is preallocated and coalesced once per generation.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InlineBurstSurvivesFactoryCreationWhileDisconnectRemainsSilent(
        bool disconnect)
    {
        FakeLease lease = CreateLease(Switch2ControllerModel.ProController2,
            disconnect ? 55UL : 54UL);
        lease.InlineDisconnect = disconnect;
        lease.InlineNotificationCount = disconnect ? 0 : 5;

        if (!disconnect)
        {
            Assert.IsTrue(TryCreate(lease, out var burstOwner,
                out var burstRegistration, out var burstFailure), burstFailure.Kind.ToString());
            Assert.IsTrue(burstRegistration.IsValid);
            Assert.IsTrue(burstOwner.TryAbortCreated(burstRegistration, 1_000, out var abort), abort.ToString());
            Assert.AreEqual(1, lease.UnsubscribeCount);
            Assert.AreEqual(1, lease.ReleaseWaitCount);
            return;
        }

        Assert.IsFalse(TryCreate(lease, out var owner,
            out var registration, out var failure));
        Assert.IsNull(owner);
        Assert.IsFalse(registration.IsValid);
        Assert.AreEqual(Switch2BluetoothRuntimeCreateFailureKind.
            InputOwnerRejected, failure.Kind);
        Assert.AreEqual(Switch2BluetoothInputStartFailure.
            SubscriptionInterrupted, failure.InputFailure);
        Assert.AreEqual(1, lease.UnsubscribeCount,
            "The input prepare boundary owns one exact compensation request.");
        Assert.AreEqual(1, lease.ReleaseWaitCount);
    }

    [TestMethod]
    public void CrossOwnerAndCrossTableActivationCommitFailClosed()
    {
        FakeLease firstLease = CreateLease(
            Switch2ControllerModel.ProController2, 56);
        FakeLease secondLease = CreateLease(
            Switch2ControllerModel.ProController2, 57);
        Assert.IsTrue(TryCreate(firstLease, out var first,
            out var firstRegistration, out _));
        Assert.IsTrue(TryCreate(secondLease, out var second,
            out var secondRegistration, out _));
        InputControllerRegistrationTable firstTable = OpenTable(1, 30);
        InputControllerRegistrationTable secondTable = OpenTable(1, 40);
        Assert.IsTrue(firstTable.TryReserveAndBind(firstRegistration,
            out var firstToken, out _, out _));
        Assert.IsTrue(secondTable.TryReserveAndBind(secondRegistration,
            out var secondToken, out _, out _));
        Switch2BluetoothRuntimeSlotAdoptionCredential firstAdoption =
            Adopt(first, firstToken);
        Switch2BluetoothRuntimeSlotAdoptionCredential secondAdoption =
            Adopt(second, secondToken);
        Assert.IsTrue(first.TryPrepareActivation(firstAdoption, 1_000,
            out var firstCredential, out _));
        Assert.IsTrue(second.TryPrepareActivation(secondAdoption, 1_000,
            out var secondCredential, out _));
        Assert.IsTrue(firstTable.TryBeginActivate(firstToken,
            out var firstActivation, out _));
        Assert.IsTrue(secondTable.TryBeginActivate(secondToken,
            out var secondActivation, out _));
        InputControllerActivationCommitCredential firstCommit =
            AcquireActivationCommit(firstTable, firstActivation);
        InputControllerActivationCommitCredential secondCommit =
            AcquireActivationCommit(secondTable, secondActivation);

        Assert.IsFalse(first.TryCommitPrepared(secondCredential,
            firstCommit, out var wrongCredential));
        Assert.AreEqual(Switch2BluetoothRuntimeCommitFailure.
            InvalidCredential, wrongCredential);
        Assert.IsFalse(first.TryCommitPrepared(firstCredential,
            secondCommit, out var wrongActivation));
        Assert.AreEqual(Switch2BluetoothRuntimeCommitFailure.
            InvalidActivationCommitCredential, wrongActivation);
        Assert.IsTrue(first.TryCommitPrepared(firstCredential,
            firstCommit, out _));
        Assert.IsTrue(firstTable.TryCompleteActivate(firstCommit, true,
            out _));

        Assert.IsTrue(second.TryAbortPrepared(secondCredential, 1_000,
            out _));
        Assert.IsFalse(secondTable.TryCompleteActivate(secondCommit,
            externalCommitSucceeded: false, out var rejected));
        Assert.AreEqual(InputControllerSlotTableFailure.
            ActivationCommitRejected, rejected);
        StopWithoutRemoving(first, firstRegistration, firstTable, firstToken);
    }

    [TestMethod]
    public void ConcurrentCommitAndAbortConsumeOneExactCredential()
    {
        for (int iteration = 0; iteration < 24; iteration++)
        {
            FakeLease lease = CreateLease(
                Switch2ControllerModel.ProController2,
                (ulong)iteration + 100);
            ConfigurablePump pump = new();
            Assert.IsTrue(TryCreate(lease, new FixedPumpFactory(pump),
                Switch2RuntimeTerminalScheduler.Instance, out var owner,
                out var registration, out _));
            InputControllerRegistrationTable table = OpenTable(1,
                (ulong)iteration + 100);
            Assert.IsTrue(table.TryReserveAndBind(registration,
                out var token, out _, out _));
            Switch2BluetoothRuntimeSlotAdoptionCredential adoption =
                Adopt(owner, token);
            Assert.IsTrue(owner.TryPrepareActivation(adoption, 1_000,
                out var credential, out _));
            Assert.IsTrue(table.TryBeginActivate(token,
                out var activation, out _));
            InputControllerActivationCommitCredential activationCommit =
                AcquireActivationCommit(table, activation);

            using Barrier barrier = new(2);
            bool committed = false;
            bool aborted = false;
            Task commit = Task.Run(() =>
            {
                barrier.SignalAndWait();
                committed = owner.TryCommitPrepared(credential,
                    activationCommit, out _);
            });
            Task abort = Task.Run(() =>
            {
                barrier.SignalAndWait();
                aborted = owner.TryAbortPrepared(credential, 1_000,
                    out _);
            });
            Assert.IsTrue(Task.WaitAll(new[] { commit, abort },
                TimeSpan.FromSeconds(2)));
            Assert.AreNotEqual(committed, aborted,
                "Exactly one single-use composition credential must win.");

            bool completed = table.TryCompleteActivate(activationCommit,
                committed, out var completionFailure);
            Assert.AreEqual(committed, completed);
            if (committed)
            {
                StopWithoutRemoving(owner, registration, table, token);
            }
            else
            {
                Assert.AreEqual(InputControllerSlotTableFailure.
                    ActivationCommitRejected, completionFailure);
                Assert.AreEqual(
                    Switch2BluetoothRuntimeOwnerState.AbortedUnpublished,
                    owner.State);
            }
        }
    }

    private static InputControllerSlotToken Activate(
        Switch2BluetoothRuntimeOwner owner,
        InputControllerRegistration registration,
        InputControllerRegistrationTable table)
    {
        Assert.IsTrue(table.TryReserveAndBind(registration, out var token,
            out _, out var tableFailure), tableFailure.ToString());
        Switch2BluetoothRuntimeSlotAdoptionCredential adoption =
            Adopt(owner, token);
        Assert.IsTrue(owner.TryPrepareActivation(adoption, 1_000,
            out var credential, out var prepareFailure),
            prepareFailure.ToString());
        Assert.IsTrue(table.TryBeginActivate(token, out var activation,
            out tableFailure), tableFailure.ToString());
        InputControllerActivationCommitCredential activationCommit =
            AcquireActivationCommit(table, activation);
        Assert.IsTrue(owner.TryCommitPrepared(credential, activationCommit,
            out var commitFailure), commitFailure.ToString());
        Assert.IsTrue(table.TryCompleteActivate(activationCommit, true,
            out tableFailure), tableFailure.ToString());
        return token;
    }

    private static InputControllerActivationCommitCredential
        AcquireActivationCommit(InputControllerRegistrationTable table,
            in InputControllerActivationClaim activation)
    {
        Assert.IsTrue(table.TryAcquireActivationCommit(activation,
            out var credential, out var failure), failure.ToString());
        Assert.IsTrue(credential.IsValid);
        return credential;
    }

    private static Switch2BluetoothRuntimeSlotAdoptionCredential Adopt(
        Switch2BluetoothRuntimeOwner owner,
        in InputControllerSlotToken token)
    {
        Assert.IsTrue(owner.TryAdoptBoundSlot(token, out var credential,
            out var failure), failure.ToString());
        Assert.IsTrue(credential.IsValid);
        return credential;
    }

    private static void StopWithoutRemoving(
        Switch2BluetoothRuntimeOwner owner,
        InputControllerRegistration registration,
        InputControllerRegistrationTable table,
        InputControllerSlotToken token)
    {
        InputControllerRetirementClaim claim = default;
        owner.RuntimeDevice.Report += (sender, args) =>
        {
            if (((Switch2RuntimeReportEventArgs)args).Kind !=
                Switch2RuntimeReportKind.TerminalNeutral)
            {
                return;
            }
            if (table.TryAcquireTerminalReportLease(claim,
                    (DS4Device)sender, out var terminalLease, out _))
            {
                terminalLease.TryAcknowledgeTerminalNeutral(out _);
                terminalLease.Dispose();
            }
        };
        Assert.IsTrue(table.TryBeginRetire(token, out claim,
            out var tableFailure), tableFailure.ToString());
        Assert.IsTrue(owner.TryArmRetirement(claim, out var armFailure),
            armFailure.ToString());
        Assert.IsTrue(registration.TryStopAndQuiesce(1_000,
            out var stopFailure), $"{stopFailure}: {owner.LastStopFailure.Kind}");
    }

    private static bool TryCreate(FakeLease lease,
        out Switch2BluetoothRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2BluetoothRuntimeCreateFailure failure) => TryCreate(lease,
            Switch2BluetoothRuntimeDrainPumpFactory.Instance,
            Switch2RuntimeTerminalScheduler.Instance, out owner,
            out registration, out failure);

    private static bool TryCreate(FakeLease lease,
        ISwitch2BluetoothRuntimeDrainPumpFactory pumpFactory,
        ISwitch2RuntimeTerminalScheduler scheduler,
        out Switch2BluetoothRuntimeOwner owner,
        out InputControllerRegistration registration,
        out Switch2BluetoothRuntimeCreateFailure failure)
    {
        Assert.IsTrue(Switch2InputCalibrationSnapshot.TryCreateFallback(
            lease.Admission.Model, DeviceGeneration, out var calibration));
        return Switch2BluetoothRuntimeOwner.TryCreateCore(lease.Admission,
            lease, DeviceGeneration, TransportGeneration, QpcFrequency,
            calibration, queueCapacity: 4, LifecycleTimeoutMilliseconds,
            pumpFactory, scheduler, out owner, out registration, out failure);
    }

    private static InputControllerRegistrationTable OpenTable(int slots,
        ulong generation = 1)
    {
        InputControllerRegistrationTable table = new(slots);
        Assert.IsTrue(table.TryOpen(generation, out var failure),
            failure.ToString());
        return table;
    }

    private static FakeLease CreateLease(Switch2ControllerModel model,
        ulong scanGeneration)
    {
        ushort productId = model switch
        {
            Switch2ControllerModel.ProController2 =>
                Switch2AdvertisementCodec.ProController2ProductId,
            Switch2ControllerModel.JoyCon2Left =>
                Switch2AdvertisementCodec.JoyCon2LeftProductId,
            _ => Switch2AdvertisementCodec.JoyCon2RightProductId,
        };
        Switch2BluetoothConnectionAdmission admission = new(scanGeneration,
            model, productId);
        Switch2GattProperty properties = Switch2GattProperty.Read |
            Switch2GattProperty.Notify;
        Switch2BluetoothGattSnapshot gatt = new(scanGeneration, 1, 1,
            Switch2InputCodec.ServiceUuid,
            Switch2InputCodec.Common05CharacteristicUuid, properties);
        return new FakeLease(admission, gatt);
    }

    private sealed class FakeLease : ISwitch2BluetoothInputLease,
        ISwitch2BluetoothInputLeaseReleaseProof,
        ISwitch2BluetoothHdRumbleBindableTransportLease,
        ISwitch2BluetoothDisconnectedOutputProof
    {
        private Switch2BluetoothInputNotification notification;
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

        internal bool ThrowSubscribe { get; set; }

        internal bool InlineDisconnect { get; set; }

        internal int InlineNotificationCount { get; set; }

        internal Switch2BluetoothInputLeaseReleaseResult ReleaseResult
        { get; set; } = Switch2BluetoothInputLeaseReleaseResult.Released;

        internal int SubscribeCount { get; private set; }

        internal int UnsubscribeCount { get; private set; }

        internal int ReleaseWaitCount { get; private set; }

        public bool HasHdRumbleOutput { get; set; }
        internal int FeedbackWriteCount { get; private set; }
        internal bool Disconnected { get; private set; }
        internal bool RejectFeedback { get; set; }
        public bool IsDisconnectedAndReleased(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration) =>
            Disconnected && ReleaseWaitCount > 0 &&
            ReleaseResult == Switch2BluetoothInputLeaseReleaseResult.Released &&
            Authenticates(model, deviceGeneration, transportGeneration);
        public bool TryBindHdRumbleLifetime(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration) =>
            Authenticates(model, deviceGeneration, transportGeneration);
        public bool Authenticates(Switch2ControllerModel model,
            ulong deviceGeneration, ulong transportGeneration) =>
            model == Admission.Model && deviceGeneration == DeviceGeneration &&
            transportGeneration == TransportGeneration;
        public Switch2BluetoothHdRumbleTransportWriteResult TryWritePayload(
            ReadOnlySpan<byte> payload, Switch2ControllerModel expectedModel,
            ulong expectedDeviceGeneration, ulong expectedTransportGeneration)
        {
            FeedbackWriteCount++;
            return Disconnected || RejectFeedback ? Switch2BluetoothHdRumbleTransportWriteResult.Reject(
                expectedModel, expectedDeviceGeneration, expectedTransportGeneration,
                Switch2BluetoothHdRumbleTransportWriteFailure.TransportEnded) :
                Switch2BluetoothHdRumbleTransportWriteResult.Complete(expectedModel,
                    expectedDeviceGeneration, expectedTransportGeneration, payload.Length);
        }

        public bool TrySubscribeCccdNotify(ulong transportGeneration,
            Switch2BluetoothInputNotification notification,
            Switch2BluetoothInputDisconnected disconnected)
        {
            SubscribeCount++;
            this.notification = notification;
            this.disconnected = disconnected;
            for (int index = 0; index < InlineNotificationCount; index++)
            {
                byte[] body = Body((uint)index + 1);
                notification(transportGeneration, Switch2InputCodec.ServiceUuid,
                    Switch2InputCodec.Common05CharacteristicUuid, body,
                    index + 1);
            }
            if (InlineDisconnect)
            {
                disconnected(transportGeneration);
            }
            if (ThrowSubscribe)
            {
                throw new InvalidOperationException("synthetic subscribe fault");
            }
            return SubscribeResult;
        }

        public bool TryUnsubscribeCccdNone(ulong transportGeneration)
        {
            UnsubscribeCount++;
            return transportGeneration == TransportGeneration;
        }

        public Switch2BluetoothInputLeaseReleaseResult WaitForRelease(
            ulong transportGeneration, int timeoutMilliseconds)
        {
            ReleaseWaitCount++;
            return transportGeneration == TransportGeneration &&
                    timeoutMilliseconds >= 0 ? ReleaseResult :
                Switch2BluetoothInputLeaseReleaseResult.Rejected;
        }

        internal void Notify(uint counter, long qpc)
        {
            byte[] body = Body(counter);
            notification?.Invoke(TransportGeneration,
                Switch2InputCodec.ServiceUuid,
                Switch2InputCodec.Common05CharacteristicUuid, body, qpc);
        }

        internal void Disconnect()
        {
            Disconnected = true;
            disconnected?.Invoke(TransportGeneration);
        }
    }

    private sealed class ConfigurablePump : ISwitch2BluetoothRuntimeDrainPump
    {
        internal bool StartResult { get; set; } = true;
        internal bool StopResult { get; set; } = true;
        public Switch2BluetoothInputDrainPumpState State { get; private set; } =
            Switch2BluetoothInputDrainPumpState.Created;
        public Switch2BluetoothInputDrainPumpFailure TerminalFailure =>
            StartResult && StopResult ? default :
                Switch2BluetoothInputDrainPumpFailure.OwnerRejected;
        public bool RequiresQuarantine => !StopResult;
        public bool IsCurrentWorkerThread => false;
        public long PublishedCount => 0;
        public bool TrySetLifecycleAttentionHandler(
            Action<Switch2BluetoothInputDrainPumpAttention> handler) =>
            handler != null;
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
            State = StopResult ? Switch2BluetoothInputDrainPumpState.Stopped :
                Switch2BluetoothInputDrainPumpState.Quarantined;
            failure = StopResult ? default :
                Switch2BluetoothInputDrainPumpFailure.WorkerExitTimedOut;
            return StopResult;
        }
    }

    private sealed class FixedPumpFactory :
        ISwitch2BluetoothRuntimeDrainPumpFactory
    {
        private readonly ISwitch2BluetoothRuntimeDrainPump pump;
        internal FixedPumpFactory(ISwitch2BluetoothRuntimeDrainPump pump) =>
            this.pump = pump;
        public bool TryCreate(Switch2BluetoothInputOwner inputOwner,
            out ISwitch2BluetoothRuntimeDrainPump created,
            out Switch2BluetoothInputDrainPumpFailure failure)
        {
            created = pump;
            failure = default;
            return true;
        }
    }

    private sealed class ThrowingPumpFactory :
        ISwitch2BluetoothRuntimeDrainPumpFactory
    {
        public bool TryCreate(Switch2BluetoothInputOwner inputOwner,
            out ISwitch2BluetoothRuntimeDrainPump pump,
            out Switch2BluetoothInputDrainPumpFailure failure) =>
            throw new InvalidOperationException("synthetic pump fault");
    }

    private sealed class DeferredTerminalScheduler :
        ISwitch2RuntimeTerminalScheduler
    {
        private readonly TaskCompletionSource<
            Switch2TerminalNeutralRequestResult> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<Switch2TerminalNeutralRequestResult> callback;
        internal int ScheduleCount { get; private set; }

        public bool TrySchedule(
            Func<Switch2TerminalNeutralRequestResult> callback,
            out Task<Switch2TerminalNeutralRequestResult> task)
        {
            ScheduleCount++;
            this.callback = callback;
            task = completion.Task;
            return true;
        }

        internal void Complete() => completion.TrySetResult(callback());
    }

    private static byte[] Body(uint counter)
    {
        byte[] body = new byte[Switch2InputCodec.BluetoothLeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, counter);
        return body;
    }
}
