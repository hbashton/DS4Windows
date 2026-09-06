using System.Runtime.CompilerServices;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public sealed class StrictAllocationMeasurementScopeTests
{
    [TestMethod]
    public void EmptyIntegerLoopPreservesExactZeroAllocationGate()
    {
        int checksum = SumIntegers(2_000);
        long allocated;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            checksum += SumIntegers(20_000);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(201_989_000, checksum);
    }

    [TestMethod]
    public void RealManagedAllocationStillFailsExactZeroAllocationGate()
    {
        GC.KeepAlive(AllocatePositiveControl());
        byte[] retained;
        long allocated;
        using (StrictAllocationMeasurementScope.Begin())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            retained = AllocatePositiveControl();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        GC.KeepAlive(retained);
        Assert.IsTrue(allocated >= 128L, "The real managed allocation must remain visible.");
        Assert.ThrowsException<AssertFailedException>(() => Assert.AreEqual(0L, allocated));
    }

    [TestMethod]
    public void ReservationFailureIsNotRetriedAndDoesNotEndAnUnownedRegion()
    {
        var runtime = new FakeRuntime { StartSucceeds = false };
        Assert.ThrowsException<InvalidOperationException>(() =>
            StrictAllocationMeasurementScope.Begin(runtime));
        Assert.AreEqual(1, runtime.StartCalls);
        Assert.AreEqual(StrictAllocationMeasurementScope.ReservationBytes, runtime.LastReservation);
        Assert.IsTrue(runtime.LastDisallowFullBlockingGc);
        Assert.AreEqual(0, runtime.EndCalls);
        AssertNextScopeCanComplete();
    }

    [TestMethod]
    public void StartupExceptionReleasesHelperGateWithoutEndingAnUnownedRegion()
    {
        var runtime = new FakeRuntime { ThrowOnStart = true };
        Assert.ThrowsException<InvalidOperationException>(() =>
            StrictAllocationMeasurementScope.Begin(runtime));
        Assert.AreEqual(1, runtime.StartCalls);
        Assert.AreEqual(0, runtime.EndCalls);
        AssertNextScopeCanComplete();
    }

    [TestMethod]
    public void ExistingExternalRegionIsNeitherAdoptedNorEnded()
    {
        var runtime = new FakeRuntime { IsActive = true };
        Assert.ThrowsException<InvalidOperationException>(() =>
            StrictAllocationMeasurementScope.Begin(runtime));
        Assert.AreEqual(0, runtime.StartCalls);
        Assert.AreEqual(0, runtime.EndCalls);
        Assert.IsTrue(runtime.IsActive);
        AssertNextScopeCanComplete();
    }

    [TestMethod]
    public void NestedAttemptCannotEndOrReplaceOuterScope()
    {
        var outerRuntime = new FakeRuntime();
        var innerRuntime = new FakeRuntime();
        using (StrictAllocationMeasurementScope.Begin(outerRuntime))
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                StrictAllocationMeasurementScope.Begin(innerRuntime));
            Assert.IsTrue(outerRuntime.IsActive);
            Assert.AreEqual(0, outerRuntime.EndCalls);
            Assert.AreEqual(0, innerRuntime.StartCalls);
        }
        Assert.AreEqual(1, outerRuntime.EndCalls);
        AssertNextScopeCanComplete();
    }

    [TestMethod]
    public void LostRegionFailsEvenWithNoMeasuredAllocation()
    {
        var runtime = new FakeRuntime();
        var scope = StrictAllocationMeasurementScope.Begin(runtime);
        runtime.IsActive = false;
        Assert.ThrowsException<InvalidOperationException>(() => scope.Dispose());
        Assert.AreEqual(0, runtime.EndCalls);
        scope.Dispose();
        AssertNextScopeCanComplete();
    }

    [TestMethod]
    public void EndFailurePropagatesAndReleasesHelperGate()
    {
        var runtime = new FakeRuntime { ThrowOnEnd = true };
        var scope = StrictAllocationMeasurementScope.Begin(runtime);
        Assert.ThrowsException<InvalidOperationException>(() => scope.Dispose());
        Assert.AreEqual(1, runtime.EndCalls);
        scope.Dispose();
        Assert.AreEqual(1, runtime.EndCalls);
        AssertNextScopeCanComplete();
    }

    [TestMethod]
    public void WorkloadExceptionStillEndsExactlyOnce()
    {
        var runtime = new FakeRuntime();
        Assert.ThrowsException<ArgumentException>(() =>
        {
            using (StrictAllocationMeasurementScope.Begin(runtime))
                throw new ArgumentException("Synthetic workload failure.");
        });
        Assert.AreEqual(1, runtime.EndCalls);
        Assert.IsFalse(runtime.IsActive);
        AssertNextScopeCanComplete();
    }

    [TestMethod]
    public void WrongThreadCannotDisposeOwnersScope()
    {
        var runtime = new FakeRuntime();
        using (var scope = StrictAllocationMeasurementScope.Begin(runtime))
        {
            Task.Run(() => Assert.ThrowsException<InvalidOperationException>(() => scope.Dispose()))
                .GetAwaiter().GetResult();
            Assert.IsTrue(runtime.IsActive);
            Assert.AreEqual(0, runtime.EndCalls);
        }
        Assert.AreEqual(1, runtime.EndCalls);
        AssertNextScopeCanComplete();
    }

    [TestMethod]
    public void GlobalHelperGateSerializesDifferentThreads()
    {
        var firstRuntime = new FakeRuntime();
        var secondRuntime = new FakeRuntime();
        using var attempted = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        Task worker;
        using (StrictAllocationMeasurementScope.Begin(firstRuntime))
        {
            worker = Task.Run(() =>
            {
                attempted.Set();
                using (StrictAllocationMeasurementScope.Begin(secondRuntime))
                    entered.Set();
            });
            Assert.IsTrue(attempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(entered.IsSet);
            Assert.AreEqual(0, secondRuntime.StartCalls);
        }

        Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(entered.IsSet);
        Assert.AreEqual(1, firstRuntime.EndCalls);
        Assert.AreEqual(1, secondRuntime.EndCalls);
    }

    private static void AssertNextScopeCanComplete()
    {
        // Same-thread Monitor reentrancy could hide a leaked gate after failure.
        // A different thread must acquire and release it within a bounded wait.
        Task next = Task.Run(() =>
        {
            var runtime = new FakeRuntime();
            var scope = StrictAllocationMeasurementScope.Begin(runtime);
            scope.Dispose();
            scope.Dispose();
            Assert.AreEqual(1, runtime.StartCalls);
            Assert.AreEqual(1, runtime.EndCalls);
        });
        Assert.IsTrue(next.Wait(TimeSpan.FromSeconds(5)),
            "The completed or failed scope must release the gate for another thread.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] AllocatePositiveControl() => new byte[128];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SumIntegers(int count)
    {
        int sum = 0;
        for (int i = 0; i < count; i++) sum += i;
        return sum;
    }

    private sealed class FakeRuntime : StrictAllocationMeasurementScope.INoGcRegionRuntime
    {
        public bool IsActive { get; set; }
        internal bool StartSucceeds = true;
        internal bool ThrowOnStart;
        internal bool ThrowOnEnd;
        internal int StartCalls;
        internal int EndCalls;
        internal long LastReservation;
        internal bool LastDisallowFullBlockingGc;

        public bool TryStart(long totalSize, bool disallowFullBlockingGc)
        {
            StartCalls++;
            LastReservation = totalSize;
            LastDisallowFullBlockingGc = disallowFullBlockingGc;
            if (ThrowOnStart) throw new InvalidOperationException("Synthetic reservation failure.");
            IsActive = StartSucceeds;
            return StartSucceeds;
        }

        public void End()
        {
            EndCalls++;
            IsActive = false;
            if (ThrowOnEnd) throw new InvalidOperationException("Synthetic end failure.");
        }
    }
}
