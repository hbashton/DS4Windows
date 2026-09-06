namespace DS4Windows.Switch2.Verification;

/// <summary>
/// Explicit, dormant laboratory evidence mode. It reuses the verifier's sole
/// MI_01 command owner and read-only MI_00 input owner. Nothing in this class
/// is referenced by DS4Windows production registration.
/// </summary>
internal static class StartupEvidenceCaptureMode
{
    internal static async Task<int> RunAsync(ResultDestination destination,
        string verifierAssemblySha256)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var result = new StartupEvidenceCaptureResult
        {
            VerifierAssemblySha256 = verifierAssemblySha256,
        };
        var recorder = new StartupEvidenceRecorder(result);
        TargetDeviceSessionIdentity? target = null;
        HidInputChannel? input = null;
        WinUsbCommandChannel? command = null;
        bool procedureSucceeded = false;
        bool ledMayBeOn = false;
        StartupEvidenceCommandAttempt? activeAttempt = null;

        using var userCancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            userCancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        using var interaction = CancellationTokenSource
            .CreateLinkedTokenSource(userCancellation.Token);
        interaction.CancelAfter(
            StartupEvidenceCapturePlan.WholeInteractionTimeoutMilliseconds);

        try
        {
            if (!StartupEvidenceCapturePlan.TryValidate(out _))
            {
                throw new StartupEvidenceCaptureException(
                    StartupEvidenceFailureCode.InvalidPlan);
            }

            target = await AcquireTargetAsync(result, interaction.Token)
                .ConfigureAwait(false);
            await RequireIdentityAsync(target, interaction.Token)
                .ConfigureAwait(false);

            BoundedAcquireResult<HidInputChannel> inputAcquisition =
                await AcquireInputAsync(target, interaction.Token)
                    .ConfigureAwait(false);
            if (!inputAcquisition.Succeeded)
            {
                RecordAcquisitionFailure(result,
                    StartupEvidenceAcquisitionPhase.HidInputOpen,
                    inputAcquisition.Failure);
                result.Cleanup.LateInputReleaseUnconfirmed |=
                    inputAcquisition.LateReleaseUnconfirmed;
                throw new StartupEvidenceCaptureException(
                    inputAcquisition.Status == BoundedOperationStatus.TimedOut ?
                        StartupEvidenceFailureCode.HidInputOpenTimedOut :
                        StartupEvidenceFailureCode.HidInputOpenFailed);
            }
            input = inputAcquisition.Resource!;

            BoundedAcquireResult<WinUsbCommandChannel> commandAcquisition =
                await AcquireCommandAsync(target, interaction.Token)
                    .ConfigureAwait(false);
            if (!commandAcquisition.Succeeded)
            {
                RecordAcquisitionFailure(result,
                    StartupEvidenceAcquisitionPhase.CommandOpen,
                    commandAcquisition.Failure);
                result.Cleanup.LateCommandReleaseUnconfirmed |=
                    commandAcquisition.LateReleaseUnconfirmed;
                throw new StartupEvidenceCaptureException(
                    commandAcquisition.Status ==
                            BoundedOperationStatus.TimedOut ?
                        StartupEvidenceFailureCode.CommandOpenTimedOut :
                        StartupEvidenceFailureCode.CommandOpenFailed);
            }
            command = commandAcquisition.Resource!;
            WinUsbCommandChannel activeCommand = command;

            foreach (StartupEvidenceCommandKind operation in
                     StartupEvidenceCapturePlan.StartupOrder)
            {
                activeAttempt = recorder.Begin(operation);
                CommandWireObservation observation =
                    await RunCommandAsync(activeCommand, operation,
                        interaction.Token).ConfigureAwait(false);
                if (!recorder.TryComplete(activeAttempt, observation))
                {
                    throw new StartupEvidenceCaptureException(
                        StartupEvidenceFailureCode.UnexpectedFailure);
                }
                if (activeAttempt.ValidationDisposition ==
                    StartupEvidenceValidationDisposition.
                        ExistingValidatorRejected)
                {
                    throw new StartupEvidenceCaptureException(
                        StartupEvidenceFailureCode.CommandResponseRejected);
                }
                activeAttempt = null;
            }

            await RequireIdentityAsync(target, interaction.Token)
                .ConfigureAwait(false);
            HidInputChannel activeInput = input!;
            try
            {
                InputRateCapture rate = await InputCaptureDeadline.RunAsync(
                    token => activeInput.CollectInputRateAsync(token),
                    () => ReleaseInputAsync(activeInput), interaction.Token)
                    .ConfigureAwait(false);
                CopyRate(rate, result.InputRate);
            }
            catch (HardwareVerificationException exception) when (
                !exception.ResourceOwnershipReturned &&
                exception.AbandonedResource ==
                    AbandonedResourceOwnership.InputCaptureHid)
            {
                input = null;
                result.Cleanup.InputOwnershipAbandoned = true;
                result.Cleanup.LateInputReleaseUnconfirmed = true;
                throw;
            }
            await RequireIdentityAsync(target, interaction.Token)
                .ConfigureAwait(false);

            activeAttempt = recorder.Begin(
                StartupEvidenceCommandKind.PlayerLed1);
            // The request may reach the device even if the response or the
            // ownership deadline is lost. Arm cleanup before invoking I/O.
            ledMayBeOn = true;
            CommandWireObservation playerOne = await RunCommandAsync(
                activeCommand,
                StartupEvidenceCommandKind.PlayerLed1, interaction.Token)
                .ConfigureAwait(false);
            if (!recorder.TryComplete(activeAttempt, playerOne))
            {
                throw new StartupEvidenceCaptureException(
                    StartupEvidenceFailureCode.UnexpectedFailure);
            }
            if (activeAttempt.ValidationDisposition !=
                StartupEvidenceValidationDisposition.ExistingValidatorAccepted)
            {
                throw new StartupEvidenceCaptureException(
                    StartupEvidenceFailureCode.CommandResponseRejected);
            }
            activeAttempt = null;
            procedureSucceeded = true;
        }
        catch (HardwareVerificationException exception)
        {
            result.HardwareFailureCode = exception.Code;
            result.HardwareFailureWin32ErrorCode =
                exception.NativeErrorCode;
            if (activeAttempt is not null)
            {
                activeAttempt.TransferFailureStage =
                    exception.CommandTransferStage;
            }
            if (!exception.ResourceOwnershipReturned)
            {
                if (exception.AbandonedResource ==
                    AbandonedResourceOwnership.CommandOutputWinUsb)
                {
                    command = null;
                    result.Cleanup.CommandOwnershipAbandoned = true;
                    result.Cleanup.LateCommandReleaseUnconfirmed = true;
                    if (result.Cleanup.PlayerLedAllOffRequired)
                    {
                        result.Cleanup
                            .PlayerLedNeutralizationBlockedByOwnership = true;
                    }
                }
                else if (exception.AbandonedResource ==
                    AbandonedResourceOwnership.InputCaptureHid)
                {
                    input = null;
                    result.Cleanup.InputOwnershipAbandoned = true;
                    result.Cleanup.LateInputReleaseUnconfirmed = true;
                }
            }
            result.ProcedureFailureCode = MapFailure(exception,
                interaction.IsCancellationRequested,
                userCancellation.IsCancellationRequested);
        }
        catch (StartupEvidenceCaptureException exception)
        {
            result.ProcedureFailureCode = exception.Code;
        }
        catch (OperationCanceledException)
        {
            result.ProcedureFailureCode =
                userCancellation.IsCancellationRequested ?
                    StartupEvidenceFailureCode.Cancelled :
                    StartupEvidenceFailureCode.InteractionTimedOut;
        }
        catch
        {
            result.ProcedureFailureCode =
                StartupEvidenceFailureCode.UnexpectedFailure;
        }
        finally
        {
            if (ledMayBeOn)
            {
                command = await TryNeutralizePlayerLedAsync(target, command,
                    recorder, result).ConfigureAwait(false);
            }
            command = await DisposeCommandAsync(command, result)
                .ConfigureAwait(false);
            input = await DisposeInputAsync(input, result)
                .ConfigureAwait(false);
            Console.CancelKeyPress -= cancelHandler;
        }

