using DS4Windows.Switch2;

namespace DS4Windows.Switch2.Verification;

internal static class VerificationPlan
{
    internal const ushort VendorId = 0x057E;
    internal const ushort ProductId = 0x2069;
    internal const ushort DeviceReleaseBcd = 0x0201;
    internal const string HidInterfaceMarker = "MI_00";
    internal const string WinUsbInterfaceMarker = "MI_01";
    internal const string HidService = "HidUsb";
    internal const string WinUsbService = "WinUSB";
    internal const byte BulkOutPipeId = 0x02;
    internal const byte BulkInPipeId = 0x82;
    internal const ushort BulkMaximumPacketSize = 64;
    internal const byte HidInputReportId = 0x05;
    internal const int HidReportLength = 64;
    internal const int InputWarmupReportCount = 1_024;
    internal const int InputWarmupLiveTailIntervals = 8;
    internal const int InputWarmupMinimumIntervalMicroseconds = 500;
    internal const int InputReportCount = 256;
    internal const int InputReadTimeoutMilliseconds = 1_000;
    // 1,280 reports require about 5.12 seconds even at only 250 Hz. Keep the
    // complete phase finite while leaving nearly three times that interval for
    // scheduler and USB jitter.
    internal const int InputCaptureTimeoutMilliseconds = 15_000;
    internal const int CommandTimeoutMilliseconds = 500;
    internal const int CommandOperationTimeoutMilliseconds = 1_250;
    internal const int HapticCleanupTimeoutMilliseconds = 1_000;
    internal const int LedCleanupTimeoutMilliseconds = 1_500;
    internal const int ChannelDisposeTimeoutMilliseconds = 500;
    internal const int SessionRevalidationTimeoutMilliseconds = 1_500;
    internal const int HapticWriteTimeoutMilliseconds = 250;
    internal const ushort Oscillator0Control = 0x187;
    internal const ushort Oscillator1Control = 0x112;
    internal const ushort BasisAmplitude = 64;
    internal const ushort StopAmplitude = 0;
    internal const ushort SdlClampAmplitudeCode = 453;
    // Fourteen basis attempts leave two never-reused 4-bit sequence values
    // for the bounded idempotent stop attempts in the same procedure.
    internal const int HapticFrameCount = 14;
    internal const int HapticCadenceMilliseconds = 12;
    internal const int HapticMaximumDurationMilliseconds = 250;
    internal const int StopMaximumAttempts = 2;

    // A process/handle exit does not prove physical zero amplitude after a
    // noncooperative kernel write, and this protocol has no haptic readback or
    // admitted watchdog guarantee. Keep live nonzero output closed until a
    // separately reviewed neutralization mechanism exists.
    internal static bool LiveHapticMutationSafetyGateOpen => false;

    internal static Switch2HdRumbleSubframe BasisSubframe => new(
        Oscillator0Control, BasisAmplitude,
        Oscillator1Control, BasisAmplitude);

    internal static Switch2HdRumbleSubframe StopSubframe => new(
        Oscillator0Control, StopAmplitude,
        Oscillator1Control, StopAmplitude);

