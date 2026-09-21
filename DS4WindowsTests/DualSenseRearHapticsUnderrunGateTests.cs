using System.IO.MemoryMappedFiles;
using System.Diagnostics;
using System.Reflection;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class DualSenseRearHapticsUnderrunGateTests
{
    // At this synthetic QPC frequency one block is exactly 32 ticks, and
    // the recent-publication window is exactly 96 ticks. No wall-clock wait.
    private const long Frequency = 3000;

    [TestMethod]
    public void EmptyRecentLaneHasOneAbsoluteBlockDeadline()
    {
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        long deadline = gate.Update(1000, false, true, 7, 990);
        Assert.AreEqual(1032L, deadline);
        for (long now = 1000; now < deadline; now++)
            for (int wake = 0; wake < 10; wake++)
                Assert.AreEqual(deadline, gate.Update(now, false, true, 7, 990),
                    "A data/control wake flood must not extend the absolute deadline.");
        Assert.AreEqual(0L, gate.Update(deadline, false, true, 7, 990));
        Assert.AreEqual(0L, gate.Update(deadline + 1, false, true, 7, 990));
        Assert.AreEqual(0L, gate.Update(deadline + 10, false, true, 7, 990));
    }

    [DataTestMethod]
    [DataRow(0L, 32L)]
    [DataRow(63L, 32L)]
    [DataRow(64L, 32L)]
    [DataRow(65L, 31L)]
    [DataRow(95L, 1L)]
    [DataRow(96L, 0L)]
    [DataRow(97L, 0L)]
    public void DeadlineNeverExceedsOneBlockOrThreeBlockRecentness(long age, long wait)
    {
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        long result = gate.Update(1000, false, true, 1, 1000 - age);
        Assert.AreEqual(wait == 0 ? 0 : 1000 + wait, result);
    }

    [DataTestMethod]
    [DataRow(0L, 990L)]
    [DataRow(-1L, 990L)]
    [DataRow(1L, 0L)]
    [DataRow(1L, -1L)]
    [DataRow(1L, 1001L)]
    public void MissingOrFuturePublicationDoesNotCreateAHold(long sequence, long enqueued)
    {
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        Assert.AreEqual(0L, gate.Update(1000, false, true, sequence, enqueued));
    }

    [TestMethod]
    public void ArithmeticAtEndOfQpcRangeFailsClosed()
    {
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        Assert.AreEqual(0L, gate.Update(long.MaxValue - 10, false, true, 1, long.MaxValue - 20));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DualSenseRearHapticsUnderrunGate(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DualSenseRearHapticsUnderrunGate(-1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DualSenseRearHapticsUnderrunGate(long.MaxValue));
    }

    [DataTestMethod]
    [DataRow(true, 0, 5000, 1000)]
    [DataRow(true, 0, 500, 500)]
    [DataRow(false, 0, 5000, 5000)]
    [DataRow(true, 1, 5000, 5000)]
    public void ActualHelperBusyNativeWakeIsBoundedWithoutChangingMediaDebt(
        bool activeRearGate, int reportsAhead, int mediaMicroseconds, int expectedMicroseconds)
    {
        using var fixture = new DS4Windows.Tests.DualSenseBluetoothNativeIdleRetryTests.Fixture();
        byte[] command = new byte[48];
        command[0] = 2;
        command[1] = 4;
        command[11] = 0x21;
        fixture.ReceiveNativeCommand(command);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object host = fixture.GetType().GetField("host", flags).GetValue(fixture);
        Type type = host.GetType();
        long millisecond = Math.Max(1, Stopwatch.Frequency / 1000);
        long now = 100 * millisecond;
        long mediaDeadline = now + millisecond * mediaMicroseconds / 1000;
        type.GetField("rearHapticsDeferralDeadlineQpc", flags).SetValue(host, activeRearGate ? mediaDeadline : 0L);
        type.GetField("nativeCommandCreditAvailable", flags).SetValue(host, false);
        type.GetField("nativeStateReportsAhead", flags).SetValue(host, reportsAhead);
        long actual = (long)type.GetMethod("SelectV5PresentationWakeDeadlineLocked", flags)
            .Invoke(host, new object[] { now, mediaDeadline });
        Assert.AreEqual(now + millisecond * expectedMicroseconds / 1000, actual,
            "An active empty-rear gate must retry native Busy credit within one millisecond without a wall-clock timing assertion.");
        Assert.AreEqual(reportsAhead, fixture.ReportsAhead);
        Assert.AreEqual((31, 1, 1, 0L), fixture.NativeOwnershipSnapshot());
    }

    [TestMethod]
    public void ReadyOrFutureGenerationCancelsWaitingWithoutRenewingTheSameSequence()
    {
        foreach (bool ready in new[] { false, true })
        {
            var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
            Assert.AreEqual(1032L, gate.Update(1000, false, true, 1, 990));
            Assert.AreEqual(0L, gate.Update(1001, ready, false, 1, 990));
            Assert.AreEqual(0L, gate.Update(1002, false, true, 1, 990));
        }
    }

    [TestMethod]
    public void NewCommittedSequenceAndLifecycleResetEachHaveTheirOwnBoundedOpportunity()
    {
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        Assert.AreEqual(1032L, gate.Update(1000, false, true, 1, 990));
        Assert.AreEqual(0L, gate.Update(1032, false, true, 1, 990));
        Assert.AreEqual(1072L, gate.Update(1040, false, true, 2, 1039));
        gate.Reset();
        Assert.AreEqual(1073L, gate.Update(1041, false, true, 2, 1039));
        gate.Reset();
        Assert.AreEqual(0L, gate.Update(1042, false, true, 0, 0));
    }

    [TestMethod]
    public void NoRearPublicationDoesNotTurnAControllerOrSpeakerOnlySessionIntoAWait()
    {
        using var ring = new RingFixture();
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        Assert.IsFalse(ring.Consumer.TryPrepareCurrentGeneration(1000,
            out bool empty, out long sequence, out long enqueued));
        Assert.IsTrue(empty);
        Assert.AreEqual(0L, sequence);
        Assert.AreEqual(0L, enqueued);
        Assert.AreEqual(0L, gate.Update(1000, false, empty, sequence, enqueued));
        Assert.IsFalse(ring.Consumer.DataAvailableSignal.WaitOne(0));
    }

    [DataTestMethod]
    [DataRow((byte)0)]
    [DataRow((byte)0xA7)]
    public void BindingAndPhysicalRetryRetainExactPayloadUntilCommitIncludingRealZero(byte fill)
    {
        using var ring = new RingFixture();
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        byte[] first = Enumerable.Repeat(fill, 64).ToArray();
        byte[] next = Enumerable.Repeat((byte)0x3B, 64).ToArray();
        byte[] report = Enumerable.Repeat((byte)0xCC, DualSenseBluetoothAudioPacer.ReportLength).ToArray();
        Assert.IsTrue(ring.Producer.Publish(first, 0, 1, long.MaxValue, 990));
        Assert.IsTrue(ring.Consumer.TryPrepareCurrentGeneration(1000,
            out bool empty, out long sequence, out long enqueued));
        Assert.IsFalse(empty);
        Assert.AreEqual(0L, sequence, "Preparation is not a physical acceptance.");
        Assert.AreEqual(0L, enqueued);
        Assert.AreEqual(1, ring.Producer.Count);
        Assert.AreEqual(0L, gate.Update(1000, true, empty, sequence, enqueued));
        Assert.IsTrue(ring.Producer.Publish(next, 0, 1, long.MaxValue, 1001));
        for (int retry = 0; retry < 3; retry++)
        {
            Assert.IsTrue(ring.Consumer.TryPrepareCurrentGeneration(1002 + retry, out empty, out sequence, out enqueued));
            Assert.AreEqual(0L, sequence);
            Assert.IsTrue(ring.Consumer.PrepareForPresentation(report, 1002 + retry));
            CollectionAssert.AreEqual(first, report[78..142]);
            Assert.AreEqual((byte)0xCC, report[0], "Prebinding must not consume/alter front or control data.");
            Assert.AreEqual(2, ring.Producer.Count);
            Assert.AreEqual(0L, ring.Consumer.PresentedCount);
        }
        ring.Consumer.CommitPrepared();
        Assert.AreEqual(1, ring.Producer.Count);
        Assert.IsTrue(ring.Consumer.TryPrepareCurrentGeneration(1006, out empty, out sequence, out enqueued));
        Assert.AreEqual(1L, sequence);
        Assert.AreEqual(990L, enqueued);
        Assert.IsTrue(ring.Consumer.PrepareForPresentation(report, 1006));
        CollectionAssert.AreEqual(next, report[78..142]);
        ring.Consumer.CommitPrepared();
        Assert.IsFalse(ring.Consumer.TryPrepareCurrentGeneration(1010, out empty, out sequence, out enqueued));
        Assert.IsTrue(empty);
        Assert.AreEqual(2L, sequence);
        Assert.AreEqual(1001L, enqueued);
        Assert.AreEqual(1042L, gate.Update(1010, false, empty, sequence, enqueued));
        ring.Consumer.CommitPrepared();
        Assert.AreEqual(2L, ring.Consumer.PresentedCount, "A duplicate commit cannot invent a new opportunity.");
    }

    [TestMethod]
    public void FutureGenerationIsNotEmptyAndMustNotHoldItsOrderedLifecycleCommand()
    {
        using var ring = new RingFixture();
        byte[] payload = new byte[64];
        Assert.IsTrue(ring.Producer.Publish(payload, 0, 1, long.MaxValue, 990));
        Assert.IsTrue(ring.Consumer.TryPrepareCurrentGeneration(995, out _, out _, out _));
        ring.Consumer.CommitPrepared();
        Assert.IsTrue(ring.Producer.Publish(payload, 0, 2, long.MaxValue, 999));
        Assert.IsFalse(ring.Consumer.TryPrepareCurrentGeneration(1000, out bool empty, out long sequence, out long enqueued));
        Assert.IsFalse(empty);
        Assert.AreEqual(1L, sequence);
        Assert.AreEqual(990L, enqueued);
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        Assert.AreEqual(0L, gate.Update(1000, false, empty, sequence, enqueued));
        Assert.AreEqual(1, ring.Producer.Count);
        ring.Consumer.AcceptGeneration(2, true);
        Assert.IsTrue(ring.Consumer.TryPrepareCurrentGeneration(1001, out empty, out sequence, out enqueued));
        Assert.AreEqual(0L, sequence);
        Assert.AreEqual(0L, enqueued);
        ring.Consumer.CommitPrepared();
        Assert.IsFalse(ring.Consumer.TryPrepareCurrentGeneration(1002, out empty, out sequence, out enqueued));
        Assert.AreEqual(2L, sequence);
        Assert.AreEqual(999L, enqueued);
    }

    [TestMethod]
    public void GenerationResetAndDiscardedOldPayloadDoNotRefreshCommittedRecency()
    {
        using var ring = new RingFixture();
        var payload = new byte[64];
        Assert.IsTrue(ring.Producer.Publish(payload, 0, 1, long.MaxValue, 990));
        Assert.IsTrue(ring.Consumer.TryPrepareCurrentGeneration(995, out _, out _, out _));
        ring.Consumer.CommitPrepared();
        Assert.IsTrue(ring.Producer.Publish(payload, 0, 1, long.MaxValue, 999));
        ring.Consumer.AcceptGeneration(2, true);
        Assert.IsFalse(ring.Consumer.TryPrepareCurrentGeneration(1000, out bool empty, out long sequence, out long enqueued));
        Assert.IsTrue(empty);
        Assert.AreEqual(0L, sequence);
        Assert.AreEqual(0L, enqueued);
        Assert.AreEqual(0, ring.Producer.Count);
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        Assert.AreEqual(0L, gate.Update(1000, false, empty, sequence, enqueued));
    }

    [TestMethod]
    public void DataWakeObservesPublishedSequenceMetadataAndAllPayloadBytes()
    {
        using var ring = new RingFixture();
        using var map = MemoryMappedFile.OpenExisting(ring.Producer.MapName, MemoryMappedFileRights.Read);
        using var view = map.CreateViewAccessor(0, 64 + 4 * 96, MemoryMappedFileAccess.Read);
        using var receiverReady = new ManualResetEventSlim(false);
        byte[] payload = Enumerable.Range(0, 64).Select(index => (byte)(index * 3)).ToArray();
        byte[] observed = new byte[64];
        long write = -1, published = -1, timestamp = -1;
        int generation = -1;
        Task receiver = Task.Run(() =>
        {
            receiverReady.Set();
            Assert.IsTrue(ring.Consumer.DataAvailableSignal.WaitOne(2000));
            Thread.MemoryBarrier();
            write = view.ReadInt64(16);
            published = view.ReadInt64(64);
            timestamp = view.ReadInt64(72);
            generation = view.ReadInt32(88);
            view.ReadArray(96, observed, 0, 64);
        });
        Assert.IsTrue(receiverReady.Wait(2000));
        Assert.IsTrue(ring.Producer.Publish(payload, 0, 1, long.MaxValue, 12345));
        Assert.IsTrue(receiver.Wait(2000));
        Assert.AreEqual(2, view.ReadInt32(4));
        Assert.AreEqual(1L, write);
        Assert.AreEqual(1L, published);
        Assert.AreEqual(12345L, timestamp);
        Assert.AreEqual(1, generation);
        CollectionAssert.AreEqual(payload, observed);
        Assert.IsFalse(ring.Consumer.DataAvailableSignal.WaitOne(0), "Data notification is an independent auto-reset wake.");
        Assert.AreEqual(1, ring.Producer.Count, "Observing a wake must not consume the source.");
    }

    [TestMethod]
    public void StopAndDataWaitsDoNotStealTheProducerSpaceCredit()
    {
        using var ring = new RingFixture();
        using var producerSpace = EventWaitHandle.OpenExisting(ring.Producer.SpaceAvailableName);
        Assert.AreNotSame(ring.Consumer.DataAvailableSignal, ring.Consumer.StopRequestedSignal);
        Assert.IsTrue(ring.Producer.Publish(new byte[64], 0, 1, long.MaxValue, 990));
        Assert.IsTrue(ring.Consumer.DataAvailableSignal.WaitOne(0));
        Assert.IsFalse(producerSpace.WaitOne(0));
        Assert.IsTrue(ring.Consumer.TryPrepareCurrentGeneration(1000, out _, out _, out _));
        ring.Consumer.CommitPrepared(); // Creates exactly one producer-space credit.
        ring.Producer.RequestStop();
        var waits = new[] { ring.Consumer.DataAvailableSignal, ring.Consumer.StopRequestedSignal };
        Assert.AreEqual(1, WaitHandle.WaitAny(waits, 0));
        Assert.IsTrue(ring.Consumer.StopRequestedSignal.WaitOne(0), "Stop is manual reset, not a stolen one-shot credit.");
        Assert.IsTrue(producerSpace.WaitOne(0), "The presenter never waits on the producer's space event.");
        Assert.IsFalse(producerSpace.WaitOne(0));
        Assert.IsFalse(ring.Producer.Publish(new byte[64], 0, 1, long.MaxValue, 1001));
        Assert.IsFalse(ring.Consumer.DataAvailableSignal.WaitOne(0));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WarmedGatePrebindRetryCommitAndDataWakeAllocateZero(bool positiveControl)
    {
        using var ring = new RingFixture();
        var gate = new DualSenseRearHapticsUnderrunGate(Frequency);
        byte[] payload = Enumerable.Repeat((byte)0x73, 64).ToArray();
        byte[] report = new byte[DualSenseBluetoothAudioPacer.ReportLength];
        bool succeeded = true;
        for (int index = 0; index < 6000; index++)
            succeeded &= LoadedCycle(ring, gate, payload, report, 1000 + index * 100L);
        long allocated;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 6000; index++)
                succeeded &= LoadedCycle(ring, gate, payload, report, 1_000_000 + index * 100L);
            if (positiveControl) GC.KeepAlive(new byte[128]);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.IsTrue(succeeded);
        if (positiveControl) Assert.IsTrue(allocated >= 128);
        else Assert.AreEqual(0L, allocated);
        CollectionAssert.AreEqual(payload, report[78..142]);
    }

    private static bool LoadedCycle(RingFixture ring, DualSenseRearHapticsUnderrunGate gate,
        byte[] payload, byte[] report, long now)
    {
        bool ok = ring.Producer.Publish(payload, 0, 1, long.MaxValue, now);
        ok &= ring.Consumer.DataAvailableSignal.WaitOne(0);
        bool ready = ring.Consumer.TryPrepareCurrentGeneration(now, out bool empty, out long sequence, out long enqueued);
        ok &= ready && !empty && gate.Update(now, ready, empty, sequence, enqueued) == 0;
        ok &= ring.Consumer.PrepareForPresentation(report, now);
        ok &= ring.Consumer.PrepareForPresentation(report, now + 1); // Accepted-write retry keeps ownership.
        ring.Consumer.CommitPrepared();
        ready = ring.Consumer.TryPrepareCurrentGeneration(now + 2, out empty, out sequence, out enqueued);
        long deadline = gate.Update(now + 2, ready, empty, sequence, enqueued);
        ok &= !ready && empty && deadline == now + 34;
        ok &= gate.Update(now + 3, false, true, sequence, enqueued) == deadline;
        ok &= gate.Update(deadline, false, true, sequence, enqueued) == 0;
        return ok;
    }

    private sealed class RingFixture : IDisposable
    {
        internal readonly DualSenseRealtimeHapticsSharedRing Producer;
        internal readonly DualSenseRealtimeHapticsSharedRing Consumer;

        internal RingFixture()
        {
            Producer = DualSenseRealtimeHapticsSharedRing.CreateOwner(
                "DS4Windows.Tests.RearUnderrun." + Guid.NewGuid().ToString("N"), 4);
            Consumer = DualSenseRealtimeHapticsSharedRing.OpenConsumer(Producer.MapName,
                Producer.SpaceAvailableName, Producer.StopRequestedName, 4);
        }

        public void Dispose()
        {
            Consumer.Dispose();
            Producer.Dispose();
        }
    }
}
