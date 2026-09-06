using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DS4Windows;
using DS4Windows.InputDevices;

namespace DS4WindowsTests;

// Owned synthetic device identities only: no HID handles, controller threads,
// UI, installed applications, filesystem probes, or physical output.
[TestClass]
[DoNotParallelize]
public sealed class ReportDiagnosticsWorkerTests
{
    [TestMethod]
    public void CoalescingPreservesIndependentFacetsWithoutReplayingDeliveredOnes()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        Assert.IsTrue(source.TryPublish(new ReportDiagnosticsSnapshot
        {
            DeviceError = "first error", FirstReport = true,
            ProfileName = "initial profile", InitialBattery = 77,
        }));
        Assert.IsTrue(source.TryPublish(new ReportDiagnosticsSnapshot
        {
            LagChanged = true, LagOn = true, Latency = 12.5,
            BatteryNotification = true, Battery = 14,
        }));
        Assert.IsTrue(source.TryPublish(Startup(50)));

        Assert.AreEqual(2L, source.CoalescedCount);
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        var observed = fixture.Observed.Single();
        Assert.AreSame(source, observed.Source);
        Assert.AreSame(source.Device, observed.Device);
        Assert.AreEqual(0, observed.Controller);
        Assert.AreEqual("first error", observed.DeviceError);
        Assert.IsTrue(observed.LagChanged);
        Assert.IsTrue(observed.LagOn);
        Assert.AreEqual(12.5, observed.Latency);
        Assert.IsTrue(observed.FirstReport);
        Assert.AreEqual("initial profile", observed.ProfileName);
        Assert.AreEqual(77, observed.InitialBattery);
        Assert.IsTrue(observed.BatteryNotification);
        Assert.AreEqual(14, observed.Battery);
        Assert.IsTrue(observed.StartupDiagnostic);
        Assert.AreEqual(50, observed.StartupReportCount);
        Assert.AreEqual(0, fixture.Worker.DrainOnce());

        Assert.IsTrue(source.TryPublish(new ReportDiagnosticsSnapshot
        { BatteryNotification = true, Battery = 15 }));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        observed = fixture.Observed.Last();
        Assert.IsNull(observed.DeviceError);
        Assert.IsFalse(observed.LagChanged);
        Assert.IsFalse(observed.FirstReport);
        Assert.IsFalse(observed.StartupDiagnostic);
        Assert.IsTrue(observed.BatteryNotification);
        Assert.AreEqual(15, observed.Battery);

