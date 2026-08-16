using System.Text.Json.Serialization;

namespace DS4Windows.ViiperLiveValidation;

internal sealed class EvidenceDocument
{
    public int SchemaVersion { get; set; } = 2;
    public string Tool { get; set; } = "DS4Windows.ViiperLiveValidation";
    public string Status { get; set; } = "failure";
    public bool Finalized { get; set; }
    public string StartedUtc { get; set; } =
        DateTimeOffset.UtcNow.ToString("O");
    public string? EndedUtc { get; set; }
    public string? OutputPath { get; set; }
    public string? ConsentNonceSha256 { get; set; }
    public string? FailureStage { get; set; }
    public HostEvidence Host { get; set; } = new();
    public BindingEvidence? Bindings { get; set; }
    public List<ControllerEvidence> Controllers { get; set; } = new();
    public List<FailureEvidence> Failures { get; set; } = new();

    [JsonIgnore]
    internal string CurrentStage { get; set; } = "startup";

    internal void RecordFailure(Exception error)
    {
        FailureStage ??= CurrentStage;
        if (Failures.Count >= EvidenceLimits.MaximumFailures)
        {
            return;
        }
        Failures.Add(new FailureEvidence
        {
            Stage = EvidenceLimits.Truncate(CurrentStage, 256),
            Type = EvidenceLimits.Truncate(error.GetType().FullName ??
                error.GetType().Name, 256),
            Message = EvidenceLimits.Truncate(error.Message, 4096),
            Detail = EvidenceLimits.Truncate(error.ToString(), 16384),
        });
    }
}

internal sealed class HostEvidence
{
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string Framework { get; set; } =
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string Architecture { get; set; } =
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public long QpcFrequency { get; set; } = System.Diagnostics.Stopwatch.Frequency;
}

