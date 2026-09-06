using System.Reflection;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public sealed class LegacyNintendoRumbleDeliveryTests
{
    [DataTestMethod]
    [DataRow(0)] // Pro, 64-byte reports
    [DataRow(1)] // Left Joy-Con, Bluetooth
    [DataRow(2)] // Right Joy-Con, Bluetooth
    [DataRow(3)] // Left Joy-Con, USB
    [DataRow(4)] // Right Joy-Con, USB
    public void FailedNeutralIsRetriedUntilAccepted(int kind)
    {
        var target = new Target(kind);
        target.Publish(180, 100);
        target.Write();
        target.Publish(0, 0);
        target.Sink.Accept = false;
        target.Write();
        target.Write();
        Assert.AreEqual(3, target.Sink.Reports.Count,
            "A failed neutral must remain pending on the existing writer.");
        target.Sink.Accept = true;
        target.Write();
        Assert.AreEqual(4, target.Sink.Reports.Count);
        foreach (byte[] report in target.Sink.Reports.Skip(1))
            target.AssertNeutral(report);
        target.AssertConsecutiveCounters();
        target.Write();
        Assert.AreEqual(4, target.Sink.Reports.Count,
            "A successfully delivered neutral must return to idle.");
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void FailedNonzeroThenFailedNeutralDoesNotAssumeHardwareWasIdle(int kind)
    {
        var target = new Target(kind);
        target.Sink.Accept = false;
        target.Publish(180, 100);
        target.Write();
        target.Publish(0, 0);
        target.Write();
        target.Sink.Accept = true;
        target.Write();
        Assert.AreEqual(3, target.Sink.Reports.Count,
            "A failed write does not prove the device ignored its payload.");
        target.AssertNeutral(target.Sink.Reports[2]);
        target.Write();
        Assert.AreEqual(3, target.Sink.Reports.Count);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void ThrowingNeutralWriteDoesNotAcknowledgeIt(int kind)
    {
        var target = new Target(kind);
        target.Publish(180, 100);
        target.Write();
        target.Publish(0, 0);
        target.Sink.Throw = true;
        target.Write(); // native exceptions stay on the output owner, not input
        target.Sink.Throw = false;
        target.Write();
        Assert.AreEqual(3, target.Sink.Reports.Count);
        target.AssertNeutral(target.Sink.Reports[2]);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void RetryUsesNewestMailboxRatherThanReplayingFailedPayload(int kind)
    {
        var target = new Target(kind);
        target.Publish(180, 100);
        target.Write();
        target.Publish(0, 0);
        target.Sink.Accept = false;
        target.Write();
        target.Publish(180, 100);
        target.Sink.Accept = true;
        target.Write();
        Assert.AreEqual(3, target.Sink.Reports.Count);
        CollectionAssert.AreEqual(target.Sink.Reports[0][2..],
            target.Sink.Reports[2][2..]);
        target.AssertNeutral(target.Sink.Reports[1]);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void HealthyPathRetainsIdleSuppressionAndActiveRefresh(int kind)
    {
        var target = new Target(kind);
        target.Write();
        Assert.AreEqual(0, target.Sink.Reports.Count);
        target.Publish(180, 100);
        target.Write();
        target.Write();
        Assert.AreEqual(2, target.Sink.Reports.Count);
        CollectionAssert.AreEqual(target.Sink.Reports[0][2..],
            target.Sink.Reports[1][2..]);
        target.Publish(0, 0);
        target.Write();
        target.AssertNeutral(target.Sink.Reports[2]);
        target.Write();
        Assert.AreEqual(3, target.Sink.Reports.Count);
        target.AssertConsecutiveCounters();
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void ZeroPublishedDuringActiveWriteIsDeliveredOnNextPass(int kind)
    {
        var target = new Target(kind);
        target.Publish(180, 100);
        target.Sink.DuringWrite = () => target.Publish(0, 0);
        target.Write();
        target.Sink.DuringWrite = null;
        target.Write();
        Assert.AreEqual(2, target.Sink.Reports.Count);
        target.AssertNeutral(target.Sink.Reports[1]);
        target.Write();
        Assert.AreEqual(2, target.Sink.Reports.Count);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task InputPublicationDoesNotWaitForBlockedNativeRumble(int kind)
    {
        var target = new Target(kind);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        target.Sink.DuringWrite = () => { entered.Set(); release.Wait(); };
        target.Publish(180, 100);
        Task writing = Task.Run(target.Write);
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
            Task publication = Task.Run(() => { target.Publish(0, 0); target.PublishReport(); });
            await publication.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(writing.IsCompleted, "The native writer must remain blocked during publication.");
        }
        finally
        {
            release.Set();
            await writing.WaitAsync(TimeSpan.FromSeconds(2));
        }
        target.Sink.DuringWrite = null;
        Assert.IsTrue(target.Output.PumpOnce());
        Assert.AreEqual(2, target.Sink.Reports.Count);
        target.AssertNeutral(target.Sink.Reports[1]);
        target.AssertConsecutiveCounters();
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task RealOutputWorkerDrainsNeutralBeforeDeviceStopReturns(int kind)
    {
        var target = new Target(kind);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        target.Sink.DuringWrite = () =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.Set(); release.Wait(); }
        };
        target.Output.Start("Recording Nintendo output");
        Task stopped = null;
        try
        {
            target.Publish(180, 100);
            target.PublishReport();
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
            stopped = Task.Run(target.Stop);
            // Publication can proceed even while the retiring native writer is
            // blocked; whether it wins Stop or loses, final output must be zero.
            target.Publish(200, 200);
            target.PublishReport();
            release.Set();
            await stopped.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            if (stopped != null) await stopped.WaitAsync(TimeSpan.FromSeconds(2));
            else target.Stop();
        }
        Assert.IsTrue(target.Output.StopDelivered);
        Assert.IsTrue(target.Sink.Reports.Count >= 2);
        target.AssertNeutral(target.Sink.Reports[^1]);
        target.AssertConsecutiveCounters();
        int retiredCount = target.Sink.Reports.Count;
        target.Publish(255, 255);
        target.PublishReport();
        Assert.IsFalse(target.Output.PumpOnce());
        Assert.AreEqual(retiredCount, target.Sink.Reports.Count);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void WarmInputPublicationAndNativeAdmissionAllocateNothing(int kind)
    {
        var target = new Target(kind);
        target.Sink.Capture = false;
        target.Publish(180, 100);
        for (int i = 0; i < 2000; ++i) target.Write();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; ++i) target.Write();
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class Target
    {
        private readonly int kind;
        private readonly DS4Device device;
        internal readonly RecordingSink Sink = new();
        internal readonly Action Write;
        internal readonly Action PublishReport;
        internal readonly LegacyNintendoRumbleOutput Output;
        internal readonly Action Stop;

        internal Target(int kind)
        {
            this.kind = kind;
            // Run the actual device constructors, rumble mailbox, merge and
            // packet encoder. Only OS initialization and the final HID write
            // are replaced: no PostInit, handle, thread or hardware operation.
            HidDevice hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(
                typeof(HidDevice));
            if (kind == 0)
            {
                var pro = new RecordingPro(hid, Sink);
                SetField(typeof(SwitchProDevice), pro, "rumbleReportBuffer",
                    new byte[pro.RumbleReportLen]);
                device = pro;
                PublishReport = pro.WriteReport;
                Output = pro.InitializeRumbleOutput();
                Stop = pro.StopRumble;
            }
            else
            {
                var joyCon = new RecordingJoyCon(hid, Sink);
                SetField(typeof(JoyConDevice), joyCon, "sideType",
                    kind is 1 or 3 ? JoyConDevice.JoyConSide.Left :
                        JoyConDevice.JoyConSide.Right);
                SetField(typeof(JoyConDevice), joyCon, "rumbleReportBuffer",
                    new byte[kind <= 2 ? JoyConDevice.RUMBLE_REPORT_LEN_BT :
                        JoyConDevice.RUMBLE_REPORT_LEN_USB]);
                device = joyCon;
                PublishReport = joyCon.WriteReport;
                Output = joyCon.InitializeRumbleOutput();
                Stop = joyCon.StopRumble;
            }
            // The same pump is normally run by the device's dedicated worker.
            // Manual scheduling makes retry order deterministic without HID.
            Write = () => { PublishReport(); Output.PumpOnce(); };
        }

        internal void Publish(byte heavy, byte light) =>
            device.setRumble(light, heavy);

        internal void AssertNeutral(byte[] report)
        {
            Assert.AreEqual((byte)0x10, report[0]);
            byte[] neutral = [0x00, 0x01, 0x60, 0x40];
            if (kind is 0 or 1 or 3)
                CollectionAssert.AreEqual(neutral, report[2..6]);
            if (kind is 0 or 2 or 4)
                CollectionAssert.AreEqual(neutral, report[6..10]);
        }

        internal void AssertConsecutiveCounters()
        {
            for (int i = 0; i < Sink.Reports.Count; i++)
                Assert.AreEqual((byte)(i & 0x0f), Sink.Reports[i][1]);
        }
    }

    private sealed class RecordingSink
    {
        internal bool Accept = true;
        internal bool Throw;
        internal bool Capture = true;
        internal Action DuringWrite;
        internal readonly List<byte[]> Reports = new();

        internal bool Write(byte[] report)
        {
            if (Capture) Reports.Add((byte[])report.Clone());
            DuringWrite?.Invoke();
            if (Throw) throw new IOException("Injected HID write failure");
            return Accept;
        }
    }

    private sealed class RecordingPro(HidDevice hid, RecordingSink sink)
        : SwitchProDevice(hid, "Recording Pro")
    {
        internal void StopRumble() => StopOutputUpdate();
        protected override bool WriteRumbleReport(byte[] report) =>
            sink.Write(report);
    }

    private sealed class RecordingJoyCon(HidDevice hid, RecordingSink sink)
        : JoyConDevice(hid, "Recording Joy-Con")
    {
        internal void StopRumble() => StopOutputUpdate();
        protected override bool WriteRumbleReport(byte[] report) =>
            sink.Write(report);
    }

    private static void SetField(Type type, object target, string name,
        object value) => type.GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
