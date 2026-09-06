using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS4Windows.Switch2.Verification;

internal sealed class VerificationResult
{
    public int SchemaVersion { get; init; } = 4;
    public string Tool { get; init; } = "Switch2UsbHardwareVerify";
    public string Procedure { get; init; } =
        "fixed-switch2-pro-usb-bcd0201-sole-writer-v2";
    public required string VerifierAssemblySha256 { get; init; }
    public bool Success { get; set; }
    public string SuccessScope { get; init; } =
        "host-side fixed-scope procedure and exact captured command response-shape/tuple matches only; responses are not transaction-correlated and HID completion has no physical haptic readback";
    public string? FailureCode { get; set; }
    public string? ProcedureFailureCode { get; set; }
    public int? FailureNativeErrorCode { get; set; }
    public string? CommandResponseFailureDetail { get; set; }
    public string? CommandTransferFailureStage { get; set; }
    public int? CommandObservedResponseLength { get; set; }
    public int? CommandObservedResponseHeaderByte4 { get; set; }
    public int? CommandObservedResponseAcknowledgement { get; set; }
    public PublicTargetIdentity Target { get; init; } = new();
    public BulkTopologyResult BulkTopology { get; init; } = new();
    public PipePolicyResult PipePolicy { get; init; } = new();
    public VolatileInitializationResult VolatileInitialization { get; init; } =
        new();
    public BatteryResult Battery { get; init; } = new();
    public LedResult Led { get; init; } = new();
    public InputRateResult InputRate { get; init; } = new();
    public HapticResult Haptic { get; init; } = new();
    public CleanupResult Cleanup { get; init; } = new();
    public string[] RedactionManifest { get; init; } =
    [
        "device paths are neither serialized nor hashed",
        "device instance IDs are neither serialized nor hashed",
        "container IDs are neither serialized nor hashed",
        "serial numbers and MAC addresses are not queried",
        "raw input reports are not serialized",
    ];
    public string[] Limitations { get; init; } =
    [
        "This is a bounded mechanism check, not a latency benchmark or conformance test.",
        "The warm-up live-tail floor is a backlog heuristic, not proof that the host input queue is empty.",
        "Player LED AllOff is explicit neutralization, not restoration of prior state.",
        "The planned haptic basis is modest and SDL-corroborated but is not emitted while the live-mutation safety gate is closed.",
        "Only capture-backed volatile USB-HID enable and common-report selection are attempted; no host-address USB initialisation, pairing, association, memory, feature-selection, firmware, calibration, driver, or persistent-state operation is performed.",
        "A failed cleanup attempt is reported and cannot prove physical neutral state.",
        "A completed HID output write has no device response/readback and does not prove physical actuator neutral state.",
        "The controller must not be unplugged, replaced, reset, or re-enumerated during the run; exact repeated Windows identity/topology matching is not a physical-generation signal.",
        "The capture-pinned 0x78 response byte is not decoded as a semantic status and the protocol has no admitted transaction identifier for causal response attribution.",
        "If a noncooperative HID operation crosses a hard phase deadline, Main abandons channel ownership to a late disposer; neutralization may be blocked until completion or process exit.",
        "If a noncooperative WinUSB command crosses its hard ownership deadline, only its late owner may dispose the command channel; Player LED neutralization may remain blocked and unconfirmed.",
    ];

    internal string ToJson() => JsonSerializer.Serialize(this,
        VerificationJsonContext.Default.VerificationResult);
}

internal sealed class PipePolicyResult
{
    public bool Validated { get; set; }
    public int? ObservedAllowPartialReadsValueLength { get; set; }
    public int? ObservedAllowPartialReadsValue { get; set; }

    internal void Record(WinUsbPipePolicyObservation observation)
    {
        ObservedAllowPartialReadsValueLength =
            checked((int)observation.AllowPartialReadsValueLength);
        ObservedAllowPartialReadsValue =
            checked((int)observation.AllowPartialReadsValue);
    }
}

internal sealed class PublicTargetIdentity
{
    public string Model { get; init; } = "Nintendo Switch 2 Pro Controller";
    public string VendorId { get; init; } = "0x057E";
    public string ProductId { get; init; } = "0x2069";
    public string DeviceReleaseBcd { get; init; } = "0x0201";
    public string HidInterface { get; init; } = "MI_00";
    public string CommandInterface { get; init; } = "MI_01";
    public bool SoleHidWriterAdmissionRequired { get; init; } = true;
    public bool SoleHidWriterAdmissionSucceeded { get; set; }
    public string SessionIdentityScope { get; init; } =
        "exact private in-run Windows identity and topology revalidation; no physical-generation claim";
    public string RunPrecondition { get; init; } =
        "no unplug, replacement, reset, or re-enumeration during the procedure";
}

