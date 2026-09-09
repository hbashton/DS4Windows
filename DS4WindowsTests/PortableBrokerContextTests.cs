using System.Diagnostics;
using System.Security.Cryptography;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class PortableBrokerContextTests
{
    private string root, digest;
    private FakeHost host;
    private PortableBrokerContext context;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(AppContext.BaseDirectory, "portable-broker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), PortableBrokerContext.MarkerText + "\n");
        byte[] image = "fake release image; never executed"u8.ToArray();
        File.WriteAllBytes(Path.Combine(root, "viiper.exe"), image);
        digest = Convert.ToHexString(SHA256.HashData(image));
        host = new FakeHost(Path.Combine(root, "viiper.exe"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        context?.Dispose();
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
            Path.GetFileName(root).StartsWith("portable-broker-", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }

    private PortableBrokerContext Open(Func<IEnumerable<string>> managed = null, Action<string> pathCheck = null) =>
        context = PortableBrokerContext.Create(root, digest, host, managed ?? (() => Array.Empty<string>()), pathCheck);

    [TestMethod]
    public void AbsentMarkerDoesNotInspectBrokerOrRegistryOrCreateData()
    {
        File.Delete(Path.Combine(root, PortableBrokerContext.MarkerFileName));
        host.SnapshotFailure = true;
        Assert.IsNull(Open(() => throw new InvalidOperationException("Registry must not be inspected.")));
        Assert.AreEqual(0, host.Snapshots);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "portable-data")));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("DS4Windows portable package v2")]
    [DataRow(" DS4Windows portable package v1")]
    [DataRow("DS4Windows portable package v1 extra")]
    public void InvalidMarkerFailsClosed(string marker)
    {
        File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), marker);
        Assert.ThrowsException<PortableBrokerStartupException>(() => Open());
        Assert.AreEqual(0, host.Starts);
    }

    [TestMethod]
    public void PreparationIsReadOnlyAndPinsExactReleaseImage()
    {
        Open();
        Assert.IsFalse(Directory.Exists(context.DataPath));
        Assert.IsTrue(context.IsVerifiedBackend(Path.Combine(root, "viiper.exe")));
        Assert.IsFalse(context.IsVerifiedBackend(Path.Combine(root, "elsewhere", "viiper.exe")));
        Assert.ThrowsException<IOException>(() => File.WriteAllText(context.ViiperPath, "replace"));
        Assert.IsTrue(context.InspectOwnedProcess(out bool running, out _));
        Assert.IsFalse(running);
        context.Dispose();
        Assert.IsFalse(context.IsVerifiedBackend(context.ViiperPath));
        File.WriteAllText(context.ViiperPath, "pin released");
    }

    [TestMethod]
    public void AdjacentSidecarCannotAuthorizeWrongBinary()
    {
        File.WriteAllText(Path.Combine(root, "viiper.exe.sha256"), digest);
        digest = new string('0', 64);
        Assert.ThrowsException<PortableBrokerStartupException>(() => Open());
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "portable-data")));
        File.WriteAllText(Path.Combine(root, "viiper.exe"), "failed validation released handle");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RegisteredManagedRootAndDescendantsPreserveInstalledMode(bool parent)
    {
        host.SnapshotFailure = true;
        Assert.IsNull(Open(() => new[] { parent ? Path.GetDirectoryName(root) : root }));
        Assert.AreEqual(0, host.Snapshots);
        Assert.AreEqual(0, host.Starts);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "portable-data")));
    }

    [TestMethod]
    public void UpdaterCopiedStaleMarkerAndBadRootBrokerDoNotBrickManagedInstall()
    {
        File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName), "old or malformed portable marker");
        File.WriteAllText(Path.Combine(root, "viiper.exe"), "wrong or old root broker");
        host.SnapshotFailure = true;
        Assert.IsNull(Open(() => new[] { root + Path.DirectorySeparatorChar },
            _ => throw new IOException("Portable files must not be inspected in managed mode.")));
        Assert.AreEqual(0, host.Snapshots);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "portable-data")));
    }

    [DataTestMethod]
    [DataRow(".")]
    [DataRow(@"C:relative")]
    [DataRow(@"C:\")]
    [DataRow(@"\\server\share\DS4Windows")]
    public void InvalidRegisteredPathCannotAuthorizeManagedBypass(string managed)
    {
        Assert.ThrowsException<PortableBrokerStartupException>(() => Open(() => new[] { managed }));
        Assert.AreEqual(0, host.Starts);
    }

    [TestMethod]
    public void NormalPortablePathPolicyIsNotTheLabStoragePolicy()
    {
        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), PortableBrokerContext.ValidateRoot(path));
        }
        foreach (string path in new[] { ".", @"C:relative", @"\\server\share\portable", Path.GetPathRoot(root),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            Assert.ThrowsException<ArgumentException>(() => PortableBrokerContext.ValidateRoot(path));
    }

    [DataTestMethod]
    [DataRow("DS4Windows.portable")]
    [DataRow("viiper.exe")]
    [DataRow("portable-data/VIIPER")]
    [DataRow("portable-data/VIIPER/viiper.key.txt")]
    [DataRow("portable-data/VIIPER/viiper.json")]
    public void EverySelectedPathMustPassReparseInspection(string relative)
    {
        string rejected = Path.GetFullPath(Path.Combine(root, relative));
        Assert.ThrowsException<PortableBrokerStartupException>(() => Open(pathCheck: path =>
        {
            if (path == rejected) throw new IOException("Injected link rejection.");
        }));
        Assert.AreEqual(0, host.Starts);
    }

    [DataTestMethod]
    [DataRow("foreign")]
    [DataRow("unreadable")]
    [DataRow("multiple")]
    [DataRow("enumeration-failed")]
    public void ForeignMultipleOrUnknownOwnersAreNeverStoppedOrAdopted(string mode)
    {
        if (mode == "enumeration-failed") host.SnapshotFailure = true;
        else
        {
            host.Peers.Add(new(71, 72, mode == "unreadable" ? null : Path.Combine(root, "other", "viiper.exe")));
            if (mode == "multiple") host.Peers.Add(new(73, 74, host.ImagePath));
        }
        Assert.ThrowsException<PortableBrokerStartupException>(() => Open());
        Assert.AreEqual(0, host.Starts);
        Assert.AreEqual(0, host.Child.StopCalls);
    }

    [TestMethod]
    public void LaunchUsesOnlyLocalExplicitConfigurationAndClearsInheritedOverrides()
    {
        string old = Environment.GetEnvironmentVariable("VIIPER_LOG_RAW_FILE");
        Environment.SetEnvironmentVariable("VIIPER_LOG_RAW_FILE", "must-not-be-opened");
        try
        {
            Open().Start();
            ProcessStartInfo start = host.StartInfo;
            Assert.AreEqual(context.ViiperPath, start.FileName);
            Assert.AreEqual(root, start.WorkingDirectory);
            Assert.IsFalse(start.UseShellExecute);
            Assert.IsTrue(start.CreateNoWindow);
            Assert.AreEqual(ProcessWindowStyle.Hidden, start.WindowStyle);
            Assert.IsTrue(string.IsNullOrEmpty(start.Verb));
            Assert.IsFalse(start.Environment.Keys.Any(key => key.StartsWith("VIIPER_", StringComparison.OrdinalIgnoreCase)));
            CollectionAssert.Contains(start.ArgumentList.ToArray(), "--config-only");
            CollectionAssert.Contains(start.ArgumentList.ToArray(), context.ConfigPath);
            CollectionAssert.Contains(start.ArgumentList.ToArray(), context.KeyPath);
            CollectionAssert.Contains(start.ArgumentList.ToArray(), "--api.require-local-host-auth=true");
            CollectionAssert.Contains(start.ArgumentList.ToArray(), "--usb.retained-import-authority-id=4923336367393615921");
            CollectionAssert.Contains(start.ArgumentList.ToArray(), "--api.addr=127.0.0.1:3242");
            CollectionAssert.Contains(start.ArgumentList.ToArray(), "--usb.addr=127.0.0.1:3241");
            Assert.AreEqual("{}", File.ReadAllText(context.ConfigPath));
            using (var ordinaryReader = new FileStream(context.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.AreEqual((int)'{', ordinaryReader.ReadByte());
            Assert.ThrowsException<IOException>(() => File.WriteAllText(context.ConfigPath, "replacement"));
            Assert.ThrowsException<IOException>(() => File.Delete(context.ConfigPath));
            Assert.IsFalse(File.Exists(context.KeyPath), "Only VIIPER creates the real key; this fake host never runs VIIPER.");
            context.Start();
            Assert.AreEqual(1, host.Starts);
            Assert.IsTrue(context.InspectOwnedProcess(out bool running, out _));
            Assert.IsTrue(running);
        }
        finally { Environment.SetEnvironmentVariable("VIIPER_LOG_RAW_FILE", old); }
    }

    [TestMethod]
    public void ExistingChangedConfigIsNotOverwrittenAndNoChildStarts()
    {
        Open();
        Directory.CreateDirectory(context.DataPath);
        File.WriteAllText(context.ConfigPath, "{\"log.file\":\"elsewhere\"}");
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.AreEqual("{\"log.file\":\"elsewhere\"}", File.ReadAllText(context.ConfigPath));
        Assert.AreEqual(0, host.Starts);
    }

    [TestMethod]
    public void ExistingEmptyConfigIsNotSilentlyOverwritten()
    {
        Open();
        Directory.CreateDirectory(context.DataPath);
        File.WriteAllText(context.ConfigPath, "");
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.AreEqual(0L, new FileInfo(context.ConfigPath).Length);
        Assert.AreEqual(0, host.Starts);
    }

    [TestMethod]
    public void ConfigurationChangedBetweenCreationAndReadPinCannotLaunch()
    {
        int configInspections = 0;
        string configPath = Path.Combine(root, "portable-data", "VIIPER", "viiper.json");
        Open(pathCheck: path =>
        {
            PortableLabContext.ValidateNoReparsePoints(path);
            if (path == configPath && ++configInspections == 4)
                File.WriteAllText(configPath, "{\"changed\":true}");
        });
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.AreEqual("{\"changed\":true}", File.ReadAllText(configPath));
        Assert.AreEqual(0, host.Starts);
    }

    [TestMethod]
    public void ProcessAppearingAfterPreparationPreventsLaunch()
    {
        Open();
        host.Peers.Add(new(99, 100, Path.Combine(root, "other", "viiper.exe")));
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.AreEqual(0, host.Starts);
        Assert.AreEqual(0, host.Child.StopCalls);
    }

    [TestMethod]
    public void ForeignLaunchRaceStopsOnlyOurNewChild()
    {
        Open();
        host.AfterStart = () => host.Peers.Add(new(99, 100, Path.Combine(root, "other", "viiper.exe")));
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.AreEqual(1, host.Child.StopCalls);
        Assert.AreEqual(1, host.Peers.Count);
        Assert.AreEqual(99, host.Peers[0].ProcessId);
    }

    [TestMethod]
    public void FailedLaunchCannotFallBackOrRepeat()
    {
        Open();
        host.StartFailure = true;
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.AreEqual(1, host.Starts);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CleanupTerminatesOnlyTheStillIdenticalOwnedChild(bool sameIdentity)
    {
        Open().Start();
        host.Child.SameIdentity = sameIdentity;
        context.Dispose();
        context.Dispose();
        Assert.AreEqual(sameIdentity ? 1 : 0, host.Child.StopCalls);
        Assert.AreEqual(1, host.Child.DisposeCalls);
        if (sameIdentity) Assert.AreEqual(5000, host.Child.StopTimeout);
    }

    private void PrepareBorrowed()
    {
        Open().Start();
        string[] arguments = host.StartInfo.ArgumentList.ToArray();
        string keyPath = context.KeyPath;
        context.Dispose();
        File.WriteAllText(keyPath, "public fake key; never authenticates a real broker");
        host = new FakeHost(Path.Combine(root, "viiper.exe")) { Arguments = arguments };
        host.Peers.Add(new(501, 502, host.ImagePath));
        Open();
    }

    [TestMethod]
    public void ExactExistingPortableLaunchCanBeBorrowedButNeverStopped()
    {
        PrepareBorrowed();
        context.Start();
        Assert.AreEqual(0, host.Starts);
        Assert.AreEqual(1, host.ArgumentReads);
        Assert.IsTrue(context.InspectOwnedProcess(out bool running, out _));
        Assert.IsTrue(running);
        context.Dispose();
        Assert.AreEqual(0, host.Child.StopCalls);
        Assert.AreEqual(1, host.Peers.Count);
    }

    [DataTestMethod]
    [DataRow("key")]
    [DataRow("config-only")]
    [DataRow("authority")]
    [DataRow("unreadable")]
    [DataRow("pid-reused")]
    public void SameImageDoesNotAuthorizeUnknownOrChangedExistingLaunch(string change)
    {
        PrepareBorrowed();
        if (change == "key") File.WriteAllText(context.KeyPath, " ");
        if (change == "config-only") host.Arguments = host.Arguments.Where(value => value != "--config-only").ToArray();
        if (change == "authority") host.Arguments = host.Arguments.Where(value => !value.StartsWith("--usb.retained-import-authority-id=")).ToArray();
        if (change == "unreadable") host.ArgumentFailure = true;
        if (change == "pid-reused") host.AfterReadArguments = () => host.Peers[0] = host.Peers[0] with { StartTimeUtcTicks = 999 };
        Assert.ThrowsException<PortableBrokerStartupException>(() => context.Start());
        Assert.AreEqual(0, host.Starts);
        Assert.AreEqual(0, host.Child.StopCalls);
        Assert.AreEqual(1, host.Peers.Count);
    }

    private sealed class FakeHost : IPortableBrokerProcessHost
    {
        internal readonly string ImagePath;
        internal readonly List<PortableBrokerProcessIdentity> Peers = new();
        internal readonly FakeProcess Child;
        internal int Snapshots, Starts, ArgumentReads;
        internal bool SnapshotFailure, StartFailure, ArgumentFailure;
        internal Action AfterStart, AfterReadArguments;
        internal ProcessStartInfo StartInfo;
        internal string[] Arguments;
        internal FakeHost(string path) { ImagePath = path; Child = new FakeProcess(this); }
        public IReadOnlyList<PortableBrokerProcessIdentity> Snapshot()
        {
            Snapshots++;
            if (SnapshotFailure) throw new IOException("Unreadable owner");
            return Peers.ToArray();
        }
        public IReadOnlyList<string> ReadArguments(PortableBrokerProcessIdentity identity)
        {
            ArgumentReads++;
            if (ArgumentFailure) throw new IOException("Unreadable arguments");
            AfterReadArguments?.Invoke();
            return Arguments;
        }
        public IPortableBrokerProcess Start(ProcessStartInfo startInfo)
        {
            Starts++;
            if (StartFailure) throw new IOException("Injected launch failure");
            StartInfo = startInfo;
            Child.Running = true;
            Peers.Add(new(Child.ProcessId, Child.StartTimeUtcTicks, ImagePath));
            AfterStart?.Invoke();
            return Child;
        }
    }

    private sealed class FakeProcess : IPortableBrokerProcess
    {
        private readonly FakeHost host;
        internal bool Running, SameIdentity = true;
        internal int StopCalls, DisposeCalls, StopTimeout;
        internal FakeProcess(FakeHost host) { this.host = host; }
        public int ProcessId => 123;
        public long StartTimeUtcTicks => 456;
        public bool IsRunning => Running;
        public bool IdentityMatches => SameIdentity;
        public void StopAndWait(int timeoutMilliseconds)
        {
            StopCalls++;
            StopTimeout = timeoutMilliseconds;
            Running = false;
            host.Peers.RemoveAll(peer => peer.ProcessId == ProcessId && peer.StartTimeUtcTicks == StartTimeUtcTicks);
        }
        public void Dispose() => DisposeCalls++;
    }
}
