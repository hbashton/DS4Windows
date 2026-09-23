using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class ViiperDependencyReadinessTests
{
    private static ViiperDependencyStatus Ready => new(true, "verified", false, null);
    private static ViiperDependencyStatus Unsafe => new(false, "changed driver", true, "enabled filter");

    [TestMethod]
    public void OrdinaryStatusReadsReuseTheSnapshotWithoutDependencyWork()
    {
        int checks = 0;
        var readiness = new ViiperDependencyReadiness(() => { checks++; return Ready; });
        for (int index = 0; index < 20; index++) Assert.AreEqual(Ready, readiness.Read());
        Assert.AreEqual(1, checks);
    }

    [TestMethod]
    public void EachDependencyQueryHasItsOwnFiniteForwardOnlyEnumerationPolicy()
    {
        var first = ViiperSetupManager.CreateDependencyQueryOptions();
        Assert.AreEqual(TimeSpan.FromSeconds(2), first.Timeout);
        Assert.IsTrue(first.ReturnImmediately);
        Assert.IsFalse(first.Rewindable);
        first.Timeout = TimeSpan.MaxValue;
        var next = ViiperSetupManager.CreateDependencyQueryOptions();
        Assert.AreNotSame(first, next);
        Assert.AreEqual(TimeSpan.FromSeconds(2), next.Timeout,
            "One query cannot relax the timeout for subsequent dependency inspections.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitRepairReplacesBothStaleFailureAndStaleSuccess(bool initiallyReady)
    {
        ViiperDependencyStatus current = initiallyReady ? Ready : Unsafe;
        var readiness = new ViiperDependencyReadiness(() => current);
        Assert.AreEqual(current, readiness.Read());
        current = initiallyReady ? Unsafe : Ready;
        Assert.AreEqual(current, readiness.Read(refresh: true));
        Assert.AreEqual(current, readiness.Read(), "Later rendering must use the refreshed result.");
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("Stopped")]
    [DataRow("Running")]
    public void DisabledRegistryEntryCannotHideAnUnknownLoadedFilter(string staleState)
    {
        Assert.IsTrue(ViiperSetupManager.IsUnsafeCitrixUsbMonitorState(true, staleState, 4,
            runtimeStateVerified: false));
        Assert.IsFalse(ViiperSetupManager.IsUnsafeCitrixUsbMonitorState(true, "Stopped", 4,
            runtimeStateVerified: true));
        Assert.IsTrue(ViiperSetupManager.IsUnsafeCitrixUsbMonitorState(true, "Running", 4,
            runtimeStateVerified: true));
    }

    [TestMethod]
    public void FailedFreshInspectionCannotLeaveMemoizedSuccess()
    {
        int calls = 0;
        var readiness = new ViiperDependencyReadiness(() => ++calls switch
        {
            1 => Ready,
            2 => throw new IOException("inspection failed"),
            _ => Unsafe,
        });
        Assert.AreEqual(Ready, readiness.Read());
        Assert.ThrowsException<IOException>(() => readiness.Read(refresh: true));
        Assert.AreEqual(Unsafe, readiness.Read());
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task ConcurrentStatusReaderCannotObserveOldSuccessDuringRefresh()
    {
        int calls = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        var readiness = new ViiperDependencyReadiness(() =>
        {
            if (++calls == 1) return Ready;
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
            return Unsafe;
        });
        Assert.AreEqual(Ready, readiness.Read());
        Task<ViiperDependencyStatus> refresh = Task.Run(() => readiness.Read(refresh: true));
        Task<ViiperDependencyStatus> read = null;
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            read = Task.Run(() => { readerStarted.Set(); return readiness.Read(); });
            Assert.IsTrue(readerStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(read.IsCompleted, "The old trusted snapshot is revoked while inspection is running.");
        }
        finally { release.Set(); }
        Assert.AreEqual(Unsafe, await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(Unsafe, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, calls, "A waiting status reader must share the newly verified snapshot.");
    }
}
