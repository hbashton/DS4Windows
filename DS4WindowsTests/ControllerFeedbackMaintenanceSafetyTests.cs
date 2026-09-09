using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public sealed class ControllerFeedbackMaintenanceSafetyTests
{
    [TestMethod]
    public void RefreshNeverRenewsTheGameLeaseAndExpiryStillStopsExactlyOnce()
    {
        var pump = CreatePump();
        var lane = CreateLane(pump, ControllerFeedbackPublicationOrigin.NativeGame);
        var sink = new CountingSink();
        Assert.IsTrue(lane.TryPublish(new(1234, 4321, 200, 100), 1000));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(1000, sink, out var first));
        Assert.IsTrue(pump.RequiresOutputMaintenance);
        foreach (ulong now in new ulong[] { 13000, 25000, 101000, 250999 })
        {
            Assert.IsTrue(pump.TryRefreshCurrentPresentation(now, allowNoFrame: true, applyOnly: true));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(now, sink, out var refreshed));
            Assert.AreEqual(first.Frame, refreshed.Frame, "Refresh must not forge timestamp, TTL, sequence or actuator state.");
            Assert.AreEqual(first.DeliveryEpoch, refreshed.DeliveryEpoch);
        }
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(251000, allowNoFrame: true, applyOnly: true));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(251000, sink, out var stop));
        Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop, stop.Disposition);
        Assert.IsFalse(pump.RequiresOutputMaintenance);
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(300000, allowNoFrame: true, applyOnly: true));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.None, pump.PumpOnce(300000, sink, out _));
        Assert.AreEqual(6, sink.Calls);
    }

    [TestMethod]
    public void NeutralStateDoesNotStartAStreamOfNeutralKeepalives()
    {
        var pump = CreatePump();
        var lane = CreateLane(pump, ControllerFeedbackPublicationOrigin.NativeGame);
        var sink = new CountingSink();
        Assert.IsTrue(lane.TryPublish(default, 1000));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(1000, sink, out var neutral));
        Assert.AreEqual(ControllerFeedbackCommand.Neutral, neutral.Frame.Command);
        Assert.IsFalse(pump.RequiresOutputMaintenance);
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(13000, allowNoFrame: true, applyOnly: true));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.None, pump.PumpOnce(13000, sink, out _));
        Assert.AreEqual(1, sink.Calls);
    }

    [TestMethod]
    public void PreviewWithdrawalStopsBeforeRefreshingTheUnderlyingGame()
    {
        var pump = CreatePump();
        var game = CreateLane(pump, ControllerFeedbackPublicationOrigin.NativeGame);
        var preview = CreateLane(pump, ControllerFeedbackPublicationOrigin.TestPreview);
        var sink = new CountingSink();
        Assert.IsTrue(game.TryPublish(new(1234, 0, 0, 0), 1000));
        Assert.IsTrue(preview.TryPublish(new(5678, 0, 0, 0), 1000));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(1000, sink, out var first));
        Assert.AreEqual(ControllerFeedbackPublicationOrigin.TestPreview, first.Origin);
        Assert.IsTrue(preview.TryWithdraw(2000));
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(2000, allowNoFrame: true, applyOnly: true));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(2000, sink, out var stop));
        Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop, stop.Disposition);
        Assert.AreEqual(first.DeliveryEpoch, stop.DeliveryEpoch);
        Assert.IsTrue(pump.TryRefreshCurrentPresentation(2000, allowNoFrame: true, applyOnly: true));
        Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, pump.PumpOnce(2000, sink, out var restored));
        Assert.AreEqual(ControllerFeedbackPublicationOrigin.NativeGame, restored.Origin);
        Assert.AreEqual((ushort)1234, restored.Frame.BodyLow);
        Assert.AreEqual(3, sink.Calls);
    }

    [TestMethod]
    public void ApplyRefreshAndAdmissionHaveNoSteadyStateAllocations()
    {
        var pump = CreatePump();
        var lane = CreateLane(pump, ControllerFeedbackPublicationOrigin.NativeGame);
        var sink = new CountingSink();
        Assert.IsTrue(lane.TryPublish(new(1000, 2000, 0, 0), 1000));
        pump.PumpOnce(1000, sink, out _);
        for (int i = 0; i < 100; i++)
        {
            pump.TryRefreshCurrentPresentation(2000, allowNoFrame: true, applyOnly: true);
            pump.PumpOnce(2000, sink, out _);
        }
        int delivered = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            if (pump.TryRefreshCurrentPresentation(2000, allowNoFrame: true, applyOnly: true) &&
                pump.PumpOnce(2000, sink, out _) == ControllerFeedbackPumpDisposition.Delivered)
                delivered++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(10000, delivered);
        Assert.AreEqual(0L, allocated);
    }

    private static ControllerFeedbackStateLanePump CreatePump()
    {
        Assert.IsTrue(ControllerFeedbackStateLanePump.TryCreate(7, 11, out var pump));
        return pump;
    }

    private static ControllerFeedbackStateLanePump.Lane CreateLane(
        ControllerFeedbackStateLanePump pump, ControllerFeedbackPublicationOrigin origin)
    {
        Assert.IsTrue(pump.TryCreateLane(origin, ControllerFeedbackSource.XboxOneVirtualDevice,
            (ulong)origin + 10, 250000, 100000, out var lane));
        return lane;
    }

    private sealed class CountingSink : IControllerFeedbackDeliverySink
    {
        internal int Calls;
        public bool TryDeliver(in ControllerFeedbackDelivery delivery) { Calls++; return true; }
    }
}
