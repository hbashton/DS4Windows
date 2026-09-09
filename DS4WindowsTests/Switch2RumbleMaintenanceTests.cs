using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2RumbleMaintenanceTests
{
    [TestMethod]
    public void ClosedPhysicalFeedbackParksWorkerDespiteRetainedLocalStopIntent()
    {
        var lease = new BluetoothLease();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, 7, 11, out var lifetime));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(7, 11, Switch2Transport.BluetoothLe,
            out var runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(Switch2ControllerModel.ProController2, 7, 11, lifetime));
        runtime.StartUpdate();
        try
        {
            Assert.IsTrue(lifetime.TryStopAndRetire(3));
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var pendingField = typeof(Switch2RuntimeInputDevice).GetField("pendingPreviewRumble", flags)!;
            object pending = pendingField.GetValue(runtime);
            pendingField.FieldType.GetField("Withdraw", flags)!.SetValue(pending, true);
            pendingField.SetValue(runtime, pending);
            var service = (Func<ulong, bool>)typeof(Switch2RuntimeInputDevice).GetMethod("ServiceRumbleMaintenance", flags)!
                .CreateDelegate(typeof(Func<ulong, bool>), runtime);
            var worker = new Switch2RumbleMaintenanceWorker(service, 15, automaticTimer: false);
            try
            {
                worker.Wake();
                worker.RunScheduledTick(1000);
                Assert.IsFalse(worker.IsScheduled, "A closed feedback lifetime must not be kept alive by an undrainable local intent.");
                Assert.IsTrue((bool)pendingField.FieldType.GetField("Withdraw", flags)!
                    .GetValue(pendingField.GetValue(runtime)), "Parking does not falsify completion or erase the Stop intent.");
            }
            finally { worker.Stop(); }
        }
        finally { runtime.StopUpdate(); }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void LocalStopIntentNeverWaitsForMaintenanceAndPrecedesTheNextApply(int successor)
    {
        var lease = new BluetoothLease();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, 7, 11, out var lifetime));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(7, 11, Switch2Transport.BluetoothLe,
            out var runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(Switch2ControllerModel.ProController2, 7, 11, lifetime));
        runtime.StartUpdate();
        try
        {
            runtime.SetRumblePreview(true, 100, false, 0);
            lease.Block = true;
            Assert.IsTrue(lease.Entered.Wait(TimeSpan.FromSeconds(2)));
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object localGate = typeof(Switch2RuntimeInputDevice).GetField("localFeedbackGate", flags)!.GetValue(runtime);
            var withdraw = typeof(Switch2RuntimeInputDevice).GetMethod("TryWithdrawLocalRumbleNoLock", flags)!;
            Task<bool> queued = Task.Run(() =>
            {
                lock (localGate)
                    return (bool)withdraw.Invoke(runtime, new object[] { ControllerFeedbackPublicationOrigin.TestPreview });
            });
            Assert.IsTrue(queued.Wait(TimeSpan.FromSeconds(1)), "A local stop waited for physical output.");
            Assert.IsTrue(queued.Result, "A queued local Stop intent must not be confused with a physical receipt.");
            if (successor == 1) runtime.SetRumblePreview(true, 30, false, 0);
            if (successor == 2) Assert.IsTrue(runtime.TryStartIdentificationHaptic());
            lease.Block = false;
            lease.Release.Set();
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                byte[][] packets = lease.PayloadSnapshot();
                int stopIndex = Array.FindIndex(packets, IsSilent);
                return stopIndex >= 0 && (successor == 0 || Array.FindIndex(packets, stopIndex + 1,
                    packet => !IsSilent(packet)) > stopIndex);
            }, TimeSpan.FromSeconds(2)), "The actual output did not preserve Stop before the successor Apply.");
            if (successor == 0)
                Assert.IsTrue(SpinWait.SpinUntil(() => !lifetime.RequiresRumbleMaintenance, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            lease.Block = false;
            lease.Release.Set();
            runtime.StopUpdate();
            Assert.IsTrue(SpinWait.SpinUntil(() => lifetime.TryStopAndRetire(3), TimeSpan.FromSeconds(2)));
        }
    }

    [TestMethod]
    public void ZeroStrengthApplyDoesNotProduceSilentKeepAliveTraffic()
    {
        var writer = new Writer();
        var sink = new Switch2HdRumbleDeliverySink(writer, 7, 11);
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(0, out var silent));
        Assert.IsTrue(sink.TrySelectConfiguration(Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            Switch2HdRumbleImpulseTuning.Default, silent, out _));
        var delivery = Delivery(ControllerFeedbackSource.Xbox360VirtualDevice, 40000, 30000);
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.IsFalse(sink.NeedsSustainedRefresh);
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(1, writer.Calls);
        Assert.IsTrue(sink.TrySelectConfiguration(Switch2HdRumbleFeedbackPolicy.SdlBodyOnlyCompatibility,
            Switch2HdRumbleImpulseTuning.Default, Switch2HdRumbleBodyTuning.Default, out bool changed));
        Assert.IsTrue(changed);
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.IsTrue(sink.NeedsSustainedRefresh);
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(3, writer.Calls, "Unmuting must restore both actual output and its keepalives.");
    }

    [TestMethod]
    public void RealDormantWorkerSustainsUserPreviewBeyondInitialLeaseAndStopsCleanly()
    {
        var lease = new BluetoothLease { SignalSustainAfterMicroseconds = 300000 };
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, 7, 11, out var lifetime));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(7, 11, Switch2Transport.BluetoothLe,
            out var runtime, out _));
        Assert.IsTrue(runtime.TryAttachBluetoothFeedbackLifetime(Switch2ControllerModel.ProController2, 7, 11, lifetime));
        runtime.StartUpdate();
        try
        {
            Assert.AreEqual(0, lease.Payloads.Count, "An idle runtime must not write rumble.");
            runtime.SetRumblePreview(true, 100, false, 0);
            Assert.IsTrue(lease.Sustained.Wait(TimeSpan.FromSeconds(2)),
                "The actual worker did not sustain the held UI value beyond its initial250ms lease.");
            runtime.ClearRumblePreview();
        }
        finally { runtime.StopUpdate(); }
        Assert.IsTrue(SpinWait.SpinUntil(() => lifetime.TryStopAndRetire(3), TimeSpan.FromSeconds(2)));
        int writes = lease.Payloads.Count;
        Thread.Sleep(40);
        Assert.AreEqual(writes, lease.Payloads.Count, "A stopped worker replayed stale feedback.");
    }

    [TestMethod]
    public void OuterBluetoothRetirementDoesNotWaitForTheVirtualSessionGate()
    {
        var lease = new BluetoothLease();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, 7, 11, out var lifetime));
        Assert.IsTrue(lifetime.TryActivate());
        Assert.IsTrue(lifetime.TryCreateVirtualFeedbackSession(ControllerFeedbackSource.XboxOneVirtualDevice, out var session));
        object gate = typeof(Switch2VirtualFeedbackSession).GetField("gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task held = Task.Run(() => { lock (gate) { entered.Set(); Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(3))); } });
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
            Task<bool> retire = Task.Run(() => lifetime.TryStopAndRetire(1));
            Assert.IsTrue(retire.Wait(TimeSpan.FromSeconds(1)), "Outer retirement waited for a busy session gate.");
            Assert.IsFalse(retire.Result);
        }
        finally { release.Set(); Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(3))); }
        Assert.IsTrue(lifetime.TryStopAndRetire(3));
    }

    [TestMethod]
    public void UncertainRichKeepAliveRetryRetainsItsExactGroupsThroughOrdinaryPump()
    {
        var writer = new Writer();
        var sink = new Switch2HdRumbleDeliverySink(writer, 7, 11);
        var delivery = Delivery(ControllerFeedbackSource.Xbox360VirtualDevice, 1, 1);
        var left = new Switch2HdRumbleGroup(new(101, 11, 201, 21), new(102, 12, 202, 22), new(103, 13, 203, 23));
        var right = new Switch2HdRumbleGroup(new(301, 31, 401, 41), new(302, 32, 402, 42), new(303, 33, 403, 43));
        Assert.IsTrue(sink.TryStageSourcePreservedSynthesis(delivery.Frame,
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2TestPreview, left, right));
        Assert.IsTrue(sink.TryDeliver(delivery));
        var exact = writer.Last;
        writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(Switch2HdRumblePhysicalWriteFailure.TransportRejected);
        Assert.IsFalse(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(exact, writer.Last);
        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(exact, writer.Last, "An ordinary retry must not flatten an uncertain maintenance submission.");
    }

    [TestMethod]
    public void BluetoothKeepAliveUsesActualWriterAndNextPacketCounterThenRetires()
    {
        var lease = new BluetoothLease();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, 7, 11, out var lifetime));
        Assert.IsTrue(lifetime.TryActivate());
        Assert.IsTrue(lifetime.TryCreateLane(ControllerFeedbackPublicationOrigin.TestPreview,
            ControllerFeedbackSource.Xbox360VirtualDevice, 19, 250000, 100000, out var lane));
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(10000, 20000, 0, 0), now));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, lifetime.TryPumpOnce(now, out _));
        byte[] first = lease.Payloads[0];
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, lifetime.TryServiceRumbleMaintenance(now, _ => { }));
        Assert.AreEqual(2, lease.Payloads.Count);
        Assert.AreEqual((first[1] + 1) & 15, lease.Payloads[1][1] & 15);
        CollectionAssert.AreEqual(first.AsSpan(2, 15).ToArray(), lease.Payloads[1].AsSpan(2, 15).ToArray());
        Assert.IsTrue(lifetime.TryStopAndRetire(3));
        int stoppedCount = lease.Payloads.Count;
        Assert.AreEqual(ControllerFeedbackPumpDisposition.None, lifetime.TryServiceRumbleMaintenance(now, _ => Assert.Fail("Retired lease renewed.")));
        Assert.AreEqual(stoppedCount, lease.Payloads.Count);
        Assert.IsFalse(lifetime.RequiresRumbleMaintenance);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BluetoothInputDrainAndRetirementNeverWaitForMaintenancePhysicalWrite(bool withVirtualSession)
    {
        var lease = new BluetoothLease();
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease,
            Switch2ControllerModel.ProController2, 7, 11, out var lifetime));
        Assert.IsTrue(lifetime.TryActivate());
        if (withVirtualSession)
            Assert.IsTrue(lifetime.TryCreateVirtualFeedbackSession(ControllerFeedbackSource.XboxOneVirtualDevice, out _));
        Assert.IsTrue(lifetime.TryCreateLane(ControllerFeedbackPublicationOrigin.TestPreview,
            ControllerFeedbackSource.Xbox360VirtualDevice, 19, 250000, 100000, out var lane));
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(10000, 20000, 0, 0), now));
        lifetime.TryPumpOnce(now, out _);
        lease.Block = true;
        Task maintenance = Task.Run(() => lifetime.TryServiceRumbleMaintenance(now, _ => { }));
        try
        {
            Assert.IsTrue(lease.Entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Busy, lifetime.TryPumpOnce(now, out _));
            Assert.IsFalse(lifetime.TryPublishNativePreviewAndPump(lane,
                new ControllerFeedbackActuatorState(1, 1, 0, 0), default, default));
            Task<bool> stop = Task.Factory.StartNew(() => lifetime.TryStopAndRetire(1),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.IsTrue(stop.Wait(TimeSpan.FromSeconds(1)), "Bounded retirement waited for maintenance I/O.");
            Assert.IsFalse(stop.Result);
        }
        finally
        {
            lease.Block = false;
            lease.Release.Set();
            Assert.IsTrue(maintenance.Wait(TimeSpan.FromSeconds(3)));
        }
        Assert.IsTrue(lifetime.TryStopAndRetire(3));
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void RuntimeRenewsHeldLocalStateButNeverExtendsFiniteCues(bool preview, bool cue)
    {
        Assert.IsTrue(ControllerFeedbackStateLanePump.TryCreate(7, 11, out var pump));
        var origin = preview ? ControllerFeedbackPublicationOrigin.TestPreview : ControllerFeedbackPublicationOrigin.ProfileEffect;
        Assert.IsTrue(pump.TryCreateLane(origin, ControllerFeedbackSource.Xbox360VirtualDevice,
            19, 250000, 100000, out var lane));
        Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(100, 200, 0, 0), 1000));
        var sink = new CountingSink();
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(1000, sink, out _));
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(7, 11, Switch2Transport.Usb,
            out var runtime, out _));
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(Switch2RuntimeInputDevice).GetField(preview ? "previewFeedbackLane" : "profileFeedbackLane", fields)!
            .SetValue(runtime, lane);
        typeof(Switch2RuntimeInputDevice).GetField(preview ? "identificationHapticOwnsPreviewLane" : "connectionHapticOwnsProfileLane", fields)!
            .SetValue(runtime, cue);
        typeof(Switch2RuntimeInputDevice).GetField(preview ? "previewRumbleHeld" : "profileRumbleHeld", fields)!
            .SetValue(runtime, !cue);
        runtime.RenewLocalRumbleLeases(101000);
        runtime.RenewLocalRumbleLeases(201000);
        runtime.RenewLocalRumbleLeases(301000);
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(301000, allowNoFrame: true, applyOnly: true));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(301000, sink, out var current));
        Assert.AreEqual(cue ? ControllerFeedbackDeliveryDisposition.Stop : ControllerFeedbackDeliveryDisposition.Frame,
            current.Disposition);
        if (!cue)
        {
            Assert.AreEqual((ushort)100, current.Frame.BodyLow);
            Assert.AreEqual((ushort)200, current.Frame.BodyHigh);
            Assert.AreEqual(250000UL, current.Frame.TimeToLiveMicroseconds);
        }
    }

    [TestMethod]
    public void MissingTheLocalLeaseDeadlineStillStopsInsteadOfResurrectingState()
    {
        Assert.IsTrue(ControllerFeedbackStateLanePump.TryCreate(7, 11, out var pump));
        Assert.IsTrue(pump.TryCreateLane(ControllerFeedbackPublicationOrigin.TestPreview,
            ControllerFeedbackSource.Xbox360VirtualDevice, 19, 250000, 100000, out var lane));
        Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(1, 2, 0, 0), 1000));
        var sink = new CountingSink();
        pump.PumpOnce(1000, sink, out _);
        Assert.AreEqual(ControllerFeedbackLeaseServiceDisposition.StopRequested, lane.ServiceLease(251000));
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(251000, allowNoFrame: true, applyOnly: true));
        pump.PumpOnce(251000, sink, out var stopped);
        Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop, stopped.Disposition);
        Assert.IsFalse(pump.RequiresOutputMaintenance);
        Assert.AreEqual(ControllerFeedbackLeaseServiceDisposition.Inactive, lane.ServiceLease(301000));
    }

    [TestMethod]
    public void ExternalExpiryAndCompletedNeutralParkMaintenanceWithoutNeutralSpam()
    {
        Assert.IsTrue(ControllerFeedbackStateLanePump.TryCreate(7, 11, out var pump));
        Assert.IsTrue(pump.TryCreateBrokerIngress(ControllerFeedbackSource.XboxOneVirtualDevice, 19, out var ingress));
        var sink = new CountingSink();
        Assert.IsTrue(ingress.TryPublish(new ControllerFeedbackActuatorState(10, 20, 0, 0), 1000, 250000));
        pump.PumpOnce(1000, sink, out _);
        Assert.IsTrue(pump.RequiresOutputMaintenance);
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(251000, allowNoFrame: true, applyOnly: true));
        pump.PumpOnce(251000, sink, out var stop);
        Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop, stop.Disposition);
        Assert.IsFalse(pump.RequiresOutputMaintenance);
        Assert.IsTrue(ingress.TryPublish(default(ControllerFeedbackActuatorState), 252000, 250000));
        pump.PumpOnce(252000, sink, out var neutral);
        Assert.AreEqual(ControllerFeedbackCommand.Neutral, neutral.Frame.Command);
        Assert.IsFalse(pump.RequiresOutputMaintenance);
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(252001, allowNoFrame: true, applyOnly: true));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.None, pump.PumpOnce(252001, sink, out _));
        Assert.AreEqual(3, sink.Calls);
    }

    [TestMethod]
    public void WorkerCoalescesWakeParksOnIdleAndCannotRestartAfterStop()
    {
        int calls = 0;
        var worker = new Switch2RumbleMaintenanceWorker(_ => { calls++; return false; }, 15, automaticTimer: false);
        Assert.IsFalse(worker.IsScheduled);
        worker.RunScheduledTick(1);
        Assert.AreEqual(0, calls);
        for (int index = 0; index < 100; index++) worker.Wake();
        Assert.IsTrue(worker.IsScheduled);
        worker.RunScheduledTick(2);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(worker.IsScheduled);
        worker.Stop();
        worker.Wake();
        worker.RunScheduledTick(3);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(worker.IsStopped);
    }

    [TestMethod]
    public void WorkerNeverOverlapsAndStopDoesNotWaitForAdmittedOutput()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var worker = new Switch2RumbleMaintenanceWorker(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(3)));
            return true;
        }, 12, automaticTimer: false);
        worker.Wake();
        Task first = Task.Run(() => worker.RunScheduledTick(1));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
            worker.Wake();
            worker.RunScheduledTick(2);
            worker.Stop();
            Assert.IsTrue(worker.IsStopped);
            Assert.AreEqual(1, calls);
        }
        finally { release.Set(); Assert.IsTrue(first.Wait(TimeSpan.FromSeconds(3))); }
        Assert.IsFalse(worker.IsScheduled);
    }

    [TestMethod]
    public void WarmMaintenanceSchedulingAllocatesNothing()
    {
        var worker = new Switch2RumbleMaintenanceWorker(_ => true, 15, automaticTimer: false);
        worker.Wake();
        for (int index = 0; index < 1000; index++) worker.RunScheduledTick((ulong)index);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10000; index++)
        {
            worker.Wake();
            worker.RunScheduledTick((ulong)index);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        worker.Stop();
        Assert.AreEqual(0L, allocated);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HeldHeavyOrLightRumbleIsRePresentedWithoutChangingItsLease(bool heavy)
    {
        var writer = new Writer();
        var sink = new Switch2HdRumbleDeliverySink(writer, 7, 11);
        var delivery = Delivery(ControllerFeedbackSource.Xbox360VirtualDevice,
            heavy ? (ushort)50000 : (ushort)0, heavy ? (ushort)0 : (ushort)50000);
        Assert.IsTrue(sink.TryDeliver(delivery));
        var first = writer.Last;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls, "Finite HD packets require physical refresh, not only lease renewal.");
        Assert.AreEqual(first, writer.Last, "Keepalive must not change intensity, routing, sequence or expiry.");
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls, "Ordinary duplicate delivery remains idempotent.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeHeldGroupsKeepTheirExactThreeFramesAndIndependentSides(bool preview)
    {
        var writer = new Writer();
        var sink = new Switch2HdRumbleDeliverySink(writer, 7, 11);
        var delivery = Delivery(ControllerFeedbackSource.Xbox360VirtualDevice, 1, 1);
        var left = new Switch2HdRumbleGroup(new(101, 11, 201, 21), new(102, 12, 202, 22), new(103, 13, 203, 23));
        var right = new Switch2HdRumbleGroup(new(301, 31, 401, 41), new(302, 32, 402, 42), new(303, 33, 403, 43));
        var fidelity = preview ? Switch2HdRumbleFeedbackFidelity.NativeSwitch2TestPreview :
            Switch2HdRumbleFeedbackFidelity.NativeSwitch2ProfileEffect;
        Assert.IsTrue(sink.TryStageSourcePreservedSynthesis(delivery.Frame, fidelity, left, right));
        Assert.IsTrue(sink.TryDeliver(delivery));
        var first = writer.Last;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls);
        Assert.AreEqual(first, writer.Last, "Native groups must not become the lossy canonical marker on refresh.");
    }

    [TestMethod]
    public void PcmStreamPacketIsNotLoopedWhenTheSourceStopsSendingSamples()
    {
        var writer = new Writer();
        var sink = new Switch2HdRumbleDeliverySink(writer, 7, 11);
        var delivery = Delivery(ControllerFeedbackSource.DualSenseVirtualDevice, 1, 1);
        var group = new Switch2HdRumbleGroup(new(100, 20, 200, 30), new(100, 10, 200, 20), new(100, 0, 200, 0));
        Assert.IsTrue(sink.TryStageSourcePreservedSynthesis(delivery.Frame,
            Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand, group, group));
        Assert.IsTrue(sink.TryDeliver(delivery));
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(1, writer.Calls, "A finite PCM transient is not a latched body-rumble state.");
        Assert.IsFalse(sink.NeedsSustainedRefresh);
    }

    [TestMethod]
    public void StopCannotBeReplacedByAnOldKeepAlive()
    {
        var writer = new Writer();
        var sink = new Switch2HdRumbleDeliverySink(writer, 7, 11);
        var delivery = Delivery(ControllerFeedbackSource.Xbox360VirtualDevice, 1, 1);
        Assert.IsTrue(sink.TryDeliver(delivery));
        var stop = new ControllerFeedbackDelivery(ControllerFeedbackDeliveryDisposition.Stop,
            delivery.Origin, default, 7, 11, delivery.DeliveryEpoch);
        Assert.IsTrue(sink.TryDeliver(stop));
        Assert.IsFalse(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls);
    }

    private static ControllerFeedbackDelivery Delivery(ControllerFeedbackSource source, ushort low, ushort high)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(source, ControllerFeedbackCommand.Apply,
            ControllerFeedbackActuators.All, low, high, 0, 0, 1, 7, 11, 19, now, 250000, out var frame));
        return new ControllerFeedbackDelivery(ControllerFeedbackDeliveryDisposition.Frame,
            ControllerFeedbackPublicationOrigin.TestPreview, frame, 7, 11, 31);
    }

    private static bool IsSilent(byte[] packet)
    {
        Assert.IsTrue(Switch2BluetoothHdRumbleCodec.TryDecodeProController(packet, out _, out var left, out var right, out _));
        return !left.First.HasNonzeroAmplitude && !left.Second.HasNonzeroAmplitude && !left.Third.HasNonzeroAmplitude &&
            !right.First.HasNonzeroAmplitude && !right.Second.HasNonzeroAmplitude && !right.Third.HasNonzeroAmplitude;
    }

    private sealed class Writer : ISwitch2HdRumblePhysicalWriter
    {
        internal int Calls;
        internal Switch2HdRumblePhysicalSubmission Last;
        internal Switch2HdRumblePhysicalWriteResult Result = Switch2HdRumblePhysicalWriteResult.Success();
        public bool Authenticates(ulong device, ulong transport) => device == 7 && transport == 11;
        public Switch2HdRumblePhysicalWriteResult TryWrite(in Switch2HdRumblePhysicalSubmission submission)
        {
            Calls++;
            Last = submission;
            return Result;
        }
    }

    private sealed class CountingSink : IControllerFeedbackDeliverySink
    {
        internal int Calls;
        public bool TryDeliver(in ControllerFeedbackDelivery delivery) { Calls++; return true; }
    }

    private sealed class BluetoothLease : ISwitch2BluetoothHdRumbleBindableTransportLease
    {
        internal readonly List<byte[]> Payloads = new();
        private readonly object payloadGate = new();
        internal byte[][] PayloadSnapshot() { lock (payloadGate) return Payloads.ToArray(); }
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        internal readonly ManualResetEventSlim Sustained = new();
        internal ulong SignalSustainAfterMicroseconds;
        private ulong firstWriteMicroseconds;
        internal volatile bool Block;
        public bool HasHdRumbleOutput => true;
        public bool TryBindHdRumbleLifetime(Switch2ControllerModel model, ulong device, ulong transport) =>
            Authenticates(model, device, transport);
        public bool Authenticates(Switch2ControllerModel model, ulong device, ulong transport) =>
            model == Switch2ControllerModel.ProController2 && device == 7 && transport == 11;
        public Switch2BluetoothHdRumbleTransportWriteResult TryWritePayload(ReadOnlySpan<byte> payload,
            Switch2ControllerModel model, ulong device, ulong transport)
        {
            if (Block)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(3)))
                    return Switch2BluetoothHdRumbleTransportWriteResult.Uncertain(model, device, transport,
                        Switch2BluetoothHdRumbleTransportWriteFailure.TimedOut);
            }
            lock (payloadGate) Payloads.Add(payload.ToArray());
            if (ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now))
            {
                if (firstWriteMicroseconds == 0) firstWriteMicroseconds = now;
                if (SignalSustainAfterMicroseconds != 0 && now - firstWriteMicroseconds >= SignalSustainAfterMicroseconds)
                    Sustained.Set();
            }
            return Switch2BluetoothHdRumbleTransportWriteResult.Complete(model, device, transport, payload.Length);
        }
    }
}
