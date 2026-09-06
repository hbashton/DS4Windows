using System.Threading;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Switch2ControlServiceReversibleProfileStagingTests
{
    private GyroOutMode savedGyroMode;
    private Mapping.PostMapStickData savedPostMap;
    private byte savedGyroX, savedGyroY;

    [TestInitialize]
    public void IsolateMappingState()
    {
        savedGyroMode = Global.GyroOutputMode[0];
        savedPostMap = Mapping.mapStickActionData[0];
        savedGyroX = Mapping.gyroStickX[0];
        savedGyroY = Mapping.gyroStickY[0];
        Global.GyroOutputMode[0] = GyroOutMode.None;
        Mapping.mapStickActionData[0] = new Mapping.PostMapStickData();
    }

    [TestCleanup]
    public void RestoreMappingState()
    {
        Global.GyroOutputMode[0] = savedGyroMode;
        Mapping.mapStickActionData[0] = savedPostMap;
        Mapping.gyroStickX[0] = savedGyroX;
        Mapping.gyroStickY[0] = savedGyroY;
    }
    [TestMethod]
    public void ExactMouseStageInstallsAndRemovesOnlyItsOwnSlotInstance()
    {
        Fixture fixture = CreateFixture(41, 51, 61);
        fixture.Controllers[0] = fixture.Device;
        fixture.Device.DeviceSlotNumber = 0;
        var touchPads = new Mouse[1];
        var request = new Switch2ControlServiceProfileStageRequest(
            new object(), fixture.Token);
        var stage = new Switch2ControlServiceExactMouseSlotStage(
            fixture.Table, fixture.LifecycleGate, fixture.Controllers,
            touchPads);

        Switch2ControlServiceExactMouseSlotInverse inverse;
        lock (fixture.LifecycleGate)
        {
            Switch2ControlServiceReversibleStageResult prepared =
                stage.TryPrepare(request, out inverse);
            Assert.IsTrue(prepared.Succeeded);
        }

        Assert.IsNotNull(inverse);
        Assert.AreSame(inverse.Mouse, touchPads[0]);

        lock (fixture.LifecycleGate)
        {
            Switch2ControlServiceReversibleStageResult removed =
                stage.TryUndo(request, inverse);
            Assert.IsTrue(removed.Succeeded);
        }
        Assert.IsNull(touchPads[0]);

        lock (fixture.LifecycleGate)
        {
            Switch2ControlServiceReversibleStageResult replay =
                stage.TryUndo(request, inverse);
            Assert.AreEqual(
                Switch2ControlServiceReversibleStageOutcome.ProvenRejected,
                replay.Outcome);
        }
    }

    [TestMethod]
    public void ExactMouseStageRejectsFactoryFailureWithoutArrayMutation()
    {
        Fixture fixture = CreateFixture(42, 52, 62);
        fixture.Controllers[0] = fixture.Device;
        fixture.Device.DeviceSlotNumber = 0;
        var touchPads = new Mouse[1];
        var request = new Switch2ControlServiceProfileStageRequest(
            new object(), fixture.Token);
        var stage = new Switch2ControlServiceExactMouseSlotStage(
            fixture.Table, fixture.LifecycleGate, fixture.Controllers,
            touchPads, static (_, _) => throw new InvalidOperationException());

        lock (fixture.LifecycleGate)
        {
            Switch2ControlServiceReversibleStageResult result =
                stage.TryPrepare(request, out var inverse);
            Assert.AreEqual(
                Switch2ControlServiceReversibleStageOutcome.ProvenRejected,
                result.Outcome);
            Assert.AreEqual(
                Switch2ControlServiceReversibleStageFailureKind.
                    DependencyThrew,
                result.FailureKind);
            Assert.IsNull(inverse);
        }
        Assert.IsNull(touchPads[0]);
    }

    [TestMethod]
    public void ExactMouseStageRetainsNewerOccupantOnCleanupMismatch()
    {
        Fixture fixture = CreateFixture(43, 53, 63);
        fixture.Controllers[0] = fixture.Device;
        fixture.Device.DeviceSlotNumber = 0;
        var touchPads = new Mouse[1];
        var request = new Switch2ControlServiceProfileStageRequest(
            new object(), fixture.Token);
        var stage = new Switch2ControlServiceExactMouseSlotStage(
            fixture.Table, fixture.LifecycleGate, fixture.Controllers,
            touchPads);

        Switch2ControlServiceExactMouseSlotInverse inverse;
        lock (fixture.LifecycleGate)
        {
            Assert.IsTrue(stage.TryPrepare(request, out inverse).Succeeded);
        }
        Mouse newer = new(0, fixture.Device);
        touchPads[0] = newer;

        lock (fixture.LifecycleGate)
        {
            Switch2ControlServiceReversibleStageResult result =
                stage.TryUndo(request, inverse);
            Assert.AreEqual(
                Switch2ControlServiceReversibleStageOutcome.OutcomeUncertain,
                result.Outcome);
            Assert.AreEqual(
                Switch2ControlServiceReversibleStageFailureKind.SlotChanged,
                result.FailureKind);
        }
        Assert.AreSame(newer, touchPads[0]);
    }

    [TestMethod]
    public void ExactPrepareAndAbortRestoreEveryAdmittedSlotMutation()
    {
        Fixture fixture = CreateFixture(1, 11, 21);
        fixture.Device.DeviceSlotNumber = 7;

        Switch2ControlServiceSlotHostResult prepared =
            fixture.Host.TryPrepare(fixture.Lease);

        Assert.IsTrue(prepared.Succeeded);
        Assert.AreSame(fixture.Device, fixture.Controllers[0]);
        Assert.AreEqual(0, fixture.Device.DeviceSlotNumber);
        AssertManagerContainsExact(fixture.Manager, 0, fixture.Device);
        Assert.IsNotNull(fixture.TouchPads[0]);
        Assert.IsTrue(fixture.Profile.Installed);

        Switch2ControlServiceSlotHostResult aborted =
            fixture.Host.TryAbort(fixture.Lease);

        Assert.IsTrue(aborted.Succeeded);
        Assert.IsNull(fixture.Controllers[0]);
        Assert.AreEqual(7, fixture.Device.DeviceSlotNumber,
            "The exact prior DeviceSlotNumber must be restored.");
        AssertManagerEmpty(fixture.Manager);
        Assert.IsNull(fixture.TouchPads[0]);
        Assert.IsFalse(fixture.Profile.Installed);
    }

    [TestMethod]
    public void CopiedExactLeaseIsIdempotentButForeignTableLeaseIsRejected()
    {
        Fixture fixture = CreateFixture(2, 12, 22);
        Switch2ControlServiceSlotLease copied = fixture.Lease;

        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.Host.TryPrepare(copied).Succeeded);
        Assert.AreEqual(1, fixture.Profile.PrepareCount);
        AssertManagerContainsExact(fixture.Manager, 0, fixture.Device);

        Fixture foreign = CreateFixture(2, 13, 23);
        Switch2ControlServiceSlotHostResult rejected =
            fixture.Host.TryPrepare(foreign.Lease);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.ProvenRejected,
            rejected.Outcome);
        Assert.AreEqual(
            Switch2ControlServiceSlotHostFailureKind.InvalidCredential,
            rejected.FailureKind);
        Assert.AreSame(fixture.Device, fixture.Controllers[0]);
        Assert.IsTrue(fixture.Host.TryAbort(fixture.Lease).Succeeded);
    }

    [TestMethod]
    public void SameSlotNewGenerationRejectsStaleCleanupWithoutTouchingNewOwner()
    {
        Fixture fixture = CreateFixture(3, 14, 24);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.Host.TryAbort(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.Table.TryRollback(fixture.RollbackClaim,
            out _));
        Assert.IsTrue(fixture.Table.TryClose(3, out _, out _));
        Assert.IsTrue(fixture.Table.TryOpen(4, out _));

        Registration second = CreateRegistration(15, 25);
        Assert.IsTrue(fixture.Table.TryReserveAndBind(second.Value,
            out InputControllerSlotToken secondToken,
            out InputControllerSetupRollbackClaim secondRollback, out _));
        var secondLease = new Switch2ControlServiceSlotLease(new object(),
            secondToken);
        Assert.IsTrue(fixture.Host.TryPrepare(secondLease).Succeeded);

        Switch2ControlServiceSlotHostResult stale =
            fixture.Host.TryAbort(fixture.Lease);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.ProvenRejected,
            stale.Outcome);
        Assert.AreSame(second.Device, fixture.Controllers[0]);
        AssertManagerContainsExact(fixture.Manager, 0, second.Device);
        Assert.IsTrue(fixture.Host.TryAbort(secondLease).Succeeded);
        Assert.IsTrue(fixture.Table.TryRollback(secondRollback, out _));
    }

    [TestMethod]
    public void NewerArrayOccupantBlocksCleanupAndAbortRetryUsesRetainedInverse()
    {
        Fixture fixture = CreateFixture(5, 16, 26);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Registration newer = CreateRegistration(17, 27);
        fixture.Controllers[0] = newer.Device;

        Switch2ControlServiceSlotHostResult first =
            fixture.Host.TryAbort(fixture.Lease);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.OutcomeUncertain,
            first.Outcome);
        Assert.AreSame(newer.Device, fixture.Controllers[0],
            "Cleanup must not clear a newer occupant.");
        AssertManagerContainsExact(fixture.Manager, 0, fixture.Device);

        fixture.Controllers[0] = fixture.Device;
        Switch2ControlServiceSlotHostResult retry =
            fixture.Host.TryAbort(fixture.Lease);

        Assert.IsTrue(retry.Succeeded);
        Assert.IsNull(fixture.Controllers[0]);
        AssertManagerEmpty(fixture.Manager);
    }

    [TestMethod]
    public void ProfilePrepareRejectionRollsBackExactSlotBeforeReturning()
    {
        Fixture fixture = CreateFixture(6, 18, 28,
            FakeProfileStage.PrepareMode.ProvenReject);

        Switch2ControlServiceSlotHostResult result =
            fixture.Host.TryPrepare(fixture.Lease);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.ProvenRejected,
            result.Outcome);
        Assert.AreEqual(
            Switch2ControlServiceSlotHostFailureKind.ProfileSetupRejected,
            result.FailureKind);
        Assert.IsNull(fixture.Controllers[0]);
        Assert.AreEqual(DS4Device.DEFAULT_JOINT_SLOT_NUMBER,
            fixture.Device.DeviceSlotNumber);
        AssertManagerEmpty(fixture.Manager);
    }

    [TestMethod]
    public void PartialProfileCleanupIsRetainedAndRetriedBeforeExactSlotUndo()
    {
        Fixture fixture = CreateFixture(7, 19, 29);
        fixture.Profile.CleanupFailuresRemaining = 1;
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);

        Switch2ControlServiceSlotHostResult first =
            fixture.Host.TryAbort(fixture.Lease);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.OutcomeUncertain,
            first.Outcome);
        Assert.IsTrue(fixture.Profile.Installed);
        Assert.AreSame(fixture.Device, fixture.Controllers[0],
            "Earlier exact-slot inverses must remain pending until the later profile inverse succeeds.");
        Assert.IsFalse(fixture.Host.TryPrepare(fixture.Lease).Succeeded,
            "Partial cleanup cannot be reused as a prepared gyro/mapping host.");

        Switch2ControlServiceSlotHostResult retry =
            fixture.Host.TryAbort(fixture.Lease);

        Assert.IsTrue(retry.Succeeded);
        Assert.AreEqual(2, fixture.Profile.UndoCount);
        Assert.IsFalse(fixture.Profile.Installed);
        Assert.IsNull(fixture.Controllers[0]);
        AssertManagerEmpty(fixture.Manager);
    }

    [TestMethod]
    public void ProfileCleanupRetryCannotInvokeOldInverseAgainstReplacementMouse()
    {
        Fixture fixture = CreateFixture(70, 190, 290);
        fixture.Profile.CleanupFailuresRemaining = 1;
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Mouse original = fixture.TouchPads[0];
        Assert.IsFalse(fixture.Host.TryAbort(fixture.Lease).Succeeded);
        Assert.AreEqual(1, fixture.Profile.UndoCount);
        var replacement = new Mouse(0, fixture.Device);
        fixture.TouchPads[0] = replacement;
        var data = Mapping.mapStickActionData[0];
        long replacementEpoch = data.CaptureEpoch();
        Assert.IsTrue(data.TrySubmit(replacementEpoch,
            GyroMouseStickInfo.OutputStick.RightStick, true, true, 150, 100, true));

        Assert.IsFalse(fixture.Host.TryAbort(fixture.Lease).Succeeded);
        Assert.AreEqual(1, fixture.Profile.UndoCount,
            "Reject before an old profile inverse can reset a different Mouse.");
        Assert.AreSame(replacement, fixture.TouchPads[0]);
        Assert.AreEqual(replacementEpoch, data.CaptureEpoch());
        Assert.IsTrue(data.dirty);
        fixture.TouchPads[0] = original;
        Assert.IsTrue(fixture.Host.TryAbort(fixture.Lease).Succeeded);
        Assert.AreEqual(2, fixture.Profile.UndoCount);
        Assert.IsNull(fixture.TouchPads[0]);
    }

    [TestMethod]
    public void ThrowAfterUnownedProfileMutationFailsClosedAndKeepsSlotOccupied()
    {
        Fixture fixture = CreateFixture(8, 20, 30,
            FakeProfileStage.PrepareMode.ThrowAfterMutation);

        Switch2ControlServiceSlotHostResult prepare =
            fixture.Host.TryPrepare(fixture.Lease);
        Switch2ControlServiceSlotHostResult abort =
            fixture.Host.TryAbort(fixture.Lease);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.OutcomeUncertain,
            prepare.Outcome);
        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.OutcomeUncertain,
            abort.Outcome);
        Assert.IsTrue(fixture.Profile.Installed);
        Assert.AreSame(fixture.Device, fixture.Controllers[0],
            "Unknown profile mutation authority must quarantine the exact external slot instead of presenting a false rollback.");
    }

    [TestMethod]
    public void ExistingPipelineReceivesSameRegularAndTerminalEnvelopesSynchronously()
    {
        var probe = new PipelineProbe();
        Fixture fixture = CreateFixture(9, 31, 41,
            FakeProfileStage.PrepareMode.Success, probe.Invoke);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.Table.TryActivate(fixture.Token, out _));
        Assert.IsTrue(fixture.PublishRegular().Succeeded);
        var regular = fixture.LastEnvelope;
        Assert.AreSame(regular, probe.LastReport);
        Assert.AreSame(fixture.Device, probe.LastDevice);
        Assert.AreEqual(0, probe.LastSlot);
        Assert.AreEqual(1, probe.CallCount);

        Assert.IsTrue(fixture.Table.TryBeginRetire(fixture.Token,
            out InputControllerRetirementClaim claim, out _));
        Assert.IsTrue(fixture.PublishTerminal().Succeeded);
        var terminal = fixture.LastEnvelope;
        Assert.AreSame(terminal, probe.LastReport,
            "The host must not allocate or substitute a report wrapper.");
        Assert.AreEqual(2, probe.CallCount);

        AcknowledgeAndQuiesce(fixture, claim);
        Assert.IsTrue(fixture.Host.TryRemove(fixture.Lease).Succeeded);
        Assert.IsNull(fixture.Controllers[0]);
    }

    [TestMethod]
    public void DispatchRejectsAReplacedMouseWithoutInvokingPipeline()
    {
        var probe = new PipelineProbe();
        Fixture fixture = CreateFixture(44, 54, 64,
            FakeProfileStage.PrepareMode.Success, probe.Invoke);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Mouse retained = fixture.TouchPads[0];
        fixture.TouchPads[0] = new Mouse(0, fixture.Device);
        var report = new Switch2RuntimeReportEventArgs(
            Switch2RuntimeReportKind.Regular, 54);

        Switch2ControlServiceSlotHostResult result =
            fixture.Host.TryDispatch(fixture.Lease, fixture.Device, report);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.ProvenRejected,
            result.Outcome);
        Assert.AreEqual(
            Switch2ControlServiceSlotHostFailureKind.CallbackRejected,
            result.FailureKind);
        Assert.AreEqual(0, probe.CallCount);
        fixture.TouchPads[0] = retained;
        Assert.IsTrue(fixture.Host.TryAbort(fixture.Lease).Succeeded);
    }

    [TestMethod]
    public void RemoveRejectsTableAcknowledgementThatBypassedHostTerminal()
    {
        Fixture fixture = CreateFixture(10, 32, 42);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.Table.TryActivate(fixture.Token, out _));
        Assert.IsTrue(fixture.Table.TryBeginRetire(fixture.Token,
            out InputControllerRetirementClaim claim, out _));
        AcknowledgeAndQuiesce(fixture, claim);

        Switch2ControlServiceSlotHostResult removed =
            fixture.Host.TryRemove(fixture.Lease);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.ProvenRejected,
            removed.Outcome);
        Assert.AreEqual(
            Switch2ControlServiceSlotHostFailureKind.TerminalNeutralRejected,
            removed.FailureKind);
        Assert.AreSame(fixture.Device, fixture.Controllers[0]);
        Assert.IsTrue(fixture.Profile.Installed);
    }

    [TestMethod]
    public void TerminalDispatchIsRejectedUntilExactTableLifetimeIsRetiring()
    {
        Fixture fixture = CreateFixture(16, 38, 48);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.Table.TryActivate(fixture.Token, out _));
        var terminal = new Switch2RuntimeReportEventArgs(
            Switch2RuntimeReportKind.TerminalNeutral, 38);

        Switch2ControlServiceSlotHostResult early =
            fixture.Host.TryDispatch(fixture.Lease, fixture.Device, terminal);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.ProvenRejected,
            early.Outcome);
        Assert.AreEqual(
            Switch2ControlServiceSlotHostFailureKind.TerminalNeutralRejected,
            early.FailureKind);
        Assert.IsTrue(fixture.Table.TryBeginRetire(fixture.Token,
            out InputControllerRetirementClaim claim, out _));
        Assert.IsTrue(fixture.PublishTerminal().Succeeded);
        AcknowledgeAndQuiesce(fixture, claim);
        Assert.IsTrue(fixture.Host.TryRemove(fixture.Lease).Succeeded);
    }

    [TestMethod]
    public void PipelineReentrancyIsRejectedWithoutSecondDispatch()
    {
        Fixture fixture = null;
        Switch2ControlServiceSlotHostResult nested = default;
        var probe = new PipelineProbe();
        probe.OnInvoke = () => nested = fixture.Host.TryDispatch(
            fixture.Lease, fixture.Device,
            (Switch2RuntimeReportEventArgs)probe.LastReport);
        fixture = CreateFixture(11, 33, 43,
            FakeProfileStage.PrepareMode.Success, probe.Invoke);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Switch2ControlServiceSlotHostResult outer =
            fixture.PublishRegular();

        Assert.IsTrue(outer.Succeeded);
        Assert.AreEqual(1, probe.CallCount);
        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.ProvenRejected,
            nested.Outcome);
        Assert.AreEqual(
            Switch2ControlServiceSlotHostFailureKind.CallbackRejected,
            nested.FailureKind);
        Assert.IsTrue(fixture.Host.TryAbort(fixture.Lease).Succeeded);
    }

    [TestMethod]
    public void ConcurrentSecondDispatchFailsClosedWhileFirstOwnsPipeline()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var probe = new PipelineProbe
        {
            Entered = entered,
            Release = release,
        };
        Fixture fixture = CreateFixture(12, 34, 44,
            FakeProfileStage.PrepareMode.Success, probe.Invoke);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        var report = new Switch2RuntimeReportEventArgs(
            Switch2RuntimeReportKind.Regular, 34);
        Task<Switch2ControlServiceSlotHostResult> first = Task.Run(() =>
            fixture.PublishRegular());
        Assert.IsTrue(entered.Wait(2_000));

        Switch2ControlServiceSlotHostResult second =
            fixture.Host.TryDispatch(fixture.Lease, fixture.Device, report);
        release.Set();

        Assert.IsTrue(first.GetAwaiter().GetResult().Succeeded);
        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.ProvenRejected,
            second.Outcome);
        Assert.AreEqual(1, probe.CallCount);
        Assert.IsTrue(fixture.Host.TryAbort(fixture.Lease).Succeeded);
    }

    [TestMethod]
    public void ExactDispatchAddsNoManagedAllocationOrReportWrapper()
    {
        var probe = new AllocationPipelineProbe();
        Fixture fixture = CreateFixture(13, 35, 45,
            FakeProfileStage.PrepareMode.Success, probe.Invoke);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.PublishRegular().Succeeded);
        var report = fixture.LastEnvelope;

        const int iterations = 20_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            Switch2ControlServiceSlotHostResult result =
                fixture.PublishRegular();
            if (!result.Succeeded)
            {
                Assert.Fail(result.FailureKind.ToString());
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(iterations + 1, probe.CallCount);
        Assert.AreSame(report, probe.LastReport);
        Assert.IsTrue(fixture.Host.TryAbort(fixture.Lease).Succeeded);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OtherSlotColdProfileWorkDoesNotBlockActiveReportDispatch(bool cleanup)
    {
        using var coldEntered = new ManualResetEventSlim();
        using var releaseCold = new ManualResetEventSlim();
        using var reportsEntered = new ManualResetEventSlim();
        var probe = new PipelineProbe();
        Fixture fixture = CreateFixture(71, 81, 91,
            pipeline: probe.Invoke, slotCount: 2);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.Table.TryActivate(fixture.Token, out _));
        Assert.IsTrue(fixture.PublishRegular().Succeeded);
        Registration other = CreateRegistration(82, 92);
        Assert.IsTrue(fixture.Table.TryReserveAndBind(other.Value,
            out InputControllerSlotToken otherToken, out _, out _));
        Assert.AreEqual(1, otherToken.Slot);
        var otherLease = new Switch2ControlServiceSlotLease(new object(), otherToken);
        if (cleanup)
            Assert.IsTrue(fixture.Host.TryPrepare(otherLease).Succeeded);

        Action blockCold = () =>
        {
            coldEntered.Set();
            if (!releaseCold.Wait(10_000))
                throw new TimeoutException("Test did not release the cold profile operation.");
        };
        if (cleanup) fixture.Profile.OnUndo = blockCold;
        else fixture.Profile.OnPrepare = blockCold;

        Task<Switch2ControlServiceSlotHostResult> cold = Task.Factory.StartNew(
            () => cleanup ? fixture.Host.TryAbort(otherLease) : fixture.Host.TryPrepare(otherLease),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task reports = null;
        bool completedWhileColdBlocked = false;
        try
        {
            Assert.IsTrue(coldEntered.Wait(2_000));
            reports = Task.Factory.StartNew(() =>
            {
                reportsEntered.Set();
                for (int index = 0; index < 256; index++)
                    Assert.IsTrue(fixture.PublishRegular().Succeeded);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.IsTrue(reportsEntered.Wait(2_000));
            completedWhileColdBlocked = reports.Wait(1_000);
        }
        finally
        {
            releaseCold.Set();
            Assert.IsTrue(cold.GetAwaiter().GetResult().Succeeded);
            reports?.GetAwaiter().GetResult();
            fixture.Profile.OnPrepare = null;
            fixture.Profile.OnUndo = null;
            if (!cleanup) Assert.IsTrue(fixture.Host.TryAbort(otherLease).Succeeded);
        }

        Assert.IsTrue(completedWhileColdBlocked,
            "Another slot's profile/output setup or cleanup must not park this controller's input worker.");
        Assert.AreEqual(257, probe.CallCount);
        Assert.AreSame(fixture.Device, fixture.Controllers[0]);
        Assert.IsTrue(fixture.Table.TryBeginRetire(fixture.Token, out var claim, out _));
        Assert.IsTrue(fixture.PublishTerminal().Succeeded);
        AcknowledgeAndQuiesce(fixture, claim);
        Assert.IsTrue(fixture.Host.TryRemove(fixture.Lease).Succeeded);
    }

    [TestMethod]
    public void ProfilePrepareReentrancyCannotConsumePendingExactInverse()
    {
        Fixture fixture = CreateFixture(14, 36, 46);
        Switch2ControlServiceSlotHostResult reentrant = default;
        fixture.Profile.OnPrepare = () => reentrant =
            fixture.Host.TryAbort(fixture.Lease);

        Switch2ControlServiceSlotHostResult prepared =
            fixture.Host.TryPrepare(fixture.Lease);

        Assert.IsTrue(prepared.Succeeded);
        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.OutcomeUncertain,
            reentrant.Outcome);
        Assert.AreSame(fixture.Device, fixture.Controllers[0]);
        Assert.IsTrue(fixture.Host.TryAbort(fixture.Lease).Succeeded);
    }

    [TestMethod]
    public void ProvenRejectionWithInverseIsContradictoryAndNeverInvoked()
    {
        Fixture fixture = CreateFixture(15, 37, 47,
            FakeProfileStage.PrepareMode.ProvenRejectWithInverse);

        Switch2ControlServiceSlotHostResult prepared =
            fixture.Host.TryPrepare(fixture.Lease);
        Switch2ControlServiceSlotHostResult aborted =
            fixture.Host.TryAbort(fixture.Lease);

        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.OutcomeUncertain,
            prepared.Outcome);
        Assert.AreEqual(Switch2ControlServiceSlotHostOutcome.OutcomeUncertain,
            aborted.Outcome);
        Assert.AreEqual(0, fixture.Profile.UndoCount,
            "An inverse contradicting a proven rejection is not authority and must never be invoked.");
        Assert.IsTrue(fixture.Profile.Installed);
        Assert.AreSame(fixture.Device, fixture.Controllers[0]);
    }

    [TestMethod]
    public void ObservedPipelineUsesExactSourceAndRetiresBeforeTerminalMapping()
    {
        int canonicalCalls = 0, fallbackCalls = 0;
        ReportDiagnosticsWorker.Source captured = null;
        ReportDiagnosticsSnapshot delivered = default;
        using var diagnostics = new ReportDiagnosticsWorker(1, snapshot =>
        {
            Assert.AreEqual(1, canonicalCalls, "Mapping precedes optional diagnostics.");
            delivered = snapshot;
        }, startWorker: false);
        diagnostics.Resume();
        Fixture fixture = CreateFixture(91, 92, 93,
            pipeline: (_, _, _) => fallbackCalls++, diagnostics: diagnostics,
            observedPipeline: (device, envelope, slot, source) =>
            {
                canonicalCalls++;
                Assert.IsNotNull(source);
                Assert.AreSame(device, source.Device);
                Assert.AreEqual(slot, source.Controller);
                if (((Switch2RuntimeReportEventArgs)envelope).Kind == Switch2RuntimeReportKind.Regular)
                {
                    captured = source;
                    Assert.IsTrue(source.TryPublish(new ReportDiagnosticsSnapshot { DeviceError = "source error" }));
                }
                else
                {
                    Assert.AreSame(captured, source);
                    Assert.IsFalse(source.IsCurrent);
                    Assert.IsFalse(source.TryPublish(new ReportDiagnosticsSnapshot { DeviceError = "terminal" }));
                }
            });
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.Table.TryActivate(fixture.Token, out _));
        Assert.IsTrue(fixture.PublishRegular().Succeeded);
        Assert.AreEqual(1, diagnostics.DrainOnce());
        Assert.AreSame(captured, delivered.Source);
        Assert.AreSame(fixture.Device, delivered.Device);
        Assert.AreEqual("source error", delivered.DeviceError);
        Assert.IsTrue(fixture.Table.TryBeginRetire(fixture.Token, out var claim, out _));
        Assert.IsTrue(fixture.PublishTerminal().Succeeded);
        Assert.AreEqual(2, canonicalCalls);
        Assert.AreEqual(0, fallbackCalls, "There must be exactly one canonical mapper invocation per report.");
        Assert.AreEqual(0, diagnostics.DrainOnce());
        AcknowledgeAndQuiesce(fixture, claim);
        Assert.IsTrue(fixture.Host.TryRemove(fixture.Lease).Succeeded);
        Assert.AreEqual(0L, fixture.Host.DiagnosticsRegistrationFailureCount);
    }

    [TestMethod]
    public void UnavailableOptionalDiagnosticsDoNotRejectCanonicalPublication()
    {
        using var diagnostics = new ReportDiagnosticsWorker(1, _ => Assert.Fail(), startWorker: false);
        diagnostics.Dispose();
        int calls = 0;
        Fixture fixture = CreateFixture(94, 95, 96, diagnostics: diagnostics,
            observedPipeline: (_, _, _, source) =>
            {
                Assert.IsNull(source);
                calls++;
            });
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.AreEqual(1L, fixture.Host.DiagnosticsRegistrationFailureCount);
        Assert.IsTrue(fixture.Table.TryActivate(fixture.Token, out _));
        Assert.IsTrue(fixture.PublishRegular().Succeeded);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(fixture.Table.TryBeginRetire(fixture.Token, out var claim, out _));
        Assert.IsTrue(fixture.PublishTerminal().Succeeded);
        Assert.AreEqual(2, calls);
        AcknowledgeAndQuiesce(fixture, claim);
        Assert.IsTrue(fixture.Host.TryRemove(fixture.Lease).Succeeded);
    }

    [TestMethod]
    public void ExactHostCleanupCannotRetireSameDeviceSuccessorDiagnostics()
    {
        ReportDiagnosticsWorker.Source original = null;
        using var diagnostics = new ReportDiagnosticsWorker(1, _ => { }, startWorker: false);
        diagnostics.Resume();
        Fixture fixture = CreateFixture(97, 98, 99, diagnostics: diagnostics,
            observedPipeline: (_, _, _, source) => original ??= source);
        Assert.IsTrue(fixture.Host.TryPrepare(fixture.Lease).Succeeded);
        Assert.IsTrue(fixture.Table.TryActivate(fixture.Token, out _));
        Assert.IsTrue(fixture.PublishRegular().Succeeded);
        Assert.IsTrue(original.IsCurrent);
        ReportDiagnosticsWorker.Source successor = diagnostics.Register(0, fixture.Device);
        Assert.IsFalse(original.IsCurrent);
        Assert.IsTrue(successor.IsCurrent);
        Assert.IsTrue(fixture.Table.TryBeginRetire(fixture.Token, out var claim, out _));
        Assert.IsTrue(fixture.PublishTerminal().Succeeded);
        AcknowledgeAndQuiesce(fixture, claim);
        Assert.IsTrue(fixture.Host.TryRemove(fixture.Lease).Succeeded);
        Assert.IsTrue(successor.IsCurrent, "Terminal and cleanup must retire only the StageRecord's captured source.");
        Assert.IsTrue(successor.TryPublish(new ReportDiagnosticsSnapshot { DeviceError = "successor" }));
        Assert.AreEqual(1, diagnostics.DrainOnce());
    }

    private static Fixture CreateFixture(ulong serviceGeneration,
        ulong deviceGeneration, ulong transportGeneration,
        FakeProfileStage.PrepareMode mode =
            FakeProfileStage.PrepareMode.Success,
        Switch2ControlServiceExistingReportPipeline pipeline = null,
        ReportDiagnosticsWorker diagnostics = null,
        Switch2ControlServiceObservedReportPipeline observedPipeline = null,
        int slotCount = 1)
    {
        Registration registration = CreateRegistration(deviceGeneration,
            transportGeneration);
        var table = new InputControllerRegistrationTable(slotCount);
        Assert.IsTrue(table.TryOpen(serviceGeneration, out _));
        Assert.IsTrue(table.TryReserveAndBind(registration.Value,
            out InputControllerSlotToken token,
            out InputControllerSetupRollbackClaim rollbackClaim, out _));
        var controllers = new DS4Device[slotCount];
        var touchPads = new Mouse[slotCount];
        var manager = new ControllerSlotManager();
        var profile = new FakeProfileStage(mode);
        pipeline ??= static (_, _, _) => { };
        var lifecycleGate = new object();
        var host = new Switch2ControlServiceReversibleProfileSlotHost(table,
            lifecycleGate, controllers, touchPads, manager, profile, pipeline,
            diagnosticsWorker: diagnostics, observedPipeline: observedPipeline);
        var lease = new Switch2ControlServiceSlotLease(new object(), token);
        return new Fixture(table, registration.Device, token, rollbackClaim,
            lease, lifecycleGate, controllers, touchPads, manager, profile,
            host, global::DS4WindowsTests.Switch2RuntimeInputDeviceTests.CreateProFrame(
                deviceGeneration, transportGeneration, 0, timestamp: 100_000));
    }

    private static Registration CreateRegistration(ulong deviceGeneration,
        ulong transportGeneration)
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(deviceGeneration,
            transportGeneration, Switch2Transport.Usb, out var device,
            out var createFailure), createFailure.ToString());
        var owner = new FakeOwner(device, deviceGeneration);
        Assert.IsTrue(InputControllerRegistration.TryCreate(device,
            deviceGeneration, InputControllerOwnershipKind.Switch2Runtime,
            hasHidInterface: false, hasPersistentIdentity: false, owner,
            out var registration, out var registrationFailure),
            registrationFailure.ToString());
        return new Registration(device, registration);
    }

    private static void AcknowledgeAndQuiesce(Fixture fixture,
        InputControllerRetirementClaim claim)
    {
        Assert.IsTrue(fixture.Table.TryAcquireTerminalReportLease(claim,
            fixture.Device, out InputControllerReportLease terminal,
            out _));
        using (terminal)
        {
            Assert.IsTrue(terminal.TryAcknowledgeTerminalNeutral(out _));
        }
        Assert.IsTrue(fixture.Table.TryMarkQuiesced(claim, out _));
    }

    private static void AssertManagerContainsExact(
        ControllerSlotManager manager, int slot, DS4Device device)
    {
        using var read = new ReadLocker(manager.CollectionLocker);
        Assert.AreEqual(1, manager.ControllerColl.Count);
        Assert.AreSame(device, manager.ControllerColl[0]);
        Assert.IsTrue(manager.ControllerDict.TryGetValue(slot,
            out DS4Device slotDevice));
        Assert.AreSame(device, slotDevice);
        Assert.IsTrue(manager.ReverseControllerDict.TryGetValue(device,
            out int reverseSlot));
        Assert.AreEqual(slot, reverseSlot);
    }

    private static void AssertManagerEmpty(ControllerSlotManager manager)
    {
        using var read = new ReadLocker(manager.CollectionLocker);
        Assert.AreEqual(0, manager.ControllerColl.Count);
        Assert.AreEqual(0, manager.ControllerDict.Count);
        Assert.AreEqual(0, manager.ReverseControllerDict.Count);
    }

    private sealed record Fixture(InputControllerRegistrationTable Table,
        Switch2RuntimeInputDevice Device, InputControllerSlotToken Token,
        InputControllerSetupRollbackClaim RollbackClaim,
        Switch2ControlServiceSlotLease Lease, object LifecycleGate,
        DS4Device[] Controllers, Mouse[] TouchPads,
        ControllerSlotManager Manager,
        FakeProfileStage Profile,
        Switch2ControlServiceReversibleProfileSlotHost Host,
        Switch2ProProfileInputFrame Frame)
    {
        private bool publicationWired;
        internal Switch2RuntimeReportEventArgs LastEnvelope { get; private set; }
        internal Switch2ControlServiceSlotHostResult LastDispatch { get; private set; }

        private void WirePublication()
        {
            if (publicationWired) return;
            Device.Report += OnReport;
            Device.StartUpdate();
            publicationWired = true;
        }

        private void OnReport(DS4Device sender, EventArgs args)
        {
            LastEnvelope = (Switch2RuntimeReportEventArgs)args;
            LastDispatch = Host.TryDispatch(Lease, sender, LastEnvelope);
        }

        internal Switch2ControlServiceSlotHostResult PublishRegular()
        {
            WirePublication();
            Assert.IsTrue(Device.TryPublishPro(Frame));
            return LastDispatch;
        }

        internal Switch2ControlServiceSlotHostResult PublishTerminal()
        {
            WirePublication();
            Assert.AreEqual(Switch2TerminalNeutralRequestResult.AcceptedCompleted,
                Device.RequestTerminalNeutral());
            return LastDispatch;
        }
    }

    private readonly record struct Registration(
        Switch2RuntimeInputDevice Device,
        InputControllerRegistration Value);

    private sealed class FakeOwner : IInputControllerRegistrationOwner
    {
        private readonly DS4Device device;
        private readonly ulong generation;

        internal FakeOwner(DS4Device device, ulong generation)
        {
            this.device = device;
            this.generation = generation;
        }

        public InputControllerOwnershipKind Kind =>
            InputControllerOwnershipKind.Switch2Runtime;

        public bool Authenticates(DS4Device candidate,
            ulong candidateGeneration) => ReferenceEquals(device, candidate) &&
            generation == candidateGeneration;

        public bool TryStopAndQuiesce(DS4Device candidate,
            ulong candidateGeneration, int timeoutMilliseconds,
            out InputControllerOwnerOperationFailure failure)
        {
            failure = InputControllerOwnerOperationFailure.None;
            return Authenticates(candidate, candidateGeneration);
        }

        public bool TryRemove(DS4Device candidate, ulong candidateGeneration,
            out InputControllerOwnerOperationFailure failure)
        {
            failure = InputControllerOwnerOperationFailure.None;
            return Authenticates(candidate, candidateGeneration);
        }
    }

    private sealed class FakeProfileStage :
        ISwitch2ControlServiceReversibleProfileStage
    {
        internal enum PrepareMode : byte
        {
            Success = 0,
            ProvenReject,
            ProvenRejectWithInverse,
            ThrowAfterMutation,
        }

        private readonly PrepareMode mode;

        internal FakeProfileStage(PrepareMode mode)
        {
            this.mode = mode;
        }

        internal bool Installed { get; private set; }
        internal int PrepareCount { get; private set; }
        internal int UndoCount { get; private set; }
        internal int CleanupFailuresRemaining { get; set; }
        internal Action OnPrepare { get; set; }
        internal Action OnUndo { get; set; }

        public Switch2ControlServiceReversibleStageResult TryPrepare(
            in Switch2ControlServiceProfileStageRequest request,
            out ISwitch2ControlServiceReversibleProfileStageInverse inverse)
        {
            PrepareCount++;
            OnPrepare?.Invoke();
            inverse = null;
            if (mode == PrepareMode.ProvenReject)
            {
                return Switch2ControlServiceReversibleStageResult.Reject(
                    Switch2ControlServiceReversibleStageFailureKind.
                        ProfileSetupRejected);
            }

            Installed = true;
            if (mode == PrepareMode.ProvenRejectWithInverse)
            {
                inverse = new FakeProfileInverse(this, request.Token);
                return Switch2ControlServiceReversibleStageResult.Reject(
                    Switch2ControlServiceReversibleStageFailureKind.
                        ProfileSetupRejected);
            }
            if (mode == PrepareMode.ThrowAfterMutation)
            {
                throw new InvalidOperationException();
            }

            inverse = new FakeProfileInverse(this, request.Token);
            return Switch2ControlServiceReversibleStageResult.Success();
        }

        private sealed class FakeProfileInverse :
            ISwitch2ControlServiceReversibleProfileStageInverse
        {
            private readonly FakeProfileStage owner;
            private readonly InputControllerSlotToken token;
            private bool consumed;

            internal FakeProfileInverse(FakeProfileStage owner,
                InputControllerSlotToken token)
            {
                this.owner = owner;
                this.token = token;
            }

            public bool Authenticates(
                in Switch2ControlServiceProfileStageRequest request) =>
                !consumed && request.Token == token;

            public Switch2ControlServiceReversibleStageResult TryUndo(
                in Switch2ControlServiceProfileStageRequest request)
            {
                owner.UndoCount++;
                owner.OnUndo?.Invoke();
                if (!Authenticates(request))
                {
                    return Switch2ControlServiceReversibleStageResult.Reject(
                        Switch2ControlServiceReversibleStageFailureKind.
                            InvalidCredential);
                }
                if (owner.CleanupFailuresRemaining > 0)
                {
                    owner.CleanupFailuresRemaining--;
                    return Switch2ControlServiceReversibleStageResult.
                        Uncertain(
                            Switch2ControlServiceReversibleStageFailureKind.
                                CleanupRejected);
                }

                owner.Installed = false;
                consumed = true;
                return Switch2ControlServiceReversibleStageResult.Success();
            }
        }
    }

    private sealed class PipelineProbe
    {
        internal int CallCount { get; private set; }
        internal DS4Device LastDevice { get; private set; }
        internal EventArgs LastReport { get; private set; }
        internal int LastSlot { get; private set; }
        internal Action OnInvoke { get; set; }
        internal ManualResetEventSlim Entered { get; set; }
        internal ManualResetEventSlim Release { get; set; }

        internal void Invoke(DS4Device device, EventArgs report, int slot)
        {
            CallCount++;
            LastDevice = device;
            LastReport = report;
            LastSlot = slot;
            OnInvoke?.Invoke();
            Entered?.Set();
            Release?.Wait(2_000);
        }
    }

    private sealed class AllocationPipelineProbe
    {
        internal int CallCount { get; private set; }
        internal EventArgs LastReport { get; private set; }

        internal void Invoke(DS4Device device, EventArgs report, int slot)
        {
            CallCount++;
            LastReport = report;
        }
    }
}