    internal static bool TryValidate(out string failure)
    {
        ushort vendorId = VendorId;
        ushort productId = ProductId;
        ushort deviceRelease = DeviceReleaseBcd;
        byte bulkOut = BulkOutPipeId;
        byte bulkIn = BulkInPipeId;
        ushort bulkPacketSize = BulkMaximumPacketSize;
        int reportLength = HidReportLength;
        int frameCount = HapticFrameCount;
        int cadence = HapticCadenceMilliseconds;
        int maximumDuration = HapticMaximumDurationMilliseconds;
        int stopAttempts = StopMaximumAttempts;
        int inputCount = InputReportCount;
        int inputCaptureTimeout = InputCaptureTimeoutMilliseconds;
        ushort basisAmplitude = BasisAmplitude;
        ushort clampAmplitude = SdlClampAmplitudeCode;
        ushort stopAmplitude = StopAmplitude;
        int sessionRevalidationTimeout =
            SessionRevalidationTimeoutMilliseconds;
        int channelDisposeTimeout = ChannelDisposeTimeoutMilliseconds;
        int commandOperationTimeout = CommandOperationTimeoutMilliseconds;
        int hapticCleanupTimeout = HapticCleanupTimeoutMilliseconds;
        int ledCleanupTimeout = LedCleanupTimeoutMilliseconds;

        if (vendorId == 0 || productId == 0 || deviceRelease != 0x0201)
        {
            failure = "identity";
            return false;
        }
        if (bulkOut != 0x02 || bulkIn != 0x82 ||
            (bulkOut & 0x80) != 0 || (bulkIn & 0x80) == 0 ||
            bulkPacketSize != 64)
        {
            failure = "bulk-topology";
            return false;
        }
        if (reportLength != Switch2UsbHdRumbleCodec.ReportLength ||
            frameCount <= 0 || cadence <= 0 ||
            frameCount * cadence > maximumDuration ||
            frameCount + stopAttempts > 16)
        {
            failure = "haptic-duration";
            return false;
        }
        if (basisAmplitude == 0 || basisAmplitude >= clampAmplitude ||
            stopAmplitude != 0 ||
            BasisSubframe.Oscillator0ControlCode !=
                StopSubframe.Oscillator0ControlCode ||
            BasisSubframe.Oscillator1ControlCode !=
                StopSubframe.Oscillator1ControlCode)
        {
            failure = "haptic-basis";
            return false;
        }
        int inputPhaseReports = InputWarmupReportCount + inputCount;
        int conservative250HzMilliseconds = checked(inputPhaseReports * 4);
        if (stopAttempts != 2 || inputCount != 256 ||
            inputCaptureTimeout < conservative250HzMilliseconds * 2 ||
            inputCaptureTimeout > 30_000)
        {
            failure = "bounds";
            return false;
        }
        if (sessionRevalidationTimeout <= 0 || channelDisposeTimeout <= 0 ||
            commandOperationTimeout <= CommandTimeoutMilliseconds ||
            commandOperationTimeout >= ledCleanupTimeout ||
            hapticCleanupTimeout <= 0 || ledCleanupTimeout <= 0)
        {
            failure = "cleanup-bounds";
            return false;
        }

        Span<byte> report = stackalloc byte[HidReportLength];
        if (!TryWriteHapticReport(0, BasisSubframe, report) ||
            !TryWriteHapticReport(0, StopSubframe, report))
        {
            failure = "linked-codec";
            return false;
        }

        Span<byte> initializationRequest = stackalloc byte[
            Switch2UsbCommandCodec.InitializationRequestLength];
        ReadOnlySpan<byte> enableResponse =
            stackalloc byte[]
            {
                0x03, 0x01, 0x00, 0x03, 0x00, 0xF8, 0x00, 0x00,
                0x01, 0x00, 0x00, 0x00,
            };
        ReadOnlySpan<byte> selectResponse =
            stackalloc byte[]
            {
                0x03, 0x01, 0x00, 0x0A, 0x00, 0xF8, 0x00, 0x00,
            };
        if (!Switch2UsbCommandCodec.TryWriteInitializationRequest(
                Switch2UsbInitializationStep.EnableUsbHidReports,
                initializationRequest, out _) ||
            !Switch2UsbCommandCodec.TryValidateInitializationRequest(
                initializationRequest,
                Switch2UsbInitializationStep.EnableUsbHidReports, out _) ||
            !Switch2UsbCommandCodec.TryValidateInitializationResponse(
                enableResponse,
                Switch2UsbInitializationStep.EnableUsbHidReports, out _) ||
            !Switch2UsbCommandCodec.TryWriteInitializationRequest(
                Switch2UsbInitializationStep.SelectCommonInputReport,
                initializationRequest, out _) ||
            !Switch2UsbCommandCodec.TryValidateInitializationRequest(
                initializationRequest,
                Switch2UsbInitializationStep.SelectCommonInputReport,
                out _) ||
            !Switch2UsbCommandCodec.TryValidateInitializationResponse(
                selectResponse,
                Switch2UsbInitializationStep.SelectCommonInputReport, out _))
        {
            failure = "linked-initialization-codec";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    internal static bool TryWriteHapticReport(byte counter,
        in Switch2HdRumbleSubframe subframe, Span<byte> destination)
    {
        if (counter > 0x0F || destination.Length != HidReportLength)
        {
            return false;
        }

        if (!Switch2UsbHdRumbleCodec.TryEncodeSdlCompatibility(
                Switch2UsbHdRumbleCodec.ProControllerReportId, counter,
                subframe, destination) ||
            !Switch2UsbHdRumbleCodec.TryDecodeProController(destination,
                out byte decodedCounter, out Switch2HdRumbleGroup left,
                out Switch2HdRumbleGroup right, out _) ||
            decodedCounter != counter || !left.First.Equals(subframe) ||
            !right.First.Equals(subframe) ||
            !left.Second.Equals(default) || !left.Third.Equals(default) ||
            !right.Second.Equals(default) || !right.Third.Equals(default))
        {
            destination.Clear();
            return false;
        }
        return true;
    }
}

internal static class CleanupBudgetFactory
{
    internal static CancellationTokenSource CreateHaptic() => new(
        VerificationPlan.HapticCleanupTimeoutMilliseconds);

    internal static CancellationTokenSource CreateLed() => new(
        VerificationPlan.LedCleanupTimeoutMilliseconds);

    internal static CancellationTokenSource CreateChannelDispose() => new(
        VerificationPlan.ChannelDisposeTimeoutMilliseconds);
}

internal enum BoundedOperationStatus
{
    Succeeded,
    Failed,
    TimedOut,
}

internal readonly record struct BoundedAcquireResult<T>(
    BoundedOperationStatus Status, T? Resource,
    bool LateReleaseUnconfirmed = false,
    Exception? Failure = null) where T : class
{
    internal bool Succeeded => Status == BoundedOperationStatus.Succeeded &&
        Resource is not null;
}

internal readonly record struct BoundedReplacementResult<T>(
    BoundedOperationStatus ReleaseStatus,
    BoundedAcquireResult<T> Acquisition) where T : class
{
    internal bool Succeeded =>
        ReleaseStatus == BoundedOperationStatus.Succeeded &&
        Acquisition.Succeeded;
}

internal static class BoundedNativeOperation
{
    internal static Task<BoundedOperationStatus> TryRunAsync(Action operation,
        CancellationToken token) => TryRunAsync(
        () =>
        {
            operation();
            return Task.CompletedTask;
        }, token);

    internal static async Task<BoundedOperationStatus> TryRunAsync(
        Func<Task> operation, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return BoundedOperationStatus.TimedOut;
        }
        Task pending;
        try
        {
            pending = Task.Run(operation, CancellationToken.None);
        }
        catch
        {
            return BoundedOperationStatus.Failed;
        }

        try
        {
            await pending.WaitAsync(token).ConfigureAwait(false);
            return BoundedOperationStatus.Succeeded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = ObserveAsync(pending);
            return BoundedOperationStatus.TimedOut;
        }
        catch
        {
            return BoundedOperationStatus.Failed;
        }
    }

    internal static async Task<BoundedAcquireResult<T>> TryAcquireAsync<T>(
        Func<T> acquire, Func<T, Task> releaseLate,
        CancellationToken token) where T : class
    {
        if (token.IsCancellationRequested)
        {
            return new(BoundedOperationStatus.TimedOut, null);
        }

        Task<T> pending;
        try
        {
            pending = Task.Run(acquire, CancellationToken.None);
        }
        catch (Exception exception)
        {
            return new(BoundedOperationStatus.Failed, null,
                Failure: exception);
        }
        try
        {
            T resource = await pending.WaitAsync(token).ConfigureAwait(false);
            if (resource is null)
            {
                return new(BoundedOperationStatus.Failed, null);
            }
            if (token.IsCancellationRequested)
            {
                _ = ReleaseLateAsync(Task.FromResult(resource), releaseLate);
                return new(BoundedOperationStatus.TimedOut, null,
                    LateReleaseUnconfirmed: true);
            }
            return new(BoundedOperationStatus.Succeeded, resource);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = ReleaseLateAsync(pending, releaseLate);
            return new(BoundedOperationStatus.TimedOut, null,
                LateReleaseUnconfirmed: true);
        }
        catch (Exception exception)
        {
            return new(BoundedOperationStatus.Failed, null,
                Failure: exception);
        }
    }

    internal static async Task<BoundedReplacementResult<T>> TryReplaceAsync<T>(
        T oldResource, Func<T, Task> releaseOld, Func<T> acquireReplacement,
        Func<T, Task> releaseLateReplacement, CancellationToken token)
        where T : class
    {
        BoundedOperationStatus released = await TryRunAsync(
            () => releaseOld(oldResource), token).ConfigureAwait(false);
        if (released != BoundedOperationStatus.Succeeded)
        {
            return new(released,
                new BoundedAcquireResult<T>(released, null));
        }

        // A replacement writer is never opened until releaseOld completed.
        BoundedAcquireResult<T> acquisition = await TryAcquireAsync(
            acquireReplacement, releaseLateReplacement, token)
            .ConfigureAwait(false);
        return new(released, acquisition);
    }

    private static async Task ObserveAsync(Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
            // The timed-out caller already recorded the operation as incomplete.
        }
    }

