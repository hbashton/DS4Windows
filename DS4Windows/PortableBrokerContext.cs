using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DS4Windows;

internal sealed class PortableBrokerStartupException : Exception
{
    internal PortableBrokerStartupException(string message) : base(message) { }
    internal PortableBrokerStartupException(string message, Exception innerException) : base(message, innerException) { }
}

// Owns only the broker started by this normal portable package. It does not
// change profile storage, lab policy, installed credentials, tasks or drivers.
internal sealed class PortableBrokerContext : IDisposable
{
    internal const string MarkerFileName = "DS4Windows.portable";
    internal const string MarkerText = "DS4Windows portable package v1";
    private const string CloseOtherBroker = "Another VIIPER is running, or its ownership could not be verified. Use Install / Repair VIIPER in Settings to retry while DS4Windows stays open. An unidentified or inaccessible broker must be closed manually; no unverified process was stopped.";
    private const string StartFailure = "The portable VIIPER could not start safely. Use Install / Repair VIIPER in Settings to retry while DS4Windows stays open. Check that this extracted folder is writable and the supported USB/IP driver is installed. If Windows denies access to a broker, close that identified broker manually or use administrator access.";
    private readonly object gate = new();
    private readonly IPortableBrokerProcessHost host;
    private readonly Action<string> validatePath;
    private readonly FileStream backendPin;
    private FileStream configurationPin;
    private FileStream keyPin;
    private IPortableBrokerProcess owned;
    private PortableBrokerProcessIdentity? borrowed;
    private bool disposed, startFailed;
    private static PortableBrokerContext current;

    internal static PortableBrokerContext Current => current;
    internal static bool IsActive => current != null;
    internal string ViiperPath { get; }
    internal string KeyPath { get; }
    internal string DataPath { get; }
    internal string ConfigPath { get; }

