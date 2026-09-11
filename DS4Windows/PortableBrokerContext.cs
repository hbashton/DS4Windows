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
}

// Owns only the broker started by this normal portable package. It does not
// change profile storage, lab policy, installed credentials, tasks or drivers.
internal sealed class PortableBrokerContext : IDisposable
{
    internal const string MarkerFileName = "DS4Windows.portable";
    internal const string MarkerText = "DS4Windows portable package v1";
    private const string CloseOtherBroker = "Another VIIPER is running, or its ownership could not be verified. Close VIIPER, then reopen this portable DS4Windows. No existing broker was stopped.";
    private const string StartFailure = "The portable VIIPER could not start safely. Close VIIPER and reopen DS4Windows. Check that this extracted folder is writable and the supported USB/IP driver is installed. If Windows denies driver access, run DS4Windows as administrator.";
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
            "Close any conflicting VIIPER before reopening DS4Windows. Your key and profiles were not replaced.";
    }

    internal static void Initialize(string executableDirectory)
    {
        if (current != null)
            throw new PortableBrokerStartupException("The portable broker owner was already initialized. Reopen DS4Windows.");
        current = Create(executableDirectory, ViiperSetupManager.SupportedViiperSha256,
            new PortableBrokerProcessHost(), ReadManagedRoots);
    }

    // The production entry always supplies the compiled release digest. Tests
    // inject only the process boundary, managed-root lookup and path inspection.
    internal static PortableBrokerContext Create(string directory, string expectedDigest,
        IPortableBrokerProcessHost processHost, Func<IEnumerable<string>> managedRoots,
        Action<string> inspectPath = null)
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
            foreach (string managed in managedRoots())
                if (!string.IsNullOrWhiteSpace(managed) && AtOrBelow(root, NormalizeManagedRoot(managed)))
                    return null;
            Action<string> inspect = inspectPath ?? PortableLabContext.ValidateNoReparsePoints;
            inspect(marker);
            if ((markerAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                new FileInfo(marker).Length > 128 ||
                File.ReadAllText(marker).TrimEnd('\r', '\n') != MarkerText)
                throw new PortableBrokerStartupException("This portable package marker is invalid. Extract a fresh, complete DS4Windows portable package.");
            ValidateRoot(directory);
            PortableBrokerContext context = new(root, expectedDigest, processHost, inspect);
            try { context.FindCompatibleCandidate(); return context; }
            catch { context.Dispose(); throw; }
        }
        catch (PortableBrokerStartupException) { throw; }
        catch
        {
            throw new PortableBrokerStartupException("The portable package could not be verified. Extract it into a writable local folder outside Program Files, without links, and reopen DS4Windows.");
        }
    }

    private PortableBrokerContext(string root, string expectedDigest,
        IPortableBrokerProcessHost processHost, Action<string> inspect)
    {
        host = processHost;
        validatePath = inspect;
        ViiperPath = Path.Combine(root, "viiper.exe");
        DataPath = Path.Combine(root, "portable-data", "VIIPER");
        KeyPath = Path.Combine(DataPath, "viiper.key.txt");
        ConfigPath = Path.Combine(DataPath, "viiper.json");
        ValidateLocalPaths();
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
            return !disposed && backendPin.CanRead && string.Equals(path, ViiperPath, StringComparison.OrdinalIgnoreCase);
    }

    internal bool InspectOwnedProcess(out bool running, out string failure)
    {
        lock (gate) return InspectOwnedProcessCore(out running, out failure);
    }

    private bool InspectOwnedProcessCore(out bool running, out string failure)
    {
        running = false;
        failure = null;
        if (disposed || startFailed) { failure = StartFailure; return false; }
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
            try { backendPin.Dispose(); } catch { }
        }
    }
}

internal readonly record struct PortableBrokerProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);

internal interface IPortableBrokerProcessHost
{
    IReadOnlyList<PortableBrokerProcessIdentity> Snapshot();
    IReadOnlyList<string> ReadArguments(PortableBrokerProcessIdentity identity);
    IPortableBrokerProcess Start(ProcessStartInfo startInfo);
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
