using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DS4Windows;
using DS4Windows.Sunshine;

namespace DS4WindowsTests;

[TestClass]
public sealed class SunshineStreamInputDeviceTests
{
    [TestMethod]
    public void ProfileLinkIdentityIsStablePerSunshineSlotAcrossSessions()
    {
        var firstConnection = CreateDevice(sessionId: 55, controllerId: 0,
            clientIndex: 0);
        var reconnect = CreateDevice(sessionId: 99, controllerId: 0,
            clientIndex: 3);
        var secondSlot = CreateDevice(sessionId: 99, controllerId: 1,
            clientIndex: 0);

        Assert.AreEqual("SUNSHINE-SLOT-00", firstConnection.ProfileLinkId);
        Assert.AreEqual(firstConnection.ProfileLinkId, reconnect.ProfileLinkId);
        Assert.AreNotEqual(firstConnection.ProfileLinkId,
            secondSlot.ProfileLinkId);
        Assert.AreEqual(firstConnection.ProfileLinkId,
            firstConnection.DisplayIdentity);
        Assert.AreEqual(secondSlot.ProfileLinkId, secondSlot.DisplayIdentity);
    }

    [TestMethod]
    public void StartUpdateInitializesDebouncerAfterSlotAssignment()
    {
        var device = CreateDevice();
        Assert.IsNull(device.Debouncer);

        device.DeviceSlotNumber = 0;
        device.StartUpdate();

        Assert.IsNotNull(device.Debouncer);
    }

    [TestMethod]
    public void NormalizedStateFeedsExistingDs4MappingStateAtFullStickPrecision()
    {
        var device = CreateDevice();
        int reports = 0;
        device.Report += (_, _) => reports++;
        var packet = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.State, 55, 2, 1, 1,
            buttons: 0x1000 | 0x0010 | 0x200000,
            leftTrigger: 201, rightTrigger: 3,
            leftX: 12345, leftY: 23456,
            rightX: -12345, rightY: -23456);

        Assert.IsTrue(device.Apply(packet));

        DS4State state = device.getRawCurrentState();
        Assert.AreEqual(1, reports);
        Assert.IsFalse(device.HasHidInterface);
        Assert.IsFalse(device.SupportsPhysicalOutput);
        Assert.IsTrue(state.Cross);
        Assert.IsTrue(state.Options);
        Assert.IsTrue(state.TouchButton);
        Assert.AreEqual((byte)201, state.L2);
        Assert.AreEqual((byte)3, state.R2);
        Assert.AreEqual((byte)176, state.LX);
        Assert.AreEqual((byte)36, state.LY);
        Assert.AreEqual(12345, state.LXAxis.ToSigned16());
        Assert.AreEqual(-23456, state.LYAxis.ToSigned16());
        Assert.AreEqual(-12345, state.RXAxis.ToSigned16());
        Assert.AreEqual(23456, state.RYAxis.ToSigned16());
    }

    [TestMethod]
    public void PartialPacketsPreserveControlsAndAdvancePreviousStateAfterReport()
    {
        var device = CreateDevice();
        var observed = new List<(bool Cross, bool PreviousCross, short LeftX)>();
        device.Report += (_, _) => observed.Add((
            device.getRawCurrentState().Cross,
            device.getPreviousStateRef().Cross,
            device.getRawCurrentState().LXAxis.ToSigned16()));

        Assert.IsTrue(device.Apply(new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.State, 55, 2, 1, 1,
            buttons: 0x1000, leftX: 16384)));
        Assert.IsTrue(device.Apply(new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Motion, 55, 2, 1, 2,
            motionType: 2, x: 10, y: -20, z: 30)));
        Assert.IsTrue(device.Apply(new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.State, 55, 2, 1, 3,
            buttons: 0)));

        Assert.AreEqual(3, observed.Count);
        Assert.AreEqual((true, false, (short)16384), observed[0]);
        Assert.AreEqual((true, true, (short)16384), observed[1]);
        Assert.AreEqual((false, true, (short)0), observed[2]);
    }

    [TestMethod]
    public void TryHaltReportingRunActionUsesTheExternalPublicationGate()
    {
        var device = CreateDevice();
        bool ran = false;

        Assert.IsTrue(device.TryHaltReportingRunAction(() => ran = true));
        Assert.IsTrue(ran);
    }

    [TestMethod]
    public void QueuedActionsRunInOrderWithoutAnHidWorker()
    {
        var device = CreateDevice();
        var order = new List<int>();
        using var completed = new ManualResetEventSlim();
        bool serializedWithPublication = false;

        device.queueEvent(() =>
        {
            // A reentrant pause is rejected only when the queue action is
            // already running under the same publication gate as input.
            serializedWithPublication =
                !device.TryHaltReportingRunAction(() => { });
            order.Add(1);
        });
        device.queueEvent(() =>
        {
            order.Add(2);
            completed.Set();
        });

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(3)),
            "No-HID queued actions must not wait for a HID input worker.");
        Assert.IsTrue(serializedWithPublication);
        CollectionAssert.AreEqual(new[] { 1, 2 }, order);
    }

    [TestMethod]
    public void MotionTouchAndBatterySamplesAreAppliedAndStaleSequencesRejected()
    {
        var device = CreateDevice();
        int reports = 0;
        device.Report += (_, _) => reports++;

        Assert.IsTrue(device.Apply(new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Motion, 55, 2, 1, 1,
            motionType: 2, x: 10, y: -20, z: 30)));
        DS4State motion = device.getRawCurrentState();
        Assert.AreEqual(-10, motion.Motion.angVelYaw);
        Assert.AreEqual(-20, motion.Motion.angVelPitch);
        Assert.AreEqual(-30, motion.Motion.angVelRoll);

        Assert.IsTrue(device.Apply(new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Touch, 55, 2, 1, 2,
            touchEvent: 1, pointerId: 12, x: 0.25f, y: 0.75f, pressure: 0.8f)));
        DS4State touch = device.getRawCurrentState();
        Assert.IsTrue(touch.Touch1);
        Assert.AreEqual((short)480, touch.TrackPadTouch0.X);
        Assert.AreEqual((short)707, touch.TrackPadTouch0.Y);

        Assert.IsTrue(device.Apply(new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Battery, 55, 2, 1, 3,
            batteryState: 3, batteryPercent: 73)));
        Assert.AreEqual(73, device.getBattery());
        Assert.IsTrue(device.isCharging());
        Assert.AreEqual((byte)73, device.getRawCurrentState().Battery);

        Assert.IsFalse(device.Apply(new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.State, 55, 2, 1, 2,
            buttons: 0)));
        Assert.AreEqual(3, reports);
    }

    [TestMethod]
    public void RemovalStopsReportsFromRetiredStreamLifetime()
    {
        var device = CreateDevice();
        int removals = 0;
        int reports = 0;
        device.Removal += (_, _) => removals++;
        device.Report += (_, _) => reports++;
        device.RequestRemoval();

        Assert.IsFalse(device.Apply(new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.State, 55, 2, 1, 1)));
        device.RequestRemoval();
        Assert.AreEqual(1, removals);
        Assert.AreEqual(0, reports);
    }

    private static SunshineStreamInputDevice CreateDevice(ulong sessionId = 55,
        ushort controllerId = 2, byte clientIndex = 1) =>
        new(sessionId, controllerId, clientIndex, 1, 0x12, 0x3456);
}
