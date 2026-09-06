using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DS4WinWPF;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests;

[TestClass]
[DoNotParallelize]
public class PortableLabContextTests
{
    private string root;
    private string digest;
    private PortableLabContext context;
    private object oldContext;
    private object oldRequested;
    private static readonly FieldInfo CurrentField = typeof(PortableLabContext)
        .GetField("current", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo RequestedField = typeof(PortableLabContext)
        .GetField("<Requested>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);

    [TestInitialize]
    public void Setup()
    {
        // Dedicated scratch directory in the test output, not shared AppData.
        root = Path.Combine(AppContext.BaseDirectory, "portable-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] binary = "test image; never executed"u8.ToArray();
        File.WriteAllBytes(Path.Combine(root, "viiper.exe"), binary);
        digest = Convert.ToHexString(SHA256.HashData(binary));
        oldContext = CurrentField.GetValue(null);
        oldRequested = RequestedField.GetValue(null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        CurrentField.SetValue(null, oldContext);
        RequestedField.SetValue(null, oldRequested);
        context?.Dispose();
        if (root != null && Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) &&
            Path.GetFileName(root).StartsWith("portable-policy-", StringComparison.Ordinal))
            Directory.Delete(root, true);
    }

    private PortableLabContext Open(bool activate = false)
    {
        context = PortableLabContext.Create(new[] { "--portable-lab", digest }, root);
        if (activate) CurrentField.SetValue(null, context);
        return context;
    }

    [TestMethod]
    public void OmittedOptionPreservesNormalLaunchAndDoesNotInspectLabFiles()
    {
        Assert.IsNull(PortableLabContext.Create(new[] { "-driverinstall" }, "not a path"));
    }

    [TestMethod]
    public void FixedPathsAndCallerHashAreReadOnlyAndPinBackendUntilExit()
    {
        Open();
        Assert.AreEqual(Path.Combine(root, "lab-data"), context.DataPath);
        Assert.AreEqual(Path.Combine(root, "lab-data", "viiper.key.txt"), context.KeyPath);
        Assert.IsFalse(Directory.Exists(context.DataPath));
        Assert.IsTrue(context.IsVerifiedBackend(Path.Combine(root, "viiper.exe")));
        Assert.IsFalse(context.IsVerifiedBackend(Path.Combine(root, "other", "viiper.exe")));
        Assert.ThrowsException<IOException>(() => File.WriteAllText(context.ViiperPath, "replace"));
        Assert.ThrowsException<IOException>(() => File.Delete(context.ViiperPath));
        context.Dispose();
        Assert.IsFalse(context.IsVerifiedBackend(context.ViiperPath));
        File.WriteAllText(context.ViiperPath, "replacement after disposal is allowed");
    }

    [DataTestMethod]
    [DataRow("-driverinstall")]
    [DataRow("-runtask")]
    [DataRow("-command")]
    [DataRow("-re-enabledevice")]
    [DataRow("--refresh-ds4windows-startup-task")]
    [DataRow("--install-viiper")]
    [DataRow("--unknown")]
    public void AnyMaintenanceOrUnknownArgumentRejectsBeforeReadingAnImage(string option)
    {
        Assert.ThrowsException<ArgumentException>(() => PortableLabContext.Create(
            new[] { option, "--portable-lab", digest }, "nonexistent"));
        Assert.ThrowsException<ArgumentException>(() => PortableLabContext.Create(
            new[] { "--portable-lab", digest, option }, "nonexistent"));
    }

    [TestMethod]
    public void IncompleteDuplicateAndMisspelledLabOptionsFailClosed()
    {
        foreach (string[] args in new[] {
            new[] { "--portable-lab" }, new[] { "--portable-lab=" + digest },
            new[] { "--Portable-Lab", digest }, new[] { "--portable-lab", "" },
            new[] { "--portable-lab", new string('z', 64) },
            new[] { "--portable-lab", digest, "--portable-lab", digest },
            new[] { "--portable-lab", digest, "-m", "-m" },
            new[] { "--portable-lab", digest, "-stop", "-stop" } })
            Assert.ThrowsException<ArgumentException>(() => PortableLabContext.Create(args, "nonexistent"));
        context = PortableLabContext.Create(new[] { "-m", "--portable-lab", digest.ToLowerInvariant(), "-stop" }, root);
        Assert.IsNotNull(context);
    }

    [TestMethod]
    public void WrongDigestNeverFallsBackToAdjacentSidecarOrProduction()
    {
        File.WriteAllText(Path.Combine(root, "viiper.exe.sha256"), digest);
        Assert.ThrowsException<InvalidDataException>(() => PortableLabContext.Create(
            new[] { "--portable-lab", new string('0', 64) }, root));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "lab-data")));
        File.WriteAllText(Path.Combine(root, "viiper.exe"), "failed validation releases its read pin");
    }

