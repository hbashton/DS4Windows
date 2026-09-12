using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using DS4Windows.InputDevices;
using Fixture = DS4Windows.Tests.DualSenseBluetoothNativeIdleRetryTests.Fixture;

namespace DS4Windows.Tests;

/// <summary>
/// Real helper threads, timers, FIFO ownership and final writer composition;
/// synthetic native I/O only. These are software throughput/fairness tests,
/// not a measurement of Bluetooth transmission or controller input latency.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DualSenseBluetoothNativeRateBudgetTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int MediaFrames = 128;
    public TestContext TestContext { get; set; }

    [DataTestMethod]
    [DataRow(250, 500, true)]
    [DataRow(500, 1000, true)]
    [DataRow(500, 200, false)]
    public void FixedDeadlineDistinctCommandsExposeServiceBudgetWithoutSlowingTheSource(
        int offeredHz, int nativeBudgetHz, bool sufficientBudget)
    {
        using var fixture = new Fixture(realtimeCapacity: 16,
            nativeCommandRateHz: nativeBudgetHz, writeSlotCount: 32);
        using var recording = new Recording(fixture);
        var finiteMedia = new FiniteMedia(fixture, MediaFrames);
        byte[][] commands = Enumerable.Range(0, offeredHz)
            .Select(index => Command(index, stop: index == offeredHz - 1)).ToArray();
        finiteMedia.Refill();
        fixture.StartAcknowledgements();
        fixture.StartIdle();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count(
            report => report[0] == 0x36) >= 8, 3000), "The finite media prime did not complete.");

        // Every intended source deadline is anchored once, not reset after a
        // late wake or full queue. Thus backpressure is included in measured
        // effect age instead of silently lowering the offered source rate.
        long origin = Stopwatch.GetTimestamp();
        long finalDeadline = origin + (commands.Length - 1L) * Stopwatch.Frequency / offeredHz;
        long abortAt = finalDeadline + 6 * Stopwatch.Frequency;
        int highWater = 0;
        long maximumAdmissionLateness = 0;
        for (int index = 0; index < commands.Length; index++)
        {
            long deadline = origin + index * Stopwatch.Frequency / offeredHz;
            finiteMedia.Refill();
            WaitForSourceDeadline(deadline);
            while (fixture.NativeOwnershipSnapshot().Admissions >= DualSenseBluetoothAudioPacer.NativeCommandCapacity)
            {
                highWater = DualSenseBluetoothAudioPacer.NativeCommandCapacity;
                finiteMedia.Refill();
                Assert.IsTrue(Stopwatch.GetTimestamp() < abortAt,
                    "Native admission stopped making progress under a healthy synthetic writer.");
                Thread.Sleep(1);
            }
            fixture.ReceiveNativeCommand(commands[index]);
            maximumAdmissionLateness = Math.Max(maximumAdmissionLateness,
                Stopwatch.GetTimestamp() - deadline);
            highWater = Math.Max(highWater, fixture.NativeOwnershipSnapshot().Admissions);
        }

        while (fixture.NativeOwnershipSnapshot().Admissions != 0 ||
            fixture.Native.Reports.Count(report => report[0] == 0x36) < MediaFrames)
        {
            finiteMedia.Refill();
            Assert.IsTrue(Stopwatch.GetTimestamp() < abortAt,
                "Finite native/media work did not retire after the offered source ended.");
            Thread.Sleep(1);
        }
        fixture.Stop();

        (byte[] Report, long Qpc)[] writes = recording.Snapshot();
        var native = writes.Where(write => HasTriggerCommand(write.Report)).ToArray();
        Assert.AreEqual(commands.Length, native.Length,
            "Every distinct trigger command, including A/B/A and the final stop, must present once.");
        for (int index = 0; index < commands.Length; index++)
            AssertCommand(commands[index], native[index].Report);
        finiteMedia.AssertPresented(writes.Select(write => write.Report).ToArray());
        Assert.AreEqual((32, 0, 0, 0L), fixture.NativeOwnershipSnapshot());

        double maximumEffectAgeMs = native.Select((write, index) =>
            Milliseconds(write.Qpc - (origin + index * Stopwatch.Frequency / offeredHz))).Max();
        double finalEffectAgeMs = Milliseconds(native[^1].Qpc - finalDeadline);
        var media = writes.Where(write => write.Report[0] == 0x36).ToArray();
        double maximumSteadyMediaGapMs = Enumerable.Range(9, media.Length - 9)
            .Select(index => Milliseconds(media[index].Qpc - media[index - 1].Qpc)).Max();
        TestContext?.WriteLine($"offeredHz={offeredHz}; nativeBudgetHz={nativeBudgetHz}; " +
            $"commands={commands.Length}; highWater={highWater}; " +
            $"maximumAdmissionLatenessMs={Milliseconds(maximumAdmissionLateness):F3}; " +
            $"maximumEffectAgeMs={maximumEffectAgeMs:F3}; finalEffectAgeMs={finalEffectAgeMs:F3}; " +
            $"maximumSteadyMediaGapMs={maximumSteadyMediaGapMs:F3}");

        if (sufficientBudget)
        {
            Assert.IsTrue(highWater < 16, $"Native occupancy grew to {highWater}/32.");
            Assert.IsTrue(maximumEffectAgeMs < 80,
                $"Fixed-deadline commands accumulated {maximumEffectAgeMs:F3} ms of software age.");
            Assert.IsTrue(finalEffectAgeMs < 80,
                $"The final explicit stop was delayed {finalEffectAgeMs:F3} ms after its intended deadline.");
            Assert.IsTrue(maximumSteadyMediaGapMs < 80,
                $"Native work starved independently clocked media for {maximumSteadyMediaGapMs:F3} ms.");
        }
        else
        {
            // Positive bottleneck control: the exact same helper at its old
            // 200 Hz budget cannot service 500 distinct commands per second.
            Assert.AreEqual(32, highWater);
            Assert.IsTrue(finalEffectAgeMs > 500,
                "The insufficient-budget control unexpectedly hid source backlog.");
        }
    }

    [TestMethod]
    public void OptionalSingleNativeCreditPreservesCommandsWithoutBlockingFiniteMedia()
    {
        using var fixture = new Fixture(realtimeCapacity: 16,
            nativeCommandRateHz: 1000, writeSlotCount: 32, nativeCommandInFlightLimit: 1);
        using var recording = new Recording(fixture);
        byte[][] commands = { Command(0), Command(1), Command(2), Command(3, stop: true) };
        fixture.Native.HoldInitialSubmission = true;
        foreach (byte[] command in commands) fixture.ReceiveNativeCommand(command);
        fixture.StartAcknowledgements();
        fixture.StartIdle();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count >= 1, 3000));
        Assert.AreEqual((byte)0x31, fixture.Native.Reports.First()[0]);

        // This test explicitly selects one native credit; production retains
        // its existing ring-sized limit. Thirty-one physical slots are still
        // available, but the configured native credit is held. Media may use
        // other slots; it must not carry a
        // later native command around the outstanding command's completion.
        var finiteMedia = new FiniteMedia(fixture, 8);
        finiteMedia.Refill();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Native.Reports.Count(
            report => report[0] == 0x36) >= 8, 3000),
            "A pending native completion blocked independent finite media.");
        Assert.AreEqual(1, fixture.Native.Reports.Count(HasTriggerCommand),
            "A second native command passed the outstanding physical-native credit.");
        fixture.Native.ReturnCredit();
        Assert.IsTrue(SpinWait.SpinUntil(() => fixture.NativeOwnershipSnapshot().Admissions == 0, 3000),
            "Returning the native physical credit failed to resume the exact waiting head.");
        fixture.Stop();
        var writes = recording.Snapshot();
        byte[][] native = writes.Select(write => write.Report).Where(HasTriggerCommand).ToArray();
        Assert.AreEqual(commands.Length, native.Length);
        for (int index = 0; index < commands.Length; index++) AssertCommand(commands[index], native[index]);
        finiteMedia.AssertPresented(writes.Select(write => write.Report).ToArray());
        Assert.AreEqual((32, 0, 0, 0L), fixture.NativeOwnershipSnapshot());
    }

    private static void WaitForSourceDeadline(long deadline)
    {
        while (Stopwatch.GetTimestamp() < deadline)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining > Stopwatch.Frequency / 1000) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static int StateOffset(byte[] report) => report[0] == 0x31 ? 3 : 13;
    private static bool HasTriggerCommand(byte[] report) => (report[StateOffset(report)] & 0x04) != 0;

    private static byte[] Command(int index, bool stop = false)
    {
        byte[] command = new byte[48];
        command[0] = 0x02;
        command[1] = 0x0F;
        command[3] = stop ? (byte)0 : (byte)(1 + index % 127);
        command[4] = stop ? (byte)0 : (byte)(1 + index / 127);
        command[11] = stop ? (byte)0x05 : (byte)0x21;
        // The first three trigger programs are exactly A -> B -> A. Later
        // programs remain distinct across the low-byte wrap at 256.
        int marker = index == 2 ? 1 : index + 1;
        command[12] = stop ? (byte)0 : (byte)marker;
        command[13] = stop ? (byte)0 : (byte)(marker >> 8);
        command[22] = 0x05;
        return command;
    }

    private static void AssertCommand(byte[] expected, byte[] report)
    {
        int offset = StateOffset(report);
        Assert.AreEqual(expected[1] & 3, report[offset] & 3);
        Assert.AreEqual(expected[3], report[offset + 2]);
        Assert.AreEqual(expected[4], report[offset + 3]);
        CollectionAssert.AreEqual(expected.AsSpan(11, 11).ToArray(),
            report.AsSpan(offset + 10, 11).ToArray());
    }

    private sealed class Recording : IDisposable
    {
        private readonly Fixture fixture;
        private readonly ConcurrentQueue<long> timestamps = new();
        private readonly Action record;

        internal Recording(Fixture fixture)
        {
            this.fixture = fixture;
            record = Record;
            fixture.Native.DuringSubmit = record;
        }

        private void Record()
        {
            timestamps.Enqueue(Stopwatch.GetTimestamp());
            fixture.Native.DuringSubmit = record;
        }

        internal (byte[] Report, long Qpc)[] Snapshot()
        {
            byte[][] reports = fixture.Native.Reports.ToArray();
            long[] qpc = timestamps.ToArray();
            Assert.AreEqual(reports.Length, qpc.Length);
            return reports.Select((report, index) => (report, qpc[index])).ToArray();
        }

        public void Dispose() => fixture.Native.DuringSubmit = null;
    }

    private sealed class FiniteMedia
    {
        private readonly object host;
        private readonly object reservoir;
        private readonly PropertyInfo mediaCount;
        private readonly MethodInfo receive;
        private readonly DualSenseRealtimeHapticsSharedRing pcm;
        private readonly byte[][] mediaPayloads;
        private readonly byte[][] waveforms;
        private int mediaQueued;
        private int pcmQueued;

        internal FiniteMedia(Fixture fixture, int frames)
        {
            host = typeof(Fixture).GetField("host", Private)!.GetValue(fixture);
            reservoir = host.GetType().GetField("reservoir", Private)!.GetValue(host);
            mediaCount = reservoir.GetType().GetProperty("Count");
            receive = host.GetType().GetMethod("ReceiveQueuedReport", Private);
            pcm = (DualSenseRealtimeHapticsSharedRing)typeof(Fixture).GetField("realtime", Private)!.GetValue(fixture);
            mediaPayloads = new byte[frames][];
            waveforms = new byte[frames][];
            for (int index = 0; index < frames; index++)
            {
                byte[] payload = new byte[20 + DualSenseBluetoothAudioPacer.ReportLength];
                BinaryPrimitives.WriteInt64LittleEndian(payload, index + 1);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), 1);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12), long.MaxValue);
                Span<byte> report = payload.AsSpan(20);
                report[0] = 0x36;
                report[11] = 0x90;
                report[12] = 63;
                report[76] = 0x92;
                report[77] = 64;
                report[142] = 0x93;
                report[143] = 200;
                report.Slice(144, 200).Fill((byte)(index + 1));
                mediaPayloads[index] = payload;
                waveforms[index] = Enumerable.Repeat(
                    index % 4 == 3 ? (byte)0 : (byte)(index + 1), 64).ToArray();
            }
        }

        internal void Refill()
        {
            while (pcmQueued < waveforms.Length && pcm.Count < pcm.Capacity)
            {
                Assert.IsTrue(pcm.Publish(waveforms[pcmQueued], 0, 1,
                    long.MaxValue, Stopwatch.GetTimestamp()));
                pcmQueued++;
            }
            while (mediaQueued < mediaPayloads.Length && (int)mediaCount.GetValue(reservoir) < 16)
            {
                byte[] payload = mediaPayloads[mediaQueued++];
                receive.Invoke(host, new object[] { payload, payload.Length });
            }
        }

        internal void AssertPresented(byte[][] writes)
        {
            byte[][] media = writes.Where(report => report[0] == 0x36).ToArray();
            Assert.AreEqual(mediaPayloads.Length, media.Length);
            Assert.AreEqual((long)waveforms.Length, pcm.PresentedCount);
            for (int index = 0; index < media.Length; index++)
            {
                CollectionAssert.AreEqual(mediaPayloads[index].AsSpan(20 + 144, 200).ToArray(),
                    media[index].AsSpan(144, 200).ToArray(), "Finite speaker frames were changed or reordered.");
                CollectionAssert.AreEqual(waveforms[index], media[index].AsSpan(78, 64).ToArray(),
                    "Finite haptics, including zero boundaries, were consumed by the control lane or reordered.");
            }
        }
    }
}
