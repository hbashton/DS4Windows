using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DS4Windows.Switch2;

namespace DS4Windows.Switch2.Verification;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (!OutputOptions.TryParse(args, out OutputOptions options))
        {
            Console.Error.WriteLine(
                "Invalid arguments. Syntax: [--output <new-file.json>] or --capture-startup-evidence --output <new-file.json>");
            return 2;
        }

        await using ResultDestination? destination =
            ResultDestination.TryCreate(options.OutputPath);
        if (destination is null)
        {
            Console.Error.WriteLine("The output destination could not be opened safely.");
            return 2;
        }

        if (!TryComputeVerifierAssemblySha256(out string assemblySha256))
        {
            Console.Error.WriteLine(
                "The verifier build identity could not be established safely.");
            return 3;
        }

        if (options.Mode == VerificationOutputMode.StartupEvidenceCapture)
        {
            return await StartupEvidenceCaptureMode.RunAsync(destination,
                assemblySha256).ConfigureAwait(false);
        }

        var result = new VerificationResult
        {
            VerifierAssemblySha256 = assemblySha256,
        };
        TargetDeviceSessionIdentity? target = null;
        HidInputChannel? input = null;
        HidReadWriteChannel? hid = null;
        WinUsbCommandChannel? command = null;
        bool procedureSucceeded = false;
        bool ledMayBeOn = false;
        bool hapticMayBeActive = false;
        var hapticSequence = new HapticAttemptSequence();
        var outputLeases = new OutputLeaseGate();

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            if (!VerificationPlan.TryValidate(out _))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.InvalidPlan);
            }

            target = WindowsTargetDiscovery.DiscoverSessionIdentity();
            WindowsTargetDiscovery.Revalidate(target);

            // Acquire a read-only, share-read-only MI_00 lease. Compatible
            // readers may coexist, but any existing or new HID writer is
            // excluded for the rest of this procedure. An incompatible
            // existing handle fails closed before any hardware I/O.
            input = HidInputChannel.Open(target);
            result.Target.SoleHidWriterAdmissionSucceeded = true;

            // Discard a large fixed warm-up window and require a live tail
            // before recording completion cadence. This passive phase is
            // deliberately independent of the vendor command interface.
            await RequireBoundedSessionIdentityAsync(target,
                cancellation.Token).ConfigureAwait(false);
            HidInputChannel inputCaptureChannel = input;
            InputRateCapture rate = await InputCaptureDeadline.RunAsync(
                token => inputCaptureChannel.CollectInputRateAsync(token),
                () => ReleaseInputChannelAsync(inputCaptureChannel),
                cancellation.Token).ConfigureAwait(false);
            await RequireBoundedSessionIdentityAsync(target,
                cancellation.Token).ConfigureAwait(false);
            CopyRate(rate, result.InputRate);

            // MI_01 is a distinct command interface. Its retained WinUSB
            // handle is the sole command owner used for battery and LED
            // operations and for the matching AllOff cleanup.
            command = WinUsbCommandChannel.Open(target);
            outputLeases.RegisterCommandLease();
            result.BulkTopology.Validated = true;
            result.PipePolicy.Validated = true;
            WinUsbCommandChannel commandLease = command;

            // These two capture-backed operations are volatile and leave the
            // already-observed common report selected. They contain no host
            // address, pairing material, persistent storage, or feature mask.
            // Each must return its exact pinned USB response before the next
            // command is admitted.
            result.VolatileInitialization.EnableUsbHidReportsAttempted = true;
            await RunOwnedCommandOperationAsync(commandLease, token =>
            {
                commandLease.RunVolatileInitializationStep(
                    Switch2UsbInitializationStep.EnableUsbHidReports, token);
                return true;
            }, cancellation.Token).ConfigureAwait(false);
            result.VolatileInitialization
                .EnableUsbHidReportsExactResponseShapeAndTuple = true;

            result.VolatileInitialization
                .SelectCommonInputReportAttempted = true;
            await RunOwnedCommandOperationAsync(commandLease, token =>
            {
                commandLease.RunVolatileInitializationStep(
                    Switch2UsbInitializationStep.SelectCommonInputReport,
                    token);
                return true;
            }, cancellation.Token).ConfigureAwait(false);
            result.VolatileInitialization
                .SelectCommonInputReportExactResponseShapeAndTuple = true;

            BatteryVoltageCommandResult battery =
                await RunOwnedCommandOperationAsync(commandLease,
                    commandLease.GetBatteryVoltage, cancellation.Token)
                    .ConfigureAwait(false);
            result.Battery.RawVoltage = battery.RawVoltage;
            result.Battery.ResponseStyle = battery.ResponseStyle;
            result.Battery.ExactResponseShapeAndTuple = true;

            await RequireBoundedSessionIdentityAsync(target,
                cancellation.Token).ConfigureAwait(false);
            result.Led.Player1MutationAttempted = true;
            result.Led.Player1MutationDeliveryAmbiguous = true;
            ledMayBeOn = true;
            result.Cleanup.PlayerLedAllOffRequired = true;
            Switch2UsbCommandResponseStyle playerOneResponseStyle =
                await RunOwnedCommandOperationAsync(commandLease,
                    token => commandLease.SetPlayerLed(
                        Switch2PlayerLedCommand.Player1Only, token,
                        revalidateSessionIdentity: false), cancellation.Token)
                    .ConfigureAwait(false);
            await RequireBoundedSessionIdentityAsync(target,
                cancellation.Token).ConfigureAwait(false);
            result.Led.Player1ResponseStyle = playerOneResponseStyle;
            result.Led.Player1ExactResponseShapeAndTuple = true;
            result.Led.Player1MutationDeliveryAmbiguous = false;

            if (!VerificationPlan.LiveHapticMutationSafetyGateOpen)
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.HapticMutationSafetyGateClosed);
            }

            // A mutation-capable HID handle is acquired only for the haptic
            // phase. The shared read-only input channel is retired first so
            // this process cannot defeat its own exclusive-writer check.
            await ReleaseInputChannelAsync(input).ConfigureAwait(false);
            input = null;
            hid = HidReadWriteChannel.Open(target);
            outputLeases.RegisterHidLease();
            outputLeases.RequireBoth();
            hapticMayBeActive = true;
            result.Cleanup.HapticStopRequired = true;
            result.Haptic.NonzeroMutationAttempted = true;
            await RequireBoundedSessionIdentityAsync(target,
                cancellation.Token).ConfigureAwait(false);
            HidReadWriteChannel hapticOutputChannel = hid;
            await HapticPhaseDeadline.RunAsync(
                token => WriteBasisSequenceAsync(hapticOutputChannel,
                    result.Haptic, hapticSequence, token),
                () => ReleaseHidChannelAsync(hapticOutputChannel),
                cancellation.Token).ConfigureAwait(false);

            procedureSucceeded = true;
        }
        catch (HardwareVerificationException exception)
        {
            if (!exception.ResourceOwnershipReturned)
            {
                // A hard input, command, or HID phase boundary may leave a
                // noncooperative task owning its channel. Do not race cleanup
                // or final disposal against its late-owner observer.
                if (exception.AbandonedResource ==
                    AbandonedResourceOwnership.InputCaptureHid)
                {
                    input = null;
                    result.Cleanup.InputCaptureHidOwnershipAbandoned = true;
                    result.Cleanup.LateInputHandleReleaseUnconfirmed = true;
                }
                else if (exception.AbandonedResource ==
                    AbandonedResourceOwnership.CommandOutputWinUsb)
                {
                    command = null;
                    result.Cleanup.CommandOutputOwnershipAbandoned = true;
                    result.Cleanup.LateOutputHandleReleaseUnconfirmed = true;
                    if (result.Cleanup.PlayerLedAllOffRequired)
                    {
                        result.Cleanup.PlayerLedCommandOwnershipAbandoned =
                            true;
                        result.Cleanup
                            .PlayerLedNeutralizationBlockedByOwnership = true;
                    }
                }
                else if (exception.AbandonedResource ==
                    AbandonedResourceOwnership.HapticOutputHid)
                {
                    hid = null;
                    result.Cleanup.HapticOutputHidOwnershipAbandoned = true;
                    result.Cleanup.HapticNeutralizationBlockedByOwnership =
                        true;
                    result.Cleanup.LateOutputHandleReleaseUnconfirmed = true;
                }
            }
            result.ProcedureFailureCode = exception.Code.ToString();
            result.FailureNativeErrorCode = exception.NativeErrorCode;
            result.CommandResponseFailureDetail =
                exception.CommandResponseFailure?.ToString();
            result.CommandTransferFailureStage =
                exception.CommandTransferStage?.ToString();
            result.CommandObservedResponseLength =
                exception.ObservedResponseLength;
            result.CommandObservedResponseHeaderByte4 =
                exception.ObservedResponseHeaderByte4;
            result.CommandObservedResponseAcknowledgement =
                exception.ObservedResponseAcknowledgement;
            if (exception.PipeTopology is { } pipeTopology)
            {
                result.BulkTopology.Record(pipeTopology);
            }
            if (exception.PipePolicy is { } pipePolicy)
            {
                result.PipePolicy.Record(pipePolicy);
            }
        }
        catch (OperationCanceledException)
        {
            result.ProcedureFailureCode =
                VerificationFailureCode.Cancelled.ToString();
        }
        catch
        {
            // Exception text and native identifiers are intentionally omitted.
            result.ProcedureFailureCode =
                VerificationFailureCode.UnexpectedFailure.ToString();
        }
        finally
        {
            var ledAttemptResult = new LedResult();
            var ledAttemptCleanup = new CleanupResult
            {
                PlayerLedAllOffRequired =
                    result.Cleanup.PlayerLedAllOffRequired,
            };
            WinUsbCommandChannel? ledAttemptChannel = command;
            var hapticAttemptCleanup = new CleanupResult
            {
                HapticStopRequired = result.Cleanup.HapticStopRequired,
            };
            HidReadWriteChannel? hapticAttemptChannel = hid;

            CleanupOrderResult cleanupOrder =
                await CleanupOrderCoordinator.RunLedThenHapticAsync(
                async token =>
                {
                    ledAttemptChannel = await TryCleanupPlayerLedAsync(target,
                        ledAttemptChannel, ledMayBeOn, ledAttemptResult,
                        ledAttemptCleanup, token).ConfigureAwait(false);
                }, CleanupBudgetFactory.CreateLed,
                async () =>
                {
                    WinUsbCommandChannel? abandoned = ledAttemptChannel;
                    ledAttemptChannel = null;
                    if (abandoned is not null)
                    {
                        await ReleaseCommandChannelAsync(abandoned)
                            .ConfigureAwait(false);
                    }
                },
                async token =>
                {
                    hapticAttemptChannel = await TryCleanupHapticsAsync(target,
                        hapticAttemptChannel, hapticMayBeActive,
                        hapticAttemptCleanup, hapticSequence, token)
                        .ConfigureAwait(false);
                }, CleanupBudgetFactory.CreateHaptic,
                async () =>
                {
                    HidReadWriteChannel? abandoned = hapticAttemptChannel;
                    hapticAttemptChannel = null;
                    if (abandoned is not null)
                    {
                        await ReleaseHidChannelAsync(abandoned)
                            .ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);

            if (cleanupOrder.CommandOwnershipReturned)
            {
                command = ledAttemptChannel;
                ApplyLedCleanupResult(ledAttemptResult, ledAttemptCleanup,
                    result.Led, result.Cleanup);
            }
            else
            {
                // The timed-out arm retains sole ownership. Never race it with
                // a replacement or final dispose in this process.
                command = null;
                result.Cleanup.PlayerLedAllOffSucceeded = false;
                result.Led.AllOffMutationDeliveryAmbiguous = true;
                result.Cleanup.PlayerLedCleanupArmTimedOut = true;
                result.Cleanup.PlayerLedCommandOwnershipAbandoned = true;
                result.Cleanup.CommandOutputOwnershipAbandoned = true;
                result.Cleanup.PlayerLedNeutralizationBlockedByOwnership =
                    result.Cleanup.PlayerLedAllOffRequired;
                result.Cleanup.LateOutputHandleReleaseUnconfirmed = true;
            }

            if (cleanupOrder.HidOwnershipReturned)
            {
                hid = hapticAttemptChannel;
                ApplyHapticCleanupResult(hapticAttemptCleanup,
                    result.Cleanup);
            }
            else
            {
                hid = null;
                result.Cleanup.HapticStopHostWriteCompleted = false;
                result.Haptic.ZeroAmplitudeDeliveryAmbiguous = true;
                result.Cleanup.HapticCleanupArmTimedOut = true;
                result.Cleanup.HapticCleanupHidOwnershipAbandoned = true;
                result.Cleanup.LateOutputHandleReleaseUnconfirmed = true;
            }

            command = await TryDisposeCommandChannelAsync(command,
                result.Cleanup).ConfigureAwait(false);
            hid = await TryDisposeHidChannelAsync(hid, result.Cleanup)
                .ConfigureAwait(false);
            input = await TryDisposeInputChannelAsync(input, result.Cleanup)
                .ConfigureAwait(false);
            result.Haptic.ZeroAmplitudeWritesAttempted =
                result.Cleanup.HapticStopAttempts;
            result.Haptic.ZeroAmplitudeHostWritesCompleted =
                result.Cleanup.HapticStopHostWriteCompleted ? 1 : 0;
            Console.CancelKeyPress -= cancelHandler;
        }

        bool cleanupSucceeded =
            (!result.Cleanup.HapticStopRequired ||
                result.Cleanup.HapticStopHostWriteCompleted) &&
            (!result.Cleanup.PlayerLedAllOffRequired ||
                result.Cleanup.PlayerLedAllOffSucceeded) &&
            !result.Cleanup.SessionIdentityRevalidationFailure &&
            !result.Cleanup.LateOutputHandleReleaseUnconfirmed &&
            !result.Cleanup.LateInputHandleReleaseUnconfirmed &&
            !result.Cleanup.InputChannelDisposeFailure &&
            !result.Cleanup.HidChannelDisposeFailure &&
            !result.Cleanup.CommandChannelDisposeFailure;
        FinalizeOutcome(result, procedureSucceeded, cleanupSucceeded);

        try
        {
            string json = result.ToJson();
            if (!VerificationPrivacyValidator.IsPrivacySafeClosedSchemaJson(
                    json) || !await destination.WriteAsync(json)
                    .ConfigureAwait(false))
            {
                Console.Error.WriteLine(
                    "The privacy-safe result could not be written.");
                return 3;
            }
        }
        catch
        {
            Console.Error.WriteLine(
                "The privacy-safe result could not be written.");
            return 3;
        }
        return result.Success ? 0 : 1;
    }

    internal static bool TryComputeVerifierAssemblySha256(
        out string sha256)
    {
        sha256 = string.Empty;
        try
        {
            string location = typeof(Program).Assembly.Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                return false;
            }
            using FileStream stream = new(location, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return sha256.Length == 64;
        }
        catch
        {
            sha256 = string.Empty;
            return false;
        }
    }

    internal static void FinalizeOutcome(VerificationResult result,
        bool procedureSucceeded, bool cleanupSucceeded)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Success = procedureSucceeded && cleanupSucceeded;
        result.FailureCode = cleanupSucceeded ?
            result.ProcedureFailureCode : "CleanupIncomplete";
    }

    internal static void ApplyLedCleanupResult(LedResult sourceLed,
        CleanupResult source, LedResult destinationLed,
        CleanupResult destination)
    {
        destinationLed.AllOffExactResponseShapeAndTuple =
            sourceLed.AllOffExactResponseShapeAndTuple;
        destinationLed.AllOffMutationAttempted =
            sourceLed.AllOffMutationAttempted;
        destinationLed.AllOffMutationDeliveryAmbiguous =
            sourceLed.AllOffMutationDeliveryAmbiguous;
        destinationLed.AllOffResponseStyle = sourceLed.AllOffResponseStyle;
        destination.PlayerLedAllOffSucceeded =
            source.PlayerLedAllOffSucceeded;
        destination.CommandChannelReopened = source.CommandChannelReopened;
        destination.SessionIdentityRevalidationFailure |=
            source.SessionIdentityRevalidationFailure;
        destination.CommandChannelDisposeFailure |=
            source.CommandChannelDisposeFailure;
        destination.CommandOutputOwnershipAbandoned |=
            source.CommandOutputOwnershipAbandoned;
        destination.PlayerLedCommandOwnershipAbandoned |=
            source.PlayerLedCommandOwnershipAbandoned;
        destination.PlayerLedNeutralizationBlockedByOwnership |=
            source.PlayerLedNeutralizationBlockedByOwnership;
        destination.LateOutputHandleReleaseUnconfirmed |=
            source.LateOutputHandleReleaseUnconfirmed;
    }

    private static void ApplyHapticCleanupResult(CleanupResult source,
        CleanupResult destination)
    {
        destination.HapticStopAttempts = source.HapticStopAttempts;
        destination.HapticStopHostWriteCompleted =
            source.HapticStopHostWriteCompleted;
        destination.HidChannelReopened = source.HidChannelReopened;
        destination.SessionIdentityRevalidationFailure |=
            source.SessionIdentityRevalidationFailure;
        destination.HidChannelDisposeFailure |=
            source.HidChannelDisposeFailure;
        destination.LateOutputHandleReleaseUnconfirmed |=
            source.LateOutputHandleReleaseUnconfirmed;
    }

    private static async Task RequireBoundedSessionIdentityAsync(
        TargetDeviceSessionIdentity target, CancellationToken outerToken)
    {
        if (outerToken.IsCancellationRequested)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.Cancelled);
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(
            outerToken);
        budget.CancelAfter(
            VerificationPlan.SessionRevalidationTimeoutMilliseconds);
        BoundedOperationStatus status = await BoundedNativeOperation
            .TryRunAsync(() => WindowsTargetDiscovery.Revalidate(target),
                budget.Token).ConfigureAwait(false);
        if (status == BoundedOperationStatus.Succeeded)
        {
            return;
        }
        throw new HardwareVerificationException(
            outerToken.IsCancellationRequested ?
                VerificationFailureCode.Cancelled :
                VerificationFailureCode.SessionIdentityRevalidationFailed);
    }

    private static Task<T> RunOwnedCommandOperationAsync<T>(
        WinUsbCommandChannel channel, Func<CancellationToken, T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(operation);
        return CommandOperationDeadline.RunAsync(
            token => Task.FromResult(operation(token)),
            () => ReleaseCommandChannelAsync(channel), cancellationToken);
    }

    private static async Task<WinUsbCommandChannel?>
        TryCleanupPlayerLedAsync(TargetDeviceSessionIdentity? target,
            WinUsbCommandChannel? command, bool ledMayBeOn,
            LedResult ledResult, CleanupResult cleanup,
            CancellationToken token)
    {
        if (!ledMayBeOn || target is null || command is null)
        {
            return command;
        }
        if (token.IsCancellationRequested)
        {
            return command;
        }

        if (CommandCleanupPolicy.RequiresFreshChannel(
                cleanup.PlayerLedAllOffRequired,
                command.IsFaulted ? CommandTransactionPhase.Faulted :
                    CommandTransactionPhase.Ready))
        {
            WinUsbCommandChannel oldChannel = command;
            command = null;
            BoundedReplacementResult<WinUsbCommandChannel> replacement =
                await BoundedNativeOperation.TryReplaceAsync(oldChannel,
                    ReleaseCommandChannelAsync,
                    () => WinUsbCommandChannel.Open(target),
                    ReleaseCommandChannelAsync, token).ConfigureAwait(false);
            RecordCommandReplacementStatus(replacement.ReleaseStatus,
                replacement.Acquisition.LateReleaseUnconfirmed, cleanup);
            if (!replacement.Succeeded)
            {
                cleanup.PlayerLedAllOffSucceeded = false;
                return null;
            }
            command = replacement.Acquisition.Resource;
            cleanup.CommandChannelReopened = true;
        }
        if (command is null)
        {
            cleanup.PlayerLedAllOffSucceeded = false;
            return null;
        }

        BoundedOperationStatus identity = await BoundedNativeOperation
            .TryRunAsync(() => WindowsTargetDiscovery.Revalidate(target),
                token).ConfigureAwait(false);
        if (identity != BoundedOperationStatus.Succeeded)
        {
            cleanup.SessionIdentityRevalidationFailure = true;
            cleanup.PlayerLedAllOffSucceeded = false;
            return command;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            // The bounded revalidation above is the cleanup identity gate.
            // Avoid a second synchronous enumeration inside the transaction.
            LedCleanupResultTransition.MarkAttempted(ledResult);
            WinUsbCommandChannel allOffChannel = command;
            Switch2UsbCommandResponseStyle responseStyle =
                await RunOwnedCommandOperationAsync(allOffChannel,
                    operationToken => allOffChannel.SetPlayerLed(
                        Switch2PlayerLedCommand.AllOff, operationToken,
                        revalidateSessionIdentity: false), token)
                    .ConfigureAwait(false);
            identity = await BoundedNativeOperation.TryRunAsync(
                () => WindowsTargetDiscovery.Revalidate(target), token)
                .ConfigureAwait(false);
            if (identity != BoundedOperationStatus.Succeeded)
            {
                cleanup.SessionIdentityRevalidationFailure = true;
                cleanup.PlayerLedAllOffSucceeded = false;
                return command;
            }
            LedCleanupResultTransition.MarkConfirmed(ledResult,
                responseStyle);
            cleanup.PlayerLedAllOffSucceeded = true;
        }
        catch (HardwareVerificationException exception) when (
            !exception.ResourceOwnershipReturned &&
            exception.AbandonedResource ==
                AbandonedResourceOwnership.CommandOutputWinUsb)
        {
            command = null;
            cleanup.CommandOutputOwnershipAbandoned = true;
            cleanup.PlayerLedCommandOwnershipAbandoned = true;
            cleanup.PlayerLedNeutralizationBlockedByOwnership = true;
            cleanup.LateOutputHandleReleaseUnconfirmed = true;
            cleanup.PlayerLedAllOffSucceeded = false;
        }
        catch
        {
            cleanup.PlayerLedAllOffSucceeded = false;
        }
        return command;
    }

    private static async Task<HidReadWriteChannel?> TryCleanupHapticsAsync(
        TargetDeviceSessionIdentity? target, HidReadWriteChannel? hid,
        bool hapticMayBeActive, CleanupResult cleanup,
        HapticAttemptSequence sequence, CancellationToken token)
    {
        if (!hapticMayBeActive || target is null || hid is null)
        {
            return hid;
        }
        if (token.IsCancellationRequested)
        {
            return hid;
        }

        if (HapticCleanupPolicy.RequiresFreshChannel(
                cleanup.HapticStopRequired, hid.IsOutputFaulted))
        {
            HidReadWriteChannel oldChannel = hid;
            hid = null;
            BoundedReplacementResult<HidReadWriteChannel> replacement =
                await BoundedNativeOperation.TryReplaceAsync(oldChannel,
                    ReleaseHidChannelAsync,
                    () => HidReadWriteChannel.Open(target),
                    ReleaseHidChannelAsync, token).ConfigureAwait(false);
            RecordHidChannelDisposalStatus(replacement.ReleaseStatus,
                cleanup);
            cleanup.LateOutputHandleReleaseUnconfirmed |=
                replacement.Acquisition.LateReleaseUnconfirmed;
            if (!replacement.Succeeded)
            {
                return null;
            }
            hid = replacement.Acquisition.Resource;
            cleanup.HidChannelReopened = true;
        }

        return hid is null ? null : await TryStopHapticsAsync(target, hid,
            cleanup, sequence, token).ConfigureAwait(false);
    }

    private static async Task<WinUsbCommandChannel?>
        TryDisposeCommandChannelAsync(WinUsbCommandChannel? command,
            CleanupResult cleanup)
    {
        if (command is null)
        {
            return null;
        }
        using CancellationTokenSource budget =
            CleanupBudgetFactory.CreateChannelDispose();
        BoundedOperationStatus status = await BoundedNativeOperation
            .TryRunAsync(() => command.Dispose(), budget.Token)
            .ConfigureAwait(false);
        RecordCommandChannelDisposalStatus(status, cleanup);
        return null;
    }

    private static async Task<HidReadWriteChannel?>
        TryDisposeHidChannelAsync(HidReadWriteChannel? hid,
            CleanupResult cleanup)
    {
        if (hid is null)
        {
            return null;
        }
        using CancellationTokenSource budget =
            CleanupBudgetFactory.CreateChannelDispose();
        BoundedOperationStatus status = await BoundedNativeOperation
            .TryRunAsync(() => ReleaseHidChannelAsync(hid), budget.Token)
            .ConfigureAwait(false);
        RecordHidChannelDisposalStatus(status, cleanup);
        return null;
    }

    private static async Task<HidInputChannel?>
        TryDisposeInputChannelAsync(HidInputChannel? input,
            CleanupResult cleanup)
    {
        if (input is null)
        {
            return null;
        }
        using CancellationTokenSource budget =
            CleanupBudgetFactory.CreateChannelDispose();
        BoundedOperationStatus status = await BoundedNativeOperation
            .TryRunAsync(() => ReleaseInputChannelAsync(input), budget.Token)
            .ConfigureAwait(false);
        RecordInputChannelDisposalStatus(status, cleanup);
        return null;
    }

    internal static void RecordCommandChannelDisposalStatus(
        BoundedOperationStatus status, CleanupResult cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        cleanup.CommandChannelDisposeFailure |=
            status == BoundedOperationStatus.Failed;
        cleanup.LateOutputHandleReleaseUnconfirmed |=
            status == BoundedOperationStatus.TimedOut;
    }

    internal static void RecordCommandReplacementStatus(
        BoundedOperationStatus releaseStatus,
        bool lateAcquisitionReleaseUnconfirmed, CleanupResult cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        RecordCommandChannelDisposalStatus(releaseStatus, cleanup);
        cleanup.LateOutputHandleReleaseUnconfirmed |=
            lateAcquisitionReleaseUnconfirmed;
        if (releaseStatus == BoundedOperationStatus.TimedOut ||
            lateAcquisitionReleaseUnconfirmed)
        {
            cleanup.CommandOutputOwnershipAbandoned = true;
            cleanup.PlayerLedCommandOwnershipAbandoned = true;
            cleanup.PlayerLedNeutralizationBlockedByOwnership = true;
        }
    }

    internal static void RecordHidChannelDisposalStatus(
        BoundedOperationStatus status, CleanupResult cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        cleanup.HidChannelDisposeFailure |=
            status == BoundedOperationStatus.Failed;
        cleanup.LateOutputHandleReleaseUnconfirmed |=
            status == BoundedOperationStatus.TimedOut;
    }

    internal static void RecordInputChannelDisposalStatus(
        BoundedOperationStatus status, CleanupResult cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        cleanup.InputChannelDisposeFailure |=
            status == BoundedOperationStatus.Failed;
        cleanup.LateInputHandleReleaseUnconfirmed |=
            status == BoundedOperationStatus.TimedOut;
    }

    private static Task ReleaseCommandChannelAsync(
        WinUsbCommandChannel channel)
    {
        channel.Dispose();
        return Task.CompletedTask;
    }

    private static async Task ReleaseHidChannelAsync(
        HidReadWriteChannel channel) =>
        await channel.DisposeAsync().ConfigureAwait(false);

    private static async Task ReleaseInputChannelAsync(
        HidInputChannel channel) =>
        await channel.DisposeAsync().ConfigureAwait(false);

    private static void CopyRate(InputRateCapture source,
        InputRateResult destination)
    {
        destination.ExactReports = source.Timing.ExactReports;
        destination.ObservedReportsPerSecond =
            source.Timing.ReportsPerSecond;
        destination.MeanIntervalMilliseconds =
            source.Timing.MeanIntervalMilliseconds;
        destination.P50IntervalMilliseconds =
            source.Timing.P50IntervalMilliseconds;
        destination.P95IntervalMilliseconds =
            source.Timing.P95IntervalMilliseconds;
        destination.P99IntervalMilliseconds =
            source.Timing.P99IntervalMilliseconds;
        destination.CounterForwardMovements =
            source.Counter.ForwardMovements;
        destination.CounterMinimumDelta = source.Counter.MinimumDelta;
        destination.CounterMaximumDelta = source.Counter.MaximumDelta;
        destination.CounterPlusFourMovements =
            source.Counter.PlusFourMovements;
        destination.CounterWrapObserved = source.Counter.WrapObserved;
    }

    private static async Task WriteBasisSequenceAsync(
        HidReadWriteChannel hid, HapticResult result,
        HapticAttemptSequence sequence,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        var report = new byte[VerificationPlan.HidReportLength];

        for (int ordinal = 0; ordinal < VerificationPlan.HapticFrameCount;
             ordinal++)
        {
            // Reserve before I/O: a timeout can mean ambiguous delivery, so
            // completion must never control sequence advancement.
            if (!sequence.TryReserve(out byte counter) ||
                !VerificationPlan.TryWriteHapticReport(counter,
                    VerificationPlan.BasisSubframe, report))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.InvalidPlan);
            }
            result.WritesAttempted++;
            await hid.WriteReportAsync(report, cancellationToken)
                .ConfigureAwait(false);
            result.HostWritesCompleted++;

            if (ordinal + 1 < VerificationPlan.HapticFrameCount)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                TimeSpan deadline = TimeSpan.FromMilliseconds(
                    (ordinal + 1) *
                    VerificationPlan.HapticCadenceMilliseconds);
                TimeSpan delay = deadline - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMilliseconds(
                VerificationPlan.HapticMaximumDurationMilliseconds))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.HapticDurationExceeded);
        }
    }

    private static async Task<HidReadWriteChannel?> TryStopHapticsAsync(
        TargetDeviceSessionIdentity target, HidReadWriteChannel hid,
        CleanupResult cleanup, HapticAttemptSequence sequence,
        CancellationToken token)
    {
        var stop = new byte[VerificationPlan.HidReportLength];
        for (int attempt = 0;
             attempt < VerificationPlan.StopMaximumAttempts; attempt++)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }
            cleanup.HapticStopAttempts++;
            BoundedOperationStatus identity = await BoundedNativeOperation
                .TryRunAsync(() => WindowsTargetDiscovery.Revalidate(target),
                    token).ConfigureAwait(false);
            if (identity != BoundedOperationStatus.Succeeded)
            {
                cleanup.SessionIdentityRevalidationFailure = true;
                break;
            }
            try
            {
                // Every stop attempt owns a fresh value even when the prior
                // write timed out after potentially reaching the controller.
                if (!sequence.TryReserve(out byte counter) ||
                    !VerificationPlan.TryWriteHapticReport(counter,
                        VerificationPlan.StopSubframe, stop))
                {
                    break;
                }
                await hid.WriteReportAsync(stop, token)
                    .ConfigureAwait(false);
                cleanup.HapticStopHostWriteCompleted = true;
                return hid;
            }
            catch
            {
                if (token.IsCancellationRequested ||
                    attempt + 1 == VerificationPlan.StopMaximumAttempts)
                {
                    break;
                }
                if (hid.IsOutputFaulted)
                {
                    HidReadWriteChannel poisoned = hid;
                    BoundedReplacementResult<HidReadWriteChannel> replacement =
                        await BoundedNativeOperation.TryReplaceAsync(poisoned,
                            ReleaseHidChannelAsync,
                            () => HidReadWriteChannel.Open(target),
                            ReleaseHidChannelAsync, token)
                            .ConfigureAwait(false);
                    RecordHidChannelDisposalStatus(
                        replacement.ReleaseStatus, cleanup);
                    cleanup.LateOutputHandleReleaseUnconfirmed |=
                        replacement.Acquisition.LateReleaseUnconfirmed;
                    if (!replacement.Succeeded)
                    {
                        return null;
                    }
                    hid = replacement.Acquisition.Resource!;
                    cleanup.HidChannelReopened = true;
                }
                try
                {
                    await Task.Delay(
                        VerificationPlan.HapticCadenceMilliseconds, token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }
        return hid;
    }
}