    private static async Task ReleaseLateAsync<T>(Task<T> pending,
        Func<T, Task> releaseLate) where T : class
    {
        try
        {
            T resource = await pending.ConfigureAwait(false);
            if (resource is not null)
            {
                await releaseLate(resource).ConfigureAwait(false);
            }
        }
        catch
        {
            // A late result is never admitted. Best-effort disposal cannot
            // change the already reported timeout/failure outcome.
        }
    }
}

internal enum ExclusiveResourceHandoffState
{
    Active,
    Completed,
    Abandoned,
}

internal sealed class ExclusiveResourceHandoff
{
    private int state = (int)ExclusiveResourceHandoffState.Active;

    internal ExclusiveResourceHandoffState State =>
        (ExclusiveResourceHandoffState)Volatile.Read(ref state);

    internal bool TryMarkCompleted() => Interlocked.CompareExchange(ref state,
        (int)ExclusiveResourceHandoffState.Completed,
        (int)ExclusiveResourceHandoffState.Active) ==
        (int)ExclusiveResourceHandoffState.Active;

    internal bool TryAbandon() => Interlocked.CompareExchange(ref state,
        (int)ExclusiveResourceHandoffState.Abandoned,
        (int)ExclusiveResourceHandoffState.Active) ==
        (int)ExclusiveResourceHandoffState.Active;
}

