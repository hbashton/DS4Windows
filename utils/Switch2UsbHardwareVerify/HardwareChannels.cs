using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DS4Windows.Switch2;
using Microsoft.Win32.SafeHandles;

namespace DS4Windows.Switch2.Verification;

internal sealed class HidInputChannel : IAsyncDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    internal const uint InputShareMode = FileShareRead | FileShareWrite;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private readonly FileStream stream;

    private HidInputChannel(FileStream stream)
    {
        this.stream = stream;
    }

    internal static HidInputChannel Open(TargetDeviceSessionIdentity target)
    {
        WindowsTargetDiscovery.Revalidate(target);
        SafeFileHandle handle = NativeMethods.CreateFileW(
            target.Hid.InterfacePath, GenericRead,
            InputShareMode, IntPtr.Zero, OpenExisting,
            FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int nativeErrorCode = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new HardwareVerificationException(
                VerificationFailureCode.HidReadOpenFailed,
                nativeErrorCode: nativeErrorCode);
        }

        var attributes = new WindowsTargetDiscovery.HiddAttributes
        {
            Size = Marshal.SizeOf<WindowsTargetDiscovery.HiddAttributes>(),
        };
        if (!NativeMethods.HidD_GetAttributes(handle, ref attributes) ||
            attributes.VendorId != VerificationPlan.VendorId ||
            attributes.ProductId != VerificationPlan.ProductId ||
            attributes.VersionNumber != VerificationPlan.DeviceReleaseBcd)
        {
            handle.Dispose();
            throw new HardwareVerificationException(
                VerificationFailureCode.HidIdentityChanged);
        }
        if (!WindowsTargetDiscovery.HasExpectedHidCaps(handle))
        {
            handle.Dispose();
            throw new HardwareVerificationException(
                VerificationFailureCode.HidReportTopologyMismatch);
        }

        return new HidInputChannel(new FileStream(handle, FileAccess.Read,
            VerificationPlan.HidReportLength, isAsync: true));
    }

    internal async Task<InputRateCapture> CollectInputRateAsync(
        CancellationToken cancellationToken)
    {
        var report = new byte[VerificationPlan.HidReportLength];
        var warmupTicks = new long[VerificationPlan.InputWarmupReportCount];
        for (int index = 0;
             index < VerificationPlan.InputWarmupReportCount; index++)
        {
            await ReadExactCommon05Async(report, cancellationToken)
                .ConfigureAwait(false);
            warmupTicks[index] = Stopwatch.GetTimestamp();
        }
        if (!WarmupDrainValidator.HasLiveTail(warmupTicks,
                Stopwatch.Frequency))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InputBacklogNotDrained);
        }

        var completionTicks = new long[VerificationPlan.InputReportCount];
        var counters = new uint[VerificationPlan.InputReportCount];
        for (int index = 0; index < completionTicks.Length; index++)
        {
            await ReadExactCommon05Async(report, cancellationToken)
                .ConfigureAwait(false);
            completionTicks[index] = Stopwatch.GetTimestamp();
            counters[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                report.AsSpan(1, sizeof(uint)));
        }

        if (!Common05CounterObservation.TryAnalyze(counters,
                out Common05CounterObservation counterObservation))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InputCounterInvalid);
        }

        return new InputRateCapture(
            InputRateObservation.FromCompletionTicks(completionTicks,
                Stopwatch.Frequency), counterObservation);
    }

    private async Task ReadExactCommon05Async(byte[] report,
        CancellationToken cancellationToken)
    {
        Array.Clear(report);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(VerificationPlan.InputReadTimeoutMilliseconds);
        int read;
        try
        {
            read = await stream.ReadAsync(report.AsMemory(), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new HardwareVerificationException(
                cancellationToken.IsCancellationRequested ?
                    VerificationFailureCode.Cancelled :
                    VerificationFailureCode.InputReadFailed);
        }
        catch (IOException)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InputReadFailed);
        }

        if (read != VerificationPlan.HidReportLength ||
            report[0] != VerificationPlan.HidInputReportId)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InputReportInvalid);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName,
            uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle,
            ref WindowsTargetDiscovery.HiddAttributes attributes);
    }
}

