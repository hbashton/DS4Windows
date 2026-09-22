using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using DS4Windows.InputDevices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fixture = DS4Windows.Tests.DualSenseBluetoothNativeIdleRetryTests.Fixture;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public class DualSenseBluetoothRearUnderrunTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void ActualHelperDoesNotInsertAnUnauthoredRearBlockBetweenFiniteSourceBlocks()
    {
        using var fixture = new Fixture();
        var pcm = (DualSenseRealtimeHapticsSharedRing)typeof(Fixture)
            .GetField("realtime", Private)!.GetValue(fixture)!;
        object host = typeof(Fixture).GetField("host", Private)!.GetValue(fixture)!;
        FieldInfo nativeClaim = host.GetType().GetField("claimedNativeCommand", Private)!;
        byte[] first = Enumerable.Repeat((byte)0x21, 64).ToArray();
        byte[] second = Enumerable.Repeat((byte)0x43, 64).ToArray();
        byte[] trigger = new byte[48];
        trigger[0] = 0x02;
        trigger[1] = 0x04;
        trigger[11] = 0x01;
        trigger[12] = 0x37;
        trigger[13] = 0x40;

        Exception callbackError = null;
        bool firstPublished = false;
        bool triggerQueued = false;
        bool secondPublishedByNativeCommand = false;
        Action primeSource = null;
        primeSource = () =>
        {
            try
            {
                int previousMedia = fixture.Native.Reports.Count(report => report[0] == 0x36);
                if (!firstPublished && previousMedia == 7)
                {
                    // The eighth startup carrier has already been composed.
                    // A belongs to the first steady-state media interval.
                    Assert.IsTrue(pcm.Publish(first, 0, 1, long.MaxValue,
                        Stopwatch.GetTimestamp()));
                    firstPublished = true;
                }
            }
            catch (Exception error)
            {
                callbackError = error;
            }

            if (!firstPublished && callbackError == null)
                fixture.Native.DuringSubmit = primeSource;
        };

        Action inspectDueMedia = () =>
        {
            if (triggerQueued || pcm.PresentedCount != 1 || pcm.Count != 0)
                return;
            try
            {
                Assert.IsFalse(pcm.HasPreparedGeneration);
                // Real native work arrives at a due-media boundary, before any
                // dequeue/claim. Its accepted physical call releases the next
                // independent PCM block. No sleeps or writer-credit stalls
                // determine this ordering.
                fixture.ReceiveNativeCommand(trigger);
                triggerQueued = true;
                fixture.Native.DuringSubmit = () =>
                {
                    try
                    {
                        Assert.IsNotNull(nativeClaim.GetValue(host),
                            "B is released by the actual native command, not an arbitrary media write.");
                        Assert.AreEqual(0, pcm.Count);
                        Assert.IsTrue(pcm.Publish(second, 0, 1, long.MaxValue,
                            Stopwatch.GetTimestamp()));
                        secondPublishedByNativeCommand = true;
                    }
                    catch (Exception error) { callbackError = error; }
                };
            }
            catch (Exception error) { callbackError = error; }
        };
        host.GetType().GetField("BeforeRealtimeHapticsAvailabilityProbeTestHook", Private)!
            .SetValue(host, inspectDueMedia);

        fixture.QueueSpeakerReports(12);
        fixture.Native.DuringSubmit = primeSource;
        fixture.StartIdle();
        bool completed = SpinWait.SpinUntil(() =>
            fixture.Native.Reports.Count(report => report[0] == 0x36) >= 12, 3000);
        fixture.Stop();

        Assert.IsTrue(completed, "The real helper must finish the bounded speaker sequence.");
        Assert.IsNull(callbackError, callbackError?.ToString());
        Assert.IsTrue(firstPublished && triggerQueued && secondPublishedByNativeCommand,
            "The actual helper must traverse the intended finite A / empty / B source sequence.");
        byte[][] all = fixture.Native.Reports.ToArray();
        byte[][] media = all.Where(report => report[0] == 0x36).ToArray();
        Assert.AreEqual(12, media.Length);
        for (int index = 0; index < media.Length; index++)
            Assert.IsTrue(media[index].AsSpan(144, 200).ToArray().All(value => value == index + 1),
                $"Speaker packet {index} was dropped, replayed, or reordered.");

        byte[][] controls = all.Where(report =>
            (report[report[0] == 0x31 ? 3 : 13] & 0x04) != 0).ToArray();
        Assert.AreEqual(1, controls.Length, "The unrelated trigger command must be accepted exactly once.");
        int stateOffset = controls[0][0] == 0x31 ? 3 : 13;
        CollectionAssert.AreEqual(trigger.AsSpan(11, 11).ToArray(),
            controls[0].AsSpan(stateOffset + 10, 11).ToArray());
        Assert.AreEqual((32, 0, 0, 0L), fixture.NativeOwnershipSnapshot());
        Assert.AreEqual(2L, pcm.PresentedCount,
            "Only the two authored rear blocks may consume ring entries.");

        int firstIndex = Array.FindIndex(media, report => report.AsSpan(78, 64).SequenceEqual(first));
        int secondIndex = Array.FindIndex(media, report => report.AsSpan(78, 64).SequenceEqual(second));
        Assert.IsTrue(firstIndex >= 0 && secondIndex > firstIndex);
        Assert.AreEqual(firstIndex + 1, secondIndex,
            $"The source authored only A and B, but the physical helper inserted {secondIndex - firstIndex - 1} " +
            "extra rear interval(s). Front audio and the native trigger still progressed, so this is not a lost source block.");
    }

    [TestMethod]
    public void ActualHelperEndsABoundedRearWaitWhenNoMoreSourceArrives()
    {
        using var scenario = new Scenario();
        scenario.OnEmptyDue = () =>
        {
            scenario.Fixture.ReceiveNativeCommand(Trigger());
            scenario.OnNextSubmit(() =>
            {
                scenario.AssertNativeClaim();
                scenario.AssertDeferring();
                // Actual EOS/producer stall: deliberately never publish B.
            });
        };
        scenario.Start();
        byte[][] all = scenario.Finish(16);
        byte[][] media = Media(all);
        AssertFrontOrder(media);
        int a = RearIndex(media, scenario.A);
        Assert.AreEqual(8, a);
        Assert.IsTrue(media.Skip(a + 1).All(report => report.AsSpan(78, 64).ToArray().All(value => value == 0)),
            "After the finite wait expires, ordinary idle silence must resume without replaying A.");
        Assert.AreEqual(1L, scenario.Pcm.PresentedCount);
        Assert.AreEqual(1, scenario.ObservedDeadlines.Count,
            "The same committed source block must not renew an absolute wait on every idle carrier.");
        AssertOneTrigger(all);
    }

    [TestMethod]
    public void ActualHelperPreservesAnAuthoredZeroBetweenRearBlocks()
    {
        using var scenario = new Scenario();
        scenario.OnEmptyDue = () =>
        {
            scenario.Fixture.ReceiveNativeCommand(Trigger());
            scenario.OnNextSubmit(() =>
            {
                scenario.AssertNativeClaim();
                scenario.AssertDeferring();
                scenario.Publish(new byte[64]);
                scenario.Publish(scenario.B);
            });
        };
        scenario.Start();
        byte[][] all = scenario.Finish(16);
        byte[][] media = Media(all);
        AssertFrontOrder(media);
        int a = RearIndex(media, scenario.A);
        Assert.AreEqual(a + 2, RearIndex(media, scenario.B),
            "The one authored zero is real PCM: it must neither be dropped nor acquire an extra synthetic neighbor.");
        CollectionAssert.AreEqual(new byte[64], media[a + 1].AsSpan(78, 64).ToArray());
        Assert.AreEqual(3L, scenario.Pcm.PresentedCount);
        AssertOneTrigger(all);
    }

    [TestMethod]
    public void ActualHelperRetainsRearAndFrontOwnershipWhilePhysicalCreditIsBusy()
    {
        using var scenario = new Scenario();
        scenario.OnEmptyDue = () =>
        {
            scenario.Fixture.ReceiveNativeCommand(Trigger());
            scenario.OnNextSubmit(() =>
            {
                scenario.AssertNativeClaim();
                scenario.AssertDeferring();
                scenario.Publish(scenario.B);
                scenario.Fixture.Native.HoldInitialSubmission = true;
            });
        };
        scenario.Start();
        FieldInfo pendingEvent = scenario.Fixture.Native.GetType().GetField("slotEvent", Private)!;
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => scenario.Failure != null ||
                (IntPtr)pendingEvent.GetValue(scenario.Fixture.Native)! != IntPtr.Zero, 3000),
                "The independent native command must reach its deliberate synthetic pending write.");
            scenario.AssertNoFailure();
            scenario.Fixture.Native.ArmBusyProbe();
            Assert.IsTrue(SpinWait.SpinUntil(() => scenario.Failure != null ||
                (Volatile.Read(ref scenario.Fixture.Native.ProbeReturned) != 0 &&
                    scenario.Pcm.HasPreparedGeneration), 3000),
                "B must bind at the real presentation boundary while the oldest physical slot is unavailable.");
            scenario.AssertNoFailure();
            Assert.AreEqual(1L, scenario.Pcm.PresentedCount);
            Assert.AreEqual(9, Media(scenario.Fixture.Native.Reports.ToArray()).Length,
                "A Busy physical slot must not spend a front frame or send a synthetic rear carrier.");
            scenario.Publish(scenario.C);
            Assert.AreEqual(2, scenario.Pcm.Count,
                "The prepared B slot remains owned until physical acceptance, ahead of C.");
        }
        finally
        {
            scenario.Fixture.Native.ReturnCredit();
        }
        byte[][] all = scenario.Finish(16);
        byte[][] media = Media(all);
        AssertFrontOrder(media);
        int a = RearIndex(media, scenario.A);
        Assert.AreEqual(a + 1, RearIndex(media, scenario.B));
        Assert.AreEqual(a + 2, RearIndex(media, scenario.C));
        Assert.AreEqual(3L, scenario.Pcm.PresentedCount);
        AssertOneTrigger(all);
    }

    [TestMethod]
    public void ActualHelperClearCancelsRearDeferralAndRejectsTheOldGeneration()
    {
        using var scenario = new Scenario();
        scenario.OnEmptyDue = () =>
        {
            scenario.Fixture.ReceiveNativeCommand(Trigger());
            scenario.OnNextSubmit(() =>
            {
                scenario.AssertNativeClaim();
                scenario.AssertDeferring();
                scenario.Fixture.Clear();
                scenario.Publish(scenario.B, generation: 1);
                scenario.Publish(scenario.C, generation: 2);
                scenario.QueueNewEpochSpeakerReports();
            });
        };
        scenario.Start();
        byte[][] all = scenario.Finish(17);
        byte[][] media = Media(all);
        Assert.AreEqual(17, media.Length);
        AssertFrontOrder(media.Take(9).ToArray());
        AssertFrontOrder(media.Skip(9).ToArray(), firstMarker: 101);
        Assert.AreEqual(-1, RearIndex(media, scenario.B),
            "An old producer cannot make a held rear block cross the Clear/new-generation boundary.");
        Assert.AreEqual(9, RearIndex(media, scenario.C));
        Assert.AreEqual(2L, scenario.Pcm.PresentedCount);
        Assert.AreEqual(0, scenario.Pcm.Count);
        Assert.AreEqual(0L, scenario.DeferralDeadline,
            "A new lifecycle must not inherit the previous source's deferral deadline.");
    }

    [TestMethod]
    public void ActualHelperPresentsReadyMicrophoneControlDuringRearDeferral()
    {
        using var scenario = new Scenario();
        scenario.OnEmptyDue = () =>
        {
            scenario.Receive("ReceiveMicrophoneStatus", new byte[] { 1 });
            scenario.Fixture.ReceiveNativeCommand(Trigger());
            scenario.OnNextSubmit(() =>
            {
                Assert.IsNull(scenario.NativeClaim,
                    "The ready 0x32 microphone transition must retain priority over the native trigger.");
                scenario.AssertDeferring();
                scenario.Publish(scenario.B);
            });
        };
        scenario.Start();
        byte[][] all = scenario.Finish(16);
        byte[][] media = Media(all);
        AssertFrontOrder(media);
        int a = RearIndex(media, scenario.A);
        Assert.AreEqual(a + 1, RearIndex(media, scenario.B));
        byte[][] microphone = all.Where(report => report[0] == 0x32).ToArray();
        Assert.AreEqual(1, microphone.Length);
        int micIndex = Array.FindIndex(all, report => report[0] == 0x32);
        int bIndex = Array.FindIndex(all, report => report[0] == 0x36 &&
            report.AsSpan(78, 64).SequenceEqual(scenario.B));
        Assert.IsTrue(micIndex < bIndex,
            "A ready microphone mode command must not wait behind unavailable rear PCM.");
        AssertOneTrigger(all);
    }

    [TestMethod]
    public void ActualHelperRechecksBusyNativeCreditWithoutSpendingTheRearWait()
    {
        using var scenario = new Scenario();
        var writer = (DualSenseBluetoothRealtimeWriter)typeof(Fixture)
            .GetField("writer", Private)!.GetValue(scenario.Fixture)!;
        bool heldA = false;
        bool busyArmed = false;
        bool creditReturned = false;
        bool nativeAcceptedDuringWait = false;
        int presenterThread = 0;
        long armedQpc = 0, gateObservedQpc = 0, gateDeadlineQpc = 0;
        long busyQpc = 0, creditReturnedQpc = 0, nativeSubmitQpc = 0;
        scenario.OnEveryDue = () =>
        {
            if (!heldA && scenario.Pcm.Count == 1 && scenario.Pcm.PresentedCount == 0)
            {
                heldA = true;
                scenario.OnNextSubmit(() => scenario.Fixture.Native.HoldInitialSubmission = true);
            }
        };
        scenario.OnEmptyDue = () =>
        {
            presenterThread = Environment.CurrentManagedThreadId;
            // Stage the predecessor's completion on the presenter, before it
            // starts the real bounded gate. No test-thread scheduling belongs
            // inside the 10.667 ms credit/deferral contract under test.
            scenario.Fixture.Native.DuringCompletionProbe = () =>
                busyQpc = Stopwatch.GetTimestamp();
            armedQpc = Stopwatch.GetTimestamp();
            scenario.Fixture.Native.ArmBusyProbe();
            busyArmed = true;
            scenario.Fixture.ReceiveNativeCommand(Trigger());
            scenario.OnNextSubmit(() =>
            {
                nativeSubmitQpc = Stopwatch.GetTimestamp();
                Assert.AreEqual(presenterThread, Environment.CurrentManagedThreadId);
                Assert.IsTrue(creditReturned,
                    "The native command must first traverse a real Busy write and returned credit.");
                scenario.AssertNativeClaim();
                scenario.AssertDeferring();
                Assert.IsTrue(nativeSubmitQpc < gateDeadlineQpc,
                    "Native acceptance must precede the original absolute rear deadline.");
                Assert.AreEqual(9, Media(scenario.Fixture.Native.Reports.ToArray()).Length,
                    "Returned native credit must be observed without first spending a front/rear media interval.");
                scenario.Publish(scenario.B);
                nativeAcceptedDuringWait = true;
            });
        };
        scenario.SetBeforeNativeCreditProbe(() =>
        {
            if (!busyArmed || creditReturned) return;
            Assert.AreEqual(presenterThread, Environment.CurrentManagedThreadId);
            scenario.AssertDeferring();
            if (gateObservedQpc == 0)
            {
                gateObservedQpc = Stopwatch.GetTimestamp();
                gateDeadlineQpc = scenario.DeferralDeadline;
            }
            Assert.AreEqual(gateDeadlineQpc, scenario.DeferralDeadline,
                "A Busy retry must not renew the rear wait.");
            if (Volatile.Read(ref scenario.Fixture.Native.ProbeReturned) == 0) return;

            // This hook is after the previous writer call has fully returned,
            // not inside its completion callback. Prove the failed admission
            // left the same native command queued and spent no front/rear data.
            writer.GetOwnershipState(out bool disposed, out bool activeWrite, out _);
            Assert.IsFalse(disposed || activeWrite);
            Assert.AreEqual((31, 1, 1, 0L), scenario.Fixture.NativeOwnershipSnapshot());
            Assert.AreEqual(9, Media(scenario.Fixture.Native.Reports.ToArray()).Length);
            Assert.AreEqual(1L, scenario.Pcm.PresentedCount);
            Assert.AreEqual(0, scenario.Pcm.Count);
            Assert.IsFalse(scenario.Pcm.HasPreparedGeneration);
            // Only native credit returns: no rear/data/reservoir notification.
            scenario.Fixture.Native.ReturnCredit();
            creditReturnedQpc = Stopwatch.GetTimestamp();
            creditReturned = true;
        });
        byte[][] all;
        scenario.Start();
        try
        {
            all = scenario.Finish(16);
        }
        finally
        {
            scenario.Fixture.Native.ReturnCredit();
            TestContext?.WriteLine($"Busy/rear QPC frequency={Stopwatch.Frequency}; " +
                $"armed={armedQpc}; gateObserved={gateObservedQpc}; deadline={gateDeadlineQpc}; " +
                $"busyCompletion={busyQpc}; creditReturned={creditReturnedQpc}; nativeSubmit={nativeSubmitQpc}");
        }
        Assert.IsTrue(heldA && busyArmed && creditReturned && nativeAcceptedDuringWait);
        Assert.IsTrue(armedQpc <= gateObservedQpc && gateObservedQpc <= busyQpc &&
            busyQpc <= creditReturnedQpc && creditReturnedQpc <= nativeSubmitQpc &&
            nativeSubmitQpc < gateDeadlineQpc,
            "The actual Busy/retry/accept sequence must remain inside its unchanged absolute gate.");
        byte[][] media = Media(all);
        AssertFrontOrder(media);
        Assert.AreEqual(RearIndex(media, scenario.A) + 1, RearIndex(media, scenario.B));
        Assert.AreEqual(2L, scenario.Pcm.PresentedCount);
        AssertOneTrigger(all);
    }

    private static byte[] Trigger()
    {
        byte[] trigger = new byte[48];
        trigger[0] = 2;
        trigger[1] = 4;
        trigger[11] = 1;
        trigger[12] = 0x37;
        trigger[13] = 0x40;
        return trigger;
    }

    private static byte[][] Media(byte[][] all) => all.Where(report => report[0] == 0x36).ToArray();
    private static int RearIndex(byte[][] media, byte[] rear) =>
        Array.FindIndex(media, report => report.AsSpan(78, 64).SequenceEqual(rear));

    private static void AssertFrontOrder(byte[][] media, int firstMarker = 1)
    {
        for (int i = 0; i < media.Length; i++)
            Assert.IsTrue(media[i].AsSpan(144, 200).ToArray().All(value => value == firstMarker + i),
                $"Front media marker {firstMarker + i} was lost, repeated, or reordered.");
    }

    private static void AssertOneTrigger(byte[][] all)
    {
        byte[][] native = all.Where(report => report[0] != 0x32 &&
            (report[report[0] == 0x31 ? 3 : 13] & 4) != 0).ToArray();
        Assert.AreEqual(1, native.Length);
        int offset = native[0][0] == 0x31 ? 3 : 13;
        CollectionAssert.AreEqual(Trigger().AsSpan(11, 11).ToArray(),
            native[0].AsSpan(offset + 10, 11).ToArray());
    }

    private sealed class Scenario : IDisposable
    {
        internal readonly Fixture Fixture = new();
        internal readonly DualSenseRealtimeHapticsSharedRing Pcm;
        internal readonly byte[] A = Enumerable.Repeat((byte)0x21, 64).ToArray();
        internal readonly byte[] B = Enumerable.Repeat((byte)0x43, 64).ToArray();
        internal readonly byte[] C = Enumerable.Repeat((byte)0x65, 64).ToArray();
        internal readonly HashSet<long> ObservedDeadlines = new();
        private readonly object host;
        private bool primed;
        private bool emptyDueVisited;
        internal Exception Failure;
        internal Action OnEmptyDue;
        internal Action OnEveryDue;

        internal Scenario()
        {
            Pcm = (DualSenseRealtimeHapticsSharedRing)typeof(Fixture).GetField("realtime", Private)!.GetValue(Fixture)!;
            host = typeof(Fixture).GetField("host", Private)!.GetValue(Fixture)!;
            host.GetType().GetField("BeforeRealtimeHapticsAvailabilityProbeTestHook", Private)!
                .SetValue(host, (Action)(() => Guard(() =>
                {
                    OnEveryDue?.Invoke();
                    ObserveDeadline();
                    if (!emptyDueVisited && Pcm.PresentedCount == 1 && Pcm.Count == 0)
                    {
                        emptyDueVisited = true;
                        OnEmptyDue?.Invoke();
                    }
                })));
        }

        internal object NativeClaim => host.GetType().GetField("claimedNativeCommand", Private)!.GetValue(host);
        internal long DeferralDeadline => (long)host.GetType().GetField("rearHapticsDeferralDeadlineQpc", Private)!.GetValue(host)!;

        private void ObserveDeadline()
        {
            long deadline = DeferralDeadline;
            if (deadline != 0) ObservedDeadlines.Add(deadline);
        }

        internal void AssertDeferring()
        {
            ObserveDeadline();
            Assert.AreNotEqual(0L, DeferralDeadline,
                "The control must be handled by the real presenter while rear media is deferred.");
        }

        internal void AssertNativeClaim() => Assert.IsNotNull(NativeClaim);
        internal void OnNextSubmit(Action action) => Fixture.Native.DuringSubmit = () => Guard(action);
        internal void SetBeforeNativeCreditProbe(Action action) =>
            host.GetType().GetField("BeforeNativeCommandCreditProbeTestHook", Private)!
                .SetValue(host, (Action)(() => Guard(action)));
        internal void Publish(byte[] block, int generation = 1) =>
            Assert.IsTrue(Pcm.Publish(block, 0, generation, long.MaxValue, Stopwatch.GetTimestamp()));

        internal void Start()
        {
            Action prime = null;
            prime = () => Guard(() =>
            {
                if (Fixture.Native.Reports.Count(report => report[0] == 0x36) == 7)
                {
                    Publish(A);
                    primed = true;
                }
                else Fixture.Native.DuringSubmit = prime;
            });
            Fixture.QueueSpeakerReports(16);
            Fixture.Native.DuringSubmit = prime;
            Fixture.StartIdle();
        }

        internal byte[][] Finish(int expectedMedia)
        {
            bool complete = SpinWait.SpinUntil(() => Failure != null ||
                Fixture.Native.Reports.Count(report => report[0] == 0x36) >= expectedMedia, 3000);
            Fixture.Stop();
            AssertNoFailure();
            Assert.IsTrue(complete, "The finite actual-helper sequence must complete; this is a watchdog, not a latency assertion.");
            Assert.IsTrue(primed && emptyDueVisited);
            Assert.AreEqual((32, 0, 0, 0L), Fixture.NativeOwnershipSnapshot());
            return Fixture.Native.Reports.ToArray();
        }

        internal void QueueNewEpochSpeakerReports()
        {
            for (int i = 0; i < 8; i++)
            {
                byte[] payload = new byte[20 + DualSenseBluetoothAudioPacer.ReportLength];
                BinaryPrimitives.WriteInt64LittleEndian(payload, 100 + i);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), 2);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12), long.MaxValue);
                Span<byte> report = payload.AsSpan(20);
                report[0] = 0x36;
                report[11] = 0x90;
                report[12] = 63;
                report[76] = 0x92;
                report[77] = 64;
                report[142] = 0x93;
                report[143] = 200;
                report.Slice(144, 200).Fill((byte)(101 + i));
                Receive("ReceiveQueuedReport", payload);
            }
        }

        internal void Receive(string name, byte[] payload) =>
            host.GetType().GetMethod(name, Private)!.Invoke(host, new object[] { payload, payload.Length });
        internal void AssertNoFailure() => Assert.IsNull(Failure, Failure?.ToString());
        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception error) { Failure = error; }
        }
        public void Dispose() => Fixture.Dispose();
    }
}