internal static class CleanupOrderCoordinator
{
    internal static async Task<CleanupOrderResult> RunLedThenHapticAsync(
        Func<CancellationToken, Task> ledCleanup,
        Func<CancellationTokenSource> createLedBudget,
        Func<CancellationToken, Task> hapticCleanup,
        Func<CancellationTokenSource> createHapticBudget) =>
        await RunLedThenHapticAsync(ledCleanup, createLedBudget,
            static () => Task.CompletedTask, hapticCleanup,
            createHapticBudget, static () => Task.CompletedTask)
            .ConfigureAwait(false);

    internal static async Task<CleanupOrderResult> RunLedThenHapticAsync(
        Func<CancellationToken, Task> ledCleanup,
        Func<CancellationTokenSource> createLedBudget,
        Func<Task> releaseAbandonedLedChannel,
        Func<CancellationToken, Task> hapticCleanup,
        Func<CancellationTokenSource> createHapticBudget,
        Func<Task> releaseAbandonedHidChannel)
    {
        bool ledFinished = await RunArmAsync(ledCleanup, createLedBudget,
            releaseAbandonedLedChannel).ConfigureAwait(false);

        // The haptic budget starts only after the LED arm has finished. A slow
        // or noncooperative LED attempt cannot consume or suppress the
        // independent haptic cleanup window.
        bool hapticFinished = await RunArmAsync(hapticCleanup,
            createHapticBudget, releaseAbandonedHidChannel)
            .ConfigureAwait(false);
        return new CleanupOrderResult(ledFinished, hapticFinished);
    }