internal sealed class BulkTopologyResult
{
    public bool Validated { get; set; }
    public string ExpectedOutPipe { get; init; } = "0x02 bulk OUT";
    public string ExpectedInPipe { get; init; } = "0x82 bulk IN";
    public int ExpectedMaximumPacketSize { get; init; } = 64;
    public int? ObservedInterfaceNumber { get; set; }
    public int? ObservedAlternateSetting { get; set; }
    public int? ObservedEndpointCount { get; set; }
    public int? ObservedPipe0Id { get; set; }
    public int? ObservedPipe0TypeCode { get; set; }
    public int? ObservedPipe0MaximumPacketSize { get; set; }
    public int? ObservedPipe0Interval { get; set; }
    public int? ObservedPipe1Id { get; set; }
    public int? ObservedPipe1TypeCode { get; set; }
    public int? ObservedPipe1MaximumPacketSize { get; set; }
    public int? ObservedPipe1Interval { get; set; }

    internal void Record(UsbPipeTopologyObservation observation)
    {
        ObservedInterfaceNumber = observation.InterfaceNumber;
        ObservedAlternateSetting = observation.AlternateSetting;
        ObservedEndpointCount = observation.EndpointCount;
        ObservedPipe0Id = observation.Pipe0.PipeId;
        ObservedPipe0TypeCode = (int)observation.Pipe0.PipeType;
        ObservedPipe0MaximumPacketSize = observation.Pipe0.MaximumPacketSize;
        ObservedPipe0Interval = observation.Pipe0.Interval;
        ObservedPipe1Id = observation.Pipe1.PipeId;
        ObservedPipe1TypeCode = (int)observation.Pipe1.PipeType;
        ObservedPipe1MaximumPacketSize = observation.Pipe1.MaximumPacketSize;
        ObservedPipe1Interval = observation.Pipe1.Interval;
    }
}

internal sealed class BatteryResult
{
    public bool ExactResponseShapeAndTuple { get; set; }
    public Switch2UsbCommandResponseStyle? ResponseStyle { get; set; }
    public ushort? RawVoltage { get; set; }
}

internal sealed class VolatileInitializationResult
{
    public bool EnableUsbHidReportsAttempted { get; set; }
    public bool EnableUsbHidReportsExactResponseShapeAndTuple { get; set; }
    public bool SelectCommonInputReportAttempted { get; set; }
    public bool SelectCommonInputReportExactResponseShapeAndTuple { get; set; }
}

internal sealed class LedResult
{
    public bool Player1MutationAttempted { get; set; }
    public bool Player1MutationDeliveryAmbiguous { get; set; }
    public bool Player1ExactResponseShapeAndTuple { get; set; }
    public Switch2UsbCommandResponseStyle? Player1ResponseStyle { get; set; }
    public bool AllOffMutationAttempted { get; set; }
    public bool AllOffMutationDeliveryAmbiguous { get; set; }
    public bool AllOffExactResponseShapeAndTuple { get; set; }
    public Switch2UsbCommandResponseStyle? AllOffResponseStyle { get; set; }
    public string FinalPolicy { get; init; } =
        "AllOff explicit neutral; prior LED state is not restored";
}

internal static class LedCleanupResultTransition
{
    internal static void MarkAttempted(LedResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.AllOffMutationAttempted = true;
        result.AllOffMutationDeliveryAmbiguous = true;
        result.AllOffExactResponseShapeAndTuple = false;
        result.AllOffResponseStyle = null;
    }

    internal static void MarkConfirmed(LedResult result,
        Switch2UsbCommandResponseStyle responseStyle)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(responseStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(responseStyle));
        }
        result.AllOffMutationDeliveryAmbiguous = false;
        result.AllOffExactResponseShapeAndTuple = true;
        result.AllOffResponseStyle = responseStyle;
    }
}

