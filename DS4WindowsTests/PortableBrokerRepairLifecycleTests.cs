using System.Reflection;
using System.Security.Cryptography;
using DS4Windows;
using FakeHost = DS4WindowsTests.PortableBrokerRepairTests.FakeHost;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class PortableBrokerRepairLifecycleTests
{
    private string root, image, digest;
    private FakeHost host;
    private PortableBrokerContext context;
    private static readonly FieldInfo CurrentField = typeof(PortableBrokerContext)
        .GetField("current", BindingFlags.Static | BindingFlags.NonPublic);

    [TestInitialize]
    public void Initialize()
    {
        Assert.IsNull(PortableBrokerContext.Current, "No test may borrow a live global portable owner.");
        root = Path.Combine(AppContext.BaseDirectory, "portable-repair-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), PortableBrokerContext.MarkerText);
        image = Path.Combine(root, "viiper.exe");
        File.WriteAllText(image, "test image; never executed");
        digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image)));
        host = new FakeHost();
    }

    [TestCleanup]
    public void Cleanup()
    {
        context?.Dispose();
        if (ReferenceEquals(PortableBrokerContext.Current, context)) CurrentField.SetValue(null, null);
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
            Path.GetFileName(root).StartsWith("portable-repair-lifetime-", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }

    private PortableBrokerContext Open() => context = PortableBrokerContext.Create(root, digest, host, () => Array.Empty<string>());

    [TestMethod]
    public void ExplicitRetirementStopsOnlyOwnedIdentityThenReleasesPinsAndCurrent()
    {
        Open().Start();
        CurrentField.SetValue(null, context);
        PortableBrokerProcessIdentity owned = host.Peers.Single();
        PortableBrokerProcessIdentity unrelated = new(201, 202, Path.Combine(root, "installed", "viiper.exe"));
        host.Peers.Add(unrelated);
        PortableBrokerContext.RetireCurrentForRepair();
        Assert.IsNull(PortableBrokerContext.Current);
        Assert.AreEqual(owned, host.Stopped.Single());
        Assert.AreEqual(unrelated, host.Peers.Single());
        Assert.AreEqual(0, host.Child.NormalStops);
        Assert.AreEqual(1, host.Child.Disposals);
        Assert.IsFalse(context.IsVerifiedBackend(image));
        File.WriteAllText(image, "image pin released");
        File.WriteAllText(context.ConfigPath, "configuration pin released");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StopFailureOrStillRunningTargetRetainsCurrentAndItsPins(bool ignoreStop)
    {
        Open().Start();
        CurrentField.SetValue(null, context);
        host.StopFailure = !ignoreStop;
        host.IgnoreStop = ignoreStop;
        try
        {
            PortableBrokerContext.RetireCurrentForRepair();
            Assert.Fail("Failed retirement must not release the owner.");
        }
        catch (IOException) when (!ignoreStop) { }
        catch (PortableBrokerStartupException) when (ignoreStop) { }
        Assert.AreSame(context, PortableBrokerContext.Current);
        Assert.IsTrue(context.IsVerifiedBackend(image));
        Assert.ThrowsException<IOException>(() => File.WriteAllText(image, "must remain pinned"));
    }

    [TestMethod]
    public void ReusedPidAndSamePathNewcomerAreNotAuthorizedByOldOwnership()
    {
        Open().Start();
        host.Peers[0] = host.Peers[0] with { StartTimeUtcTicks = 999 };
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.RetireForRepair());
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.IsTrue(context.IsVerifiedBackend(image));
    }

    private void BorrowVerifiedBroker()
    {
        Open().Start();
        string[] args = host.Arguments;
        string key = context.KeyPath;
        context.Dispose();
        File.WriteAllText(key, "fixture key, never authenticates a real broker");
        host = new FakeHost { Arguments = args };
        host.Peers.Add(new(301, 302, image));
        Open().Start();
    }

    [TestMethod]
    public void ExplicitRepairMayRetireVerifiedBorrowedBrokerAndReleaseKeyPin()
    {
        BorrowVerifiedBroker();
        PortableBrokerProcessIdentity borrowed = host.Peers.Single();
        context.RetireForRepair();
        Assert.AreEqual(borrowed, host.Stopped.Single());
        Assert.AreEqual(0, host.Peers.Count);
        File.WriteAllText(context.KeyPath, "key pin released");
        Assert.IsFalse(context.IsVerifiedBackend(image));
    }

    [TestMethod]
    public void OrdinaryDisposeStillNeverStopsBorrowedBroker()
    {
        BorrowVerifiedBroker();
        context.Dispose();
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.AreEqual(1, host.Peers.Count);
    }

    [TestMethod]
    public void UnavailableContextKeepsPortableIdentityWithoutAllowingBrokerStartup()
    {
        File.Delete(image);
        context = PortableBrokerContext.CreateUnavailable(root, host, () => Array.Empty<string>());
        CurrentField.SetValue(null, context);
        Assert.IsTrue(PortableBrokerContext.IsActive);
        Assert.AreEqual(image, context.ViiperPath);
        Assert.AreEqual(Path.Combine(root, "portable-data", "VIIPER", "viiper.key.txt"), context.KeyPath);
        Assert.IsFalse(context.IsVerifiedBackend(image));
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.IsFalse(context.InspectOwnedProcess(out bool running, out _));
        Assert.IsFalse(running);
        Assert.AreEqual(0, host.Starts);
        PortableBrokerContext.RetireCurrentForRepair();
        Assert.IsFalse(PortableBrokerContext.IsActive);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnavailableOrDisposedContextCanRetireAnExactTargetForExplicitRepair(bool disposed)
    {
        context = PortableBrokerContext.CreateUnavailable(root, host, () => Array.Empty<string>());
        if (disposed) context.Dispose();
        CurrentField.SetValue(null, context);
        PortableBrokerProcessIdentity target = new(401, 402, image);
        host.Peers.Add(target);
        PortableBrokerContext.RetireCurrentForRepair();
        Assert.IsNull(PortableBrokerContext.Current);
        Assert.AreEqual(target, host.Stopped.Single());
        Assert.AreEqual(0, host.Peers.Count);
    }

    [TestMethod]
    public void SelectedStopLeavesEveryOtherBrokerUntouchedAndVerifiesExit()
    {
        PortableBrokerProcessIdentity target = new(501, 502, image);
        PortableBrokerProcessIdentity unrelated = new(601, 602, Path.Combine(root, "installed", "viiper.exe"));
        host.Peers.AddRange(new[] { unrelated, target });
        PortableBrokerProcessHost.StopSelectedForRepair(image, host);
        Assert.AreEqual(target, host.Stopped.Single());
        Assert.AreEqual(unrelated, host.Peers.Single());
        Assert.AreEqual(2, host.Snapshots);
    }

    [DataTestMethod]
    [DataRow("unknown-path")]
    [DataRow("unknown-start")]
    [DataRow("relative-path")]
    public void AnyUnqueryableSnapshotIdentityBlocksStopBeforeMutation(string mode)
    {
        host.Peers.Add(new(501, 502, image));
        host.Peers.Add(new(601, mode == "unknown-start" ? 0 : 602,
            mode == "unknown-path" ? null : mode == "relative-path" ? "viiper.exe" : Path.Combine(root, "other", "viiper.exe")));
        Assert.ThrowsException<PortableBrokerStartupException>(() => PortableBrokerProcessHost.StopSelectedForRepair(image, host));
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.AreEqual(2, host.Peers.Count);
    }

    [TestMethod]
    public void SamePathProcessAppearingAfterStopPreventsReplacementAuthorization()
    {
        host.Peers.Add(new(501, 502, image));
        host.AfterStop = () => host.Peers.Add(new(701, 702, image));
        Assert.ThrowsException<PortableBrokerStartupException>(() => PortableBrokerProcessHost.StopSelectedForRepair(image, host));
        Assert.AreEqual(1, host.Stopped.Count);
        Assert.AreEqual(701, host.Peers.Single().ProcessId);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnavailableOrDisposedRetirementCannotAdoptANewSamePathProcess(bool disposed)
    {
        context = PortableBrokerContext.CreateUnavailable(root, host, () => Array.Empty<string>());
        if (disposed) context.Dispose();
        CurrentField.SetValue(null, context);
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        host.Peers.Add(new(801, 802, image));
        bool helperCalled = false;
        Assert.ThrowsException<PortableBrokerStartupException>(() =>
            PortableBrokerContext.RetireCurrentForRepair(captured, () => helperCalled = true));
        Assert.IsFalse(helperCalled);
        Assert.AreSame(context, PortableBrokerContext.Current);
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.AreEqual(801, host.Peers.Single().ProcessId);
    }

    [TestMethod]
    public void TrustedOwnerMismatchFailsBeforeAnyInjectedElevationOrStop()
    {
        Open().Start();
        host.Peers[0] = host.Peers[0] with { StartTimeUtcTicks = 999 };
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        bool helperCalled = false;
        Assert.ThrowsException<PortableBrokerStartupException>(() =>
            context.RetireForRepair(captured, () => helperCalled = true));
        Assert.IsFalse(helperCalled);
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.IsTrue(context.IsVerifiedBackend(image));
    }

    [TestMethod]
    public void CapturedStopDelegateRunsOnceWithPinsHeldAndReleasesOnlyAfterVerifiedExit()
    {
        Open().Start();
        host.Peers.Add(new(901, 902, Path.Combine(root, "foreign", "viiper.exe")));
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        int helperCalls = 0;
        context.RetireForRepair(captured, () =>
        {
            helperCalls++;
            Assert.ThrowsException<IOException>(() => File.WriteAllText(image, "pin must remain held during helper"));
            PortableBrokerProcessHost.StopCapturedForRepair(captured, processHost: host);
        });
        Assert.AreEqual(1, helperCalls);
        Assert.AreEqual(2, host.Stopped.Count);
        Assert.IsFalse(context.IsVerifiedBackend(image));
        File.WriteAllText(image, "released after verified exit");
    }

    [TestMethod]
    public void NewcomerAfterStopDelegatePreservesCurrentAndPinsWithoutStoppingNewIdentity()
    {
        Open().Start();
        CurrentField.SetValue(null, context);
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        Assert.ThrowsException<PortableBrokerStartupException>(() =>
            PortableBrokerContext.RetireCurrentForRepair(captured, () =>
            {
                PortableBrokerProcessHost.StopCapturedForRepair(captured, processHost: host);
                host.Peers.Add(new(901, 902, image));
            }));
        Assert.AreSame(context, PortableBrokerContext.Current);
        Assert.IsTrue(context.IsVerifiedBackend(image));
        Assert.AreEqual(1, host.Stopped.Count);
        Assert.AreEqual(901, host.Peers.Single().ProcessId);
        Assert.ThrowsException<IOException>(() => File.WriteAllText(image, "must remain pinned"));
    }

    [TestMethod]
    public void FailedStopDelegateLeavesCurrentAndPinIntactForRetry()
    {
        Open().Start();
        CurrentField.SetValue(null, context);
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        Assert.ThrowsException<UnauthorizedAccessException>(() =>
            PortableBrokerContext.RetireCurrentForRepair(captured, () => throw new UnauthorizedAccessException("UAC denied")));
        Assert.AreSame(context, PortableBrokerContext.Current);
        Assert.IsTrue(context.IsVerifiedBackend(image));
        Assert.AreEqual(1, host.Peers.Count);
        Assert.AreEqual(0, host.Stopped.Count);
    }
}