    private static async Task<bool> RunArmAsync(
        Func<CancellationToken, Task> cleanup,
        Func<CancellationTokenSource> createBudget,
        Func<Task> releaseAbandonedChannel)
    {
        CancellationTokenSource? budget = null;
        bool lifetimeTransferred = false;
        try
        {
            budget = createBudget();
            if (budget.IsCancellationRequested)
            {
                return true;
            }

            var handoff = new ExclusiveResourceHandoff();
            // Invoke on a worker so a callback that blocks before returning its
            // Task cannot defeat the hard arm boundary.
            Task pending = Task.Run(() => cleanup(budget.Token),
                CancellationToken.None);
            _ = ObserveHandoffAsync(pending, handoff, budget,
                releaseAbandonedChannel);
            try
            {
                await pending.WaitAsync(budget.Token).ConfigureAwait(false);
                handoff.TryMarkCompleted();
                return true;
            }
            catch (OperationCanceledException) when (
                budget.IsCancellationRequested)
            {
                if (handoff.TryAbandon())
                {
                    lifetimeTransferred = true;
                    return false;
                }

                // Completion won the atomic handoff race. The task no longer
                // owns the channel, even if cancellation won WaitAsync.
                try
                {
                    await pending.ConfigureAwait(false);
                }
                catch
                {
                    // A completed fault still returned channel ownership.
                }
                return true;
            }
            catch
            {
                handoff.TryMarkCompleted();
                return true;
            }
        }
        catch
        {
            // No active worker owns the channel when setup itself fails.
            return true;
        }
        finally
        {
            if (!lifetimeTransferred)
            {
                budget?.Dispose();
            }
        }
    }

    private static async Task ObserveHandoffAsync(Task pending,
        ExclusiveResourceHandoff handoff, CancellationTokenSource budget,
        Func<Task> releaseAbandonedChannel)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
            // Ownership, not callback success, controls the handoff below.
        }

        if (handoff.TryMarkCompleted() ||
            handoff.State != ExclusiveResourceHandoffState.Abandoned)
        {
            return;
        }
        try
        {
            await releaseAbandonedChannel().ConfigureAwait(false);
        }
        catch
        {
            // The serialized result already reports release as unconfirmed.
        }
        finally
        {
            budget.Dispose();
        }
    }
}

internal readonly record struct CleanupOrderResult(bool LedArmFinished,
    bool HapticArmFinished)
{
    internal bool CommandOwnershipReturned => LedArmFinished;
    internal bool HidOwnershipReturned => HapticArmFinished;
}

internal static class InputCaptureDeadline
{
    internal static Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> capture,
        Func<Task> releaseAbandonedChannel,
        CancellationToken userCancellationToken) =>
        OwnedOperationDeadline.RunAsync(capture, releaseAbandonedChannel,
            userCancellationToken,
            TimeSpan.FromMilliseconds(
                VerificationPlan.InputCaptureTimeoutMilliseconds),
            VerificationFailureCode.InputCapturePhaseTimedOut,
            AbandonedResourceOwnership.InputCaptureHid);

    // The timeout overload exists for deterministic pure tests. Production
    // has no caller-controlled duration surface.
    internal static Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> capture,
        Func<Task> releaseAbandonedChannel,
        CancellationToken userCancellationToken, TimeSpan timeout) =>
        OwnedOperationDeadline.RunAsync(capture, releaseAbandonedChannel,
            userCancellationToken, timeout,
            VerificationFailureCode.InputCapturePhaseTimedOut,
            AbandonedResourceOwnership.InputCaptureHid);
}

