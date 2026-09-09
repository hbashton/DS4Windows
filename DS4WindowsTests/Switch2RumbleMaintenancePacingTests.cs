using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2RumbleMaintenancePacingTests
{
    [DataTestMethod]
    [DataRow(12)]
    [DataRow(15)]
    public void PreWriteContentionRecoversAfterOneMillisecondNotAnotherFullPeriod(int interval)
    {
        var worker = new Switch2RumbleMaintenanceWorker(_ => Switch2RumbleMaintenanceResult.Contended,
            interval, automaticTimer: false);
        try
        {
            worker.Wake();
            int delay = worker.RunScheduledTick((ulong)interval * 1000, (ulong)interval * 1000);
            Assert.AreEqual(1, delay);
            Assert.AreEqual(interval + 1, interval + delay, "One collision must yield 13/16 ms, not 24/30 ms.");
        }
        finally { worker.Stop(); }
    }

    [DataTestMethod]
    [DataRow(12)]
    [DataRow(15)]
    public void PersistentContentionBacksOffAfterThreeFastRetries(int interval)
    {
        var worker = new Switch2RumbleMaintenanceWorker(_ => Switch2RumbleMaintenanceResult.Contended,
            interval, automaticTimer: false);
        try
        {
            worker.Wake();
            for (int index = 0; index < 8; index++)
            {
                worker.Wake(); // Coalesced input wake must not reset the burst limit.
                Assert.AreEqual(index < 3 ? 1 : interval, worker.RunScheduledTick(1000, 1000));
            }
        }
        finally { worker.Stop(); }
    }

    [DataTestMethod]
    [DataRow(12)]
    [DataRow(15)]
    public void AttemptedOrUncertainWriteRetainsFullRetryDelayAfterCompletion(int interval)
    {
        var worker = new Switch2RumbleMaintenanceWorker(_ => Switch2RumbleMaintenanceResult.RetryPending,
            interval, automaticTimer: false);
        try
        {
            worker.Wake();
            Assert.AreEqual(interval, worker.RunScheduledTick(1000, 9000));
        }
        finally { worker.Stop(); }
    }

    [TestMethod]
    public void ActiveUsesAbsoluteDueAndCeilingWithoutDoubleSubtractingServiceTime()
    {
        var worker = new Switch2RumbleMaintenanceWorker(_ => Switch2RumbleMaintenanceResult.Active(12001),
            15, automaticTimer: false);
        try
        {
            worker.Wake();
            Assert.AreEqual(3, worker.RunScheduledTick(1000, 10000));
            Assert.AreEqual(1, worker.RunScheduledTick(13000, 13000), "An overdue tick schedules once, never a catch-up burst.");
        }
        finally { worker.Stop(); }
    }

    [TestMethod]
    public void ServiceExceptionsRetryAtNormalCadenceAndStillStop()
    {
        int calls = 0;
        var worker = new Switch2RumbleMaintenanceWorker(_ =>
        {
            if (++calls != 2) throw new InvalidOperationException("Synthetic service failure");
            return Switch2RumbleMaintenanceResult.Active(50000);
        }, 15, automaticTimer: false);
        worker.Wake();
        Assert.AreEqual(15, worker.RunScheduledTick(1000, 9000));
        Assert.AreEqual(1, worker.FailureCount);
        Assert.AreEqual(15, worker.RunScheduledTick(24000, 24000));
        Assert.AreEqual(1, worker.FailureCount);
        for (int index = 0; index < 10; index++)
            Assert.AreEqual(15, worker.RunScheduledTick(30000, 30000));
        Assert.AreEqual(11, worker.FailureCount);
        worker.Stop();
        Assert.AreEqual(0, worker.RunScheduledTick(50000, 50000));
        Assert.IsTrue(worker.IsStopped);
    }

    [TestMethod]
    public void FailedAdmissionClockCannotLeaveAnOverdueOneMillisecondRetryLoop()
    {
        ulong clock = 1000;
        var writer = new Writer();
        var sink = CreateSink(writer, 15, () => clock);
        var delivery = Delivery();
        Assert.IsTrue(sink.TryDeliver(delivery));
        clock = 0;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(0UL, sink.NextMaintenanceDueMicroseconds);
        int retries = 0;
        Assert.AreEqual(15, Switch2RumbleMaintenanceSchedule.GetDelayMilliseconds(
            Switch2RumbleMaintenanceResult.Active(sink.NextMaintenanceDueMicroseconds), 15, 30000, ref retries));
        clock = 30000;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(1, writer.Calls);
        Assert.AreEqual(45000UL, sink.NextMaintenanceDueMicroseconds);
        clock = 45000;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls);
    }

    [DataTestMethod]
    [DataRow(12)]
    [DataRow(15)]
    public void PhysicalSustainUsesLastSuccessfulWriteStartAndNeverCatchesUp(int interval)
    {
        ulong clock = 1000;
        var writer = new Writer();
        var sink = CreateSink(writer, interval, () => clock);
        var delivery = Delivery();
        Assert.IsTrue(sink.TryDeliver(delivery));
        var original = writer.Last;
        ulong due = 1000UL + (ulong)interval * 1000;
        Assert.AreEqual(due, sink.NextMaintenanceDueMicroseconds);
        clock = due - 1;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(1, writer.Calls);
        Assert.IsTrue(sink.NeedsSustainedRefresh, "An early duplicate must not park a still-held oscillator.");
        clock = due;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls);
        Assert.AreEqual(original, writer.Last);
        clock += (ulong)interval * 10000;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(3, writer.Calls, "Missed periods must not produce a catch-up burst.");
        Assert.AreEqual(clock + (ulong)interval * 1000, sink.NextMaintenanceDueMicroseconds);
    }

    [DataTestMethod]
    [DataRow(12)]
    [DataRow(15)]
    public void FastContentionRecoveryCannotRepeatAnotherPublishersNewWriteEarly(int interval)
    {
        ulong clock = 1000;
        var writer = new Writer();
        var sink = CreateSink(writer, interval, () => clock);
        var current = Delivery();
        Assert.IsTrue(sink.TryDeliver(current));
        bool busy = true;
        var worker = new Switch2RumbleMaintenanceWorker(_ =>
        {
            if (busy) return Switch2RumbleMaintenanceResult.Contended;
            Assert.IsTrue(sink.MaintenanceSink.TryDeliver(current));
            return Switch2RumbleMaintenanceResult.Active(sink.NextMaintenanceDueMicroseconds);
        }, interval, automaticTimer: false);
        try
        {
            worker.Wake();
            clock += (ulong)interval * 1000;
            Assert.AreEqual(1, worker.RunScheduledTick(clock, clock));
            clock += 1000;
            current = Delivery(sequence: 2);
            Assert.IsTrue(sink.TryDeliver(current), "A genuinely new publisher frame is never paced.");
            busy = false;
            Assert.AreEqual(interval, worker.RunScheduledTick(clock, clock));
            Assert.AreEqual(2, writer.Calls, "The fast retry must see the competing successful write's cadence.");
            clock += (ulong)interval * 1000;
            Assert.AreEqual(interval, worker.RunScheduledTick(clock, clock));
            Assert.AreEqual(3, writer.Calls);
        }
        finally { worker.Stop(); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedOrBackwardClockRecoversAfterAConservativeFullInterval(bool backward)
    {
        ulong clock = backward ? 10000UL : 0;
        var writer = new Writer();
        var sink = CreateSink(writer, 15, () => clock);
        var delivery = Delivery();
        Assert.IsTrue(sink.TryDeliver(delivery));
        clock = 5000;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(1, writer.Calls, "Recovery must first establish a safe baseline, not resend immediately.");
        Assert.AreEqual(20000UL, sink.NextMaintenanceDueMicroseconds);
        clock = 19999;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(1, writer.Calls);
        clock = 20000;
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(delivery));
        Assert.AreEqual(2, writer.Calls, "A transient clock failure must not permanently silence a held effect.");
    }

    [TestMethod]
    public void UncertainRetryDoesNotUseOrAdvanceSuccessfulWriteCadence()
    {
        ulong clock = 1000;
        var writer = new Writer();
        var sink = CreateSink(writer, 15, () => clock);
        var first = Delivery();
        Assert.IsTrue(sink.TryDeliver(first));
        var update = Delivery(sequence: 2);
        clock = 2000;
        writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(Switch2HdRumblePhysicalWriteFailure.TransportEnded);
        Assert.IsFalse(sink.TryDeliver(update));
        Assert.AreEqual(16000UL, sink.NextMaintenanceDueMicroseconds);
        var attempted = writer.Last;
        writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(update), "An unresolved write is not a completed sustain duplicate.");
        Assert.AreEqual(3, writer.Calls);
        Assert.AreEqual(attempted, writer.Last);
        Assert.AreEqual(17000UL, sink.NextMaintenanceDueMicroseconds);
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(update));
        Assert.AreEqual(3, writer.Calls);
    }

    [DataTestMethod]
    [DataRow(12)]
    [DataRow(15)]
    public void NewRichPcmAndImpulseUpdatesAndStopBypassTheSustainFloor(int interval)
    {
        var writer = new Writer();
        var sink = CreateSink(writer, interval, () => 1000);
        var pcm = Delivery(source: ControllerFeedbackSource.DualSenseVirtualDevice);
        var first = new Switch2HdRumbleGroup(new(101, 201, 301, 401), default, default);
        var second = new Switch2HdRumbleGroup(new(111, 211, 311, 411), default, default);
        Assert.IsTrue(sink.TryStageSourcePreservedSynthesis(pcm.Frame,
            Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand, first, second));
        Assert.IsTrue(sink.TryDeliver(pcm));
        Assert.IsTrue(sink.TryStageSourcePreservedSynthesis(pcm.Frame,
            Switch2HdRumbleFeedbackFidelity.DualSensePcmDualBand, second, first));
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(pcm));
        Assert.AreEqual(2, writer.Calls);
        Assert.AreEqual(second, writer.Last.Left);
        Assert.IsFalse(sink.NeedsSustainedRefresh);
        Assert.IsTrue(sink.TryDeliver(Stop(pcm)));
        Assert.AreEqual(3, writer.Calls);
        Assert.IsFalse(sink.MaintenanceSink.TryDeliver(pcm));

        var impulse = Delivery(sequence: 2, source: ControllerFeedbackSource.XboxOneVirtualDevice, epoch: 32);
        Assert.IsTrue(sink.TrySelectPolicy(Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating));
        Assert.IsTrue(sink.TryDeliver(impulse));
        Assert.IsTrue(sink.TryStageImpulseReleasePresentation(impulse.Frame, 4000, 2000, 1));
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(impulse));
        Assert.AreEqual(5, writer.Calls);
        Assert.IsTrue(sink.HasPresentedImpulseReleaseRevision(impulse.Frame, 1));
        Assert.IsTrue(Switch2HdRumbleBodyTuning.TryCreate(50, out var tuning));
        Assert.IsTrue(sink.TrySelectConfiguration(Switch2HdRumbleFeedbackPolicy.SideLocalImpulseDualBandSaturating,
            Switch2HdRumbleImpulseTuning.Default, tuning, out _));
        Assert.IsTrue(sink.MaintenanceSink.TryDeliver(impulse));
        Assert.AreEqual(6, writer.Calls);
        Assert.IsTrue(sink.TryDeliver(Stop(impulse)));
        Assert.AreEqual(7, writer.Calls);
        Assert.IsFalse(sink.MaintenanceSink.TryDeliver(impulse));
        Assert.IsTrue(sink.TryRetire());
        Assert.IsFalse(sink.MaintenanceSink.TryDeliver(impulse));
    }

    [TestMethod]
    public void CanonicalExpiryStopIsImmediateEvenBeforePhysicalRepeatDue()
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        Assert.IsTrue(ControllerFeedbackStateLanePump.TryCreate(7, 11, out var pump));
        Assert.IsTrue(pump.TryCreateBrokerIngress(ControllerFeedbackSource.XboxOneVirtualDevice, 19, out var ingress));
        var writer = new Writer();
        var sink = CreateSink(writer, 15, () => 1000);
        Assert.IsTrue(ingress.TryPublish(new ControllerFeedbackActuatorState(100, 200, 0, 0), now, 250000));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(now, sink, out _));
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(now + 250000, allowNoFrame: true, applyOnly: true));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(now + 250000, sink.MaintenanceSink, out var stop));
        Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop, stop.Disposition);
        Assert.AreEqual(2, writer.Calls);
        Assert.IsFalse(sink.NeedsSustainedRefresh);
        Assert.IsFalse(pump.RequiresOutputMaintenance);
    }

    [TestMethod]
    public void PacedDuplicatesAndContentionSchedulingAllocateNothingAfterWarmup()
    {
        var writer = new Writer();
        var sink = CreateSink(writer, 15, () => 1000);
        var delivery = Delivery();
        Assert.IsTrue(sink.TryDeliver(delivery));
        int retries = 0;
        var busy = Switch2RumbleMaintenanceResult.Contended;
        for (int index = 0; index < 1000; index++)
        {
            sink.MaintenanceSink.TryDeliver(delivery);
            _ = Switch2RumbleMaintenanceSchedule.GetDelayMilliseconds(busy, 15, 1000, ref retries);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = true;
        for (int index = 0; index < 10000; index++)
        {
            accepted &= sink.MaintenanceSink.TryDeliver(delivery);
            _ = Switch2RumbleMaintenanceSchedule.GetDelayMilliseconds(busy, 15, 1000, ref retries);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(accepted);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(1, writer.Calls);
    }

    private static Switch2HdRumbleDeliverySink CreateSink(Writer writer, int interval, Func<ulong> clock) =>
        new(writer, 7, 11, minimumMaintenanceIntervalMicroseconds: (ulong)interval * 1000, hostWriteStartClock: clock);

    private static ControllerFeedbackDelivery Delivery(ulong sequence = 1,
        ControllerFeedbackSource source = ControllerFeedbackSource.Xbox360VirtualDevice, ulong epoch = 31)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(source, ControllerFeedbackCommand.Apply,
            ControllerFeedbackActuators.All, 40000, 30000, 10000, 5000, sequence, 7, 11, 19, now, 250000, out var frame));
        return new(ControllerFeedbackDeliveryDisposition.Frame, ControllerFeedbackPublicationOrigin.TestPreview,
            frame, 7, 11, epoch);
    }

    private static ControllerFeedbackDelivery Stop(in ControllerFeedbackDelivery delivery) =>
        new(ControllerFeedbackDeliveryDisposition.Stop, delivery.Origin, default, 7, 11, delivery.DeliveryEpoch);

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
}
