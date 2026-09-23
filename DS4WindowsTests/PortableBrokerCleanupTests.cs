using System.Diagnostics;
using System.Security.Cryptography;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class PortableBrokerCleanupTests
{
    private string root;
    private PortableBrokerContext context;
    private FakeHost host;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(AppContext.BaseDirectory, "portable cleanup " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), PortableBrokerContext.MarkerText);
        byte[] image = "fake cleanup fixture; never executed"u8.ToArray();
        string imagePath = Path.Combine(root, "viiper.exe");
        File.WriteAllBytes(imagePath, image);
        host = new FakeHost(imagePath);
        context = PortableBrokerContext.Create(root, Convert.ToHexString(SHA256.HashData(image)), host,
            () => Array.Empty<string>());
    }

    [TestCleanup]
    public void Cleanup()
    {
        host.Child.StopMode = "success";
        host.Child.InspectionThrows = false;
        context?.Dispose();
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
            Path.GetFileName(root).StartsWith("portable cleanup ", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }

    [DataTestMethod]
    [DataRow("throws")]
    [DataRow("still-running")]
    public void FailedDisposeRetainsOwnedHandleAndPinsForAnotherVerifiedStop(string mode)
    {
        context.Start();
        host.Child.StopMode = mode;
        PortableBrokerStartupException error = Assert.ThrowsException<PortableBrokerStartupException>(() => context.Dispose());
        StringAssert.Contains(error.Message, "could not be confirmed stopped");
        Assert.AreEqual(0, host.Child.DisposeCalls);
        Assert.IsTrue(host.Child.Running);
        AssertProtected();
        Assert.IsFalse(context.InspectOwnedProcess(out _, out _), "A failed retirement must not resume controller output.");

        host.Child.StopMode = "success";
        context.Dispose();
        context.Dispose();
        Assert.AreEqual(2, host.Child.StopCalls);
        Assert.AreEqual(1, host.Child.DisposeCalls);
        Assert.AreEqual(0, host.Peers.Count);
        AssertReleased();
    }

    [DataTestMethod]
    [DataRow("throws")]
    [DataRow("still-running")]
    [DataRow("inspection-throws")]
    public void FailedLaunchCleanupKeepsIdentitySoExplicitRepairCanRetry(string mode)
    {
        host.Child.StopMode = mode;
        host.AfterStart = () =>
        {
            // A concurrent foreign broker invalidates startup, but is never
            // ours to terminate. Every process in this test is synthetic.
            host.Peers.Add(new(999, 1000, Path.Combine(root, "foreign", "viiper.exe")));
            host.Child.InspectionThrows = mode == "inspection-throws";
        };
        PortableBrokerStartupException error = Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        StringAssert.Contains(error.Message, "could not be confirmed stopped");
        Assert.AreEqual(0, host.Child.DisposeCalls);
        AssertProtected();
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.AreEqual(1, host.Starts, "Retry must not launch a second broker over an unretired child.");

        host.Peers.RemoveAll(peer => peer.ProcessId == 999);
        host.Child.InspectionThrows = false;
        host.Child.StopMode = "success";
        context.RetireForRepair(host.Snapshot());
        Assert.AreEqual(1, host.RepairStops.Count,
            "The surviving child remains the selected owner, not an unauthorized newcomer.");
        Assert.AreEqual(123, host.RepairStops[0].ProcessId);
        Assert.AreEqual(456, host.RepairStops[0].StartTimeUtcTicks);
        Assert.AreEqual(1, host.Child.DisposeCalls);
        AssertReleased();
    }

    [TestMethod]
    public void ExplicitRepairStopFailurePreservesPinsAndCanRetrySameCapturedOwner()
    {
        context.Start();
        PortableBrokerProcessIdentity[] captured = host.Snapshot().ToArray();
        host.RepairStopThrows = true;
        Assert.ThrowsException<IOException>(() => context.RetireForRepair(captured));
        Assert.AreEqual(0, host.Child.DisposeCalls);
        AssertProtected();
        host.RepairStopThrows = false;
        context.RetireForRepair(captured);
        Assert.AreEqual(2, host.RepairStops.Count);
        Assert.AreEqual(captured[0], host.RepairStops[1]);
        AssertReleased();
    }

    [TestMethod]
    public void FailedCleanupCannotAuthorizeReusedPidOrSamePathSuccessor()
    {
        context.Start();
        host.Child.StopMode = "still-running";
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Dispose());
        PortableBrokerProcessIdentity original = host.Peers.Single();
        PortableBrokerProcessIdentity successor = original with { StartTimeUtcTicks = original.StartTimeUtcTicks + 1 };
        host.Peers[0] = successor;
        host.Child.SameIdentity = false;
        PortableBrokerStartupException error = Assert.ThrowsException<PortableBrokerStartupException>(
            () => context.RetireForRepair(host.Snapshot()));
        StringAssert.Contains(error.Message, "owner changed");
        Assert.AreEqual(0, host.RepairStops.Count);
        AssertProtected();

        context.Dispose(); // A stale handle is released, never retargeted.
        Assert.AreEqual(1, host.Child.StopCalls);
        Assert.AreEqual(successor, host.Peers.Single());
        AssertReleased();
    }

    [TestMethod]
    public void ChildExitingAfterFailedRetirementReleasesPinsWithoutAnotherKill()
    {
        context.Start();
        host.Child.StopMode = "throws";
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Dispose());
        host.Child.Running = false;
        host.Peers.Clear();
        context.Dispose();
        Assert.AreEqual(1, host.Child.StopCalls);
        Assert.AreEqual(1, host.Child.DisposeCalls);
        AssertReleased();
    }

    private void AssertProtected()
    {
        Assert.IsTrue(context.IsVerifiedBackend(context.ViiperPath));
        Assert.ThrowsException<IOException>(() => File.WriteAllText(context.ViiperPath, "must remain pinned"));
        Assert.ThrowsException<IOException>(() => File.WriteAllText(context.ConfigPath, "must remain pinned"));
    }

    private void AssertReleased()
    {
        Assert.IsFalse(context.IsVerifiedBackend(context.ViiperPath));
        File.WriteAllText(context.ViiperPath, "verified cleanup released image");
        File.WriteAllText(context.ConfigPath, "verified cleanup released config");
    }

    private sealed class FakeHost : IPortableBrokerProcessHost
    {
        private readonly string imagePath;
        internal readonly List<PortableBrokerProcessIdentity> Peers = new();
        internal readonly List<PortableBrokerProcessIdentity> RepairStops = new();
        internal readonly FakeProcess Child;
        internal Action AfterStart;
        internal bool RepairStopThrows;
        internal int Starts;
        internal FakeHost(string imagePath)
        {
            this.imagePath = imagePath;
            Child = new FakeProcess(this);
        }
        public IReadOnlyList<PortableBrokerProcessIdentity> Snapshot() => Peers.ToArray();
        public IReadOnlyList<string> ReadArguments(PortableBrokerProcessIdentity identity) =>
            throw new AssertFailedException("This fixture does not borrow processes.");
        public IPortableBrokerProcess Start(ProcessStartInfo startInfo)
        {
            Starts++;
            Child.Running = true;
            Peers.Add(new(Child.ProcessId, Child.StartTimeUtcTicks, imagePath));
            AfterStart?.Invoke();
            return Child;
        }
        public void StopForRepair(PortableBrokerProcessIdentity identity, int timeoutMilliseconds)
        {
            Assert.IsTrue(timeoutMilliseconds is > 0 and <= 5000);
            RepairStops.Add(identity);
            if (RepairStopThrows) throw new IOException("Injected retirement failure");
            Assert.AreEqual(Child.ProcessId, identity.ProcessId);
            Assert.AreEqual(Child.StartTimeUtcTicks, identity.StartTimeUtcTicks);
            Assert.AreEqual(imagePath, identity.ExecutablePath);
            Child.Running = false;
            Peers.RemoveAll(peer => peer == identity);
        }
    }

    private sealed class FakeProcess : IPortableBrokerProcess
    {
        private readonly FakeHost host;
        internal bool Running, InspectionThrows, SameIdentity = true;
        internal string StopMode = "success";
        internal int StopCalls, DisposeCalls;
        internal FakeProcess(FakeHost host) => this.host = host;
        public int ProcessId => 123;
        public long StartTimeUtcTicks => 456;
        public bool IsRunning => InspectionThrows ? throw new IOException("Injected process observation failure") : Running;
        public bool IdentityMatches => SameIdentity;
        public void StopAndWait(int timeoutMilliseconds)
        {
            Assert.AreEqual(5000, timeoutMilliseconds);
            StopCalls++;
            if (StopMode == "throws") throw new IOException("Injected stop failure");
            if (StopMode == "still-running") return;
            Running = false;
            host.Peers.RemoveAll(peer => peer.ProcessId == ProcessId && peer.StartTimeUtcTicks == StartTimeUtcTicks);
        }
        public void Dispose() => DisposeCalls++;
    }
}