    internal static string DescribeReadinessFailure(string probeFailure)
    {
        // ProbeServer returns only a fixed phase plus an exception type, never
        // exception messages, credentials or peer bytes. Keep that contract
        // explicit at the dialog boundary instead of dropping the useful cause.
        string detail = "No successful authenticated reply was received.";
        if (probeFailure != null && probeFailure.Length <= 128)
        {
            int separator = probeFailure.IndexOf(": ", StringComparison.Ordinal);
            if (separator > 0)
            {
                string phase = probeFailure[..separator];
                string reason = probeFailure[(separator + 2)..];
                bool knownPhase = phase is "Connect" or "CompleteConnect" or
                    "Authenticate" or "OpenStream" or "WritePing" or "ReadPing";
                bool safeReason = reason is "timeout" or "no valid VIIPER response" ||
                    (reason.EndsWith("Exception", StringComparison.Ordinal) &&
                     reason.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'));
                if (knownPhase && safeReason) detail = probeFailure;
            }
        }
        return "Portable VIIPER did not become ready.\n\nReadiness check: " + detail +
            "\n\nInclude this check in your bug report. Use a complete extracted portable package and USB/IP 0.9.7.7. " +
            "Use Install / Repair VIIPER in Settings to retry without closing DS4Windows. If a conflicting broker cannot be identified or accessed, close it manually first. Your key and profiles were not replaced.";
    }

    internal static void Initialize(string executableDirectory)
    {
        if (current != null)
            throw new PortableBrokerStartupException("The portable broker owner was already initialized. Reopen DS4Windows.");
        current = Create(executableDirectory, ViiperSetupManager.SupportedViiperSha256,
            new PortableBrokerProcessHost(), ReadManagedRoots);
    }

    internal static void InitializeUnavailable(string executableDirectory)
    {
        if (current != null)
            throw new PortableBrokerStartupException("The portable broker owner was already initialized.");
        current = CreateUnavailable(executableDirectory, new PortableBrokerProcessHost(), ReadManagedRoots);
    }

    internal static PortableBrokerContext CreateUnavailable(string directory,
        IPortableBrokerProcessHost processHost, Func<IEnumerable<string>> managedRoots)
    {
        string root = FindPortableRoot(directory, managedRoots)
            ?? throw new PortableBrokerStartupException("A portable repair placeholder requires a verified portable package.");
        return new(root, null, processHost, PortableLabContext.ValidateNoReparsePoints, pinBackend: false);
    }

    // Only the explicit repair coordinator may retire a verified borrowed
    // broker. Its caller has already drained all controller/output lifetimes.
    // Ordinary Dispose below deliberately remains owned-process-only.
    internal static void RetireCurrentForRepair(
        IReadOnlyList<PortableBrokerProcessIdentity> captured = null, Action stopCaptured = null)
    {
        PortableBrokerContext context = current;
        if (context == null) return;
        context.RetireForRepair(captured, stopCaptured);
        if (ReferenceEquals(current, context)) current = null;
    }

    internal void RetireForRepair(
        IReadOnlyList<PortableBrokerProcessIdentity> captured = null, Action stopCaptured = null)
    {
        lock (gate)
        {
            captured ??= PortableBrokerProcessHost.CaptureForRepair(host);
            IReadOnlyList<PortableBrokerProcessIdentity> peers =
                PortableBrokerProcessHost.ValidateCapturedForRepair(captured, host);
            var local = captured.Where(peer => string.Equals(Path.GetFullPath(peer.ExecutablePath), ViiperPath,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!disposed && backendPin != null && local.Length > 1)
                throw new PortableBrokerStartupException("The portable VIIPER repair target could not be identified. No unverified broker was stopped.");
            PortableBrokerProcessIdentity? selected = owned != null
                ? new(owned.ProcessId, owned.StartTimeUtcTicks, ViiperPath) : borrowed;
            if (!disposed && backendPin != null && local.Length == 1)
            {
                // A same-path newcomer is not the previously verified owner.
                // Do not silently authorize it just because repair was clicked.
                if (selected == null || local[0].ProcessId != selected.Value.ProcessId ||
                    local[0].StartTimeUtcTicks != selected.Value.StartTimeUtcTicks ||
                    !string.Equals(Path.GetFullPath(local[0].ExecutablePath),
                        Path.GetFullPath(selected.Value.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                    throw new PortableBrokerStartupException("The portable VIIPER owner changed. Close that broker and retry repair. No replacement process was stopped.");
            }
            // The coordinator may use a stop-only elevated helper for this
            // same frozen set. Pins and trusted-owner validation remain here;
            // no second path-based enumeration may adopt a newly started PID.
            if (stopCaptured != null) stopCaptured();
            else
            {
                var stopping = Stopwatch.StartNew();
                foreach (PortableBrokerProcessIdentity peer in peers.Where(peer =>
                             string.Equals(peer.ExecutablePath, ViiperPath, StringComparison.OrdinalIgnoreCase)))
                {
                    int remaining = 5_000 - (int)stopping.ElapsedMilliseconds;
                    if (remaining <= 0)
                        throw new PortableBrokerStartupException("Portable VIIPER retirement exceeded its deadline. Its pinned image was not released.");
                    host.StopForRepair(peer, remaining);
                }
            }
            IReadOnlyList<PortableBrokerProcessIdentity> after =
                PortableBrokerProcessHost.ValidateCapturedForRepair(captured, host);
            if (after.Any(peer => string.Equals(peer.ExecutablePath, ViiperPath, StringComparison.OrdinalIgnoreCase)))
                throw new PortableBrokerStartupException("The portable VIIPER did not exit. Its pinned image was not released or replaced.");
            if (disposed) return;
            // Retire only after verified exit. StopForRepair has the explicit
            // authority to stop a borrowed identity; Dispose does not.
            disposed = true;
            try { owned?.Dispose(); } catch { }
            owned = null;
            borrowed = null;
            configurationPin?.Dispose();
            configurationPin = null;
            keyPin?.Dispose();
            keyPin = null;
            backendPin?.Dispose();
        }
    }

    // The production entry always supplies the compiled release digest. Tests
    // inject only the process boundary, managed-root lookup and path inspection.
    internal static PortableBrokerContext Create(string directory, string expectedDigest,
        IPortableBrokerProcessHost processHost, Func<IEnumerable<string>> managedRoots,
        Action<string> inspectPath = null)
    {
        string root = FindPortableRoot(directory, managedRoots, inspectPath);
        if (root == null) return null;
        try
        {
            PortableBrokerContext context = new(root, expectedDigest, processHost,
                inspectPath ?? PortableLabContext.ValidateNoReparsePoints);
            try { context.FindCompatibleCandidate(); return context; }
            catch { context.Dispose(); throw; }
        }
        catch (PortableBrokerStartupException) { throw; }
        catch
        {
            throw new PortableBrokerStartupException("The portable package could not be verified. Extract it into a writable local folder outside Program Files, without links, and reopen DS4Windows.");
        }
    }

    // Resolve package identity without opening/pinning viiper.exe. Startup can
    // recover a missing or older bundled broker before constructing its owner.
    internal static string FindPortableRoot(string directory,
        Func<IEnumerable<string>> managedRoots = null, Action<string> inspectPath = null)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string marker = Path.Combine(root, MarkerFileName);
            FileAttributes markerAttributes;
            try { markerAttributes = File.GetAttributes(marker); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            // Existing DS4Updater releases copy the portable ZIP wholesale.
            // A marker copied into the registered managed installation is not
            // authority to change its broker/key/task policy, nor a reason to
            // brick installed startup. Do not read its contents or broker here.
            foreach (string managed in (managedRoots ?? ReadManagedRoots)())
                if (!string.IsNullOrWhiteSpace(managed) && AtOrBelow(root, NormalizeManagedRoot(managed)))
                    return null;
            Action<string> inspect = inspectPath ?? PortableLabContext.ValidateNoReparsePoints;
            inspect(marker);
            if ((markerAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                new FileInfo(marker).Length > 128 ||
                File.ReadAllText(marker).TrimEnd('\r', '\n') != MarkerText)
                throw new PortableBrokerStartupException("This portable package marker is invalid. Extract a fresh, complete DS4Windows portable package.");
            ValidateRoot(directory);
            return root;
        }
        catch (PortableBrokerStartupException) { throw; }
        catch
        {
            throw new PortableBrokerStartupException("The portable package could not be verified. Extract it into a writable local folder outside Program Files, without links, and reopen DS4Windows.");
        }
    }

    private PortableBrokerContext(string root, string expectedDigest,
        IPortableBrokerProcessHost processHost, Action<string> inspect, bool pinBackend = true)
    {
        host = processHost;
        validatePath = inspect;
        ViiperPath = Path.Combine(root, "viiper.exe");
        DataPath = Path.Combine(root, "portable-data", "VIIPER");
        KeyPath = Path.Combine(DataPath, "viiper.key.txt");
        ConfigPath = Path.Combine(DataPath, "viiper.json");
        ValidateLocalPaths();
        if (!pinBackend) return;
        backendPin = new FileStream(ViiperPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(backendPin)),
                    expectedDigest, StringComparison.OrdinalIgnoreCase))
                throw new PortableBrokerStartupException("The portable viiper.exe does not match this DS4Windows release. Extract a fresh, complete portable package; do not substitute another VIIPER build.");
        }
        catch { backendPin.Dispose(); throw; }
    }

    private static IEnumerable<string> ReadManagedRoots()
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey key = machine.OpenSubKey(@"SOFTWARE\DS4Windows", writable: false);
        object value = key?.GetValue("InstallPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value != null && value is not string)
            throw new InvalidDataException("The registered DS4Windows installation path is not a string.");
        return new[] { value as string };
    }

    private static bool AtOrBelow(string path, string root)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeManagedRoot(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("The registered managed installation path is not local and absolute.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (root == Path.GetPathRoot(root))
            throw new ArgumentException("A drive root is not a registered DS4Windows installation directory.");
        return root;
    }

    internal static string ValidateRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Portable packages require a local absolute directory.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (root == Path.GetPathRoot(root)) throw new ArgumentException("A drive root is not a portable package directory.");
        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows })
        {
            string protectedRoot = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(protectedRoot) && AtOrBelow(root, protectedRoot))
                throw new ArgumentException("Portable packages cannot use Windows or Program Files storage.");
        }
        PortableLabContext.ValidateNoReparsePoints(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        return root;
    }

