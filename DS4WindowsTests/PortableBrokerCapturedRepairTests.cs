using System.Security.Cryptography;
using DS4Windows;
using FakeHost = DS4WindowsTests.PortableBrokerRepairTests.FakeHost;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class PortableBrokerCapturedRepairTests
{
    private string root, localImage, foreignImage, payload, digest;
    private FakeHost host;
    private int acquisitions;
    private static readonly byte[] Release = "current pinned release; never executed"u8.ToArray();

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(AppContext.BaseDirectory, "portable-captured-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), PortableBrokerContext.MarkerText);
        localImage = Path.Combine(root, "viiper.exe");
        foreignImage = Path.Combine(root, "other-copy", "viiper.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(foreignImage));
        File.WriteAllText(foreignImage, "foreign image must not be replaced");
        File.WriteAllBytes(localImage, Release);
        payload = Path.Combine(root, "release.bin");
        File.WriteAllBytes(payload, Release);
        digest = Convert.ToHexString(SHA256.HashData(Release));
        host = new FakeHost();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
            Path.GetFileName(root).StartsWith("portable-captured-", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }

    private Task<ViiperPayloadLease> Acquire(string directory, CancellationToken token)
    {
        Assert.AreEqual(root, directory);
        token.ThrowIfCancellationRequested();
        acquisitions++;
        return Task.FromResult(new ViiperPayloadLease(payload));
    }

    private Task Prepare(IReadOnlyList<PortableBrokerProcessIdentity> snapshot,
        Func<string, CancellationToken, Task<ViiperPayloadLease>> acquire = null) =>
        PortableBrokerMaintenance.PrepareStartupAsync(root, digest, acquire ?? Acquire,
            snapshot, host, () => Array.Empty<string>());

    [TestMethod]
    public void CapturedForeignIdentityIsStoppedWhileSamePathBorrowCandidateIsPreserved()
    {
        PortableBrokerProcessIdentity local = new(1, 101, localImage);
        PortableBrokerProcessIdentity foreign = new(2, 102, foreignImage);
        host.Peers.AddRange(new[] { local, foreign });
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        PortableBrokerProcessHost.StopCapturedForRepair(captured, localImage, host);
        Assert.AreEqual(foreign, host.Stopped.Single());
        Assert.AreEqual(local, host.Peers.Single());
        Assert.AreEqual(2, captured.Count, "Captured identities must not follow mutations of the live snapshot.");
        Assert.AreEqual("foreign image must not be replaced", File.ReadAllText(foreignImage));
    }

    [DataTestMethod]
    [DataRow("newcomer")]
    [DataRow("pid-reused")]
    [DataRow("path-changed")]
    public void NewOrChangedIdentityFailsBeforeStoppingAnyCapturedBroker(string change)
    {
        host.Peers.Add(new(2, 102, foreignImage));
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        if (change == "newcomer") host.Peers.Add(new(3, 103, localImage));
        if (change == "pid-reused") host.Peers[0] = host.Peers[0] with { StartTimeUtcTicks = 999 };
        if (change == "path-changed") host.Peers[0] = host.Peers[0] with { ExecutablePath = localImage };
        Assert.ThrowsException<PortableBrokerStartupException>(() =>
            PortableBrokerProcessHost.StopCapturedForRepair(captured, processHost: host));
        Assert.AreEqual(0, host.Stopped.Count);
    }

    [DataTestMethod]
    [DataRow("name")]
    [DataRow("unknown")]
    [DataRow("relative")]
    [DataRow("time")]
    [DataRow("duplicate")]
    public void UnknownOrUnrelatedExecutableCannotBeCapturedOrStopped(string invalid)
    {
        string path = invalid == "name" ? Path.Combine(root, "unrelated.exe") :
            invalid == "unknown" ? null : invalid == "relative" ? "viiper.exe" : foreignImage;
        host.Peers.Add(new(2, invalid == "time" ? 0 : 102, path));
        if (invalid == "duplicate") host.Peers.Add(host.Peers[0]);
        Assert.ThrowsException<PortableBrokerStartupException>(() => PortableBrokerProcessHost.CaptureForRepair(host));
        Assert.ThrowsException<PortableBrokerStartupException>(() =>
            PortableBrokerProcessHost.StopCapturedForRepair(host.Peers.ToArray(), processHost: host));
        Assert.AreEqual(0, host.Stopped.Count);
    }

    [TestMethod]
    public void AlreadyExitedCapturedIdentityIsNotReplacedWithAFreshSelection()
    {
        host.Peers.Add(new(2, 102, foreignImage));
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        host.Peers.Clear();
        PortableBrokerProcessHost.StopCapturedForRepair(captured, processHost: host);
        Assert.AreEqual(0, host.Stopped.Count);
    }

    [TestMethod]
    public void NewcomerAfterExactStopFailsWithoutStoppingTheNewIdentity()
    {
        PortableBrokerProcessIdentity old = new(2, 102, foreignImage);
        PortableBrokerProcessIdentity newcomer = new(3, 103, foreignImage);
        host.Peers.Add(old);
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        host.AfterStop = () => host.Peers.Add(newcomer);
        Assert.ThrowsException<PortableBrokerStartupException>(() =>
            PortableBrokerProcessHost.StopCapturedForRepair(captured, processHost: host));
        Assert.AreEqual(old, host.Stopped.Single());
        Assert.AreEqual(newcomer, host.Peers.Single());
    }

    [TestMethod]
    public void StopFailureDoesNotReportSuccessOrSelectAnAlternativeProcess()
    {
        host.Peers.Add(new(2, 102, foreignImage));
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        host.StopFailure = true;
        Assert.ThrowsException<IOException>(() => PortableBrokerProcessHost.StopCapturedForRepair(captured, processHost: host));
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.AreEqual(1, host.Peers.Count);
    }

    [TestMethod]
    public async Task MatchingLocalImageStillRecoversCapturedForeignConflictWithoutDownloading()
    {
        host.Peers.Add(new(2, 102, foreignImage));
        await Prepare(PortableBrokerProcessHost.CaptureForRepair(host));
        Assert.AreEqual(1, host.Stopped.Count);
        Assert.AreEqual(0, acquisitions);
        Assert.AreEqual(0, host.Starts, "Normal startup, not maintenance, owns the subsequent broker launch.");
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(localImage));
        Assert.AreEqual("foreign image must not be replaced", File.ReadAllText(foreignImage));
        using PortableBrokerContext context = PortableBrokerContext.Create(root, digest, host, () => Array.Empty<string>());
        Assert.IsTrue(context.IsVerifiedBackend(localImage));
    }

    [TestMethod]
    public async Task MatchingSamePathBrokerKeepsNormalBorrowPolicyAndIsNeverStoppedAtPreparation()
    {
        PortableBrokerProcessIdentity local = new(1, 101, localImage);
        host.Peers.Add(local);
        await Prepare(PortableBrokerProcessHost.CaptureForRepair(host));
        Assert.AreEqual(local, host.Peers.Single());
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.AreEqual(0, acquisitions);
    }

    [TestMethod]
    public async Task MismatchedLocalImageIsReplacedAfterCapturedLocalAndForeignBrokersExit()
    {
        File.WriteAllText(localImage, "older local release");
        host.Peers.AddRange(new[] { new PortableBrokerProcessIdentity(1, 101, localImage), new(2, 102, foreignImage) });
        await Prepare(PortableBrokerProcessHost.CaptureForRepair(host));
        Assert.AreEqual(2, host.Stopped.Count);
        Assert.AreEqual(0, host.Peers.Count);
        Assert.AreEqual(1, acquisitions);
        CollectionAssert.AreEqual(Release, File.ReadAllBytes(localImage));
        Assert.AreEqual("foreign image must not be replaced", File.ReadAllText(foreignImage));
    }

    [TestMethod]
    public async Task UnverifiedPayloadCannotAuthorizeStoppingACapturedProcess()
    {
        File.WriteAllText(localImage, "older local release");
        File.WriteAllText(payload, "wrong release");
        host.Peers.Add(new(2, 102, foreignImage));
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() =>
            Prepare(PortableBrokerProcessHost.CaptureForRepair(host)));
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.AreEqual("older local release", File.ReadAllText(localImage));
    }

    [TestMethod]
    public async Task IdentityAppearingDuringPayloadWaitIsNeverAddedToTheAuthorizedSnapshot()
    {
        File.WriteAllText(localImage, "older local release");
        host.Peers.Add(new(2, 102, foreignImage));
        IReadOnlyList<PortableBrokerProcessIdentity> captured = PortableBrokerProcessHost.CaptureForRepair(host);
        await Assert.ThrowsExceptionAsync<PortableBrokerStartupException>(() => Prepare(captured, async (directory, token) =>
        {
            host.Peers.Add(new(3, 103, localImage));
            return await Acquire(directory, token);
        }));
        Assert.AreEqual(0, host.Stopped.Count);
        Assert.AreEqual("older local release", File.ReadAllText(localImage));
    }
}
