using System.Diagnostics;
using System.Security.Cryptography;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class PortableBrokerRepairTests
{
    private string root, destination, payload, digest;
    private FakeHost host;
    private int acquisitions, disposals;
    private static readonly byte[] Release = "compiled release fixture; never executed"u8.ToArray();

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(AppContext.BaseDirectory, "portable-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), PortableBrokerContext.MarkerText);
        destination = Path.Combine(root, "viiper.exe");
        payload = Path.Combine(root, "extras", "viiper.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(payload));
        File.WriteAllBytes(payload, Release);
        File.WriteAllText(destination, "previous image");
        digest = Convert.ToHexString(SHA256.HashData(Release));
        host = new FakeHost();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
            Path.GetFileName(root).StartsWith("portable-repair-", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }

    private Task<ViiperPayloadLease> Acquire(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        acquisitions++;
        return Task.FromResult(new ViiperPayloadLease(payload, cleanup: () => disposals++));
    }

    private Task<PortableBrokerRepairResult> Repair(
        Func<CancellationToken, Task<ViiperPayloadLease>> provider = null,
        CancellationToken token = default, IPortableBrokerRepairCommitter committer = null,
        Action<string> inspect = null, Func<IEnumerable<string>> managed = null) =>
        PortableBrokerRepair.RepairAsync(root, digest, provider ?? Acquire, token, host,
            managed ?? (() => Array.Empty<string>()), inspect, committer);

    private void AssertCleanStage() => Assert.AreEqual(0,
        Directory.EnumerateFiles(root, ".viiper-repair-*").Count());

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrMismatchedBrokerIsRepairedInTheExistingFolderOnly(bool missing)
    {
        if (missing) File.Delete(destination);
        var preserved = new Dictionary<string, string>
        {
            ["portable-data/VIIPER/viiper.key.txt"] = "local key",
            ["portable-data/VIIPER/viiper.json"] = "{}",
            ["Profiles/profile.xml"] = "user profile",
            ["installed-copy/viiper.exe"] = "installed image",
            ["installed-copy/viiper.key.txt"] = "installed key",
        };
        foreach (var entry in preserved)
        {
            string path = Path.Combine(root, entry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, entry.Value);
        }
        PortableBrokerRepairResult result = await Repair();
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(destination, result.ViiperPath);
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(destination));
        foreach (var entry in preserved) Assert.AreEqual(entry.Value, File.ReadAllText(Path.Combine(root, entry.Key)));
        Assert.AreEqual(1, acquisitions);
        Assert.AreEqual(1, disposals);
        Assert.AreEqual(0, host.Starts);
        Assert.AreEqual(0, host.Stopped.Count);
        using PortableBrokerContext context = PortableBrokerContext.Create(root, digest, host, () => Array.Empty<string>());
        Assert.IsTrue(context.IsVerifiedBackend(destination));
        AssertCleanStage();
    }

    [TestMethod]
    public async Task CorrectPinnedImageIsANoOpWithoutDownloadProcessInspectionOrRestart()
    {
        File.WriteAllBytes(destination, Release);
        using PortableBrokerContext context = PortableBrokerContext.Create(root, digest, host, () => Array.Empty<string>());
        host.SnapshotFailure = true;
        PortableBrokerRepairResult result = await Repair();
        Assert.IsFalse(result.Changed);
        Assert.AreEqual(0, acquisitions);
        Assert.IsFalse(File.Exists(Path.Combine(root, ".viiper-repair.lock")));
        Assert.IsTrue(context.IsVerifiedBackend(destination));
    }

    [TestMethod]
    public async Task InvalidPayloadCannotReplaceOldImageAndLeaseIsReleased()
    {
        File.WriteAllText(payload, "wrong release");
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair());
        Assert.AreEqual("previous image", File.ReadAllText(destination));
        Assert.AreEqual(1, disposals);
        AssertCleanStage();
    }

    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("invalid")]
    [DataRow("managed")]
    public async Task MarkerAndManagedRootPolicyCannotBeBypassedByRepair(string mode)
    {
        if (mode == "missing") File.Delete(Path.Combine(root, PortableBrokerContext.MarkerFileName));
        if (mode == "invalid") File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), "not a package");
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair(
            managed: () => mode == "managed" ? new[] { root } : Array.Empty<string>()));
        Assert.AreEqual(0, acquisitions);
        Assert.AreEqual(0, host.Snapshots);
        Assert.AreEqual("previous image", File.ReadAllText(destination));
    }

    [TestMethod]
    public void ResolverDoesNotPinOrRequireTheBroker()
    {
        File.Delete(destination);
        Assert.AreEqual(root, PortableBrokerRepair.TryGetPortableRoot(root, () => Array.Empty<string>()));
        Assert.IsNull(PortableBrokerRepair.TryGetPortableRoot(root, () => new[] { root }));
        File.Delete(Path.Combine(root, PortableBrokerContext.MarkerFileName));
        Assert.IsNull(PortableBrokerRepair.TryGetPortableRoot(root,
            () => throw new AssertFailedException("No marker must not inspect the registry boundary.")));
    }

    [TestMethod]
    public async Task LongPortableFolderAndStagingPathsUseTheSameNativeAndManagedIdentity()
    {
        string longRoot = Path.Combine(root, new string('a', 80), new string('b', 80));
        Directory.CreateDirectory(longRoot);
        Assert.IsTrue(longRoot.Length > 260);
        File.WriteAllText(Path.Combine(longRoot, PortableBrokerContext.MarkerFileName), PortableBrokerContext.MarkerText);
        string longDestination = Path.Combine(longRoot, "viiper.exe");
        File.WriteAllText(longDestination, "previous long-path image");
        PortableBrokerRepairResult result = await PortableBrokerRepair.RepairAsync(longRoot, digest,
            Acquire, processHost: host, managedRoots: () => Array.Empty<string>());
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(longDestination, result.ViiperPath);
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(longDestination));
        Assert.AreEqual("previous image", File.ReadAllText(destination));
        Assert.AreEqual(0, Directory.EnumerateFiles(longRoot, ".viiper-repair-*").Count());
    }

    [DataTestMethod]
    [DataRow("same-image")]
    [DataRow("unknown-image")]
    [DataRow("unknown-start")]
    [DataRow("snapshot-failure")]
    public async Task RunningOrUnknownDestinationIsNeverReplacedOrStoppedByTheOfflineCore(string mode)
    {
        host.Peers.Add(new(41, mode == "unknown-start" ? 0 : 42,
            mode == "unknown-image" ? null : destination));
        host.SnapshotFailure = mode == "snapshot-failure";
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair());
        Assert.AreEqual(0, acquisitions);
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.AreEqual("previous image", File.ReadAllText(destination));
    }

    [TestMethod]
    public async Task UnrelatedInstalledBrokerIsNeitherStoppedNorAReasonToRedirectPortableRepair()
    {
        PortableBrokerProcessIdentity installed = new(41, 42, Path.Combine(root, "installed-copy", "viiper.exe"));
        host.Peers.Add(installed);
        Assert.IsTrue((await Repair()).Changed);
        Assert.AreEqual(installed, host.Peers.Single());
        Assert.AreEqual(0, host.Stopped.Count);
    }

    [TestMethod]
    public async Task ContextPinPreventsRepairUntilExplicitRetirement()
    {
        string previousDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)));
        using PortableBrokerContext context = PortableBrokerContext.Create(root, previousDigest, host, () => Array.Empty<string>());
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair());
        Assert.AreEqual(0, acquisitions);
        Assert.IsTrue(context.IsVerifiedBackend(destination));
        Assert.AreEqual("previous image", File.ReadAllText(destination));
        context.RetireForRepair();
        Assert.IsTrue((await Repair()).Changed);
    }

    [TestMethod]
    public async Task AFileChangedWhileAcquiringPayloadIsNotOverwritten()
    {
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair(async token =>
        {
            File.WriteAllText(destination, "someone else's replacement");
            return await Acquire(token);
        }));
        Assert.AreEqual("someone else's replacement", File.ReadAllText(destination));
        Assert.AreEqual(1, disposals);
        AssertCleanStage();
    }

    [TestMethod]
    public async Task BrokerStartingWhilePayloadIsAcquiredBlocksCommit()
    {
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair(async token =>
        {
            host.Peers.Add(new(51, 52, destination));
            return await Acquire(token);
        }));
        Assert.AreEqual("previous image", File.ReadAllText(destination));
        Assert.AreEqual(0, host.Stopped.Count);
        AssertCleanStage();
    }

    [TestMethod]
    public async Task CancellationAndProviderFailuresLeaveOriginalAndReleaseTheMutex()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => Repair(token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Acquire(token);
        }, cancellation.Token));
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair(_ =>
            throw new IOException("injected offline failure")));
        Assert.AreEqual("previous image", File.ReadAllText(destination));
        Assert.IsTrue((await Repair()).Changed);
    }

    [TestMethod]
    public async Task ConcurrentRepairsSerializeAndOnlyAcquireOnePayload()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PortableBrokerRepairResult> first = Repair(async token =>
        {
            entered.SetResult(true);
            await release.Task.WaitAsync(token);
            return await Acquire(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<PortableBrokerRepairResult> second = Repair();
        Assert.IsFalse(second.IsCompleted);
        release.SetResult(true);
        PortableBrokerRepairResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, results.Count(result => result.Changed));
        Assert.AreEqual(1, acquisitions);
        Assert.AreEqual(1, disposals);
        AssertCleanStage();
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForAnotherRepairDoesNotAcquireItsPayload()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PortableBrokerRepairResult> first = Repair(async token =>
        {
            await release.Task.WaitAsync(token);
            return await Acquire(token);
        });
        using var cancellation = new CancellationTokenSource();
        Task<PortableBrokerRepairResult> second = Repair(token: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => second);
        release.SetResult(true);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, acquisitions);
    }

    [TestMethod]
    public async Task DirectoryAndMarkerCannotBeReplacedWhileRepairAwaits()
    {
        await Repair(async token =>
        {
            Assert.ThrowsException<IOException>(() => Directory.Move(root, root + "-moved"));
            Assert.ThrowsException<IOException>(() => File.WriteAllText(
                Path.Combine(root, PortableBrokerContext.MarkerFileName), "changed"));
            return await Acquire(token);
        });
        Assert.IsTrue(Directory.Exists(root));
        Assert.IsFalse(Directory.Exists(root + "-moved"));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementFailureBeforeOrAfterAtomicSwapPreservesOriginal(bool afterSwap)
    {
        var files = new ThrowingCommitter { ThrowAfter = afterSwap };
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair(committer: files));
        Assert.AreEqual("previous image", File.ReadAllText(destination));
        Assert.AreEqual(1, disposals);
        AssertCleanStage();
    }

    [TestMethod]
    public async Task FailedMissingImageCommitRollsBackToMissing()
    {
        File.Delete(destination);
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair(
            committer: new ThrowingCommitter { ThrowAfter = true }));
        Assert.IsFalse(File.Exists(destination));
        AssertCleanStage();
    }

    [TestMethod]
    public async Task BlockedRollbackPreservesTheOriginalBackupForRecovery()
    {
        PortableBrokerStartupException error = await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair(
            committer: new ThrowingCommitter { ThrowAfter = true, FailRollback = true }));
        StringAssert.Contains(error.Message, "rollback");
        string backup = Directory.EnumerateFiles(root, ".viiper-repair-*.bak").Single();
        Assert.AreEqual("previous image", File.ReadAllText(backup));
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(destination));
        Assert.AreEqual(0, Directory.EnumerateFiles(root, ".viiper-repair-*.tmp").Count());
    }

    [TestMethod]
    public async Task DestinationMustPassPathInspectionBeforeAnyPayloadAcquisition()
    {
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Repair(inspect: path =>
        {
            if (path == destination) throw new IOException("injected reparse point");
        }));
        Assert.AreEqual(0, acquisitions);
        Assert.AreEqual("previous image", File.ReadAllText(destination));
    }

    private sealed class ThrowingCommitter : IPortableBrokerRepairCommitter
    {
        internal bool ThrowAfter, FailRollback;
        private int calls;
        public void Replace(string source, string target, string backup)
        {
            calls++;
            if (calls > 1)
            {
                if (FailRollback) throw new IOException("injected rollback failure");
                File.Replace(source, target, backup);
                return;
            }
            if (ThrowAfter) File.Replace(source, target, backup);
            throw new IOException("injected atomic replacement failure");
        }
        public void Move(string source, string target)
        {
            if (ThrowAfter) File.Move(source, target);
            throw new IOException("injected atomic move failure");
        }
    }

    internal sealed class FakeHost : IPortableBrokerProcessHost
    {
        internal readonly List<PortableBrokerProcessIdentity> Peers = new();
        internal readonly List<PortableBrokerProcessIdentity> Stopped = new();
        internal bool SnapshotFailure, StopFailure, IgnoreStop;
        internal int Snapshots, Starts;
        internal string[] Arguments;
        internal Action AfterStop;
        internal FakeProcess Child;
        public IReadOnlyList<PortableBrokerProcessIdentity> Snapshot()
        {
            Snapshots++;
            if (SnapshotFailure) throw new IOException("injected unreadable process metadata");
            return Peers.ToArray();
        }
        public IReadOnlyList<string> ReadArguments(PortableBrokerProcessIdentity identity) => Arguments;
        public IPortableBrokerProcess Start(ProcessStartInfo startInfo)
        {
            Starts++;
            Arguments = startInfo.ArgumentList.ToArray();
            Child = new FakeProcess(this, new(101, 102, startInfo.FileName));
            Peers.Add(Child.Identity);
            return Child;
        }
        public void StopForRepair(PortableBrokerProcessIdentity identity, int timeoutMilliseconds)
        {
            Assert.IsTrue(timeoutMilliseconds is > 0 and <= 5000);
            if (StopFailure) throw new IOException("injected stop timeout");
            if (!Peers.Contains(identity)) throw new IOException("injected identity changed");
            Stopped.Add(identity);
            if (!IgnoreStop) Peers.Remove(identity);
            AfterStop?.Invoke();
        }
    }

    internal sealed class FakeProcess : IPortableBrokerProcess
    {
        private readonly FakeHost host;
        internal readonly PortableBrokerProcessIdentity Identity;
        internal int NormalStops, Disposals;
        internal FakeProcess(FakeHost host, PortableBrokerProcessIdentity identity) { this.host = host; Identity = identity; }
        public int ProcessId => Identity.ProcessId;
        public long StartTimeUtcTicks => Identity.StartTimeUtcTicks;
        public bool IsRunning => host.Peers.Contains(Identity);
        public bool IdentityMatches => host.Peers.Contains(Identity);
        public void StopAndWait(int timeoutMilliseconds) { NormalStops++; host.Peers.Remove(Identity); }
        public void Dispose() => Disposals++;
    }
}
