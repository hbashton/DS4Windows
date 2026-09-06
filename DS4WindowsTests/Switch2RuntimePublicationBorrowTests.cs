using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests;

[TestClass]
public sealed class Switch2RuntimePublicationBorrowTests
{
    [TestMethod]
    public void ExactRegularAndTerminalBorrowOnlyInsideTheirCurrentCallback()
    {
        Switch2RuntimeInputDevice device = CreateDevice();
        Switch2RuntimeReportEventArgs regular = null, terminal = null;
        int calls = 0;
        device.Report += (sender, args) =>
        {
            var envelope = (Switch2RuntimeReportEventArgs)args;
            Assert.IsTrue(device.TryBorrowCurrentPublication(envelope, out var state, out bool hasMotion));
            Assert.IsNotNull(state);
            Assert.IsTrue(device.TryBorrowCurrentPublication(envelope, out var same, out _));
            Assert.AreSame(state, same, "The production mapper borrows, rather than copies, its state.");
            AssertRejected(device, new Switch2RuntimeReportEventArgs(envelope.Kind, envelope.RuntimeGeneration));
            if (envelope.Kind == Switch2RuntimeReportKind.Regular)
            {
                regular = envelope;
                Assert.IsTrue(hasMotion);
                Assert.IsTrue(state.Cross);
                Assert.IsTrue(state.LXAxis.IsHighResolution);
            }
            else
            {
                terminal = envelope;
                Assert.IsFalse(hasMotion);
                Assert.IsFalse(state.Cross);
                Assert.AreEqual((byte)128, state.LX);
                AssertRejected(device, regular);
            }
            calls++;
        };
        Assert.IsTrue(device.TryPublishPro(Frame()));
        AssertRejected(device, regular);
        Assert.IsTrue(device.TryPublishTerminalNeutral());
        AssertRejected(device, regular);
        AssertRejected(device, terminal);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void ActualEnvelopeCannotBorrowDuringRawGyroQueuedOrProfileActions()
    {
        Switch2RuntimeInputDevice device = CreateDevice();
        Switch2RuntimeReportEventArgs regular = null;
        int rawCalls = 0, preCalls = 0, postCalls = 0, profileCalls = 0, reportCalls = 0;
        device.Report += (sender, args) =>
        {
            regular = (Switch2RuntimeReportEventArgs)args;
            Assert.IsTrue(device.TryBorrowCurrentPublication(regular, out _, out _));
            reportCalls++;
            device.queueEvent(() => { AssertRejected(device, regular); postCalls++; });
        };
        Assert.IsTrue(device.TryPublishPro(Frame()));
        device.HaltReportingRunAction(() => { AssertRejected(device, regular); profileCalls++; });
        device.queueEvent(() => { AssertRejected(device, regular); preCalls++; });
        device.SixAxis.SixAccelMoved += (_, _) =>
        {
            AssertRejected(device, regular);
            rawCalls++;
        };
        Assert.IsTrue(device.TryPublishPro(Frame(counter: 2, timestamp: 120_000)));
        Assert.AreEqual(1, rawCalls);
        Assert.AreEqual(1, preCalls);
        Assert.AreEqual(2, postCalls);
        Assert.AreEqual(1, profileCalls);
        Assert.AreEqual(2, reportCalls);
        AssertRejected(device, regular);
    }

    [TestMethod]
    public void ExactEnvelopeCannotBeBorrowedByAnotherThreadWhileReportIsActive()
    {
        Switch2RuntimeInputDevice device = CreateDevice();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Switch2RuntimeReportEventArgs envelope = null;
        device.Report += (sender, args) =>
        {
            envelope = (Switch2RuntimeReportEventArgs)args;
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException();
            Assert.IsTrue(device.TryBorrowCurrentPublication(envelope, out _, out _));
        };
        var publish = Task.Run(() => device.TryPublishPro(Frame()));
        try
        {
            Assert.IsTrue(entered.Wait(5000));
            AssertRejected(device, envelope);
        }
        finally { release.Set(); }
        Assert.IsTrue(publish.Wait(5000));
        Assert.IsTrue(publish.Result);
        AssertRejected(device, envelope);
    }

    [TestMethod]
    public void ThrowingReportCannotLeaveBorrowAuthorityOpen()
    {
        Switch2RuntimeInputDevice device = CreateDevice();
        Switch2RuntimeReportEventArgs envelope = null;
        int postCalls = 0;
        device.Report += (sender, args) =>
        {
            envelope = (Switch2RuntimeReportEventArgs)args;
            Assert.IsTrue(device.TryBorrowCurrentPublication(envelope, out _, out _));
            device.queueEvent(() => { AssertRejected(device, envelope); postCalls++; });
            throw new InvalidOperationException("intentional observer failure");
        };
        Assert.IsFalse(device.TryPublishPro(Frame()));
        Assert.AreEqual(1, postCalls);
        AssertRejected(device, envelope);
        device.HaltReportingRunAction(() => AssertRejected(device, envelope));
    }

    [TestMethod]
    public void WarmedBorrowAndPublicationAllocateNoManagedReportObjects()
    {
        Switch2RuntimeInputDevice device = CreateDevice();
        var frame = Frame();
        bool allAccepted = true;
        int calls = 0;
        device.Report += (sender, args) =>
        {
            allAccepted &= device.TryBorrowCurrentPublication(
                (Switch2RuntimeReportEventArgs)args, out var state, out _)
                && state != null;
            calls++;
        };
        for (int i = 0; i < 256; i++) allAccepted &= device.TryPublishPro(frame);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) allAccepted &= device.TryPublishPro(frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allAccepted);
        Assert.AreEqual(4352, calls);
        Assert.AreEqual(0L, allocated,
            "Repeated frames measure allocation only, not physical report cadence or latency.");
    }

    private static void AssertRejected(Switch2RuntimeInputDevice device,
        Switch2RuntimeReportEventArgs envelope)
    {
        Assert.IsFalse(device.TryBorrowCurrentPublication(envelope, out var state, out bool hasMotion));
        Assert.IsNull(state);
        Assert.IsFalse(hasMotion);
    }

    private static Switch2RuntimeInputDevice CreateDevice()
    {
        Assert.IsTrue(Switch2RuntimeInputDevice.TryCreatePro(71_001, 71_002,
            Switch2Transport.Usb, out var device, out var failure), failure.ToString());
        device.StartUpdate();
        return device;
    }

    private static Switch2ProProfileInputFrame Frame(uint counter = 1, long timestamp = 100_000) =>
        Switch2RuntimeInputDeviceTests.CreateProFrame(71_001, 71_002,
            (uint)Switch2ProButton.FaceSouth, counter, leftX: 0x801,
            timestamp: timestamp, gyroscope: new Switch2Vector3Raw(700, 800, 900));
}
