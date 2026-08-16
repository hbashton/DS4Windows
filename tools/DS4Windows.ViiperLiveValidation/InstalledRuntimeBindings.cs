using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace DS4Windows.ViiperLiveValidation;

/// <summary>
/// Captures authoritative, read-only SCM and SetupAPI evidence for the broker
/// process and the active ROOT\VIIPER\UDE Driver Store package. Package inputs
/// are only references: every installed/running byte must match them exactly.
/// </summary>
internal static class InstalledRuntimeBindings
{
    private const string BrokerServiceName = "VIIPERNativeBroker";
    private const string DriverServiceName = "ViiperUde";
    private const string HardwareId = @"ROOT\VIIPER\UDE";

    internal static InstalledRuntimeEvidence Capture(
        BindingEvidence package)
    {
        ArgumentNullException.ThrowIfNull(package);
        FileBindingEvidence brokerPackage = RequirePackageRole(package,
            "broker");
        FileBindingEvidence infPackage = RequirePackageRole(package,
            "driver-inf");
        FileBindingEvidence catPackage = RequirePackageRole(package,
            "driver-cat");
        FileBindingEvidence sysPackage = RequirePackageRole(package,
            "driver-sys");

        BrokerObservation broker = WindowsRuntimeInspector.ReadBroker(
            BrokerServiceName);
        DriverObservation driver = WindowsRuntimeInspector.ReadDriver(
            HardwareId, DriverServiceName);

        var evidence = new InstalledRuntimeEvidence
        {
            Broker = new BrokerServiceEvidence
            {
                ServiceName = broker.ServiceName,
                State = broker.State,
                ProcessId = broker.ProcessId,
                ServiceType = broker.ServiceType,
                StartType = broker.StartType,
                ServiceAccount = broker.ServiceAccount,
                ConfiguredImagePath = broker.ConfiguredImagePath,
                RunningImage = BindObservedFile(
                    "installed-running-broker", broker.RunningImagePath,
                    brokerPackage),
                ConfiguredImageIsRunningImage =
                    broker.ConfiguredImageIsRunningImage,
            },
            Driver = new InstalledDriverEvidence
            {
                HardwareId = HardwareId,
                InstanceId = driver.InstanceId,
                ServiceName = driver.ServiceName,
                ServiceState = driver.ServiceState,
                ServiceType = driver.ServiceType,
                ServiceStartType = driver.ServiceStartType,
                Started = driver.Started,
                ProblemCode = driver.ProblemCode,
                DriverVersion = driver.DriverVersion,
                PublishedInfName = driver.PublishedInfName,
                PublishedInf = BindObservedFile(
                    "installed-published-inf", driver.PublishedInfPath,
                    infPackage),
                DriverStoreInf = BindObservedFile(
                    "installed-driver-store-inf", driver.DriverStoreInfPath,
                    infPackage),
                DriverStoreCat = BindObservedFile(
                    "installed-driver-store-cat", driver.DriverStoreCatPath,
                    catPackage),
                DriverStoreSys = BindObservedFile(
                    "installed-driver-store-sys", driver.DriverStoreSysPath,
                    sysPackage),
                LoadedServiceImage = BindObservedFile(
                    "installed-driver-service-image",
                    driver.LoadedServiceImagePath, sysPackage),
            },
        };
        ValidateExactPackage(package, evidence);
        return evidence;
    }

    internal static void ValidateExactPackage(BindingEvidence package,
        InstalledRuntimeEvidence installed)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(installed);
        BrokerServiceEvidence broker = installed.Broker ??
            throw new ViiperIdentityException(
                "Installed runtime evidence omitted the broker service.");
        InstalledDriverEvidence driver = installed.Driver ??
            throw new ViiperIdentityException(
                "Installed runtime evidence omitted the active driver.");

