using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public class JoyConPeerAssociationTests
{
    [TestMethod]
    public void ExactPeerRemovalDetachesOnce()
    {
        JoyConDevice source = CreateDevice(0);
        JoyConDevice peer = CreateDevice(1);
        source.JointDevice = peer;

        Assert.IsTrue(source.TryDetachJointDevice(peer));
        Assert.IsNull(source.JointDevice);
        Assert.AreEqual(DS4Device.DEFAULT_JOINT_SLOT_NUMBER,
            source.JointDeviceSlotNumber);
        Assert.IsFalse(source.TryDetachJointDevice(peer));
        Assert.IsFalse(source.TryDetachJointDevice(null));
    }

    [TestMethod]
    public void OldPeerRemovalCannotDetachSuccessorEvenAtSameSlot()
    {
        JoyConDevice source = CreateDevice(0);
        JoyConDevice oldPeer = CreateDevice(1);
        JoyConDevice successor = CreateDevice(1);
        source.JointDevice = oldPeer;
        source.JointDevice = successor;

        Assert.IsFalse(source.TryDetachJointDevice(oldPeer));
        Assert.AreSame(successor, source.JointDevice);
        Assert.AreEqual(1, source.JointDeviceSlotNumber);
        Assert.IsFalse(source.TryDetachJointDevice(null));
        Assert.AreSame(successor, source.JointDevice);
        Assert.IsTrue(source.TryDetachJointDevice(successor));
    }

    [TestMethod]
    public void SlotReadRacingDetachAndReplacementUsesOnePeerSnapshot()
    {
        JoyConDevice source = CreateDevice(0);
        JoyConDevice oldPeer = CreateDevice(1);
        JoyConDevice successor = CreateDevice(2);
        using ManualResetEventSlim start = new(false);
        Task writer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 100_000; i++)
            {
                source.JointDevice = oldPeer;
                source.TryDetachJointDevice(oldPeer);
                source.JointDevice = successor;
                if (source.TryDetachJointDevice(oldPeer))
                    throw new AssertFailedException("Old peer detached successor.");
                source.TryDetachJointDevice(successor);
            }
        });

        start.Set();
        try
        {
            for (int i = 0; i < 100_000; i++)
            {
                int slot = source.JointDeviceSlotNumber;
                if (slot != DS4Device.DEFAULT_JOINT_SLOT_NUMBER &&
                    slot != 1 && slot != 2)
                    throw new AssertFailedException($"Unexpected peer slot {slot}.");
            }
        }
        finally
        {
            Assert.IsTrue(writer.Wait(TimeSpan.FromSeconds(10)),
                "Peer writer failed to finish.");
        }
    }

    [TestMethod]
    public void WarmPeerAccessDoesNotAllocate()
    {
        JoyConDevice source = CreateDevice(0);
        JoyConDevice peer = CreateDevice(1);
        for (int i = 0; i < 1_000; i++)
        {
            source.JointDevice = peer;
            _ = source.JointDevice;
            _ = source.JointDeviceSlotNumber;
            source.TryDetachJointDevice(peer);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int observed = 0;
        for (int i = 0; i < 10_000; i++)
        {
            source.JointDevice = peer;
            observed += source.JointDeviceSlotNumber;
            source.TryDetachJointDevice(peer);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(10_000, observed);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void DedicatedThreadPeerMeasurementRemainsZeroAndDetectsPositiveControl()
    {
        // Supplemental evidence for a one-off full-suite allocation reading.
        // Keep the original test and zero-byte assertion unchanged. This
        // isolates the exact warmed loop from the test runner's worker thread.
        JoyConDevice source = CreateDevice(0);
        JoyConDevice peer = CreateDevice(1);
        long[] allocations = new long[8];
        int[] observations = new int[8];
        long positiveControl = 0;
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (int warm = 0; warm < 32; warm++) MeasurePeerLoop(source, peer, out _);
                AllocateControl();
                for (int sample = 0; sample < allocations.Length; sample++)
                    allocations[sample] = MeasurePeerLoop(source, peer, out observations[sample]);
                long before = GC.GetAllocatedBytesForCurrentThread();
                AllocateControl();
                positiveControl = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true, Name = "Peer allocation measurement" };
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsNull(failure, failure?.ToString());
        for (int sample = 0; sample < allocations.Length; sample++)
        {
            Assert.AreEqual(10_000, observations[sample]);
            Assert.AreEqual(0L, allocations[sample], $"Dedicated measurement {sample}");
        }
        Assert.IsTrue(positiveControl >= 128, "The same meter must detect an intentional managed allocation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePeerLoop(JoyConDevice source, JoyConDevice peer, out int observed)
    {
        observed = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            source.JointDevice = peer;
            observed += source.JointDeviceSlotNumber;
            source.TryDetachJointDevice(peer);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateControl() => GC.KeepAlive(new byte[128]);

    // Association-only tests must not enumerate or open real HID hardware.
    // No constructor-owned transport/state is accessed by these methods.
    private static JoyConDevice CreateDevice(int slot)
    {
        var device = (JoyConDevice)RuntimeHelpers.GetUninitializedObject(
            typeof(JoyConDevice));
        device.DeviceSlotNumber = slot;
        return device;
    }
}
