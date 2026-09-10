using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

/// <summary>
/// The actual one-shot worker and delivery sink, with an explicit host clock
/// advanced by physical-service cost. No Timer/Sleep benchmark, transport,
/// waveform mutation, or assertion about Windows/HCI presentation timing.
/// </summary>
[TestClass]
public sealed class Switch2RumbleComposedScheduleTests
{
    [DataTestMethod]
    [DataRow(10)]
    [DataRow(12)]
    [DataRow(15)]
    public void SuccessfulServiceCostIsSubtractedOnceFromNextWriteStartDue(int interval)
    {
        foreach (ulong cost in new[] { 0UL, 500UL, 2500UL, (ulong)interval * 1000 + 2000 })
        {
            using var fixture = new Fixture(interval);
            fixture.Writer.Cost = cost;
            ulong due = fixture.Sink.NextMaintenanceDueMicroseconds;
            int delay = fixture.Tick(due, cost);
            Assert.AreEqual(2, fixture.Writer.Starts.Count);
            Assert.AreEqual(due, fixture.Writer.Starts[1]);
            Assert.AreEqual(due + fixture.Period, fixture.Sink.NextMaintenanceDueMicroseconds);
            int expected = cost >= fixture.Period ? 1 :
                (int)((fixture.Period - cost + 999) / 1000);
            Assert.AreEqual(expected, delay,
                "Rearm must use successful host-write start + period, not completion + period or a twice-subtracted cost.");

            ulong nextTick = fixture.Now + (ulong)delay * 1000;
            fixture.Tick(nextTick, cost);
            Assert.AreEqual(3, fixture.Writer.Starts.Count);
            Assert.IsTrue(fixture.Writer.Starts[2] - fixture.Writer.Starts[1] >= fixture.Period);
            Assert.AreEqual(nextTick + fixture.Period, fixture.Sink.NextMaintenanceDueMicroseconds);
        }
    }

    [DataTestMethod]
    [DataRow(10)]
    [DataRow(12)]
    [DataRow(15)]
    public void EarlyTickAndCompetingPublisherRecheckTheActualSuccessfulWriteAnchor(int interval)
    {
        using var fixture = new Fixture(interval);
        ulong originalDue = fixture.Sink.NextMaintenanceDueMicroseconds;
        Assert.AreEqual(1, fixture.Tick(originalDue - 1, 0));
        Assert.AreEqual(1, fixture.Writer.Starts.Count, "An early timer callback must not emit a keep-alive.");

        fixture.PublishNew(originalDue - 1);
        Assert.AreEqual(2, fixture.Writer.Starts.Count, "A new publisher command must remain immediate.");
        ulong competingDue = originalDue - 1 + fixture.Period;
        Assert.AreEqual(competingDue, fixture.Sink.NextMaintenanceDueMicroseconds);
        Assert.AreEqual(interval - 1, fixture.Tick(originalDue + 999, 0));
        Assert.AreEqual(2, fixture.Writer.Starts.Count, "The already scheduled retry must not repeat the competing write early.");
        Assert.AreEqual(interval, fixture.Tick(competingDue, 0));
        Assert.AreEqual(3, fixture.Writer.Starts.Count);
    }

    [DataTestMethod]
    [DataRow(10)]
    [DataRow(12)]
    [DataRow(15)]
    public void LateCallbackPublishesOnceAndReanchorsWithoutCatchUpBursts(int interval)
    {
        using var fixture = new Fixture(interval);
        ulong late = fixture.Sink.NextMaintenanceDueMicroseconds + 5 * fixture.Period + 375;
        Assert.AreEqual(interval, fixture.Tick(late, 0));
        Assert.AreEqual(2, fixture.Writer.Starts.Count);
        Assert.AreEqual(late + fixture.Period, fixture.Sink.NextMaintenanceDueMicroseconds);
        // Deliberately invoke an extra scheduled callback at the same time:
        // the sink, not just the timer, must enforce the physical repeat floor.
        Assert.AreEqual(interval, fixture.Tick(late, 0));
        Assert.AreEqual(2, fixture.Writer.Starts.Count);
        Assert.AreEqual(interval, fixture.Tick(late + fixture.Period, 0));
        Assert.AreEqual(3, fixture.Writer.Starts.Count);
    }

    [DataTestMethod]
    [DataRow(10)]
    [DataRow(12)]
    [DataRow(15)]
    public void UncertainWriteKeepsExactRetryAndUsesFullBackoffAfterCompletion(int interval)
    {
        using var fixture = new Fixture(interval);
        ulong previousDue = fixture.Sink.NextMaintenanceDueMicroseconds;
        fixture.Writer.Cost = 2400;
        fixture.Writer.Result = Switch2HdRumblePhysicalWriteResult.Uncertain(
            Switch2HdRumblePhysicalWriteFailure.TransportEnded);
        Assert.AreEqual(interval, fixture.Tick(previousDue, 2400));
        Assert.AreEqual(previousDue, fixture.Sink.NextMaintenanceDueMicroseconds,
            "An uncertain attempt cannot advance the successful-write anchor.");
        var unresolved = fixture.Writer.Submissions[1];
        ulong retryAt = fixture.Now + fixture.Period;
        fixture.Writer.Result = Switch2HdRumblePhysicalWriteResult.Success();
        Assert.AreEqual(interval - 2, fixture.Tick(retryAt, 2400));
        Assert.AreEqual(unresolved, fixture.Writer.Submissions[2],
            "The actual sink must retain the unresolved submission, not regenerate another command.");
        Assert.AreEqual(retryAt + fixture.Period, fixture.Sink.NextMaintenanceDueMicroseconds);
        Assert.AreEqual(3, fixture.Writer.Starts.Count);
    }