        bool brokerExact =
            string.Equals(broker.ServiceName, BrokerServiceName,
                StringComparison.Ordinal) &&
            string.Equals(broker.State, "running",
                StringComparison.Ordinal) && broker.ProcessId != 0 &&
            broker.ServiceType == 0x10 && broker.StartType == 2 &&
            string.Equals(broker.ServiceAccount, "LocalSystem",
                StringComparison.Ordinal) &&
            broker.ConfiguredImageIsRunningImage &&
            MatchesPackage(broker.RunningImage,
                RequirePackageRole(package, "broker"));
        broker.ExactPackageMatch = brokerExact;

        bool driverExact =
            string.Equals(driver.HardwareId, HardwareId,
                StringComparison.Ordinal) &&
            driver.InstanceId.StartsWith(@"ROOT\VIIPER\",
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(driver.ServiceName, DriverServiceName,
                StringComparison.OrdinalIgnoreCase) && driver.Started &&
            string.Equals(driver.ServiceState, "running",
                StringComparison.Ordinal) && driver.ServiceType == 1 &&
            driver.ServiceStartType == 3 &&
            driver.ProblemCode == 0 &&
            string.Equals(driver.DriverVersion,
                package.DriverPackageVersion,
                StringComparison.Ordinal) &&
            IsSafePublishedInfName(driver.PublishedInfName) &&
            string.Equals(Path.GetFileName(driver.DriverStoreInf.Path),
                "ViiperUde.inf", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(driver.DriverStoreCat.Path),
                "ViiperUde.cat", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(driver.DriverStoreSys.Path),
                "ViiperUde.sys", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(
                    driver.LoadedServiceImage.Path), "ViiperUde.sys",
                StringComparison.OrdinalIgnoreCase) &&
            MatchesPackage(driver.PublishedInf,
                RequirePackageRole(package, "driver-inf")) &&
            MatchesPackage(driver.DriverStoreInf,
                RequirePackageRole(package, "driver-inf")) &&
            MatchesPackage(driver.DriverStoreCat,
                RequirePackageRole(package, "driver-cat")) &&
            MatchesPackage(driver.DriverStoreSys,
                RequirePackageRole(package, "driver-sys")) &&
            MatchesPackage(driver.LoadedServiceImage,
                RequirePackageRole(package, "driver-sys"));
        driver.ExactPackageMatch = driverExact;
        installed.ExactPackageMatch = brokerExact && driverExact;
        if (!installed.ExactPackageMatch)
        {
            throw new ViiperIdentityException(
                "The installed/running VIIPER broker or active Driver Store INF/CAT/SYS identity does not exactly match the source-bound package.");
        }
    }

    internal static void RequireUnchanged(InstalledRuntimeEvidence initial,
        InstalledRuntimeEvidence final)
    {
        if (!SameBroker(initial.Broker, final.Broker) ||
            !SameDriver(initial.Driver, final.Driver) ||
            !initial.ExactPackageMatch || !final.ExactPackageMatch)
        {
            throw new ViiperIdentityException(
                "The installed/running VIIPER runtime identity changed during live validation.");
        }
    }

