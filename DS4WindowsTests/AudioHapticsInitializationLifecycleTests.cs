using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;
using Fixture = DS4Windows.Tests.AudioHapticsSourceBoundaryTests.Fixture;
using NintendoFixture = DS4WindowsTests.Switch2AudioHapticsOutputTests.RuntimeFixture;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioHapticsInitializationLifecycleTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CaptureFailureBeforeStartClearsPublishedOwnerEvenWhenCleanupAlsoFails(bool cleanupThrows)
    {
        using var f = new Fixture();
        f.Deliver(f.Capture);
        if (cleanupThrows) MakeCaptureDisposalFail(f.Capture);
        var original = new InvalidOperationException("endpoint vanished before StartRecording");
        var invocation = Assert.ThrowsException<TargetInvocationException>(() =>
            f.Invoke("InitializeCapture", (Action)(() => throw original)));
        Assert.AreSame(original, invocation.InnerException,
            "A cleanup failure must not replace the actual initialization error.");
        Assert.IsNull(f.Get<object>("processCapture"));
        Assert.IsNull(f.Get<object>("captureSource"));
        Assert.IsNull(f.Get<object>("captureFormat"));
        Assert.IsNull(f.Get<object>("processor"));
        Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        Assert.IsFalse(f.Get<bool>("latestFrameAvailable"));

        // Positive control: the same cold boundary can bind a replacement;
        // construction only, with no OS capture or controller I/O.
        var replacement = new ProcessLoopbackWaveCapture(Environment.ProcessId);
        f.Invoke("InitializeCapture", (Action)(() =>
        {
            f.Set("processCapture", replacement);
            f.Bind(replacement);
        }));
        Assert.AreSame(replacement, f.Get<object>("processCapture"));
        Assert.AreSame(replacement, f.Get<object>("captureSource"));
        f.Invoke("RetireProcessCapture", true);
    }

    [TestMethod]
    public void RepeatedSourceFailureKeepsRetryDeadlineAndThrottlesLogWithoutHidingStatus()
    {
        using var f = new Fixture();
        int logs = 0;
        EventHandler<DebugEventArgs> handler = (_, _) => logs++;
        AppLogger.GuiLog += handler;
        try
        {
            long before = Stopwatch.GetTimestamp();
            for (int i = 0; i < 20; i++)
                f.Invoke("ReportCaptureStartFailure", new InvalidOperationException("missing endpoint"), false);
            Assert.AreEqual(1, logs);
            Assert.IsTrue(f.Get<long>("nextCaptureRetryTimestamp") > before);
            Assert.IsTrue(f.Get<long>("nextCaptureFailureLogTimestamp") >
                f.Get<long>("nextCaptureRetryTimestamp"));
            StringAssert.Contains(f.Runtime.Status.Message, "missing endpoint");
            Assert.IsFalse(f.Runtime.Status.Active);
            f.Set("nextCaptureFailureLogTimestamp", 0L);
            f.Invoke("ReportCaptureStartFailure", new InvalidOperationException("still missing"), false);
            Assert.AreEqual(2, logs, "A persistent error is reported again after the throttle window.");
        }
        finally { AppLogger.GuiLog -= handler; }
    }

    [TestMethod]
    public void WriterFailureRevokesNintendoProducerRejectsStalePcmAndRequestsExplicitRebuild()
    {
        using var nintendo = new NintendoFixture();
        nintendo.Activate();
        using var f = new Fixture(device: nintendo.Runtime);
        f.Set("started", 1);
        f.Invoke("EnsureNintendoOutput");
        var oldOutput = f.Get<Switch2AudioHapticsOutput>("nintendoOutput");
        Assert.IsTrue(oldOutput.IsReady);
        f.Deliver(f.Capture);
        EventHandler<DebugEventArgs> failedUi = (_, _) => throw new ObjectDisposedException("retired UI");
        AppLogger.GuiLog += failedUi;
        try
        {
            // This is the same wrapper invoked by the dedicated writer Thread.
            f.Invoke("RunWriter", (Action)(() => throw new InvalidOperationException("output rejected")));
        }
        finally { AppLogger.GuiLog -= failedUi; }
        Assert.AreEqual(1, f.Get<int>("writerFailed"));
        Assert.IsFalse(oldOutput.IsReady);
        Assert.IsNull(f.Get<object>("processCapture"));
        Assert.IsNull(f.Get<object>("captureSource"));
        Assert.IsFalse(f.Runtime.Status.Active);
        StringAssert.Contains(f.Runtime.Status.Message, "output rejected");
        f.Deliver(f.Capture);
        f.Invoke("EnsureNintendoOutput");
        Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        Assert.IsNull(f.Get<object>("nintendoOutput"));
        Assert.IsFalse(f.Runtime.ApplyToGameHaptics(new byte[64], 0));
        Assert.IsFalse(f.Runtime.TryUpdateSettings(f.Settings, OutContType.ViiperDualSense, "", -1),
            "The owning service must construct a new runtime, not accept settings on a dead writer.");
    }

    [TestMethod]
    public void NormalWriterBoundaryDoesNotManufactureFailure()
    {
        using var f = new Fixture();
        int calls = 0;
        f.Invoke("RunWriter", (Action)(() => calls++));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, f.Get<int>("writerFailed"));
        Assert.AreSame(f.Capture, f.Get<object>("processCapture"));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TerminalFaultNeutralizesUncertainFirstBluetoothWriteButDoesNotTouchUnownedFeedback(bool attempted)
    {
        // This matches the existing hardware-less Bluetooth transport fixture:
        // an initialized device around an unopened HID shell, never a real HID.
        var hid = (HidDevice)RuntimeHelpers.GetUninitializedObject(typeof(HidDevice));
        var device = new DualSenseDevice(hid, "Audio Haptics terminal fault test");
        typeof(DS4Device).GetField("conType", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(device, ConnectionType.BT);
        Assert.IsTrue(device.EnsureBluetoothCombinedOutputTransport());
        using var f = new Fixture(device: device);
        f.Set("bluetoothTransportReady", 1);
        byte[] cached = (byte[])typeof(DualSenseDevice).GetField("latestBluetoothCombinedSpeakerReport",
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(device);
        const int sampleOffset = 78;
        Array.Fill(cached, (byte)71, sampleOffset, 64);
        if (attempted)
        {
            Array.Fill(f.Get<byte[]>("writerFrame"), (byte)71);
            // Actual WriteBluetoothHapticsSamples copies the PCM and THEN reads
            // the output mailbox. Fault that dependency to cover uncertainty
            // between local side effect and returned admission result.
            typeof(DualSenseDevice).GetField("physicalOutputStateMailbox",
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(device, null);
            Assert.ThrowsException<TargetInvocationException>(() =>
                f.Invoke("PublishStandaloneSonyFrame", true, 71));
            Assert.IsFalse(f.Get<bool>("standaloneHapticsActive"));
            Assert.IsTrue(f.Get<bool>("standaloneMayOwnBluetoothHaptics"));
        }
        f.Invoke("RunWriter", (Action)(() => throw new InvalidOperationException("synthetic terminal fault")));
        byte expected = attempted ? (byte)0 : (byte)71;
        CollectionAssert.AreEqual(new byte[] { expected, expected, expected, expected },
            cached[sampleOffset..(sampleOffset + 4)]);
        Assert.AreEqual(attempted, f.Get<bool>("standaloneMayOwnBluetoothHaptics"),
            "An uncertain neutral keeps its reservation; a runtime that never published must not claim native feedback.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TerminalOutputStopClearsUsbQueueWithoutMutatingTheInFlightFrame(bool writerFault)
    {
        // No HID handle, Windows endpoint or renderer: the real queue/stop
        // helper can be exercised with an unstarted USB device and NAudio's
        // in-memory provider.
        var device = (DualSenseDevice)RuntimeHelpers.GetUninitializedObject(typeof(DualSenseDevice));
        typeof(DS4Device).GetField("conType", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(device, ConnectionType.USB);
        using var f = new Fixture(device: device);
        var provider = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 4));
        byte[] samples = new byte[256];
        Array.Fill(samples, (byte)37);
        provider.AddSamples(samples, 0, samples.Length);
        f.Set("usbProvider", provider);
        f.Set("standaloneHapticsActive", true);
        Array.Fill(f.Get<byte[]>("writerFrame"), (byte)71);
        f.Invoke("PublishStandaloneSonyFrame", true, 71);
        Assert.IsTrue(provider.BufferedBytes > samples.Length,
            "The positive control must use the actual USB conversion/publication helper.");
        if (writerFault)
            f.Invoke("RunWriter", (Action)(() => throw new InvalidOperationException("synthetic writer fault")));
        else f.Runtime.Dispose();
        Assert.AreEqual(0, provider.BufferedBytes);
        Assert.IsFalse(f.Get<bool>("standaloneHapticsActive"));
        CollectionAssert.AreEqual(new byte[] { 71, 71, 71, 71 }, f.Get<byte[]>("writerFrame")[..4],
            "Terminal neutralization must not modify the buffer an in-flight writer may be reading.");
        f.Set("usbProvider", provider);
        f.Invoke("PublishStandaloneSonyFrame", true, 71);
        Assert.AreEqual(0, provider.BufferedBytes, "An already-copied frame cannot rearm a failed/stopped output.");
    }

    [TestMethod]
    public void DisposalContinuesPastFailedCaptureCleanupAndPreservesPrimaryDiagnostic()
    {
        using var f = new Fixture();
        var provider = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 4));
        provider.AddSamples(new byte[128], 0, 128);
        f.Set("usbProvider", provider);
        MakeCaptureDisposalFail(f.Capture);
        int cleanupLogs = 0;
        int primaryLogs = 0;
        EventHandler<DebugEventArgs> log = (_, args) =>
        {
            string data = args.Data;
            if (data.Contains("cleanup failed")) cleanupLogs++;
            if (data.Contains("actual initialization failure")) primaryLogs++;
        };
        AppLogger.GuiLog += log;
        try
        {
            f.Invoke("RetireFailedCaptures");
            f.Invoke("ReportCaptureStartFailure", new InvalidOperationException("actual initialization failure"), false);
            Assert.AreEqual(1, cleanupLogs);
            Assert.AreEqual(1, primaryLogs, "A secondary disposal failure cannot consume the primary diagnostic's throttle.");
            // Rebind the fault-injected source to exercise Dispose's own
            // independent cleanup, not just initialization rollback.
            f.Set("processCapture", f.Capture);
            f.Runtime.Dispose();
            Assert.AreEqual(0, provider.BufferedBytes);
            Assert.IsNull(f.Get<object>("usbProvider"));
            Assert.IsNull(f.Get<object>("processCapture"));
        }
        finally { AppLogger.GuiLog -= log; }
    }

    [TestMethod]
    public void CopiedGameCarrierWaitingOnFrameGateCannotPublishAfterStopStarts()
    {
        using var f = new Fixture();
        f.Deliver(f.Capture);
        byte[] report = new byte[64];
        Array.Fill(report, (byte)23);
        bool accepted = true;
        Thread carrier;
        Task stop;
        object gate = f.Get<object>("frameLock");
        using var entered = new ManualResetEventSlim();
        lock (gate)
        {
            var carrierThread = new Thread(() =>
            {
                entered.Set();
                accepted = f.Runtime.ApplyToGameHaptics(report, 0);
            });
            carrierThread.Start();
            Assert.IsTrue(entered.Wait(2000));
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                (carrierThread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 2000),
                "The carrier must pass initial admission and block at the held frame gate.");
            carrier = carrierThread;
            stop = Task.Run(f.Runtime.Dispose);
            Assert.IsTrue(SpinWait.SpinUntil(() => f.Get<int>("disposed") != 0, 2000));
        }
        Assert.IsTrue(carrier.Join(2000));
        Assert.IsTrue(stop.Wait(2000));
        Assert.IsFalse(accepted);
        CollectionAssert.AreEqual(new byte[] { 23, 23, 23, 23 }, report[..4]);
    }

    private static void MakeCaptureDisposalFail(ProcessLoopbackWaveCapture capture)
    {
        // ProcessLoopbackWaveCapture and its events intentionally permit
        // repeated Dispose. Explicitly inject a missing cleanup dependency
        // after a clean retirement instead of assuming double-disposal fails.
        // This still runs the actual Dispose and capture-retirement callsites.
        capture.Dispose();
        typeof(ProcessLoopbackWaveCapture).GetField("reconnectRequested",
            BindingFlags.NonPublic | BindingFlags.Instance).SetValue(capture, null);
    }
}
