using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DS4Windows.Tests;

// No app, process, scheduled task, registry, driver, or controller is started.
// Runtime coverage uses an inert image, a fake process host, and loopback TCP.
[TestClass]
[DoNotParallelize]
public class PortableBrokerIntegrationTests
{
    [TestMethod]
    public void PortablePreflightFollowsHelpersAndLegacyArgumentsButPrecedesMutation()
    {
        string core = Section(Read("App.xaml.cs"),
            "private void ApplicationStartupCore(", "private void CancelPortableStartup(");
        const string initialize = "PortableBrokerContext.Initialize(";
        foreach (string earlier in new[]
        {
            "TryRunTaskRefreshHelper(", "TryRunStartupTaskRegistrationHelper(",
            "TryRunElevatedInstallerHost(", "TryRunForeignViiperTerminationHelper(",
            "DualSenseBluetoothAudioPacer.", "GameBarIntegration.TryRunProbeCommand(",
            "CheckOptions(parser);", "if (exitApp)",
        })
            Before(core, earlier, initialize);
        foreach (string later in new[]
        {
            "RetargetExistingTaskToCurrentExecutable();", "Global.FindConfigLocation();",
            "CreateControlService(parser);", "Global.Load();",
        })
            Before(core, initialize, later);
        StringAssert.Contains(core,
            "if (!DS4Windows.PortableBrokerContext.IsActive)\n                StartupMethods.RetargetExistingTaskToCurrentExecutable();");
    }

    [TestMethod]
    public void BrokerStartsOnlyAfterExclusiveMapperGateAndBeforeConfiguration()
    {
        string core = Section(Read("App.xaml.cs"),
            "private void ApplicationStartupCore(", "private void CancelPortableStartup(");
        Before(core, "CreateSingleAppComEvent(SingleAppComEventName,",
            "if (!StartPortableBroker()) return;");
        Before(core, "if (!StartPortableBroker()) return;", "CreateTempWorkerThread();");
        Before(core, "if (!StartPortableBroker()) return;", "Global.FindConfigLocation();");
        StringAssert.Contains(core,
            "requireNew: DS4Windows.PortableLabContext.IsActive ||\n                        DS4Windows.PortableBrokerContext.IsActive");
        // A second ordinary launch can activate its existing matching mapper;
        // the explicit development lab retains its no-signal policy.
        StringAssert.Contains(core,
            "if (!DS4Windows.PortableLabContext.IsActive)\n                        tempComEvent.Set();");
    }