    private static FileBindingEvidence RequirePackageRole(
        BindingEvidence package, string role)
    {
        FileBindingEvidence[] matches = package.PackageArtifacts.Where(
            artifact => string.Equals(artifact.Role, role,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || !matches[0].ExactMatch ||
            matches[0].Length <= 0 ||
            matches[0].Sha256.Length != 64)
        {
            throw new ViiperIdentityException(
                $"Source-bound metadata must provide exactly one verified '{role}' artifact.");
        }
        return matches[0];
    }

    private static FileBindingEvidence BindObservedFile(string role,
        string path, FileBindingEvidence expected)
    {
        FileBindingEvidence observed = SourceBindings.BindFile(role, path);
        observed.ExpectedLength = expected.Length;
        observed.ExpectedSha256 = expected.Sha256;
        observed.ExactMatch = MatchesPackage(observed, expected);
        return observed;
    }

    private static bool MatchesPackage(FileBindingEvidence observed,
        FileBindingEvidence expected) => observed.ExactMatch &&
        observed.Length == expected.Length &&
        string.Equals(observed.Sha256, expected.Sha256,
            StringComparison.Ordinal);

    private static bool SameBroker(BrokerServiceEvidence left,
        BrokerServiceEvidence right) =>
        left.ServiceName == right.ServiceName && left.State == right.State &&
        left.ProcessId == right.ProcessId &&
        left.ServiceType == right.ServiceType &&
        left.StartType == right.StartType &&
        left.ServiceAccount == right.ServiceAccount &&
        string.Equals(left.ConfiguredImagePath, right.ConfiguredImagePath,
            StringComparison.OrdinalIgnoreCase) &&
        left.ConfiguredImageIsRunningImage ==
            right.ConfiguredImageIsRunningImage &&
        SameFile(left.RunningImage, right.RunningImage) &&
        left.ExactPackageMatch == right.ExactPackageMatch;

    private static bool SameDriver(InstalledDriverEvidence left,
        InstalledDriverEvidence right) =>
        left.HardwareId == right.HardwareId &&
        string.Equals(left.InstanceId, right.InstanceId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ServiceName, right.ServiceName,
            StringComparison.OrdinalIgnoreCase) &&
        left.ServiceState == right.ServiceState &&
        left.ServiceType == right.ServiceType &&
        left.ServiceStartType == right.ServiceStartType &&
        left.Started == right.Started &&
        left.ProblemCode == right.ProblemCode &&
        left.DriverVersion == right.DriverVersion &&
        string.Equals(left.PublishedInfName, right.PublishedInfName,
            StringComparison.OrdinalIgnoreCase) &&
        SameFile(left.PublishedInf, right.PublishedInf) &&
        SameFile(left.DriverStoreInf, right.DriverStoreInf) &&
        SameFile(left.DriverStoreCat, right.DriverStoreCat) &&
        SameFile(left.DriverStoreSys, right.DriverStoreSys) &&
        SameFile(left.LoadedServiceImage, right.LoadedServiceImage) &&
        left.ExactPackageMatch == right.ExactPackageMatch;

    private static bool SameFile(FileBindingEvidence left,
        FileBindingEvidence right) =>
        string.Equals(left.Path, right.Path,
            StringComparison.OrdinalIgnoreCase) &&
        left.Length == right.Length && left.Sha256 == right.Sha256 &&
        left.ExpectedLength == right.ExpectedLength &&
        left.ExpectedSha256 == right.ExpectedSha256 &&
        left.ExactMatch == right.ExactMatch;

    private static bool IsSafePublishedInfName(string value)
    {
        if (!value.StartsWith("oem", StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ||
            value.Length < 8 || Path.GetFileName(value) != value)
        {
            return false;
        }
        return value.AsSpan(3, value.Length - 7).ToArray().All(
            character => character is >= '0' and <= '9');
    }
}

internal sealed class InstalledRuntimeLease : IDisposable
{
    private readonly List<LockedObservedFile> files;
    private int disposed;

    private InstalledRuntimeLease(InstalledRuntimeEvidence evidence,
        List<LockedObservedFile> files)
    {
        Evidence = evidence;
        this.files = files;
    }

    internal InstalledRuntimeEvidence Evidence { get; }

    internal static InstalledRuntimeLease Create(BindingEvidence package)
    {
        InstalledRuntimeEvidence evidence =
            InstalledRuntimeBindings.Capture(package);
        var files = new List<LockedObservedFile>();
        try
        {
            foreach (FileBindingEvidence binding in EnumerateFiles(evidence)
                         .GroupBy(file => file.Path,
                             StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                files.Add(new LockedObservedFile(binding));
            }
            InstalledRuntimeEvidence recaptured =
                InstalledRuntimeBindings.Capture(package);
            InstalledRuntimeBindings.RequireUnchanged(evidence, recaptured);
            return new InstalledRuntimeLease(evidence, files);
        }
        catch
        {
            foreach (LockedObservedFile file in files)
            {
                file.Dispose();
            }
            throw;
        }
    }

    internal void Revalidate(BindingEvidence package)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        foreach (LockedObservedFile file in files)
        {
            file.Revalidate();
        }
        InstalledRuntimeEvidence final =
            InstalledRuntimeBindings.Capture(package);
        InstalledRuntimeBindings.RequireUnchanged(Evidence, final);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        foreach (LockedObservedFile file in files)
        {
            file.Dispose();
        }
    }

    private static IEnumerable<FileBindingEvidence> EnumerateFiles(
        InstalledRuntimeEvidence evidence)
    {
        yield return evidence.Broker.RunningImage;
        yield return evidence.Driver.PublishedInf;
        yield return evidence.Driver.DriverStoreInf;
        yield return evidence.Driver.DriverStoreCat;
        yield return evidence.Driver.DriverStoreSys;
        yield return evidence.Driver.LoadedServiceImage;
    }

    private sealed class LockedObservedFile : IDisposable
    {
        private readonly FileBindingEvidence binding;
        private readonly FileStream stream;

        internal LockedObservedFile(FileBindingEvidence binding)
        {
            this.binding = binding;
            stream = new FileStream(binding.Path, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.SequentialScan);
            Revalidate();
        }

        internal void Revalidate()
        {
            if (stream.Length != binding.Length)
            {
                throw new ViiperIdentityException(
                    $"Locked installed file '{binding.Role}' changed length.");
            }
            stream.Position = 0;
            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stream))
                .ToLowerInvariant();
            stream.Position = 0;
            if (!string.Equals(hash, binding.Sha256,
                    StringComparison.Ordinal))
            {
                throw new ViiperIdentityException(
                    $"Locked installed file '{binding.Role}' changed SHA-256.");
            }
        }

        public void Dispose() => stream.Dispose();
    }
}