internal sealed class HidReadWriteChannel : IAsyncDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    // Read-only observers may coexist; any handle with write access conflicts
    // with this share mode, preserving one physical output writer.
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private readonly FileStream stream;
    private readonly HidOutputAttempt output;

    internal bool IsOutputFaulted => output.IsFaulted;

    private HidReadWriteChannel(FileStream stream)
    {
        this.stream = stream;
        output = new HidOutputAttempt((report, token) =>
            stream.WriteAsync(report, token));
    }

    internal static HidReadWriteChannel Open(
        TargetDeviceSessionIdentity target)
    {
        WindowsTargetDiscovery.Revalidate(target);
        SafeFileHandle handle = NativeMethods.CreateFileW(
            target.Hid.InterfacePath, GenericRead | GenericWrite,
            FileShareRead, IntPtr.Zero, OpenExisting,
            FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int nativeErrorCode = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new HardwareVerificationException(
                VerificationFailureCode.HidReadWriteOpenFailed,
                nativeErrorCode: nativeErrorCode);
        }

        var attributes = new WindowsTargetDiscovery.HiddAttributes
        {
            Size = Marshal.SizeOf<WindowsTargetDiscovery.HiddAttributes>(),
        };
        if (!NativeMethods.HidD_GetAttributes(handle, ref attributes) ||
            attributes.VendorId != VerificationPlan.VendorId ||
            attributes.ProductId != VerificationPlan.ProductId ||
            attributes.VersionNumber != VerificationPlan.DeviceReleaseBcd)
        {
            handle.Dispose();
            throw new HardwareVerificationException(
                VerificationFailureCode.HidIdentityChanged);
        }
        if (!WindowsTargetDiscovery.HasExpectedHidCaps(handle))
        {
            handle.Dispose();
            throw new HardwareVerificationException(
                VerificationFailureCode.HidReportTopologyMismatch);
        }

        return new HidReadWriteChannel(new FileStream(handle,
            FileAccess.ReadWrite, VerificationPlan.HidReportLength,
            isAsync: true));
    }

    internal async Task WriteReportAsync(ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken) =>
        await output.WriteReportAsync(report, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName,
            uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle,
            ref WindowsTargetDiscovery.HiddAttributes attributes);
    }
}

