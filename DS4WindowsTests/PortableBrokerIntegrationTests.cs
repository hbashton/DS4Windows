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
    public TestContext TestContext { get; set; }

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
        StringAssert.Contains(core, "requireNew: DS4Windows.PortableLabContext.IsActive ||");
        Before(core, "AcquirePortableRepairStartupGate()", "PortableBrokerMaintenance.EnsureStartupPayload(");
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
            "portable.InspectOwnedProcess(out running, out _) && running;");
        Before(start, "ViiperSetupManager.ProbeServer(",
            "portable.InspectOwnedProcess(out running, out _)");
        Assert.IsFalse(start.Contains("TryStartServer(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FailedReadinessRetainsSafeCauseAndRetiresOwnedChildBeforeShowingTheDialog()
    {
        string start = Section(Read("App.xaml.cs"),
            "private bool StartPortableBroker()", "private static void ShowStartupDialog(");
        StringAssert.Contains(start, "out lastProbeFailure, totalTimeoutMilliseconds:");
        StringAssert.Contains(start, "DescribeReadinessFailure(lastProbeFailure)");
        Before(start, "portable.Dispose();", "MessageBox.Show(message");
        StringAssert.Contains(start, "catch (DS4Windows.PortableBrokerStartupException retirementFailure)");
        Assert.IsFalse(start.Contains("CancelPortableStartup(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StartupBorrowFailureKeepsOneAutomaticRecoveryButMaintenanceDoesNotChaseNewcomers()
    {
        string app = Read("App.xaml.cs");
        string selection = Section(app, "bool startupMaintenanceAttempted = true;",
            "// Preserve legacy startup retargeting");
        StringAssert.Contains(selection,
            "startupMaintenanceAttempted = DS4Windows.PortableBrokerMaintenance.EnsureStartupPayload(");
        const string guarded = "if (startupMaintenanceAttempted) DS4Windows.ViiperRecovery.TryBeginAutomaticRecovery();";
        Assert.AreEqual(2, selection.Split(new[] { guarded }, StringSplitOptions.None).Length - 1);
        Before(selection, guarded, "PortableBrokerContext.Initialize(");
        string startup = Section(app, "private bool StartPortableBroker()", "private static void ShowStartupDialog(");
        Assert.IsFalse(startup.Contains("TryBeginAutomaticRecovery", StringComparison.Ordinal),
            "A preserved same-path argv/start failure must allow the later guarded recovery attempt.");
        string maintenance = Read("PortableBrokerMaintenance.cs");
        StringAssert.Contains(maintenance, "internal static bool EnsureStartupPayload(");
        StringAssert.Contains(maintenance, "if (!replace && !conflicting) return false;");
        Before(maintenance, "PortableRepairProgress.Run<object>", "return true;");
    }

    [DataTestMethod]
    [DataRow("Connect: SocketException")]
    [DataRow("Connect: timeout")]
    [DataRow("Authenticate: PlatformNotSupportedException")]
    [DataRow("Authenticate: UnauthorizedAccessException")]
    [DataRow("ReadPing: AuthenticationTagMismatchException")]
    [DataRow("ReadPing: no valid VIIPER response")]
    public void ReadinessMessageRetainsOnlyTheProbePhaseAndType(string failure)
    {
        string message = PortableBrokerContext.DescribeReadinessFailure(failure);
        StringAssert.Contains(message, "Readiness check: " + failure);
        StringAssert.Contains(message, "Your key and profiles were not replaced.");
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Authenticate: token-secret-C:\\user\\viiper.key.txt")]
    [DataRow("Authenticate: IOException\r\npeer data")]
    [DataRow("Unknown: AuthenticationException")]
    [DataRow("ReadPing: <VIIPER peer bytes>")]
    public void ReadinessMessageDoesNotEchoUnexpectedDiagnosticContent(string failure)
    {
        string message = PortableBrokerContext.DescribeReadinessFailure(failure);
        StringAssert.Contains(message, "No successful authenticated reply was received.");
        if (!string.IsNullOrEmpty(failure))
            Assert.IsFalse(message.Contains(failure, StringComparison.Ordinal));
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
        StringAssert.Contains(status, "portable != null || !startupRequested ||");
        StringAssert.Contains(status, "StartupMethods.IsRunAtStartupRequested()");
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
    public void ForcedPortableRepairUsesLocalMaintenanceAndPublicInstallerIsGuarded()
    {
        string source = Read("DS4Control", "Viiper", "ViiperSetupManager.cs");
        string prompt = Section(source, "public static bool EnsureReadyWithPrompt(",
            "if (PortableLabContext.IsActive)");
        StringAssert.Contains(prompt, "if (PortableBrokerContext.IsActive)");
        StringAssert.Contains(prompt, "if (!status.Ready || forcePrompt)");
        StringAssert.Contains(prompt, "if (!ViiperRecovery.Repair(owner)) return false;");
        Before(prompt, "if (ViiperRecovery.RepairRequired && !PortableLabContext.IsActive &&",
            "if (forcePrompt && status.Ready)");
        StringAssert.Contains(prompt, "!PortableLabContext.IsActive && runtimePrerequisitesReady &&");
        StringAssert.Contains(prompt, "runtimePrerequisitesReady && !managedInstallationMissing) return false;");
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
        Before(exit, "CleanShutdown();", "DisposePortableBrokerForShutdown();");
        StringAssert.Contains(exit, "finally");
        string stop = Section(source, "private void CleanShutdown()", "Environment.Exit(0);");
        Before(stop, "shutdownHub.StopAndShutDown(immediateUnplug: true);",
            "DisposePortableBrokerForShutdown();");
        StringAssert.Contains(stop, "if (shutdownTimedOut)");
        string cleanup = source[source.IndexOf("private static void DisposePortableBrokerForShutdown()", StringComparison.Ordinal)..];
        StringAssert.Contains(cleanup, "PortableBrokerContext.Current?.Dispose();");
        StringAssert.Contains(cleanup, "catch (DS4Windows.PortableBrokerStartupException error)");
        StringAssert.Contains(cleanup, "Logger?.Warn");
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
            Before(entry, "mainWinVM.UpdaterRequiresApplicationShutdown", "RequestApplicationShutdown();");
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
        StringAssert.Contains(launch, "finally { preparedPortableUpdater = null; preparedManagedUpdater = null; }");
        Before(launch, "PortableBrokerContext.IsActive",
            "new Process()");
        Before(launch, "PortableBrokerContext.IsActive",
            "p.Start()");
        Assert.IsFalse(launch.Contains("UsesBorrowedBroker", StringComparison.Ordinal));
        // Only ordinary, unmarked managed updates retain the existing path.
        StringAssert.Contains(launch, "argList.Add(\"-autolaunch\");");
        StringAssert.Contains(launch, "argList.Add(\"--releaseTag\");");
        Before(launch, "ManagedUpdaterBootstrap.Launch(", "new Process()");
        StringAssert.Contains(launch, "PortableBrokerContext.FindPortableRoot(Global.exedirpath)");
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TotalProbeDeadlineClosesTrickledAuthenticationOrEncryptedPing(bool duringAuthentication)
    {
        using ProbeFixture fixture = new();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using ManualResetEventSlim measuredServerReady = new();
        using ManualResetEventSlim reachedPhase = new();
        using ManualResetEventSlim peerClosed = new();
        byte[] key = ViiperAuthentication.DeriveKey(ProbeFixture.Password);
        string serverPhase = "WarmupAccept";
        Task server = Task.Factory.StartNew(() =>
        {
            // Exercise the real key-file cache, authenticated handshake and both
            // encrypted-record directions before measuring the trickle deadline.
            // A fresh fixture path otherwise puts cold PBKDF/cipher/JIT work and
            // dedicated-thread startup inside the unrelated 500 ms stall budget.
            using (TcpClient warmPeer = listener.AcceptTcpClient())
            {
                warmPeer.ReceiveTimeout = 4000;
                warmPeer.SendTimeout = 4000;
                using NetworkStream warmWire = warmPeer.GetStream();
                Volatile.Write(ref serverPhase, "WarmupAuthenticate");
                byte[] warmHello = ReadProbeHello(warmWire, key);
                byte[] warmNonce = Enumerable.Repeat((byte)0x5a, 32).ToArray();
                using ViiperEncryptedStream warmEncrypted = new(warmWire,
                    ViiperAuthentication.DeriveSessionKey(key, warmNonce, warmHello[5..37]),
                    ViiperConnectionRole.Server);
                warmWire.Write("OK\0"u8.ToArray().Concat(warmNonce).ToArray());
                Volatile.Write(ref serverPhase, "WarmupReadPing");
                ReadProbePing(warmEncrypted);
                warmEncrypted.Write("VIIPER warmup fixture\0"u8);
            }

            Volatile.Write(ref serverPhase, "MeasuredAccept");
            measuredServerReady.Set();
            using TcpClient peer = listener.AcceptTcpClient();
            peer.ReceiveTimeout = 4000;
            peer.SendTimeout = 4000;
            using NetworkStream wire = peer.GetStream();
            Volatile.Write(ref serverPhase, "MeasuredAuthenticate");
            byte[] hello = ReadProbeHello(wire, key);
            byte[] nonce = Enumerable.Repeat((byte)0x5a, 32).ToArray();
            byte[] reply = "OK\0"u8.ToArray().Concat(nonce).ToArray();
            using ViiperEncryptedStream encrypted = duringAuthentication ? null : new(wire,
                ViiperAuthentication.DeriveSessionKey(key, nonce, hello[5..37]),
                ViiperConnectionRole.Server);
            if (!duringAuthentication)
            {
                wire.Write(reply);
                Volatile.Write(ref serverPhase, "MeasuredReadPing");
                ReadProbePing(encrypted);
                // Produce a valid first server record, then trickle its bytes.
                using MemoryStream captured = new();
                using ViiperEncryptedStream encoder = new(captured,
                    ViiperAuthentication.DeriveSessionKey(key, nonce, hello[5..37]),
                    ViiperConnectionRole.Server);
                encoder.Write("VIIPER deadline fixture\0"u8);
                reply = captured.ToArray();
            }
            Volatile.Write(ref serverPhase, duringAuthentication ? "TrickleAuthenticate" : "TrickleReadPing");
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
        Exception primaryFailure = null;
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            bool warmReady = ViiperSetupManager.ProbeServer("127.0.0.1", port,
                authenticated: true, out string warmFailure, totalTimeoutMilliseconds: 3000);
            Assert.IsTrue(warmReady,
                $"The positive authenticated warmup failed: {warmFailure}; server={Volatile.Read(ref serverPhase)}.");
            Assert.IsTrue(measuredServerReady.Wait(5000),
                $"The same server thread was not ready for the measured connection; server={Volatile.Read(ref serverPhase)}.");
            Stopwatch elapsed = Stopwatch.StartNew();
            bool ready = ViiperSetupManager.ProbeServer("127.0.0.1", port, authenticated: true,
                out string failure, totalTimeoutMilliseconds: 500);
            elapsed.Stop();
            string phaseEvidence = $"probe={failure}; server={Volatile.Read(ref serverPhase)}; elapsed={elapsed.ElapsedMilliseconds} ms";
            TestContext?.WriteLine(phaseEvidence);
            Assert.IsFalse(ready);
            Assert.IsTrue(reachedPhase.IsSet, "The real handshake must reach the intended stall: " + phaseEvidence);
            StringAssert.StartsWith(failure, duringAuthentication ? "Authenticate:" : "ReadPing:");
            Assert.IsTrue(elapsed.ElapsedMilliseconds < 1800,
                $"The 500 ms whole-probe deadline took {elapsed.ElapsedMilliseconds} ms.");
            Assert.IsTrue(peerClosed.Wait(3000), "The exact connection was closed, not abandoned.");
            Assert.IsTrue(server.Wait(5000), "No loopback peer remains after the probe.");
            Assert.IsFalse(failure.Contains(ProbeFixture.Password, StringComparison.Ordinal));
            Assert.IsFalse(failure.Contains(fixture.Context.KeyPath, StringComparison.Ordinal));
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            listener.Stop();
            try
            {
                bool stopped = server.Wait(5000);
                if (!stopped)
                {
                    const string message = "The loopback server did not retire during bounded cleanup.";
                    if (primaryFailure == null) Assert.Fail(message);
                    TestContext?.WriteLine(message);
                    primaryFailure.Data["ProbeServerCleanup"] = message;
                }
            }
            catch (AggregateException cleanupFailure) when (primaryFailure != null)
            {
                // Preserve the original phase/assertion failure; do not turn an
                // early handshake error into an apparently unrelated Wait fault.
                primaryFailure.Data["ProbeServerCleanup"] = cleanupFailure.ToString();
                TestContext?.WriteLine($"Server cleanup after {Volatile.Read(ref serverPhase)}: {cleanupFailure}");
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    [TestMethod]
    public void RepairedManagedLaunchRechecksDriverSafetyBeforeProcessStart()
    {
        string source = Read("DS4Control", "Viiper", "ViiperSetupManager.cs");
        string method = Section(source, "internal static bool TryStartRepairedServer(",
            "private static bool TryStartServer(");
        Before(method, "HasSafeRuntimePrerequisites(GetStatus())", "TryStartServer(viiperPath)");
        string recovery = Read("DS4Control", "Viiper", "ViiperRecovery.cs");
        Before(recovery, "HasSafeRuntimePrerequisites(ViiperSetupManager.GetStatus())", "PortableRepairProgress.Run");
    }

    [TestMethod]
    public void PortableRecoveryPrecedesBackendPinAndNeverMigrates()
    {
        string app = Read("App.xaml.cs");
        Before(app, "PortableBrokerMaintenance.EnsureStartupPayload(", "PortableBrokerContext.Initialize(");
        string maintenance = Read("PortableBrokerMaintenance.cs");
        StringAssert.Contains(maintenance, "if (PortableLabContext.IsActive) return false;");
        StringAssert.Contains(maintenance, "PortableBrokerRepair.RepairAsync(root,");
        Assert.IsFalse(maintenance.Contains("LaunchInstaller(", StringComparison.Ordinal));
        Assert.IsFalse(maintenance.Contains("RetargetExistingTask", StringComparison.Ordinal));
        Assert.IsFalse(maintenance.Contains("Process.Kill", StringComparison.Ordinal));
        Assert.IsFalse(maintenance.Contains("Application.Current.Shutdown();", StringComparison.Ordinal));
    }

    private static byte[] ReadProbeHello(NetworkStream wire, byte[] key)
    {
        byte[] hello = new byte[69];
        wire.ReadExactly(hello);
        CollectionAssert.AreEqual("eVI2\0"u8.ToArray(), hello[..5]);
        byte[] authInput = "VIIPER-Auth-v2"u8.ToArray().Concat(hello[5..37]).ToArray();
        CollectionAssert.AreEqual(HMACSHA256.HashData(key, authInput), hello[37..]);
        return hello;
    }

    private static void ReadProbePing(ViiperEncryptedStream encrypted)
    {
        byte[] ping = new byte[5];
        encrypted.ReadExactly(ping);
        CollectionAssert.AreEqual("ping\0"u8.ToArray(), ping);
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