internal static class CommandOperationDeadline
{
    internal static Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Task> releaseAbandonedChannel,
        CancellationToken userCancellationToken) =>
        OwnedOperationDeadline.RunAsync(operation, releaseAbandonedChannel,
            userCancellationToken,
            TimeSpan.FromMilliseconds(
                VerificationPlan.CommandOperationTimeoutMilliseconds),
            VerificationFailureCode.CommandOperationTimedOut,
            AbandonedResourceOwnership.CommandOutputWinUsb);

    // The timeout overload exists only for deterministic pure tests.
    internal static Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Task> releaseAbandonedChannel,
        CancellationToken userCancellationToken, TimeSpan timeout) =>
        OwnedOperationDeadline.RunAsync(operation, releaseAbandonedChannel,
            userCancellationToken, timeout,
            VerificationFailureCode.CommandOperationTimedOut,
            AbandonedResourceOwnership.CommandOutputWinUsb);
}

internal static class HapticPhaseDeadline
{
    internal static async Task RunAsync(
        Func<CancellationToken, Task> writePhase,
        Func<Task> releaseAbandonedChannel,
        CancellationToken userCancellationToken)
    {
        await OwnedOperationDeadline.RunAsync(async token =>
        {
            await writePhase(token).ConfigureAwait(false);
            return true;
        }, releaseAbandonedChannel, userCancellationToken,
            TimeSpan.FromMilliseconds(
                VerificationPlan.HapticMaximumDurationMilliseconds),
            VerificationFailureCode.HapticPhaseTimedOut,
            AbandonedResourceOwnership.HapticOutputHid)
            .ConfigureAwait(false);
    }
}