internal static class WarmupDrainValidator
{
    internal static bool HasLiveTail(ReadOnlySpan<long> completionTicks,
        long frequency)
    {
        int intervalCount = VerificationPlan.InputWarmupLiveTailIntervals;
        if (frequency <= 0 || completionTicks.Length <= intervalCount)
        {
            return false;
        }

        long minimumTicks = Math.Max(1,
            (frequency * VerificationPlan.InputWarmupMinimumIntervalMicroseconds +
                999_999) / 1_000_000);
        int first = completionTicks.Length - intervalCount;
        for (int index = first; index < completionTicks.Length; index++)
        {
            if (completionTicks[index] - completionTicks[index - 1] <
                minimumTicks)
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed class HidOutputAttempt
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>
        writeAsync;
    private readonly HidOutputSessionState state = new();

    internal HidOutputAttempt(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeAsync)
    {
        this.writeAsync = writeAsync;
    }

    internal bool IsFaulted => state.IsFaulted;

    internal async Task WriteReportAsync(ReadOnlyMemory<byte> report,
        CancellationToken cancellationToken)
    {
        if (report.Length != VerificationPlan.HidReportLength)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.HapticWriteFailed);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(VerificationPlan.HapticWriteTimeoutMilliseconds);
        try
        {
            await writeAsync(report, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            state.MarkAmbiguousFailure();
            throw new HardwareVerificationException(
                cancellationToken.IsCancellationRequested ?
                    VerificationFailureCode.Cancelled :
                    VerificationFailureCode.HapticWriteFailed);
        }
        catch (IOException)
        {
            state.MarkAmbiguousFailure();
            throw new HardwareVerificationException(
                VerificationFailureCode.HapticWriteFailed);
        }
        catch
        {
            state.MarkAmbiguousFailure();
            throw new HardwareVerificationException(
                VerificationFailureCode.HapticWriteFailed);
        }
    }
}

internal sealed class HidOutputSessionState
{
    private int faulted;

    internal bool IsFaulted => Volatile.Read(ref faulted) != 0;

    internal void MarkAmbiguousFailure() => Volatile.Write(ref faulted, 1);
}

internal static class HapticCleanupPolicy
{
    internal static bool RequiresFreshChannel(bool neutralizationRequired,
        bool outputSessionFaulted) => neutralizationRequired &&
        outputSessionFaulted;
}

internal readonly record struct InputRateCapture(InputRateObservation Timing,
    Common05CounterObservation Counter);

internal readonly record struct Common05CounterObservation(
    int ForwardMovements, uint MinimumDelta, uint MaximumDelta,
    int PlusFourMovements, bool WrapObserved)
{
    internal static bool TryAnalyze(ReadOnlySpan<uint> counters,
        out Common05CounterObservation observation)
    {
        observation = default;
        if (counters.Length < 2)
        {
            return false;
        }

        uint minimum = uint.MaxValue;
        uint maximum = 0;
        int plusFour = 0;
        bool wrapped = false;
        for (int index = 1; index < counters.Length; index++)
        {
            uint previous = counters[index - 1];
            uint current = counters[index];
            uint delta = unchecked(current - previous);
            if (delta == 0 || delta > 0x7FFFFFFF)
            {
                return false;
            }
            minimum = Math.Min(minimum, delta);
            maximum = Math.Max(maximum, delta);
            plusFour += delta == 4 ? 1 : 0;
            wrapped |= current < previous;
        }

        observation = new Common05CounterObservation(counters.Length - 1,
            minimum, maximum, plusFour, wrapped);
        return true;
    }
}

internal readonly record struct InputRateObservation(int ExactReports,
    double ReportsPerSecond, double MeanIntervalMilliseconds,
    double P50IntervalMilliseconds, double P95IntervalMilliseconds,
    double P99IntervalMilliseconds)
{
    internal static InputRateObservation FromCompletionTicks(
        ReadOnlySpan<long> completionTicks, long frequency)
    {
        if (completionTicks.Length < 2 || frequency <= 0)
        {
            throw new ArgumentException("At least two timestamps are required.");
        }

        var deltas = new long[completionTicks.Length - 1];
        long total = 0;
        for (int index = 1; index < completionTicks.Length; index++)
        {
            long delta = completionTicks[index] - completionTicks[index - 1];
            if (delta <= 0)
            {
                throw new ArgumentException("Timestamps must increase.");
            }
            deltas[index - 1] = delta;
            total += delta;
        }
        Array.Sort(deltas);

        double millisecondsPerTick = 1_000.0 / frequency;
        double seconds = total / (double)frequency;
        return new InputRateObservation(completionTicks.Length,
            deltas.Length / seconds,
            total * millisecondsPerTick / deltas.Length,
            Percentile(deltas, 0.50) * millisecondsPerTick,
            Percentile(deltas, 0.95) * millisecondsPerTick,
            Percentile(deltas, 0.99) * millisecondsPerTick);
    }

    private static long Percentile(ReadOnlySpan<long> sorted, double value)
    {
        int index = Math.Max(0,
            (int)Math.Ceiling(sorted.Length * value) - 1);
        return sorted[index];
    }
}

internal sealed class WinUsbCommandChannel : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareNone = 0;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTransferTimeout = 3;
    private const uint AllowPartialReads = 5;
    private readonly TargetDeviceSessionIdentity target;
    private readonly SafeFileHandle fileHandle;
    private readonly SafeWinUsbHandle winUsbHandle;
    private readonly byte outPipe;
    private readonly byte inPipe;
    private readonly CommandTransactionState transactionState = new();

    internal bool IsFaulted => transactionState.IsFaulted;

    private WinUsbCommandChannel(TargetDeviceSessionIdentity target,
        SafeFileHandle fileHandle, SafeWinUsbHandle winUsbHandle,
        byte outPipe, byte inPipe)
    {
        this.target = target;
        this.fileHandle = fileHandle;
        this.winUsbHandle = winUsbHandle;
        this.outPipe = outPipe;
        this.inPipe = inPipe;
    }

    internal static WinUsbCommandChannel Open(
        TargetDeviceSessionIdentity target)
    {
        WindowsTargetDiscovery.Revalidate(target);
        SafeFileHandle file = NativeMethods.CreateFileW(
            target.WinUsb.InterfacePath, GenericRead | GenericWrite,
            FileShareNone, IntPtr.Zero, OpenExisting,
            FileFlagOverlapped, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int nativeErrorCode = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbOpenFailed,
                nativeErrorCode: nativeErrorCode);
        }

        if (!NativeMethods.WinUsb_Initialize(file, out IntPtr rawHandle))
        {
            file.Dispose();
            throw new HardwareVerificationException(
                VerificationFailureCode.WinUsbInitializeFailed);
        }
        var winUsb = new SafeWinUsbHandle(rawHandle);
        try
        {
            if (!NativeMethods.WinUsb_GetCurrentAlternateSetting(winUsb,
                    out byte currentAlternateSetting))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.WinUsbAlternateSettingQueryFailed,
                    nativeErrorCode: Marshal.GetLastWin32Error());
            }
            if (!ActiveAlternateSettingValidator.IsExactDefault(
                    currentAlternateSetting))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.WinUsbAlternateSettingMismatch);
            }
            if (!NativeMethods.WinUsb_QueryInterfaceSettings(winUsb, 0,
                    out UsbInterfaceDescriptor descriptor))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode
                        .WinUsbInterfaceDescriptorQueryFailed,
                    nativeErrorCode: Marshal.GetLastWin32Error());
            }
            if (descriptor.NumEndpoints != 2)
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode
                        .WinUsbInterfaceDescriptorMismatch);
            }

            Span<PipeFact> pipeFacts = stackalloc PipeFact[2];
            for (byte index = 0; index < 2; index++)
            {
                if (!NativeMethods.WinUsb_QueryPipe(winUsb, 0, index,
                        out WinUsbPipeInformation pipe))
                {
                    throw new HardwareVerificationException(
                        VerificationFailureCode.WinUsbPipeQueryFailed,
                        nativeErrorCode: Marshal.GetLastWin32Error());
                }
                pipeFacts[index] = new PipeFact(pipe.PipeId,
                    pipe.PipeType, pipe.MaximumPacketSize, pipe.Interval);
            }

            if (!PipeTopologyValidator.TryValidate(
                    descriptor.InterfaceNumber,
                    descriptor.AlternateSetting, pipeFacts,
                    out PipeFact bulkOut, out PipeFact bulkIn))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.WinUsbPipeTopologyMismatch,
                    pipeTopology: new UsbPipeTopologyObservation(
                        descriptor.InterfaceNumber,
                        descriptor.AlternateSetting,
                        descriptor.NumEndpoints, pipeFacts[0], pipeFacts[1]));
            }

            uint timeout = VerificationPlan.CommandTimeoutMilliseconds;
            byte allowPartialReads = 0;
            if (!NativeMethods.WinUsb_SetPipePolicy(winUsb, bulkOut.PipeId,
                    PipeTransferTimeout, sizeof(uint), ref timeout) ||
                !NativeMethods.WinUsb_SetPipePolicy(winUsb, bulkIn.PipeId,
                    PipeTransferTimeout, sizeof(uint), ref timeout) ||
                !NativeMethods.WinUsb_SetBytePipePolicy(winUsb,
                    bulkIn.PipeId, AllowPartialReads, sizeof(byte),
                    ref allowPartialReads))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.WinUsbPipePolicySetFailed,
                    nativeErrorCode: Marshal.GetLastWin32Error());
            }
            uint policyLength = sizeof(byte);
            if (!NativeMethods.WinUsb_GetBytePipePolicy(winUsb,
                    bulkIn.PipeId,
                    AllowPartialReads, ref policyLength,
                    out byte admittedPartialReads))
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.WinUsbPipePolicyReadFailed,
                    nativeErrorCode: Marshal.GetLastWin32Error());
            }
            if (policyLength != sizeof(byte) || admittedPartialReads != 0)
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.WinUsbPipePolicyMismatch,
                    pipePolicy: new WinUsbPipePolicyObservation(
                        policyLength, admittedPartialReads));
            }

            return new WinUsbCommandChannel(target, file, winUsb,
                bulkOut.PipeId, bulkIn.PipeId);
        }
        catch
        {
            winUsb.Dispose();
            file.Dispose();
            throw;
        }
    }

    internal BatteryVoltageCommandResult GetBatteryVoltage(
        CancellationToken cancellationToken)
    {
        byte[] request = new byte[Switch2UsbCommandCodec.RequestLength];
        if (!Switch2UsbCommandCodec.TryWriteGetBatteryVoltageRequest(request,
                out _) ||
            !Switch2UsbCommandCodec.TryValidateGetBatteryVoltageRequest(
                request, out _))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InvalidPlan);
        }
        byte[] response = Transact(request,
            Switch2UsbCommandCodec.BatteryVoltageResponseLength,
            cancellationToken, revalidateSessionIdentity: true);
        if (!Switch2UsbCommandCodec.TryParseGetBatteryVoltageResponse(response,
                out ushort voltage,
                out Switch2UsbCommandResponseStyle responseStyle,
                out Switch2UsbCommandFailure failure))
        {
            InvalidateProtocolState();
            throw new HardwareVerificationException(
                VerificationFailureCode.CommandResponseInvalid,
                commandResponseFailure: failure,
                observedResponseLength: response.Length,
                observedResponseHeaderByte4: response[4],
                observedResponseAcknowledgement: response[5]);
        }
        return new BatteryVoltageCommandResult(voltage, responseStyle);
    }

    internal void RunVolatileInitializationStep(
        Switch2UsbInitializationStep step,
        CancellationToken cancellationToken)
    {
        StartupEvidenceCommandKind operation = step switch
        {
            Switch2UsbInitializationStep.EnableUsbHidReports =>
                StartupEvidenceCommandKind.EnableUsbHidReports,
            Switch2UsbInitializationStep.SelectCommonInputReport =>
                StartupEvidenceCommandKind.SelectCommonInputReport,
            _ => throw new HardwareVerificationException(
                VerificationFailureCode.InvalidPlan),
        };
        CommandWireObservation observation = CaptureStartupEvidence(
            operation, cancellationToken);
        if (observation.ExistingValidatorAccepted != true)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.CommandResponseInvalid,
                commandResponseFailure:
                    observation.ExistingValidatorFailure,
                observedResponseLength: observation.Response.Length,
                observedResponseHeaderByte4:
                    observation.Response.Length > 4 ?
                        observation.Response[4] : null,
                observedResponseAcknowledgement:
                    observation.Response.Length > 5 ?
                        observation.Response[5] : null);
        }
    }

    internal CommandWireObservation CaptureStartupEvidence(
        StartupEvidenceCommandKind operation,
        CancellationToken cancellationToken)
    {
        if (!StartupEvidenceCapturePlan.TryCreateRequest(operation,
                out byte[] request))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InvalidPlan);
        }

        if (StartupEvidenceCapturePlan.IsFeatureOperation(operation))
        {
            Switch2UsbFeatureStep featureStep = operation ==
                    StartupEvidenceCommandKind.SetFeatureMask ?
                Switch2UsbFeatureStep.SetFeatureMask :
                Switch2UsbFeatureStep.EnableFeatures;
            byte[] featureResponse = Transact(request,
                Switch2UsbCommandCodec.FeatureResponseLength,
                cancellationToken, revalidateSessionIdentity: true);
            bool featureAccepted = Switch2UsbCommandCodec.
                TryValidateFeatureResponse(featureResponse, featureStep,
                    out Switch2UsbCommandFailure featureFailure);
            if (!featureAccepted)
            {
                InvalidateProtocolState();
            }
            return new CommandWireObservation(operation, request,
                featureResponse, featureAccepted,
                featureAccepted ? null : featureFailure);
        }

        Switch2UsbInitializationStep step = operation switch
        {
            StartupEvidenceCommandKind.EnableUsbHidReports =>
                Switch2UsbInitializationStep.EnableUsbHidReports,
            StartupEvidenceCommandKind.SelectCommonInputReport =>
                Switch2UsbInitializationStep.SelectCommonInputReport,
            _ => throw new HardwareVerificationException(
                VerificationFailureCode.InvalidPlan),
        };
        if (!Switch2UsbCommandCodec.TryGetInitializationResponseLength(step,
                out int responseLength))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InvalidPlan);
        }
        byte[] response = Transact(request, responseLength,
            cancellationToken, revalidateSessionIdentity: true);
        bool accepted = Switch2UsbCommandCodec
            .TryValidateInitializationResponse(response, step,
                out Switch2UsbCommandFailure failure);
        if (!accepted)
        {
            InvalidateProtocolState();
        }
        return new CommandWireObservation(operation, request, response,
            accepted, accepted ? null : failure);
    }

    internal Switch2UsbCommandResponseStyle SetPlayerLed(
        Switch2PlayerLedCommand command,
        CancellationToken cancellationToken,
        bool revalidateSessionIdentity)
    {
        if (command is not (Switch2PlayerLedCommand.Player1Only or
                Switch2PlayerLedCommand.AllOff))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InvalidPlan);
        }

        CommandWireObservation observation = CapturePlayerLedEvidence(command,
            cancellationToken, revalidateSessionIdentity,
            out Switch2UsbCommandResponseStyle responseStyle);
        if (observation.ExistingValidatorAccepted != true)
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.CommandResponseInvalid,
                commandResponseFailure:
                    observation.ExistingValidatorFailure,
                observedResponseLength: observation.Response.Length,
                observedResponseHeaderByte4:
                    observation.Response.Length > 4 ?
                        observation.Response[4] : null,
                observedResponseAcknowledgement:
                    observation.Response.Length > 5 ?
                        observation.Response[5] : null);
        }
        return responseStyle;
    }

    internal CommandWireObservation CapturePlayerLedEvidence(
        Switch2PlayerLedCommand command,
        CancellationToken cancellationToken,
        bool revalidateSessionIdentity,
        out Switch2UsbCommandResponseStyle responseStyle)
    {
        StartupEvidenceCommandKind operation = command switch
        {
            Switch2PlayerLedCommand.Player1Only =>
                StartupEvidenceCommandKind.PlayerLed1,
            Switch2PlayerLedCommand.AllOff =>
                StartupEvidenceCommandKind.PlayerLedAllOff,
            _ => throw new HardwareVerificationException(
                VerificationFailureCode.InvalidPlan),
        };
        if (!StartupEvidenceCapturePlan.TryCreateRequest(operation,
                out byte[] request))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InvalidPlan);
        }
        byte[] response = Transact(request,
            Switch2UsbCommandCodec.PlayerLedResponseLength,
            cancellationToken, revalidateSessionIdentity);
        bool accepted = Switch2UsbCommandCodec.TryValidatePlayerLedResponse(
            response, command, out responseStyle,
            out Switch2UsbCommandFailure failure);
        if (!accepted)
        {
            InvalidateProtocolState();
            responseStyle = default;
        }
        return new CommandWireObservation(operation, request, response,
            accepted, accepted ? null : failure);
    }

    private byte[] Transact(byte[] request, int responseLength,
        CancellationToken cancellationToken,
        bool revalidateSessionIdentity) => TransactCore(request,
            responseLength, responseLength, cancellationToken,
            revalidateSessionIdentity);

    private byte[] TransactVariable(byte[] request,
        int maximumResponseLength, CancellationToken cancellationToken,
        bool revalidateSessionIdentity) => TransactCore(request,
            maximumResponseLength, exactResponseLength: null,
            cancellationToken, revalidateSessionIdentity);

    private byte[] TransactCore(byte[] request, int responseBufferLength,
        int? exactResponseLength, CancellationToken cancellationToken,
        bool revalidateSessionIdentity)
    {
        if (responseBufferLength <= 0 ||
            responseBufferLength > VerificationPlan.BulkMaximumPacketSize ||
            exactResponseLength is int exact &&
                (exact <= 0 || exact != responseBufferLength))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.InvalidPlan);
        }
        if (!transactionState.TryBegin())
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.CommandTransferFailed,
                commandTransferStage:
                    CommandTransferFailureStage.TransactionBegin);
        }
        bool completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (revalidateSessionIdentity)
            {
                WindowsTargetDiscovery.Revalidate(target);
            }
            PrepareInputPipe();
            // Do not run a synchronous native abort inside a cancellation
            // callback. CancellationTokenSource invokes callbacks inline; a
            // wedged AbortPipe could prevent the ownership deadline's waiter
            // from observing cancellation. The pipe policy remains the normal
            // transfer bound, while CommandOperationDeadline is the hard
            // boundary and gives a non-returning call to one late owner.
            bool writeSucceeded = NativeMethods.WinUsb_WritePipe(
                winUsbHandle, outPipe, request,
                checked((uint)request.Length), out uint written,
                IntPtr.Zero);
            int writeError = Marshal.GetLastWin32Error();
            if (!writeSucceeded || written != (uint)request.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new HardwareVerificationException(
                    VerificationFailureCode.CommandTransferFailed,
                    nativeErrorCode: writeSucceeded ? null : writeError,
                    commandTransferStage:
                        CommandTransferFailureStage.RequestWrite);
            }

            // Exact validated forms use their fixed response length. The two
            // feature evidence operations instead use one packet-sized read,
            // matching the pinned SDL observation path while deliberately not
            // assigning semantics to the returned length or bytes. With
            // ALLOW_PARTIAL_READS disabled, an oversized packet is rejected
            // instead of being retained for a later transaction.
            var responsePacket = new byte[responseBufferLength];
            bool readSucceeded = NativeMethods.WinUsb_ReadPipe(
                winUsbHandle, inPipe, responsePacket,
                checked((uint)responsePacket.Length), out uint read,
                IntPtr.Zero);
            int readError = Marshal.GetLastWin32Error();
            if (!readSucceeded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new HardwareVerificationException(
                    VerificationFailureCode.CommandTransferFailed,
                    nativeErrorCode: readError,
                    commandTransferStage:
                        CommandTransferFailureStage.ResponseRead,
                    observedResponseLength: checked((int)read));
            }
            bool admitted = exactResponseLength is int expected ?
                CommandResponseAdmission.TryAdmit(responsePacket, read,
                    expected, out byte[] response) :
                RawCommandResponseAdmission.TryAdmit(responsePacket, read,
                    responseBufferLength, out response);
            if (!admitted)
            {
                throw new HardwareVerificationException(
                    VerificationFailureCode.CommandResponseInvalid,
                    commandTransferStage:
                        CommandTransferFailureStage.ResponseAdmission,
                    observedResponseLength: checked((int)read));
            }
            if (revalidateSessionIdentity)
            {
                WindowsTargetDiscovery.Revalidate(target);
            }
            completed = true;
            return response;
        }
        catch (OperationCanceledException)
        {
            transactionState.MarkFaulted();
            RecoverPipes();
            throw new HardwareVerificationException(
                VerificationFailureCode.Cancelled);
        }
        catch
        {
            transactionState.MarkFaulted();
            RecoverPipes();
            throw;
        }
        finally
        {
            if (completed)
            {
                transactionState.CompleteSuccess();
            }
        }
    }

    private void PrepareInputPipe()
    {
        // WinUsb_FlushPipe is the documented host-cache drain. There is
        // intentionally no speculative IN read or endpoint reset: neither is
        // part of the audited write-then-read transaction, and resetting a
        // healthy device pipe after a shape mismatch can disrupt later command
        // traffic. Exact tuple validation is still not causal attribution;
        // the admitted protocol has no transaction identifier.
        if (!NativeMethods.WinUsb_FlushPipe(winUsbHandle, inPipe))
        {
            throw new HardwareVerificationException(
                VerificationFailureCode.CommandTransferFailed,
                nativeErrorCode: Marshal.GetLastWin32Error(),
                commandTransferStage:
                    CommandTransferFailureStage.StaleInputFlush);
        }
    }

    private void InvalidateProtocolState()
    {
        transactionState.MarkFaulted();
        RecoverPipes();
    }

    private void RecoverPipes()
    {
        NativeMethods.WinUsb_AbortPipe(winUsbHandle, outPipe);
        NativeMethods.WinUsb_AbortPipe(winUsbHandle, inPipe);
        NativeMethods.WinUsb_FlushPipe(winUsbHandle, inPipe);
    }

    public void Dispose()
    {
        transactionState.Dispose();
        winUsbHandle.Dispose();
        fileHandle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsbInterfaceDescriptor
    {
        internal byte Length;
        internal byte DescriptorType;
        internal byte InterfaceNumber;
        internal byte AlternateSetting;
        internal byte NumEndpoints;
        internal byte InterfaceClass;
        internal byte InterfaceSubClass;
        internal byte InterfaceProtocol;
        internal byte Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinUsbPipeInformation
    {
        internal NativePipeType PipeType;
        internal byte PipeId;
        internal ushort MaximumPacketSize;
        internal byte Interval;
    }

    private sealed class SafeWinUsbHandle :
        SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeWinUsbHandle(IntPtr handle) : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() =>
            NativeMethods.WinUsb_Free(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName,
            uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_Initialize(
            SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_QueryInterfaceSettings(
            SafeWinUsbHandle interfaceHandle, byte alternateSetting,
            out UsbInterfaceDescriptor descriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_GetCurrentAlternateSetting(
            SafeWinUsbHandle interfaceHandle,
            out byte currentAlternateSetting);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_QueryPipe(
            SafeWinUsbHandle interfaceHandle, byte alternateSetting,
            byte pipeIndex, out WinUsbPipeInformation pipeInformation);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_SetPipePolicy(
            SafeWinUsbHandle interfaceHandle, byte pipeId, uint policyType,
            uint valueLength, ref uint value);

        [DllImport("winusb.dll", EntryPoint = "WinUsb_SetPipePolicy",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_SetBytePipePolicy(
            SafeWinUsbHandle interfaceHandle, byte pipeId, uint policyType,
            uint valueLength, ref byte value);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_GetPipePolicy(
            SafeWinUsbHandle interfaceHandle, byte pipeId, uint policyType,
            ref uint valueLength, out uint value);

        [DllImport("winusb.dll", EntryPoint = "WinUsb_GetPipePolicy",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_GetBytePipePolicy(
            SafeWinUsbHandle interfaceHandle, byte pipeId, uint policyType,
            ref uint valueLength, out byte value);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_WritePipe(
            SafeWinUsbHandle interfaceHandle, byte pipeId,
            [In] byte[] buffer, uint bufferLength,
            out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_ReadPipe(
            SafeWinUsbHandle interfaceHandle, byte pipeId,
            [Out] byte[] buffer, uint bufferLength,
            out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_AbortPipe(
            SafeWinUsbHandle interfaceHandle, byte pipeId);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_FlushPipe(
            SafeWinUsbHandle interfaceHandle, byte pipeId);

    }
}

internal readonly record struct BatteryVoltageCommandResult(ushort RawVoltage,
    Switch2UsbCommandResponseStyle ResponseStyle);

internal static class CommandResponseAdmission
{
    internal static bool TryAdmit(ReadOnlySpan<byte> packet,
        uint transferredLength, int expectedLength, out byte[] response)
    {
        response = [];
        if (expectedLength <= 0 ||
            expectedLength > VerificationPlan.BulkMaximumPacketSize ||
            transferredLength != (uint)expectedLength ||
            transferredLength > (uint)packet.Length)
        {
            return false;
        }

        response = packet.Slice(0, expectedLength).ToArray();
        return true;
    }
}

internal static class RawCommandResponseAdmission
{
    internal static bool TryAdmit(ReadOnlySpan<byte> packet,
        uint transferredLength, int maximumLength, out byte[] response)
    {
        response = [];
        if (maximumLength <= 0 ||
            maximumLength > VerificationPlan.BulkMaximumPacketSize ||
            transferredLength == 0 ||
            transferredLength > (uint)maximumLength ||
            transferredLength > (uint)packet.Length)
        {
            return false;
        }

        response = packet.Slice(0, checked((int)transferredLength)).ToArray();
        return true;
    }
}

internal enum CommandTransactionPhase
{
    Ready,
    InFlight,
    Faulted,
    Disposed,
}

internal sealed class CommandTransactionState
{
    private int phase = (int)CommandTransactionPhase.Ready;

    internal bool IsFaulted => Volatile.Read(ref phase) ==
        (int)CommandTransactionPhase.Faulted;

    internal CommandTransactionPhase Phase =>
        (CommandTransactionPhase)Volatile.Read(ref phase);

    internal bool TryBegin() => Interlocked.CompareExchange(ref phase,
        (int)CommandTransactionPhase.InFlight,
        (int)CommandTransactionPhase.Ready) ==
        (int)CommandTransactionPhase.Ready;

    internal void CompleteSuccess()
    {
        if (Interlocked.CompareExchange(ref phase,
                (int)CommandTransactionPhase.Ready,
                (int)CommandTransactionPhase.InFlight) !=
            (int)CommandTransactionPhase.InFlight)
        {
            Interlocked.Exchange(ref phase,
                (int)CommandTransactionPhase.Faulted);
        }
    }

    internal void MarkFaulted() => Interlocked.Exchange(ref phase,
        (int)CommandTransactionPhase.Faulted);

    internal void Dispose() => Interlocked.Exchange(ref phase,
        (int)CommandTransactionPhase.Disposed);
}

internal static class CommandCleanupPolicy
{
    internal static bool RequiresFreshChannel(bool neutralizationRequired,
        CommandTransactionPhase phase) => neutralizationRequired &&
        phase == CommandTransactionPhase.Faulted;
}
