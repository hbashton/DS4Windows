using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using DS4Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4WindowsTests;

[TestClass]
public class ViiperManagedRepairTests
{
    private static readonly byte[] Release = Enumerable.Range(0, 251).Select(i => (byte)i).ToArray();
    private static readonly byte[] Previous = { 9, 8, 7, 6 };
    private static string Hash => Convert.ToHexString(SHA256.HashData(Release));

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VerifiedBrokerIsReplacedOrCreatedWithoutTouchingOtherFiles(bool existing)
    {
        using var fixture = new Fixture();
        if (existing) File.WriteAllBytes(fixture.Target, Previous);
        int stops = 0;
        await fixture.Repair(path =>
        {
            Assert.AreEqual(fixture.Target, path);
            Assert.AreEqual(existing, File.Exists(path));
            stops++;
        });
        Assert.AreEqual(1, stops);
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(fixture.Target));
        fixture.AssertUnrelatedUntouched();
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task HealthySamePathPayloadStopsSelectedBrokerWithoutReplacingFile()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Release);
        DateTime timestamp = File.GetLastWriteTimeUtc(fixture.Target);
        int stops = 0;
        await ViiperManagedRepair.RepairCoreAsync(fixture.Target, fixture.Target, Hash, true,
            path => { Assert.AreEqual(fixture.Target, path); stops++; },
            committer: new Committer { Forbid = true });
        Assert.AreEqual(1, stops);
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(fixture.Target));
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task MissingBrokerSubdirectoryCanBeCreatedOnlyBelowExistingInstallation()
    {
        using var fixture = new Fixture();
        string target = Path.Combine(fixture.Root, "existing-install", "VIIPER", "viiper.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(target)));
        await ViiperManagedRepair.RepairCoreAsync(fixture.Payload, target, Hash, true, _ => { });
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(target));
        string absent = Path.Combine(fixture.Root, "absent-install", "VIIPER", "viiper.exe");
        await Assert.ThrowsExceptionAsync<System.ComponentModel.Win32Exception>(() =>
            ViiperManagedRepair.RepairCoreAsync(fixture.Payload, absent, Hash, true, _ => Assert.Fail()));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "absent-install")));
    }

    [TestMethod]
    public async Task NonAdministratorIsRejectedBeforeMutationOrProcessStop()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() =>
            ViiperManagedRepair.RepairCoreAsync(fixture.Payload, fixture.Target, Hash, false, _ => Assert.Fail()));
        fixture.AssertOriginalDirectory();
    }

    [TestMethod]
    public async Task InvalidPayloadIsRejectedBeforeProcessStopAndPreservesInstalledImage()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        File.WriteAllText(fixture.Payload, "untrusted bytes");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Repair(_ => Assert.Fail()));
        CollectionAssert.AreEqual(Previous, File.ReadAllBytes(fixture.Target));
        fixture.AssertNoTemporaryFiles();
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory, ".viiper-managed-repair.lock")));
    }

    [TestMethod]
    public async Task CancellationBeforeWorkCreatesNothing()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.Repair(_ => Assert.Fail(), cancel.Token));
        fixture.AssertOriginalDirectory();
    }

    [TestMethod]
    public async Task CancellationAtStopBoundaryCannotCommitPreparedImage()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        using var cancel = new CancellationTokenSource();
        int stops = 0;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fixture.Repair(_ =>
        { stops++; cancel.Cancel(); }, cancel.Token, new Committer { Forbid = true }));
        Assert.AreEqual(1, stops);
        CollectionAssert.AreEqual(Previous, File.ReadAllBytes(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task FailedExactProcessStopPreventsReplacement()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Repair(_ => throw new IOException("changed process identity")));
        CollectionAssert.AreEqual(Previous, File.ReadAllBytes(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task DestinationChangeDuringStopIsNotOverwritten()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Repair(_ => File.WriteAllText(fixture.Target, "new owner")));
        Assert.AreEqual("new owner", File.ReadAllText(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CommitFailureBeforeOrAfterAtomicReplacementRestoresOriginal(bool after)
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        var files = new Committer { FailFirst = true, FailAfter = after };
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Repair(_ => { }, files: files));
        CollectionAssert.AreEqual(Previous, File.ReadAllBytes(fixture.Target));
        fixture.AssertNoTemporaryFiles();
        fixture.AssertUnrelatedUntouched();
    }

    [TestMethod]
    public async Task FailedNewFileCommitRollsBackOnlyItsOwnExpectedImage()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Repair(_ => { },
            files: new Committer { FailFirst = true, FailAfter = true }));
        Assert.IsFalse(File.Exists(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task UncertainRollbackPreservesBackupInsteadOfClobberingDifferentImage()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Repair(_ => { }, files:
            new Committer { FailFirst = true, FailAfter = true, AlterDestination = true }));
        Assert.AreEqual("unexpected replacement", File.ReadAllText(fixture.Target));
        string[] backups = System.IO.Directory.GetFiles(fixture.Directory, "*.bak");
        Assert.AreEqual(1, backups.Length);
        CollectionAssert.AreEqual(Previous, File.ReadAllBytes(backups[0]));
        fixture.AssertUnrelatedUntouched();
    }

    [TestMethod]
    public async Task ExternalImagePinBlocksCommitWithoutChangingOldBytes()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        using var pin = File.Open(fixture.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Repair(_ => { }));
        CollectionAssert.AreEqual(Previous, File.ReadAllBytes(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task ConcurrentRepairLockFailsClosedBeforeStoppingProcesses()
    {
        using var fixture = new Fixture();
        using var pin = File.Open(Path.Combine(fixture.Directory, ".viiper-managed-repair.lock"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsExceptionAsync<IOException>(() => fixture.Repair(_ => Assert.Fail()));
        Assert.IsFalse(File.Exists(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public void HelperParsingDoesNotInvokeAnotherStartupModeOrAcceptExtraArguments()
    {
        Assert.IsFalse(ViiperManagedRepair.TryRunHelper(new[] { "--something-else" }, out int unrelated));
        Assert.AreEqual(0, unrelated);
        Assert.IsTrue(ViiperManagedRepair.TryRunHelper(new[] { ViiperManagedRepair.HelperArgument }, out int missing));
        Assert.AreNotEqual(0, missing);
        Assert.IsTrue(ViiperManagedRepair.TryRunHelper(new[] { "--other", ViiperManagedRepair.HelperArgument, "payload" }, out int extra));
        Assert.AreNotEqual(0, extra);
        Assert.AreEqual(TimeSpan.FromSeconds(30), ViiperManagedRepair.HelperDeadline);
        Assert.AreEqual(TimeSpan.FromSeconds(45), ViiperManagedRepair.ParentDeadline);
    }

    [TestMethod]
    public async Task CapturedForeignAndCanonicalBrokersStopBeforeCanonicalReplacementOnly()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        string foreign = Path.Combine(fixture.Root, "other", "viiper.exe");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(foreign));
        File.WriteAllText(foreign, "foreign image must not be replaced");
        var captured = new[] { new PortableBrokerProcessIdentity(100, 1000, fixture.Target), new PortableBrokerProcessIdentity(200, 2000, foreign) };
        var host = new FakeProcessHost(captured);
        await ViiperManagedRepair.RepairCapturedCoreAsync(fixture.Payload, fixture.Target, Hash, true, captured, processHost: host);
        CollectionAssert.AreEquivalent(new[] { 100, 200 }, host.Stopped.ToArray());
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(fixture.Target));
        Assert.AreEqual("foreign image must not be replaced", File.ReadAllText(foreign));
        fixture.AssertUnrelatedUntouched();
    }

    [TestMethod]
    public async Task HealthySamePathStillClosesOnlyCapturedForeignConflict()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Release);
        var captured = new[] { new PortableBrokerProcessIdentity(201, 2001, Path.Combine(fixture.Root, "foreign", "viiper.exe")) };
        var host = new FakeProcessHost(captured);
        await ViiperManagedRepair.RepairCapturedCoreAsync(fixture.Target, fixture.Target, Hash, true, captured,
            processHost: host, committer: new Committer { Forbid = true });
        CollectionAssert.AreEqual(new[] { 201 }, host.Stopped.ToArray());
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [DataTestMethod]
    [DataRow("newcomer")]
    [DataRow("reused-pid")]
    [DataRow("changed-image")]
    [DataRow("unknown")]
    public async Task ChangedPostElevationSnapshotCannotStopOrReplace(string change)
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        var original = new PortableBrokerProcessIdentity(300, 3000, fixture.Target);
        var captured = new[] { original };
        var current = change switch
        {
            "newcomer" => new[] { original, new PortableBrokerProcessIdentity(301, 3001, Path.Combine(fixture.Root, "new", "viiper.exe")) },
            "reused-pid" => new[] { original with { StartTimeUtcTicks = 3001 } },
            "changed-image" => new[] { original with { ExecutablePath = Path.Combine(fixture.Root, "different", "viiper.exe") } },
            _ => new[] { original with { ExecutablePath = null } }
        };
        var host = new FakeProcessHost(current);
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() =>
            ViiperManagedRepair.RepairCapturedCoreAsync(fixture.Payload, fixture.Target, Hash, true, captured, processHost: host));
        Assert.AreEqual(0, host.Stopped.Count);
        CollectionAssert.AreEqual(Previous, File.ReadAllBytes(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task AlreadyExitedCapturedBrokerIsNotReplacedByFreshEnumerationAuthority()
    {
        using var fixture = new Fixture();
        var captured = new[] { new PortableBrokerProcessIdentity(400, 4000, fixture.Target) };
        var host = new FakeProcessHost(Array.Empty<PortableBrokerProcessIdentity>());
        await ViiperManagedRepair.RepairCapturedCoreAsync(fixture.Payload, fixture.Target, Hash, true, captured, processHost: host);
        Assert.AreEqual(0, host.Stopped.Count);
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(fixture.Target));
    }

    [TestMethod]
    public async Task NewBrokerAfterCapturedStopBlocksImageCommit()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Target, Previous);
        var captured = new[] { new PortableBrokerProcessIdentity(500, 5000, fixture.Target) };
        var host = new FakeProcessHost(captured)
        {
            AfterStop = peers => peers.Add(new(501, 5001, Path.Combine(fixture.Root, "new", "viiper.exe")))
        };
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() =>
            ViiperManagedRepair.RepairCapturedCoreAsync(fixture.Payload, fixture.Target, Hash, true, captured, processHost: host));
        CollectionAssert.AreEqual(new[] { 500 }, host.Stopped.ToArray());
        CollectionAssert.AreEqual(Previous, File.ReadAllBytes(fixture.Target));
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public void CrossElevationSnapshotIsBoundedExactAndImmutable()
    {
        var original = new[] { new PortableBrokerProcessIdentity(600, 6000, @"C:\Other folder\viiper.exe") };
        string encoded = ViiperManagedRepair.EncodeSnapshot(original);
        original[0] = new(601, 6001, @"C:\changed\viiper.exe");
        PortableBrokerProcessIdentity[] decoded = ViiperManagedRepair.DecodeSnapshot(encoded);
        Assert.AreEqual(new PortableBrokerProcessIdentity(600, 6000, @"C:\Other folder\viiper.exe"), decoded.Single());
        Assert.AreEqual(0, ViiperManagedRepair.DecodeSnapshot(ViiperManagedRepair.EncodeSnapshot(Array.Empty<PortableBrokerProcessIdentity>())).Length);
        Assert.ThrowsException<InvalidDataException>(() => ViiperManagedRepair.EncodeSnapshot(new[] { decoded[0], decoded[0] }));
        Assert.ThrowsException<InvalidDataException>(() => ViiperManagedRepair.EncodeSnapshot(Enumerable.Repeat(decoded[0], 257).ToArray()));
        Assert.ThrowsException<InvalidDataException>(() => ViiperManagedRepair.DecodeSnapshot(new string('A', 30_000)));
    }

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("[null]")]
    [DataRow("[{\"ProcessId\":1}]")]
    [DataRow("[{\"ProcessId\":1,\"ProcessId\":2,\"StartTimeUtcTicks\":1,\"ExecutablePath\":\"C:\\\\x\\\\viiper.exe\"}]")]
    [DataRow("[{\"ProcessId\":0,\"StartTimeUtcTicks\":1,\"ExecutablePath\":\"C:\\\\x\\\\viiper.exe\"}]")]
    [DataRow("[{\"ProcessId\":1,\"StartTimeUtcTicks\":1,\"ExecutablePath\":\"relative/viiper.exe\"}]")]
    [DataRow("[{\"ProcessId\":1,\"StartTimeUtcTicks\":1,\"ExecutablePath\":\"C:\\\\x\\\\not-viiper.exe\"}]")]
    [DataRow("[{\"ProcessId\":1,\"StartTimeUtcTicks\":1,\"ExecutablePath\":null}]")]
    [DataRow("[{\"ProcessId\":1,\"StartTimeUtcTicks\":1,\"ExecutablePath\":\"C:\\\\x\\\\viiper.exe\",\"Other\":true}]")]
    public void MalformedCrossElevationIdentityCannotBecomeStopAuthority(string json)
    {
        Assert.ThrowsException<InvalidDataException>(() => ViiperManagedRepair.DecodeSnapshot(Convert.ToBase64String(Encoding.UTF8.GetBytes(json))));
    }

    private sealed class FakeProcessHost(IEnumerable<PortableBrokerProcessIdentity> initial) : IPortableBrokerProcessHost
    {
        private readonly List<PortableBrokerProcessIdentity> peers = initial.ToList();
        internal readonly List<int> Stopped = new();
        internal Action<List<PortableBrokerProcessIdentity>> AfterStop;
        public IReadOnlyList<PortableBrokerProcessIdentity> Snapshot() => peers.ToArray();
        public void StopForRepair(PortableBrokerProcessIdentity identity, int timeoutMilliseconds)
        {
            Assert.IsTrue(timeoutMilliseconds is > 0 and <= 5000);
            Assert.IsTrue(peers.Remove(identity), "Only the exact captured identity may be stopped.");
            Stopped.Add(identity.ProcessId);
            AfterStop?.Invoke(peers);
        }
        public IReadOnlyList<string> ReadArguments(PortableBrokerProcessIdentity identity) => throw new AssertFailedException("No live process arguments needed.");
        public IPortableBrokerProcess Start(ProcessStartInfo startInfo) => throw new AssertFailedException("The helper must not start a broker.");
    }

    private sealed class Committer : IPortableBrokerRepairCommitter
    {
        internal bool Forbid, FailFirst, FailAfter, AlterDestination;
        private int calls;
        public void Replace(string source, string destination, string backup) => Run(() => File.Replace(source, destination, backup), destination);
        public void Move(string source, string destination) => Run(() => File.Move(source, destination), destination);
        private void Run(Action action, string destination)
        {
            if (Forbid) Assert.Fail("The image must not be replaced.");
            bool fail = FailFirst && calls++ == 0;
            if (fail && !FailAfter) throw new IOException("before atomic commit");
            action();
            if (fail && AlterDestination) File.WriteAllText(destination, "unexpected replacement");
            if (fail) throw new IOException("after atomic commit");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ViiperManagedRepairTests-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(Root, "DS4Windows", "VIIPER");
        internal string Payload => Path.Combine(Root, "source.exe");
        internal string Target => Path.Combine(Directory, "viiper.exe");
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Payload, Release);
            File.WriteAllText(Path.Combine(Directory, "config.json"), "config sentinel");
            File.WriteAllText(Path.Combine(Directory, "viiper.key.txt"), "test-only key sentinel");
            File.WriteAllText(Path.Combine(Root, "DS4Windows", "profile.xml"), "profile sentinel");
        }
        internal Task Repair(Action<string> stop, CancellationToken token = default, IPortableBrokerRepairCommitter files = null) =>
            ViiperManagedRepair.RepairCoreAsync(Payload, Target, Hash, true, stop, token, files);
        internal void AssertUnrelatedUntouched()
        {
            Assert.AreEqual("config sentinel", File.ReadAllText(Path.Combine(Directory, "config.json")));
            Assert.AreEqual("test-only key sentinel", File.ReadAllText(Path.Combine(Directory, "viiper.key.txt")));
            Assert.AreEqual("profile sentinel", File.ReadAllText(Path.Combine(Root, "DS4Windows", "profile.xml")));
            CollectionAssert.AreEqual(Release, File.ReadAllBytes(Payload));
        }
        internal void AssertNoTemporaryFiles()
        {
            Assert.AreEqual(0, System.IO.Directory.GetFiles(Directory, "*.tmp").Length);
            Assert.AreEqual(0, System.IO.Directory.GetFiles(Directory, "*.bak").Length);
        }
        internal void AssertOriginalDirectory()
        {
            CollectionAssert.AreEquivalent(new[] { "config.json", "viiper.key.txt" },
                System.IO.Directory.GetFiles(Directory).Select(Path.GetFileName).ToArray());
        }
        public void Dispose()
        {
            string path = Path.GetFullPath(Root);
            if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(path).StartsWith("ViiperManagedRepairTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup target changed.");
            System.IO.Directory.Delete(path, recursive: true);
        }
    }
}