    private PortableBrokerProcessIdentity? FindCompatibleCandidate()
    {
        try
        {
            IReadOnlyList<PortableBrokerProcessIdentity> peers = host.Snapshot();
            if (peers.Count == 0) return null;
            if (peers.Count != 1 || peers[0].ProcessId <= 0 || peers[0].StartTimeUtcTicks <= 0 ||
                !string.Equals(peers[0].ExecutablePath, ViiperPath, StringComparison.OrdinalIgnoreCase))
                throw new PortableBrokerStartupException(CloseOtherBroker);
            return peers[0];
        }
        catch { throw new PortableBrokerStartupException(CloseOtherBroker); }
    }

    private void ValidateLocalPaths()
    {
        validatePath(ViiperPath);
        validatePath(DataPath);
        validatePath(KeyPath);
        validatePath(ConfigPath);
    }

    internal bool IsVerifiedBackend(string path)
    {
        lock (gate)
            return !disposed && backendPin?.CanRead == true && string.Equals(path, ViiperPath, StringComparison.OrdinalIgnoreCase);
    }

    internal bool InspectOwnedProcess(out bool running, out string failure)
    {
        lock (gate) return InspectOwnedProcessCore(out running, out failure);
    }

    private bool InspectOwnedProcessCore(out bool running, out string failure)
    {
        running = false;
        failure = null;
        if (disposed || startFailed || backendPin == null) { failure = StartFailure; return false; }
        try
        {
            IReadOnlyList<PortableBrokerProcessIdentity> peers = host.Snapshot();
            if (borrowed is { } existing)
            {
                if (peers.Count == 1 && peers[0] == existing) { running = true; return true; }
                failure = CloseOtherBroker;
                return false;
            }
            if (owned == null)
            {
                if (peers.Count == 0) return true;
                failure = CloseOtherBroker;
                return false;
            }
            if (peers.Any(peer => !IsOwned(peer))) { failure = CloseOtherBroker; return false; }
            if (peers.Count != 1 || !owned.IsRunning || !owned.IdentityMatches)
            {
                failure = StartFailure;
                return false;
            }
            running = true;
            return true;
        }
        catch { failure = CloseOtherBroker; return false; }
    }

