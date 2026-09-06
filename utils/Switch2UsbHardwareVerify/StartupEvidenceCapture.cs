using System.Text.Json;
using System.Text.Json.Serialization;
using DS4Windows.Switch2;

namespace DS4Windows.Switch2.Verification;

internal enum StartupEvidenceCommandKind
{
    EnableUsbHidReports,
    SetFeatureMask,
    EnableFeatures,
    SelectCommonInputReport,
    PlayerLed1,
    PlayerLedAllOff,
}

internal enum StartupEvidenceValidationDisposition
{
    TransferIncomplete,
    ExistingValidatorAccepted,
    ExistingValidatorRejected,
    RawObservationOnlyNoFeatureResponseValidator,
}

internal enum StartupEvidenceFailureCode
{
    InvalidPlan,
    DiscoveryFailed,
    DiscoveryTimedOut,
    SessionIdentityRevalidationFailed,
    HidInputOpenFailed,
    HidInputOpenTimedOut,
    CommandOpenFailed,
    CommandOpenTimedOut,
    InteractionTimedOut,
    CommandOperationTimedOut,
    CommandTransferFailed,
    CommandResponseRejected,
    InputCaptureFailed,
    InputCaptureTimedOut,
    PlayerLedFailed,
    Cancelled,
    CleanupIncomplete,
    UnexpectedFailure,
}

internal enum StartupEvidenceAcquisitionPhase
{
    Discovery,
    HidInputOpen,
    CommandOpen,
}

/// <summary>
/// One exact host-side command observation. The closed feature forms retain
/// their production-codec result, but the observation deliberately has no
/// conversion to a production full-duplex or registration proof.
/// </summary>
internal sealed class CommandWireObservation
{
    internal CommandWireObservation(StartupEvidenceCommandKind operation,
        ReadOnlySpan<byte> request, ReadOnlySpan<byte> response,
        bool? existingValidatorAccepted,
        Switch2UsbCommandFailure? existingValidatorFailure)
    {
        Operation = operation;
        Request = request.ToArray();
        Response = response.ToArray();
        ExistingValidatorAccepted = existingValidatorAccepted;
        ExistingValidatorFailure = existingValidatorFailure;
    }

    internal StartupEvidenceCommandKind Operation { get; }
    internal byte[] Request { get; }
    internal byte[] Response { get; }
    internal bool? ExistingValidatorAccepted { get; }
    internal Switch2UsbCommandFailure? ExistingValidatorFailure { get; }
}

internal static class StartupEvidenceCapturePlan
{
    internal const int SchemaVersion = 2;
    internal const string Tool = "Switch2UsbHardwareVerify";
    internal const string Procedure =
        "fixed-switch2-pro-usb-bcd0201-startup-evidence-v2";
    internal const string SuccessScope =
        "bounded host procedure and exact allowlisted response capture completed; no general protocol semantics, production full-duplex lease, or end-to-end product proof is established";
    internal const string ArtifactClassification =
        "local laboratory evidence with closed source-reviewed response tuples; automatic commit or publication remains disabled";
    internal const string HostBoundary =
        "one exclusive MI_01 lifetime; for each ordinal: flush bulk IN 0x82, write the exact request once to bulk OUT 0x02, then perform one bounded bulk IN read";
    internal const int DiscoveryTimeoutMilliseconds = 1_500;
    internal const int ChannelOpenTimeoutMilliseconds = 1_500;
    internal const int WholeInteractionTimeoutMilliseconds = 30_000;
    internal const int FeatureResponseReadMaximum =
        VerificationPlan.BulkMaximumPacketSize;

    internal static readonly string[] RedactionManifest =
    [
        "The tool does not intentionally query or add Windows device paths, instance identifiers, containers, serials, addresses, association data, or pairing data to the artifact.",
        "All serialized response bytes must match a closed source-reviewed command tuple; arbitrary response bytes are rejected.",
        "Raw input reports and individual input counters are not serialized.",
        "Only six closed volatile command request/response observations may be serialized as uppercase hexadecimal.",
        "Exception text and native identifiers are never serialized.",
    ];

    internal static readonly string[] Limitations =
    [
        "The command ordinal proves only one host-side exclusive MI_01 flush/write/read sequence; the protocol has no admitted transaction identifier.",
        "Feature responses are admitted only as the exact 12-byte tuples captured from 057E:2069 bcdDevice 0x0201; the validator does not generalize them to another firmware, transport, mask, or command.",
        "Input timing is host completion cadence, not calibrated input latency or proof of a nominal report rate.",
        "Player LED AllOff is explicit volatile neutralization, not restoration of an unknown prior LED state.",
        "No source-backed inverse for the feature-mask/enable commands is established; their session configuration may remain for the current connection until another owner reconfigures it or the controller disconnects.",
        "Native discovery, open, cancel, free, and close calls have no hard Win32 wall-clock guarantee; caller admission and ownership handoff are bounded and late resources are never reused.",
        "MI_00 is opened with read access while sharing both read and write; this permits existing observers or output-capable handles and does not prove that the production full-duplex MI_00 owner can acquire its read/write lease.",
        "This dormant utility mode is not production registration, association, pairing, persistent configuration, firmware, calibration, or hardware-safety validation.",
    ];