internal sealed record BrokerObservation(string ServiceName, string State,
    uint ProcessId, uint ServiceType, uint StartType, string ServiceAccount,
    string ConfiguredImagePath, string RunningImagePath,
    bool ConfiguredImageIsRunningImage);

internal sealed record DriverObservation(string InstanceId,
    string ServiceName, string ServiceState, uint ServiceType,
    uint ServiceStartType, bool Started, uint ProblemCode,
    string DriverVersion, string PublishedInfName, string PublishedInfPath,
    string DriverStoreInfPath, string DriverStoreCatPath,
    string DriverStoreSysPath, string LoadedServiceImagePath);

internal static class WindowsRuntimeInspector
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 4;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint SpdrpService = 0x00000004;
    private const uint RegSz = 1;
    private const uint RegMultiSz = 7;
    private const uint DevpropTypeString = 0x00000012;
    private const uint DnStarted = 0x00000008;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const uint MaximumNativeBufferBytes = 1024 * 1024;
    private const uint MaximumWindowsPathCharacters = 32768;
    private static readonly DevPropKey DriverVersionKey = new()
    {
        Fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"),
        Pid = 3,
    };
    private static readonly DevPropKey DriverInfPathKey = new()
    {
        Fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"),
        Pid = 5,
    };

    internal static BrokerObservation ReadBroker(string serviceName)
    {
        EnsureWindows();
        using SafeServiceHandle manager = OpenSCManager(null, null,
            ScManagerConnect);
        ThrowIfInvalid(manager, "open the Windows service manager");
        using SafeServiceHandle service = OpenService(manager, serviceName,
            ServiceQueryConfig | ServiceQueryStatus);
        ThrowIfInvalid(service, $"open service '{serviceName}'");

        QueryServiceConfig(service, IntPtr.Zero, 0, out uint configBytes);
        if (configBytes == 0 || configBytes > MaximumNativeBufferBytes ||
            Marshal.GetLastWin32Error() !=
                ErrorInsufficientBuffer)
        {
            throw LastWin32("size the broker service configuration");
        }
        IntPtr configBuffer = Marshal.AllocHGlobal((int)configBytes);
        QueryServiceConfigData config;
        string commandLine;
        string account;
        try
        {
            if (!QueryServiceConfig(service, configBuffer, configBytes,
                    out _))
            {
                throw LastWin32("read the broker service configuration");
            }
            config = Marshal.PtrToStructure<QueryServiceConfigData>(
                configBuffer);
            commandLine = Marshal.PtrToStringUni(
                config.BinaryPathName) ?? string.Empty;
            account = Marshal.PtrToStringUni(config.ServiceStartName) ??
                string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(configBuffer);
        }

        uint statusBytes = (uint)Marshal.SizeOf<ServiceStatusProcess>();
        IntPtr statusBuffer = Marshal.AllocHGlobal((int)statusBytes);
        ServiceStatusProcess status;
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo,
                    statusBuffer, statusBytes, out _))
            {
                throw LastWin32("read the broker service process status");
            }
            status = Marshal.PtrToStructure<ServiceStatusProcess>(
                statusBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(statusBuffer);
        }
        if (status.CurrentState != ServiceRunning || status.ProcessId == 0)
        {
            throw new ViiperIdentityException(
                $"Service '{serviceName}' is not running as a concrete process (state={status.CurrentState}, pid={status.ProcessId}).");
        }

        string configuredImage = FirstCommandLineArgument(commandLine);
        using SafeKernelHandle process = OpenProcess(
            ProcessQueryLimitedInformation, false, status.ProcessId);
        ThrowIfInvalid(process,
            $"open running broker process {status.ProcessId}");
        string runningImage = QueryProcessImage(process);
        string configuredIdentity = FileIdentity(configuredImage);
        string runningIdentity = FileIdentity(runningImage);
        bool same = string.Equals(configuredImage, runningImage,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(configuredIdentity, runningIdentity,
                StringComparison.Ordinal);

        return new BrokerObservation(serviceName, "running",
            status.ProcessId, config.ServiceType, config.StartType, account,
            configuredImage, runningImage, same);
    }

    internal static DriverObservation ReadDriver(string hardwareId,
        string expectedService)
    {
        EnsureWindows();
        using SafeDeviceInfoSetHandle set = SetupDiGetClassDevs(IntPtr.Zero,
            "ROOT", IntPtr.Zero, DigcfAllClasses);
        ThrowIfInvalid(set, "enumerate ROOT Plug and Play devices");
        var matches = new List<DeviceObservation>();
        for (uint index = 0; ; index++)
        {
            var data = new SpDevInfoData
            {
                CbSize = (uint)Marshal.SizeOf<SpDevInfoData>(),
            };
            if (!SetupDiEnumDeviceInfo(set, index, ref data))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems)
                {
                    break;
                }
                throw new Win32Exception(error,
                    "Could not enumerate ROOT Plug and Play devices.");
            }
            string[] hardwareIds = ReadRegistryMultiString(set, ref data,
                SpdrpHardwareId);
            if (!hardwareIds.Any(candidate => string.Equals(candidate,
                    hardwareId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            string instanceId = ReadInstanceId(set, ref data);
            string service = ReadRegistryString(set, ref data,
                SpdrpService);
            string publishedInf = ReadDevicePropertyString(set, ref data,
                DriverInfPathKey);
            string driverVersion = ReadDevicePropertyString(set, ref data,
                DriverVersionKey);
            uint result = CM_Get_DevNode_Status(out uint status,
                out uint problem, data.DevInst, 0);
            if (result != 0)
            {
                throw new Win32Exception((int)result,
                    "Could not read the VIIPER devnode status.");
            }
            matches.Add(new DeviceObservation(instanceId, service,
                (status & DnStarted) != 0, problem, driverVersion,
                publishedInf));
        }
        if (matches.Count != 1)
        {
            throw new ViiperIdentityException(
                $"Expected exactly one authoritative '{hardwareId}' devnode; found {matches.Count}.");
        }
        DeviceObservation device = matches[0];
        if (!string.Equals(device.ServiceName, expectedService,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ViiperIdentityException(
                $"The authoritative VIIPER root devnode is bound to unexpected service '{device.ServiceName}'.");
        }
        if (!IsSafePublishedInfName(device.PublishedInfName))
        {
            throw new ViiperIdentityException(
                $"The authoritative VIIPER root devnode exposes invalid published INF '{device.PublishedInfName}'.");
        }

        string windows = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        string publishedPath = SourceBindings.RequireRegularFile(
            Path.Combine(windows, "INF", device.PublishedInfName),
            "installed published VIIPER INF");
        string storeInf = GetDriverStoreInf(publishedPath);
        string repositoryRoot = Path.GetFullPath(Path.Combine(windows,
            "System32", "DriverStore", "FileRepository"));
        RequireBeneath(storeInf, repositoryRoot,
            "installed VIIPER Driver Store INF");
        string storeDirectory = Path.GetDirectoryName(storeInf) ??
            throw new IOException(
                "The installed VIIPER Driver Store directory is unavailable.");
        string storeCat = SourceBindings.RequireRegularFile(Path.Combine(
            storeDirectory, "ViiperUde.cat"),
            "installed VIIPER Driver Store catalog");
        string storeSys = SourceBindings.RequireRegularFile(Path.Combine(
            storeDirectory, "ViiperUde.sys"),
            "installed VIIPER Driver Store image");
        RequireBeneath(storeCat, repositoryRoot,
            "installed VIIPER Driver Store catalog");
        RequireBeneath(storeSys, repositoryRoot,
            "installed VIIPER Driver Store image");

        DriverServiceObservation driverService = ReadDriverService(
            expectedService);

        return new DriverObservation(device.InstanceId,
            device.ServiceName, "running", driverService.ServiceType,
            driverService.StartType, device.Started, device.ProblemCode,
            device.DriverVersion, device.PublishedInfName, publishedPath,
            storeInf, storeCat, storeSys, driverService.ImagePath);
    }

    private static DriverServiceObservation ReadDriverService(
        string serviceName)
    {
        using SafeServiceHandle manager = OpenSCManager(null, null,
            ScManagerConnect);
        ThrowIfInvalid(manager, "open the Windows service manager");
        using SafeServiceHandle service = OpenService(manager, serviceName,
            ServiceQueryConfig | ServiceQueryStatus);
        ThrowIfInvalid(service, $"open driver service '{serviceName}'");

        QueryServiceConfig(service, IntPtr.Zero, 0, out uint configBytes);
        if (configBytes == 0 || configBytes > MaximumNativeBufferBytes ||
            Marshal.GetLastWin32Error() !=
                ErrorInsufficientBuffer)
        {
            throw LastWin32("size the VIIPER driver service configuration");
        }
        IntPtr configBuffer = Marshal.AllocHGlobal((int)configBytes);
        QueryServiceConfigData config;
        string commandLine;
        try
        {
            if (!QueryServiceConfig(service, configBuffer, configBytes,
                    out _))
            {
                throw LastWin32(
                    "read the VIIPER driver service configuration");
            }
            config = Marshal.PtrToStructure<QueryServiceConfigData>(
                configBuffer);
            commandLine = Marshal.PtrToStringUni(
                config.BinaryPathName) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(configBuffer);
        }

        uint statusBytes = (uint)Marshal.SizeOf<ServiceStatusProcess>();
        IntPtr statusBuffer = Marshal.AllocHGlobal((int)statusBytes);
        ServiceStatusProcess status;
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo,
                    statusBuffer, statusBytes, out _))
            {
                throw LastWin32("read the VIIPER driver service status");
            }
            status = Marshal.PtrToStructure<ServiceStatusProcess>(
                statusBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(statusBuffer);
        }
        if (config.ServiceType != 1 || status.ServiceType != 1 ||
            status.CurrentState != ServiceRunning)
        {
            throw new ViiperIdentityException(
                $"Driver service '{serviceName}' is not a running kernel driver (configType={config.ServiceType}, statusType={status.ServiceType}, state={status.CurrentState}).");
        }
        return new DriverServiceObservation(config.ServiceType,
            config.StartType, FirstCommandLineArgument(commandLine));
    }

    private static string GetDriverStoreInf(string publishedInf)
    {
        SetupGetInfDriverStoreLocation(publishedInf, IntPtr.Zero, null, null, 0,
            out uint required);
        if (required == 0 || required > MaximumWindowsPathCharacters ||
            Marshal.GetLastWin32Error() !=
                ErrorInsufficientBuffer)
        {
            throw LastWin32("resolve the active VIIPER Driver Store INF");
        }
        var buffer = new StringBuilder((int)required);
        if (!SetupGetInfDriverStoreLocation(publishedInf, IntPtr.Zero, null,
                buffer, required, out _))
        {
            throw LastWin32("resolve the active VIIPER Driver Store INF");
        }
        return SourceBindings.RequireRegularFile(buffer.ToString(),
            "installed VIIPER Driver Store INF");
    }

    private static string FirstCommandLineArgument(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ViiperIdentityException(
                "The broker service has an empty ImagePath command line.");
        }
        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero || count < 1)
        {
            throw LastWin32("parse the broker service ImagePath");
        }
        try
        {
            IntPtr first = Marshal.ReadIntPtr(argv);
            string executable = Marshal.PtrToStringUni(first) ??
                string.Empty;
            return SourceBindings.RequireRegularFile(
                ResolveWindowsImagePath(executable),
                "configured VIIPER broker service image");
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static string ResolveWindowsImagePath(string raw)
    {
        string value = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }
        if (value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            value = value[4..];
        }
        string windows = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        if (value.StartsWith(@"\SystemRoot\",
                StringComparison.OrdinalIgnoreCase))
        {
            value = Path.Combine(windows, value[12..]);
        }
        else if (value.StartsWith(@"System32\",
                     StringComparison.OrdinalIgnoreCase))
        {
            value = Path.Combine(windows, value);
        }
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ViiperIdentityException(
                $"Installed VIIPER service uses unsupported relative image path '{raw}'.");
        }
        return SourceBindings.RequireRegularFile(value,
            "installed VIIPER service image");
    }

    private static string QueryProcessImage(SafeKernelHandle process)
    {
        var buffer = new StringBuilder(32768);
        uint length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref length) ||
            length == 0)
        {
            throw LastWin32("read the running broker process image");
        }
        return SourceBindings.RequireRegularFile(
            buffer.ToString(0, (int)length),
            "running VIIPER broker service image");
    }

    private static string FileIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return WindowsFileIdentity.Read(stream.SafeFileHandle);
    }

    private static string[] ReadRegistryMultiString(
        SafeDeviceInfoSetHandle set, ref SpDevInfoData data, uint property)
    {
        byte[] bytes = ReadRegistryProperty(set, ref data, property,
            RegMultiSz);
        return Encoding.Unicode.GetString(bytes).Split('\0',
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ReadRegistryString(SafeDeviceInfoSetHandle set,
        ref SpDevInfoData data, uint property)
    {
        byte[] bytes = ReadRegistryProperty(set, ref data, property, RegSz);
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static byte[] ReadRegistryProperty(SafeDeviceInfoSetHandle set,
        ref SpDevInfoData data, uint property, uint expectedType)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, property,
            out uint type, null, 0, out uint required);
        int error = Marshal.GetLastWin32Error();
        if (required < 2 || required > MaximumNativeBufferBytes ||
            error != ErrorInsufficientBuffer ||
            type != expectedType)
        {
            throw new Win32Exception(error,
                $"Could not size devnode registry property {property}.");
        }
        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property,
                out type, buffer, required, out _) || type != expectedType)
        {
            throw LastWin32($"read devnode registry property {property}");
        }
        return buffer;
    }

    private static string ReadDevicePropertyString(
        SafeDeviceInfoSetHandle set, ref SpDevInfoData data,
        DevPropKey key)
    {
        SetupDiGetDeviceProperty(set, ref data, ref key, out uint type,
            null, 0, out uint required, 0);
        int error = Marshal.GetLastWin32Error();
        if (required < 2 || required > MaximumNativeBufferBytes ||
            error != ErrorInsufficientBuffer ||
            type != DevpropTypeString)
        {
            throw new Win32Exception(error,
                $"Could not size devnode property {key.Pid}.");
        }
        var buffer = new byte[required];
        if (!SetupDiGetDeviceProperty(set, ref data, ref key, out type,
                buffer, required, out _, 0) || type != DevpropTypeString)
        {
            throw LastWin32($"read devnode property {key.Pid}");
        }
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string ReadInstanceId(SafeDeviceInfoSetHandle set,
        ref SpDevInfoData data)
    {
        SetupDiGetDeviceInstanceId(set, ref data, null, 0,
            out uint required);
        if (required < 2 || required > MaximumWindowsPathCharacters ||
            Marshal.GetLastWin32Error() !=
                ErrorInsufficientBuffer)
        {
            throw LastWin32("size the VIIPER devnode instance ID");
        }
        var buffer = new StringBuilder((int)required);
        if (!SetupDiGetDeviceInstanceId(set, ref data, buffer, required,
                out _))
        {
            throw LastWin32("read the VIIPER devnode instance ID");
        }
        return buffer.ToString();
    }

    private static void RequireBeneath(string path, string root,
        string role)
    {
        string full = Path.GetFullPath(path);
        string prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ViiperIdentityException(
                $"The {role} escaped the Windows Driver Store: '{full}'.");
        }
        SourceBindings.RejectReparseAncestors(full, root);
    }

    private static bool IsSafePublishedInfName(string value) =>
        value.Length >= 8 &&
        value.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
        value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(value) == value &&
        value.AsSpan(3, value.Length - 7).ToArray().All(
            character => character is >= '0' and <= '9');

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "VIIPER installed-runtime validation requires Windows.");
        }
    }

    private static void ThrowIfInvalid(SafeHandle handle, string operation)
    {
        if (handle.IsInvalid)
        {
            throw LastWin32(operation);
        }
    }

    private static Win32Exception LastWin32(string operation) => new(
        Marshal.GetLastWin32Error(), $"Could not {operation}.");

    private sealed record DeviceObservation(string InstanceId,
        string ServiceName, bool Started, uint ProblemCode,
        string DriverVersion, string PublishedInfName);

    private sealed record DriverServiceObservation(uint ServiceType,
        uint StartType, string ImagePath);

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigData
    {
        internal uint ServiceType;
        internal uint StartType;
        internal uint ErrorControl;
        internal IntPtr BinaryPathName;
        internal IntPtr LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies;
        internal IntPtr ServiceStartName;
        internal IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        internal uint CbSize;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        internal Guid Fmtid;
        internal uint Pid;
    }

    private sealed class SafeServiceHandle :
        SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    private sealed class SafeKernelHandle :
        SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeKernelHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeDeviceInfoSetHandle :
        SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeDeviceInfoSetHandle() : base(true) { }
        protected override bool ReleaseHandle() =>
            SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machine,
        string? database, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(SafeServiceHandle service,
        IntPtr config, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service, int infoLevel, IntPtr buffer,
        uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeKernelHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeKernelHandle process, uint flags, StringBuilder executableName,
        ref uint size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeDeviceInfoSetHandle SetupDiGetClassDevs(
        IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        SafeDeviceInfoSetHandle set, uint index, ref SpDevInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        SafeDeviceInfoSetHandle set, ref SpDevInfoData data, uint property,
        out uint propertyType, byte[]? buffer, uint bufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceProperty(
        SafeDeviceInfoSetHandle set, ref SpDevInfoData data,
        ref DevPropKey propertyKey, out uint propertyType, byte[]? buffer,
        uint bufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        SafeDeviceInfoSetHandle set, ref SpDevInfoData data,
        StringBuilder? instanceId, uint instanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupGetInfDriverStoreLocation(
        string fileName, IntPtr alternatePlatformInfo,
        string? localeName, StringBuilder? returnBuffer,
        uint returnBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status,
        out uint problemNumber, uint deviceInstance, uint flags);
}