    private bool IsOwned(PortableBrokerProcessIdentity peer) =>
        peer.ProcessId == owned.ProcessId && peer.StartTimeUtcTicks == owned.StartTimeUtcTicks &&
        string.Equals(peer.ExecutablePath, ViiperPath, StringComparison.OrdinalIgnoreCase);

    internal void Start()
    {
        lock (gate)
        {
            if (disposed || startFailed) throw new PortableBrokerStartupException(StartFailure);
            if (backendPin == null)
                throw new PortableBrokerStartupException("Portable VIIPER is unavailable. Use Install/Repair VIIPER in Settings to restore the matching broker in this portable folder.");
            if (owned != null || borrowed != null)
            {
                if (InspectOwnedProcessCore(out bool running, out string failure) && running) return;
                throw new PortableBrokerStartupException(failure ?? StartFailure);
            }
            try
            {
                PortableBrokerProcessIdentity? candidate = FindCompatibleCandidate();
                ValidateLocalPaths();
                if (candidate == null) Directory.CreateDirectory(DataPath);
                ValidateLocalPaths();
                // Never retain write access in a sharing pin: ordinary readers
                // opening with FileShare.Read cannot coexist with that access.
                // Create only if absent, then validate the read-only opened file
                // again rather than trusting what was written before reopening.
                if (candidate == null)
                {
                    FileStream created = null;
                    try
                    {
                        created = new FileStream(ConfigPath, FileMode.CreateNew,
                            FileAccess.Write, FileShare.None);
                    }
                    catch (IOException) when (File.Exists(ConfigPath))
                    {
                        // A pre-existing/racing file is not ours to overwrite.
                        // Its exact contents and path still must pass below.
                    }
                    if (created != null)
                        using (created)
                        {
                            created.Write("{}"u8);
                            created.Flush(flushToDisk: true);
                        }
                }
                ValidateLocalPaths();
                configurationPin = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                ValidateLocalPaths();
                if (configurationPin.Length != 2 || configurationPin.ReadByte() != '{' || configurationPin.ReadByte() != '}')
                    throw new PortableBrokerStartupException("The generated portable VIIPER configuration was changed. Restore portable-data/VIIPER/viiper.json to an empty JSON object ({}) and reopen DS4Windows. Your key and profiles were not replaced.");
                PortableBrokerProcessIdentity? latest = FindCompatibleCandidate();
                if (latest != candidate) throw new PortableBrokerStartupException(CloseOtherBroker);
                if (candidate is { } existing)
                {
                    keyPin = new FileStream(KeyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (keyPin.Length is < 1 or > 4096)
                        throw new PortableBrokerStartupException(CloseOtherBroker);
                    using (var reader = new StreamReader(keyPin, Encoding.UTF8, true, 1024, leaveOpen: true))
                        if (string.IsNullOrWhiteSpace(reader.ReadToEnd()))
                            throw new PortableBrokerStartupException(CloseOtherBroker);
                    IReadOnlyList<string> arguments = host.ReadArguments(existing);
                    if (!arguments.SequenceEqual(BuildStartInfo().ArgumentList, StringComparer.Ordinal) ||
                        FindCompatibleCandidate() != existing)
                        throw new PortableBrokerStartupException(CloseOtherBroker);
                    borrowed = existing; // Readiness still requires the caller's authenticated ping.
                    return;
                }
                owned = host.Start(BuildStartInfo());
                if (owned == null) throw new PortableBrokerStartupException(StartFailure);
                if (!InspectOwnedProcessCore(out bool running, out string failure) || !running)
                    throw new PortableBrokerStartupException(failure ?? StartFailure);
            }
            catch (Exception error)
            {
                startFailed = true;
                StopOwnedProcess();
                configurationPin?.Dispose();
                configurationPin = null;
                keyPin?.Dispose();
                keyPin = null;
                throw error is PortableBrokerStartupException known ? known : new PortableBrokerStartupException(StartFailure);
            }
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var start = new ProcessStartInfo(ViiperPath)
        {
            WorkingDirectory = Path.GetDirectoryName(ViiperPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("VIIPER_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (string argument in new[]
        {
            "--config-only", "--config", ConfigPath, "--update-notify=none",
            "--log.level=warn", "--log.file=", "--log.raw-file=", "server",
            "--key-file", KeyPath, "--usb.addr=127.0.0.1:3241", "--api.addr=127.0.0.1:3242",
            "--api.require-local-host-auth=true", "--api.auto-attach-local-client=true",
            "--api.auto-attach-windows-native=true", "--usb.retained-import-authority-id=" +
                ViiperSetupManager.ViiperRetainedImportAuthorityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--usb.write-batch-flush-interval=0", "--usb.retained-input-service-ms=0",
            "--connection-timeout=30s", "--api.device-handler-connect-timeout=5s", "--usb.endpoint-diagnostics=false",
        }) start.ArgumentList.Add(argument);
        return start;
    }

    private void StopOwnedProcess()
    {
        if (owned == null) return;
        try
        {
            // A retained Process handle plus creation time prevents PID reuse
            // from turning cleanup into termination of someone else's broker.
            if (owned.IsRunning && owned.IdentityMatches) owned.StopAndWait(5_000);
        }
        catch { /* Never fall back to name/PID enumeration or foreign termination. */ }
        finally
        {
            try { owned.Dispose(); } catch { }
            owned = null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            StopOwnedProcess(); // Caller has already drained its controller/output lifetimes.
            try { configurationPin?.Dispose(); } catch { }
            try { keyPin?.Dispose(); } catch { }
            try { backendPin?.Dispose(); } catch { }
        }
    }
}

internal readonly record struct PortableBrokerProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);

internal interface IPortableBrokerProcessHost
{
    IReadOnlyList<PortableBrokerProcessIdentity> Snapshot();
    IReadOnlyList<string> ReadArguments(PortableBrokerProcessIdentity identity);
    IPortableBrokerProcess Start(ProcessStartInfo startInfo);
    void StopForRepair(PortableBrokerProcessIdentity identity, int timeoutMilliseconds) =>
        throw new NotSupportedException("This process host does not authorize explicit repair termination.");
}

internal interface IPortableBrokerProcess : IDisposable
{
    int ProcessId { get; }
    long StartTimeUtcTicks { get; }
    bool IsRunning { get; }
    bool IdentityMatches { get; }
    void StopAndWait(int timeoutMilliseconds);
}

internal sealed class PortableBrokerProcessHost : IPortableBrokerProcessHost
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public IReadOnlyList<string> ReadArguments(PortableBrokerProcessIdentity identity)
    {
        // WMI providers do not all honor enumeration timeouts during connect.
        // Bound the caller independently; this read-only query owns/disposes
        // its own resources even if a provider completes after that deadline.
        return Task.Run(() => ReadArgumentsCore(identity)).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    }

    private static IReadOnlyList<string> ReadArgumentsCore(PortableBrokerProcessIdentity identity)
    {
        // Bounded, read-only process metadata; no PEB reads/injection or access
        // to another process's environment. Explicit argv fixes all settings.
        using var query = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"),
            new ObjectQuery("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + identity.ProcessId),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), ReturnImmediately = true });
        using ManagementObjectCollection rows = query.Get();
        string commandLine = null;
        int count = 0;
        foreach (ManagementObject row in rows)
            using (row) { count++; commandLine = row["CommandLine"] as string; }
        if (count != 1 || string.IsNullOrWhiteSpace(commandLine) || commandLine.Length > 32_768)
            throw new PortableBrokerStartupException("VIIPER launch settings could not be verified. Close VIIPER, then reopen DS4Windows.");
        IntPtr arguments = CommandLineToArgvW(commandLine, out int argumentCount);
        if (arguments == IntPtr.Zero) throw new InvalidOperationException("Cannot read VIIPER arguments.");
        try
        {
            var result = new List<string>();
            for (int index = 1; index < argumentCount; index++)
                result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size)));
            return result;
        }
        finally { _ = LocalFree(arguments); }
    }

