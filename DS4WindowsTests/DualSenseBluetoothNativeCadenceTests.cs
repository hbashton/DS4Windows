using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Fixture = DS4Windows.Tests.DualSenseBluetoothNativeIdleRetryTests.Fixture;

namespace DS4Windows.Tests;

[TestClass]
public class DualSenseBluetoothNativeCadenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void V5MediaClockCannotDrainEven100HzNativeCommandsWithoutIndependentControlSlots()
    {
        // Actual production clock, virtual microseconds, no wall-clock sleeps.
        var clock = new DualSenseV5NativePresentationScheduler(1_000_000);
        clock.Start(1);
        int mediaReports = 0;
        while (clock.NextDeadlineQpc < 1_600_001)
        {
            mediaReports++;
            clock.AdvanceAfterSend(clock.NextDeadlineQpc);
        }
        Assert.AreEqual(150, mediaReports);
        Assert.AreEqual(10, 160 - mediaReports,
            "One native command per media frame accumulates ten commands per 1.6 seconds at only 100 Hz.");
    }

    [DataTestMethod]
    [DataRow(false, false, 0, 5_000L, true)]
    [DataRow(true, false, 0, 5_000L, false)]
    [DataRow(true, false, 0, 10_000L, true)]
    [DataRow(true, false, 0, 12_000L, true)]
    [DataRow(true, true, 0, 5_000L, true)]
    [DataRow(true, false, 8, 5_000L, true)]
    public void ActualHelperPrefersDueOrPrimingMediaButAllowsNativeBetweenDeadlines(
        bool native, bool priming, int startupRemaining, long now, bool expectedPiggyback)
    {
        using var fixture = new Fixture();
        fixture.QueueSpeakerReports(8);
        if (native) fixture.ReceiveNativeCommand(Trigger(1));
        else fixture.ReceiveLocalState(Trigger(1));
        object host = GetHost(fixture);
        host.GetType().GetField("primeRequired", Private).SetValue(host, priming);
        var clock = new DualSenseV5NativePresentationScheduler(1_000_000);
        clock.Start(0);
        clock.AdvanceAfterSend(0);
        bool actual = (bool)host.GetType().GetMethod("MustPiggybackControllerStateLocked", Private)
            .Invoke(host, new object[] { clock, startupRemaining, now });
        Assert.AreEqual(expectedPiggyback, actual);
        Assert.AreEqual(10_000L, clock.NextDeadlineQpc,
            "Choosing a control slot must not mutate the media deadline.");
    }

    [TestMethod]
    public void ActualMediaWaitYieldsToNewWorkWithoutPretendingMediaIsDue()
    {
        using var fixture = new Fixture();
        using var stop = new ManualResetEvent(false);
        using var changed = new AutoResetEvent(true);
        object host = GetHost(fixture);
        MethodInfo wait = host.GetType().GetMethod("WaitUntil", BindingFlags.Static | BindingFlags.NonPublic);
        bool due = (bool)wait.Invoke(null, new object[]
        {
            IntPtr.Zero, Stopwatch.GetTimestamp() + Stopwatch.Frequency,
            stop, new WaitHandle[] { stop, changed }
        });
        Assert.IsFalse(due, "New native work must interrupt a future media wait, not consume that media slot.");
        changed.Set();
        due = (bool)wait.Invoke(null, new object[]
        {
            IntPtr.Zero, Stopwatch.GetTimestamp() - 1,
            stop, new WaitHandle[] { stop, changed }
        });
        Assert.IsTrue(due, "Already-due media has priority even when new control work is signaled.");
    }

    [DataTestMethod]
    [DataRow(1, 0, false)]
    [DataRow(0, 0, false)]
    [DataRow(0, 2, true)]
    [DataRow(0, 1, true)]
    [DataRow(-1, 0, true)]
    public void ActualFinalMediaDequeueRechecksLateMicrophoneBarrier(int pendingStatus, int reportsAhead, bool eligible)
    {
        using var fixture = new Fixture();
        fixture.QueueSpeakerReports(8);
        object host = GetHost(fixture);
        Type hostType = host.GetType();
        object reservoir = hostType.GetField("reservoir", Private).GetValue(host);
        PropertyInfo count = reservoir.GetType().GetProperty("Count");
        hostType.GetField("pendingMicrophoneStatus", Private).SetValue(host, pendingStatus);
        hostType.GetField("microphoneStatusReportsAhead", Private).SetValue(host, reportsAhead);
        object[] arguments = { null };
        bool dequeued = (bool)hostType.GetMethod("TryDequeueReportForPresentationLocked", Private).Invoke(host, arguments);
        Assert.AreEqual(eligible, dequeued);
        Assert.AreEqual(eligible ? 7 : 8, (int)count.GetValue(reservoir),
            "A late ready 0x32 must leave the exact next media interval unconsumed; owed FE frames must still proceed.");
        Assert.AreEqual(eligible, arguments[0] != null);
    }

    [DataTestMethod]
    [DataRow(200, 9, 6, true)]
    [DataRow(200, 10, 10, true)]
    [DataRow(200, 12, 10, true)]
    [DataRow(500, 9, 3, true)]
    [DataRow(500, 10, 10, true)]
    [DataRow(500, 12, 10, true)]
    [DataRow(500, 9, 10, false)]
    public void ActualWakeSelectionCannotLivelockWhenBothMediaAndControlAreOverdue(
        int nativeRateHz, int nowMilliseconds, int expectedMilliseconds, bool creditAvailable)
    {
        using var fixture = new Fixture(nativeCommandRateHz: nativeRateHz);
        fixture.QueueSpeakerReports(8);
        fixture.ReceiveNativeCommand(Trigger(1));
        object host = GetHost(fixture);
        Type hostType = host.GetType();
        long millisecond = Stopwatch.Frequency / 1000;
        hostType.GetField("lastControllerStateSubmissionQpc", Private).SetValue(host, millisecond);
        hostType.GetField("nativeCommandCreditAvailable", Private).SetValue(host, creditAvailable);
        long selected = (long)hostType.GetMethod("SelectV5PresentationWakeDeadlineLocked", Private)
            .Invoke(host, new object[] { nowMilliseconds * millisecond, 10 * millisecond });
        Assert.AreEqual(expectedMilliseconds * millisecond, selected,
            "Only future media may yield to an earlier control deadline. Overdue media must remain selectable.");
        Assert.AreEqual((31, 1, 1, 0L), fixture.NativeOwnershipSnapshot());
    }

    [TestMethod]
    public void ActualHelperNativeBurstUsesControlGapsWithoutStretchingPcmOrLosingTriggerEdges()
    {
        using var fixture = new Fixture();
        fixture.QueueSpeakerReports(64);
        var pcm = (DualSenseRealtimeHapticsSharedRing)typeof(Fixture)
            .GetField("realtime", Private).GetValue(fixture);
        byte[][] waveforms = new[] { Filled(0x55), Filled(0xAA), Filled(0x19), Filled(0) };
        int startupSubmissions = 0;
        Exception admissionError = null;
        Action feed = null;
        feed = () =>
        {
            if (++startupSubmissions < 8)
            {
                fixture.Native.DuringSubmit = feed;
                return;
            }
            try
            {
                // Admit only after the final startup frame has been composed.
                // All controls and PCM below therefore target steady media.
                foreach (byte[] waveform in waveforms)
                    Assert.IsTrue(pcm.Publish(waveform, 0, 1, long.MaxValue, Stopwatch.GetTimestamp()));
                for (byte marker = 1; marker <= 32; marker++)
                    fixture.ReceiveNativeCommand(Trigger(marker));
            }
            catch (Exception error) { admissionError = error; }
        };
        fixture.Native.DuringSubmit = feed;
        fixture.StartIdle();
        _ = SpinWait.SpinUntil(() => fixture.Native.Reports.Count(report => report[0] == 0x36) >= 64, 3000);
        fixture.Stop();
        Assert.IsNull(admissionError, admissionError?.ToString());
        byte[][] reports = fixture.Native.Reports.ToArray();
        byte[][] media = reports.Where(report => report[0] == 0x36).ToArray();
        Assert.AreEqual(64, media.Length);
        for (int index = 0; index < media.Length; index++)
            Assert.IsTrue(media[index].AsSpan(144, 200).ToArray().All(value => value == index + 1),
                "Every original encoded media frame must retain its payload and order.");
        for (int index = 0; index < waveforms.Length; index++)
            CollectionAssert.AreEqual(waveforms[index], media[index + 8].AsSpan(78, 64).ToArray(),
                "Interleaved control writes must not consume, overwrite or stretch the finite PCM sequence.");

        byte[][] controls = reports.Where(report => (report[StateOffset(report)] & 4) != 0).ToArray();
        Assert.AreEqual(32, controls.Length, "Every exact trigger command must reach the physical writer once.");
        for (int index = 0; index < controls.Length; index++)
            CollectionAssert.AreEqual(Trigger((byte)(index + 1)).AsSpan(11, 11).ToArray(),
                controls[index].AsSpan(StateOffset(controls[index]) + 10, 11).ToArray());
        Assert.IsTrue(controls.Any(report => report[0] == 0x31),
            "A steady-media native burst must be able to use the independent control lane.");
        int mediaSeen = 0;
        int nativeSeen = 0;
        int lastNativeMediaIndex = 0;
        foreach (byte[] report in reports)
        {
            if (report[0] == 0x36) mediaSeen++;
            if ((report[StateOffset(report)] & 4) != 0 && ++nativeSeen == 32)
                lastNativeMediaIndex = mediaSeen;
        }
        Assert.IsTrue(lastNativeMediaIndex <= 32,
            $"The native FIFO must drain faster than the media clock; last command arrived at media {lastNativeMediaIndex}. " +
            "The RC4.5.6 one-command-per-media policy needs media 40 for this burst.");
        Assert.AreEqual((32, 0, 0, 0L), fixture.NativeOwnershipSnapshot());
    }

    [DataTestMethod]
    [DataRow(100, 320)]
    [DataRow(200, 120)]
    public void ActualHelperPacedNativeSourceDoesNotAccumulateAMediaRateBacklog(int sourceHz, int commands)
    {
        using var fixture = new Fixture(realtimeCapacity: 16);
        object host = GetHost(fixture);
        object mediaQueue = host.GetType().GetField("reservoir", Private).GetValue(host);
        PropertyInfo mediaCount = mediaQueue.GetType().GetProperty("Count");
        var pcm = (DualSenseRealtimeHapticsSharedRing)typeof(Fixture)
            .GetField("realtime", Private).GetValue(fixture);
        var waveforms = new List<byte[]>();
        var expected = new List<byte[]>();
        int highWater = 0;

        // A real production helper and synthetic successful HID completions.
        // Source PCM/media stay independently available; only native commands
        // arrive at the requested 100/200 Hz. Never publish into a full ring.
        void RefillMedia()
        {
            while ((int)mediaCount.GetValue(mediaQueue) < 16)
                fixture.QueueSpeakerReports(1);
            while (pcm.Count < pcm.Capacity)
            {
                byte[] waveform = Filled((byte)(waveforms.Count % 4 == 3 ? 0 : waveforms.Count % 251 + 1));
                Assert.IsTrue(pcm.Publish(waveform, 0, 1, long.MaxValue, Stopwatch.GetTimestamp()));
                waveforms.Add(waveform);
            }
        }

        RefillMedia();
        fixture.StartAcknowledgements();
        fixture.StartIdle();
        long interval = Stopwatch.Frequency / sourceHz;
        long nextSource = Stopwatch.GetTimestamp();
        long abortAt = nextSource + Stopwatch.Frequency * 12;
        for (int index = 0; index < commands; index++)
        {
            while (Stopwatch.GetTimestamp() < nextSource)
            {
                RefillMedia();
                Thread.Sleep(1);
            }
            while (fixture.NativeOwnershipSnapshot().Admissions >= 32)
            {
                highWater = 32;
                RefillMedia();
                Assert.IsTrue(Stopwatch.GetTimestamp() < abortAt,
                    $"Native credits did not return at source {index}/{commands}; {DescribeHost(host, fixture)}");
                Thread.Sleep(1);
            }
            byte[] command = Trigger((byte)(index + 1));
            command[13] = (byte)((index + 1) >> 8);
            fixture.ReceiveNativeCommand(command);
            expected.Add(command);
            highWater = Math.Max(highWater, fixture.NativeOwnershipSnapshot().Admissions);
            RefillMedia();
            // A late source wake reanchors; it never invents a catch-up burst.
            nextSource += interval;
            if (nextSource < Stopwatch.GetTimestamp())
                nextSource = Stopwatch.GetTimestamp() + interval;
        }
        _ = SpinWait.SpinUntil(() => fixture.NativeOwnershipSnapshot().Admissions == 0 && pcm.Count == 0, 3000);
        fixture.Stop();
        byte[][] reports = fixture.Native.Reports.ToArray();
        byte[][] media = reports.Where(report => report[0] == 0x36).ToArray();
        byte[][] native = reports.Where(report => (report[StateOffset(report)] & 4) != 0).ToArray();
        Assert.AreEqual(commands, native.Length);
        for (int index = 0; index < commands; index++)
            CollectionAssert.AreEqual(expected[index].AsSpan(11, 11).ToArray(),
                native[index].AsSpan(StateOffset(native[index]) + 10, 11).ToArray());
        Assert.AreEqual((long)waveforms.Count, pcm.PresentedCount);
        for (int index = 0; index < waveforms.Count; index++)
            CollectionAssert.AreEqual(waveforms[index], media[index].AsSpan(78, 64).ToArray(),
                "Paced native control must not drop, reorder or replace finite PCM, including its zero boundaries.");
        Assert.IsTrue(highWater < 16,
            $"A {sourceHz} Hz source accumulated {highWater}/32 native commands behind independently available media.");
        Assert.AreEqual((32, 0, 0, 0L), fixture.NativeOwnershipSnapshot());
    }

    private static object GetHost(Fixture fixture) =>
        typeof(Fixture).GetField("host", Private).GetValue(fixture);

    private static string DescribeHost(object host, Fixture fixture)
    {
        Type type = host.GetType();
        return $"ownership={fixture.NativeOwnershipSnapshot()}, physical={fixture.Native.Reports.Count}, " +
            $"stop={((WaitHandle)type.GetField("stopRequested", Private).GetValue(host)).WaitOne(0)}, " +
            $"pacerAlive={((Thread)type.GetField("pacerThread", Private).GetValue(host)).IsAlive}, " +
            $"ackAlive={((Thread)type.GetField("acknowledgementThread", Private).GetValue(host)).IsAlive}, " +
            $"acks={fixture.AcknowledgementCount}, ahead={fixture.ReportsAhead}, qpc={Stopwatch.GetTimestamp()}, " +
            $"thread={((Thread)type.GetField("pacerThread", Private).GetValue(host)).ThreadState}, " +
            DescribeScalars(host) + "; writer=" + DescribeScalars(type.GetField("writer", Private).GetValue(host));
    }

    private static string DescribeScalars(object instance) => string.Join(", ", instance.GetType()
        .GetFields(Private).Where(field => field.FieldType.IsPrimitive)
        .Select(field => field.Name + "=" + field.GetValue(instance)));

    private static int StateOffset(byte[] report) => report[0] == 0x31 ? 3 : 13;
    private static byte[] Filled(byte value) => Enumerable.Repeat(value, 64).ToArray();
    private static byte[] Trigger(byte marker)
    {
        byte[] command = new byte[48];
        command[0] = 2;
        command[1] = 4;
        command[11] = 1;
        command[12] = marker;
        command[13] = 0x40;
        return command;
    }
}
