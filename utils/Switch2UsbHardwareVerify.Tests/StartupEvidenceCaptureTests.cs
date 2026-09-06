using System.Text.Json;
using System.Text.Json.Nodes;
using DS4Windows.Switch2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Switch2.Verification.Tests;

[TestClass]
public sealed class StartupEvidenceCaptureTests
{
    [TestMethod]
    public void PlanPinsOnlyTheClosedStartupAndLedRequests()
    {
        Assert.IsTrue(StartupEvidenceCapturePlan.TryValidate(out _));
        Assert.AreEqual(0x00000003u, HidInputChannel.InputShareMode,
            "The read-only evidence handle must share existing write access instead of manufacturing an ownership failure.");
        CollectionAssert.AreEqual(new[]
        {
            StartupEvidenceCommandKind.EnableUsbHidReports,
            StartupEvidenceCommandKind.SetFeatureMask,
            StartupEvidenceCommandKind.EnableFeatures,
            StartupEvidenceCommandKind.SelectCommonInputReport,
        }, StartupEvidenceCapturePlan.StartupOrder);

        AssertRequest(StartupEvidenceCommandKind.EnableUsbHidReports,
            "039100030004000001000000");
        AssertRequest(StartupEvidenceCommandKind.SetFeatureMask,
            "0C9100020004000027000000");
        AssertRequest(StartupEvidenceCommandKind.EnableFeatures,
            "0C9100040004000027000000");
        AssertRequest(StartupEvidenceCommandKind.SelectCommonInputReport,
            "0391000A0004000005000000");
        AssertRequest(StartupEvidenceCommandKind.PlayerLed1,
            "0991000100000000");
        AssertRequest(StartupEvidenceCommandKind.PlayerLedAllOff,
            "0991000600000000");

        for (int value = -1; value <= 7; value++)
        {
            var operation = (StartupEvidenceCommandKind)value;
            if (Enum.IsDefined(operation))
            {
                continue;
            }
            Assert.IsFalse(StartupEvidenceCapturePlan.TryCreateRequest(
                operation, out byte[] rejected));
            Assert.AreEqual(0, rejected.Length);
        }
    }

    [TestMethod]
    public void FeatureResponsesRequireTheExactPinnedBcd0201Tuples()
    {
        foreach (StartupEvidenceCommandKind operation in new[]
                 {
                     StartupEvidenceCommandKind.SetFeatureMask,
                     StartupEvidenceCommandKind.EnableFeatures,
                 })
        {
            Assert.IsTrue(StartupEvidenceCapturePlan
                .HasFeatureResponseValidator(operation));
        }

        StartupEvidenceCaptureResult result = CreateSuccessfulResult();
        StartupEvidenceCommandAttempt feature = result.Commands[1];
        Assert.AreEqual(StartupEvidenceValidationDisposition
            .ExistingValidatorAccepted,
            feature.ValidationDisposition);
        Assert.IsFalse(feature.SemanticAcknowledgementEstablished);
        Assert.IsFalse(feature.EligibleForProductionStartupProof);
        Assert.IsFalse(result.Causality
            .RawObservationsAutomaticallyAdmittedAsValidator);
        Assert.IsFalse(result.Causality
            .FeatureResponseSemanticAcknowledgementEstablished);
        Assert.IsTrue(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(result.ToJson()));
    }

    [TestMethod]
    public void RawResponseAdmissionIsPacketBoundedAndCopiesOnlyTransferredBytes()
    {
        byte[] packet = Enumerable.Range(0, 64)
            .Select(value => checked((byte)value)).ToArray();
        Assert.IsTrue(RawCommandResponseAdmission.TryAdmit(packet, 7, 64,
            out byte[] response));
        CollectionAssert.AreEqual(packet[..7], response);
        packet[0] = 0xFF;
        Assert.AreEqual((byte)0, response[0],
            "The retained observation must not alias the native read buffer.");

        Assert.IsFalse(RawCommandResponseAdmission.TryAdmit(packet, 0, 64,
            out _));
        Assert.IsFalse(RawCommandResponseAdmission.TryAdmit(packet, 65, 64,
            out _));
        Assert.IsFalse(RawCommandResponseAdmission.TryAdmit(packet, 64, 65,
            out _));
        Assert.IsFalse(RawCommandResponseAdmission.TryAdmit(packet, 64, 0,
            out _));
    }