    public IReadOnlyList<PortableBrokerProcessIdentity> Snapshot()
    {
        var result = new List<PortableBrokerProcessIdentity>();
        Process[] processes = Process.GetProcessesByName("viiper");
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    result.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, ReadImagePath(process.Id)));
                }
                catch
                {
                    // Only a verified exit may disappear from the snapshot.
                    // An unreadable live owner must never look like no broker.
                    try { if (process.HasExited) continue; } catch { }
                    throw new PortableBrokerStartupException("VIIPER process ownership could not be read. Close VIIPER, then reopen DS4Windows.");
                }
            }
            return result;
        }
        finally { foreach (Process process in processes) process.Dispose(); }
    }

    private static string ReadImagePath(int processId)
    {
        const uint queryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(queryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Cannot inspect a VIIPER process.");
        try
        {
            var path = new StringBuilder(32_768);
            int length = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length) || length <= 0)
                throw new InvalidOperationException("Cannot inspect a VIIPER image.");
            return path.ToString(0, length);
        }
        finally { _ = CloseHandle(handle); }
    }

    public void StopForRepair(PortableBrokerProcessIdentity identity, int timeoutMilliseconds)
    {
        if (identity.ProcessId <= 0 || identity.StartTimeUtcTicks <= 0 ||
            !Path.IsPathFullyQualified(identity.ExecutablePath) || timeoutMilliseconds is < 1 or > 30_000)
            throw new ArgumentException("Explicit VIIPER repair requires a complete process identity and bounded timeout.");
        Process process;
        try { process = Process.GetProcessById(identity.ProcessId); }
        catch (ArgumentException) { return; } // The selected identity already exited.
        using (process)
        {
            IntPtr handle = process.Handle; // Keep this exact process object alive throughout stop/wait.
            if (process.HasExited) return;
            var path = new StringBuilder(32_768);
            int length = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length) || length <= 0 ||
                process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks ||
                !string.Equals(Path.GetFullPath(path.ToString(0, length)),
                    Path.GetFullPath(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                throw new PortableBrokerStartupException("The selected VIIPER process identity changed. No replacement process was stopped.");
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            if (!process.WaitForExit(timeoutMilliseconds))
                throw new PortableBrokerStartupException("The selected VIIPER process did not exit in time. Its image was not replaced.");
        }
    }

    internal static void StopSelectedForRepair(string path, IPortableBrokerProcessHost processHost = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), "viiper.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Explicit repair requires an absolute VIIPER image path.");
        string selectedPath = Path.GetFullPath(path);
        IPortableBrokerProcessHost host = processHost ?? new PortableBrokerProcessHost();
        static bool Unknown(PortableBrokerProcessIdentity peer) => peer.ProcessId <= 0 ||
            peer.StartTimeUtcTicks <= 0 || string.IsNullOrWhiteSpace(peer.ExecutablePath) ||
            !Path.IsPathFullyQualified(peer.ExecutablePath);
        bool Selected(PortableBrokerProcessIdentity peer) => string.Equals(
            Path.GetFullPath(peer.ExecutablePath), selectedPath, StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<PortableBrokerProcessIdentity> before = host.Snapshot();
        if (before.Count > 256 || before.Any(Unknown))
            throw new PortableBrokerStartupException("A VIIPER repair target could not be identified. No process was stopped.");
        var stopping = Stopwatch.StartNew();
        foreach (PortableBrokerProcessIdentity identity in before.Where(Selected))
        {
            int remaining = 5_000 - (int)stopping.ElapsedMilliseconds;
            if (remaining <= 0)
                throw new PortableBrokerStartupException("The selected VIIPER processes did not exit within the repair deadline. Their images were not replaced.");
            host.StopForRepair(identity, remaining);
        }
        IReadOnlyList<PortableBrokerProcessIdentity> after = host.Snapshot();
        if (after.Any(Unknown) || after.Any(Selected))
            throw new PortableBrokerStartupException("The selected VIIPER has not exited or its identity could not be verified. Its image was not replaced.");
    }

    // Explicit recovery authority is a frozen process identity set, not an
    // executable name wildcard. Image path/PID/start time identify what the
    // user authorized us to retire; they do not authenticate an old binary.
    // The replacement is independently pinned to the compiled release hash.
    internal static IReadOnlyList<PortableBrokerProcessIdentity> CaptureForRepair(
        IPortableBrokerProcessHost processHost = null)
    {
        RejectLabRepair();
        return Array.AsReadOnly(ValidateRepairIdentities(
            (processHost ?? new PortableBrokerProcessHost()).Snapshot()));
    }

    internal static void StopCapturedForRepair(
        IReadOnlyList<PortableBrokerProcessIdentity> captured,
        string preserveImagePath = null, IPortableBrokerProcessHost processHost = null)
    {
        RejectLabRepair();
        PortableBrokerProcessIdentity[] selected = ValidateRepairIdentities(captured);
        if (preserveImagePath != null)
            preserveImagePath = ValidateRepairImagePath(preserveImagePath);
        IPortableBrokerProcessHost host = processHost ?? new PortableBrokerProcessHost();
        IReadOnlyList<PortableBrokerProcessIdentity> before = ValidateCapturedForRepair(selected, host);
        bool WasCaptured(PortableBrokerProcessIdentity process) =>
            selected.Any(expected => SameRepairIdentity(expected, process));
        bool Preserved(PortableBrokerProcessIdentity process) => preserveImagePath != null &&
            string.Equals(process.ExecutablePath, preserveImagePath, StringComparison.OrdinalIgnoreCase);
        var stopping = Stopwatch.StartNew();
        foreach (PortableBrokerProcessIdentity identity in before.Where(process => !Preserved(process)))
        {
            int remaining = 5_000 - (int)stopping.ElapsedMilliseconds;
            if (remaining <= 0)
                throw new PortableBrokerStartupException("The captured VIIPER processes did not exit within the repair deadline. No replacement broker was started.");
            host.StopForRepair(identity, remaining);
        }
        PortableBrokerProcessIdentity[] after = ValidateRepairIdentities(host.Snapshot());
        if (after.Any(process => !WasCaptured(process) || !Preserved(process)))
            throw new PortableBrokerStartupException("VIIPER ownership changed or a captured broker did not exit. No newly discovered process was stopped; the replacement broker was not started.");
    }

    internal static IReadOnlyList<PortableBrokerProcessIdentity> ValidateCapturedForRepair(
        IReadOnlyList<PortableBrokerProcessIdentity> captured, IPortableBrokerProcessHost processHost = null)
    {
        RejectLabRepair();
        PortableBrokerProcessIdentity[] selected = ValidateRepairIdentities(captured);
        PortableBrokerProcessIdentity[] current = ValidateRepairIdentities(
            (processHost ?? new PortableBrokerProcessHost()).Snapshot());
        if (current.Any(process => !selected.Any(expected => SameRepairIdentity(expected, process))))
            throw new PortableBrokerStartupException("A new or changed VIIPER process appeared during repair. No newly discovered process was stopped; retry after checking the conflicting broker.");
        return Array.AsReadOnly(current);
    }

    private static void RejectLabRepair()
    {
        if (PortableLabContext.Requested || PortableLabContext.IsActive)
            throw new PortableBrokerStartupException("Portable-lab sessions do not stop or replace VIIPER brokers.");
    }

    private static PortableBrokerProcessIdentity[] ValidateRepairIdentities(
        IReadOnlyList<PortableBrokerProcessIdentity> identities)
    {
        if (identities == null || identities.Count > 256)
            throw new PortableBrokerStartupException("VIIPER process identities could not be safely captured. No unverified process was stopped.");
        PortableBrokerProcessIdentity[] result = identities.Select(identity =>
        {
            if (identity.ProcessId <= 0 || identity.StartTimeUtcTicks <= 0)
                throw new PortableBrokerStartupException("A VIIPER process identity is incomplete. No unverified process was stopped.");
            return identity with { ExecutablePath = ValidateRepairImagePath(identity.ExecutablePath) };
        }).ToArray();
        if (result.Length > 256 || result.Select(identity => identity.ProcessId).Distinct().Count() != result.Length)
            throw new PortableBrokerStartupException("VIIPER process identities were ambiguous. No unverified process was stopped.");
        return result;
    }

    private static string ValidateRepairImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(path), "viiper.exe", StringComparison.OrdinalIgnoreCase))
            throw new PortableBrokerStartupException("A VIIPER repair target is not an identified local viiper.exe. No unverified executable was stopped.");
        return Path.GetFullPath(path);
    }

    private static bool SameRepairIdentity(PortableBrokerProcessIdentity left, PortableBrokerProcessIdentity right) =>
        left.ProcessId == right.ProcessId && left.StartTimeUtcTicks == right.StartTimeUtcTicks &&
        string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    public IPortableBrokerProcess Start(ProcessStartInfo startInfo)
    {
        Process process = Process.Start(startInfo);
        if (process == null) return null;
        try { return new OwnedPortableBrokerProcess(process); }
        catch
        {
            // Process.Start supplied this exact handle; no name-based cleanup.
            try { if (!process.HasExited) { process.Kill(entireProcessTree: false); process.WaitForExit(5_000); } } catch { }
            process.Dispose();
            throw;
        }
    }

    private sealed class OwnedPortableBrokerProcess : IPortableBrokerProcess
    {
        private readonly Process process;
        public int ProcessId { get; }
        public long StartTimeUtcTicks { get; }
        internal OwnedPortableBrokerProcess(Process process)
        {
            this.process = process;
            _ = process.Handle; // Keep the actual process object alive until cleanup.
            ProcessId = process.Id;
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        }
        public bool IsRunning => !process.HasExited;
        public bool IdentityMatches => process.Id == ProcessId && process.StartTime.ToUniversalTime().Ticks == StartTimeUtcTicks;
        public void StopAndWait(int timeoutMilliseconds)
        {
            if (!IsRunning || !IdentityMatches) return;
            process.Kill(entireProcessTree: false);
            process.WaitForExit(timeoutMilliseconds);
        }
        public void Dispose() => process.Dispose();
    }
}