        Assert.IsTrue(source.TryPublish(new ReportDiagnosticsSnapshot
        { DeviceError = "second error" }));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        observed = fixture.Observed.Last();
        Assert.AreEqual("second error", observed.DeviceError);
        Assert.IsFalse(observed.BatteryNotification);
        Assert.IsFalse(observed.FirstReport);
    }

    [TestMethod]
    public void FirstBatteryZeroIsDeliveredAndUnchangedBatteryDoesNotWakeOrReplay()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        var emptyBattery = new ReportDiagnosticsSnapshot
        { BatteryNotification = true, Battery = 0 };
        Assert.IsTrue(source.TryPublish(emptyBattery));
        Assert.IsFalse(source.TryPublish(emptyBattery));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Assert.AreEqual(0, fixture.Observed.Single().Battery);
        Assert.IsFalse(source.TryPublish(emptyBattery));
        Assert.AreEqual(0, fixture.Worker.DrainOnce());

        emptyBattery.DeviceError = "battery-independent error";
        Assert.IsTrue(source.TryPublish(emptyBattery));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Assert.IsFalse(fixture.Observed.Last().BatteryNotification);
        Assert.AreEqual("battery-independent error", fixture.Observed.Last().DeviceError);
    }

    [TestMethod]
    public void NewBatteryPolicyRearmsAnUnchangedPercentageWithoutReplayingOtherFacets()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        var battery = new ReportDiagnosticsSnapshot
        { BatteryNotification = true, Battery = 75, BatteryPolicyRevision = 1 };
        Assert.IsTrue(source.TryPublish(battery));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Assert.IsFalse(source.TryPublish(battery));
        Assert.AreEqual(0, fixture.Worker.DrainOnce());

        battery.BatteryPolicyRevision = 2;
        Assert.IsTrue(source.TryPublish(battery));
        Assert.IsFalse(source.TryPublish(battery));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        var observed = fixture.Observed.Last();
        Assert.IsTrue(observed.BatteryNotification);
        Assert.AreEqual(75, observed.Battery);
        Assert.AreEqual(2L, observed.BatteryPolicyRevision);
        Assert.IsFalse(observed.FirstReport);
        Assert.IsFalse(observed.LagChanged);
        Assert.IsFalse(observed.StartupDiagnostic);
        Assert.IsNull(observed.DeviceError);
        Assert.AreEqual(2, fixture.Observed.Count);
    }

    [TestMethod]
    public void CoalescedLagAndStartupUseTheirLatestFacetValues()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        Assert.IsTrue(source.TryPublish(new ReportDiagnosticsSnapshot
        { LagChanged = true, LagOn = true, Latency = 30 }));
        Assert.IsTrue(source.TryPublish(Startup(1)));
        Assert.IsTrue(source.TryPublish(new ReportDiagnosticsSnapshot
        { LagChanged = true, LagOn = false, Latency = 2 }));
        Assert.IsTrue(source.TryPublish(Startup(50)));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        var observed = fixture.Observed.Single();
        Assert.IsTrue(observed.LagChanged);
        Assert.IsFalse(observed.LagOn);
        Assert.AreEqual(2.0, observed.Latency);
        Assert.IsTrue(observed.StartupDiagnostic);
        Assert.AreEqual(50, observed.StartupReportCount);
        Assert.AreEqual(0, fixture.Worker.DrainOnce());
    }

    [TestMethod]
    public void StartupLatencyIsCapturedWithoutLagAndDoesNotOverwriteLagFacetLatency()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        var startup = Startup(1);
        startup.StartupLatency = 3.25;
        Assert.IsTrue(source.TryPublish(startup));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Assert.IsFalse(fixture.Observed.Single().LagChanged);
        Assert.AreEqual(3.25, fixture.Observed.Single().StartupLatency);

        Assert.IsTrue(source.TryPublish(new ReportDiagnosticsSnapshot
        { LagChanged = true, LagOn = true, Latency = 25.5 }));
        startup.StartupReportCount = 50;
        startup.StartupLatency = 1.5;
        Assert.IsTrue(source.TryPublish(startup));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        var observed = fixture.Observed.Last();
        Assert.IsTrue(observed.LagChanged);
        Assert.AreEqual(25.5, observed.Latency);
        Assert.IsTrue(observed.StartupDiagnostic);
        Assert.AreEqual(1.5, observed.StartupLatency);
    }

    [TestMethod]
    public void SourceOwnsIdentityAndRejectsForeignDeviceWithoutContaminatingMailbox()
    {
        using var fixture = new Fixture(2);
        var source = fixture.Register();
        var foreign = fixture.Register(1);
        var snapshot = Startup(7);
        snapshot.Source = foreign;
        snapshot.Controller = 1;
        snapshot.Device = foreign.Device;
        Assert.IsFalse(source.TryPublish(snapshot));
        Assert.AreEqual(0, fixture.Worker.DrainOnce());
        snapshot.Device = source.Device;
        Assert.IsTrue(source.TryPublish(snapshot));
        snapshot.StartupReportCount = 99;
        snapshot.DeviceError = "mutated caller value";
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        var observed = fixture.Observed.Single();
        Assert.AreSame(source, observed.Source);
        Assert.AreSame(source.Device, observed.Device);
        Assert.AreEqual(0, observed.Controller);
        Assert.AreEqual(7, observed.StartupReportCount);
        Assert.IsNull(observed.DeviceError);
    }

    [TestMethod]
    public void SameDeviceSlotReplacementRejectsOldHandleAndNeverMergesOldFacets()
    {
        using var fixture = new Fixture();
        var device = new TestDevice();
        var first = fixture.Register(device: device);
        Assert.IsTrue(first.TryPublish(new ReportDiagnosticsSnapshot
        {
            FirstReport = true, ProfileName = "retired profile", InitialBattery = 90,
            DeviceError = "retired error", LagChanged = true, LagOn = true,
        }));
        var replacement = fixture.Register(device: device);
        Assert.AreNotSame(first, replacement);
        Assert.IsFalse(first.IsCurrent);
        Assert.IsTrue(replacement.IsCurrent);
        first.Retire();
        Assert.IsTrue(replacement.IsCurrent,
            "Late retirement of an old handle must not detach its successor.");
        Assert.IsFalse(first.TryPublish(Startup(2)));
        Assert.IsTrue(replacement.TryPublish(Startup(3)));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        var observed = fixture.Observed.Single();
        Assert.AreSame(replacement, observed.Source);
        Assert.AreEqual(3, observed.StartupReportCount);
        Assert.IsFalse(observed.FirstReport);
        Assert.IsFalse(observed.LagChanged);
        Assert.IsNull(observed.DeviceError);
        Assert.IsNull(observed.ProfileName);
    }

    [TestMethod]
    public void PauseRevokesEverySourceAndResumeDoesNotResurrectOldHandles()
    {
        using var fixture = new Fixture(2);
        var first = fixture.Register();
        var second = fixture.Register(1);
        Assert.IsTrue(first.TryPublish(Startup(1)));
        Assert.IsTrue(second.TryPublish(Startup(2)));
        fixture.Worker.Pause();
        Assert.IsFalse(first.IsCurrent);
        Assert.IsFalse(second.IsCurrent);
        Assert.IsNull(fixture.Worker.Register(0, first.Device));
        Assert.AreEqual(0, fixture.Worker.DrainOnce());
        fixture.Worker.Resume();
        Assert.IsFalse(first.TryPublish(Startup(3)));
        Assert.IsFalse(second.TryPublish(Startup(4)));
        var replacement = fixture.Register(device: first.Device);
        first.Retire();
        Assert.IsTrue(replacement.TryPublish(Startup(5)));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Assert.AreSame(replacement, fixture.Observed.Single().Source);
        Assert.AreEqual(5, fixture.Observed.Single().StartupReportCount);
    }

    [TestMethod]
    public void RetiringOneSourceDoesNotDiscardAnotherSlot()
    {
        using var fixture = new Fixture(2);
        var first = fixture.Register();
        var second = fixture.Register(1);
        Assert.IsTrue(first.TryPublish(Startup(1)));
        Assert.IsTrue(second.TryPublish(Startup(2)));
        first.Retire();
        first.Retire();
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Assert.AreSame(second, fixture.Observed.Single().Source);
        Assert.IsTrue(second.IsCurrent);
    }

    [TestMethod]
    public void BlockedDispatchDoesNotBlockPublicationRetirementReplacementOrPause()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var lastObserved = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<ReportDiagnosticsSnapshot>();
        using var worker = new ReportDiagnosticsWorker(1, snapshot =>
        {
            observed.Enqueue(snapshot);
            if (snapshot.StartupReportCount == 1)
            {
                entered.Set();
                release.Wait();
            }
            if (snapshot.StartupReportCount == 500) lastObserved.Set();
        });
        worker.Resume();
        var device = new TestDevice();
        var first = worker.Register(0, device);
        Task<ReportDiagnosticsWorker.Source> transition = null;
        try
        {
            Assert.IsTrue(first.TryPublish(Startup(1)));
            Assert.IsTrue(entered.Wait(2_000));
            transition = Task.Run(() =>
            {
                for (int count = 2; count <= 300; count++)
                    if (!first.TryPublish(Startup(count)))
                        throw new InvalidOperationException("The active publisher was rejected.");
                first.Retire();
                var intermediate = worker.Register(0, device);
                if (!intermediate.TryPublish(Startup(400)))
                    throw new InvalidOperationException("Replacement source was rejected.");
                worker.Pause();
                worker.Resume();
                var final = worker.Register(0, device);
                if (!final.TryPublish(Startup(500)))
                    throw new InvalidOperationException("Resumed source was rejected.");
                return final;
            });
            Assert.IsTrue(transition.Wait(2_000),
                "Optional dispatch blocked report publication or a cold source transition.");
            Assert.IsFalse(first.IsCurrent);
            Assert.IsTrue(transition.Result.IsCurrent);
            Assert.IsFalse(first.TryPublish(Startup(600)));
            release.Set();
            Assert.IsTrue(lastObserved.Wait(2_000));
            Assert.AreEqual(2, observed.Count);
            Assert.AreSame(first, observed.First().Source,
                "An already admitted old callback retains its original identity.");
            Assert.AreSame(transition.Result, observed.Last().Source);
        }
        finally
        {
            release.Set();
            transition?.Wait(2_000);
            worker.Dispose();
            Assert.IsTrue(SpinWait.SpinUntil(() => !worker.IsAlive, 2_000));
        }
    }

    [TestMethod]
    public void DisposeReturnsWhileDispatchIsBlockedAndEventuallyClosesWorker()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var worker = new ReportDiagnosticsWorker(1, _ =>
        {
            entered.Set();
            release.Wait();
        });
        worker.Resume();
        var source = worker.Register(0, new TestDevice());
        Task disposing = null;
        try
        {
            Assert.IsTrue(source.TryPublish(Startup(1)));
            Assert.IsTrue(entered.Wait(2_000));
            disposing = Task.Run(worker.Dispose);
            Assert.IsTrue(disposing.Wait(2_000), "Dispose waited for a blocked logger.");
            Assert.IsTrue(worker.IsAlive,
                "The admitted callback is still blocked until explicitly released.");
            Assert.IsFalse(source.IsCurrent);
            Assert.IsFalse(source.TryPublish(Startup(2)));
            Assert.IsNull(worker.Register(0, source.Device));
            worker.Resume();
            Assert.IsNull(worker.Register(0, source.Device));
        }
        finally
        {
            release.Set();
            disposing?.Wait(2_000);
            worker.Dispose();
            Assert.IsTrue(SpinWait.SpinUntil(() => !worker.IsAlive, 2_000));
        }
    }

    [TestMethod]
    public void FailingDispatchAndFailingFailureReporterDoNotKillConsumerOrReplayFacets()
    {
        var observed = new List<ReportDiagnosticsSnapshot>();
        int attempts = 0, reports = 0;
        using var worker = new ReportDiagnosticsWorker(1, snapshot =>
        {
            if (++attempts == 1) throw new InvalidOperationException("Synthetic logger failure.");
            observed.Add(snapshot);
        }, _ =>
        {
            reports++;
            throw new InvalidOperationException("Synthetic failure reporter failure.");
        }, startWorker: false);
        worker.Resume();
        var source = worker.Register(0, new TestDevice());
        Assert.IsTrue(source.TryPublish(new ReportDiagnosticsSnapshot
        { FirstReport = true, ProfileName = "attempted once", InitialBattery = 80 }));
        Assert.AreEqual(0, worker.DrainOnce());
        Assert.AreEqual(1L, worker.DispatchFailureCount);
        Assert.AreEqual(1, reports);
        Assert.IsTrue(source.TryPublish(Startup(2)));
        Assert.AreEqual(1, worker.DrainOnce());
        Assert.AreEqual(2, attempts);
        Assert.IsFalse(observed.Single().FirstReport,
            "A failed optional dispatch is counted, not replayed indefinitely.");
        Assert.AreEqual(2, observed.Single().StartupReportCount);
    }

    [TestMethod]
    public void DispatchCanDisposeItsOwnWorkerWithoutSelfJoin()
    {
        using var callbackFinished = new ManualResetEventSlim();
        ReportDiagnosticsWorker worker = null;
        worker = new ReportDiagnosticsWorker(1, _ =>
        {
            worker.Dispose();
            callbackFinished.Set();
        });
        try
        {
            worker.Resume();
            var source = worker.Register(0, new TestDevice());
            Assert.IsTrue(source.TryPublish(Startup(1)));
            Assert.IsTrue(callbackFinished.Wait(2_000), "Dispatch deadlocked disposing its own worker.");
            Assert.IsTrue(SpinWait.SpinUntil(() => !worker.IsAlive, 2_000));
            Assert.AreEqual(0L, worker.DispatchFailureCount);
            Assert.IsFalse(source.TryPublish(Startup(2)));
        }
        finally { worker.Dispose(); }
    }

    [TestMethod]
    public void WarmPublisherAllocatesNothingWhileCoalescing()
    {
        using var fixture = new Fixture();
        var source = fixture.Register();
        Assert.IsTrue(PublishMany(source, 2_000));
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = PublishMany(source, 20_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(accepted);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(1, fixture.Worker.DrainOnce());
        Assert.AreEqual(19_999, fixture.Observed.Last().StartupReportCount);
        Assert.AreEqual(0L, source.ConcurrentPublishRejectionCount);
    }

    [TestMethod]
    public void ConcurrentConsumerSeesCoherentMonotonicOwnedSnapshotsAndFinalPublication()
    {
        const int finalSequence = 25_000;
        int lastSequence = 0, observations = 0, coherenceFailures = 0;
        using var finalObserved = new ManualResetEventSlim();
        using var worker = new ReportDiagnosticsWorker(1, snapshot =>
        {
            int sequence = snapshot.StartupReportCount;
            if (sequence <= lastSequence || !IsCoherent(snapshot))
                Interlocked.Increment(ref coherenceFailures);
            lastSequence = sequence;
            Interlocked.Increment(ref observations);
            if (sequence == finalSequence) finalObserved.Set();
        });
        worker.Resume();
        var source = worker.Register(0, new TestDevice());
        Task<bool> producer = Task.Run(() =>
        {
            bool accepted = true;
            for (int sequence = 1; sequence <= finalSequence; sequence++)
            {
                accepted &= source.TryPublish(Coherent(sequence));
                if ((sequence & 63) == 0) Thread.Yield();
            }
            return accepted;
        });
        try
        {
            Assert.IsTrue(producer.Wait(10_000));
            Assert.IsTrue(producer.Result);
            Assert.IsTrue(finalObserved.Wait(5_000), "Final diagnostics publication was stranded.");
            Assert.AreEqual(0, Volatile.Read(ref coherenceFailures));
            Assert.IsTrue(Volatile.Read(ref observations) > 0);
            Assert.AreEqual(finalSequence, Volatile.Read(ref lastSequence));
            Assert.AreEqual(0L, source.ConcurrentPublishRejectionCount);
            Assert.AreEqual(0L, worker.DispatchFailureCount);
        }
        finally
        {
            worker.Dispose();
            Assert.IsTrue(producer.Wait(2_000));
            Assert.IsTrue(SpinWait.SpinUntil(() => !worker.IsAlive, 2_000));
        }
    }

    [TestMethod]
    public void ClosureRacingActiveProducerRejectsFurtherPublicationWithoutDisposedSignalUse()
    {
        for (int repetition = 0; repetition < 16; repetition++)
        {
            using var started = new ManualResetEventSlim();
            using var worker = new ReportDiagnosticsWorker(1, static _ => { });
            worker.Resume();
            var source = worker.Register(0, new TestDevice());
            Task producer = Task.Run(() =>
            {
                int sequence = 0;
                while (source.TryPublish(Startup(++sequence)))
                    if (sequence == 100) started.Set();
            });
            try
            {
                Assert.IsTrue(started.Wait(2_000), "Producer never became active.");
                worker.Dispose();
                Assert.IsTrue(producer.Wait(2_000));
                Assert.IsFalse(source.TryPublish(Startup(0)));
                Assert.IsNull(worker.Register(0, source.Device));
                worker.Dispose();
                Assert.IsTrue(SpinWait.SpinUntil(() => !worker.IsAlive, 2_000));
                Assert.AreEqual(0L, worker.DispatchFailureCount);
            }
            finally
            {
                worker.Dispose();
                Assert.IsTrue(producer.Wait(2_000));
                Assert.IsTrue(SpinWait.SpinUntil(() => !worker.IsAlive, 2_000));
            }
        }
    }

    [TestMethod]
    public void InvalidRegistrationAndEmptyPublicationHaveNoSideEffects()
    {
        int callbacks = 0;
        using var worker = new ReportDiagnosticsWorker(1, _ => callbacks++,
            startWorker: false);
        var device = new TestDevice();
        Assert.IsNull(worker.Register(0, device));
        worker.Resume();
        Assert.IsNull(worker.Register(-1, device));
        Assert.IsNull(worker.Register(1, device));
        Assert.IsNull(worker.Register(0, null));
        var source = worker.Register(0, device);
        Assert.IsNotNull(source);
        Assert.IsFalse(source.TryPublish(default));
        Assert.AreEqual(0, worker.DrainOnce());
        worker.Dispose();
        worker.Dispose();
        worker.Resume();
        Assert.IsNull(worker.Register(0, device));
        Assert.IsFalse(source.TryPublish(Startup(1)));
        Assert.AreEqual(0, worker.DrainOnce());
        Assert.AreEqual(0, callbacks);
        Assert.AreEqual(0L, worker.DispatchFailureCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool PublishMany(ReportDiagnosticsWorker.Source source, int count)
    {
        bool accepted = true;
        for (int sequence = 0; sequence < count; sequence++)
            accepted &= source.TryPublish(Startup(sequence));
        return accepted;
    }

    private static ReportDiagnosticsSnapshot Startup(int sequence) => new()
    { StartupDiagnostic = true, StartupReportCount = sequence };

    private static ReportDiagnosticsSnapshot Coherent(int sequence) => new()
    {
        StartupDiagnostic = true, StartupReportCount = sequence,
        DeviceError = (sequence & 1) == 0 ? "even error" : "odd error",
        FirstReport = true, ProfileName = (sequence & 1) == 0 ? "even profile" : "odd profile",
        InitialBattery = sequence % 101, BatteryNotification = true, Battery = sequence % 101,
        LagChanged = true, LagOn = (sequence & 1) == 0, Latency = sequence + 0.25,
        StartupLatency = sequence + 0.75,
        Synced = true, UseDInputOnly = (sequence & 1) != 0,
        ActiveOutput = (sequence & 1) == 0 ? OutContType.ViiperXboxOne : OutContType.ViiperDS4,
        Cross = (sequence & 1) == 0, Circle = (sequence & 1) != 0, PS = (sequence & 2) != 0,
        LX = (byte)sequence, LY = (byte)(sequence >> 8),
        RX = (byte)~sequence, RY = (byte)~(sequence >> 8),
        L2 = (byte)(sequence * 3), R2 = (byte)(sequence * 7),
    };

    private static bool IsCoherent(ReportDiagnosticsSnapshot actual)
    {
        var expected = Coherent(actual.StartupReportCount);
        return actual.Source != null && ReferenceEquals(actual.Device, actual.Source.Device) &&
            actual.Controller == actual.Source.Controller && actual.StartupDiagnostic &&
            actual.FirstReport && actual.LagChanged && actual.BatteryNotification &&
            actual.DeviceError == expected.DeviceError && actual.ProfileName == expected.ProfileName &&
            actual.InitialBattery == expected.InitialBattery && actual.Battery == expected.Battery &&
            actual.LagOn == expected.LagOn && actual.Latency == expected.Latency &&
            actual.StartupLatency == expected.StartupLatency &&
            actual.Synced == expected.Synced && actual.UseDInputOnly == expected.UseDInputOnly &&
            actual.ActiveOutput == expected.ActiveOutput && actual.Cross == expected.Cross &&
            actual.Circle == expected.Circle && actual.PS == expected.PS &&
            actual.LX == expected.LX && actual.LY == expected.LY &&
            actual.RX == expected.RX && actual.RY == expected.RY &&
            actual.L2 == expected.L2 && actual.R2 == expected.R2;
    }

    private sealed class TestDevice() : DS4Device("Diagnostics synthetic identity",
        InputDeviceType.DS4, ConnectionType.USB);

    private sealed class Fixture : IDisposable
    {
        internal readonly List<ReportDiagnosticsSnapshot> Observed = new();
        internal readonly ReportDiagnosticsWorker Worker;

        internal Fixture(int slots = 1)
        {
            Worker = new ReportDiagnosticsWorker(slots, Observed.Add, startWorker: false);
            Worker.Resume();
        }

        internal ReportDiagnosticsWorker.Source Register(int slot = 0, DS4Device device = null)
        {
            var source = Worker.Register(slot, device ?? new TestDevice());
            Assert.IsNotNull(source);
            return source;
        }

        public void Dispose() => Worker.Dispose();
    }
}
