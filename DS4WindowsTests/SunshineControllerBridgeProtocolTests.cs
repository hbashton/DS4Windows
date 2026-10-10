using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DS4Windows.Sunshine;

namespace DS4WindowsTests;

[TestClass]
public sealed class SunshineControllerBridgeProtocolTests
{
    [TestMethod]
    public void StateRecordRoundTripsAllNormalizedControls()
    {
        var source = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.State, 0x1122334455667788,
            controllerId: 7, clientIndex: 2, sequence: 0xAABBCCDD,
            buttons: 0x87654321, leftTrigger: 19, rightTrigger: 240,
            leftX: short.MinValue, leftY: 0, rightX: 1234,
            rightY: short.MaxValue);
        byte[] bytes = new byte[SunshineControllerBridgePacket.RecordLength];

        source.Write(bytes);

        Assert.IsTrue(SunshineControllerBridgePacket.TryRead(bytes, out var read));
        Assert.AreEqual(source.Kind, read.Kind);
        Assert.AreEqual(source.SessionId, read.SessionId);
        Assert.AreEqual(source.ControllerId, read.ControllerId);
        Assert.AreEqual(source.ClientIndex, read.ClientIndex);
        Assert.AreEqual(source.Sequence, read.Sequence);
        Assert.AreEqual(source.Buttons, read.Buttons);
        Assert.AreEqual(source.LeftTrigger, read.LeftTrigger);
        Assert.AreEqual(source.RightTrigger, read.RightTrigger);
        Assert.AreEqual(source.LeftX, read.LeftX);
        Assert.AreEqual(source.LeftY, read.LeftY);
        Assert.AreEqual(source.RightX, read.RightX);
        Assert.AreEqual(source.RightY, read.RightY);
    }

    [TestMethod]
    public void MotionAndTouchRecordsKeepSensorSamplesAndCoordinates()
    {
        byte[] bytes = new byte[SunshineControllerBridgePacket.RecordLength];
        var motion = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Motion, 91,
            controllerId: 1, clientIndex: 1, sequence: 2,
            motionType: 2, x: -1.25f, y: 2.5f, z: 9.80665f);
        motion.Write(bytes);
        Assert.IsTrue(SunshineControllerBridgePacket.TryRead(bytes, out var readMotion));
        Assert.AreEqual(motion.MotionType, readMotion.MotionType);
        Assert.AreEqual(motion.X, readMotion.X);
        Assert.AreEqual(motion.Y, readMotion.Y);
        Assert.AreEqual(motion.Z, readMotion.Z, 0.00001f);

        var touch = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Touch, 91,
            controllerId: 1, clientIndex: 1, sequence: 3,
            touchEvent: 1, pointerId: 0x12345678,
            x: 0.25f, y: 0.75f, pressure: 0.5f);
        touch.Write(bytes);
        Assert.IsTrue(SunshineControllerBridgePacket.TryRead(bytes, out var readTouch));
        Assert.AreEqual(touch.TouchEvent, readTouch.TouchEvent);
        Assert.AreEqual(touch.PointerId, readTouch.PointerId);
        Assert.AreEqual(touch.X, readTouch.X);
        Assert.AreEqual(touch.Y, readTouch.Y);
        Assert.AreEqual(touch.Pressure, readTouch.Pressure);
    }

    [TestMethod]
    public void FeedbackRecordRoundTripsRumbleRgbAndAdaptiveTriggerPayload()
    {
        byte[] left = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        byte[] right = { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        var source = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Feedback, 3,
            controllerId: 3, clientIndex: 0, sequence: 4,
            feedbackKind: (byte)SunshineControllerBridgeFeedbackKind.AdaptiveTriggers,
            lowRumble: 42, highRumble: 99, red: 4, green: 5, blue: 6,
            motionRate: 100, motionType: 2, triggerFlags: 3,
            leftTriggerType: 1, rightTriggerType: 2,
            leftTriggerData: left, rightTriggerData: right);
        byte[] bytes = new byte[SunshineControllerBridgePacket.RecordLength];

        source.Write(bytes);

        Assert.IsTrue(SunshineControllerBridgePacket.TryRead(bytes, out var read));
        Assert.AreEqual(source.FeedbackKind, read.FeedbackKind);
        Assert.AreEqual(source.LowRumble, read.LowRumble);
        Assert.AreEqual(source.HighRumble, read.HighRumble);
        Assert.AreEqual(source.Red, read.Red);
        Assert.AreEqual(source.Green, read.Green);
        Assert.AreEqual(source.Blue, read.Blue);
        Assert.AreEqual(source.MotionRate, read.MotionRate);
        CollectionAssert.AreEqual(left, read.LeftTriggerData);
        CollectionAssert.AreEqual(right, read.RightTriggerData);
    }

    [TestMethod]
    public void ReaderRejectsWrongVersionReservedBytesInvalidSlotAndNonFiniteMotion()
    {
        byte[] bytes = new byte[SunshineControllerBridgePacket.RecordLength];
        new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Hello, 4, 0, 0, 0).Write(bytes);

        bytes[4] = 2;
        Assert.IsFalse(SunshineControllerBridgePacket.TryRead(bytes, out _));
        bytes[4] = 1;

        bytes[7] = 1;
        Assert.IsFalse(SunshineControllerBridgePacket.TryRead(bytes, out _));
        bytes[7] = 0;

        bytes[8] = 16;
        Assert.IsFalse(SunshineControllerBridgePacket.TryRead(bytes, out _));

        var motion = new SunshineControllerBridgePacket(
            SunshineControllerBridgePacketKind.Motion, 4, 0, 0, 0,
            motionType: 1, x: float.NaN);
        motion.Write(bytes);
        Assert.IsFalse(SunshineControllerBridgePacket.TryRead(bytes, out _));
    }

    [TestMethod]
    public void PacketConstructorRejectsMalformedAdaptiveTriggerLength()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new SunshineControllerBridgePacket(
                SunshineControllerBridgePacketKind.Feedback, 1, 0, 0, 0,
                leftTriggerData: new byte[9]));
    }
}