    [DataTestMethod]
    [DataRow(10)]
    [DataRow(12)]
    [DataRow(15)]
    public void ContentionBudgetDoesNotResetOnWakeAndSuccessRechecksCompetingWrite(int interval)
    {
        using var fixture = new Fixture(interval);
        fixture.Contended = true;
        ulong tick = fixture.Sink.NextMaintenanceDueMicroseconds;
        for (int index = 0; index < 5; index++)
        {
            fixture.Worker.Wake();
            int delay = fixture.Tick(tick, 0);
            Assert.AreEqual(index < 3 ? 1 : interval, delay);
            Assert.AreEqual(1, fixture.Writer.Starts.Count, "Pre-admission contention must not call the physical writer.");
            tick += (ulong)delay * 1000;
        }
        fixture.PublishNew(tick);
        fixture.Contended = false;
        Assert.AreEqual(interval, fixture.Tick(tick, 0));
        Assert.AreEqual(2, fixture.Writer.Starts.Count);
        fixture.Contended = true;
        Assert.AreEqual(1, fixture.Tick(tick + fixture.Period, 0),
            "A completed non-contended service resets the fast-retry budget.");
    }

    [DataTestMethod]
    [DataRow(10)]
    [DataRow(12)]
    [DataRow(15)]
    public void StopDuringAdmittedWriteAllowsReceiptButSealsEveryLaterRearm(int interval)
    {
        using var fixture = new Fixture(interval);
        fixture.Writer.Cost = 700;
        fixture.Writer.DuringWrite = fixture.Worker.Stop;
        ulong due = fixture.Sink.NextMaintenanceDueMicroseconds;
        Assert.AreEqual(0, fixture.Tick(due, 700));
        Assert.AreEqual(2, fixture.Writer.Starts.Count, "The already-admitted write retains its one receipt.");
        Assert.IsTrue(fixture.Worker.IsStopped);
        fixture.Worker.Wake();
        Assert.AreEqual(0, fixture.Tick(due + 10 * fixture.Period, 0));
        Assert.AreEqual(2, fixture.Writer.Starts.Count, "A stale wake/callback cannot restart the stopped worker.");
    }

    private sealed class Fixture : IDisposable
    {
        internal ulong Now = 1000;
        internal readonly ulong Period;
        internal readonly Writer Writer;
        internal readonly Switch2HdRumbleDeliverySink Sink;
        internal readonly Switch2RumbleMaintenanceWorker Worker;
        internal bool Contended;
        private ControllerFeedbackDelivery current;
        private ulong sequence = 1;

        internal Fixture(int interval)
        {
            Period = (ulong)interval * 1000;
            Writer = new Writer(this);
            Sink = new Switch2HdRumbleDeliverySink(Writer, 7, 11,
                minimumMaintenanceIntervalMicroseconds: Period, hostWriteStartClock: () => Now);
            current = Delivery(sequence);
            Assert.IsTrue(Sink.TryDeliver(current));
            Worker = new Switch2RumbleMaintenanceWorker(_ =>
            {
                if (Contended) return Switch2RumbleMaintenanceResult.Contended;
                if (!Sink.MaintenanceSink.TryDeliver(current)) return Switch2RumbleMaintenanceResult.RetryPending;
                return Sink.NeedsSustainedRefresh ?
                    Switch2RumbleMaintenanceResult.Active(Sink.NextMaintenanceDueMicroseconds) :
                    Switch2RumbleMaintenanceResult.Idle;
            }, interval, automaticTimer: false);
            Worker.Wake();
        }

        internal int Tick(ulong started, ulong expectedServiceCost)
        {
            Now = started;
            int delay = Worker.RunScheduledTick(started, started + expectedServiceCost);
            Assert.AreEqual(started + expectedServiceCost, Now,
                "The modeled service completion must equal the fake writer's actual clock advancement.");
            return delay;
        }

        internal void PublishNew(ulong at)
        {
            Now = at;
            current = Delivery(++sequence);
            Assert.IsTrue(Sink.TryDeliver(current));
            Worker.Wake();
        }
        public void Dispose() => Worker.Stop();
    }

    private sealed class Writer(Fixture fixture) : ISwitch2HdRumblePhysicalWriter
    {
        internal readonly List<ulong> Starts = [];
        internal readonly List<Switch2HdRumblePhysicalSubmission> Submissions = [];
        internal ulong Cost;
        internal Action DuringWrite;
        internal Switch2HdRumblePhysicalWriteResult Result = Switch2HdRumblePhysicalWriteResult.Success();
        public bool Authenticates(ulong device, ulong transport) => device == 7 && transport == 11;
        public Switch2HdRumblePhysicalWriteResult TryWrite(in Switch2HdRumblePhysicalSubmission submission)
        {
            Starts.Add(fixture.Now);
            Submissions.Add(submission);
            fixture.Now += Cost;
            DuringWrite?.Invoke();
            return Result;
        }
    }

    private static ControllerFeedbackDelivery Delivery(ulong sequence)
    {
        Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
        Assert.IsTrue(ControllerFeedbackFrame.TryCreate(ControllerFeedbackSource.Xbox360VirtualDevice,
            ControllerFeedbackCommand.Apply, ControllerFeedbackActuators.All, 40000, 30000, 0, 0,
            sequence, 7, 11, 19, now, 250000, out var frame));
        return new ControllerFeedbackDelivery(ControllerFeedbackDeliveryDisposition.Frame,
            ControllerFeedbackPublicationOrigin.TestPreview, frame, 7, 11, 31);
    }
}