internal static class OwnedOperationDeadline
{
    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Task> releaseAbandonedChannel,
        CancellationToken userCancellationToken, TimeSpan timeout,
        VerificationFailureCode timeoutFailureCode,
        AbandonedResourceOwnership abandonedResource)
    {
        if (userCancellationToken.IsCancellationRequested)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.Cancelled);
        }
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var phase = CancellationTokenSource.CreateLinkedTokenSource(
            userCancellationToken);
        phase.CancelAfter(timeout);
        if (phase.IsCancellationRequested)
        {
            phase.Dispose();
            throw new HardwareVerificationException(
                userCancellationToken.IsCancellationRequested ?
                    VerificationFailureCode.Cancelled : timeoutFailureCode);
        }

        bool lifetimeTransferred = false;
        var handoff = new ExclusiveResourceHandoff();
        var userCancellationSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration userCancellationRegistration =
            userCancellationToken.Register(static state =>
                ((TaskCompletionSource)state!).TrySetResult(),
                userCancellationSignal);
        Task<T> pending;
        try
        {
            pending = Task.Run(() => operation(phase.Token),
                CancellationToken.None);
        }
        catch
        {
            userCancellationRegistration.Unregister();
            phase.Dispose();
            throw;
        }
        _ = ObserveHandoffAsync(pending, handoff, phase,
            releaseAbandonedChannel);
        try
        {
            Task timeoutTask = Task.Delay(timeout);
            Task winner = await Task.WhenAny(pending, timeoutTask,
                userCancellationSignal.Task).ConfigureAwait(false);
            if (winner != pending)
            {
                // CancelAsync marks the operation token without allowing a
                // blocking cancellation callback to hold up this ownership
                // decision. Only the late observer may touch the resource once
                // abandonment wins below.
                _ = phase.CancelAsync();
                if (handoff.TryAbandon())
                {
                    lifetimeTransferred = true;
                    throw new HardwareVerificationException(
                        userCancellationToken.IsCancellationRequested ?
                            VerificationFailureCode.Cancelled :
                            timeoutFailureCode, abandonedResource);
                }

                // Completion won the atomic race. Consume its exact outcome;
                // the observer did not acquire late-disposal authority.
            }

            T result = await pending.ConfigureAwait(false);
            handoff.TryMarkCompleted();
            return result;
        }
        catch (HardwareVerificationException exception) when (
            exception.ResourceOwnershipReturned &&
            exception.Code == VerificationFailureCode.Cancelled &&
            (userCancellationToken.IsCancellationRequested ||
                phase.IsCancellationRequested))
        {
            handoff.TryMarkCompleted();
            throw new HardwareVerificationException(
                userCancellationToken.IsCancellationRequested ?
                    VerificationFailureCode.Cancelled :
                    timeoutFailureCode);
        }
        catch (OperationCanceledException) when (
            userCancellationToken.IsCancellationRequested ||
            phase.IsCancellationRequested)
        {
            if (handoff.TryAbandon())
            {
                lifetimeTransferred = true;
                throw new HardwareVerificationException(
                    userCancellationToken.IsCancellationRequested ?
                        VerificationFailureCode.Cancelled :
                        timeoutFailureCode, abandonedResource);
            }

            // Completion won. Consume its exact outcome while ownership stays
            // with Main; the observer will not release the channel.
            try
            {
                return await pending.ConfigureAwait(false);
            }
            catch (HardwareVerificationException exception) when (
                exception.Code == VerificationFailureCode.Cancelled)
            {
                throw new HardwareVerificationException(
                    userCancellationToken.IsCancellationRequested ?
                        VerificationFailureCode.Cancelled :
                        timeoutFailureCode);
            }
            catch (OperationCanceledException)
            {
                throw new HardwareVerificationException(
                    userCancellationToken.IsCancellationRequested ?
                        VerificationFailureCode.Cancelled :
                        timeoutFailureCode);
            }
        }
        catch
        {
            handoff.TryMarkCompleted();
            throw;
        }
        finally
        {
            userCancellationRegistration.Unregister();
            if (!lifetimeTransferred)
            {
                phase.Dispose();
            }
        }
    }

    private static async Task ObserveHandoffAsync(Task pending,
        ExclusiveResourceHandoff handoff,
        CancellationTokenSource phase,
        Func<Task> releaseAbandonedChannel)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
            // Ownership, not operation success, controls the handoff below.
        }
        if (handoff.TryMarkCompleted() ||
            handoff.State != ExclusiveResourceHandoffState.Abandoned)
        {
            return;
        }
        try
        {
            await releaseAbandonedChannel().ConfigureAwait(false);
        }
        catch
        {
            // Main has already reported the abandoned release as unconfirmed.
        }
        finally
        {
            phase.Dispose();
        }
    }
}

internal sealed class OutputLeaseGate
{
    private bool hidLease;
    private bool commandLease;

    internal bool CanBeginHaptics => hidLease && commandLease;

    internal void RegisterHidLease() => hidLease = true;

    internal void RegisterCommandLease() => commandLease = true;

    internal void RequireBoth()
    {
        if (!CanBeginHaptics)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.OutputLeaseIncomplete);
        }
    }
}

internal sealed class HapticAttemptSequence
{
    private int next;

    internal int Reservations => next;

    internal bool TryReserve(out byte counter)
    {
        if (next >= 16)
        {
            counter = 0;
            return false;
        }
        counter = checked((byte)next);
        next++;
        return true;
    }
}

internal readonly record struct PipeFact(byte PipeId, NativePipeType PipeType,
    ushort MaximumPacketSize, byte Interval);

internal readonly record struct UsbPipeTopologyObservation(
    byte InterfaceNumber, byte AlternateSetting, byte EndpointCount,
    PipeFact Pipe0, PipeFact Pipe1);

internal readonly record struct WinUsbPipePolicyObservation(
    uint AllowPartialReadsValueLength, uint AllowPartialReadsValue);