internal sealed class FailureEvidence
{
    public string Stage { get; set; } = "unknown";
    public string Type { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

internal sealed class BindingEvidence
{
    public string ViiperSourceRevision { get; set; } = string.Empty;
    public string ReleaseEligibility { get; set; } = string.Empty;
    public string DriverPackageVersion { get; set; } = string.Empty;
    public string DriverBuildIdentity { get; set; } = string.Empty;
    public ushort AbiMajor { get; set; }
    public ushort AbiMinor { get; set; }
    public uint Capabilities { get; set; }
    public FileBindingEvidence RunnerExecutable { get; set; } = new();
    public FileBindingEvidence RunnerAssembly { get; set; } = new();
    public FileBindingEvidence Ds4WindowsAssembly { get; set; } = new();
    public FileBindingEvidence Metadata { get; set; } = new();
    public List<FileBindingEvidence> PackageArtifacts { get; set; } = new();
    public InstalledRuntimeEvidence InstalledRuntime { get; set; } = new();
    public ProbeExecutionEvidence InputProbeExecution { get; set; } = new();
    public ProbeExecutionEvidence MediaProbeExecution { get; set; } = new();
}

internal sealed class FileBindingEvidence
{
    public string Role { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long? ExpectedLength { get; set; }
    public string? ExpectedSha256 { get; set; }
    public bool ExactMatch { get; set; }
}

internal sealed class ProbeExecutionEvidence
{
    public string Role { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string LockedFileIdentity { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Length { get; set; }
    public int LaunchCount { get; set; }
    public int? LastProcessId { get; set; }
    public string? LastProcessImagePath { get; set; }
    public string? LastProcessFileIdentity { get; set; }
    public bool AllLaunchesExact { get; set; } = true;
}

internal sealed class InstalledRuntimeEvidence
{
    public BrokerServiceEvidence Broker { get; set; } = new();
    public InstalledDriverEvidence Driver { get; set; } = new();
    public bool ExactPackageMatch { get; set; }
}

internal sealed class BrokerServiceEvidence
{
    public string ServiceName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public uint ProcessId { get; set; }
    public uint ServiceType { get; set; }
    public uint StartType { get; set; }
    public string ServiceAccount { get; set; } = string.Empty;
    public string ConfiguredImagePath { get; set; } = string.Empty;
    public FileBindingEvidence RunningImage { get; set; } = new();
    public bool ConfiguredImageIsRunningImage { get; set; }
    public bool ExactPackageMatch { get; set; }
}

internal sealed class InstalledDriverEvidence
{
    public string HardwareId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string ServiceState { get; set; } = string.Empty;
    public uint ServiceType { get; set; }
    public uint ServiceStartType { get; set; }
    public bool Started { get; set; }
    public uint ProblemCode { get; set; }
    public string DriverVersion { get; set; } = string.Empty;
    public string PublishedInfName { get; set; } = string.Empty;
    public FileBindingEvidence PublishedInf { get; set; } = new();
    public FileBindingEvidence DriverStoreInf { get; set; } = new();
    public FileBindingEvidence DriverStoreCat { get; set; } = new();
    public FileBindingEvidence DriverStoreSys { get; set; } = new();
    public FileBindingEvidence LoadedServiceImage { get; set; } = new();
    public bool ExactPackageMatch { get; set; }
}

internal sealed class ControllerEvidence
{
    public string Name { get; set; } = string.Empty;
    public string Handler { get; set; } = string.Empty;
    public string Vid { get; set; } = string.Empty;
    public string Pid { get; set; } = string.Empty;
    public string StreamProtocol { get; set; } = string.Empty;
    public PingReceiptEvidence? PingReceipt { get; set; }
    public DeviceReceiptEvidence? DeviceReceipt { get; set; }
    public InputEvidence? Input { get; set; }
    public FeedbackEvidence? Feedback { get; set; }
    public MediaEvidence? Media { get; set; }
    public ReconnectEvidence? Reconnect { get; set; }
    public CounterEvidence? Counters { get; set; }
    public CleanupEvidence Cleanup { get; set; } = new();
    public string Status { get; set; } = "failure";
    public List<FailureEvidence> Failures { get; set; } = new();
}

internal sealed class PingReceiptEvidence
{
    public string Server { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Transport { get; set; } = string.Empty;
    public ushort AbiMajor { get; set; }
    public ushort AbiMinor { get; set; }
    public uint Capabilities { get; set; }
    public string DriverPackageVersion { get; set; } = string.Empty;
    public string DriverBuildIdentity { get; set; } = string.Empty;
    public string ControllerInstanceId { get; set; } = string.Empty;
    public string ControllerSessionId { get; set; } = string.Empty;
}

internal sealed class DeviceReceiptEvidence
{
    public string Transport { get; set; } = string.Empty;
    public uint BusId { get; set; }
    public string DevId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string Vid { get; set; } = string.Empty;
    public string Pid { get; set; } = string.Empty;
    public string DeviceSerialNumber { get; set; } = string.Empty;
    public string BrokerBuildIdentity { get; set; } = string.Empty;
    public string LogicalLifetimeId { get; set; } = string.Empty;
    public long StreamGeneration { get; set; }
    public string NativeDeviceId { get; set; } = string.Empty;
    public uint NativeDeviceGeneration { get; set; }
    public string ControllerSessionId { get; set; } = string.Empty;
    public string ControllerInstanceId { get; set; } = string.Empty;
    public uint Usb20PortNumber { get; set; }
    public uint Usb30PortNumber { get; set; }
}

internal sealed class InputEvidence
{
    public string ObserverPath { get; set; } = string.Empty;
    public long QpcFrequency { get; set; }
    public int HidInputReportLength { get; set; }
    public List<InputSampleEvidence> Samples { get; set; } = new();
    public LatencySummaryEvidence Summary { get; set; } = new();
}

internal sealed class InputSampleEvidence
{
    public int Sequence { get; set; }
    public byte Marker { get; set; }
    public long PublishedQpc { get; set; }
    public long ObservedQpc { get; set; }
    public long LatencyMicroseconds { get; set; }
}

internal sealed class LatencySummaryEvidence
{
    public int Samples { get; set; }
    public long P50Microseconds { get; set; }
    public long P95Microseconds { get; set; }
    public long P99Microseconds { get; set; }
    public long MaximumMicroseconds { get; set; }
    public bool Passed { get; set; }
}

internal sealed class FeedbackEvidence
{
    public string ProbeOutput { get; set; } = string.Empty;
    public string ObserverPath { get; set; } = string.Empty;
    public int HidOutputReportLength { get; set; }
    public string ExpectedPayloadHex { get; set; } = string.Empty;
    public string ObservedPayloadHex { get; set; } = string.Empty;
    public long ObservedFrameNumber { get; set; }
    public bool ExactMatch { get; set; }
}

internal sealed class MediaEvidence
{
    public int DurationSeconds { get; set; }
    public string ProbeOutput { get; set; } = string.Empty;
    public SortedDictionary<string, string> ProbeMetrics { get; set; } =
        new(StringComparer.Ordinal);
    public long SpeakerFramesObserved { get; set; }
    public long SpeakerBytesObserved { get; set; }
    public long SpeakerNonZeroBytesObserved { get; set; }
    public long MicrophoneFramesSubmitted { get; set; }
    public long MicrophoneBytesSubmitted { get; set; }
    public bool Passed { get; set; }
}

internal sealed class ReconnectEvidence
{
    public DeviceReceiptEvidence Before { get; set; } = new();
    public DeviceReceiptEvidence After { get; set; } = new();
    public InputEvidence? PostReconnectInput { get; set; }
    public MediaEvidence? PostReconnectMedia { get; set; }
    public int RecoveryAttemptsAtCompletion { get; set; }
    public long RecoveriesCompletedAtCompletion { get; set; }
    public bool ExactLifetimePreserved { get; set; }
    public bool Passed { get; set; }
}

internal sealed class CounterEvidence
{
    public long StatePacketsSubmitted { get; set; }
    public long StatePacketsWritten { get; set; }
    public long StatePacketsCoalesced { get; set; }
    public long FeedbackFramesObserved { get; set; }
    public long SpeakerFramesEnqueued { get; set; }
    public long SpeakerFramesDequeued { get; set; }
    public long SpeakerFramesDropped { get; set; }
    public long SpeakerFramesExpired { get; set; }
    public long SpeakerFramesDelivered { get; set; }
    public long SpeakerFramesStale { get; set; }
    public long SpeakerNoSubscriberDeferrals { get; set; }
    public long SpeakerCallbackFailures { get; set; }
    public long ControlFramesEnqueued { get; set; }
    public long ControlFramesDequeued { get; set; }
    public long ControlFramesDropped { get; set; }
    public long OrderedControlFramesEnqueued { get; set; }
    public long OrderedControlFramesDequeued { get; set; }
    public long OrderedControlFramesDropped { get; set; }
    public long OrderedControlFramesExpired { get; set; }
    public long ControlFramesDelivered { get; set; }
    public long ControlFramesStale { get; set; }
    public long ControlCallbackFailures { get; set; }
    public long ValidationMicrophoneFramesSubmitted { get; set; }
    public long ValidationMicrophoneBytesSubmitted { get; set; }
    public long ValidationTransportInterruptions { get; set; }
    public long ValidationStreamRecoveriesCompleted { get; set; }
}

internal sealed class CleanupEvidence
{
    public bool DisconnectAttempted { get; set; }
    public bool DisconnectSucceeded { get; set; }
    public bool HidBaselineRestored { get; set; }
    public bool MediaBaselineRestored { get; set; }
    public string? HidBaselineSnapshotSha256 { get; set; }
    public string? MediaBaselineSnapshotSha256 { get; set; }
    public string? HidAfterSnapshotSha256 { get; set; }
    public string? MediaAfterSnapshotSha256 { get; set; }
}

internal static class EvidenceLimits
{
    internal const int MaximumFailures = 32;
    internal const int MaximumJsonBytes = 2 * 1024 * 1024;

    internal static string Truncate(string? value, int maximum)
    {
        string text = value ?? string.Empty;
        return text.Length <= maximum ? text : text[..maximum];
    }
}