internal sealed class InputRateResult
{
    public int WarmupReports { get; init; } =
        VerificationPlan.InputWarmupReportCount;
    public int RequestedReports { get; init; } =
        VerificationPlan.InputReportCount;
    public int WholePhaseDeadlineMilliseconds { get; init; } =
        VerificationPlan.InputCaptureTimeoutMilliseconds;
    public int ExactReports { get; set; }
    public string RequiredReportId { get; init; } = "0x05";
    public double? ObservedReportsPerSecond { get; set; }
    public double? MeanIntervalMilliseconds { get; set; }
    public double? P50IntervalMilliseconds { get; set; }
    public double? P95IntervalMilliseconds { get; set; }
    public double? P99IntervalMilliseconds { get; set; }
    public int CounterForwardMovements { get; set; }
    public uint? CounterMinimumDelta { get; set; }
    public uint? CounterMaximumDelta { get; set; }
    public int CounterPlusFourMovements { get; set; }
    public bool CounterWrapObserved { get; set; }
    public string CounterScope { get; init; } =
        "uint32 LE Common05 movement at USB bytes 1..4; delta is not packet loss or proof of report rate";
    public string TimingScope { get; init; } =
        "single verifier reader host completion cadence; other readers may coexist; not calibrated input latency";
}

internal sealed class HapticResult
{
    public string LiveMutationSafetyGate { get; init; } =
        "closed: process exit cannot prove physical neutralization after a noncooperative HID write";
    public bool NonzeroMutationBlockedBySafetyGate { get; init; } =
        !VerificationPlan.LiveHapticMutationSafetyGateOpen;
    public bool NonzeroMutationAttempted { get; set; }
    public int ZeroAmplitudeWritesAttempted { get; set; }
    public int ZeroAmplitudeHostWritesCompleted { get; set; }
    public bool ZeroAmplitudeDeliveryAmbiguous { get; set; }
    public string Oscillator0Control { get; init; } = "0x187";
    public string Oscillator1Control { get; init; } = "0x112";
    public int BasisAmplitudeCode { get; init; } =
        VerificationPlan.BasisAmplitude;
    public int SdlClampAmplitudeCode { get; init; } =
        VerificationPlan.SdlClampAmplitudeCode;
    public int RequestedFrames { get; init; } =
        VerificationPlan.HapticFrameCount;
    public int WritesAttempted { get; set; }
    public int HostWritesCompleted { get; set; }
    public int CadenceMilliseconds { get; init; } =
        VerificationPlan.HapticCadenceMilliseconds;
    public int MaximumNonzeroEmissionWindowMilliseconds { get; init; } =
        VerificationPlan.HapticMaximumDurationMilliseconds;
    public string NeutralizationScope { get; init; } =
        "LED cleanup is attempted first; zero-amplitude host write is separately bounded and has no physical readback";
}

internal sealed class CleanupResult
{
    public bool HapticStopRequired { get; set; }
    public int HapticStopAttempts { get; set; }
    public bool HapticStopHostWriteCompleted { get; set; }
    public bool HidChannelReopened { get; set; }
    public bool InputCaptureHidOwnershipAbandoned { get; set; }
    public bool HapticOutputHidOwnershipAbandoned { get; set; }
    public bool HapticNeutralizationBlockedByOwnership { get; set; }
    public bool PlayerLedAllOffRequired { get; set; }
    public bool PlayerLedAllOffSucceeded { get; set; }
    public bool PlayerLedCleanupArmTimedOut { get; set; }
    public bool PlayerLedCommandOwnershipAbandoned { get; set; }
    public bool CommandOutputOwnershipAbandoned { get; set; }
    public bool PlayerLedNeutralizationBlockedByOwnership { get; set; }
    public bool CommandChannelReopened { get; set; }
    public bool HapticCleanupArmTimedOut { get; set; }
    public bool HapticCleanupHidOwnershipAbandoned { get; set; }
    public bool LateOutputHandleReleaseUnconfirmed { get; set; }
    public bool LateInputHandleReleaseUnconfirmed { get; set; }
    public bool SessionIdentityRevalidationFailure { get; set; }
    public bool InputChannelDisposeFailure { get; set; }
    public bool HidChannelDisposeFailure { get; set; }
    public bool CommandChannelDisposeFailure { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(VerificationResult))]
internal partial class VerificationJsonContext : JsonSerializerContext
{
}

internal static class VerificationPrivacyValidator
{
    private static readonly string[] ForbiddenPropertyFragments =
    [
        "path", "instanceid", "containerid", "serial", "mac", "hash",
    ];