internal enum NativePipeType
{
    Control = 0,
    Isochronous = 1,
    Bulk = 2,
    Interrupt = 3,
}

internal static class PipeTopologyValidator
{
    internal static bool TryValidate(byte interfaceNumber,
        byte alternateSetting, ReadOnlySpan<PipeFact> pipes,
        out PipeFact bulkOut, out PipeFact bulkIn)
    {
        bulkOut = default;
        bulkIn = default;
        if (interfaceNumber != 1 || alternateSetting != 0 || pipes.Length != 2)
        {
            return false;
        }

        foreach (PipeFact pipe in pipes)
        {
            if (pipe.PipeType != NativePipeType.Bulk || pipe.Interval != 0 ||
                pipe.MaximumPacketSize !=
                    VerificationPlan.BulkMaximumPacketSize)
            {
                return false;
            }
            if (pipe.PipeId == VerificationPlan.BulkOutPipeId &&
                bulkOut == default)
            {
                bulkOut = pipe;
            }
            else if (pipe.PipeId == VerificationPlan.BulkInPipeId &&
                bulkIn == default)
            {
                bulkIn = pipe;
            }
            else
            {
                return false;
            }
        }

        return bulkOut.PipeId == VerificationPlan.BulkOutPipeId &&
            bulkIn.PipeId == VerificationPlan.BulkInPipeId;
    }
}

internal static class ActiveAlternateSettingValidator
{
    internal static bool IsExactDefault(byte currentAlternateSetting) =>
        currentAlternateSetting == 0;
}

internal static class TargetIdentityRules
{
    internal static bool IsHidCollection(string instanceId,
        string hardwareIds, Guid containerId, ushort vendorId,
        ushort productId, ushort versionNumber) =>
        HasIdentity(instanceId, hardwareIds,
            VerificationPlan.HidInterfaceMarker) &&
        containerId != Guid.Empty && vendorId == VerificationPlan.VendorId &&
        productId == VerificationPlan.ProductId &&
        versionNumber == VerificationPlan.DeviceReleaseBcd;

    internal static bool IsHidParent(string instanceId, string hardwareIds,
        string service, Guid containerId, Guid expectedContainerId) =>
        HasIdentity(instanceId, hardwareIds,
            VerificationPlan.HidInterfaceMarker) &&
        string.Equals(service, VerificationPlan.HidService,
            StringComparison.OrdinalIgnoreCase) &&
        containerId != Guid.Empty && containerId == expectedContainerId;

    internal static bool IsWinUsbNode(string instanceId, string hardwareIds,
        string service, Guid containerId, Guid expectedContainerId) =>
        HasIdentity(instanceId, hardwareIds,
            VerificationPlan.WinUsbInterfaceMarker) &&
        string.Equals(service, VerificationPlan.WinUsbService,
            StringComparison.OrdinalIgnoreCase) &&
        containerId != Guid.Empty && containerId == expectedContainerId;

    private static bool HasIdentity(string instanceId, string hardwareIds,
        string interfaceMarker) =>
        TargetIdentityMarker.HasExactInterfaceIdentity(instanceId,
            interfaceMarker) ||
        TargetIdentityMarker.HasExactInterfaceIdentity(hardwareIds,
            interfaceMarker);
}

internal static class TargetIdentityMarker
{
    private static readonly char[] ComponentSeparators = ['\\', '&', '#'];

    internal static bool HasExactInterfaceIdentity(string value,
        string interfaceMarker)
    {
        foreach (string entry in value.Split('\0',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string[] components = entry.Split(ComponentSeparators,
                StringSplitOptions.RemoveEmptyEntries);
            if (components.Contains("VID_057E",
                    StringComparer.OrdinalIgnoreCase) &&
                components.Contains("PID_2069",
                    StringComparer.OrdinalIgnoreCase) &&
                components.Contains(interfaceMarker,
                    StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