        StartupEvidenceExpectedOutcome outcome =
            StartupEvidenceOutcomePolicy.Evaluate(result,
                procedureSucceeded);
        result.Success = outcome.Success;
        result.FailureCode = outcome.FailureCode;

        try
        {
            string json = result.ToJson();
            if (!StartupEvidencePrivateArtifactValidator
                    .IsClosedSchemaPrivateArtifact(json) ||
                !await destination.WriteAsync(json).ConfigureAwait(false))
            {
                Console.Error.WriteLine(
                    "The closed private evidence result could not be written.");
                return 3;
            }
        }
        catch
        {
            Console.Error.WriteLine(
                "The closed private evidence result could not be written.");
            return 3;
        }
        return result.Success ? 0 : 1;
    }

    private static async Task<TargetDeviceSessionIdentity> AcquireTargetAsync(
        StartupEvidenceCaptureResult result, CancellationToken outerToken)
    {
        using CancellationTokenSource budget = CreatePhaseBudget(outerToken,
            StartupEvidenceCapturePlan.DiscoveryTimeoutMilliseconds);
        BoundedAcquireResult<TargetDeviceSessionIdentity> acquisition =
            await BoundedNativeOperation.TryAcquireAsync(
                WindowsTargetDiscovery.DiscoverSessionIdentity,
                static _ => Task.CompletedTask, budget.Token)
                .ConfigureAwait(false);
        if (!acquisition.Succeeded)
        {
            RecordAcquisitionFailure(result,
                StartupEvidenceAcquisitionPhase.Discovery,
                acquisition.Failure);
            throw new StartupEvidenceCaptureException(
                acquisition.Status == BoundedOperationStatus.TimedOut ?
                    StartupEvidenceFailureCode.DiscoveryTimedOut :
                    StartupEvidenceFailureCode.DiscoveryFailed);
        }
        return acquisition.Resource!;
    }

    private static void RecordAcquisitionFailure(
        StartupEvidenceCaptureResult result,
        StartupEvidenceAcquisitionPhase phase, Exception? exception)
    {
        if (exception is not HardwareVerificationException hardwareFailure)
        {
            return;
        }
        result.AcquisitionFailure = new StartupEvidenceAcquisitionFailure
        {
            Phase = phase,
            Code = hardwareFailure.Code,
            Win32ErrorCode = hardwareFailure.NativeErrorCode,
        };
        result.HardwareFailureCode = hardwareFailure.Code;
        result.HardwareFailureWin32ErrorCode =
            hardwareFailure.NativeErrorCode;
    }

    private static async Task<BoundedAcquireResult<HidInputChannel>>
        AcquireInputAsync(TargetDeviceSessionIdentity target,
            CancellationToken outerToken)
    {
        using CancellationTokenSource budget = CreatePhaseBudget(outerToken,
            StartupEvidenceCapturePlan.ChannelOpenTimeoutMilliseconds);
        return await BoundedNativeOperation.TryAcquireAsync(
            () => HidInputChannel.Open(target), ReleaseInputAsync,
            budget.Token).ConfigureAwait(false);
    }

    private static async Task<BoundedAcquireResult<WinUsbCommandChannel>>
        AcquireCommandAsync(TargetDeviceSessionIdentity target,
            CancellationToken outerToken)
    {
        using CancellationTokenSource budget = CreatePhaseBudget(outerToken,
            StartupEvidenceCapturePlan.ChannelOpenTimeoutMilliseconds);
        return await BoundedNativeOperation.TryAcquireAsync(
            () => WinUsbCommandChannel.Open(target), ReleaseCommandAsync,
            budget.Token).ConfigureAwait(false);
    }

    private static async Task RequireIdentityAsync(
        TargetDeviceSessionIdentity target, CancellationToken outerToken)
    {
        using CancellationTokenSource budget = CreatePhaseBudget(outerToken,
            VerificationPlan.SessionRevalidationTimeoutMilliseconds);
        BoundedOperationStatus status = await BoundedNativeOperation
            .TryRunAsync(() => WindowsTargetDiscovery.Revalidate(target),
                budget.Token).ConfigureAwait(false);
        if (status != BoundedOperationStatus.Succeeded)
        {
            throw new StartupEvidenceCaptureException(
                StartupEvidenceFailureCode.SessionIdentityRevalidationFailed);
        }
    }

    private static async Task<CommandWireObservation> RunCommandAsync(
        WinUsbCommandChannel channel,
        StartupEvidenceCommandKind operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return await CommandOperationDeadline.RunAsync(token =>
        {
            CommandWireObservation observation = operation switch
            {
                StartupEvidenceCommandKind.PlayerLed1 =>
                    channel.CapturePlayerLedEvidence(
                        Switch2PlayerLedCommand.Player1Only, token,
                        revalidateSessionIdentity: true, out _),
                StartupEvidenceCommandKind.PlayerLedAllOff =>
                    channel.CapturePlayerLedEvidence(
                        Switch2PlayerLedCommand.AllOff, token,
                        revalidateSessionIdentity: false, out _),
                _ => channel.CaptureStartupEvidence(operation, token),
            };
            return Task.FromResult(observation);
        }, () => ReleaseCommandAsync(channel), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<WinUsbCommandChannel?>
        TryNeutralizePlayerLedAsync(TargetDeviceSessionIdentity? target,
            WinUsbCommandChannel? command, StartupEvidenceRecorder recorder,
            StartupEvidenceCaptureResult result)
    {
        if (target is null || command is null)
        {
            result.Cleanup.PlayerLedNeutralizationBlockedByOwnership = true;
            return command;
        }

        using CancellationTokenSource cleanupBudget =
            CleanupBudgetFactory.CreateLed();
        try
        {
            if (command.IsFaulted)
            {
                WinUsbCommandChannel old = command;
                command = null;
                BoundedReplacementResult<WinUsbCommandChannel> replacement =
                    await BoundedNativeOperation.TryReplaceAsync(old,
                        ReleaseCommandAsync,
                        () => WinUsbCommandChannel.Open(target),
                        ReleaseCommandAsync, cleanupBudget.Token)
                        .ConfigureAwait(false);
                if (!replacement.Succeeded)
                {
                    result.Cleanup.CommandDisposeFailed |=
                        replacement.ReleaseStatus ==
                            BoundedOperationStatus.Failed;
                    result.Cleanup.LateCommandReleaseUnconfirmed |=
                        replacement.ReleaseStatus ==
                            BoundedOperationStatus.TimedOut ||
                        replacement.Acquisition.LateReleaseUnconfirmed;
                    result.Cleanup
                        .PlayerLedNeutralizationBlockedByOwnership = true;
                    return null;
                }
                command = replacement.Acquisition.Resource;
            }

            BoundedOperationStatus identity = await BoundedNativeOperation
                .TryRunAsync(() => WindowsTargetDiscovery.Revalidate(target),
                    cleanupBudget.Token).ConfigureAwait(false);
            if (identity != BoundedOperationStatus.Succeeded ||
                command is null)
            {
                result.Cleanup
                    .PlayerLedNeutralizationBlockedByOwnership = true;
                return command;
            }

            StartupEvidenceCommandAttempt attempt = recorder.Begin(
                StartupEvidenceCommandKind.PlayerLedAllOff);
            WinUsbCommandChannel active = command;
            CommandWireObservation observation;
            try
            {
                observation = await RunCommandAsync(active,
                    StartupEvidenceCommandKind.PlayerLedAllOff,
                    cleanupBudget.Token).ConfigureAwait(false);
            }
            catch (HardwareVerificationException exception) when (
                !exception.ResourceOwnershipReturned &&
                exception.AbandonedResource ==
                    AbandonedResourceOwnership.CommandOutputWinUsb)
            {
                attempt.TransferFailureStage =
                    exception.CommandTransferStage;
                command = null;
                result.Cleanup.CommandOwnershipAbandoned = true;
                result.Cleanup.LateCommandReleaseUnconfirmed = true;
                result.Cleanup
                    .PlayerLedNeutralizationBlockedByOwnership = true;
                return null;
            }
            catch (HardwareVerificationException exception)
            {
                attempt.TransferFailureStage =
                    exception.CommandTransferStage;
                throw;
            }
            if (!recorder.TryComplete(attempt, observation) ||
                attempt.ValidationDisposition !=
                    StartupEvidenceValidationDisposition.
                        ExistingValidatorAccepted)
            {
                return command;
            }

            identity = await BoundedNativeOperation.TryRunAsync(
                () => WindowsTargetDiscovery.Revalidate(target),
                cleanupBudget.Token).ConfigureAwait(false);
            if (identity != BoundedOperationStatus.Succeeded)
            {
                return command;
            }
            result.Cleanup.PlayerLedAllOffExactResponseValidated = true;
            result.Cleanup.PlayerLedAllOffSucceeded = true;
        }
        catch
        {
            // No exception detail crosses the sanitized evidence boundary.
        }
        return command;
    }

    private static async Task<WinUsbCommandChannel?> DisposeCommandAsync(
        WinUsbCommandChannel? command, StartupEvidenceCaptureResult result)
    {
        if (command is null)
        {
            return null;
        }
        using CancellationTokenSource budget =
            CleanupBudgetFactory.CreateChannelDispose();
        BoundedOperationStatus status = await BoundedNativeOperation
            .TryRunAsync(command.Dispose, budget.Token).ConfigureAwait(false);
        result.Cleanup.CommandDisposeFailed |=
            status == BoundedOperationStatus.Failed;
        result.Cleanup.LateCommandReleaseUnconfirmed |=
            status == BoundedOperationStatus.TimedOut;
        return null;
    }

    private static async Task<HidInputChannel?> DisposeInputAsync(
        HidInputChannel? input, StartupEvidenceCaptureResult result)
    {
        if (input is null)
        {
            return null;
        }
        using CancellationTokenSource budget =
            CleanupBudgetFactory.CreateChannelDispose();
        BoundedOperationStatus status = await BoundedNativeOperation
            .TryRunAsync(() => ReleaseInputAsync(input), budget.Token)
            .ConfigureAwait(false);
        result.Cleanup.InputDisposeFailed |=
            status == BoundedOperationStatus.Failed;
        result.Cleanup.LateInputReleaseUnconfirmed |=
            status == BoundedOperationStatus.TimedOut;
        return null;
    }

    private static void CopyRate(InputRateCapture source,
        InputRateResult destination)
    {
        destination.ExactReports = VerificationPlan.InputReportCount;
        destination.ObservedReportsPerSecond = source.Timing.ReportsPerSecond;
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

    private static StartupEvidenceFailureCode MapFailure(
        HardwareVerificationException exception, bool interactionCancelled,
        bool userCancelled)
    {
        if (userCancelled)
        {
            return StartupEvidenceFailureCode.Cancelled;
        }
        if (interactionCancelled &&
            exception.Code == VerificationFailureCode.Cancelled)
        {
            return StartupEvidenceFailureCode.InteractionTimedOut;
        }
        return exception.Code switch
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
            VerificationFailureCode.Cancelled =>
                StartupEvidenceFailureCode.InteractionTimedOut,
            _ => StartupEvidenceFailureCode.UnexpectedFailure,
        };
    }

    private static CancellationTokenSource CreatePhaseBudget(
        CancellationToken outerToken, int milliseconds)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(
            outerToken);
        budget.CancelAfter(milliseconds);
        return budget;
    }

    private static async Task ReleaseInputAsync(HidInputChannel input) =>
        await input.DisposeAsync().ConfigureAwait(false);

    private static Task ReleaseCommandAsync(WinUsbCommandChannel command)
    {
        command.Dispose();
        return Task.CompletedTask;
    }

    private sealed class StartupEvidenceCaptureException : Exception
    {
        internal StartupEvidenceCaptureException(
            StartupEvidenceFailureCode code) : base(code.ToString())
        {
            Code = code;
        }

        internal StartupEvidenceFailureCode Code { get; }
    }
}