    private static readonly HashSet<string> AllowedPropertyNames = new(
        StringComparer.Ordinal)
    {
        "SchemaVersion", "Tool", "Procedure", "VerifierAssemblySha256",
        "Success", "SuccessScope", "FailureCode", "ProcedureFailureCode",
        "FailureNativeErrorCode",
        "CommandResponseFailureDetail", "CommandTransferFailureStage",
        "CommandObservedResponseLength",
        "CommandObservedResponseHeaderByte4",
        "CommandObservedResponseAcknowledgement", "Target", "BulkTopology",
        "PipePolicy", "VolatileInitialization", "Battery", "Led",
        "InputRate", "Haptic", "Cleanup", "RedactionManifest",
        "Limitations", "Model", "VendorId", "ProductId",
        "DeviceReleaseBcd", "HidInterface", "CommandInterface",
        "SoleHidWriterAdmissionRequired",
        "SoleHidWriterAdmissionSucceeded",
        "SessionIdentityScope", "RunPrecondition", "Validated",
        "ExpectedOutPipe", "ExpectedInPipe", "ExpectedMaximumPacketSize",
        "ObservedInterfaceNumber", "ObservedAlternateSetting",
        "ObservedEndpointCount", "ObservedPipe0Id", "ObservedPipe0TypeCode",
        "ObservedPipe0MaximumPacketSize", "ObservedPipe0Interval",
        "ObservedPipe1Id", "ObservedPipe1TypeCode",
        "ObservedPipe1MaximumPacketSize", "ObservedPipe1Interval",
        "ObservedAllowPartialReadsValueLength",
        "ObservedAllowPartialReadsValue",
        "EnableUsbHidReportsAttempted",
        "EnableUsbHidReportsExactResponseShapeAndTuple",
        "SelectCommonInputReportAttempted",
        "SelectCommonInputReportExactResponseShapeAndTuple",
        "ExactResponseShapeAndTuple", "ResponseStyle", "RawVoltage",
        "Player1ExactResponseShapeAndTuple", "Player1ResponseStyle",
        "Player1MutationAttempted", "Player1MutationDeliveryAmbiguous",
        "AllOffMutationAttempted",
        "AllOffMutationDeliveryAmbiguous",
        "AllOffExactResponseShapeAndTuple", "AllOffResponseStyle",
        "FinalPolicy", "WarmupReports",
        "RequestedReports", "WholePhaseDeadlineMilliseconds",
        "ExactReports", "RequiredReportId", "ObservedReportsPerSecond",
        "MeanIntervalMilliseconds", "P50IntervalMilliseconds",
        "P95IntervalMilliseconds", "P99IntervalMilliseconds",
        "CounterForwardMovements", "CounterMinimumDelta",
        "CounterMaximumDelta", "CounterPlusFourMovements",
        "CounterWrapObserved", "CounterScope", "TimingScope",
        "Oscillator0Control", "Oscillator1Control", "BasisAmplitudeCode",
        "SdlClampAmplitudeCode", "RequestedFrames", "WritesAttempted",
        "HostWritesCompleted", "CadenceMilliseconds",
        "MaximumNonzeroEmissionWindowMilliseconds", "NeutralizationScope",
        "LiveMutationSafetyGate", "NonzeroMutationBlockedBySafetyGate",
        "NonzeroMutationAttempted", "ZeroAmplitudeWritesAttempted",
        "ZeroAmplitudeHostWritesCompleted", "ZeroAmplitudeDeliveryAmbiguous",
        "HapticStopRequired", "HapticStopAttempts",
        "HapticStopHostWriteCompleted", "HidChannelReopened",
        "InputCaptureHidOwnershipAbandoned",
        "HapticOutputHidOwnershipAbandoned",
        "HapticNeutralizationBlockedByOwnership",
        "PlayerLedAllOffRequired", "PlayerLedAllOffSucceeded",
        "PlayerLedCleanupArmTimedOut",
        "PlayerLedCommandOwnershipAbandoned",
        "CommandOutputOwnershipAbandoned",
        "PlayerLedNeutralizationBlockedByOwnership",
        "CommandChannelReopened",
        "HapticCleanupArmTimedOut", "HapticCleanupHidOwnershipAbandoned",
        "LateOutputHandleReleaseUnconfirmed",
        "LateInputHandleReleaseUnconfirmed",
        "SessionIdentityRevalidationFailure", "InputChannelDisposeFailure",
        "HidChannelDisposeFailure",
        "CommandChannelDisposeFailure",
    };

    private static readonly HashSet<string> AllowedStringValues =
        CreateAllowedStringValues();

    internal static bool IsPrivacySafeClosedSchemaJson(string? json)
    {
        if (json is null)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !HasNoDuplicateProperties(document.RootElement) ||
                !CheckElement(document.RootElement))
            {
                return false;
            }