    [TestMethod]
    public void RecorderEnforcesExactOrderRequestAndValidationProvenance()
    {
        var incompleteResult = new StartupEvidenceCaptureResult
        {
            VerifierAssemblySha256 = new string('A', 64),
        };
        var incompleteRecorder = new StartupEvidenceRecorder(
            incompleteResult);
        _ = incompleteRecorder.Begin(
            StartupEvidenceCommandKind.EnableUsbHidReports);
        Assert.ThrowsException<InvalidOperationException>(() =>
            incompleteRecorder.Begin(
                StartupEvidenceCommandKind.SetFeatureMask));

        var result = new StartupEvidenceCaptureResult
        {
            VerifierAssemblySha256 = new string('A', 64),
        };
        var recorder = new StartupEvidenceRecorder(result);
        StartupEvidenceCommandAttempt enable = recorder.Begin(
            StartupEvidenceCommandKind.EnableUsbHidReports);
        Assert.IsTrue(StartupEvidenceCapturePlan.TryCreateRequest(
            enable.Operation, out byte[] enableRequest));
        Assert.IsTrue(recorder.TryComplete(enable, new CommandWireObservation(
            enable.Operation, enableRequest,
            Convert.FromHexString("0301000300F8000001000000"), true,
            null)));

        StartupEvidenceCommandAttempt feature = recorder.Begin(
            StartupEvidenceCommandKind.SetFeatureMask);
        Assert.IsTrue(StartupEvidenceCapturePlan.TryCreateRequest(
            feature.Operation, out byte[] featureRequest));
        Assert.IsFalse(recorder.TryComplete(feature,
            new CommandWireObservation(feature.Operation,
                featureRequest.AsSpan(1), [0x01], null, null)),
            "A shifted or foreign request cannot be attached to the ordinal.");
        Assert.IsFalse(recorder.TryComplete(feature,
            new CommandWireObservation(feature.Operation, featureRequest,
                [0x01], null, null)),
            "A feature observation must carry validator authority.");
        Assert.IsTrue(recorder.TryComplete(feature,
            new CommandWireObservation(feature.Operation, featureRequest,
                Convert.FromHexString("0C01000200F8000000000000"), true,
                null)));

        Assert.ThrowsException<InvalidOperationException>(() =>
            recorder.Begin(StartupEvidenceCommandKind.PlayerLed1));
    }

    [TestMethod]
    public void PlayerLedAttemptArmsCleanupBeforeAnyCompletion()
    {
        StartupEvidenceCaptureResult result = CreateThroughInputResult();
        var recorder = new StartupEvidenceRecorder(result);
        StartupEvidenceCommandAttempt player = recorder.Begin(
            StartupEvidenceCommandKind.PlayerLed1);

        Assert.IsTrue(result.Cleanup.PlayerLedAllOffRequired);
        Assert.IsFalse(player.HostTransferCompleted);
        Assert.IsFalse(result.Cleanup.PlayerLedAllOffAttempted);

        StartupEvidenceCommandAttempt allOff = recorder.Begin(
            StartupEvidenceCommandKind.PlayerLedAllOff);
        Assert.IsTrue(result.Cleanup.PlayerLedAllOffAttempted);
        Assert.IsFalse(allOff.HostTransferCompleted);
    }