internal enum VerificationOutputMode
{
    Standard,
    StartupEvidenceCapture,
}

internal sealed record OutputOptions(VerificationOutputMode Mode,
    string? OutputPath)
{
    internal static bool TryParse(string[] args, out OutputOptions options)
    {
        options = new OutputOptions(VerificationOutputMode.Standard,
            (string?)null);
        if (args.Length == 0)
        {
            return true;
        }

        VerificationOutputMode mode;
        string pathArgument;
        if (args.Length == 2 &&
            string.Equals(args[0], "--output", StringComparison.Ordinal))
        {
            mode = VerificationOutputMode.Standard;
            pathArgument = args[1];
        }
        else if (args.Length == 3 &&
                 string.Equals(args[0], "--capture-startup-evidence",
                     StringComparison.Ordinal) &&
                 string.Equals(args[1], "--output",
                     StringComparison.Ordinal))
        {
            // Evidence contains only closed, production-codec-validated
            // feature responses and must go to an explicitly named new local
            // file; the utility never commits or publishes it automatically.
            mode = VerificationOutputMode.StartupEvidenceCapture;
            pathArgument = args[2];
        }
        else
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(pathArgument))
        {
            return false;
        }

        try
        {
            string path = Path.GetFullPath(pathArgument);
            if (!string.Equals(Path.GetExtension(path), ".json",
                    StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return false;
            }
            options = new OutputOptions(mode, path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class ResultDestination : IAsyncDisposable
{
    private readonly StreamWriter? writer;

    private ResultDestination(StreamWriter? writer)
    {
        this.writer = writer;
    }

    internal static ResultDestination? TryCreate(string? outputPath)
    {
        if (outputPath is null)
        {
            return new ResultDestination(null);
        }
        try
        {
            var stream = new FileStream(outputPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            return new ResultDestination(new StreamWriter(stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
        }
        catch
        {
            return null;
        }
    }

    internal async Task<bool> WriteAsync(string json)
    {
        try
        {
            TextWriter target = writer ?? Console.Out;
            await target.WriteLineAsync(json).ConfigureAwait(false);
            await target.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (writer is not null)
        {
            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Never let a filesystem/native exception disclose the
                // private output destination after the fixed result path.
            }
        }
    }
}