            VerificationResult? result = JsonSerializer.Deserialize(json,
                VerificationJsonContext.Default.VerificationResult);
            return result is not null && IsSemanticallyValid(result) &&
                string.Equals(json, result.ToJson(),
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) ||
                    !HasNoDuplicateProperties(property.Value))
                {
                    return false;
                }
            }
            return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                if (!HasNoDuplicateProperties(child))
                {
                    return false;
                }
            }
            return true;
        }
        return true;
    }

    private static bool IsSemanticallyValid(VerificationResult result)
    {
        var expected = new VerificationResult
        {
            VerifierAssemblySha256 = result.VerifierAssemblySha256,
        };
        if (result.SchemaVersion != expected.SchemaVersion ||
            result.Tool != expected.Tool ||
            result.Procedure != expected.Procedure ||
            result.SuccessScope != expected.SuccessScope ||
            // This schema fixes the live nonzero gate closed, so Main cannot
            // reach procedureSucceeded. A canonical success claim would not
            // describe any execution of this binary.
            result.Success ||
            !IsUpperHexSha256(result.VerifierAssemblySha256) ||
            !IsFailureCode(result.FailureCode, allowCleanupIncomplete: true) ||
            !IsFailureCode(result.ProcedureFailureCode,
                allowCleanupIncomplete: false) ||
            result.Success != (result.FailureCode is null) ||
            (result.Success && result.ProcedureFailureCode is not null) ||
            result.FailureNativeErrorCode is < 0 ||
            result.CommandObservedResponseLength is < 0 or >
                VerificationPlan.BulkMaximumPacketSize ||
            result.CommandObservedResponseHeaderByte4 is < 0 or > byte.MaxValue ||
            result.CommandObservedResponseAcknowledgement is < 0 or > byte.MaxValue ||
            !IsOptionalEnumName<Switch2UsbCommandFailure>(
                result.CommandResponseFailureDetail) ||
            !IsOptionalEnumName<CommandTransferFailureStage>(
                result.CommandTransferFailureStage))
        {
            return false;
        }

        PublicTargetIdentity target = result.Target;
        PublicTargetIdentity expectedTarget = expected.Target;
        if (target.Model != expectedTarget.Model ||
            target.VendorId != expectedTarget.VendorId ||
            target.ProductId != expectedTarget.ProductId ||
            target.DeviceReleaseBcd != expectedTarget.DeviceReleaseBcd ||
            target.HidInterface != expectedTarget.HidInterface ||
            target.CommandInterface != expectedTarget.CommandInterface ||
            !target.SoleHidWriterAdmissionRequired ||
            target.SessionIdentityScope != expectedTarget.SessionIdentityScope ||
            target.RunPrecondition != expectedTarget.RunPrecondition)
        {
            return false;
        }

        BulkTopologyResult topology = result.BulkTopology;
        if (topology.ExpectedOutPipe != expected.BulkTopology.ExpectedOutPipe ||
            topology.ExpectedInPipe != expected.BulkTopology.ExpectedInPipe ||
            topology.ExpectedMaximumPacketSize !=
                VerificationPlan.BulkMaximumPacketSize ||
            !InByteRange(topology.ObservedInterfaceNumber) ||
            !InByteRange(topology.ObservedAlternateSetting) ||
            topology.ObservedEndpointCount is < 0 or > byte.MaxValue ||
            !InByteRange(topology.ObservedPipe0Id) ||
            topology.ObservedPipe0TypeCode is < 0 or > 3 ||
            topology.ObservedPipe0MaximumPacketSize is < 0 or > ushort.MaxValue ||
            !InByteRange(topology.ObservedPipe0Interval) ||
            !InByteRange(topology.ObservedPipe1Id) ||
            topology.ObservedPipe1TypeCode is < 0 or > 3 ||
            topology.ObservedPipe1MaximumPacketSize is < 0 or > ushort.MaxValue ||
            !InByteRange(topology.ObservedPipe1Interval) ||
            result.PipePolicy.ObservedAllowPartialReadsValueLength is < 0 or > 4 ||
            !InByteRange(result.PipePolicy.ObservedAllowPartialReadsValue))
        {
            return false;
        }

        VolatileInitializationResult initialization =
            result.VolatileInitialization;
        if ((initialization.EnableUsbHidReportsExactResponseShapeAndTuple &&
                !initialization.EnableUsbHidReportsAttempted) ||
            (initialization.SelectCommonInputReportAttempted &&
                !initialization
                    .EnableUsbHidReportsExactResponseShapeAndTuple) ||
            (initialization
                    .SelectCommonInputReportExactResponseShapeAndTuple &&
                !initialization.SelectCommonInputReportAttempted))
        {
            return false;
        }

        BatteryResult battery = result.Battery;
        if (battery.ExactResponseShapeAndTuple !=
                (battery.RawVoltage.HasValue && battery.ResponseStyle.HasValue) ||
            (battery.ResponseStyle.HasValue &&
                !Enum.IsDefined(battery.ResponseStyle.Value)) ||
            (battery.ExactResponseShapeAndTuple &&
                (!initialization
                    .EnableUsbHidReportsExactResponseShapeAndTuple ||
                 !initialization
                    .SelectCommonInputReportExactResponseShapeAndTuple)))
        {
            return false;
        }

        LedResult led = result.Led;
        if (led.FinalPolicy != expected.Led.FinalPolicy ||
            led.Player1ExactResponseShapeAndTuple !=
                led.Player1ResponseStyle.HasValue ||
            (led.Player1MutationAttempted &&
                !battery.ExactResponseShapeAndTuple) ||
            (led.Player1ResponseStyle.HasValue &&
                !Enum.IsDefined(led.Player1ResponseStyle.Value)) ||
            (led.Player1ExactResponseShapeAndTuple &&
                (!led.Player1MutationAttempted ||
                    led.Player1MutationDeliveryAmbiguous)) ||
            (led.Player1MutationDeliveryAmbiguous &&
                (!led.Player1MutationAttempted ||
                    led.Player1ExactResponseShapeAndTuple)) ||
            led.AllOffExactResponseShapeAndTuple !=
                led.AllOffResponseStyle.HasValue ||
            (led.AllOffResponseStyle.HasValue &&
                !Enum.IsDefined(led.AllOffResponseStyle.Value)) ||
            (led.AllOffExactResponseShapeAndTuple &&
                (!led.AllOffMutationAttempted ||
                    led.AllOffMutationDeliveryAmbiguous)) ||
            (led.AllOffMutationDeliveryAmbiguous &&
                led.AllOffExactResponseShapeAndTuple))
        {
            return false;
        }

        InputRateResult input = result.InputRate;
        bool hasTiming = input.ObservedReportsPerSecond.HasValue ||
            input.MeanIntervalMilliseconds.HasValue ||
            input.P50IntervalMilliseconds.HasValue ||
            input.P95IntervalMilliseconds.HasValue ||
            input.P99IntervalMilliseconds.HasValue;
        bool hasCompleteTiming = input.ObservedReportsPerSecond.HasValue &&
            input.MeanIntervalMilliseconds.HasValue &&
            input.P50IntervalMilliseconds.HasValue &&
            input.P95IntervalMilliseconds.HasValue &&
            input.P99IntervalMilliseconds.HasValue;
        if (input.WarmupReports != VerificationPlan.InputWarmupReportCount ||
            input.RequestedReports != VerificationPlan.InputReportCount ||
            input.WholePhaseDeadlineMilliseconds !=
                VerificationPlan.InputCaptureTimeoutMilliseconds ||
            input.RequiredReportId != expected.InputRate.RequiredReportId ||
            input.CounterScope != expected.InputRate.CounterScope ||
            input.TimingScope != expected.InputRate.TimingScope ||
            input.ExactReports is not (0 or VerificationPlan.InputReportCount) ||
            hasTiming != (input.ExactReports == VerificationPlan.InputReportCount) ||
            hasTiming != hasCompleteTiming ||
            !IsFiniteNonnegative(input.ObservedReportsPerSecond, 1_000_000) ||
            !IsFiniteNonnegative(input.MeanIntervalMilliseconds,
                VerificationPlan.InputCaptureTimeoutMilliseconds) ||
            !IsFiniteNonnegative(input.P50IntervalMilliseconds,
                VerificationPlan.InputCaptureTimeoutMilliseconds) ||
            !IsFiniteNonnegative(input.P95IntervalMilliseconds,
                VerificationPlan.InputCaptureTimeoutMilliseconds) ||
            !IsFiniteNonnegative(input.P99IntervalMilliseconds,
                VerificationPlan.InputCaptureTimeoutMilliseconds) ||
            input.CounterForwardMovements is < 0 or >=
                VerificationPlan.InputReportCount ||
            input.CounterPlusFourMovements is < 0 or >=
                VerificationPlan.InputReportCount ||
            input.CounterPlusFourMovements > input.CounterForwardMovements ||
            input.CounterMinimumDelta.HasValue !=
                input.CounterMaximumDelta.HasValue ||
            (input.ExactReports == 0 &&
                (input.CounterForwardMovements != 0 ||
                 input.CounterPlusFourMovements != 0 ||
                 input.CounterMinimumDelta.HasValue ||
                 input.CounterWrapObserved)) ||
            (input.ExactReports == VerificationPlan.InputReportCount &&
                (input.CounterForwardMovements !=
                    VerificationPlan.InputReportCount - 1 ||
                 !input.CounterMinimumDelta.HasValue)))
        {
            return false;
        }

        HapticResult haptic = result.Haptic;
        HapticResult expectedHaptic = expected.Haptic;
        if (haptic.LiveMutationSafetyGate !=
                expectedHaptic.LiveMutationSafetyGate ||
            !haptic.NonzeroMutationBlockedBySafetyGate ||
            haptic.NonzeroMutationAttempted ||
            haptic.ZeroAmplitudeWritesAttempted != 0 ||
            haptic.ZeroAmplitudeHostWritesCompleted != 0 ||
            haptic.Oscillator0Control != expectedHaptic.Oscillator0Control ||
            haptic.Oscillator1Control != expectedHaptic.Oscillator1Control ||
            haptic.BasisAmplitudeCode != VerificationPlan.BasisAmplitude ||
            haptic.SdlClampAmplitudeCode !=
                VerificationPlan.SdlClampAmplitudeCode ||
            haptic.RequestedFrames != VerificationPlan.HapticFrameCount ||
            haptic.WritesAttempted != 0 || haptic.HostWritesCompleted != 0 ||
            haptic.CadenceMilliseconds !=
                VerificationPlan.HapticCadenceMilliseconds ||
            haptic.MaximumNonzeroEmissionWindowMilliseconds !=
                VerificationPlan.HapticMaximumDurationMilliseconds ||
            haptic.NeutralizationScope != expectedHaptic.NeutralizationScope)
        {
            return false;
        }

        CleanupResult cleanup = result.Cleanup;
        if (cleanup.HapticStopRequired || cleanup.HapticStopAttempts != 0 ||
            cleanup.HapticStopHostWriteCompleted || cleanup.HidChannelReopened ||
            cleanup.HapticOutputHidOwnershipAbandoned ||
            cleanup.HapticNeutralizationBlockedByOwnership ||
            (cleanup.InputCaptureHidOwnershipAbandoned &&
                !cleanup.LateInputHandleReleaseUnconfirmed) ||
            (cleanup.CommandOutputOwnershipAbandoned &&
                !cleanup.LateOutputHandleReleaseUnconfirmed) ||
            (cleanup.PlayerLedAllOffSucceeded !=
                led.AllOffExactResponseShapeAndTuple) ||
            (cleanup.PlayerLedAllOffRequired &&
                !led.Player1MutationAttempted) ||
            (led.AllOffMutationAttempted &&
                !cleanup.PlayerLedAllOffRequired) ||
            (led.AllOffMutationDeliveryAmbiguous &&
                !led.AllOffMutationAttempted &&
                !cleanup.PlayerLedCleanupArmTimedOut) ||
            (cleanup.PlayerLedNeutralizationBlockedByOwnership &&
                (!cleanup.PlayerLedAllOffRequired ||
                    !cleanup.PlayerLedCommandOwnershipAbandoned)) ||
            (cleanup.PlayerLedCommandOwnershipAbandoned &&
                !cleanup.CommandOutputOwnershipAbandoned))
        {
            return false;
        }

        return result.RedactionManifest.SequenceEqual(
                   expected.RedactionManifest, StringComparer.Ordinal) &&
            result.Limitations.SequenceEqual(expected.Limitations,
                StringComparer.Ordinal);
    }

    private static bool IsFailureCode(string? value,
        bool allowCleanupIncomplete) => value is null ||
        (allowCleanupIncomplete && value == "CleanupIncomplete") ||
        IsExactEnumName<VerificationFailureCode>(value);

    private static bool IsOptionalEnumName<T>(string? value)
        where T : struct, Enum => value is null || IsExactEnumName<T>(value);

    private static bool IsExactEnumName<T>(string value)
        where T : struct, Enum => Enum.TryParse(value,
            ignoreCase: false, out T parsed) && Enum.IsDefined(parsed) &&
            value == parsed.ToString();

    private static bool InByteRange(int? value) =>
        value is null or (>= byte.MinValue and <= byte.MaxValue);

    private static bool IsFiniteNonnegative(double? value, double maximum) =>
        value is null || (double.IsFinite(value.Value) && value.Value >= 0 &&
            value.Value <= maximum);

    private static bool IsUpperHexSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    private static bool CheckElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!AllowedPropertyNames.Contains(property.Name))
                {
                    return false;
                }
                string normalized = property.Name.Replace("_", string.Empty,
                    StringComparison.Ordinal).ToLowerInvariant();
                foreach (string forbidden in ForbiddenPropertyFragments)
                {
                    if (normalized.Contains(forbidden,
                        StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
                if (property.Name == "VerifierAssemblySha256")
                {
                    if (!IsUpperHexSha256(property.Value))
                    {
                        return false;
                    }
                    continue;
                }
                if (!CheckElement(property.Value))
                {
                    return false;
                }
            }
            return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                if (!CheckElement(child))
                {
                    return false;
                }
            }
            return true;
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            return value is not null && !LooksLikePrivateIdentifier(value) &&
                AllowedStringValues.Contains(value);
        }
        // Canonical output omits optional nulls. Reject null/undefined here so
        // an explicit null cannot bypass the shape walk and reach nullable
        // runtime state in semantic validation.
        return element.ValueKind is JsonValueKind.Number or
            JsonValueKind.True or JsonValueKind.False;
    }

    private static bool IsUpperHexSha256(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string? value = element.GetString();
        if (value is null || value.Length != 64)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }
        return true;
    }

    private static HashSet<string> CreateAllowedStringValues()
    {
        var values = new HashSet<string>(StringComparer.Ordinal)
        {
            "Switch2UsbHardwareVerify",
            "fixed-switch2-pro-usb-bcd0201-sole-writer-v2",
            "host-side fixed-scope procedure and exact captured command response-shape/tuple matches only; responses are not transaction-correlated and HID completion has no physical haptic readback",
            "CleanupIncomplete",
            "Nintendo Switch 2 Pro Controller",
            "0x057E",
            "0x2069",
            "0x0201",
            "MI_00",
            "MI_01",
            "exact private in-run Windows identity and topology revalidation; no physical-generation claim",
            "no unplug, replacement, reset, or re-enumeration during the procedure",
            "0x02 bulk OUT",
            "0x82 bulk IN",
            "AllOff explicit neutral; prior LED state is not restored",
            "0x05",
            "uint32 LE Common05 movement at USB bytes 1..4; delta is not packet loss or proof of report rate",
            "single verifier reader host completion cadence; other readers may coexist; not calibrated input latency",
            "0x187",
            "0x112",
            "LED cleanup is attempted first; zero-amplitude host write is separately bounded and has no physical readback",
            "closed: process exit cannot prove physical neutralization after a noncooperative HID write",
            "device paths are neither serialized nor hashed",
            "device instance IDs are neither serialized nor hashed",
            "container IDs are neither serialized nor hashed",
            "serial numbers and MAC addresses are not queried",
            "raw input reports are not serialized",
            "This is a bounded mechanism check, not a latency benchmark or conformance test.",
            "The warm-up live-tail floor is a backlog heuristic, not proof that the host input queue is empty.",
            "Player LED AllOff is explicit neutralization, not restoration of prior state.",
            "The planned haptic basis is modest and SDL-corroborated but is not emitted while the live-mutation safety gate is closed.",
            "Only capture-backed volatile USB-HID enable and common-report selection are attempted; no host-address USB initialisation, pairing, association, memory, feature-selection, firmware, calibration, driver, or persistent-state operation is performed.",
            "A failed cleanup attempt is reported and cannot prove physical neutral state.",
            "A completed HID output write has no device response/readback and does not prove physical actuator neutral state.",
            "The controller must not be unplugged, replaced, reset, or re-enumerated during the run; exact repeated Windows identity/topology matching is not a physical-generation signal.",
            "The capture-pinned 0x78 response byte is not decoded as a semantic status and the protocol has no admitted transaction identifier for causal response attribution.",
            "If a noncooperative HID operation crosses a hard phase deadline, Main abandons channel ownership to a late disposer; neutralization may be blocked until completion or process exit.",
            "If a noncooperative WinUSB command crosses its hard ownership deadline, only its late owner may dispose the command channel; Player LED neutralization may remain blocked and unconfirmed.",
            nameof(Switch2UsbCommandResponseStyle.OriginalCapture10_78),
            nameof(Switch2UsbCommandResponseStyle.InitializedHardware00_F8),
        };
        foreach (string failureCode in
                 Enum.GetNames<VerificationFailureCode>())
        {
            values.Add(failureCode);
        }
        foreach (string failureDetail in
                 Enum.GetNames<Switch2UsbCommandFailure>())
        {
            values.Add(failureDetail);
        }
        foreach (string transferStage in
                 Enum.GetNames<CommandTransferFailureStage>())
        {
            values.Add(transferStage);
        }
        return values;
    }

    private static bool LooksLikePrivateIdentifier(string value)
    {
        if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(@"USB\", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(@"HID\", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("VID_", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("PID_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Length != 17 || value[2] is not (':' or '-'))
        {
            return false;
        }
        char separator = value[2];
        for (int index = 0; index < value.Length; index++)
        {
            if (index % 3 == 2)
            {
                if (value[index] != separator)
                {
                    return false;
                }
            }
            else if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }
        return true;
    }
}