    [TestMethod]
    public void StartupReadinessAuthenticatesAndRechecksProcessAfterTheReply()
    {
        string start = Section(Read("App.xaml.cs"),
            "private bool StartPortableBroker()", "private static void ShowStartupDialog(");
        Before(start, "portable.Start();", "ViiperSetupManager.ProbeServer(");
        StringAssert.Contains(start, "authenticated: true,");
        StringAssert.Contains(start, "totalTimeoutMilliseconds:");
        StringAssert.Contains(start,
            "portable.InspectOwnedProcess(out running, out _) && running)");
        Before(start, "ViiperSetupManager.ProbeServer(",
            "portable.InspectOwnedProcess(out running, out _)");
        Assert.IsFalse(start.Contains("TryStartServer(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeStatusKeepsSessionPathAndAuthenticationWithoutTaskOrServerFallback()
    {
        string status = Section(Read("DS4Control", "Viiper", "ViiperSetupManager.cs"),
            "public static ViiperPrerequisiteStatus GetStatus(",
            "public static bool EnsureReadyWithPrompt(");
        StringAssert.Contains(status,
            "lab?.ViiperPath ?? portable?.ViiperPath ?? ResolveRuntimeViiperPath(");
        StringAssert.Contains(status, "portable.IsVerifiedBackend(viiperPath)");
        StringAssert.Contains(status, "portable.InspectOwnedProcess(");
        StringAssert.Contains(status, "portable != null || !startupEnabled ||");
        StringAssert.Contains(status, "lab == null && portable == null && tryStartServer");
        StringAssert.Contains(status, "authenticated: lab != null || portable != null");
        Assert.IsFalse(status.Contains("PersistPreferredViiperPath(", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow("public static void RefreshSelectedStartupTaskOnLaunch()",
        "public static void RefreshSelectedStartupTaskAfterRunAtStartupChange()")]
    [DataRow("public static void RefreshSelectedStartupTaskAfterRunAtStartupChange()",
        "public static bool LaunchInstaller(")]
    [DataRow("private static bool TryStartServer(string viiperPath)",
        "private static bool CanPingServer(")]
    public void StartupTaskAndAutomaticLaunchEntryPointsGuardPortableOwnership(
        string begin, string end)
    {
        string method = Section(Read("DS4Control", "Viiper", "ViiperSetupManager.cs"),
            begin, end);
        StringAssert.Contains(method,
            "if (PortableLabContext.IsActive || PortableBrokerContext.IsActive) return");
        int guard = method.IndexOf("PortableBrokerContext.IsActive", StringComparison.Ordinal);
        foreach (string mutation in new[]
        {
            "PersistPreferredViiperPath(", "EnsureViiperStartupTask(",
            "RemoveViiperStartupTask(", "Process.Start(",
        })
        {
            int index = method.IndexOf(mutation, StringComparison.Ordinal);
            Assert.IsTrue(index < 0 || guard < index, mutation);
        }
    }

    [TestMethod]
    public void ForcedRepairPromptRemainsStatusOnlyAndPublicInstallerIsGuarded()
    {
        string source = Read("DS4Control", "Viiper", "ViiperSetupManager.cs");
        string prompt = Section(source, "public static bool EnsureReadyWithPrompt(",
            "if (PortableLabContext.IsActive)");
        StringAssert.Contains(prompt, "if (PortableBrokerContext.IsActive)");
        StringAssert.Contains(prompt, "if (!status.Ready || forcePrompt)");
        StringAssert.Contains(prompt, "return status.Ready;");
        Assert.IsFalse(prompt.Contains("LaunchInstaller(", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("new DS4WinWPF.DS4Forms.ViiperSetupPrompt",
            StringComparison.Ordinal));
        string launch = Section(source, "public static bool LaunchInstaller(",
            "status ??= GetStatus();");
        StringAssert.Contains(launch, "if (PortableBrokerContext.IsActive)");
        StringAssert.Contains(launch, "return false;");
    }

    [TestMethod]
    public void PortableKeyIsDistinctFromLabAndRoamingAndValidatesItsPath()
    {
        string source = Read("DS4Control", "Viiper", "ViiperAuthenticatedStream.cs");
        string selection = Section(source, "internal static string DefaultKeyFilePath",
            "internal static Stream Authenticate(");
        Before(selection, "PortableLabContext.Current?.KeyPath",
            "PortableBrokerContext.Current?.KeyPath");
        Before(selection, "PortableBrokerContext.Current?.KeyPath",
            "Environment.SpecialFolder.ApplicationData");
        string lookup = Section(source, "private static byte[] CopyDerivedDeploymentKey()",
            "private static void ReadExactly(");
        StringAssert.Contains(lookup,
            "if (PortableLabContext.IsActive || PortableBrokerContext.IsActive)");
        Before(lookup, "PortableLabContext.ValidateNoReparsePoints(path);",
            "FileInfo info = new(path);");
    }

    [TestMethod]
    public void BothOrdinaryAndForcedExitRetirePortableOwnershipAfterControllerDrain()
    {
        string source = Read("App.xaml.cs");
        string exit = Section(source, "private void Application_Exit(",
            "private void Application_SessionEnding(");
        Before(exit, "CleanShutdown();", "PortableBrokerContext.Current?.Dispose();");
        StringAssert.Contains(exit, "finally");
        string stop = Section(source, "private void CleanShutdown()", "Environment.Exit(0);");
        Before(stop, "shutdownHub.StopAndShutDown(immediateUnplug: true);",
            "PortableBrokerContext.Current?.Dispose();");
        StringAssert.Contains(stop, "if (shutdownTimedOut)");
    }

    [TestMethod]
    public void BothUpdateConfirmationPathsPrepareAndLaunchVerifiedUpdaterBeforeNormalShutdown()
    {
        string source = Read("DS4Forms", "MainWindow.xaml.cs");
        foreach ((string begin, string end) in new[]
        {
            ("private void DisplayUpdaterWindow(", "private bool CanStartPortableUpdate()"),
            ("private void Check_Version(", "private void TrayIconVM_RequestMinimize("),
        })
        {
            string entry = Section(source, begin, end);
            const string guard = "if (!CanStartPortableUpdate()) return;";
            Before(entry, "if (result == MessageBoxResult.Yes)", guard);
            Before(entry, guard, "mainWinVM.RunUpdaterCheck(");
            Before(entry, guard, "mainWinVM.LauchDS4Updater(");
            Before(entry, guard, "RequestApplicationShutdown();");
        }
        string guidance = Section(source, "private bool CanStartPortableUpdate()",
            "private void Check_Version(");
        StringAssert.Contains(guidance, "return !PortableLabContext.IsActive;");
        string preparation = Section(Read("DS4Forms", "ViewModels", "MainWindowsViewModel.cs"),
            "public bool RunUpdaterCheck(", "public void DownloadUpstreamVersionInfo()");
        Before(preparation, "if (PortableLabContext.IsActive) return false;", "PortableUpdaterBootstrap.PrepareAsync(");
        Before(preparation, "PortableUpdaterBootstrap.PrepareAsync(", "DownloadUpstreamUpdaterVersion()");
        StringAssert.Contains(preparation, "LastUpdaterFailure");
        Assert.IsFalse(guidance.Contains("UsesBorrowedBroker", StringComparison.Ordinal));
        Assert.IsFalse(guidance.Contains(".Dispose(", StringComparison.Ordinal));
        Assert.IsFalse(guidance.Contains(".Kill(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DirectPortableUpdaterLaunchRequiresPreparedProtocolAndNeverUsesLegacyProcessPath()
    {
        string launch = Section(Read("DS4Forms", "ViewModels", "MainWindowsViewModel.cs"),
            "public bool LauchDS4Updater(", "public bool IsNET8Available()");
        StringAssert.Contains(System.Text.RegularExpressions.Regex.Replace(launch, @"\s+", " "),
            "if (PortableLabContext.IsActive) return false;");
        StringAssert.Contains(launch, "return PortableUpdaterBootstrap.Launch(preparedPortableUpdater,");
        StringAssert.Contains(launch, "finally { preparedPortableUpdater = null; }");
        Before(launch, "PortableBrokerContext.IsActive",
            "new Process()");
        Before(launch, "PortableBrokerContext.IsActive",
            "p.Start()");
        Assert.IsFalse(launch.Contains("UsesBorrowedBroker", StringComparison.Ordinal));
        // Only ordinary, unmarked managed updates retain the existing path.
        StringAssert.Contains(launch, "argList.Add(\"-autolaunch\");");
        StringAssert.Contains(launch, "argList.Add(\"--releaseTag\");");
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TotalProbeDeadlineClosesTrickledAuthenticationOrEncryptedPing(bool duringAuthentication)
    {
        using ProbeFixture fixture = new();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using ManualResetEventSlim reachedPhase = new();
        using ManualResetEventSlim peerClosed = new();
        byte[] key = ViiperAuthentication.DeriveKey(ProbeFixture.Password);
        Task server = Task.Factory.StartNew(() =>
        {
            using TcpClient peer = listener.AcceptTcpClient();
            peer.ReceiveTimeout = 4000;
            peer.SendTimeout = 4000;
            using NetworkStream wire = peer.GetStream();
            byte[] hello = new byte[69];
            wire.ReadExactly(hello);
            CollectionAssert.AreEqual("eVI2\0"u8.ToArray(), hello[..5]);
            byte[] authInput = "VIIPER-Auth-v2"u8.ToArray().Concat(hello[5..37]).ToArray();
            CollectionAssert.AreEqual(HMACSHA256.HashData(key, authInput), hello[37..]);
            byte[] nonce = Enumerable.Repeat((byte)0x5a, 32).ToArray();
            byte[] reply = "OK\0"u8.ToArray().Concat(nonce).ToArray();
            using ViiperEncryptedStream encrypted = duringAuthentication ? null : new(wire,
                ViiperAuthentication.DeriveSessionKey(key, nonce, hello[5..37]),
                ViiperConnectionRole.Server);
            if (!duringAuthentication)
            {
                wire.Write(reply);
                byte[] ping = new byte[5];
                encrypted.ReadExactly(ping);
                CollectionAssert.AreEqual("ping\0"u8.ToArray(), ping);
                // Produce a valid first server record, then trickle its bytes.
                using MemoryStream captured = new();
                using ViiperEncryptedStream encoder = new(captured,
                    ViiperAuthentication.DeriveSessionKey(key, nonce, hello[5..37]),
                    ViiperConnectionRole.Server);
                encoder.Write("VIIPER deadline fixture\0"u8);
                reply = captured.ToArray();
            }
            reachedPhase.Set();
            try
            {
                foreach (byte value in reply)
                {
                    wire.WriteByte(value);
                    Thread.Sleep(75);
                }
                while (wire.ReadByte() >= 0) { }
                peerClosed.Set();
            }
            catch (IOException) { peerClosed.Set(); }
            catch (SocketException) { peerClosed.Set(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            bool ready = ViiperSetupManager.ProbeServer("127.0.0.1",
                ((IPEndPoint)listener.LocalEndpoint).Port, authenticated: true,
                out string failure, totalTimeoutMilliseconds: 500);
            elapsed.Stop();
            Assert.IsFalse(ready);
            Assert.IsTrue(reachedPhase.IsSet, "The real handshake reached the intended stall.");
            StringAssert.StartsWith(failure, duringAuthentication ? "Authenticate:" : "ReadPing:");
            Assert.IsTrue(elapsed.ElapsedMilliseconds < 1800,
                $"The 500 ms whole-probe deadline took {elapsed.ElapsedMilliseconds} ms.");
            Assert.IsTrue(peerClosed.Wait(3000), "The exact connection was closed, not abandoned.");
            Assert.IsTrue(server.Wait(5000), "No loopback peer remains after the probe.");
            Assert.IsFalse(failure.Contains(ProbeFixture.Password, StringComparison.Ordinal));
            Assert.IsFalse(failure.Contains(fixture.Context.KeyPath, StringComparison.Ordinal));
        }
        finally
        {
            listener.Stop();
            try { server.Wait(5000); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    private sealed class ProbeFixture : IDisposable
    {
        internal const string Password = "portable-loopback-fixture-only";
        private static readonly FieldInfo CurrentField = typeof(PortableBrokerContext)
            .GetField("current", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly object oldCurrent;
        private readonly string root;
        internal PortableBrokerContext Context { get; }

        internal ProbeFixture()
        {
            root = Path.Combine(AppContext.BaseDirectory,
                "portable-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            byte[] image = "inert test image; never executed"u8.ToArray();
            File.WriteAllBytes(Path.Combine(root, "viiper.exe"), image);
            File.WriteAllText(Path.Combine(root, PortableBrokerContext.MarkerFileName),
                PortableBrokerContext.MarkerText);
            Context = PortableBrokerContext.Create(root,
                Convert.ToHexString(SHA256.HashData(image)),
                new InertHost(), () => Array.Empty<string>());
            Directory.CreateDirectory(Context.DataPath);
            File.WriteAllText(Context.KeyPath, Password);
            oldCurrent = CurrentField.GetValue(null);
            CurrentField.SetValue(null, Context);
            Assert.AreEqual(Context.KeyPath, ViiperAuthentication.DefaultKeyFilePath);
        }

        public void Dispose()
        {
            CurrentField.SetValue(null, oldCurrent);
            Context.Dispose();
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
                && Path.GetFileName(root).StartsWith("portable-probe-", StringComparison.Ordinal))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InertHost : IPortableBrokerProcessHost
    {
        public IReadOnlyList<PortableBrokerProcessIdentity> Snapshot() =>
            Array.Empty<PortableBrokerProcessIdentity>();
        public IReadOnlyList<string> ReadArguments(PortableBrokerProcessIdentity identity) =>
            throw new AssertFailedException("No real process metadata is allowed.");
        public IPortableBrokerProcess Start(ProcessStartInfo startInfo) =>
            throw new AssertFailedException("No process may start in an integration probe.");
    }

    private static string Read(params string[] parts)
    {
        for (DirectoryInfo directory = new(AppContext.BaseDirectory);
             directory != null; directory = directory.Parent)
        {
            string path = Path.Combine(new[] { directory.FullName, "DS4Windows" }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path).Replace("\r\n", "\n");
        }
        throw new AssertFailedException("Repository source file was not found: " + Path.Combine(parts));
    }

    private static string Section(string source, string begin, string end)
    {
        int start = source.IndexOf(begin, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, begin);
        int finish = source.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
        Assert.IsTrue(finish > start, end);
        return source[start..finish];
    }

    private static void Before(string source, string earlier, string later)
    {
        int first = source.IndexOf(earlier, StringComparison.Ordinal);
        int second = source.IndexOf(later, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0 && second > first, earlier + " must precede " + later);
    }
}
