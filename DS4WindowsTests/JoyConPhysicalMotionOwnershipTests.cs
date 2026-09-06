using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
public sealed class JoyConPhysicalMotionOwnershipTests
{
    [TestMethod]
    public void PhysicalCommitPreservesFullMotionWithoutAliasingTheNextDecode()
    {
        JoyConDevice device = CreateDevice();
        DS4State current = device.GetRawCurrentStateRef();
        current.Cross = true;
        current.LXAxis = DS4MappedStickAxis.FromSigned(19);
        current.PacketCounter = 7;
        current.Motion.gyroYawFull = 101;
        current.Motion.gyroPitchFull = -202;
        current.Motion.gyroRollFull = 303;
        current.Motion.accelX = 12;
        current.Motion.outputAccelX = 71;
        current.Motion.outputGyroControls = true;

        device.PreservePhysicalStateData();
        DS4State previous = device.GetRawPreviousStateRef();

        Assert.AreNotSame(current, previous);
        Assert.AreNotSame(current.Motion, previous.Motion);
        Assert.AreEqual(101, previous.Motion.gyroYawFull);
        Assert.AreEqual(-202, previous.Motion.gyroPitchFull);
        Assert.AreEqual(303, previous.Motion.gyroRollFull);
        Assert.AreEqual(71, previous.Motion.outputAccelX,
            "History must preserve output acceleration, not replace it with raw accelX.");
        Assert.IsTrue(previous.Motion.outputGyroControls);
        Assert.AreEqual(current.LXAxis, previous.LXAxis);
        Assert.IsTrue(previous.Cross);
        Assert.AreEqual(7u, previous.PacketCounter);

        current.Motion.previousAxis = previous.Motion;
        current.Motion.gyroPitchFull = 999;
        current.Cross = false;
        Assert.AreEqual(-202, previous.Motion.gyroPitchFull);
        Assert.IsTrue(previous.Cross);
        Assert.AreNotSame(current.Motion, current.Motion.previousAxis);
        device.PreservePhysicalStateData();
        Assert.AreEqual(999, previous.Motion.gyroPitchFull);
        Assert.AreEqual(-202, previous.Motion.previousAxis.gyroPitchFull);
        Assert.IsNull(previous.Motion.previousAxis.previousAxis);
        Assert.IsFalse(previous.Cross);
    }

    [TestMethod]
    public void SelfReferencingLegacyMotionIsCapturedAsBoundedIndependentHistory()
    {
        JoyConDevice device = CreateDevice();
        SixAxis motion = device.GetRawCurrentStateRef().Motion;
        motion.previousAxis = motion;
        motion.gyroRollFull = -876;
        device.PreservePhysicalStateData();
        SixAxis previous = device.GetRawPreviousStateRef().Motion;
        Assert.AreNotSame(motion, previous);
        Assert.AreNotSame(motion, previous.previousAxis);
        Assert.AreNotSame(previous, previous.previousAxis);
        Assert.IsNull(previous.previousAxis.previousAxis);
        Assert.AreEqual(-876, previous.gyroRollFull);
        Assert.AreEqual(-876, previous.previousAxis.gyroRollFull);
        motion.gyroRollFull = 123;
        Assert.AreEqual(-876, previous.gyroRollFull);
        Assert.AreEqual(-876, previous.previousAxis.gyroRollFull);
    }

    [TestMethod]
    public void PhysicalGyroDeliveryUsesSameReportStateAndOneBorrowedEnvelope()
    {
        JoyConDevice device = CreateDevice();
        DS4State current = device.GetRawCurrentStateRef();
        SixAxisEventArgs first = null;
        int calls = 0;
        device.SixAxis.SixAccelMoved += (sender, args) =>
        {
            Assert.AreSame(device.SixAxis, sender);
            Assert.AreSame(current.Motion, args.sixAxis);
            Assert.AreEqual(current.ReportTimeStamp, args.timeStamp);
            if (first == null) first = args;
            else Assert.AreSame(first, args);
            calls++;
        };
        current.ReportTimeStamp = DateTime.UnixEpoch;
        device.PublishPhysicalMotion();
        current.ReportTimeStamp = DateTime.UnixEpoch.AddMilliseconds(2);
        device.PublishPhysicalMotion();
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void ProjectedMotionPublicationRacingLastUnsubscriptionCannotThrow()
    {
        JoyConDevice device = CreateDevice();
        using var start = new ManualResetEventSlim();
        SixAxisHandler<SixAxisEventArgs> callback = static (sender, args) => { };
        Task subscriptions = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 100_000; i++)
            {
                device.SixAxis.SixAccelMoved += callback;
                device.SixAxis.SixAccelMoved -= callback;
            }
        });
        start.Set();
        try
        {
            for (int i = 0; i < 100_000; i++) device.PublishPhysicalMotion();
        }
        finally { Assert.IsTrue(subscriptions.Wait(10_000)); }
        device.PublishPhysicalMotion();
    }

    [TestMethod]
    public void WarmPhysicalMotionPublicationAndHistoryCommitAllocateNothing()
    {
        JoyConDevice device = CreateDevice();
        DS4State current = device.GetRawCurrentStateRef();
        int calls = 0;
        device.SixAxis.SixAccelMoved += (sender, args) => calls++;
        for (int i = 0; i < 256; i++) Publish(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) Publish(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(4352, calls);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(4095, device.GetRawPreviousStateRef().Motion.gyroPitchFull);
        Assert.AreEqual(4094, device.GetRawPreviousStateRef().Motion.previousAxis.gyroPitchFull);

        void Publish(int sample)
        {
            current.Motion.previousAxis = device.GetRawPreviousStateRef().Motion;
            current.Motion.gyroPitchFull = sample;
            device.PublishPhysicalMotion();
            device.PreservePhysicalStateData();
        }
    }

    // Only the physical reader's pure state seams are initialized. No HID
    // constructor, worker, discovery, output, driver or device I/O is invoked.
    private static JoyConDevice CreateDevice()
    {
        var device = (JoyConDevice)RuntimeHelpers.GetUninitializedObject(typeof(JoyConDevice));
        Set(typeof(DS4Device), "cState", new DS4State());
        Set(typeof(DS4Device), "pState", new DS4State());
        Set(typeof(DS4Device), "sixAxis", new DS4SixAxis());
        Set(typeof(JoyConDevice), "physicalPreviousState", new DS4StateOwnedSnapshot());
        return device;
        void Set(Type declaring, string name, object value) => declaring.GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(device, value);
    }
}