    [TestMethod]
    public async Task BoundedAcquireReturnsAndLateOwnerReleasesExactlyOnce()
    {
        using var allowAcquire = new ManualResetEventSlim(false);
        var released = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int releaseCount = 0;
        using var timeout = new CancellationTokenSource(40);

        BoundedAcquireResult<object> result = await BoundedNativeOperation
            .TryAcquireAsync(() =>
            {
                allowAcquire.Wait();
                return new object();
            }, _ =>
            {
                Interlocked.Increment(ref releaseCount);
                released.TrySetResult();
                return Task.CompletedTask;
            }, timeout.Token);

        Assert.AreEqual(BoundedOperationStatus.TimedOut, result.Status);
        Assert.IsNull(result.Resource);
        Assert.IsTrue(result.LateReleaseUnconfirmed);
        allowAcquire.Set();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref releaseCount));
    }

    [TestMethod]
    public async Task BoundedAcquirePreservesTypedFailureWithoutExceptionText()
    {
        var expected = new HardwareVerificationException(
            VerificationFailureCode.HidReadOpenFailed,
            nativeErrorCode: 32);

        BoundedAcquireResult<object> result = await BoundedNativeOperation
            .TryAcquireAsync<object>(() => throw expected,
                _ => Task.CompletedTask, CancellationToken.None);

        Assert.AreEqual(BoundedOperationStatus.Failed, result.Status);
        Assert.IsNull(result.Resource);
        Assert.AreSame(expected, result.Failure);
        Assert.IsFalse(result.LateReleaseUnconfirmed);
    }

    [TestMethod]
    public async Task CommandDeadlineTransfersOneLateOwnerOnTimeout()
    {
        using var allowCompletion = new ManualResetEventSlim(false);
        var released = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int releaseCount = 0;

        HardwareVerificationException failure = await Assert
            .ThrowsExceptionAsync<HardwareVerificationException>(() =>
                OwnedOperationDeadline.RunAsync(_ =>
                {
                    allowCompletion.Wait();
                    return Task.FromResult(true);
                }, () =>
                {
                    Interlocked.Increment(ref releaseCount);
                    released.TrySetResult();
                    return Task.CompletedTask;
                }, CancellationToken.None, TimeSpan.FromMilliseconds(40),
                    VerificationFailureCode.CommandOperationTimedOut,
                    AbandonedResourceOwnership.CommandOutputWinUsb));

        Assert.AreEqual(VerificationFailureCode.CommandOperationTimedOut,
            failure.Code);
        Assert.IsFalse(failure.ResourceOwnershipReturned);
        allowCompletion.Set();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref releaseCount));
    }

    [TestMethod]
    public void SuccessfulPrivateArtifactIsClosedAndStillExplicitlyNotShareable()
    {
        StartupEvidenceCaptureResult result = CreateSuccessfulResult();
        string json = result.ToJson();

        Assert.IsTrue(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(json));
        Assert.IsFalse(result.OpaqueFeatureResponseBytesMayContainUnclassifiedData);
        Assert.IsFalse(result.AutomaticCommitOrShareAllowed);
        StringAssert.Contains(result.ArtifactClassification,
            "automatic commit or publication remains disabled");
        Assert.IsTrue(result.RedactionManifest.Any(value =>
            value.Contains("source-reviewed command tuple",
                StringComparison.Ordinal)));
        Assert.IsTrue(result.Limitations.Any(value =>
            value.Contains("may remain for the current connection",
                StringComparison.Ordinal)));
        Assert.IsFalse(result.Cleanup.FeatureConfigurationExplicitlyReverted);
        Assert.IsTrue(result.Cleanup
            .FeatureConfigurationMayRemainForCurrentConnection);
    }

    [TestMethod]
    public void ClosedArtifactRejectsEveryMutablePolicyClaim()
    {
        string canonical = CreateSuccessfulResult().ToJson();
        Action<JsonObject>[] mutations =
        [
            root => root["SuccessScope"] = "semantic ACK established",
            root => root["ArtifactClassification"] = "safe to publish",
            root => root["AutomaticCommitOrShareAllowed"] = true,
            root => root["OpaqueFeatureResponseBytesMayContainUnclassifiedData"] = true,
            root => root["Target"]!["HidInterface"] = "MI_00 writer",
            root => root["Bounds"]!["CommandOperationMilliseconds"] = 99_999,
            root => root["Causality"]!["HostBoundary"] = "semantic transaction",
            root => root["Causality"]!["RawObservationsAutomaticallyAdmittedAsValidator"] = true,
            root => root["Haptics"]!["NonzeroWritesAttempted"] = 1,
            root => root["HardwareFailureCode"] = "InputReadFailed",
            root => root["AcquisitionFailure"] = new JsonObject
            {
                ["Phase"] = "HidInputOpen",
                ["Code"] = "HidReadOpenFailed",
                ["Win32ErrorCode"] = 32,
            },
            root => root["Cleanup"]!["FeatureConfigurationExplicitlyReverted"] = true,
            root => ((JsonArray)root["RedactionManifest"]!)[0] = "sanitized",
            root => ((JsonArray)root["Limitations"]!)[0] = "no limitations",
        ];

        foreach (Action<JsonObject> mutate in mutations)
        {
            JsonObject root = (JsonObject)JsonNode.Parse(canonical)!;
            mutate(root);
            Assert.IsFalse(StartupEvidencePrivateArtifactValidator
                .IsClosedSchemaPrivateArtifact(root.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true })));
        }
    }

    [TestMethod]
    public void ClosedFailureArtifactAdmitsOnlyMatchingTypedAcquisitionFailure()
    {
        var result = new StartupEvidenceCaptureResult
        {
            VerifierAssemblySha256 = new string('A', 64),
            ProcedureFailureCode =
                StartupEvidenceFailureCode.HidInputOpenFailed,
            FailureCode = StartupEvidenceFailureCode.HidInputOpenFailed,
            HardwareFailureCode =
                VerificationFailureCode.HidReadOpenFailed,
            HardwareFailureWin32ErrorCode = 32,
            AcquisitionFailure = new StartupEvidenceAcquisitionFailure
            {
                Phase = StartupEvidenceAcquisitionPhase.HidInputOpen,
                Code = VerificationFailureCode.HidReadOpenFailed,
                Win32ErrorCode = 32,
            },
        };

        Assert.IsTrue(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(result.ToJson()));

        JsonObject wrongPhase = (JsonObject)JsonNode.Parse(
            result.ToJson())!;
        wrongPhase["AcquisitionFailure"]!["Phase"] = "CommandOpen";
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(wrongPhase.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true })));

        JsonObject missingNativeCode = (JsonObject)JsonNode.Parse(
            result.ToJson())!;
        missingNativeCode["AcquisitionFailure"]!["Win32ErrorCode"] = null;
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(missingNativeCode.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true })));

        JsonObject mismatchedHardware = (JsonObject)JsonNode.Parse(
            result.ToJson())!;
        mismatchedHardware["HardwareFailureCode"] = "InputReadFailed";
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(mismatchedHardware.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true })));
    }

    [TestMethod]
    public void ClosedArtifactRejectsKnownHostIdentifiersAndNoncanonicalHex()
    {
        JsonObject root = (JsonObject)JsonNode.Parse(
            CreateSuccessfulResult().ToJson())!;
        root["ArtifactClassification"] =
            "\\\\?\\hid#private-device-path";
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(root.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true })));

        root = (JsonObject)JsonNode.Parse(
            CreateSuccessfulResult().ToJson())!;
        root["Commands"]![1]!["ResponseHex"] = "aa";
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(root.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true })));
    }

    [TestMethod]
    public void FailureArtifactPreservesCleanupBlockWithoutInventingSuccess()
    {
        StartupEvidenceCaptureResult result = CreateThroughInputResult();
        var recorder = new StartupEvidenceRecorder(result);
        StartupEvidenceCommandAttempt player = recorder.Begin(
            StartupEvidenceCommandKind.PlayerLed1);
        player.TransferFailureStage =
            CommandTransferFailureStage.ResponseRead;
        result.Cleanup.CommandOwnershipAbandoned = true;
        result.Cleanup.LateCommandReleaseUnconfirmed = true;
        result.Cleanup.PlayerLedNeutralizationBlockedByOwnership = true;
        result.ProcedureFailureCode =
            StartupEvidenceFailureCode.CommandOperationTimedOut;
        result.FailureCode = StartupEvidenceFailureCode.CleanupIncomplete;

        string json = result.ToJson();
        Assert.IsTrue(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(json));
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Cleanup.PlayerLedAllOffRequired);
        Assert.IsFalse(result.Cleanup.PlayerLedAllOffAttempted);
        Assert.IsFalse(result.Cleanup.PlayerLedAllOffSucceeded);
    }

    [TestMethod]
    public void EverySerializedEnumDomainRejectsUndefinedNumericValues()
    {
        var undefinedTop = new StartupEvidenceCaptureResult
        {
            VerifierAssemblySha256 = new string('A', 64),
            ProcedureFailureCode = (StartupEvidenceFailureCode)999,
            FailureCode = (StartupEvidenceFailureCode)999,
        };
        StringAssert.Contains(undefinedTop.ToJson(), "999");
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(undefinedTop.ToJson()),
            "Matching undefined outer/procedure values must not bypass pair validation.");

        string success = CreateSuccessfulResult().ToJson();
        AssertJsonMutationRejected(success,
            root => root["FailureCode"] = 999);
        AssertJsonMutationRejected(success,
            root => root["ProcedureFailureCode"] = 999);
        AssertJsonMutationRejected(success,
            root => root["Commands"]![0]!["Operation"] = 999);
        AssertJsonMutationRejected(success,
            root => root["Commands"]![0]!["ValidationDisposition"] = 999);

        StartupEvidenceCaptureResult timeout = CreateThroughInputResult();
        var timeoutRecorder = new StartupEvidenceRecorder(timeout);
        StartupEvidenceCommandAttempt incomplete = timeoutRecorder.Begin(
            StartupEvidenceCommandKind.PlayerLed1);
        incomplete.TransferFailureStage =
            CommandTransferFailureStage.ResponseRead;
        timeout.Cleanup.CommandOwnershipAbandoned = true;
        timeout.Cleanup.LateCommandReleaseUnconfirmed = true;
        timeout.Cleanup.PlayerLedNeutralizationBlockedByOwnership = true;
        timeout.ProcedureFailureCode =
            StartupEvidenceFailureCode.CommandOperationTimedOut;
        timeout.FailureCode = StartupEvidenceFailureCode.CleanupIncomplete;
        Assert.IsTrue(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(timeout.ToJson()));
        AssertJsonMutationRejected(timeout.ToJson(),
            root => root["Commands"]![4]!["TransferFailureStage"] = 999);

        StartupEvidenceCaptureResult rejected = CreateRejectedPlayerResult();
        Assert.IsTrue(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(rejected.ToJson()));
        AssertJsonMutationRejected(rejected.ToJson(),
            root => root["Commands"]![4]!["ExistingValidatorFailure"] = 999);
    }

    [TestMethod]
    public void OuterFailureCodeIsExactlyDerivedFromProcedureAndCleanup()
    {
        var primaryFailure = new StartupEvidenceCaptureResult
        {
            VerifierAssemblySha256 = new string('A', 64),
            ProcedureFailureCode =
                StartupEvidenceFailureCode.DiscoveryFailed,
            FailureCode = StartupEvidenceFailureCode.DiscoveryFailed,
        };
        Assert.IsTrue(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(primaryFailure.ToJson()));

        primaryFailure.FailureCode =
            StartupEvidenceFailureCode.InputCaptureFailed;
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(primaryFailure.ToJson()),
            "Cleanup-clean results must echo the exact procedure failure.");

        StartupEvidenceCaptureResult cleanupAfterSuccess =
            CreateSuccessfulResult();
        cleanupAfterSuccess.Success = false;
        cleanupAfterSuccess.Cleanup.CommandDisposeFailed = true;
        cleanupAfterSuccess.FailureCode =
            StartupEvidenceFailureCode.CleanupIncomplete;
        Assert.IsNull(cleanupAfterSuccess.ProcedureFailureCode);
        Assert.IsTrue(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(cleanupAfterSuccess.ToJson()),
            "A successful procedure can acquire a later cleanup failure.");

        cleanupAfterSuccess.FailureCode =
            StartupEvidenceFailureCode.CommandOpenFailed;
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(cleanupAfterSuccess.ToJson()));

        StartupEvidenceCaptureResult invalidProcedureCleanup =
            CreateSuccessfulResult();
        invalidProcedureCleanup.Success = false;
        invalidProcedureCleanup.Cleanup.CommandDisposeFailed = true;
        invalidProcedureCleanup.ProcedureFailureCode =
            StartupEvidenceFailureCode.CleanupIncomplete;
        invalidProcedureCleanup.FailureCode =
            StartupEvidenceFailureCode.CleanupIncomplete;
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(
                invalidProcedureCleanup.ToJson()),
            "CleanupIncomplete is an outer result, never a procedure code.");

        StartupEvidenceCaptureResult hiddenPrimarySuccess =
            CreateSuccessfulResult();
        hiddenPrimarySuccess.Success = false;
        hiddenPrimarySuccess.ProcedureFailureCode =
            StartupEvidenceFailureCode.CommandTransferFailed;
        hiddenPrimarySuccess.FailureCode =
            StartupEvidenceFailureCode.CommandTransferFailed;
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(hiddenPrimarySuccess.ToJson()),
            "Completed primary evidence cannot be relabeled as failure.");
    }

    [TestMethod]
    public void EvidenceArgumentsAreExplicitFixedAndHaveNoRawSurface()
    {
        Assert.IsTrue(OutputOptions.TryParse(
            ["--capture-startup-evidence", "--output", "evidence.json"],
            out OutputOptions evidence));
        Assert.AreEqual(VerificationOutputMode.StartupEvidenceCapture,
            evidence.Mode);
        Assert.IsTrue(Path.IsPathFullyQualified(evidence.OutputPath!));
        Assert.IsFalse(OutputOptions.TryParse(
            ["--capture-startup-evidence"], out _));
        Assert.IsFalse(OutputOptions.TryParse(
            ["--capture-startup-evidence", "--output", "-"], out _));
        Assert.IsFalse(OutputOptions.TryParse(
            ["--capture-startup-evidence", "--raw-command", "0C91"],
            out _));
        Assert.IsFalse(OutputOptions.TryParse(
            ["--capture-startup-evidence", "--count", "1"], out _));
    }

    private static StartupEvidenceCaptureResult CreateSuccessfulResult(
        byte[]? featureResponse = null)
    {
        StartupEvidenceCaptureResult result = CreateThroughInputResult(
            featureResponse);
        var recorder = new StartupEvidenceRecorder(result);
        Complete(recorder, StartupEvidenceCommandKind.PlayerLed1,
            Convert.FromHexString("0901000100F80000"), accepted: true);
        Complete(recorder, StartupEvidenceCommandKind.PlayerLedAllOff,
            Convert.FromHexString("0901000600F80000"), accepted: true);
        result.Cleanup.PlayerLedAllOffExactResponseValidated = true;
        result.Cleanup.PlayerLedAllOffSucceeded = true;
        result.Success = true;
        return result;
    }

    private static StartupEvidenceCaptureResult CreateRejectedPlayerResult()
    {
        StartupEvidenceCaptureResult result = CreateThroughInputResult();
        var recorder = new StartupEvidenceRecorder(result);
        Complete(recorder, StartupEvidenceCommandKind.PlayerLed1,
            Convert.FromHexString("0901000100F80000"), accepted: false);
        Complete(recorder, StartupEvidenceCommandKind.PlayerLedAllOff,
            Convert.FromHexString("0901000600F80000"), accepted: true);
        result.Cleanup.PlayerLedAllOffExactResponseValidated = true;
        result.Cleanup.PlayerLedAllOffSucceeded = true;
        result.ProcedureFailureCode =
            StartupEvidenceFailureCode.CommandResponseRejected;
        result.FailureCode = result.ProcedureFailureCode;
        return result;
    }

    private static StartupEvidenceCaptureResult CreateThroughInputResult(
        byte[]? featureResponse = null)
    {
        var result = new StartupEvidenceCaptureResult
        {
            VerifierAssemblySha256 = new string('A', 64),
        };
        var recorder = new StartupEvidenceRecorder(result);
        Complete(recorder,
            StartupEvidenceCommandKind.EnableUsbHidReports,
            Convert.FromHexString("0301000300F8000001000000"),
            accepted: true);
        Complete(recorder, StartupEvidenceCommandKind.SetFeatureMask,
            featureResponse ?? Convert.FromHexString(
                "0C01000200F8000000000000"), accepted: true);
        Complete(recorder, StartupEvidenceCommandKind.EnableFeatures,
            Convert.FromHexString("0C01000400F8000000000000"),
            accepted: true);
        Complete(recorder,
            StartupEvidenceCommandKind.SelectCommonInputReport,
            Convert.FromHexString("0301000A00F80000"), accepted: true);
        result.InputRate.ExactReports = VerificationPlan.InputReportCount;
        result.InputRate.ObservedReportsPerSecond = 250;
        result.InputRate.MeanIntervalMilliseconds = 4;
        result.InputRate.P50IntervalMilliseconds = 4;
        result.InputRate.P95IntervalMilliseconds = 4.1;
        result.InputRate.P99IntervalMilliseconds = 4.2;
        result.InputRate.CounterForwardMovements =
            VerificationPlan.InputReportCount - 1;
        result.InputRate.CounterMinimumDelta = 4;
        result.InputRate.CounterMaximumDelta = 4;
        result.InputRate.CounterPlusFourMovements =
            VerificationPlan.InputReportCount - 1;
        return result;
    }

    private static void Complete(StartupEvidenceRecorder recorder,
        StartupEvidenceCommandKind operation, byte[] response,
        bool? accepted)
    {
        StartupEvidenceCommandAttempt attempt = recorder.Begin(operation);
        Assert.IsTrue(StartupEvidenceCapturePlan.TryCreateRequest(operation,
            out byte[] request));
        Assert.IsTrue(recorder.TryComplete(attempt,
            new CommandWireObservation(operation, request, response, accepted,
                accepted == false ?
                    Switch2UsbCommandFailure.InvalidAcknowledgement : null)));
    }

    private static void AssertRequest(StartupEvidenceCommandKind operation,
        string expectedHex)
    {
        Assert.IsTrue(StartupEvidenceCapturePlan.TryCreateRequest(operation,
            out byte[] request));
        Assert.AreEqual(expectedHex, Convert.ToHexString(request));
    }

    private static void AssertJsonMutationRejected(string canonical,
        Action<JsonObject> mutate)
    {
        JsonObject root = (JsonObject)JsonNode.Parse(canonical)!;
        mutate(root);
        Assert.IsFalse(StartupEvidencePrivateArtifactValidator
            .IsClosedSchemaPrivateArtifact(root.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true })));
    }
}
