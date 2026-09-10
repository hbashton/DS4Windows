using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2StandaloneRumbleCadenceTests
{
    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, 10)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, 10)]
    [DataRow(Switch2ControllerModel.ProController2, 15)]
    public void PhysicalRepeatGateMatchesSelectedCadenceWithoutChangingTheWaveform(
        Switch2ControllerModel model, int interval)
    {
        ulong hostNow = 1000;
        var lease = new Lease(model);
        Assert.IsTrue(Switch2BluetoothFeedbackLifetime.TryCreate(lease, model, 7, 11,
            out var owner, hostWriteStartClock: () => hostNow));
        Assert.IsTrue(owner.TryActivate());
        Assert.IsTrue(owner.TryCreateLane(ControllerFeedbackPublicationOrigin.TestPreview,
            ControllerFeedbackSource.Xbox360VirtualDevice, 19, 250_000, 100_000, out var lane));
        try
        {
            Assert.IsTrue(ControllerFeedbackClock.TryGetTimestampMicroseconds(out ulong now));
            Assert.IsTrue(lane.TryPublish(new ControllerFeedbackActuatorState(ushort.MaxValue, 0, 0, 0), now));
            Assert.AreEqual(ControllerFeedbackPumpDisposition.Delivered, owner.TryPumpOnce(now, out _));
            Assert.AreEqual(1, lease.Payloads.Count);
            ulong period = (ulong)interval * 1000;
            hostNow += period - 1;
            _ = owner.TryServiceRumbleMaintenance(now + period - 1, null);
            Assert.AreEqual(1, lease.Payloads.Count, "An early duplicate must not write.");
            hostNow++;
            _ = owner.TryServiceRumbleMaintenance(now + period, null);
            Assert.AreEqual(2, lease.Payloads.Count, "The physical sink must use the same interval as the worker.");
            // Payload identity except for the rolling actuator-group counters.
            var first = lease.Payloads[0];
            var second = lease.Payloads[1];
            Assert.AreEqual((first[1] + 1) & 15, second[1] & 15);
            CollectionAssert.AreEqual(first[2..17], second[2..17]);
            if (model == Switch2ControllerModel.ProController2)
                CollectionAssert.AreEqual(first[18..], second[18..]);

            Assert.IsTrue(lane.TryWithdraw(now + period + 1));
            _ = owner.TryPumpOnce(now + period + 1, out var stop);
            Assert.AreEqual(ControllerFeedbackDeliveryDisposition.Stop, stop.Disposition);
            Assert.AreEqual(3, lease.Payloads.Count, "An explicit stop bypasses the repeat interval.");
            Assert.IsFalse(owner.RequiresRumbleMaintenance);
            hostNow += period * 10;
            _ = owner.TryServiceRumbleMaintenance(now + period * 10, null);
            Assert.AreEqual(3, lease.Payloads.Count, "A stopped preview must not resume.");
        }
        finally { Assert.IsTrue(owner.TryStopAndRetireUntil(Environment.TickCount64 + 1000, 3)); }
    }

    [TestMethod]
    public void TenMillisecondWorkerRetainsCoalescingAndNonblockingStop()
    {
        int calls = 0;
        var worker = new Switch2RumbleMaintenanceWorker(_ =>
        {
            calls++;
            return Switch2RumbleMaintenanceResult.Active(21_000);
        }, 10, automaticTimer: false);
        try
        {
            worker.Wake();
            worker.Wake();
            Assert.AreEqual(10, worker.RunScheduledTick(11_000, 11_000));
            Assert.AreEqual(1, calls);
            worker.Stop();
            worker.Wake();
            Assert.AreEqual(0, worker.RunScheduledTick(21_000, 21_000));
            Assert.AreEqual(1, calls);
        }
        finally { worker.Stop(); }
    }

    [DataTestMethod]
    [DataRow(Switch2ControllerModel.JoyCon2Left, false, 10)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, false, 10)]
    [DataRow(Switch2ControllerModel.JoyCon2Left, true, 15)]
    [DataRow(Switch2ControllerModel.JoyCon2Right, true, 15)]
    [DataRow(Switch2ControllerModel.ProController2, false, 15)]
    public void CadenceSelectionDoesNotAccelerateJoinedOrProOutput(
        Switch2ControllerModel model, bool joined, int expected)
    {
        Assert.AreEqual(expected, Switch2RumbleMaintenanceWorker.BluetoothIntervalFor(model, joined));
        Assert.AreEqual(12, Switch2RumbleMaintenanceWorker.UsbIntervalMilliseconds);
    }

    private sealed class Lease(Switch2ControllerModel expectedModel) : ISwitch2BluetoothHdRumbleBindableTransportLease
    {
        internal readonly List<byte[]> Payloads = new();
        public bool HasHdRumbleOutput => true;
        public bool Authenticates(Switch2ControllerModel model, ulong device, ulong transport) =>
            model == expectedModel && device == 7 && transport == 11;
        public bool TryBindHdRumbleLifetime(Switch2ControllerModel model, ulong device, ulong transport) =>
            Authenticates(model, device, transport);
        public Switch2BluetoothHdRumbleTransportWriteResult TryWritePayload(ReadOnlySpan<byte> payload,
            Switch2ControllerModel model, ulong device, ulong transport)
        {
            Payloads.Add(payload.ToArray());
            return Switch2BluetoothHdRumbleTransportWriteResult.Complete(model, device, transport, payload.Length);
        }
    }
}