    [TestMethod]
    public void InstalledSharedRelativeAndBroadRootsReject()
    {
        foreach (string path in new[] {
            ".", @"C:relative", @"\\server\share\lab", Path.GetPathRoot(root),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lab"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lab"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lab") })
            Assert.ThrowsException<ArgumentException>(() => PortableLabContext.ValidateRoot(path));
    }

    [TestMethod]
    public void ConfigSelectionNeverConsultsRoamingOrAllowsMigration()
    {
        Open(activate: true);
        string oldPath = Global.appdatapath;
        bool oldFirstRun = Global.firstRun, oldMulti = Global.multisavespots;
        try
        {
            Global.FindConfigLocation();
            Assert.AreEqual(context.DataPath, Global.appdatapath);
            Assert.IsTrue(Global.firstRun);
            Assert.IsFalse(Global.multisavespots);
            Assert.IsFalse(Directory.Exists(context.DataPath));
            Assert.ThrowsException<InvalidOperationException>(() => Global.SaveWhere(Global.appDataPpath));
            Assert.AreEqual(context.DataPath, Global.appdatapath);
            Directory.CreateDirectory(context.DataPath);
            File.WriteAllText(Path.Combine(context.DataPath, "Auto Profiles.xml"), "<Programs />");
            Global.FindConfigLocation();
            Assert.IsFalse(Global.firstRun);
        }
        finally
        {
            CurrentField.SetValue(null, oldContext);
            Global.SaveWhere(oldPath);
            Global.firstRun = oldFirstRun;
            Global.multisavespots = oldMulti;
        }
    }

    [TestMethod]
    public void AuthenticationAndFailureLoggingStayLocalWithoutKeyFallback()
    {
        Open(activate: true);
        Assert.AreEqual(context.KeyPath, ViiperAuthentication.DefaultKeyFilePath);
        using MemoryStream transport = new();
        Assert.ThrowsException<IOException>(() => ViiperAuthentication.Authenticate(transport));
        Assert.AreEqual(0L, transport.Length, "No handshake can use the roaming deployment key.");
        string log = StartupFailureReporter.Write(new Exception("lab-only diagnostic"), "test", Global.appDataPpath);
        Assert.AreEqual(Path.Combine(context.DataPath, "Logs", "startup_failure.log"), log);
    }

    [TestMethod]
    public void InvalidLabLaunchHasNoSharedDiagnosticFallback()
    {
        Assert.ThrowsException<ArgumentException>(() => PortableLabContext.Initialize(new[] { "--portable-lab" }, root));
        Assert.IsTrue(PortableLabContext.Requested);
        Assert.AreEqual("", StartupFailureReporter.Write(new Exception("invalid lab"), "test"));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "lab-data")));
    }

    [TestMethod]
    public void SavedExclusiveSettingCannotTriggerDeviceRecoveryInLab()
    {
        bool old = Global.UseExclusiveMode;
        try
        {
            Global.UseExclusiveMode = true;
            Open(activate: true);
            Assert.IsFalse(Global.UseExclusiveMode);
            Assert.IsFalse(Global.getUseExclusiveMode());
            Assert.ThrowsException<InvalidOperationException>(() => DS4Devices.reEnableDevice("never-touch-a-device"));
            CurrentField.SetValue(null, oldContext);
            Assert.IsTrue(Global.UseExclusiveMode, "Lab policy must not rewrite the stored setting.");
        }
        finally { Global.UseExclusiveMode = old; }
    }

    [TestMethod]
    public void LostSingleInstanceCreationRaceNeitherSignalsNorAdmitsSecondMapper()
    {
        string name = "DS4Windows.PortableLab.Test." + Guid.NewGuid().ToString("N");
        using var first = App.CreateSingleAppComEvent(name, requireNew: true);
        Assert.IsNotNull(first);
        Assert.IsNull(App.CreateSingleAppComEvent(name, requireNew: true));
        Assert.IsFalse(first.WaitOne(0), "Rejected lab must not activate the existing owner.");
        using var normalSecond = App.CreateSingleAppComEvent(name, requireNew: false);
        Assert.IsNotNull(normalSecond, "Preserve ordinary second-instance behavior.");
    }

    [TestMethod]
    public void AuthenticationRetainsItsKeyWhenAnotherHandshakeRefreshesCache()
    {
        Open(activate: true);
        Directory.CreateDirectory(context.DataPath);
        const string oldPassword = "old-connection-test-password";
        const string newPassword = "replacement-connection-test-password";
        File.WriteAllText(context.KeyPath, oldPassword);

        using HandshakeWire firstWire = new();
        firstWire.BeforeFirstRead = () =>
        {
            // This is the same interleaving as a concurrent cache refresh:
            // the first handshake has sent its HMAC and awaits the server.
            // Different length guarantees cache invalidation without relying
            // on filesystem timestamp granularity or wall-clock sleeps.
            File.WriteAllText(context.KeyPath, newPassword);
            using HandshakeWire secondWire = new();
            using Stream second = ViiperAuthentication.Authenticate(secondWire);
            second.Write("second"u8);
            secondWire.AssertAuthenticatedPayload(newPassword, "second"u8.ToArray());
        };

        using Stream first = ViiperAuthentication.Authenticate(firstWire);
        first.Write("first"u8);
        firstWire.AssertAuthenticatedPayload(oldPassword, "first"u8.ToArray());
    }

    [TestMethod]
    public void FailedHandshakeDoesNotClearTheCachedKeyForItsSuccessor()
    {
        Open(activate: true);
        Directory.CreateDirectory(context.DataPath);
        const string password = "failed-connection-test-password";
        File.WriteAllText(context.KeyPath, password);
        using (HandshakeWire rejected = new(accepted: false))
            Assert.ThrowsException<IOException>(() =>
                ViiperAuthentication.Authenticate(rejected));

        using HandshakeWire successorWire = new();
        using Stream successor = ViiperAuthentication.Authenticate(successorWire);
        successor.Write("successor"u8);
        successorWire.AssertAuthenticatedPayload(password, "successor"u8.ToArray());
    }

    [DataTestMethod]
    [DataRow("success")]
    [DataRow("rejected")]
    [DataRow("write failure")]
    [DataRow("read failure")]
    [DataRow("truncated")]
    public void AuthenticationClearsHandshakeScratchOnEveryExit(string outcome)
    {
        Open(activate: true);
        Directory.CreateDirectory(context.DataPath);
        File.WriteAllText(context.KeyPath, "scratch-lifetime-test-password");
        using HandshakeWire wire = new(accepted: outcome != "rejected",
            truncated: outcome == "truncated")
        {
            FailWrite = outcome == "write failure",
            FailRead = outcome == "read failure",
        };

        if (outcome == "success")
        {
            using Stream authenticated = ViiperAuthentication.Authenticate(wire);
        }
        else if (outcome == "truncated")
            Assert.ThrowsException<EndOfStreamException>(() => ViiperAuthentication.Authenticate(wire));
        else
            Assert.ThrowsException<IOException>(() => ViiperAuthentication.Authenticate(wire));

        Assert.IsNotNull(wire.HandshakeBuffer);
        CollectionAssert.AreEqual(new byte[wire.HandshakeBuffer.Length], wire.HandshakeBuffer);
        if (wire.ResponseBuffer != null)
            CollectionAssert.AreEqual(new byte[wire.ResponseBuffer.Length], wire.ResponseBuffer);
    }

    private sealed class HandshakeWire : Stream
    {
        private readonly MemoryStream response;
        private readonly MemoryStream written = new();
        private readonly byte[] nonce = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        internal Action BeforeFirstRead { get; set; }
        internal bool FailWrite { get; set; }
        internal bool FailRead { get; set; }
        internal byte[] HandshakeBuffer { get; private set; }
        internal byte[] ResponseBuffer { get; private set; }

        internal HandshakeWire(bool accepted = true, bool truncated = false)
        {
            byte[] reply = (accepted ? "OK\0"u8.ToArray() :
                "NO\0"u8.ToArray()).Concat(nonce).ToArray();
            response = new MemoryStream(truncated ? reply[..4] : reply);
        }

        internal void AssertAuthenticatedPayload(string password, byte[] expected)
        {
            byte[] bytes = written.ToArray();
            Assert.IsTrue(bytes.Length > 69);
            CollectionAssert.AreEqual("eVI2\0"u8.ToArray(), bytes[..5]);
            byte[] key = ViiperAuthentication.DeriveKey(password);
            try
            {
                byte[] clientNonce = bytes[5..37];
                byte[] input = "VIIPER-Auth-v2"u8.ToArray().Concat(clientNonce).ToArray();
                CollectionAssert.AreEqual(HMACSHA256.HashData(key, input), bytes[37..69]);
                using MemoryStream encrypted = new(bytes[69..]);
                using ViiperEncryptedStream peer = new(encrypted,
                    ViiperAuthentication.DeriveSessionKey(key, nonce, clientNonce),
                    ViiperConnectionRole.Server);
                byte[] actual = new byte[expected.Length];
                peer.ReadExactly(actual);
                CollectionAssert.AreEqual(expected, actual);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ResponseBuffer = buffer;
            if (FailRead) throw new IOException("Synthetic handshake read failure");
            Action beforeRead = BeforeFirstRead;
            BeforeFirstRead = null;
            beforeRead?.Invoke();
            return response.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            HandshakeBuffer ??= buffer;
            if (FailWrite) throw new IOException("Synthetic handshake write failure");
            written.Write(buffer, offset, count);
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { response.Dispose(); written.Dispose(); }
            base.Dispose(disposing);
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LabReadinessRequiresAuthenticatedEncryptedPing(bool matchingKey)
    {
        Open(activate: true);
        Directory.CreateDirectory(context.DataPath);
        File.WriteAllText(context.KeyPath, "lab-test-password");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            peer.ReceiveTimeout = 2000;
            peer.SendTimeout = 2000;
            using NetworkStream wire = peer.GetStream();
            byte[] hello = new byte[69];
            wire.ReadExactly(hello);
            CollectionAssert.AreEqual("eVI2\0"u8.ToArray(), hello[..5]);
            byte[] clientNonce = hello[5..37];
            byte[] key = ViiperAuthentication.DeriveKey(matchingKey ? "lab-test-password" : "other-server-key");
            byte[] input = "VIIPER-Auth-v2"u8.ToArray().Concat(clientNonce).ToArray();
            bool authenticated = CryptographicOperations.FixedTimeEquals(
                HMACSHA256.HashData(key, input), hello[37..]);
            Assert.AreEqual(matchingKey, authenticated);
            if (!authenticated) return; // close, never downgrade to plaintext
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            wire.Write("OK\0"u8);
            wire.Write(nonce);
            using ViiperEncryptedStream encrypted = new(wire,
                ViiperAuthentication.DeriveSessionKey(key, nonce, clientNonce),
                ViiperConnectionRole.Server);
            byte[] command = new byte[5];
            encrypted.ReadExactly(command);
            CollectionAssert.AreEqual("ping\0"u8.ToArray(), command);
            byte[] reply = Encoding.ASCII.GetBytes("VIIPER lab test\0");
            encrypted.Write(reply, 0, reply.Length);
        });
        Assert.AreEqual(matchingKey, ViiperSetupManager.ProbeServer("127.0.0.1", port,
            authenticated: true, out string failure));
        if (matchingKey) Assert.IsNull(failure);
        else
        {
            StringAssert.StartsWith(failure, "Authenticate: ");
            Assert.IsFalse(failure.Contains("lab-test-password"));
            Assert.IsFalse(failure.Contains("other-server-key"));
            Assert.IsFalse(failure.Contains(context.KeyPath));
        }
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
