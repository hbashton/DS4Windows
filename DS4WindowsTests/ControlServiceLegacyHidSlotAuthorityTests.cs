using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
public sealed class ControlServiceLegacyHidSlotAuthorityTests
{
    [TestMethod]
    public void ExactLegacyProfileTargetRequiresAttachedTableAndRejectsRetirement()
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        var device = new DS4Device(hid, "Profile target identity test");
        var service = (ControlService)RuntimeHelpers.GetUninitializedObject(typeof(ControlService));
        service.DS4Controllers = new DS4Device[] { device };
        var authority = new ControlServiceLegacyHidSlotAuthority(1, _ => { }, new FakeWorkerLifecycle());
        try
        {
            Assert.AreEqual(DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid, device.WorkerLifecycleSupport);
            Assert.IsFalse(ControllerProfileActionTarget.TryCapture(service, null, 0, device, out _));
            Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
            Assert.IsFalse(ControllerProfileActionTarget.TryCapture(service, authority.Table, 0, device, out _));
            var binding = Attach(authority, device);
            Assert.IsTrue(ControllerProfileActionTarget.TryCapture(service, authority.Table, 0, device, out var target));
            Assert.IsTrue(target.TryAcquire(out var action));
            Assert.IsTrue(action.IsValid, "A supported legacy source must not take the untabled path.");
            action.Dispose();
            Assert.IsTrue(authority.TryBeginRetirement(binding, out _, out _));
            Assert.IsFalse(target.TryAcquire(out _));
            Assert.IsFalse(ControllerProfileActionTarget.TryCapture(service, authority.Table, 0, device, out _));
            Assert.IsTrue(authority.TryPublishTerminalNeutral(binding, () => { }, 1000, out _, out _));
            Assert.IsTrue(authority.TryFinalizeRetirement(binding, 1000, out _, out _));
        }
        finally { device.ReadWaitEv.Dispose(); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RetirementPresentationWaitsForProfileActionAndQuarantinesPartialFailure(bool throws)
    {
        var worker = new FakeWorkerLifecycle();
        int removals = 0;
        var authority = new ControlServiceLegacyHidSlotAuthority(1, _ => removals++, worker);
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        FakeDevice device = CreateDevice();
        var binding = Attach(authority, device);
        Assert.IsTrue(authority.Table.TryAcquireActionLease(binding.Token, 0, out var action, out _));
        int presentationCalls = 0;
        int neutralCalls = 0;
        void PresentThenNeutral()
        {
            presentationCalls++;
            if (throws)
                throw new InvalidOperationException("Synthetic partial presentation teardown");
            neutralCalls++;
        }
        try
        {
            Assert.IsTrue(authority.TryBeginRetirement(binding, out _, out _));
            Assert.IsFalse(authority.TryPublishTerminalNeutral(binding, PresentThenNeutral, 0,
                out _, out _));
            Assert.AreEqual(0, presentationCalls);
            Assert.AreEqual(0, worker.StopCalls);
            Assert.AreEqual(0, removals);
        }
        finally { action.Dispose(); }
        Assert.AreEqual(!throws, authority.TryPublishTerminalNeutral(binding, PresentThenNeutral, 0,
            out var failure, out _));
        Assert.AreEqual(1, presentationCalls);
        if (throws)
        {
            Assert.AreEqual(ControlServiceLegacyHidSlotFailure.DependencyThrew, failure);
            Assert.AreEqual(ControlServiceLegacyHidSlotState.Quarantined, binding.State);
            Assert.IsFalse(authority.TryPublishTerminalNeutral(binding, PresentThenNeutral, 0, out _, out _));
            Assert.IsTrue(authority.TryRecoverQuarantinedActivation(binding, 1000, out _));
            Assert.IsTrue(authority.TryRecoverQuarantinedActivation(binding, 1000, out _));
            Assert.AreEqual(InputControllerSlotState.Quarantined, authority.Table.GetSnapshot()[0].State);
            Assert.AreEqual(0, neutralCalls);
        }
        else
        {
            Assert.IsTrue(authority.TryPublishTerminalNeutral(binding, PresentThenNeutral, 0, out _, out _));
            Assert.IsTrue(authority.TryFinalizeRetirement(binding, 1000, out _, out _));
            Assert.AreEqual(1, neutralCalls);
        }
        Assert.AreEqual(1, presentationCalls, "Terminal completion/recovery must not replay the callback.");
        Assert.AreEqual(1, worker.StopCalls);
        Assert.AreEqual(1, removals);
    }

    [TestMethod]
    public void SharedTableConstructorUsesExactControlServiceTable()
    {
        var table = new InputControllerRegistrationTable(2);
        var authority = new ControlServiceLegacyHidSlotAuthority(table,
            _ => { }, new FakeWorkerLifecycle());
        Assert.AreSame(table, authority.Table);
        Assert.IsTrue(authority.TryOpenNext(out ulong generation,
            out var failure, out var tableFailure),
            $"{failure}/{tableFailure}");
        Assert.AreEqual(generation, table.CurrentServiceGeneration);
        Assert.IsTrue(authority.TryClose(out _, out failure,
            out tableFailure), $"{failure}/{tableFailure}");
    }

    [TestMethod]
    public void ExactBindingAdmitsReportsAndRetiresWithExactDelegates()
    {
        var worker = new FakeWorkerLifecycle();
        int registryRemovals = 0;
        var authority = new ControlServiceLegacyHidSlotAuthority(2,
            _ => registryRemovals++, worker);
        Assert.IsTrue(authority.TryOpenNext(out ulong serviceGeneration,
            out var failure, out var tableFailure),
            $"{failure}/{tableFailure}");
        Assert.AreEqual(1UL, serviceGeneration);
        FakeDevice device = CreateDevice();
        Assert.IsTrue(authority.TryBindExactSlot(1, device,
            hasPersistentIdentity: false, out var binding, out failure,
            out tableFailure), $"{failure}/{tableFailure}");

        int removalCalls = 0;
        Assert.IsTrue(authority.TrySubscribeLegacyLifecycle(binding,
            (_, _) => removalCalls++, (_, _) => { }, (_, _) => { },
            (_, _) => { }, (_, _) => { }, out failure), failure.ToString());
        int reports = 0;
        DS4Device.ReportHandler<EventArgs> report = (sender, args) =>
        {
            if (!authority.TryAcquireReport(binding, sender, out var lease,
                    out _))
            {
                return;
            }
            using (lease)
            {
                reports++;
            }
        };
        Assert.IsTrue(authority.TrySubscribeReport(binding, report,
            out failure), failure.ToString());
        Assert.IsTrue(authority.TryStartAndActivate(binding, out failure,
            out tableFailure, out var workerResult),
            $"{failure}/{tableFailure}/{workerResult.FailureKind}");

        device.RaiseReport();
        Assert.AreEqual(1, reports);
        Assert.AreEqual(InputControllerSlotState.Attached,
            authority.Table.GetSnapshot()[1].State);
        Assert.IsTrue(authority.TryBeginRetirement(binding, out failure,
            out tableFailure), $"{failure}/{tableFailure}");
        int terminalNeutral = 0;
        Assert.IsTrue(authority.TryPublishTerminalNeutral(binding,
            () => terminalNeutral++, 1_000, out failure, out tableFailure),
            $"{failure}/{tableFailure}");
        Assert.IsTrue(authority.TryFinalizeRetirement(binding, 1_000,
            out failure, out tableFailure), $"{failure}/{tableFailure}");

        Assert.AreEqual(1, terminalNeutral);
        Assert.AreEqual(1, worker.StartCalls);
        Assert.AreEqual(1, worker.StopCalls);
        Assert.AreEqual(1, registryRemovals);
        Assert.AreEqual(ControlServiceLegacyHidSlotState.Removed,
            binding.State);
        Assert.AreEqual(InputControllerSlotState.Removed,
            authority.Table.GetSnapshot()[1].State);
        device.RaiseReport();
        device.RaiseRemoval();
        Assert.AreEqual(1, reports, "The exact report delegate was removed.");
        Assert.AreEqual(0, removalCalls,
            "The exact removal delegate was removed before registry removal.");
    }

    [TestMethod]
    public void StaleGenerationCannotDetachOrRetireNewSlotOccupant()
    {
        var worker = new FakeWorkerLifecycle();
        var authority = new ControlServiceLegacyHidSlotAuthority(1,
            _ => { }, worker);
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        FakeDevice first = CreateDevice();
        ControlServiceLegacyHidSlotBinding stale = Attach(authority, first);
        Assert.IsTrue(authority.TryBeginRetirement(stale, out _, out _));
        Assert.IsTrue(authority.TryPublishTerminalNeutral(stale, () => { },
            1_000, out _, out _));
        Assert.IsTrue(authority.TryFinalizeRetirement(stale, 1_000,
            out _, out _));
        Assert.IsTrue(authority.TryClose(out _, out _, out _));

        Assert.IsTrue(authority.TryOpenNext(out ulong nextGeneration,
            out _, out _));
        Assert.AreEqual(2UL, nextGeneration);
        FakeDevice next = CreateDevice();
        ControlServiceLegacyHidSlotBinding current = Attach(authority, next);
        int currentReports = 0;
        // Attach installs one admitted report; add an observable exact
        // replacement only after proving the stale record is rejected.
        Assert.IsFalse(authority.TryDetachExactHandlers(stale, out _));
        Assert.IsFalse(authority.TryBeginRetirement(stale, out _, out _));
        Assert.IsTrue(authority.TryAcquireReport(current, next,
            out var lease, out _));
        using (lease)
        {
            currentReports++;
        }
        Assert.AreEqual(1, currentReports);
        Assert.AreSame(next, current.Device);
        Assert.AreNotEqual(stale.ConnectionGeneration,
            current.ConnectionGeneration);
        Assert.AreNotEqual(stale.ServiceGeneration,
            current.ServiceGeneration);
    }

    [TestMethod]
    public void ActiveReportDrainBlocksTerminalUntilExactLeaseReleases()
    {
        var authority = new ControlServiceLegacyHidSlotAuthority(1,
            _ => { }, new FakeWorkerLifecycle());
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        FakeDevice device = CreateDevice();
        ControlServiceLegacyHidSlotBinding binding = Attach(authority, device);
        Assert.IsTrue(authority.TryAcquireReport(binding, device,
            out var activeLease, out _));
        Assert.IsTrue(authority.TryBeginRetirement(binding, out _, out _));

        Assert.IsFalse(authority.TryPublishTerminalNeutral(binding, () => { },
            0, out var failure, out var tableFailure));
        Assert.AreEqual(
            ControlServiceLegacyHidSlotFailure.TerminalNeutralRejected,
            failure);
        Assert.AreEqual(InputControllerSlotTableFailure.TimedOut,
            tableFailure);
        activeLease.Dispose();
        Assert.IsTrue(authority.TryPublishTerminalNeutral(binding, () => { },
            1_000, out failure, out tableFailure),
            $"{failure}/{tableFailure}");
        Assert.IsTrue(authority.TryFinalizeRetirement(binding, 1_000,
            out failure, out tableFailure), $"{failure}/{tableFailure}");
    }

    [TestMethod]
    public void FailedWorkerStartQuarantinesSlotWithoutFallbackOrReuse()
    {
        var worker = new FakeWorkerLifecycle
        {
            StartResult = DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Start,
                DS4DeviceWorkerLifecycleFailureKind.StartRejected),
        };
        var authority = new ControlServiceLegacyHidSlotAuthority(1,
            _ => { }, worker);
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        FakeDevice device = CreateDevice();
        Assert.IsTrue(authority.TryBindExactSlot(0, device, false,
            out var binding, out _, out _));
        Assert.IsTrue(authority.TrySubscribeLegacyLifecycle(binding,
            (_, _) => { }, (_, _) => { }, (_, _) => { }, (_, _) => { },
            (_, _) => { }, out _));
        Assert.IsTrue(authority.TrySubscribeReport(binding, (_, _) => { },
            out _));

        Assert.IsFalse(authority.TryStartAndActivate(binding,
            out var failure, out var tableFailure, out var workerResult));
        Assert.AreEqual(
            ControlServiceLegacyHidSlotFailure.WorkerStartRejected, failure);
        Assert.AreEqual(
            DS4DeviceWorkerLifecycleFailureKind.StartRejected,
            workerResult.FailureKind);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            authority.Table.GetSnapshot()[0].State);
        Assert.IsFalse(authority.TryBindExactSlot(0, CreateDevice(), false,
            out _, out var secondFailure, out tableFailure));
        Assert.AreEqual(ControlServiceLegacyHidSlotFailure.SlotOccupied,
            secondFailure);
        Assert.AreEqual(1, worker.StartCalls);
        Assert.AreEqual(0, worker.StopCalls);
    }

    [TestMethod]
    public void UncertainStartRetainsWorkerLeaseForBoundedRecovery()
    {
        var worker = new FakeWorkerLifecycle
        {
            StartResult = DS4DeviceWorkerLifecycleResult.Uncertain(
                DS4DeviceWorkerLifecycleOperation.Start,
                DS4DeviceWorkerLifecycleFailureKind.PartialStart),
            PublishLeaseOnStartFailure = true,
        };
        int registryRemovals = 0;
        var authority = new ControlServiceLegacyHidSlotAuthority(1,
            _ => registryRemovals++, worker);
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        FakeDevice device = CreateDevice();
        Assert.IsTrue(authority.TryBindExactSlot(0, device, false,
            out var binding, out _, out _));
        Assert.IsTrue(authority.TrySubscribeLegacyLifecycle(binding,
            (_, _) => { }, (_, _) => { }, (_, _) => { }, (_, _) => { },
            (_, _) => { }, out _));
        Assert.IsTrue(authority.TrySubscribeReport(binding, (_, _) => { },
            out _));

        Assert.IsFalse(authority.TryStartAndActivate(binding, out _, out _,
            out var result));
        Assert.AreEqual(DS4DeviceWorkerLifecycleFailureKind.PartialStart,
            result.FailureKind);
        Assert.IsTrue(binding.WorkerLease.IsValid,
            "Uncertain start must retain its exact cleanup lease.");
        Assert.IsTrue(authority.TryRecoverQuarantinedActivation(binding,
            1_000, out var recoveryFailure), recoveryFailure.ToString());
        Assert.AreEqual(1, worker.StopCalls);
        Assert.AreEqual(1, registryRemovals);
        Assert.IsTrue(binding.QuarantinedCleanupProven);
        Assert.AreEqual(0, device.ReportSubscriberCount);
        Assert.AreEqual(0, device.RemovalSubscriberCount);
        Assert.AreEqual(InputControllerSlotState.Quarantined,
            authority.Table.GetSnapshot()[0].State,
            "Cleanup proof must not make a quarantined slot reusable.");
    }

    [TestMethod]
    public void FailedRetirementRetainsExplicitBoundedRetryPath()
    {
        var worker = new FakeWorkerLifecycle();
        worker.StopResults.Enqueue(
            DS4DeviceWorkerLifecycleResult.Reject(
                DS4DeviceWorkerLifecycleOperation.Stop,
                DS4DeviceWorkerLifecycleFailureKind.WrongState));
        worker.StopResults.Enqueue(
            DS4DeviceWorkerLifecycleResult.Success(
                DS4DeviceWorkerLifecycleOperation.Stop));
        int registryRemovals = 0;
        var authority = new ControlServiceLegacyHidSlotAuthority(1,
            _ => registryRemovals++, worker);
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        FakeDevice device = CreateDevice();
        ControlServiceLegacyHidSlotBinding binding = Attach(authority, device);
        Assert.IsTrue(authority.TryBeginRetirement(binding, out _, out _));
        Assert.IsTrue(authority.TryPublishTerminalNeutral(binding, () => { },
            1_000, out _, out _));

        Assert.IsFalse(authority.TryFinalizeRetirement(binding, 1_000,
            out var failure, out _));
        Assert.AreEqual(
            ControlServiceLegacyHidSlotFailure.WorkerStopRejected, failure);
        Assert.AreEqual(ControlServiceLegacyHidSlotState.Quarantined,
            binding.State);
        Assert.IsTrue(binding.TerminalNeutralPublished);
        Assert.AreEqual(1, worker.StopCalls);
        Assert.AreEqual(0, registryRemovals);

        Assert.IsTrue(authority.TryRecoverQuarantinedActivation(binding,
            1_000, out failure), failure.ToString());
        Assert.AreEqual(2, worker.StopCalls,
            "The retained exact lease must drive the bounded retry.");
        Assert.AreEqual(1, registryRemovals);
        Assert.IsTrue(binding.QuarantinedCleanupProven);
    }

    [TestMethod]
    public void OutcomeUncertainStopUsesRetainedWorkerLeaseForRecovery()
    {
        var worker = new FakeWorkerLifecycle();
        worker.StopResults.Enqueue(
            DS4DeviceWorkerLifecycleResult.Uncertain(
                DS4DeviceWorkerLifecycleOperation.Stop,
                DS4DeviceWorkerLifecycleFailureKind.StopTimedOut));
        worker.StopResults.Enqueue(
            DS4DeviceWorkerLifecycleResult.Success(
                DS4DeviceWorkerLifecycleOperation.Stop));
        int registryRemovals = 0;
        var authority = new ControlServiceLegacyHidSlotAuthority(1,
            _ => registryRemovals++, worker);
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        ControlServiceLegacyHidSlotBinding binding = Attach(authority,
            CreateDevice());
        Assert.IsTrue(authority.TryBeginRetirement(binding, out _, out _));
        Assert.IsTrue(authority.TryPublishTerminalNeutral(binding, () => { },
            1_000, out _, out _));

        Assert.IsFalse(authority.TryFinalizeRetirement(binding, 1_000,
            out var failure, out _));
        Assert.AreEqual(
            ControlServiceLegacyHidSlotFailure.WorkerStopRejected, failure);
        Assert.AreEqual(LegacyHidInputControllerOwnerState.Quarantined,
            binding.Owner.State,
            "An outcome-uncertain owner cannot be re-entered through its " +
            "ordinary registration state machine.");
        Assert.AreEqual(1, worker.StopCalls);

        Assert.IsTrue(authority.TryRecoverQuarantinedActivation(binding,
            1_000, out failure), failure.ToString());
        Assert.AreEqual(2, worker.StopCalls,
            "Recovery must use the retained exact worker lease directly.");
        Assert.AreEqual(1, registryRemovals);
        Assert.IsTrue(binding.QuarantinedWorkerStopProven);
        Assert.IsTrue(binding.QuarantinedCleanupProven);
    }

    [TestMethod]
    public void RemovedOwnerAfterTableFailureDoesNotReplayExternalCleanup()
    {
        var worker = new FakeWorkerLifecycle();
        int registryRemovals = 0;
        bool tableQuarantined = false;
        ControlServiceLegacyHidSlotAuthority authority = null;
        ControlServiceLegacyHidSlotBinding capturedBinding = null;
        authority = new ControlServiceLegacyHidSlotAuthority(1, _ =>
        {
            registryRemovals++;
            tableQuarantined = authority.Table.TryQuarantine(
                capturedBinding.RetirementClaim,
                InputControllerSlotQuarantineReason.
                    ExternalLifecycleFailure, out var ignoredFailure);
        }, worker);
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        capturedBinding = Attach(authority, CreateDevice());
        Assert.IsTrue(authority.TryBeginRetirement(capturedBinding,
            out _, out _));
        Assert.IsTrue(authority.TryPublishTerminalNeutral(capturedBinding,
            () => { }, 1_000, out _, out _));

        Assert.IsFalse(authority.TryFinalizeRetirement(capturedBinding,
            1_000, out var failure, out _));
        Assert.IsTrue(tableQuarantined,
            "The injected table transition must reject completion only " +
            "after registry removal succeeded.");
        Assert.AreEqual(ControlServiceLegacyHidSlotFailure.TableRejected,
            failure);
        Assert.AreEqual(LegacyHidInputControllerOwnerState.Removed,
            capturedBinding.Owner.State);
        Assert.IsTrue(capturedBinding.RegistryRemoved);
        Assert.AreEqual(1, worker.StopCalls);
        Assert.AreEqual(1, registryRemovals);

        Assert.IsTrue(authority.TryRecoverQuarantinedActivation(
            capturedBinding, 1_000, out failure), failure.ToString());
        Assert.AreEqual(1, worker.StopCalls,
            "A Removed owner is already proven worker-quiesced.");
        Assert.AreEqual(1, registryRemovals,
            "A Removed owner must not replay exact registry removal.");
        Assert.IsTrue(capturedBinding.QuarantinedCleanupProven);
    }

    [TestMethod]
    public void ProductionClassifierRejectsUnknownSubtypeBeforeTableMutation()
    {
        var authority = new ControlServiceLegacyHidSlotAuthority(1,
            _ => { });
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        FakeDevice device = CreateDevice();

        Assert.IsFalse(authority.TryBindExactSlot(0, device, false, out _,
            out var failure, out _));
        Assert.AreEqual(ControlServiceLegacyHidSlotFailure.UnsupportedDevice,
            failure);
        Assert.AreEqual(InputControllerSlotState.Empty,
            authority.Table.GetSnapshot()[0].State);
        Assert.AreEqual(0, device.ReportSubscriberCount);
        Assert.AreEqual(0, device.RemovalSubscriberCount);
    }

    [TestMethod]
    public void ThrowingMotionAddRetainsExactPossiblyInstalledInverse()
    {
        var authority = new ControlServiceLegacyHidSlotAuthority(1,
            _ => { }, new FakeWorkerLifecycle());
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        FakeDevice device = CreateDevice();
        Assert.IsTrue(authority.TryBindExactSlot(0, device, false,
            out var binding, out _, out _));
        device.ThrowAfterReportAdd = true;
        DS4Device.ReportHandler<EventArgs> motion = (_, _) => { };

        Assert.IsFalse(authority.TryReplaceMotionHandler(binding, motion,
            subscribe: true, out var failure));
        Assert.AreEqual(ControlServiceLegacyHidSlotFailure.DependencyThrew,
            failure);
        Assert.AreSame(motion, binding.MotionHandler);
        Assert.IsTrue(binding.MotionSubscribed,
            "An accessor may install before throwing; retain the inverse.");
        Assert.AreEqual(1, device.ReportSubscriberCount);

        device.ThrowAfterReportAdd = false;
        Assert.IsTrue(authority.TryDetachExactHandlers(binding, out failure),
            failure.ToString());
        Assert.AreEqual(0, device.ReportSubscriberCount);
        Assert.IsFalse(binding.MotionSubscribed);
    }

    [TestMethod]
    public void ConcurrentDetachWaitsForReportSubscriptionMutation()
    {
        var authority = new ControlServiceLegacyHidSlotAuthority(1,
            _ => { }, new FakeWorkerLifecycle());
        Assert.IsTrue(authority.TryOpenNext(out _, out _, out _));
        FakeDevice device = CreateDevice();
        Assert.IsTrue(authority.TryBindExactSlot(0, device, false,
            out var binding, out _, out _));
        using var addEntered = new ManualResetEventSlim();
        using var releaseAdd = new ManualResetEventSlim();
        device.ReportAddEntered = addEntered;
        device.ReleaseReportAdd = releaseAdd;

        Task<bool> subscribe = Task.Run(() =>
            authority.TrySubscribeReport(binding, (_, _) => { }, out _));
        Assert.IsTrue(addEntered.Wait(2_000));
        Task<bool> detach = Task.Run(() =>
            authority.TryDetachExactHandlers(binding, out _));
        Assert.IsFalse(detach.Wait(50),
            "Cleanup must serialize behind the exact accessor mutation.");
        releaseAdd.Set();
        Assert.IsTrue(subscribe.Result);
        Assert.IsTrue(detach.Result);
        Assert.AreEqual(0, device.ReportSubscriberCount);
        Assert.IsFalse(binding.ReportSubscribed);
    }

    private static ControlServiceLegacyHidSlotBinding Attach(
        ControlServiceLegacyHidSlotAuthority authority, DS4Device device)
    {
        Assert.IsTrue(authority.TryBindExactSlot(0, device, false,
            out var binding, out var failure, out var tableFailure),
            $"{failure}/{tableFailure}");
        Assert.IsTrue(authority.TrySubscribeLegacyLifecycle(binding,
            (_, _) => { }, (_, _) => { }, (_, _) => { }, (_, _) => { },
            (_, _) => { }, out failure), failure.ToString());
        DS4Device.ReportHandler<EventArgs> report = (sender, args) =>
        {
            if (authority.TryAcquireReport(binding, sender, out var lease,
                    out _))
            {
                lease.Dispose();
            }
        };
        Assert.IsTrue(authority.TrySubscribeReport(binding, report,
            out failure), failure.ToString());
        Assert.IsTrue(authority.TryStartAndActivate(binding, out failure,
            out tableFailure, out var result),
            $"{failure}/{tableFailure}/{result.FailureKind}");
        return binding;
    }

    private static FakeDevice CreateDevice()
    {
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(
            typeof(HidDevice));
        return new FakeDevice(hid);
    }

    private sealed class FakeDevice : DS4Device
    {
        private ReportHandler<EventArgs> report;
        private EventHandler<EventArgs> removal;

        internal FakeDevice(HidDevice hidDevice) : base(hidDevice,
            "Exact slot authority test")
        {
            Mac = "01:02:03:04:05:06";
        }

        public override event ReportHandler<EventArgs> Report
        {
            add
            {
                report += value;
                ReportAddEntered?.Set();
                ReleaseReportAdd?.Wait(2_000);
                if (ThrowAfterReportAdd)
                {
                    throw new InvalidOperationException();
                }
            }
            remove => report -= value;
        }

        public override event EventHandler<EventArgs> Removal
        {
            add => removal += value;
            remove => removal -= value;
        }

        internal int ReportSubscriberCount =>
            report?.GetInvocationList().Length ?? 0;

        internal int RemovalSubscriberCount =>
            removal?.GetInvocationList().Length ?? 0;

        internal bool ThrowAfterReportAdd { get; set; }

        internal ManualResetEventSlim ReportAddEntered { get; set; }

        internal ManualResetEventSlim ReleaseReportAdd { get; set; }

        internal void RaiseReport() => report?.Invoke(this, EventArgs.Empty);

        internal void RaiseRemoval() => removal?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeWorkerLifecycle :
        IControlServiceLegacyHidWorkerLifecycle
    {
        private readonly object issuer = new();
        private ulong generation;

        internal DS4DeviceWorkerLifecycleResult StartResult { get; set; } =
            DS4DeviceWorkerLifecycleResult.Success(
                DS4DeviceWorkerLifecycleOperation.Start);

        internal DS4DeviceWorkerLifecycleResult StopResult { get; set; } =
            DS4DeviceWorkerLifecycleResult.Success(
                DS4DeviceWorkerLifecycleOperation.Stop);

        internal bool PublishLeaseOnStartFailure { get; set; }

        internal Queue<DS4DeviceWorkerLifecycleResult> StopResults { get; } =
            new();

        internal int StartCalls { get; private set; }

        internal int StopCalls { get; private set; }

        public DS4DeviceWorkerLifecycleSupport Classify(DS4Device device) =>
            DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid;

        public bool TryStart(DS4Device device,
            out DS4DeviceWorkerLifecycleLease lease,
            out DS4DeviceWorkerLifecycleResult result)
        {
            StartCalls++;
            result = StartResult;
            lease = result.Succeeded || PublishLeaseOnStartFailure ?
                new DS4DeviceWorkerLifecycleLease(issuer, device,
                    ++generation) : default;
            return result.Succeeded;
        }

        public bool TryStop(DS4Device device,
            in DS4DeviceWorkerLifecycleLease lease, int timeoutMilliseconds,
            out DS4DeviceWorkerLifecycleResult result)
        {
            StopCalls++;
            result = StopResults.Count == 0 ? StopResult :
                StopResults.Dequeue();
            return result.Succeeded;
        }
    }
}