    internal static readonly StartupEvidenceCommandKind[] StartupOrder =
    [
        StartupEvidenceCommandKind.EnableUsbHidReports,
        StartupEvidenceCommandKind.SetFeatureMask,
        StartupEvidenceCommandKind.EnableFeatures,
        StartupEvidenceCommandKind.SelectCommonInputReport,
    ];

    internal static bool TryValidate(out string failure)
    {
        if (!VerificationPlan.TryValidate(out failure) ||
            WholeInteractionTimeoutMilliseconds <=
                VerificationPlan.InputCaptureTimeoutMilliseconds ||
            DiscoveryTimeoutMilliseconds <= 0 ||
            ChannelOpenTimeoutMilliseconds <= 0 ||
            FeatureResponseReadMaximum != 64 ||
            VerificationPlan.LiveHapticMutationSafetyGateOpen)
        {
            failure = "fixed-bounds-or-haptic-gate";
            return false;
        }

        foreach (StartupEvidenceCommandKind operation in StartupOrder)
        {
            if (!TryCreateRequest(operation, out byte[] request) ||
                request.Length != Switch2UsbCommandCodec.FeatureRequestLength)
            {
                failure = "startup-request";
                return false;
            }
        }
        if (!TryCreateRequest(StartupEvidenceCommandKind.PlayerLed1,
                out byte[] playerOne) ||
            !TryCreateRequest(StartupEvidenceCommandKind.PlayerLedAllOff,
                out byte[] allOff) ||
            playerOne.Length != Switch2UsbCommandCodec.RequestLength ||
            allOff.Length != Switch2UsbCommandCodec.RequestLength)
        {
            failure = "led-request";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    internal static bool TryCreateRequest(
        StartupEvidenceCommandKind operation, out byte[] request)
    {
        switch (operation)
        {
            case StartupEvidenceCommandKind.EnableUsbHidReports:
                request = new byte[
                    Switch2UsbCommandCodec.InitializationRequestLength];
                return Switch2UsbCommandCodec.TryWriteInitializationRequest(
                        Switch2UsbInitializationStep.EnableUsbHidReports,
                        request, out _) &&
                    Switch2UsbCommandCodec.TryValidateInitializationRequest(
                        request,
                        Switch2UsbInitializationStep.EnableUsbHidReports,
                        out _);
            case StartupEvidenceCommandKind.SetFeatureMask:
                request = new byte[
                    Switch2UsbCommandCodec.FeatureRequestLength];
                return Switch2UsbCommandCodec.TryWriteFeatureRequest(
                        Switch2UsbFeatureStep.SetFeatureMask,
                        Switch2UsbFeatureMask.ButtonsSticksImuAndRumble,
                        request, out _) &&
                    Switch2UsbCommandCodec.TryValidateFeatureRequest(request,
                        Switch2UsbFeatureStep.SetFeatureMask,
                        Switch2UsbFeatureMask.ButtonsSticksImuAndRumble,
                        out _);
            case StartupEvidenceCommandKind.EnableFeatures:
                request = new byte[
                    Switch2UsbCommandCodec.FeatureRequestLength];
                return Switch2UsbCommandCodec.TryWriteFeatureRequest(
                        Switch2UsbFeatureStep.EnableFeatures,
                        Switch2UsbFeatureMask.ButtonsSticksImuAndRumble,
                        request, out _) &&
                    Switch2UsbCommandCodec.TryValidateFeatureRequest(request,
                        Switch2UsbFeatureStep.EnableFeatures,
                        Switch2UsbFeatureMask.ButtonsSticksImuAndRumble,
                        out _);
            case StartupEvidenceCommandKind.SelectCommonInputReport:
                request = new byte[
                    Switch2UsbCommandCodec.InitializationRequestLength];
                return Switch2UsbCommandCodec.TryWriteInitializationRequest(
                        Switch2UsbInitializationStep.SelectCommonInputReport,
                        request, out _) &&
                    Switch2UsbCommandCodec.TryValidateInitializationRequest(
                        request,
                        Switch2UsbInitializationStep.SelectCommonInputReport,
                        out _);
            case StartupEvidenceCommandKind.PlayerLed1:
                request = new byte[Switch2UsbCommandCodec.RequestLength];
                return Switch2UsbCommandCodec.TryWritePlayerLedRequest(
                        Switch2PlayerLedCommand.Player1Only, request, out _) &&
                    Switch2UsbCommandCodec.TryValidatePlayerLedRequest(request,
                        Switch2PlayerLedCommand.Player1Only, out _);
            case StartupEvidenceCommandKind.PlayerLedAllOff:
                request = new byte[Switch2UsbCommandCodec.RequestLength];
                return Switch2UsbCommandCodec.TryWritePlayerLedRequest(
                        Switch2PlayerLedCommand.AllOff, request, out _) &&
                    Switch2UsbCommandCodec.TryValidatePlayerLedRequest(request,
                        Switch2PlayerLedCommand.AllOff, out _);
            default:
                request = [];
                return false;
        }
    }

    internal static bool HasFeatureResponseValidator(
        StartupEvidenceCommandKind operation) => IsFeatureOperation(operation);

    internal static bool IsFeatureOperation(
        StartupEvidenceCommandKind operation) =>
        operation is StartupEvidenceCommandKind.SetFeatureMask or
            StartupEvidenceCommandKind.EnableFeatures;

    internal static bool IsExistingValidatorOperation(
        StartupEvidenceCommandKind operation) =>
        Enum.IsDefined(operation);
}

internal sealed class StartupEvidenceCaptureResult
{
    public int SchemaVersion { get; init; } =
        StartupEvidenceCapturePlan.SchemaVersion;
    public string Tool { get; init; } = StartupEvidenceCapturePlan.Tool;
    public string Procedure { get; init; } =
        StartupEvidenceCapturePlan.Procedure;
    public required string VerifierAssemblySha256 { get; init; }
    public bool Success { get; set; }
    public StartupEvidenceFailureCode? FailureCode { get; set; }
    public StartupEvidenceFailureCode? ProcedureFailureCode { get; set; }
    public VerificationFailureCode? HardwareFailureCode { get; set; }
    public int? HardwareFailureWin32ErrorCode { get; set; }
    public StartupEvidenceAcquisitionFailure? AcquisitionFailure { get; set; }
    public string SuccessScope { get; init; } =
        StartupEvidenceCapturePlan.SuccessScope;
    public string ArtifactClassification { get; init; } =
        StartupEvidenceCapturePlan.ArtifactClassification;
    public bool OpaqueFeatureResponseBytesMayContainUnclassifiedData
    {
        get; init;
    }
    public bool AutomaticCommitOrShareAllowed { get; init; }
    public StartupEvidencePublicTarget Target { get; init; } = new();
    public StartupEvidenceBounds Bounds { get; init; } = new();
    public StartupEvidenceCausality Causality { get; init; } = new();
    public List<StartupEvidenceCommandAttempt> Commands { get; init; } = [];
    public InputRateResult InputRate { get; init; } = new();
    public StartupEvidenceHapticResult Haptics { get; init; } = new();
    public StartupEvidenceCleanupResult Cleanup { get; init; } = new();
    public string[] RedactionManifest { get; init; } =
        [.. StartupEvidenceCapturePlan.RedactionManifest];
    public string[] Limitations { get; init; } =
        [.. StartupEvidenceCapturePlan.Limitations];

    internal string ToJson() => JsonSerializer.Serialize(this,
        StartupEvidenceJsonContext.Default.StartupEvidenceCaptureResult);
}

/// <summary>
/// A deliberately narrow diagnostic copied from a typed verifier exception.
/// It contains no exception text, path, instance id, container id, address,
/// or other native identifier.
/// </summary>
internal sealed class StartupEvidenceAcquisitionFailure
{
    public StartupEvidenceAcquisitionPhase Phase { get; init; }
    public VerificationFailureCode Code { get; init; }
    public int? Win32ErrorCode { get; init; }
}

internal sealed class StartupEvidencePublicTarget
{
    public string Model { get; init; } = "Nintendo Switch 2 Pro Controller";
    public string VendorId { get; init; } = "0x057E";
    public string ProductId { get; init; } = "0x2069";
    public string DeviceReleaseBcd { get; init; } = "0x0201";
    public string HidInterface { get; init; } = "MI_00 read-only input";
    public string CommandInterface { get; init; } =
        "MI_01 exclusive WinUSB";
    public bool HidOutputHandleOpened { get; init; }
}

internal sealed class StartupEvidenceBounds
{
    public int WholeInteractionMilliseconds { get; init; } =
        StartupEvidenceCapturePlan.WholeInteractionTimeoutMilliseconds;
    public int DiscoveryMilliseconds { get; init; } =
        StartupEvidenceCapturePlan.DiscoveryTimeoutMilliseconds;
    public int ChannelOpenMilliseconds { get; init; } =
        StartupEvidenceCapturePlan.ChannelOpenTimeoutMilliseconds;
    public int CommandOperationMilliseconds { get; init; } =
        VerificationPlan.CommandOperationTimeoutMilliseconds;
    public int InputCaptureMilliseconds { get; init; } =
        VerificationPlan.InputCaptureTimeoutMilliseconds;
    public int LedCleanupMilliseconds { get; init; } =
        VerificationPlan.LedCleanupTimeoutMilliseconds;
    public int ChannelDisposeMilliseconds { get; init; } =
        VerificationPlan.ChannelDisposeTimeoutMilliseconds;
    public int FeatureResponseMaximumBytes { get; init; } =
        StartupEvidenceCapturePlan.FeatureResponseReadMaximum;
}

internal sealed class StartupEvidenceCausality
{
    public string HostBoundary { get; init; } =
        StartupEvidenceCapturePlan.HostBoundary;
    public bool ProtocolTransactionIdentifierPresent { get; init; }
    public bool FeatureResponseSemanticAcknowledgementEstablished { get; init; }
    public bool RawObservationsAutomaticallyAdmittedAsValidator { get; init; }
}

internal sealed class StartupEvidenceCommandAttempt
{
    public int Ordinal { get; init; }
    public StartupEvidenceCommandKind Operation { get; init; }
    public string RequestHex { get; init; } = string.Empty;
    public bool HostTransferCompleted { get; set; }
    public int? ResponseLength { get; set; }
    public string? ResponseHex { get; set; }
    public StartupEvidenceValidationDisposition ValidationDisposition
    {
        get; set;
    } = StartupEvidenceValidationDisposition.TransferIncomplete;
    public Switch2UsbCommandFailure? ExistingValidatorFailure { get; set; }
    public CommandTransferFailureStage? TransferFailureStage { get; set; }
    public bool SemanticAcknowledgementEstablished { get; init; }
    public bool EligibleForProductionStartupProof { get; init; }
}

internal sealed class StartupEvidenceHapticResult
{
    public bool NonzeroHapticsHardDisabled { get; init; } = true;
    public bool HidOutputHandleOpened { get; init; }
    public int NonzeroWritesAttempted { get; init; }
    public int ZeroWritesAttempted { get; init; }
}

internal sealed class StartupEvidenceCleanupResult
{
    public bool FeatureConfigurationExplicitlyReverted { get; init; }
    public bool FeatureConfigurationMayRemainForCurrentConnection
    {
        get; init;
    } = true;
    public bool PlayerLedAllOffRequired { get; set; }
    public bool PlayerLedAllOffAttempted { get; set; }
    public bool PlayerLedAllOffExactResponseValidated { get; set; }
    public bool PlayerLedAllOffSucceeded { get; set; }
    public bool PlayerLedNeutralizationBlockedByOwnership { get; set; }
    public bool CommandOwnershipAbandoned { get; set; }
    public bool InputOwnershipAbandoned { get; set; }
    public bool LateCommandReleaseUnconfirmed { get; set; }
    public bool LateInputReleaseUnconfirmed { get; set; }
    public bool CommandDisposeFailed { get; set; }
    public bool InputDisposeFailed { get; set; }
}

internal readonly record struct StartupEvidenceExpectedOutcome(
    bool CleanupSucceeded, bool Success,
    StartupEvidenceFailureCode? FailureCode);

internal static class StartupEvidenceOutcomePolicy
{
    internal static StartupEvidenceExpectedOutcome Evaluate(
        StartupEvidenceCaptureResult result, bool procedureSucceeded)
    {
        ArgumentNullException.ThrowIfNull(result);
        bool cleanupSucceeded =
            (!result.Cleanup.PlayerLedAllOffRequired ||
                result.Cleanup.PlayerLedAllOffSucceeded) &&
            !result.Cleanup.CommandOwnershipAbandoned &&
            !result.Cleanup.InputOwnershipAbandoned &&
            !result.Cleanup.LateCommandReleaseUnconfirmed &&
            !result.Cleanup.LateInputReleaseUnconfirmed &&
            !result.Cleanup.CommandDisposeFailed &&
            !result.Cleanup.InputDisposeFailed;
        return new StartupEvidenceExpectedOutcome(cleanupSucceeded,
            procedureSucceeded && cleanupSucceeded,
            cleanupSucceeded ? result.ProcedureFailureCode :
                StartupEvidenceFailureCode.CleanupIncomplete);
    }
}

internal sealed class StartupEvidenceRecorder
{
    private static readonly StartupEvidenceCommandKind[] OrderedOperations =
    [
        .. StartupEvidenceCapturePlan.StartupOrder,
        StartupEvidenceCommandKind.PlayerLed1,
        StartupEvidenceCommandKind.PlayerLedAllOff,
    ];
    private readonly StartupEvidenceCaptureResult result;

    internal StartupEvidenceRecorder(StartupEvidenceCaptureResult result)
    {
        this.result = result ?? throw new ArgumentNullException(nameof(result));
    }

    internal StartupEvidenceCommandAttempt Begin(
        StartupEvidenceCommandKind operation)
    {
        int ordinal = result.Commands.Count + 1;
        StartupEvidenceCommandAttempt? previous =
            result.Commands.LastOrDefault();
        bool cleanupAfterPlayer =
            operation == StartupEvidenceCommandKind.PlayerLedAllOff &&
            previous?.Operation == StartupEvidenceCommandKind.PlayerLed1;
        bool priorAdmitted = previous is null ||
            previous.HostTransferCompleted &&
            previous.ValidationDisposition ==
                StartupEvidenceValidationDisposition.
                    ExistingValidatorAccepted;
        if (ordinal > OrderedOperations.Length ||
            OrderedOperations[ordinal - 1] != operation ||
            (!priorAdmitted && !cleanupAfterPlayer) ||
            !StartupEvidenceCapturePlan.TryCreateRequest(operation,
                out byte[] request))
        {
            throw new InvalidOperationException("evidence-command-order");
        }

        var attempt = new StartupEvidenceCommandAttempt
        {
            Ordinal = ordinal,
            Operation = operation,
            RequestHex = Convert.ToHexString(request),
        };
        result.Commands.Add(attempt);
        if (operation == StartupEvidenceCommandKind.PlayerLed1)
        {
            // Arm AllOff before the mutation-capable dependency is called.
            result.Cleanup.PlayerLedAllOffRequired = true;
        }
        else if (operation == StartupEvidenceCommandKind.PlayerLedAllOff)
        {
            result.Cleanup.PlayerLedAllOffAttempted = true;
        }
        return attempt;
    }

    internal bool TryComplete(StartupEvidenceCommandAttempt attempt,
        CommandWireObservation observation)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(observation);
        if (!ReferenceEquals(result.Commands.LastOrDefault(), attempt) ||
            attempt.HostTransferCompleted ||
            attempt.Operation != observation.Operation ||
            !StartupEvidenceCapturePlan.TryCreateRequest(attempt.Operation,
                out byte[] expectedRequest) ||
            !observation.Request.AsSpan().SequenceEqual(expectedRequest) ||
            observation.Response.Length is <= 0 or >
                StartupEvidenceCapturePlan.FeatureResponseReadMaximum)
        {
            return false;
        }

        if (observation.ExistingValidatorAccepted is not bool accepted ||
            (accepted && observation.ExistingValidatorFailure is not null) ||
            (!accepted && observation.ExistingValidatorFailure is null or
                Switch2UsbCommandFailure.None))
        {
            return false;
        }
        attempt.ValidationDisposition = accepted ?
            StartupEvidenceValidationDisposition.ExistingValidatorAccepted :
            StartupEvidenceValidationDisposition.ExistingValidatorRejected;
        attempt.ExistingValidatorFailure =
            observation.ExistingValidatorFailure;

        attempt.ResponseLength = observation.Response.Length;
        attempt.ResponseHex = Convert.ToHexString(observation.Response);
        attempt.HostTransferCompleted = true;
        return true;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(StartupEvidenceCaptureResult))]
internal partial class StartupEvidenceJsonContext : JsonSerializerContext
{
}

internal static class StartupEvidencePrivateArtifactValidator
{
    /// <summary>
    /// Proves the closed private-artifact schema, revalidates each response
    /// through the production codec, and rejects known host identifiers. It is
    /// deliberately not represented as a general privacy proof.
    /// </summary>
    internal static bool IsClosedSchemaPrivateArtifact(string? json)
    {
        if (json is null)
        {
            return false;
        }
        try
        {
            StartupEvidenceCaptureResult? result = JsonSerializer.Deserialize(
                json, StartupEvidenceJsonContext.Default.
                    StartupEvidenceCaptureResult);
            if (result is null ||
                !string.Equals(json, result.ToJson(),
                    StringComparison.Ordinal) ||
                !TryValidateSemanticResult(result))
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
            return HasNoDuplicatePropertiesOrPrivateStrings(
                document.RootElement);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryValidateSemanticResult(
        StartupEvidenceCaptureResult result)
    {
        var canonicalInput = new InputRateResult();
        if (!IsDefinedNullable(result.FailureCode) ||
            !IsDefinedNullable(result.ProcedureFailureCode) ||
            result.ProcedureFailureCode ==
                StartupEvidenceFailureCode.CleanupIncomplete ||
            result.SchemaVersion != StartupEvidenceCapturePlan.SchemaVersion ||
            result.Tool != StartupEvidenceCapturePlan.Tool ||
            result.Procedure != StartupEvidenceCapturePlan.Procedure ||
            result.SuccessScope != StartupEvidenceCapturePlan.SuccessScope ||
            result.ArtifactClassification !=
                StartupEvidenceCapturePlan.ArtifactClassification ||
            !IsUpperHex(result.VerifierAssemblySha256, 32) ||
            result.Target.Model != "Nintendo Switch 2 Pro Controller" ||
            result.Target.VendorId != "0x057E" ||
            result.Target.ProductId != "0x2069" ||
            result.Target.DeviceReleaseBcd != "0x0201" ||
            result.Target.HidInterface != "MI_00 read-only input" ||
            result.Target.CommandInterface != "MI_01 exclusive WinUSB" ||
            result.OpaqueFeatureResponseBytesMayContainUnclassifiedData ||
            result.AutomaticCommitOrShareAllowed ||
            result.Target.HidOutputHandleOpened ||
            result.Bounds.WholeInteractionMilliseconds !=
                StartupEvidenceCapturePlan.WholeInteractionTimeoutMilliseconds ||
            result.Bounds.DiscoveryMilliseconds !=
                StartupEvidenceCapturePlan.DiscoveryTimeoutMilliseconds ||
            result.Bounds.ChannelOpenMilliseconds !=
                StartupEvidenceCapturePlan.ChannelOpenTimeoutMilliseconds ||
            result.Bounds.CommandOperationMilliseconds !=
                VerificationPlan.CommandOperationTimeoutMilliseconds ||
            result.Bounds.InputCaptureMilliseconds !=
                VerificationPlan.InputCaptureTimeoutMilliseconds ||
            result.Bounds.LedCleanupMilliseconds !=
                VerificationPlan.LedCleanupTimeoutMilliseconds ||
            result.Bounds.ChannelDisposeMilliseconds !=
                VerificationPlan.ChannelDisposeTimeoutMilliseconds ||
            result.Bounds.FeatureResponseMaximumBytes !=
                StartupEvidenceCapturePlan.FeatureResponseReadMaximum ||
            result.Causality.HostBoundary !=
                StartupEvidenceCapturePlan.HostBoundary ||
            result.Causality.ProtocolTransactionIdentifierPresent ||
            result.Causality.FeatureResponseSemanticAcknowledgementEstablished ||
            result.Causality.RawObservationsAutomaticallyAdmittedAsValidator ||
            !result.RedactionManifest.SequenceEqual(
                StartupEvidenceCapturePlan.RedactionManifest,
                StringComparer.Ordinal) ||
            !result.Limitations.SequenceEqual(
                StartupEvidenceCapturePlan.Limitations,
                StringComparer.Ordinal) ||
            !result.Haptics.NonzeroHapticsHardDisabled ||
            result.Haptics.HidOutputHandleOpened ||
            result.Haptics.NonzeroWritesAttempted != 0 ||
            result.Haptics.ZeroWritesAttempted != 0 ||
            result.Cleanup.FeatureConfigurationExplicitlyReverted ||
            !result.Cleanup
                .FeatureConfigurationMayRemainForCurrentConnection ||
            result.Commands.Count > 6)
        {
            return false;
        }

        if (!IsAcquisitionFailureValid(result))
        {
            return false;
        }
        if (!IsHardwareFailureValid(result))
        {
            return false;
        }

        if (result.InputRate.WarmupReports != canonicalInput.WarmupReports ||
            result.InputRate.RequestedReports !=
                canonicalInput.RequestedReports ||
            result.InputRate.WholePhaseDeadlineMilliseconds !=
                canonicalInput.WholePhaseDeadlineMilliseconds ||
            result.InputRate.RequiredReportId !=
                canonicalInput.RequiredReportId ||
            result.InputRate.CounterScope != canonicalInput.CounterScope ||
            result.InputRate.TimingScope != canonicalInput.TimingScope ||
            result.InputRate.ExactReports is not (0 or
                VerificationPlan.InputReportCount) ||
            result.InputRate.CounterForwardMovements is < 0 or >
                (VerificationPlan.InputReportCount - 1) ||
            result.InputRate.CounterPlusFourMovements is < 0 or >
                (VerificationPlan.InputReportCount - 1))
        {
            return false;
        }
        bool inputCompleted = result.InputRate.ExactReports ==
            VerificationPlan.InputReportCount;
        if (inputCompleted !=
                (result.InputRate.ObservedReportsPerSecond is > 0) ||
            inputCompleted !=
                (result.InputRate.MeanIntervalMilliseconds is > 0) ||
            inputCompleted !=
                (result.InputRate.P50IntervalMilliseconds is > 0) ||
            inputCompleted !=
                (result.InputRate.P95IntervalMilliseconds is > 0) ||
            inputCompleted !=
                (result.InputRate.P99IntervalMilliseconds is > 0) ||
            inputCompleted !=
                (result.InputRate.CounterMinimumDelta is > 0) ||
            inputCompleted !=
                (result.InputRate.CounterMaximumDelta is > 0) ||
            inputCompleted &&
                (result.InputRate.CounterForwardMovements !=
                    VerificationPlan.InputReportCount - 1 ||
                 result.InputRate.CounterMinimumDelta >
                    result.InputRate.CounterMaximumDelta))
        {
            return false;
        }

        for (int index = 0; index < result.Commands.Count; index++)
        {
            StartupEvidenceCommandAttempt attempt = result.Commands[index];
            if (!Enum.IsDefined(attempt.Operation) ||
                !Enum.IsDefined(attempt.ValidationDisposition) ||
                !IsDefinedNullable(attempt.ExistingValidatorFailure) ||
                !IsDefinedNullable(attempt.TransferFailureStage))
            {
                return false;
            }
            StartupEvidenceCommandKind expected = index switch
            {
                0 => StartupEvidenceCommandKind.EnableUsbHidReports,
                1 => StartupEvidenceCommandKind.SetFeatureMask,
                2 => StartupEvidenceCommandKind.EnableFeatures,
                3 => StartupEvidenceCommandKind.SelectCommonInputReport,
                4 => StartupEvidenceCommandKind.PlayerLed1,
                5 => StartupEvidenceCommandKind.PlayerLedAllOff,
                _ => throw new InvalidOperationException(),
            };
            if (attempt.Ordinal != index + 1 ||
                attempt.Operation != expected ||
                !StartupEvidenceCapturePlan.TryCreateRequest(expected,
                    out byte[] request) ||
                attempt.RequestHex != Convert.ToHexString(request) ||
                attempt.SemanticAcknowledgementEstablished ||
                attempt.EligibleForProductionStartupProof)
            {
                return false;
            }

            if (!attempt.HostTransferCompleted)
            {
                if (attempt.ResponseLength is not null ||
                    attempt.ResponseHex is not null ||
                    attempt.ValidationDisposition !=
                        StartupEvidenceValidationDisposition.
                            TransferIncomplete ||
                    attempt.ExistingValidatorFailure is not null)
                {
                    return false;
                }
                continue;
            }
            if (attempt.ResponseLength is not int responseLength ||
                responseLength is <= 0 or > 64 ||
                attempt.ResponseHex is null ||
                !IsUpperHex(attempt.ResponseHex, responseLength))
            {
                return false;
            }
            if (attempt.TransferFailureStage is not null)
            {
                return false;
            }

            if (StartupEvidenceCapturePlan.IsFeatureOperation(expected))
            {
                if (attempt.ValidationDisposition !=
                        StartupEvidenceValidationDisposition.
                            ExistingValidatorAccepted ||
                    attempt.ExistingValidatorFailure is not null ||
                    !Switch2UsbCommandCodec.TryValidateFeatureResponse(
                        Convert.FromHexString(attempt.ResponseHex),
                        expected ==
                            StartupEvidenceCommandKind.SetFeatureMask ?
                            Switch2UsbFeatureStep.SetFeatureMask :
                            Switch2UsbFeatureStep.EnableFeatures, out _))
                {
                    return false;
                }
            }
            else if (attempt.ValidationDisposition is not
                         (StartupEvidenceValidationDisposition.
                              ExistingValidatorAccepted or
                          StartupEvidenceValidationDisposition.
                              ExistingValidatorRejected))
            {
                return false;
            }
            else if (attempt.ValidationDisposition ==
                         StartupEvidenceValidationDisposition.
                             ExistingValidatorAccepted &&
                     attempt.ExistingValidatorFailure is not null ||
                     attempt.ValidationDisposition ==
                         StartupEvidenceValidationDisposition.
                             ExistingValidatorRejected &&
                     attempt.ExistingValidatorFailure is null or
                         Switch2UsbCommandFailure.None)
            {
                return false;
            }
        }

        bool hasPlayerAttempt = result.Commands.Count >= 5;
        bool hasAllOffAttempt = result.Commands.Count >= 6;
        if (result.Cleanup.PlayerLedAllOffRequired != hasPlayerAttempt ||
            result.Cleanup.PlayerLedAllOffAttempted != hasAllOffAttempt ||
            result.Cleanup.PlayerLedAllOffExactResponseValidated !=
                result.Cleanup.PlayerLedAllOffSucceeded ||
            result.Cleanup.PlayerLedAllOffSucceeded &&
                (!hasAllOffAttempt ||
                 result.Commands[5].ValidationDisposition !=
                    StartupEvidenceValidationDisposition.
                        ExistingValidatorAccepted) ||
            result.Cleanup.PlayerLedNeutralizationBlockedByOwnership &&
                result.Cleanup.PlayerLedAllOffSucceeded ||
            result.Cleanup.CommandOwnershipAbandoned &&
                !result.Cleanup.LateCommandReleaseUnconfirmed ||
            result.Cleanup.InputOwnershipAbandoned &&
                !result.Cleanup.LateInputReleaseUnconfirmed)
        {
            return false;
        }

        bool primaryEvidenceSucceeded = result.Commands.Count >= 5 &&
            result.Commands.Take(5).All(attempt =>
                attempt.HostTransferCompleted &&
                attempt.ValidationDisposition ==
                    StartupEvidenceValidationDisposition.
                        ExistingValidatorAccepted) &&
            inputCompleted;
        bool procedureSucceeded = result.ProcedureFailureCode is null;
        if (procedureSucceeded != primaryEvidenceSucceeded)
        {
            return false;
        }

        StartupEvidenceExpectedOutcome expectedOutcome =
            StartupEvidenceOutcomePolicy.Evaluate(result,
                procedureSucceeded);
        if (result.FailureCode != expectedOutcome.FailureCode ||
            result.Success != expectedOutcome.Success)
        {
            return false;
        }

        if (result.Success)
        {
            if (result.Commands.Count != 6 ||
                result.Commands.Any(attempt =>
                    !attempt.HostTransferCompleted) ||
                result.Commands.Any(attempt =>
                    attempt.ValidationDisposition !=
                        StartupEvidenceValidationDisposition.
                            ExistingValidatorAccepted) ||
                result.InputRate.ExactReports !=
                    VerificationPlan.InputReportCount ||
                !result.Cleanup.PlayerLedAllOffSucceeded ||
                result.Cleanup.CommandOwnershipAbandoned ||
                result.Cleanup.InputOwnershipAbandoned ||
                result.Cleanup.LateCommandReleaseUnconfirmed ||
                result.Cleanup.LateInputReleaseUnconfirmed ||
                result.Cleanup.CommandDisposeFailed ||
                result.Cleanup.InputDisposeFailed)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAcquisitionFailureValid(
        StartupEvidenceCaptureResult result)
    {
        StartupEvidenceAcquisitionFailure? failure =
            result.AcquisitionFailure;
        if (failure is null)
        {
            return true;
        }
        if (!Enum.IsDefined(failure.Phase) ||
            !Enum.IsDefined(failure.Code) ||
            failure.Win32ErrorCode is <= 0 ||
            result.HardwareFailureCode != failure.Code ||
            result.HardwareFailureWin32ErrorCode != failure.Win32ErrorCode)
        {
            return false;
        }

        return failure.Phase switch
        {
            StartupEvidenceAcquisitionPhase.Discovery =>
                result.ProcedureFailureCode ==
                    StartupEvidenceFailureCode.DiscoveryFailed &&
                IsDiscoveryFailure(failure.Code),
            StartupEvidenceAcquisitionPhase.HidInputOpen =>
                result.ProcedureFailureCode ==
                    StartupEvidenceFailureCode.HidInputOpenFailed &&
                failure.Code == VerificationFailureCode.HidReadOpenFailed,
            StartupEvidenceAcquisitionPhase.CommandOpen =>
                result.ProcedureFailureCode ==
                    StartupEvidenceFailureCode.CommandOpenFailed &&
                IsCommandOpenFailure(failure.Code),
            _ => false,
        };
    }

    private static bool IsHardwareFailureValid(
        StartupEvidenceCaptureResult result)
    {
        if (result.HardwareFailureCode is not VerificationFailureCode code)
        {
            return result.HardwareFailureWin32ErrorCode is null &&
                result.AcquisitionFailure is null;
        }
        if (!Enum.IsDefined(code) ||
            result.HardwareFailureWin32ErrorCode is <= 0)
        {
            return false;
        }

        StartupEvidenceFailureCode? expected = code switch
        {
            VerificationFailureCode.CommandOperationTimedOut =>
                StartupEvidenceFailureCode.CommandOperationTimedOut,
            VerificationFailureCode.CommandResponseInvalid =>
                StartupEvidenceFailureCode.CommandResponseRejected,
            VerificationFailureCode.CommandTransferFailed =>
                StartupEvidenceFailureCode.CommandTransferFailed,
            VerificationFailureCode.InputCapturePhaseTimedOut =>
                StartupEvidenceFailureCode.InputCaptureTimedOut,
            VerificationFailureCode.InputReadFailed or
                VerificationFailureCode.InputReportInvalid or
                VerificationFailureCode.InputCounterInvalid or
                VerificationFailureCode.InputBacklogNotDrained =>
                StartupEvidenceFailureCode.InputCaptureFailed,
            VerificationFailureCode.HidReadOpenFailed =>
                StartupEvidenceFailureCode.HidInputOpenFailed,
            VerificationFailureCode.WinUsbOpenFailed or
                VerificationFailureCode.WinUsbInitializeFailed or
                VerificationFailureCode.WinUsbAlternateSettingQueryFailed or
                VerificationFailureCode.WinUsbAlternateSettingMismatch or
                VerificationFailureCode.WinUsbInterfaceDescriptorQueryFailed or
                VerificationFailureCode.WinUsbInterfaceDescriptorMismatch or
                VerificationFailureCode.WinUsbPipeQueryFailed or
                VerificationFailureCode.WinUsbPipeTopologyMismatch or
                VerificationFailureCode.WinUsbPipePolicySetFailed or
                VerificationFailureCode.WinUsbPipePolicyReadFailed or
                VerificationFailureCode.WinUsbPipePolicyMismatch =>
                StartupEvidenceFailureCode.CommandOpenFailed,
            _ when IsDiscoveryFailure(code) =>
                StartupEvidenceFailureCode.DiscoveryFailed,
            _ => null,
        };
        return expected is not null &&
            result.ProcedureFailureCode == expected;
    }

    private static bool IsDiscoveryFailure(VerificationFailureCode code) =>
        code is VerificationFailureCode.HidClassSetOpenFailed or
            VerificationFailureCode.HidInterfaceIterationFailed or
            VerificationFailureCode.InterfaceDetailSizeQueryFailed or
            VerificationFailureCode.InterfaceDetailReadFailed or
            VerificationFailureCode.InterfacePathInvalid or
            VerificationFailureCode.DeviceInstanceIdReadFailed or
            VerificationFailureCode.DeviceParentLookupFailed or
            VerificationFailureCode.DeviceParentOpenFailed or
            VerificationFailureCode.DeviceContainerIdReadFailed or
            VerificationFailureCode.DeviceRegistryPropertyReadFailed or
            VerificationFailureCode.HidTargetCountNotOne or
            VerificationFailureCode.HidMetadataOpenFailed or
            VerificationFailureCode.HidIdentityChanged or
            VerificationFailureCode.HidReportTopologyMismatch or
            VerificationFailureCode.WinUsbNodeCountNotOne or
            VerificationFailureCode.WinUsbServiceMismatch or
            VerificationFailureCode.WinUsbInterfaceGuidMissing or
            VerificationFailureCode.WinUsbInterfaceGuidInvalid or
            VerificationFailureCode.WinUsbInterfaceClassSetOpenFailed or
            VerificationFailureCode.WinUsbInterfaceIterationFailed or
            VerificationFailureCode.WinUsbInterfacePathCountNotOne or
            VerificationFailureCode.WinUsbIdentityChanged;

    private static bool IsCommandOpenFailure(VerificationFailureCode code) =>
        code is VerificationFailureCode.WinUsbOpenFailed or
            VerificationFailureCode.WinUsbInitializeFailed or
            VerificationFailureCode.WinUsbAlternateSettingQueryFailed or
            VerificationFailureCode.WinUsbAlternateSettingMismatch or
            VerificationFailureCode.WinUsbInterfaceDescriptorQueryFailed or
            VerificationFailureCode.WinUsbInterfaceDescriptorMismatch or
            VerificationFailureCode.WinUsbPipeQueryFailed or
            VerificationFailureCode.WinUsbPipeTopologyMismatch or
            VerificationFailureCode.WinUsbPipePolicySetFailed or
            VerificationFailureCode.WinUsbPipePolicyReadFailed or
            VerificationFailureCode.WinUsbPipePolicyMismatch;

    private static bool IsDefinedNullable<TEnum>(TEnum? value)
        where TEnum : struct, Enum =>
        value is null || Enum.IsDefined(value.Value);

    private static bool HasNoDuplicatePropertiesOrPrivateStrings(
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) ||
                        !HasNoDuplicatePropertiesOrPrivateStrings(
                            property.Value))
                    {
                        return false;
                    }
                }
                return true;
            }
            case JsonValueKind.Array:
                return element.EnumerateArray().All(
                    HasNoDuplicatePropertiesOrPrivateStrings);
            case JsonValueKind.String:
            {
                string value = element.GetString() ?? string.Empty;
                return value.Length <= 1_024 &&
                    !value.Contains(@"\\?\", StringComparison.Ordinal) &&
                    !value.Contains(@"\\.\", StringComparison.Ordinal) &&
                    !value.Contains("USB\\VID_", StringComparison.OrdinalIgnoreCase) &&
                    !LooksLikeMacAddress(value);
            }
            default:
                return true;
        }
    }

    private static bool LooksLikeMacAddress(string value)
    {
        if (value.Length != 17)
        {
            return false;
        }
        char separator = value[2];
        if (separator is not (':' or '-'))
        {
            return false;
        }
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

    private static bool IsUpperHex(string value, int bytes)
    {
        if (value.Length != checked(bytes * 2))
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!(character is >= '0' and <= '9' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }
}
