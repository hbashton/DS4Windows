using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NintendoFixture = DS4WindowsTests.Switch2AudioHapticsOutputTests.RuntimeFixture;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioHapticsSourceBoundaryTests
{
    [DataTestMethod]
    [DataRow("source")]
    [DataRow("mode")]
    [DataRow("disable")]
    [DataRow("stop")]
    public void ActualNintendoAdmissionRejectsCopiedFrameAfterBoundaryAndRevokesOldProducer(string boundary)
    {
        using var nintendo = new NintendoFixture();
        nintendo.Activate();
        using var f = new Fixture(device: nintendo.Runtime, captureTimestamp: () => nintendo.CaptureTimestamp);
        f.Invoke("EnsureNintendoOutput");
        var oldOutput = f.Get<Switch2AudioHapticsOutput>("nintendoOutput");
        Assert.IsTrue(oldOutput.IsReady);
        nintendo.Samples.CopyTo(f.Get<byte[]>("writerFrame"), 0);
        long oldGeneration = f.Get<long>("captureGeneration");
        Assert.IsTrue(f.Publish(oldGeneration, f.Capture.CurrentSessionGeneration));
        Assert.IsTrue(nintendo.Lease.Calls > 0);

        if (boundary == "source") f.SourceChanged(f.Capture, Environment.ProcessId);
        else if (boundary == "stop") f.Runtime.Dispose();
        else
        {
            var next = f.Settings.Clone();
            if (boundary == "disable") next.Enabled = false;
            else next.Mode = AudioHapticsMode.Replace;
            f.Set("started", 1);
            Assert.IsTrue(f.Runtime.TryUpdateSettings(next, OutContType.ViiperDualSense, "", -1));
        }
        Assert.IsFalse(oldOutput.IsReady);
        f.Invoke("EnsureNintendoOutput");
        int calls = nintendo.Lease.Calls;
        Assert.IsFalse(f.Publish(oldGeneration, 1));
        Assert.AreEqual(calls, nintendo.Lease.Calls,
            "An old copied frame must not reach the actual physical lease after a new source/mode/stop boundary.");
        if (boundary is "source" or "mode")
        {
            Assert.IsTrue(f.Get<Switch2AudioHapticsOutput>("nintendoOutput").IsReady);
            Assert.IsTrue(f.Publish(f.Get<long>("captureGeneration"), f.Capture.CurrentSessionGeneration),
                "The current producer remains usable after the boundary.");
        }
    }

    [TestMethod]
    public void ActualNintendoAdmissionRejectsOldLeaseFrameBeforeSourceChangedNotification()
    {
        using var nintendo = new NintendoFixture();
        nintendo.Activate();
        using var f = new Fixture(device: nintendo.Runtime, captureTimestamp: () => nintendo.CaptureTimestamp);
        f.Invoke("EnsureNintendoOutput");
        nintendo.Samples.CopyTo(f.Get<byte[]>("writerFrame"), 0);
        long sameServiceGeneration = f.Get<long>("captureGeneration");
        Assert.IsTrue(f.Publish(sameServiceGeneration, 1));
        typeof(ProcessLoopbackWaveCapture).GetField("currentSessionGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Capture, 2L);
        int calls = nintendo.Lease.Calls;
        Assert.IsFalse(f.Publish(sameServiceGeneration, 1));
        Assert.AreEqual(calls, nintendo.Lease.Calls);
        Assert.IsTrue(f.Publish(sameServiceGeneration, 2));
    }

    [TestMethod]
    public void FirstPacketFromNewLeaseCannotCompletePreviousLeasesPartialFrame()
    {
        using var f = new Fixture();
        byte[] half = f.Pcm.AsSpan(0, 256 * 8).ToArray();
        f.Invoke("Capture_DataAvailable", f.Capture, new ProcessAudioWaveInEventArgs(half, half.Length, 1));
        Assert.AreEqual(32, f.Get<int>("captureFramePosition"));
        Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        long old = f.Get<long>("captureGeneration");
        typeof(ProcessLoopbackWaveCapture).GetField("currentSessionGeneration",
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Capture, 2L);
        f.Invoke("Capture_DataAvailable", f.Capture, new ProcessAudioWaveInEventArgs(half, half.Length, 2));
        Assert.AreNotEqual(old, f.Get<long>("captureGeneration"));
        Assert.AreEqual(32, f.Get<int>("captureFramePosition"));
        Assert.AreEqual(0, f.Get<int>("queuedFrames"), "Two source generations cannot form one output packet.");
        f.Invoke("Capture_DataAvailable", f.Capture, new ProcessAudioWaveInEventArgs(half, half.Length, 2));
        Assert.AreEqual(1, f.Get<int>("queuedFrames"));
        Assert.AreEqual(2L, f.Get<long[]>("frameQueueSessionGenerations")[0]);
    }

    [TestMethod]
    public void NintendoReadinessTracksCreatedActiveAndDisabledOutputWithoutStartingCapture()
    {
        using var nintendo = new NintendoFixture();
        using var f = new Fixture(device: nintendo.Runtime, captureTimestamp: () => nintendo.CaptureTimestamp);
        typeof(ProcessLoopbackWaveCapture).GetField("currentProcessId",
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Capture, Environment.ProcessId);
        f.Invoke("EnsureNintendoOutput");
        Assert.IsNotNull(f.Get<Switch2AudioHapticsOutput>("nintendoOutput"));
        Assert.IsFalse(f.Runtime.Status.Active);
        nintendo.Activate();
        f.Invoke("EnsureNintendoOutput");
        Assert.IsTrue(f.Runtime.Status.Active);
        Global.EnableOutputDataToDS4[0] = false;
        f.Invoke("EnsureNintendoOutput");
        Assert.IsFalse(f.Runtime.Status.Active);
    }

    [TestMethod]
    public void StoppedCallbackDoesNotWaitForLifecycleOwnerAndMailboxRetiresExactSource()
    {
        using var f = new Fixture();
        f.Deliver(f.Capture);
        object lifecycle = f.Get<object>("captureLifecycleLock");
        Task callback;
        lock (lifecycle)
        {
            callback = Task.Run(() => f.Invoke("Capture_RecordingStopped", f.Capture, new StoppedEventArgs()));
            Assert.IsTrue(callback.Wait(2000),
                "StopRecording may be waiting for this callback while its caller holds the lifecycle gate.");
            Assert.AreSame(f.Capture, f.Get<object>("stoppedCaptureSource"));
            Assert.AreSame(f.Capture, f.Get<object>("processCapture"), "The callback must not dispose its own capture.");
            Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        }
        f.Invoke("RetireStoppedCapture");
        Assert.IsNull(f.Get<object>("stoppedCaptureSource"));
        Assert.IsNull(f.Get<object>("processCapture"));
    }

    [TestMethod]
    public void NintendoBoundaryFixtureIgnoresSetupWallTimeButRejectsActualTwentyFourMillisecondAge()
    {
        using var nintendo = new NintendoFixture();
        nintendo.Activate();
        using var f = new Fixture(device: nintendo.Runtime, captureTimestamp: () => nintendo.CaptureTimestamp);
        f.Invoke("EnsureNintendoOutput");
        nintendo.Samples.CopyTo(f.Get<byte[]>("writerFrame"), 0);
        long generation = f.Get<long>("captureGeneration");
        long captured = nintendo.CaptureTimestamp;
        ulong capturedMicros = nintendo.NowMicroseconds;
        var setup = Stopwatch.StartNew();
        Thread.Sleep(50); // Deterministic simulated cold JIT/reflection/setup pause.
        Assert.IsTrue(setup.ElapsedMilliseconds >= 24);
        Assert.AreEqual(capturedMicros, nintendo.NowMicroseconds);
        Assert.IsTrue(f.PublishAt(generation, 1, captured),
            "Setup wall time cannot age a frame in a controlled-clock ownership test.");
        Assert.IsTrue(nintendo.Lease.Calls > 0);
        nintendo.AdvanceMicroseconds(23_999);
        Assert.IsTrue(f.PublishAt(generation, 1, captured));
        nintendo.AdvanceMicroseconds(1);
        int calls = nintendo.Lease.Calls;
        Assert.IsFalse(f.PublishAt(generation, 1, captured),
            "The production >=24 ms rejection is unchanged; only logical time advances it.");
        Assert.AreEqual(calls, nintendo.Lease.Calls);
        Assert.IsFalse(f.PublishAt(generation, 1, nintendo.CaptureTimestamp + Stopwatch.Frequency),
            "A controlled clock must not make future-dated samples admissible.");
    }

    [TestMethod]
    public void DefaultProductionAudioClockRejectsSamplesAgedByActualWallTime()
    {
        using var nintendo = new NintendoFixture(useManualAudioClock: false);
        nintendo.Activate();
        using var f = new Fixture(device: nintendo.Runtime);
        f.Invoke("EnsureNintendoOutput");
        Assert.IsTrue(f.Get<Switch2AudioHapticsOutput>("nintendoOutput").IsReady);
        nintendo.Samples.CopyTo(f.Get<byte[]>("writerFrame"), 0);
        long captured = Stopwatch.GetTimestamp();
        Thread.Sleep(50);
        int calls = nintendo.Lease.Calls;
        Assert.IsFalse(f.PublishAt(f.Get<long>("captureGeneration"), 1, captured));
        Assert.AreEqual(calls, nintendo.Lease.Calls,
            "A real-clock expired sample must never reach the physical lease.");
    }

    [TestMethod]
    public void ProfileProcessorRebuildObservesStoppedFormatUnderTheProcessingGate()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        object processing = f.Get<object>("captureProcessingLock");
        Task rebuild;
        lock (processing)
        {
            rebuild = Task.Run(() =>
            {
                entered.Set();
                f.Invoke("RebuildCaptureProcessor", f.Settings);
            });
            Assert.IsTrue(entered.Wait(2000));
            f.Invoke("Capture_RecordingStopped", f.Capture, new StoppedEventArgs());
            Assert.IsNull(f.Get<object>("captureFormat"));
        }
        Assert.IsTrue(rebuild.Wait(2000));
        Assert.IsNull(f.Get<object>("processor"),
            "A waiting profile update cannot rebuild a retired capture processor.");
        f.Bind(f.Capture);
        object previous = f.Get<object>("processor");
        f.Invoke("RebuildCaptureProcessor", f.Settings);
        Assert.IsNotNull(f.Get<object>("processor"));
        Assert.AreNotSame(previous, f.Get<object>("processor"),
            "The active-source positive control still rebuilds the processor.");
    }

    [TestMethod]
    public void CopiedStoppedCallbackCannotRetireSuccessorOrOverwriteItsMailbox()
    {
        using var f = new Fixture();
        object retired = new();
        f.Bind(retired);
        f.Retire(retired);
        f.Bind(f.Capture);
        f.Invoke("Capture_RecordingStopped", retired, new StoppedEventArgs());
        Assert.IsNull(f.Get<object>("stoppedCaptureSource"));
        Assert.AreSame(f.Capture, f.Get<object>("captureSource"));
        f.Deliver(f.Capture);
        Assert.IsTrue(f.Get<int>("queuedFrames") > 0);
        f.Invoke("Capture_RecordingStopped", f.Capture, new StoppedEventArgs());
        f.Invoke("Capture_RecordingStopped", retired, new StoppedEventArgs());
        Assert.AreSame(f.Capture, f.Get<object>("stoppedCaptureSource"));
    }

    [TestMethod]
    public void OldLeasePacketRetainsOriginGenerationAndCannotCrossSameSenderSourceSwitch()
    {
        using var f = new Fixture();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        Type leaseType = typeof(ProcessLoopbackWaveCapture).GetNestedType("ProcessCaptureLease", BindingFlags.NonPublic);
        object oldLease = RuntimeHelpers.GetUninitializedObject(leaseType);
        object newLease = RuntimeHelpers.GetUninitializedObject(leaseType);
        var sessionField = typeof(ProcessLoopbackWaveCapture).GetField("session", fields);
        var generationField = typeof(ProcessLoopbackWaveCapture).GetField("currentSessionGeneration", fields);
        var relay = typeof(ProcessLoopbackWaveCapture).GetMethod("OnSessionData", fields);
        WaveInEventArgs delayed = null;
        f.Capture.DataAvailable += (_, args) => delayed = args;
        sessionField.SetValue(f.Capture, oldLease);
        generationField.SetValue(f.Capture, 1L);
        relay.Invoke(f.Capture, new object[] { oldLease, 1L, f.Pcm, f.Pcm.Length });
        Assert.IsInstanceOfType(delayed, typeof(ProcessAudioWaveInEventArgs));
        Assert.AreEqual(1L, ((ProcessAudioWaveInEventArgs)delayed).SessionGeneration);

        sessionField.SetValue(f.Capture, newLease);
        generationField.SetValue(f.Capture, 2L);
        f.SourceChanged(f.Capture, Environment.ProcessId);
        f.Invoke("Capture_DataAvailable", f.Capture, delayed);
        Assert.AreEqual(0L, f.Get<long>("capturedPackets"));
        delayed = null;
        relay.Invoke(f.Capture, new object[] { oldLease, 1L, f.Pcm, f.Pcm.Length });
        Assert.IsNull(delayed, "The relay must not restamp an old lease callback as the current generation.");
        relay.Invoke(f.Capture, new object[] { newLease, 2L, f.Pcm, f.Pcm.Length });
        Assert.AreEqual(2L, ((ProcessAudioWaveInEventArgs)delayed).SessionGeneration);
        f.Invoke("Capture_DataAvailable", f.Capture, delayed);
        Assert.IsTrue(f.Get<long>("capturedNonSilentPackets") > 0);
    }
    [TestMethod]
    public void RetiredCallbackCannotRepopulateFramesAndCurrentSourceStillProducesPcm()
    {
        using var f = new Fixture();
        object old = new();
        f.Bind(old);
        f.Deliver(old);
        Assert.IsTrue(f.Get<long>("capturedNonSilentPackets") > 0);
        long oldCount = f.Get<long>("capturedPackets");
        f.Retire(old);
        f.Bind(f.Capture);
        f.Deliver(old);
        Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        Assert.AreEqual(oldCount, f.Get<long>("capturedPackets"));
        f.Deliver(f.Capture);
        Assert.IsTrue(f.Get<int>("queuedFrames") > 0);
        Assert.IsTrue(f.Get<long>("capturedPackets") > oldCount);
    }

    [TestMethod]
    public void CopiedCallbackWaitingAtProcessingGateIsRejectedAfterSourceRetirement()
    {
        using var f = new Fixture();
        object old = new();
        f.Bind(old);
        object gate = f.Get<object>("captureProcessingLock");
        using var entered = new ManualResetEventSlim();
        Task callback;
        lock (gate)
        {
            callback = Task.Run(() => { entered.Set(); f.Deliver(old); });
            Assert.IsTrue(entered.Wait(2000));
            f.Retire(old);
            f.Bind(f.Capture);
        }
        Assert.IsTrue(callback.Wait(2000));
        Assert.AreEqual(0L, f.Get<long>("capturedPackets"));
        Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        f.Deliver(f.Capture);
        Assert.IsTrue(f.Get<long>("capturedPackets") > 0);
    }

    [TestMethod]
    public void ResetWaitsForEnteredPcmProcessingThenClearsItsFinalFrame()
    {
        using var f = new Fixture();
        object frameGate = f.Get<object>("frameLock");
        object processingGate = f.Get<object>("captureProcessingLock");
        Task callback = null, reset = null;
        Monitor.Enter(frameGate);
        try
        {
            callback = Task.Run(() => f.Deliver(f.Capture));
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                if (!Monitor.TryEnter(processingGate)) return true;
                Monitor.Exit(processingGate);
                return false;
            }, 2000), "The actual PCM callback must own processing while blocked at frame publication.");
            reset = Task.Run(() => f.Invoke("ResetCapturedFrames"));
        }
        finally { Monitor.Exit(frameGate); }
        Assert.IsTrue(callback.Wait(2000));
        Assert.IsTrue(reset.Wait(2000));
        Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        Assert.IsFalse(f.Get<bool>("latestFrameAvailable"));
        Assert.AreEqual(0, f.Get<int>("captureFramePosition"));
        f.Deliver(f.Capture);
        Assert.IsTrue(f.Get<int>("queuedFrames") > 0);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreatedProcessCaptureCannotReportActiveBeforeItsSourceStarts(bool automatic)
    {
        using var f = new Fixture(automatic);
        f.Deliver(f.Capture);
        long before = f.Get<long>("captureGeneration");
        f.SourceChanged(f.Capture, Environment.ProcessId);
        Assert.IsTrue(f.Get<long>("captureGeneration") != before);
        Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        Assert.IsFalse(f.Runtime.Status.Active,
            "A source notification is not proof that the selected capture/physical output is ready.");
        long current = f.Get<long>("captureGeneration");
        f.SourceChanged(new object(), 123);
        Assert.AreEqual(current, f.Get<long>("captureGeneration"));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ModeChangeOrDisableInvalidatesOldFramesBeforeUpdatingSettings(bool disable)
    {
        using var f = new Fixture();
        f.Deliver(f.Capture);
        Assert.IsTrue(f.Get<int>("queuedFrames") > 0);
        long old = f.Get<long>("captureGeneration");
        AudioHapticsProfileSettings next = f.Settings.Clone();
        if (disable) next.Enabled = false;
        else next.Mode = AudioHapticsMode.Replace;
        f.Set("started", 1);
        Assert.IsTrue(f.Runtime.TryUpdateSettings(next, OutContType.ViiperDualSense, "", -1));
        Assert.AreNotEqual(old, f.Get<long>("captureGeneration"));
        Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        f.Deliver(f.Capture);
        Assert.AreEqual(!disable, f.Get<int>("queuedFrames") > 0);
    }

    [TestMethod]
    public void StopRejectsCopiedSourceCallbacksAndCannotRearmFrames()
    {
        using var f = new Fixture();
        f.Deliver(f.Capture);
        f.Runtime.Dispose();
        long generation = f.Get<long>("captureGeneration");
        f.Deliver(f.Capture);
        f.SourceChanged(f.Capture, 123);
        Assert.AreEqual(0, f.Get<int>("queuedFrames"));
        Assert.AreEqual(generation, f.Get<long>("captureGeneration"));
        Assert.IsFalse(f.Runtime.Status.Active);
    }

    internal sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type RuntimeType = typeof(AudioHapticsService.SlotRuntime);
        internal readonly AudioHapticsService.SlotRuntime Runtime;
        internal readonly ProcessLoopbackWaveCapture Capture;
        internal readonly AudioHapticsProfileSettings Settings;
        private readonly Func<long> captureTimestamp;
        private readonly Func<long, bool, int, long, long, bool> publishNintendo;
        private readonly WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        private readonly byte[] pcm = CreatePcm();
        internal byte[] Pcm => pcm;

        internal Fixture(bool automatic = false, DS4Device device = null,
            Func<long> captureTimestamp = null)
        {
            this.captureTimestamp = captureTimestamp ?? Stopwatch.GetTimestamp;
            Settings = new AudioHapticsProfileSettings
            {
                Enabled = true, Source = AudioHapticsSourceKind.AppSession,
                AutomaticGameDetection = automatic, ProcessId = Environment.ProcessId,
                Mode = AudioHapticsMode.Mix,
            };
            // Constructor only: this never starts a capture/client or writer.
            Capture = new ProcessLoopbackWaveCapture(Environment.ProcessId);
            typeof(ProcessLoopbackWaveCapture).GetField("currentSessionGeneration", Flags).SetValue(Capture, 1L);
            Runtime = new AudioHapticsService.SlotRuntime(0, device, Settings,
                OutContType.ViiperDualSense, "", -1);
            publishNintendo = RuntimeType.GetMethod("TryPublishNintendoFrame", Flags)
                .CreateDelegate<Func<long, bool, int, long, long, bool>>(Runtime);
            Set("processCapture", Capture);
            Bind(Capture);
        }

        internal void Bind(object source) => Invoke("BindCaptureSource", source, format, Settings);
        internal void Retire(object source) => Invoke("RetireCaptureSource", source);
        internal void Deliver(object source) => Invoke("Capture_DataAvailable", source,
            ReferenceEquals(source, Capture) ? new ProcessAudioWaveInEventArgs(pcm, pcm.Length,
                Capture.CurrentSessionGeneration) : new WaveInEventArgs(pcm, pcm.Length));
        internal void SourceChanged(object source, int pid) => Invoke("ProcessCapture_SourceChanged", source,
            new ProcessAudioSourceChangedEventArgs(pid, "synthetic source", "test"));
        internal bool Publish(long generation, long leaseGeneration) =>
            PublishAt(generation, leaseGeneration, captureTimestamp());
        internal bool PublishAt(long generation, long leaseGeneration, long captured) =>
            publishNintendo(generation, true, 80, captured, leaseGeneration);
        internal T Get<T>(string field) => (T)RuntimeType.GetField(field, Flags).GetValue(Runtime);
        internal void Set(string field, object value) => RuntimeType.GetField(field, Flags).SetValue(Runtime, value);
        internal object Invoke(string method, params object[] args) => RuntimeType.GetMethod(method, Flags).Invoke(Runtime, args);
        public void Dispose() => Runtime.Dispose();

        private static byte[] CreatePcm()
        {
            byte[] result = new byte[4800 * 2 * sizeof(float)];
            for (int sample = 0; sample < 4800; sample++)
            {
                int value = BitConverter.SingleToInt32Bits((float)(0.8 * Math.Sin(sample * 2 * Math.PI * 90 / 48000)));
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(sample * 8), value);
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(sample * 8 + 4), value);
            }
            return result;
        }
    }
}
