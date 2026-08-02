using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Owns the parent side of the isolated DualSense Bluetooth report pacer.
    /// The helper is the exact same DS4Windows executable, entered through
    /// <see cref="TryRunHelper"/> before normal application startup.
    /// </summary>
    internal sealed class DualSenseBluetoothAudioPacer : IDisposable
    {
        internal const int ReportLength = 398;
        // Keep media on the hardware-validated MeasuredTransport-sized carrier: one
        // complete speaker/haptics generation per 10.667 ms. The 547-byte
        // paired carrier is valid on the combined-report reference's raw L2CAP stack, but Windows
        // HID does not expose its send credits and acoustic traces showed
        // discontinuities after otherwise-clean encoding. Microphone and
        // controller-state transitions are serialized separately below.
        internal static readonly bool UsePairedAudioReports = false;
        // V5 arms its native media writer with eight complete reports.
        // After that one-time burst it presents fifteen 480-frame reports on
        // sixteen 10 ms host ticks, leaving one deliberate 20 ms boundary per
        // 160 ms while its controller-side reserve remains continuous.
        internal const int NativePrimeReportCount = 8;
        // Sony's 0x39 report is complete as soon as one two-frame pair exists.
        internal const int PairedPrimeReportCount = 2;
        internal const int PairedAudioInFlightLimit =
            PairedAudioTransportSlotCount;
        // the measured transport's Windows transport uses eight pinned OVERLAPPED slots in a
        // strict oldest-first ring. the combined-report reference's one-at-a-time CAN_SEND_NOW
        // discipline cannot be reproduced from HidBth completion events:
        // completion is an IRP boundary, not an exposed L2CAP send credit.
        // Keep the measured transport's eight-slot Windows cushion around the combined-report reference's 0x39
        // wire image so normal 39-82 ms completion droughts do not starve the
        // controller while newer reports can never pass the oldest slot.
        internal const int PairedAudioTransportSlotCount = 8;
        // V5 uses 32 pinned OVERLAPPED/event/buffer slots and advances a
        // strict modulo-32 FIFO. A slot is reused only after its own completion
        // has been observed; newer reports never scan past the oldest slot.
        internal const int SingleAudioTransportSlotCount = 32;
        internal const int SingleAudioInFlightLimit = 32;
        internal const int HostReservoirCapacity = 64;
        // The paired path is source-driven like CombinedReportReference. It must not replay
        // 398-era startup phases after a normal two-frame queue boundary.
        internal const int ControllerLinkWarmupIntervals = 0;
        // A single-report stream starts directly at native cadence. The old
        // paired path used a short 5 ms reserve-transfer phase; applying that
        // phase to 0x36 would burst reports at twice the firmware cadence.
        internal const int ControllerReserveTransferIntervals = 0;
        internal static bool UseMeasuredTransportAudioTransport(string value)
        {
            // Legacy experiment selectors are intentionally ignored. Every
            // physical DualSense Bluetooth session now uses the one validated
            // V5-native owner, independent of process environment.
            return false;
        }

        internal static bool UseCompactCombinedHapticsTransport(string value)
        {
            return false;
        }

        internal static bool RequiresSeparateControllerStateTransport()
        {
            return false;
        }

        internal static bool RequiresSeparateControllerStateTransport(
            string value)
        {
            return false;
        }

        internal static bool RequiresFullDuplexAudioReport(byte[] report)
        {
            return IsSpeakerAudioReport(report) && (report[4] & 0x01) != 0;
        }

        internal static bool ShouldWaitForPhysicalWriteCredit(
            bool measuredTransportAudioTransport, bool pairedAudioReports)
        {
            return !measuredTransportAudioTransport && !pairedAudioReports;
        }

        internal static bool ShouldApplyInputPhaseCorrection(
            bool compactCombinedTransport, bool pairedAudioReports)
        {
            // Native 0x36 media is source-driven. Compact/paired fallback
            // clocks also remain independent from asynchronous HID input;
            // phase nudges manufactured audible presentation jitter.
            return false;
        }

        internal static bool ShouldDropSaturatedAudio(
            bool measuredTransportAudioTransport, bool pairedAudioReport,
            bool controlOnly, bool accepted, bool transportFault)
        {
            return !controlOnly && !accepted && !transportFault &&
                (measuredTransportAudioTransport || pairedAudioReport);
        }

        internal static void ApplyCommittedMicrophoneMode(byte[] report,
            bool microphoneEnabled)
        {
            if (report == null || report.Length != ReportLength ||
                report[0] != 0x36)
            {
                throw new ArgumentException(
                    "Source must be a complete 398-byte 0x36 report.",
                    nameof(report));
            }

            if (microphoneEnabled)
            {
                report[4] |= 0x01;
            }
            else
            {
                report[4] &= 0xFE;
            }
        }

        internal static int GetNativeMicrophoneTransitionReportsAhead(
            bool committedMicrophoneEnabled, bool requestedMicrophoneEnabled)
        {
            // V5 disables the microphone/audio-clock lane only after two
            // speaker-only media generations. Enabling is the inverse: 0x32
            // precedes the first duplex media generation.
            return committedMicrophoneEnabled &&
                !requestedMicrophoneEnabled ? 2 : 0;
        }

        internal static bool GetNativeMicrophonePresentationMode(
            bool committedMicrophoneEnabled, bool requestedMicrophoneEnabled)
        {
            return requestedMicrophoneEnabled ?
                committedMicrophoneEnabled : false;
        }

        internal static int CompletePairedReportBoundary(
            int leadingSpeakerReports)
        {
            return Math.Max(0, leadingSpeakerReports & ~1);
        }

        private const string HelperArgument = "--dualsense-bt-audio-pacer-helper";
        private const int ProtocolVersion = 13;
        private const int PipeConnectTimeoutMilliseconds = 5000;
        private const int HelperReadyTimeoutMilliseconds = 5000;
        private const int HelperStopTimeoutMilliseconds = 3000;
        private const int HelperProcessExitTimeoutMilliseconds = 3000;
        private const uint HelperWriterReleaseTimeoutMilliseconds = 3000;
        private const uint HelperAudioCreditPollMilliseconds = 1;
        // Inbound microphone reports are observations, not output credits or
        // speaker presentation deadlines. Every working reference keeps the
        // outbound media clock independent; coupling it to bursty HidBth input
        // arrivals makes duplex traffic modulate an otherwise stable speaker
        // cadence. Retain the model for diagnostics, but never let it own the
        // physical 0x35 schedule.
        private static readonly bool UseMicrophoneSequencePresentationClock =
            false;
        private const int InputClockMapLength = 24;
        private const long InputClockVersionOffset = 0;
        private const long InputClockTimestampOffset = 8;
        private const long InputClockSequenceOffset = 16;
        private const int OutboundCommandCapacity = HostReservoirCapacity + 16;
        private const int InitialEpoch = 1;

        private enum MessageKind : byte
        {
            Hello = 1,
            QueueReport = 2,
            UpdateTemplate = 3,
            Clear = 4,
            Stop = 5,
            UpdateCadence = 6,
            UpdateMicrophoneStatus = 7,
            UpdateControllerState = 8,
            UpdateControllerMediaBuffer = 9,
            UpdateGameStateAndTemplate = 10,
            ResetControllerStateTransitions = 11,
            Ready = 0x80,
            ReportAcknowledged = 0x81,
            Stopped = 0x82,
            Error = 0xFF,
        }

        internal enum AcknowledgementDisposition : byte
        {
            Presented = 1,
            Cleared = 2,
            Rejected = 3,
            TransportFault = 4,
            StaleEpoch = 5,
        }

        private sealed class OutboundCommand
        {
            public readonly MessageKind Kind;
            public readonly byte[] Payload;
            public readonly long ReportId;

            public OutboundCommand(MessageKind kind, byte[] payload,
                long reportId = 0)
            {
                Kind = kind;
                Payload = payload ?? Array.Empty<byte>();
                ReportId = reportId;
            }
        }

        private sealed class PendingReportCompletion
        {
            public readonly TaskCompletionSource<AcknowledgementDisposition>
                Source = new TaskCompletionSource<AcknowledgementDisposition>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly object stateLock = new object();
        private readonly object pipeWriteLock = new object();
        private readonly NamedPipeServerStream commandPipe;
        private readonly NamedPipeServerStream responsePipe;
        private readonly Process helperProcess;
        private readonly EventWaitHandle inputArrivalSignal;
        private readonly MemoryMappedFile inputClockMap;
        private readonly MemoryMappedViewAccessor inputClockView;
        private readonly bool usesV5PresentationCadence;
        private readonly DualSenseBluetoothAudioPacerRing<OutboundCommand>
            outboundCommands = new DualSenseBluetoothAudioPacerRing<OutboundCommand>(
                OutboundCommandCapacity);
        private readonly Dictionary<long, byte> outstandingReports =
            new Dictionary<long, byte>(HostReservoirCapacity);
        private readonly Dictionary<long, PendingReportCompletion>
            pendingReportCompletions =
                new Dictionary<long, PendingReportCompletion>();
        private readonly AutoResetEvent outboundAvailable = new AutoResetEvent(false);
        private readonly ManualResetEventSlim readyEvent = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim stoppedEvent = new ManualResetEventSlim(false);
        private readonly Thread senderThread;
        private readonly Thread receiverThread;

        private byte[] latestTemplate;
        private readonly byte[] latestControllerState = new byte[
            DualSenseBluetoothPhysicalOutputSequence.
                ControllerStatePayloadLength];
        private bool latestControllerStateAvailable;
        private readonly byte[] accumulatedGameState = new byte[
            DualSenseBluetoothPhysicalOutputSequence.
                ControllerStatePayloadLength];
        private bool accumulatedGameStateAvailable;
        private long latestTemplateHapticsExpiryQpc;
        private long nextReportId;
        private long acknowledgedReports;
        private long rejectedReports;
        private long presentedReports;
        private long lastPresentedTimestamp;
        private long maximumPresentationGapTicks;
        private long latePresentationCount;
        private long helperInFlightLimitWaitCount;
        private long helperInFlightLimitEscapeCount;
        private long helperMaximumInFlightLimitWaitTicks;
        private long helperMaximumAudioPendingBeforeSubmission;
        private long helperShallowAudioSubmissionCount;
        private long helperFullAudioSubmissionCount;
        private long helperCompletedWriteCount;
        private long helperSlowCompletionCount;
        private long helperMaximumCompletionTicks;
        private long helperLateSubmissionCount;
        private long helperMaximumSubmissionGapTicks;
        private long helperSlowNativeSubmissionCount;
        private long helperMaximumNativeSubmissionTicks;
        private long clearedReports;
        private long transportFaultReports;
        private int currentEpoch = InitialEpoch;
        private int stopping;
        private int disposed;
        private long inputClockVersion;
        private int cleanStopAcknowledged;
        private string lastError = string.Empty;

        private DualSenseBluetoothAudioPacer(
            NamedPipeServerStream commandPipe,
            NamedPipeServerStream responsePipe, Process helperProcess,
            EventWaitHandle inputArrivalSignal,
            MemoryMappedFile inputClockMap,
            MemoryMappedViewAccessor inputClockView,
            bool usesV5PresentationCadence)
        {
            this.commandPipe = commandPipe;
            this.responsePipe = responsePipe;
            this.helperProcess = helperProcess;
            this.inputArrivalSignal = inputArrivalSignal;
            this.inputClockMap = inputClockMap;
            this.inputClockView = inputClockView;
            this.usesV5PresentationCadence =
                usesV5PresentationCadence;
            senderThread = new Thread(SenderLoop)
            {
                IsBackground = true,
                Name = "DualSense BT audio pacer IPC sender",
                Priority = ThreadPriority.Highest,
            };
            receiverThread = new Thread(ReceiverLoop)
            {
                IsBackground = true,
                Name = "DualSense BT audio pacer IPC receiver",
                Priority = ThreadPriority.Highest,
            };
        }

        public int OutstandingReportCount
        {
            get
            {
                lock (stateLock)
                {
                    return outstandingReports.Count;
                }
            }
        }

        public int QueuedFrames => OutstandingReportCount;
        public long AcknowledgedReports => Interlocked.Read(ref acknowledgedReports);
        public long RejectedReports => Interlocked.Read(ref rejectedReports);
        public long PresentedReports => Interlocked.Read(ref presentedReports);
        public long LatePresentationCount =>
            Interlocked.Read(ref latePresentationCount);
        public double MaximumPresentationGapMilliseconds =>
            Interlocked.Read(ref maximumPresentationGapTicks) * 1000.0 /
            Stopwatch.Frequency;
        public long HelperInFlightLimitWaitCount =>
            Interlocked.Read(ref helperInFlightLimitWaitCount);
        public long HelperInFlightLimitEscapeCount =>
            Interlocked.Read(ref helperInFlightLimitEscapeCount);
        public double HelperMaximumInFlightLimitWaitMilliseconds =>
            Interlocked.Read(ref helperMaximumInFlightLimitWaitTicks) *
            1000.0 / Stopwatch.Frequency;
        public long HelperMaximumAudioPendingBeforeSubmission =>
            Interlocked.Read(ref helperMaximumAudioPendingBeforeSubmission);
        public long HelperShallowAudioSubmissionCount =>
            Interlocked.Read(ref helperShallowAudioSubmissionCount);
        public long HelperFullAudioSubmissionCount =>
            Interlocked.Read(ref helperFullAudioSubmissionCount);
        public long HelperCompletedWriteCount =>
            Interlocked.Read(ref helperCompletedWriteCount);
        public long HelperSlowCompletionCount =>
            Interlocked.Read(ref helperSlowCompletionCount);
        public double HelperMaximumCompletionMilliseconds =>
            Interlocked.Read(ref helperMaximumCompletionTicks) * 1000.0 /
            Stopwatch.Frequency;
        public long HelperLateSubmissionCount =>
            Interlocked.Read(ref helperLateSubmissionCount);
        public double HelperMaximumSubmissionGapMilliseconds =>
            Interlocked.Read(ref helperMaximumSubmissionGapTicks) * 1000.0 /
            Stopwatch.Frequency;
        public long HelperSlowNativeSubmissionCount =>
            Interlocked.Read(ref helperSlowNativeSubmissionCount);
        public double HelperMaximumNativeSubmissionMilliseconds =>
            Interlocked.Read(ref helperMaximumNativeSubmissionTicks) * 1000.0 /
            Stopwatch.Frequency;
        public long ClearedReports => Interlocked.Read(ref clearedReports);
        public long TransportFaultReports =>
            Interlocked.Read(ref transportFaultReports);
        public bool IsReady => readyEvent.IsSet && !IsFaulted;
        public bool IsFaulted => !string.IsNullOrEmpty(LastError);
        public bool IsRunning => Volatile.Read(ref stopping) == 0 &&
            Volatile.Read(ref disposed) == 0 && !IsFaulted;

        /// <summary>
        /// Publishes one actual physical microphone frame to the isolated
        /// writer. A small seqlock preserves its controller sequence and QPC
        /// timestamp without pipe traffic or allocation on the HID thread.
        /// </summary>
        public void SignalMicrophoneFrame(byte sequence)
        {
            if (!UseMicrophoneSequencePresentationClock)
            {
                return;
            }

            // Do not consult LastError/IsRunning here: LastError owns the
            // pacer state lock and this method runs on the physical HID input
            // thread for every completed Bluetooth report.
            if (Volatile.Read(ref stopping) != 0 ||
                Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            try
            {
                long completedVersion = Interlocked.Add(
                    ref inputClockVersion, 2);
                inputClockView.Write(InputClockVersionOffset,
                    completedVersion - 1);
                inputClockView.Write(InputClockTimestampOffset,
                    Stopwatch.GetTimestamp());
                inputClockView.Write(InputClockSequenceOffset,
                    (int)sequence);
                Thread.MemoryBarrier();
                inputClockView.Write(InputClockVersionOffset,
                    completedVersion);
                inputArrivalSignal.Set();
            }
            catch (Exception ex) when (ex is ObjectDisposedException ||
                ex is InvalidOperationException)
            {
                // A disconnect may race the final completed read. The pacer
                // disposal barrier still owns helper shutdown.
            }
        }

        internal static bool IsFatalAcknowledgementDisposition(
            AcknowledgementDisposition disposition)
        {
            return disposition == AcknowledgementDisposition.TransportFault;
        }

        internal static bool IsCleanStopBarrier(bool stopSignalReceived,
            bool cleanStopAcknowledged)
        {
            return stopSignalReceived && cleanStopAcknowledged;
        }

        internal static bool CanPublishStopped(bool pacerThreadStopped,
            bool acknowledgementThreadStopped, bool transportReleased)
        {
            return pacerThreadStopped && acknowledgementThreadStopped &&
                transportReleased;
        }

        public string LastError
        {
            get
            {
                lock (stateLock)
                {
                    return lastError;
                }
            }
        }

        /// <summary>
        /// Call this at the very beginning of WPF startup. It returns false for
        /// every normal invocation. In helper mode it owns the process until
        /// the pipe closes or a Stop command arrives, then returns true so the
        /// caller can shut down WPF without entering normal DS4Windows startup.
        /// </summary>
        public static bool TryRunHelper(string[] args)
        {
            if (!TryParseHelperArguments(args, out string commandPipeName,
                out string responsePipeName, out Guid authenticationToken,
                out int parentProcessId,
                out string inputArrivalSignalName,
                out string inputClockMapName, out string devicePath))
            {
                return false;
            }

            RunHelper(commandPipeName, responsePipeName,
                authenticationToken, parentProcessId, inputArrivalSignalName,
                inputClockMapName, devicePath);
            return true;
        }

        /// <summary>
        /// Starts a helper using the exact currently-running executable. The
        /// helper opens its own shared, write-only HID file session, matching
        /// the native transport's independent media writer rather than sharing the input
        /// file object.
        /// </summary>
        public static bool TryStart(string devicePath,
            byte[] initialTemplate,
            out DualSenseBluetoothAudioPacer pacer,
            out string error)
        {
            return TryStart(devicePath, initialTemplate,
                hapticsExpiryQpc: 0, out pacer, out error);
        }

        /// <summary>
        /// Starts the helper with an atomic initial control/haptics template.
        /// The absolute QPC expiry belongs to the haptics bytes in that
        /// template, not to any older queued audio report.
        /// </summary>
        public static bool TryStart(string devicePath,
            byte[] initialTemplate, long hapticsExpiryQpc,
            out DualSenseBluetoothAudioPacer pacer,
            out string error)
        {
            return TryStart(devicePath, initialTemplate, hapticsExpiryQpc,
                useV5PresentationCadence: false, out pacer, out error);
        }

        public static bool TryStart(string devicePath,
            byte[] initialTemplate, long hapticsExpiryQpc,
            bool useV5PresentationCadence,
            out DualSenseBluetoothAudioPacer pacer,
            out string error)
        {
            pacer = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(devicePath))
            {
                error = "The DualSense HID device path is unavailable.";
                return false;
            }

            if (initialTemplate == null || initialTemplate.Length != ReportLength)
            {
                error = $"The initial combined report must be exactly {ReportLength} bytes.";
                return false;
            }

            string executablePath = GetExactCurrentExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !File.Exists(executablePath))
            {
                error = "The exact current DS4Windows executable could not be located.";
                return false;
            }

            string pipeName = "DS4Windows.DualSenseAudioPacer." +
                Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
            string commandPipeName = pipeName + ".commands";
            string responsePipeName = pipeName + ".responses";
            string inputArrivalSignalName = pipeName + ".InputArrival";
            string inputClockMapName = pipeName + ".InputClock";
            Guid authenticationToken = Guid.NewGuid();
            NamedPipeServerStream commandServer = null;
            NamedPipeServerStream responseServer = null;
            Process child = null;
            DualSenseBluetoothAudioPacer candidate = null;
            EventWaitHandle inputSignal = null;
            MemoryMappedFile inputMap = null;
            MemoryMappedViewAccessor inputView = null;

            try
            {
                commandServer = new NamedPipeServerStream(commandPipeName,
                    PipeDirection.Out,
                    1, PipeTransmissionMode.Byte,
                    PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
                    4096, 4096);
                responseServer = new NamedPipeServerStream(responsePipeName,
                    PipeDirection.In,
                    1, PipeTransmissionMode.Byte,
                    PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
                    4096, 4096);
                inputSignal = new EventWaitHandle(false,
                    EventResetMode.AutoReset, inputArrivalSignalName);
                inputMap = MemoryMappedFile.CreateNew(inputClockMapName,
                    InputClockMapLength, MemoryMappedFileAccess.ReadWrite);
                inputView = inputMap.CreateViewAccessor(0,
                    InputClockMapLength, MemoryMappedFileAccess.ReadWrite);

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ??
                        Environment.CurrentDirectory,
                };
                startInfo.ArgumentList.Add(HelperArgument);
                startInfo.ArgumentList.Add(commandPipeName);
                startInfo.ArgumentList.Add(responsePipeName);
                startInfo.ArgumentList.Add(authenticationToken.ToString("N"));
                startInfo.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString());
                startInfo.ArgumentList.Add(inputArrivalSignalName);
                startInfo.ArgumentList.Add(inputClockMapName);
                startInfo.ArgumentList.Add(devicePath);
                child = Process.Start(startInfo);
                if (child == null)
                {
                    error = "Windows did not create the DualSense audio pacer process.";
                    commandServer.Dispose();
                    responseServer.Dispose();
                    inputSignal.Dispose();
                    inputView.Dispose();
                    inputMap.Dispose();
                    return false;
                }

                // The helper has one dedicated reader and one dedicated
                // writer. Synchronous unidirectional pipes avoid the per-I/O
                // kernel event leak produced by synchronous operations on an
                // OVERLAPPED full-duplex pipe. Only connection establishment
                // needs a bounded background wait.
                Task commandConnection =
                    Task.Run(commandServer.WaitForConnection);
                Task responseConnection =
                    Task.Run(responseServer.WaitForConnection);
                Task connections = Task.WhenAll(commandConnection,
                    responseConnection);
                if (!connections.Wait(PipeConnectTimeoutMilliseconds))
                {
                    error = "Timed out waiting for the DualSense audio pacer pipes.";
                    commandServer.Dispose();
                    responseServer.Dispose();
                    inputSignal.Dispose();
                    inputView.Dispose();
                    inputMap.Dispose();
                    TryTerminateUninitializedHelper(child);
                    return false;
                }

                connections.GetAwaiter().GetResult();
                candidate = new DualSenseBluetoothAudioPacer(commandServer,
                    responseServer, child, inputSignal, inputMap, inputView,
                    usesV5PresentationCadence: true);
                inputSignal = null;
                inputMap = null;
                inputView = null;
                candidate.latestTemplate = (byte[])initialTemplate.Clone();
                candidate.latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
                candidate.receiverThread.Start();
                candidate.SendHello(authenticationToken);

                if (!candidate.readyEvent.Wait(HelperReadyTimeoutMilliseconds))
                {
                    error = string.IsNullOrEmpty(candidate.LastError) ?
                        "Timed out waiting for the DualSense audio pacer to initialize." :
                        candidate.LastError;
                    candidate.Dispose();
                    return false;
                }

                if (!string.IsNullOrEmpty(candidate.LastError))
                {
                    error = candidate.LastError;
                    candidate.Dispose();
                    return false;
                }

                candidate.SendFrame(MessageKind.UpdateTemplate,
                    BuildTemplatePayload(candidate.latestTemplate,
                        candidate.latestTemplateHapticsExpiryQpc));
                if (RequiresSeparateControllerStateTransport() &&
                    !candidate.UpdateControllerState(
                        candidate.latestTemplate))
                {
                    error = "Could not queue the initial DualSense controller state.";
                    candidate.Dispose();
                    return false;
                }
                candidate.senderThread.Start();
                pacer = candidate;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                if (candidate != null)
                {
                    candidate.Dispose();
                }
                else
                {
                    commandServer?.Dispose();
                    responseServer?.Dispose();
                    inputSignal?.Dispose();
                    inputView?.Dispose();
                    inputMap?.Dispose();
                    if (child != null)
                    {
                        TryTerminateUninitializedHelper(child);
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Adds one complete combined 0x36 report to the bounded host
        /// reservoir. The report ID remains charged against the reservoir until
        /// the helper acknowledges that it was dequeued (or cleared).
        /// </summary>
        public bool TryQueueReport(byte[] report, long hapticsExpiryQpc,
            out long reportId)
        {
            return TryQueueReportCore(report, hapticsExpiryQpc,
                completion: null, out reportId);
        }

        /// <summary>
        /// Queues one speaker-free control report and waits until the helper's
        /// HID writer confirms completion. The helper bypasses the eight-frame
        /// audio prime gate for this report shape, allowing microphone and idle
        /// controller state to remain on the same transport owner.
        /// </summary>
        public bool TryQueueControlReportAndWait(byte[] report,
            long hapticsExpiryQpc, int timeoutMilliseconds,
            out AcknowledgementDisposition disposition)
        {
            disposition = AcknowledgementDisposition.Rejected;
            if (timeoutMilliseconds <= 0 ||
                IsSpeakerAudioReport(report))
            {
                return false;
            }

            var completion = new PendingReportCompletion();
            if (!TryQueueReportCore(report, hapticsExpiryQpc, completion,
                out long reportId))
            {
                return false;
            }

            if (!completion.Source.Task.Wait(timeoutMilliseconds))
            {
                lock (stateLock)
                {
                    pendingReportCompletions.Remove(reportId);
                }

                return false;
            }

            disposition = completion.Source.Task.GetAwaiter().GetResult();
            return disposition == AcknowledgementDisposition.Presented;
        }

        private bool TryQueueReportCore(byte[] report, long hapticsExpiryQpc,
            PendingReportCompletion completion, out long reportId)
        {
            reportId = 0;
            if (report == null || report.Length != ReportLength || !IsRunning)
            {
                return false;
            }

            byte[] reportCopy = (byte[])report.Clone();
            lock (stateLock)
            {
                if (Volatile.Read(ref stopping) != 0 ||
                    Volatile.Read(ref disposed) != 0 ||
                    !string.IsNullOrEmpty(lastError) ||
                    outstandingReports.Count >= HostReservoirCapacity)
                {
                    return false;
                }

                reportId = unchecked(++nextReportId);
                if (reportId == 0)
                {
                    reportId = unchecked(++nextReportId);
                }

                byte[] payload = BuildQueuePayload(reportId, currentEpoch,
                    hapticsExpiryQpc, reportCopy);
                var command = new OutboundCommand(MessageKind.QueueReport,
                    payload, reportId);
                outstandingReports.Add(reportId, 0);
                if (completion != null)
                {
                    pendingReportCompletions.Add(reportId, completion);
                }
                if (!outboundCommands.TryEnqueue(command))
                {
                    outstandingReports.Remove(reportId);
                    pendingReportCompletions.Remove(reportId);
                    reportId = 0;
                    return false;
                }
            }

            outboundAvailable.Set();
            return true;
        }

        internal static bool IsSpeakerAudioReport(byte[] report)
        {
            return report != null && report.Length == ReportLength &&
                (report[142] == 0x93 || report[142] == 0x96) &&
                report[143] == 200;
        }

        internal static bool IsHeadsetAudioReport(byte[] report)
        {
            return IsSpeakerAudioReport(report) && report[142] == 0x96;
        }

        internal static bool CanPresentFromPrimeGate(bool primeRequired,
            int speakerReportCount, byte[] nextReport,
            int requiredPrimeReportCount = NativePrimeReportCount)
        {
            return !primeRequired ||
                (nextReport != null && !IsSpeakerAudioReport(nextReport)) ||
                speakerReportCount >= requiredPrimeReportCount;
        }

        internal bool UsesV5PresentationCadence =>
            usesV5PresentationCadence;

        internal static bool CanPresentFromTransportGate(bool primeRequired,
            int speakerReportCount, byte[] nextReport,
            int requiredPrimeReportCount = NativePrimeReportCount)
        {
            if (!CanPresentFromPrimeGate(primeRequired, speakerReportCount,
                nextReport, requiredPrimeReportCount))
            {
                return false;
            }

            // A 547-byte report is indivisible: it carries exactly two Opus
            // and two haptics frames. Never admit one speaker frame by itself,
            // even after the initial prime has completed.
            return !UsePairedAudioReports ||
                !IsSpeakerAudioReport(nextReport) ||
                speakerReportCount >= 2;
        }

        internal static int GetPrimeReportCount(
            bool useMeasuredTransportAudioTransport)
        {
            if (UsePairedAudioReports)
            {
                return PairedPrimeReportCount;
            }

            return useMeasuredTransportAudioTransport ? 1 : NativePrimeReportCount;
        }

        internal static bool UsesSourceDrivenNativePresentation(
            bool useNativeAudioTransport, byte[] nextReport)
        {
            return useNativeAudioTransport && IsSpeakerAudioReport(nextReport);
        }

        internal static bool ShouldUseV5PresentationCadence(
            bool requested, bool useNativeAudioTransport)
        {
            // The validated presentation lattice is a transport invariant,
            // not an opt-in experiment. The request argument is retained only
            // to keep older diagnostic callers source-compatible.
            return useNativeAudioTransport;
        }

        internal static bool ShouldRequireAudioPrimeAfterPresentation(
            bool presentedControlReport, int remainingReportCount)
        {
            // A momentarily empty producer queue is normal at the boundary
            // between source callbacks. CombinedReportReference simply waits until the next
            // complete pair exists. Resetting here made DS4Windows wait for a
            // fresh prime and replay its startup rate transfer on every
            // shortage, creating 77 ms gaps followed by a 20 ms burst cadence.
            // A native 0x36 stream has no half-report generation to discard or
            // rebuild. V5 resumes with the next complete source block
            // after state. Only the dormant paired transport requires a new
            // complete pair.
            return UsePairedAudioReports && presentedControlReport;
        }

        internal static bool ShouldReprimeAfterEmptyReservoir(
            bool useNativeAudioTransport)
        {
            // the native transport's eight-block gate is a one-time session prime. Once
            // that latch opens, an ordinary source boundary waits for the next
            // complete block; it never rebuilds and bursts another eight.
            return !useNativeAudioTransport;
        }

        internal static bool ShouldRetainSaturatedWrite(bool accepted,
            bool transportFault)
        {
            return !accepted && !transportFault;
        }

        public bool TryQueueReport(byte[] report, long hapticsExpiryQpc)
        {
            return TryQueueReport(report, hapticsExpiryQpc, out _);
        }

        /// <summary>
        /// Replaces the control/haptics template used at presentation time.
        /// Pending reports retain their own sequence, packet counter, speaker
        /// TLV, and Opus data.
        /// </summary>
        public bool UpdateTemplate(byte[] latestCombinedReport)
        {
            // A caller that has no matching freshness timestamp must not make
            // arbitrary haptics immortal. Treat that lane as already stale.
            return UpdateTemplate(latestCombinedReport, hapticsExpiryQpc: 0);
        }

        /// <summary>
        /// Atomically publishes current control/haptics bytes and the absolute
        /// QPC deadline for those exact haptics bytes. Queued audio can be much
        /// older; freshness is intentionally evaluated from this template.
        /// </summary>
        public bool UpdateTemplate(byte[] latestCombinedReport,
            long hapticsExpiryQpc)
        {
            if (latestCombinedReport == null ||
                latestCombinedReport.Length != ReportLength || !IsRunning)
            {
                return false;
            }

            byte[] copy = (byte[])latestCombinedReport.Clone();
            lock (stateLock)
            {
                latestTemplate = copy;
                latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
                foreach (OutboundCommand removed in outboundCommands.RemoveWhere(
                    command => command.Kind == MessageKind.UpdateTemplate))
                {
                    // Template commands do not consume report credits.
                }

                if (!outboundCommands.TryEnqueue(new OutboundCommand(
                    MessageKind.UpdateTemplate,
                    BuildTemplatePayload(copy, hapticsExpiryQpc))))
                {
                    return false;
                }
            }

            outboundAvailable.Set();
            return true;
        }

        /// <summary>
        /// Phase-locks presentation to the physical controller oscillator.
        /// Updates are coalesced and only alter future fractional intervals;
        /// the helper never restarts or bursts the stream.
        /// </summary>
        public bool UpdateCadenceRatio(double controllerClockRatio,
            long inputArrivalQpc = 0)
        {
            if (!double.IsFinite(controllerClockRatio) ||
                controllerClockRatio <
                    DualSenseBluetoothAudioPacerScheduler.MinimumRateRatio ||
                controllerClockRatio >
                    DualSenseBluetoothAudioPacerScheduler.MaximumRateRatio ||
                !IsRunning)
            {
                return false;
            }

            byte[] payload = new byte[sizeof(long) * 2];
            BinaryPrimitives.WriteInt64LittleEndian(payload,
                BitConverter.DoubleToInt64Bits(controllerClockRatio));
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(sizeof(long), sizeof(long)),
                Math.Max(0, inputArrivalQpc));
            lock (stateLock)
            {
                foreach (OutboundCommand removed in
                    outboundCommands.RemoveWhere(command =>
                        command.Kind == MessageKind.UpdateCadence))
                {
                }

                if (!outboundCommands.TryEnqueue(new OutboundCommand(
                    MessageKind.UpdateCadence, payload)))
                {
                    return false;
                }
            }

            outboundAvailable.Set();
            return true;
        }

        /// <summary>
        /// Publishes Sony's controller-side media playout level returned in a
        /// CRC-valid normal Bluetooth input report at byte 65. Updates are
        /// coalesced so the HID input thread never waits for pacer IPC.
        /// </summary>
        public bool UpdateControllerMediaBuffer(byte level,
            long observationQpc, double cadenceRatio)
        {
            if (!IsRunning || observationQpc <= 0 ||
                !double.IsFinite(cadenceRatio) ||
                cadenceRatio < DualSenseControllerMediaBufferServo.MinimumRatio ||
                cadenceRatio > DualSenseControllerMediaBufferServo.MaximumRatio)
            {
                return false;
            }

            byte[] payload = new byte[sizeof(long) * 2 + sizeof(byte)];
            BinaryPrimitives.WriteInt64LittleEndian(payload,
                observationQpc);
            payload[sizeof(long)] = level;
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(sizeof(long) + sizeof(byte), sizeof(long)),
                BitConverter.DoubleToInt64Bits(cadenceRatio));
            lock (stateLock)
            {
                if (!outboundCommands.TryReplaceNewestOrEnqueue(
                    command => command.Kind ==
                        MessageKind.UpdateControllerMediaBuffer,
                    new OutboundCommand(
                        MessageKind.UpdateControllerMediaBuffer, payload)))
                {
                    return false;
                }
            }

            outboundAvailable.Set();
            return true;
        }

        /// <summary>
        /// Atomically queues microphone intent and its matching live template.
        /// the native transport's observed Windows contract uses one native 0x32 transition
        /// followed by full-state 0x36 media; it does not insert a competing
        /// 0x31 state write into the active audio FIFO.
        /// </summary>
        public bool UpdateMicrophoneTransition(byte[] latestCombinedReport,
            long hapticsExpiryQpc, bool enabled)
        {
            if (latestCombinedReport == null ||
                latestCombinedReport.Length != ReportLength || !IsRunning)
            {
                return false;
            }

            byte[] template = (byte[])latestCombinedReport.Clone();
            byte[] status = { enabled ? (byte)1 : (byte)0 };

            lock (stateLock)
            {
                var transition = new[]
                {
                    new OutboundCommand(MessageKind.UpdateMicrophoneStatus,
                        status),
                    new OutboundCommand(MessageKind.UpdateTemplate,
                        BuildTemplatePayload(template, hapticsExpiryQpc)),
                };
                if (!outboundCommands.TryReplaceWhereWithGroup(command =>
                        command.Kind == MessageKind.UpdateMicrophoneStatus ||
                        command.Kind == MessageKind.UpdateTemplate ||
                        command.Kind == MessageKind.UpdateControllerState,
                    transition))
                {
                    return false;
                }

                // Intent is deliberately first: the helper freezes the
                // physical media header in its committed mode before the new
                // template can reach presentation. Native 0x32 then consumes
                // one ordered media interval at the reserved boundary.
                latestTemplate = template;
                latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
            }

            outboundAvailable.Set();
            return true;
        }

        /// <summary>
        /// Publishes the measured transport's complete 47-byte common effect snapshot for a
        /// discrete Bluetooth 0x31 state write. Audio and haptics remain on
        /// the ordered 0x39 carrier; repeated state snapshots are suppressed
        /// before they reach the helper.
        /// </summary>
        public bool UpdateControllerState(byte[] latestCombinedReport)
        {
            if (latestCombinedReport == null ||
                latestCombinedReport.Length != ReportLength || !IsRunning)
            {
                return false;
            }

            byte[] payload = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStatePayloadLength];
            Buffer.BlockCopy(latestCombinedReport,
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStateSourceOffset,
                payload, 0, payload.Length);

            lock (stateLock)
            {
                if (latestControllerStateAvailable &&
                    payload.AsSpan().SequenceEqual(latestControllerState))
                {
                    return true;
                }

                if (!outboundCommands.TryEnqueue(new OutboundCommand(
                    MessageKind.UpdateControllerState, payload)))
                {
                    return false;
                }

                Buffer.BlockCopy(payload, 0, latestControllerState, 0,
                    payload.Length);
                latestControllerStateAvailable = true;
            }

            outboundAvailable.Set();
            return true;
        }

        /// <summary>
        /// Publishes one exact game-authored common-state update together with
        /// the quiescent media template that follows it. The helper consumes
        /// both under one state lock, so a speaker/haptics frame can never see
        /// half of this transition.
        /// </summary>
        public bool UpdateGameStateAndTemplate(byte[] gameStateReport,
            byte[] quiescentTemplate, long hapticsExpiryQpc)
        {
            if (gameStateReport == null ||
                gameStateReport.Length != ReportLength ||
                quiescentTemplate == null ||
                quiescentTemplate.Length != ReportLength || !IsRunning)
            {
                return false;
            }

            const int stateLength =
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStatePayloadLength;
            byte[] payload = new byte[stateLength + sizeof(long) +
                ReportLength];

            lock (stateLock)
            {
                if (!accumulatedGameStateAvailable)
                {
                    Buffer.BlockCopy(gameStateReport,
                        DualSenseBluetoothPhysicalOutputSequence.
                            ControllerStateSourceOffset,
                        accumulatedGameState, 0, stateLength);
                    accumulatedGameStateAvailable = true;
                }
                else
                {
                    DualSensePendingGameStateComposer.Merge(
                        accumulatedGameState, gameStateReport,
                        DualSenseBluetoothPhysicalOutputSequence.
                            ControllerStateSourceOffset);
                }

                Buffer.BlockCopy(accumulatedGameState, 0, payload, 0,
                    stateLength);
                BinaryPrimitives.WriteInt64LittleEndian(
                    payload.AsSpan(stateLength, sizeof(long)),
                    hapticsExpiryQpc);
                Buffer.BlockCopy(quiescentTemplate, 0, payload,
                    stateLength + sizeof(long), ReportLength);
                if (!outboundCommands.TryReplaceNewestOrEnqueue(
                    command => command.Kind ==
                        MessageKind.UpdateGameStateAndTemplate,
                    new OutboundCommand(
                        MessageKind.UpdateGameStateAndTemplate, payload)))
                {
                    return false;
                }

                latestTemplate = (byte[])quiescentTemplate.Clone();
                latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
            }

            outboundAvailable.Set();
            return true;
        }

        /// <summary>
        /// Ends one native game-output ownership epoch. The helper performs
        /// state-transition filtering at the final accepted physical-write
        /// boundary, so its latch must be reset in FIFO order before profile
        /// state is allowed to take ownership again.
        /// </summary>
        public bool ResetControllerStateTransitions()
        {
            if (!IsRunning)
            {
                return false;
            }

            lock (stateLock)
            {
                Array.Clear(accumulatedGameState, 0,
                    accumulatedGameState.Length);
                accumulatedGameStateAvailable = false;
                var reset = new OutboundCommand(
                    MessageKind.ResetControllerStateTransitions,
                    Array.Empty<byte>());
                if (!outboundCommands.TryReplaceWhereWithGroup(command =>
                        command.Kind == MessageKind.UpdateGameStateAndTemplate ||
                        command.Kind == MessageKind.UpdateControllerState ||
                        command.Kind ==
                            MessageKind.ResetControllerStateTransitions,
                    new[] { reset }))
                {
                    return false;
                }
            }

            outboundAvailable.Set();
            return true;
        }

        /// <summary>
        /// Drops every report not yet presented and re-arms the eight-report
        /// prime gate. Reports already sent to the helper are acknowledged as
        /// cleared; unsent reports are released here.
        /// </summary>
        public bool Clear()
        {
            if (!IsRunning)
            {
                return false;
            }

            List<PendingReportCompletion> completions = null;
            AcknowledgementDisposition completionDisposition =
                AcknowledgementDisposition.Cleared;
            bool queued = false;
            lock (stateLock)
            {
                currentEpoch = unchecked(currentEpoch + 1);
                if (currentEpoch == 0)
                {
                    currentEpoch = 1;
                }

                foreach (OutboundCommand removed in outboundCommands.RemoveWhere(
                    command => command.Kind == MessageKind.QueueReport))
                {
                    outstandingReports.Remove(removed.ReportId);
                    TakePendingCompletionLocked(removed.ReportId,
                        ref completions);
                }

                byte[] payload = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(payload, currentEpoch);
                if (!outboundCommands.TryEnqueue(new OutboundCommand(
                    MessageKind.Clear, payload)))
                {
                    SetErrorLocked("The pacer command reservoir was full during Clear.");
                    completionDisposition =
                        AcknowledgementDisposition.TransportFault;
                    TakeAllPendingCompletionsLocked(ref completions);
                    outstandingReports.Clear();
                }
                else
                {
                    queued = true;
                }
            }

            CompletePendingReports(completions, completionDisposition);
            if (!queued)
            {
                readyEvent.Set();
                stoppedEvent.Set();
                return false;
            }

            outboundAvailable.Set();
            return true;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0)
            {
                return;
            }

            outboundCommands.Clear();
            List<PendingReportCompletion> completions = null;
            lock (stateLock)
            {
                outstandingReports.Clear();
                TakeAllPendingCompletionsLocked(ref completions);
            }
            CompletePendingReports(completions,
                AcknowledgementDisposition.Cleared);

            if (!outboundCommands.TryEnqueue(new OutboundCommand(
                MessageKind.Stop, Array.Empty<byte>())))
            {
                ClosePipeNoThrow();
                EnsureHelperProcessExited();
            }
            else
            {
                outboundAvailable.Set();
                bool signalled = stoppedEvent.Wait(
                    HelperStopTimeoutMilliseconds);
                if (!IsCleanStopBarrier(signalled,
                    Volatile.Read(ref cleanStopAcknowledged) != 0))
                {
                    // Stopped is the ownership barrier. A generic receiver
                    // error/EOF also sets stoppedEvent, but it does not prove
                    // that the helper released its dedicated HID handle.
                    ClosePipeNoThrow();
                    EnsureHelperProcessExited();
                }
            }
        }

        private void SendHello(Guid authenticationToken)
        {
            byte[] payload = new byte[sizeof(int) + 16];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, sizeof(int)),
                ProtocolVersion);
            authenticationToken.TryWriteBytes(payload.AsSpan(sizeof(int), 16));
            SendFrame(MessageKind.Hello, payload);
        }

        private void SenderLoop()
        {
            using global::DS4Windows.MultimediaThreadRegistration mmcss =
                global::DS4Windows.MultimediaThreadRegistration.EnterProAudio();
            try
            {
                while (Volatile.Read(ref disposed) == 0)
                {
                    bool sentAny = false;
                    while (outboundCommands.TryDequeue(out OutboundCommand command))
                    {
                        sentAny = true;
                        SendFrame(command.Kind, command.Payload);
                        if (command.Kind == MessageKind.Stop)
                        {
                            return;
                        }
                    }

                    if (!sentAny)
                    {
                        outboundAvailable.WaitOne(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                SetError("Pacer IPC sender failed: " + ex.GetType().Name +
                    ": " + ex.Message);
            }
        }

        private void ReceiverLoop()
        {
            using global::DS4Windows.MultimediaThreadRegistration mmcss =
                global::DS4Windows.MultimediaThreadRegistration.EnterProAudio();
            try
            {
                while (Volatile.Read(ref disposed) == 0)
                {
                    ReadFrame(responsePipe, out MessageKind kind,
                        out byte[] payload);
                    switch (kind)
                    {
                        case MessageKind.Ready:
                            readyEvent.Set();
                            break;
                        case MessageKind.ReportAcknowledged:
                            ProcessAcknowledgement(payload);
                            break;
                        case MessageKind.Stopped:
                            Volatile.Write(ref cleanStopAcknowledged, 1);
                            if (Volatile.Read(ref stopping) == 0)
                            {
                                SetError("The isolated DualSense audio pacer stopped unexpectedly after releasing transport ownership.");
                            }
                            else
                            {
                                stoppedEvent.Set();
                            }

                            return;
                        case MessageKind.Error:
                            SetError("DualSense audio pacer helper: " +
                                Encoding.UTF8.GetString(payload));
                            readyEvent.Set();
                            stoppedEvent.Set();
                            return;
                        default:
                            throw new InvalidDataException(
                                $"Unexpected pacer response 0x{(byte)kind:X2}.");
                    }
                }
            }
            catch (EndOfStreamException)
            {
                if (Volatile.Read(ref stopping) == 0 &&
                    Volatile.Read(ref disposed) == 0)
                {
                    SetError("The DualSense audio pacer pipe closed unexpectedly.");
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException ex)
            {
                if (Volatile.Read(ref stopping) == 0 &&
                    Volatile.Read(ref disposed) == 0)
                {
                    SetError("Pacer IPC receiver failed: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                SetError("Pacer IPC receiver failed: " + ex.GetType().Name +
                    ": " + ex.Message);
            }
            finally
            {
                readyEvent.Set();
                stoppedEvent.Set();
            }
        }

        private void ProcessAcknowledgement(byte[] payload)
        {
            const int writerMetricCount = 13;
            int metricOffset = sizeof(long) + sizeof(byte) + sizeof(long);
            if (payload.Length != metricOffset +
                writerMetricCount * sizeof(long))
            {
                throw new InvalidDataException("Invalid pacer acknowledgement length.");
            }

            long reportId = BinaryPrimitives.ReadInt64LittleEndian(
                payload.AsSpan(0, sizeof(long)));
            AcknowledgementDisposition disposition =
                (AcknowledgementDisposition)payload[sizeof(long)];
            long presentedTimestamp = BinaryPrimitives.ReadInt64LittleEndian(
                payload.AsSpan(sizeof(long) + sizeof(byte), sizeof(long)));
            Interlocked.Exchange(ref helperInFlightLimitWaitCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperInFlightLimitEscapeCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperMaximumInFlightLimitWaitTicks,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(
                ref helperMaximumAudioPendingBeforeSubmission,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperShallowAudioSubmissionCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperFullAudioSubmissionCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperCompletedWriteCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperSlowCompletionCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperMaximumCompletionTicks,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperLateSubmissionCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperMaximumSubmissionGapTicks,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperSlowNativeSubmissionCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));
            metricOffset += sizeof(long);
            Interlocked.Exchange(ref helperMaximumNativeSubmissionTicks,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    metricOffset, sizeof(long))));

            PendingReportCompletion completion = null;
            lock (stateLock)
            {
                if (!outstandingReports.Remove(reportId))
                {
                    return;
                }

                if (pendingReportCompletions.TryGetValue(reportId,
                    out completion))
                {
                    pendingReportCompletions.Remove(reportId);
                }
            }

            bool fatalTransportFault =
                IsFatalAcknowledgementDisposition(disposition);
            Interlocked.Increment(ref acknowledgedReports);
            switch (disposition)
            {
                case AcknowledgementDisposition.Presented:
                    Interlocked.Increment(ref presentedReports);
                    RecordPresentationTimestamp(presentedTimestamp);
                    break;
                case AcknowledgementDisposition.Cleared:
                    Interlocked.Increment(ref clearedReports);
                    break;
                case AcknowledgementDisposition.TransportFault:
                    Interlocked.Increment(ref transportFaultReports);
                    Interlocked.Increment(ref rejectedReports);
                    break;
                default:
                    Interlocked.Increment(ref rejectedReports);
                    break;
            }

            completion?.Source.TrySetResult(disposition);

            if (fatalTransportFault)
            {
                // A helper that no longer has a usable HID transport must not
                // retain logical ownership while silently rejecting every
                // following audio frame. The next device submission will
                // dispose this pacer (which is a hard ownership barrier) before
                // selecting another writer.
                SetError("The isolated DualSense audio pacer reported a fatal HID transport fault.");
            }
        }

        private void RecordPresentationTimestamp(long presentedTimestamp)
        {
            if (presentedTimestamp <= 0)
            {
                return;
            }

            long previous = Interlocked.Exchange(
                ref lastPresentedTimestamp, presentedTimestamp);
            if (previous <= 0 || presentedTimestamp <= previous)
            {
                return;
            }

            long gap = presentedTimestamp - previous;
            UpdateMaximum(ref maximumPresentationGapTicks, gap);
            if (gap > Stopwatch.Frequency * 15 / 1000)
            {
                Interlocked.Increment(ref latePresentationCount);
            }
        }

        private static void UpdateMaximum(ref long target, long candidate)
        {
            long observed = Interlocked.Read(ref target);
            while (candidate > observed)
            {
                long previous = Interlocked.CompareExchange(ref target,
                    candidate, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }

        private void TakePendingCompletionLocked(long reportId,
            ref List<PendingReportCompletion> completions)
        {
            if (!pendingReportCompletions.TryGetValue(reportId,
                out PendingReportCompletion completion))
            {
                return;
            }

            pendingReportCompletions.Remove(reportId);
            completions ??= new List<PendingReportCompletion>();
            completions.Add(completion);
        }

        private void TakeAllPendingCompletionsLocked(
            ref List<PendingReportCompletion> completions)
        {
            if (pendingReportCompletions.Count == 0)
            {
                return;
            }

            completions ??= new List<PendingReportCompletion>(
                pendingReportCompletions.Count);
            foreach (PendingReportCompletion completion in
                pendingReportCompletions.Values)
            {
                completions.Add(completion);
            }

            pendingReportCompletions.Clear();
        }

        private static void CompletePendingReports(
            List<PendingReportCompletion> completions,
            AcknowledgementDisposition disposition)
        {
            if (completions == null)
            {
                return;
            }

            foreach (PendingReportCompletion completion in completions)
            {
                completion.Source.TrySetResult(disposition);
            }
        }

        private void SendFrame(MessageKind kind, byte[] payload)
        {
            lock (pipeWriteLock)
            {
                WriteFrame(commandPipe, kind, payload);
            }
        }

        private void SetError(string error)
        {
            List<PendingReportCompletion> completions = null;
            lock (stateLock)
            {
                SetErrorLocked(error);
                outstandingReports.Clear();
                TakeAllPendingCompletionsLocked(ref completions);
            }

            CompletePendingReports(completions,
                AcknowledgementDisposition.TransportFault);
            readyEvent.Set();
            stoppedEvent.Set();
        }

        private void SetErrorLocked(string error)
        {
            if (string.IsNullOrEmpty(lastError))
            {
                lastError = error ?? "Unknown DualSense audio pacer error.";
            }
        }

        private static byte[] BuildQueuePayload(long reportId, int epoch,
            long hapticsExpiryQpc, byte[] report)
        {
            byte[] payload = new byte[sizeof(long) + sizeof(int) + sizeof(long) +
                ReportLength];
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0,
                sizeof(long)), reportId);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sizeof(long),
                sizeof(int)), epoch);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(long) +
                sizeof(int), sizeof(long)), hapticsExpiryQpc);
            Buffer.BlockCopy(report, 0, payload,
                sizeof(long) + sizeof(int) + sizeof(long), ReportLength);
            return payload;
        }

        private static byte[] BuildTemplatePayload(byte[] template,
            long hapticsExpiryQpc)
        {
            byte[] payload = new byte[sizeof(long) + ReportLength];
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0,
                sizeof(long)), hapticsExpiryQpc);
            Buffer.BlockCopy(template, 0, payload, sizeof(long), ReportLength);
            return payload;
        }

        private static bool TryParseHelperArguments(string[] args,
            out string commandPipeName, out string responsePipeName,
            out Guid authenticationToken,
            out int parentProcessId, out string inputArrivalSignalName,
            out string inputClockMapName, out string devicePath)
        {
            commandPipeName = string.Empty;
            responsePipeName = string.Empty;
            authenticationToken = Guid.Empty;
            parentProcessId = 0;
            inputArrivalSignalName = string.Empty;
            inputClockMapName = string.Empty;
            devicePath = string.Empty;
            return args != null && args.Length >= 8 &&
                string.Equals(args[0], HelperArgument,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(commandPipeName = args[1]) &&
                !string.IsNullOrWhiteSpace(responsePipeName = args[2]) &&
                Guid.TryParseExact(args[3], "N", out authenticationToken) &&
                int.TryParse(args[4], out parentProcessId) &&
                parentProcessId > 0 &&
                !string.IsNullOrWhiteSpace(inputArrivalSignalName = args[5]) &&
                !string.IsNullOrWhiteSpace(inputClockMapName = args[6]) &&
                !string.IsNullOrWhiteSpace(devicePath = args[7]);
        }

        private static string GetExactCurrentExecutablePath()
        {
            string path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName ??
                    string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void TryTerminateUninitializedHelper(Process child)
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(false);
                    child.WaitForExit(1000);
                }
            }
            catch
            {
            }
            finally
            {
                child.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            outboundCommands.Clear();
            List<PendingReportCompletion> completions = null;
            lock (stateLock)
            {
                outstandingReports.Clear();
                TakeAllPendingCompletionsLocked(ref completions);
            }
            CompletePendingReports(completions,
                AcknowledgementDisposition.Cleared);

            if (Volatile.Read(ref stopping) == 0)
            {
                // Dispose cannot call Stop after setting disposed because the
                // sender observes disposed. Send Stop directly while the pipe
                // is still available, then let pipe closure be the fallback.
                Interlocked.Exchange(ref stopping, 1);
                try
                {
                    SendFrame(MessageKind.Stop, Array.Empty<byte>());
                    stoppedEvent.Wait(HelperStopTimeoutMilliseconds);
                }
                catch
                {
                }
            }

            ClosePipeNoThrow();
            outboundAvailable.Set();

            // Process exit is the fallback ownership barrier when the helper
            // could not confirm a clean writer retirement. Never return while
            // an orphan can still own the dedicated controller handle.
            EnsureHelperProcessExited();

            if (senderThread.IsAlive && Thread.CurrentThread != senderThread)
            {
                senderThread.Join();
            }

            if (receiverThread.IsAlive && Thread.CurrentThread != receiverThread)
            {
                receiverThread.Join();
            }

            helperProcess.Dispose();
            inputArrivalSignal.Dispose();
            inputClockView.Dispose();
            inputClockMap.Dispose();
            outboundAvailable.Dispose();
            readyEvent.Dispose();
            stoppedEvent.Dispose();
        }

        private void EnsureHelperProcessExited()
        {
            try
            {
                if (helperProcess.HasExited || helperProcess.WaitForExit(
                    HelperProcessExitTimeoutMilliseconds))
                {
                    return;
                }

                helperProcess.Kill(false);
                if (!helperProcess.WaitForExit(
                    HelperProcessExitTimeoutMilliseconds))
                {
                    throw new InvalidOperationException(
                        "The DualSense audio pacer process did not terminate; " +
                        "transport ownership cannot be handed off safely.");
                }
            }
            catch (InvalidOperationException)
            {
                // Process APIs also throw InvalidOperationException when the
                // child exited between HasExited/WaitForExit/Kill. Re-check
                // before treating it as a failed ownership barrier.
                try
                {
                    if (helperProcess.HasExited)
                    {
                        return;
                    }
                }
                catch
                {
                }

                throw;
            }
        }

        private void ClosePipeNoThrow()
        {
            try
            {
                commandPipe.Dispose();
            }
            catch
            {
            }

            try
            {
                responsePipe.Dispose();
            }
            catch
            {
            }
        }

        private static void RunHelper(string commandPipeName,
            string responsePipeName, Guid authenticationToken,
            int parentProcessId, string inputArrivalSignalName,
            string inputClockMapName, string devicePath)
        {
            using var commandPipe = new NamedPipeClientStream(".",
                commandPipeName, PipeDirection.In, PipeOptions.WriteThrough |
                PipeOptions.CurrentUserOnly);
            using var responsePipe = new NamedPipeClientStream(".",
                responsePipeName, PipeDirection.Out, PipeOptions.WriteThrough |
                PipeOptions.CurrentUserOnly);

            try
            {
                commandPipe.Connect(PipeConnectTimeoutMilliseconds);
                responsePipe.Connect(PipeConnectTimeoutMilliseconds);
                using EventWaitHandle inputSignal =
                    EventWaitHandle.OpenExisting(inputArrivalSignalName);
                using MemoryMappedFile inputMap =
                    MemoryMappedFile.OpenExisting(inputClockMapName,
                        MemoryMappedFileRights.Read);
                using MemoryMappedViewAccessor inputView =
                    inputMap.CreateViewAccessor(0, InputClockMapLength,
                        MemoryMappedFileAccess.Read);
                ReadFrame(commandPipe, out MessageKind kind,
                    out byte[] payload);
                string helloError = string.Empty;
                if (kind != MessageKind.Hello || !TryParseHello(payload,
                    authenticationToken, out helloError))
                {
                    TryWriteError(responsePipe,
                        string.IsNullOrEmpty(helloError) ?
                        "Invalid pacer hello message." : helloError);
                    return;
                }

                if (!IsExpectedParentAlive(parentProcessId))
                {
                    TryWriteError(responsePipe,
                        "The pacer parent process exited during initialization.");
                    return;
                }

                if (!DualSenseBluetoothRealtimeWriter.TryCreate(devicePath,
                        UsePairedAudioReports ?
                            DualSenseBluetoothPairedAudioReportBuilder.ReportLength :
                            ReportLength,
                        out DualSenseBluetoothRealtimeWriter writer,
                        out int writerError,
                        slotCount: UsePairedAudioReports ?
                            PairedAudioTransportSlotCount :
                            SingleAudioTransportSlotCount,
                        audioInFlightLimit: UsePairedAudioReports ?
                            PairedAudioInFlightLimit :
                            SingleAudioInFlightLimit))
                {
                    TryWriteError(responsePipe,
                        "Could not open the dedicated DualSense media writer. " +
                        $"Win32Error={writerError}.");
                    return;
                }

                using (writer)
                using (var host = new HelperHost(commandPipe, responsePipe,
                    writer, parentProcessId, inputSignal, inputView))
                {
                    WriteFrame(responsePipe, MessageKind.Ready,
                        Array.Empty<byte>());
                    host.Run();
                }
            }
            catch (Exception ex)
            {
                TryWriteError(responsePipe,
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryParseHello(byte[] payload,
            Guid expectedAuthenticationToken, out string error)
        {
            error = string.Empty;
            if (payload == null || payload.Length != sizeof(int) + 16)
            {
                error = "Invalid pacer hello payload length.";
                return false;
            }

            int version = BinaryPrimitives.ReadInt32LittleEndian(
                payload.AsSpan(0, sizeof(int)));
            Guid token = new Guid(payload.AsSpan(sizeof(int), 16));
            if (version != ProtocolVersion)
            {
                error = $"Unsupported pacer protocol version {version}.";
                return false;
            }

            if (token != expectedAuthenticationToken)
            {
                error = "Pacer authentication token mismatch.";
                return false;
            }

            return true;
        }

        private static bool IsExpectedParentAlive(int parentProcessId)
        {
            try
            {
                using Process parent = Process.GetProcessById(parentProcessId);
                return !parent.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void TryWriteError(Stream pipe, string error)
        {
            try
            {
                WriteFrame(pipe, MessageKind.Error,
                    Encoding.UTF8.GetBytes(error ?? "Unknown pacer helper error."));
            }
            catch
            {
            }
        }

        private static void WriteFrame(Stream stream, MessageKind kind,
            byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            if (payload.Length > 4096)
            {
                throw new InvalidDataException("Pacer IPC payload is too large.");
            }

            Span<byte> header = stackalloc byte[sizeof(byte) + sizeof(int)];
            header[0] = (byte)kind;
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(sizeof(byte)),
                payload.Length);
            stream.Write(header);
            if (payload.Length != 0)
            {
                stream.Write(payload, 0, payload.Length);
            }
        }

        private static void ReadFrame(Stream stream, out MessageKind kind,
            out byte[] payload)
        {
            byte[] header = new byte[sizeof(byte) + sizeof(int)];
            ReadExactly(stream, header, 0, header.Length);
            kind = (MessageKind)header[0];
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
                header.AsSpan(sizeof(byte), sizeof(int)));
            if (payloadLength < 0 || payloadLength > 4096)
            {
                throw new InvalidDataException(
                    $"Invalid pacer IPC payload length {payloadLength}.");
            }

            payload = payloadLength == 0 ? Array.Empty<byte>() :
                new byte[payloadLength];
            if (payloadLength != 0)
            {
                ReadExactly(stream, payload, 0, payloadLength);
            }
        }

        private static int ReadFrameInto(Stream stream, byte[] header,
            byte[] payloadBuffer, out MessageKind kind)
        {
            if (header == null || header.Length < sizeof(byte) + sizeof(int))
            {
                throw new ArgumentException("The pacer IPC header buffer is too small.",
                    nameof(header));
            }

            if (payloadBuffer == null)
            {
                throw new ArgumentNullException(nameof(payloadBuffer));
            }

            ReadExactly(stream, header, 0, sizeof(byte) + sizeof(int));
            kind = (MessageKind)header[0];
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
                header.AsSpan(sizeof(byte), sizeof(int)));
            if (payloadLength < 0 || payloadLength > 4096 ||
                payloadLength > payloadBuffer.Length)
            {
                throw new InvalidDataException(
                    $"Invalid pacer IPC payload length {payloadLength}.");
            }

            if (payloadLength != 0)
            {
                ReadExactly(stream, payloadBuffer, 0, payloadLength);
            }

            return payloadLength;
        }

        private static void ReadExactly(Stream stream, byte[] buffer,
            int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
                count -= read;
            }
        }

        private sealed class HelperHost : IDisposable
        {
            private const int AcknowledgementCapacity =
                HostReservoirCapacity * 2;
            private const int PresentationTraceCapacity = 65536;
            private static readonly long ControllerStateIntervalQpc =
                Math.Max(1, Stopwatch.Frequency / 200);

            private sealed class QueuedReport
            {
                public long Id;
                public int Epoch;
                public long HapticsExpiryQpc;
                public readonly byte[] Report = new byte[ReportLength];

                public void Reset(long id, int epoch,
                    long hapticsExpiryQpc, byte[] source, int sourceOffset)
                {
                    Id = id;
                    Epoch = epoch;
                    HapticsExpiryQpc = hapticsExpiryQpc;
                    Buffer.BlockCopy(source, sourceOffset, Report, 0,
                        ReportLength);
                }
            }

            private readonly struct QueuedAcknowledgement
            {
                public readonly long ReportId;
                public readonly AcknowledgementDisposition Disposition;
                public readonly long PresentedTimestamp;

                public QueuedAcknowledgement(long reportId,
                    AcknowledgementDisposition disposition,
                    long presentedTimestamp)
                {
                    ReportId = reportId;
                    Disposition = disposition;
                    PresentedTimestamp = presentedTimestamp;
                }
            }

            private readonly object stateLock = new object();
            private readonly object pipeWriteLock = new object();
            private readonly Stream commandPipe;
            private readonly Stream responsePipe;
            private readonly DualSenseBluetoothRealtimeWriter writer;
            private readonly int parentProcessId;
            private readonly EventWaitHandle inputArrivalSignal;
            private readonly MemoryMappedViewAccessor inputClockView;
            private readonly DualSenseBluetoothAudioPacerRing<QueuedReport>
                reservoir = new DualSenseBluetoothAudioPacerRing<QueuedReport>(
                    HostReservoirCapacity);
            private readonly DualSenseBluetoothAudioPacerRing<QueuedReport>
                availableReports =
                    new DualSenseBluetoothAudioPacerRing<QueuedReport>(
                        HostReservoirCapacity);
            private readonly DualSenseBluetoothAudioPacerRing<QueuedAcknowledgement>
                acknowledgements =
                    new DualSenseBluetoothAudioPacerRing<QueuedAcknowledgement>(
                        AcknowledgementCapacity);
            private readonly AutoResetEvent reservoirChanged =
                new AutoResetEvent(false);
            private readonly AutoResetEvent acknowledgementAvailable =
                new AutoResetEvent(false);
            private readonly ManualResetEvent stopRequested =
                new ManualResetEvent(false);
            private readonly DualSenseMicrophonePresentationClock
                microphonePresentationClock = new();
            private readonly Thread pacerThread;
            private readonly Thread inputClockThread;
            private readonly Thread acknowledgementThread;
            private readonly byte[] commandHeader =
                new byte[sizeof(byte) + sizeof(int)];
            private readonly byte[] commandPayload = new byte[
                sizeof(long) + sizeof(int) + sizeof(long) + ReportLength];

            private readonly byte[] latestTemplate = new byte[ReportLength];
            private readonly byte[] previousTemplate = new byte[ReportLength];
            private readonly byte[] pairedAudioReport = new byte[
                DualSenseBluetoothPairedAudioReportBuilder.ReportLength];
            private readonly byte[] measuredTransportAudioReport = new byte[
                DualSenseBluetoothMeasuredTransportAudioReportBuilder.ReportLength];
            private readonly byte[] microphoneStatusReport = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    MicrophoneStatusReportLength];
            private readonly byte[] controllerStateReport = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStateReportLength];
            private readonly byte[] pendingControllerState = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStatePayloadLength];
            private readonly DualSenseNativeStateTransitionFilter
                physicalStateTransitionFilter = new();
            private readonly bool useMeasuredTransportAudioTransport;
            private readonly bool useCompactCombinedHapticsTransport;
            private readonly bool useNativeAudioTransport;
            private readonly bool useV5PresentationCadence;
            private long latestTemplateHapticsExpiryQpc;
            private long previousTemplateHapticsExpiryQpc;
            private bool latestTemplateAvailable;
            private bool previousTemplateAvailable;
            private readonly DualSenseBluetoothPhysicalOutputSequence
                physicalOutputSequence = new();
            private int currentEpoch = InitialEpoch;
            private long cadenceRatioBits =
                BitConverter.DoubleToInt64Bits(1.0);
            private long inputArrivalQpc;
            private long lastMicrophoneFrameVersion;
            private long controllerMediaBufferObservationQpc;
            private int controllerMediaBufferLevel = -1;
            private long mediaBufferCadenceRatioBits =
                BitConverter.DoubleToInt64Bits(1.0);
            private bool primeRequired = true;
            private int pendingMicrophoneStatus = -1;
            private int microphoneStatusReportsAhead;
            private bool committedMicrophoneEnabled;
            private bool presentationMicrophoneEnabled;
            private bool pendingControllerStateAvailable;
            private int controllerStateReportsAhead;
            private long lastControllerStateSubmissionQpc;
            private int disposed;
            private readonly string presentationTraceDirectory;
            private readonly long[] presentationTraceQpc;
            private readonly byte[] presentationTraceReportId;
            private readonly byte[] presentationTraceReportSequence;
            private readonly byte[] presentationTracePacketSequence;
            private readonly byte[] presentationTracePacketType;
            private readonly byte[] presentationTraceReservoirCount;
            private readonly byte[] presentationTraceAudioFlags0;
            private readonly byte[] presentationTraceAudioFlags1;
            private readonly byte[] presentationTraceHeadphoneVolume;
            private readonly byte[] presentationTraceSpeakerVolume;
            private readonly byte[] presentationTraceAudioRoute;
            private readonly byte[] presentationTraceAudioGain;
            private readonly uint[] presentationTraceHapticsHash;
            private readonly byte[] presentationTraceMediaBufferLevel;
            private readonly double[] presentationTraceMediaBufferRatio;
            private int presentationTraceCount;

            public HelperHost(Stream commandPipe, Stream responsePipe,
                DualSenseBluetoothRealtimeWriter writer,
                int parentProcessId, EventWaitHandle inputArrivalSignal,
                MemoryMappedViewAccessor inputClockView)
            {
                this.commandPipe = commandPipe;
                this.responsePipe = responsePipe;
                this.writer = writer;
                this.parentProcessId = parentProcessId;
                this.inputArrivalSignal = inputArrivalSignal ??
                    throw new ArgumentNullException(nameof(inputArrivalSignal));
                this.inputClockView = inputClockView ??
                    throw new ArgumentNullException(nameof(inputClockView));
                useMeasuredTransportAudioTransport = false;
                useCompactCombinedHapticsTransport = false;
                useNativeAudioTransport = true;
                useV5PresentationCadence = true;
                string traceDirectory = Environment.GetEnvironmentVariable(
                    "DS4WINDOWS_DUALSENSE_PCM_TRACE_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(traceDirectory))
                {
                    try
                    {
                        presentationTraceDirectory = Path.GetFullPath(
                            traceDirectory);
                        presentationTraceQpc = new long[
                            PresentationTraceCapacity];
                        presentationTraceReportId = new byte[
                            PresentationTraceCapacity];
                        presentationTraceReportSequence = new byte[
                            PresentationTraceCapacity];
                        presentationTracePacketSequence = new byte[
                            PresentationTraceCapacity];
                        presentationTracePacketType = new byte[
                            PresentationTraceCapacity];
                        presentationTraceReservoirCount = new byte[
                            PresentationTraceCapacity];
                        presentationTraceAudioFlags0 = new byte[
                            PresentationTraceCapacity];
                        presentationTraceAudioFlags1 = new byte[
                            PresentationTraceCapacity];
                        presentationTraceHeadphoneVolume = new byte[
                            PresentationTraceCapacity];
                        presentationTraceSpeakerVolume = new byte[
                            PresentationTraceCapacity];
                        presentationTraceAudioRoute = new byte[
                            PresentationTraceCapacity];
                        presentationTraceAudioGain = new byte[
                            PresentationTraceCapacity];
                        presentationTraceHapticsHash = new uint[
                            PresentationTraceCapacity];
                        presentationTraceMediaBufferLevel = new byte[
                            PresentationTraceCapacity];
                        presentationTraceMediaBufferRatio = new double[
                            PresentationTraceCapacity];
                    }
                    catch
                    {
                        presentationTraceDirectory = null;
                    }
                }
                for (int index = 0; index < HostReservoirCapacity; index++)
                {
                    if (!availableReports.TryEnqueue(new QueuedReport()))
                    {
                        throw new InvalidOperationException(
                            "Could not initialize the pacer report pool.");
                    }
                }

                pacerThread = new Thread(PacerLoop)
                {
                    IsBackground = true,
                    Name = "DualSense BT isolated audio pacer",
                    Priority = ThreadPriority.Highest,
                };
                if (UseMicrophoneSequencePresentationClock)
                {
                    inputClockThread = new Thread(InputClockLoop)
                    {
                        IsBackground = true,
                        Name = "DualSense BT microphone clock observer",
                        Priority = ThreadPriority.Highest,
                    };
                }
                acknowledgementThread = new Thread(AcknowledgementLoop)
                {
                    IsBackground = true,
                    Name = "DualSense BT audio pacer acknowledgements",
                    Priority = ThreadPriority.Highest,
                };
            }

            public void Run()
            {
                using global::DS4Windows.MultimediaThreadRegistration mmcss =
                    global::DS4Windows.MultimediaThreadRegistration.EnterProAudio();
                TryRaiseHelperProcessPriority();
                TrySetSustainedLowLatencyGc();
                acknowledgementThread.Start();
                inputClockThread?.Start();
                pacerThread.Start();

                try
                {
                    while (!stopRequested.WaitOne(0))
                    {
                        int payloadLength = ReadFrameInto(commandPipe,
                            commandHeader, commandPayload,
                            out MessageKind kind);
                        switch (kind)
                        {
                            case MessageKind.QueueReport:
                                ReceiveQueuedReport(commandPayload,
                                    payloadLength);
                                break;
                            case MessageKind.UpdateTemplate:
                                ReceiveTemplate(commandPayload, payloadLength);
                                break;
                            case MessageKind.Clear:
                                ReceiveClear(commandPayload, payloadLength);
                                break;
                            case MessageKind.UpdateCadence:
                                ReceiveCadence(commandPayload, payloadLength);
                                break;
                            case MessageKind.UpdateMicrophoneStatus:
                                ReceiveMicrophoneStatus(commandPayload,
                                    payloadLength);
                                break;
                            case MessageKind.UpdateControllerState:
                                ReceiveControllerState(commandPayload,
                                    payloadLength);
                                break;
                            case MessageKind.UpdateControllerMediaBuffer:
                                ReceiveControllerMediaBuffer(commandPayload,
                                    payloadLength);
                                break;
                            case MessageKind.UpdateGameStateAndTemplate:
                                ReceiveGameStateAndTemplate(commandPayload,
                                    payloadLength);
                                break;
                            case MessageKind.ResetControllerStateTransitions:
                                ReceiveResetControllerStateTransitions(
                                    payloadLength);
                                break;
                            case MessageKind.Stop:
                                if (payloadLength != 0)
                                {
                                    throw new InvalidDataException(
                                        "Invalid pacer Stop payload length.");
                                }

                                stopRequested.Set();
                                reservoirChanged.Set();
                                acknowledgementAvailable.Set();
                                break;
                            default:
                                throw new InvalidDataException(
                                    $"Unexpected pacer command 0x{(byte)kind:X2}.");
                        }
                    }
                }
                catch (EndOfStreamException)
                {
                    stopRequested.Set();
                }
                finally
                {
                    stopRequested.Set();
                    reservoirChanged.Set();
                    acknowledgementAvailable.Set();
                    bool pacerStopped = !pacerThread.IsAlive ||
                        pacerThread.Join(2000);
                    bool inputClockStopped = inputClockThread == null ||
                        !inputClockThread.IsAlive ||
                        inputClockThread.Join(2000);
                    bool acknowledgementsStopped =
                        !acknowledgementThread.IsAlive ||
                        acknowledgementThread.Join(2000);

                    // Stopped is a cross-process transport-ownership barrier,
                    // not merely a thread-lifecycle notification. Publish it
                    // only after no helper thread can submit another report and
                    // the helper-owned HID handle plus every OVERLAPPED buffer
                    // have been definitively retired.
                    bool transportReleased = false;
                    if (pacerStopped && inputClockStopped &&
                        acknowledgementsStopped)
                    {
                        writer.Dispose();
                        transportReleased = writer.WaitForDisposal(
                            HelperWriterReleaseTimeoutMilliseconds) &&
                            writer.NativeResourcesReleased;
                    }

                    if (CanPublishStopped(pacerStopped,
                        acknowledgementsStopped, transportReleased))
                    {
                        try
                        {
                            lock (pipeWriteLock)
                            {
                                WriteFrame(responsePipe, MessageKind.Stopped,
                                    Array.Empty<byte>());
                            }
                        }
                        catch
                        {
                        }
                    }

                    WritePresentationTrace();
                }
            }

            private void ReceiveQueuedReport(byte[] payload, int payloadLength)
            {
                int expectedLength = sizeof(long) + sizeof(int) + sizeof(long) +
                    ReportLength;
                if (payloadLength != expectedLength)
                {
                    throw new InvalidDataException(
                        "Invalid queued DualSense report payload length.");
                }

                long id = BinaryPrimitives.ReadInt64LittleEndian(
                    payload.AsSpan(0, sizeof(long)));
                int epoch = BinaryPrimitives.ReadInt32LittleEndian(
                    payload.AsSpan(sizeof(long), sizeof(int)));
                long hapticsExpiryQpc = BinaryPrimitives.ReadInt64LittleEndian(
                    payload.AsSpan(sizeof(long) + sizeof(int), sizeof(long)));

                lock (stateLock)
                {
                    if (epoch != currentEpoch)
                    {
                        QueueAcknowledgement(id,
                            AcknowledgementDisposition.StaleEpoch);
                        return;
                    }

                    if (!availableReports.TryDequeue(out QueuedReport report))
                    {
                        QueueAcknowledgement(id,
                            AcknowledgementDisposition.Rejected);
                        return;
                    }

                    report.Reset(id, epoch, hapticsExpiryQpc, payload,
                        sizeof(long) + sizeof(int) + sizeof(long));
                    // Every queued speaker report already contains the current
                    // control/haptics snapshot. Make it the presentation
                    // template atomically with queue admission so the parent
                    // does not need to send a redundant UpdateTemplate command
                    // (and allocate another clone/payload/command) every
                    // 10.667 ms. Explicit UpdateTemplate remains available for
                    // state changes that arrive between audio reports.
                    ShiftLatestTemplateToPrevious();
                    Buffer.BlockCopy(report.Report, 0, latestTemplate, 0,
                        ReportLength);
                    latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
                    latestTemplateAvailable = true;

                    // the validated implementation serializes/coalesces controller state without
                    // purging audio already admitted to its FIFO. With native
                    // single-frame 0x36 presentation there is no unmatched
                    // paired half to bypass, so retain every admitted audio
                    // generation and let this control follow it in order.
                    if (!reservoir.TryEnqueue(report))
                    {
                        availableReports.TryEnqueue(report);
                        QueueAcknowledgement(id,
                            AcknowledgementDisposition.Rejected);
                        return;
                    }
                }

                reservoirChanged.Set();
            }

            private void ReceiveTemplate(byte[] payload, int payloadLength)
            {
                if (payloadLength != sizeof(long) + ReportLength)
                {
                    throw new InvalidDataException(
                        "Invalid DualSense pacer template length.");
                }

                long hapticsExpiryQpc =
                    BinaryPrimitives.ReadInt64LittleEndian(
                        payload.AsSpan(0, sizeof(long)));
                lock (stateLock)
                {
                    ShiftLatestTemplateToPrevious();
                    Buffer.BlockCopy(payload, sizeof(long), latestTemplate, 0,
                        ReportLength);
                    latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
                    latestTemplateAvailable = true;
                }
            }

            private void ReceiveClear(byte[] payload, int payloadLength)
            {
                if (payloadLength != sizeof(int))
                {
                    throw new InvalidDataException("Invalid pacer Clear payload.");
                }

                int epoch = BinaryPrimitives.ReadInt32LittleEndian(payload);
                lock (stateLock)
                {
                    currentEpoch = epoch;
                    primeRequired = true;
                    Volatile.Write(ref controllerMediaBufferLevel, -1);
                    Interlocked.Exchange(
                        ref controllerMediaBufferObservationQpc, 0);
                    Interlocked.Exchange(ref mediaBufferCadenceRatioBits,
                        BitConverter.DoubleToInt64Bits(1.0));
                    writer.ResetSubmissionClock();
                    while (reservoir.TryDequeue(out QueuedReport report))
                    {
                        QueueAcknowledgement(report.Id,
                            AcknowledgementDisposition.Cleared);
                        if (!availableReports.TryEnqueue(report))
                        {
                            throw new InvalidOperationException(
                                "The pacer report pool overflowed during Clear.");
                        }
                    }

                    if (pendingMicrophoneStatus >= 0 &&
                        !useNativeAudioTransport)
                    {
                        microphoneStatusReportsAhead = 0;
                    }
                    if (pendingControllerStateAvailable)
                    {
                        controllerStateReportsAhead = 0;
                    }
                }

                reservoirChanged.Set();
            }

            private void ReceiveCadence(byte[] payload, int payloadLength)
            {
                if (payloadLength != sizeof(long) * 2)
                {
                    throw new InvalidDataException(
                        "Invalid DualSense pacer cadence payload length.");
                }

                double ratio = BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64LittleEndian(
                        payload.AsSpan(0, sizeof(long))));
                if (!double.IsFinite(ratio) || ratio <
                        DualSenseBluetoothAudioPacerScheduler.MinimumRateRatio ||
                    ratio >
                        DualSenseBluetoothAudioPacerScheduler.MaximumRateRatio)
                {
                    throw new InvalidDataException(
                        "Invalid DualSense pacer cadence ratio.");
                }

                Interlocked.Exchange(ref cadenceRatioBits,
                    BitConverter.DoubleToInt64Bits(ratio));
                Interlocked.Exchange(ref inputArrivalQpc,
                    BinaryPrimitives.ReadInt64LittleEndian(
                        payload.AsSpan(sizeof(long), sizeof(long))));
            }

            private void ReceiveControllerMediaBuffer(byte[] payload,
                int payloadLength)
            {
                if (payloadLength != sizeof(long) * 2 + sizeof(byte))
                {
                    throw new InvalidDataException(
                        "Invalid DualSense controller-media-buffer payload.");
                }

                long observationQpc = BinaryPrimitives.ReadInt64LittleEndian(
                    payload.AsSpan(0, sizeof(long)));
                if (observationQpc <= 0)
                {
                    throw new InvalidDataException(
                        "Invalid DualSense controller-media-buffer timestamp.");
                }

                double cadenceRatio = BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                        sizeof(long) + sizeof(byte), sizeof(long))));
                if (!double.IsFinite(cadenceRatio) ||
                    cadenceRatio < DualSenseControllerMediaBufferServo.MinimumRatio ||
                    cadenceRatio > DualSenseControllerMediaBufferServo.MaximumRatio)
                {
                    throw new InvalidDataException(
                        "Invalid DualSense controller-media-buffer cadence ratio.");
                }

                Volatile.Write(ref controllerMediaBufferLevel,
                    payload[sizeof(long)]);
                Interlocked.Exchange(ref controllerMediaBufferObservationQpc,
                    observationQpc);
                Interlocked.Exchange(ref mediaBufferCadenceRatioBits,
                    BitConverter.DoubleToInt64Bits(cadenceRatio));
            }

            private void ReceiveMicrophoneStatus(byte[] payload,
                int payloadLength)
            {
                if (payloadLength != 1 || payload[0] > 1)
                {
                    throw new InvalidDataException(
                        "Invalid DualSense microphone-status payload.");
                }

                lock (stateLock)
                {
                    int status = payload[0];
                    bool microphoneEnabled = status != 0;
                    if (pendingMicrophoneStatus == status)
                    {
                        // Coalesce an identical request without extending the
                        // accepted-report boundary or replaying the ordered
                        // state/template/native-status transition.
                        return;
                    }

                    if (pendingMicrophoneStatus >= 0 &&
                        committedMicrophoneEnabled == microphoneEnabled)
                    {
                        // The requested state returned to the last accepted
                        // 0x32 before the opposite pending transition reached
                        // the wire. Cancel it without emitting a redundant
                        // mode command or leaving presentation headers staged
                        // in the abandoned mode.
                        pendingMicrophoneStatus = -1;
                        microphoneStatusReportsAhead = 0;
                        presentationMicrophoneEnabled =
                            committedMicrophoneEnabled;
                        return;
                    }

                    if (pendingMicrophoneStatus < 0 &&
                        committedMicrophoneEnabled == microphoneEnabled)
                    {
                        return;
                    }

                    pendingMicrophoneStatus = status;
                    // 0x39 takes its mic/audio-clock header from the second
                    // logical frame. Preserve only complete physical pairs
                    // ahead of 0x32; an odd old-mode half stays behind the
                    // transition and pairs with the next new-mode frame.
                    // V5 inserts native 0x32 beside the current audio
                    // deadline rather than draining the host reservoir first.
                    // Queued 0x36 frames are patched to the committed mode at
                    // presentation, so no stale native header has to remain
                    // ahead of this transition. Compact/paired formats retain
                    // their complete physical-carrier boundary.
                    int reportsAhead;
                    if (useNativeAudioTransport)
                    {
                        // The observed V5 wire contract enables capture
                        // with 0x32 FF before the first 0x36 FF, but disables it
                        // with two 0x36 FE media generations before 0x32 FE.
                        // Stage only the presentation header here; the accepted
                        // native status remains the committed controller mode.
                        presentationMicrophoneEnabled =
                            GetNativeMicrophonePresentationMode(
                                committedMicrophoneEnabled,
                                microphoneEnabled);
                        reportsAhead =
                            GetNativeMicrophoneTransitionReportsAhead(
                                committedMicrophoneEnabled,
                                microphoneEnabled);
                    }
                    else
                    {
                        reportsAhead = CompletePairedReportBoundary(
                            reservoir.CountLeading(
                                IsQueuedSpeakerReport));
                    }
                    if (pendingControllerStateAvailable)
                    {
                        controllerStateReportsAhead = Math.Max(
                            controllerStateReportsAhead, reportsAhead);
                    }

                    microphoneStatusReportsAhead = reportsAhead;
                }

                reservoirChanged.Set();
            }

            private void ReceiveControllerState(byte[] payload,
                int payloadLength)
            {
                if (payloadLength !=
                    DualSenseBluetoothPhysicalOutputSequence.
                        ControllerStatePayloadLength)
                {
                    throw new InvalidDataException(
                        "Invalid DualSense controller-state payload.");
                }

                lock (stateLock)
                {
                    // MeasuredTransport treats 0x31 as a latest-value state latch. The
                    // first snapshot reserves its physical-pair boundary;
                    // later snapshots coalesce at that same position rather
                    // than jumping ahead of audio already admitted.
                    if (!pendingControllerStateAvailable)
                    {
                        controllerStateReportsAhead =
                            CompletePairedReportBoundary(
                                reservoir.CountLeading(
                                    IsQueuedSpeakerReport));
                        if (pendingMicrophoneStatus >= 0)
                        {
                            controllerStateReportsAhead = Math.Max(
                                controllerStateReportsAhead,
                                microphoneStatusReportsAhead);
                        }
                    }
                    Buffer.BlockCopy(payload, 0, pendingControllerState, 0,
                        payloadLength);
                    pendingControllerStateAvailable = true;
                }

                reservoirChanged.Set();
            }

            private void PacerLoop()
            {
                timeBeginPeriod(1);
                using global::DS4Windows.MultimediaThreadRegistration mmcss =
                    global::DS4Windows.MultimediaThreadRegistration.
                        EnterProAudio(critical: true);
                IntPtr timer = CreateHighResolutionTimer();
                // Compact/paired fallbacks retain the rational clock. The V5
                // source opts into the native transport's separately observed 10/20 ms
                // host lattice below; other native sources keep their existing
                // source-completion behavior.
                var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                    Stopwatch.Frequency);
                var nativeTransportScheduler =
                    new DualSenseV5NativePresentationScheduler(
                        Stopwatch.Frequency);
                int nativeTransportStartupBurstReportsRemaining = 0;
                try
                {
                    while (!stopRequested.WaitOne(0))
                    {
                        bool canPresent;
                        bool controlPrimeBypass;
                        bool microphoneStatusReady;
                        bool controllerStateReady;
                        bool sourceDrivenNativePresentation;
                        bool nativeTransportStartupBurstPresentation;
                        int idleWaitMilliseconds = 1000;
                        lock (stateLock)
                        {
                            long nowQpc = Stopwatch.GetTimestamp();
                            microphoneStatusReady =
                                pendingMicrophoneStatus >= 0 &&
                                microphoneStatusReportsAhead <= 0;
                            reservoir.TryPeek(out QueuedReport nextReport);
                            bool nativeMediaCanConsumeControllerState =
                                useV5PresentationCadence &&
                                IsSpeakerAudioReport(nextReport?.Report);
                            controllerStateReady =
                                pendingControllerStateAvailable &&
                                !nativeMediaCanConsumeControllerState &&
                                controllerStateReportsAhead <= 0 &&
                                (lastControllerStateSubmissionQpc == 0 ||
                                    nowQpc -
                                        lastControllerStateSubmissionQpc >=
                                            ControllerStateIntervalQpc);
                            if (pendingControllerStateAvailable &&
                                !controllerStateReady)
                            {
                                long remainingQpc =
                                    ControllerStateIntervalQpc -
                                    (nowQpc -
                                        lastControllerStateSubmissionQpc);
                                idleWaitMilliseconds = Math.Clamp(
                                    (int)Math.Ceiling(remainingQpc * 1000.0 /
                                        Stopwatch.Frequency), 1, 1000);
                            }
                            int speakerReportCount = nextReport != null &&
                                IsSpeakerAudioReport(nextReport.Report) ?
                                    reservoir.CountLeading(
                                        IsQueuedSpeakerReport) : 0;
                            nativeTransportStartupBurstPresentation =
                                useV5PresentationCadence &&
                                nativeTransportStartupBurstReportsRemaining > 0 &&
                                IsSpeakerAudioReport(nextReport?.Report);
                            sourceDrivenNativePresentation =
                                !useV5PresentationCadence &&
                                UsesSourceDrivenNativePresentation(
                                    useNativeAudioTransport,
                                    nextReport?.Report);
                            controlPrimeBypass = primeRequired &&
                                nextReport != null &&
                                !IsSpeakerAudioReport(nextReport.Report);
                            canPresent = microphoneStatusReady ||
                                controllerStateReady ||
                                CanPresentFromTransportGate(
                                    primeRequired, speakerReportCount,
                                    nextReport?.Report,
                                    GetPrimeReportCount(
                                        useMeasuredTransportAudioTransport));
                            if (canPresent && primeRequired &&
                                !controlPrimeBypass &&
                                !microphoneStatusReady &&
                                !controllerStateReady)
                            {
                                primeRequired = false;
                                if (useV5PresentationCadence)
                                {
                                    nativeTransportStartupBurstReportsRemaining =
                                        GetPrimeReportCount(
                                            useMeasuredTransportAudioTransport);
                                    nativeTransportStartupBurstPresentation =
                                        IsSpeakerAudioReport(
                                            nextReport?.Report);
                                    nativeTransportScheduler.Reset();
                                }
                                else
                                {
                                    scheduler.Start(Stopwatch.GetTimestamp(),
                                        ControllerLinkWarmupIntervals,
                                        ControllerReserveTransferIntervals);
                                }
                            }

                        }

                        if (!canPresent)
                        {
                            reservoirChanged.WaitOne(idleWaitMilliseconds);
                            if (!IsExpectedParentAlive(parentProcessId))
                            {
                                stopRequested.Set();
                            }

                            continue;
                        }

                        // Controller-state snapshots remain serialized with
                        // media when one is independently pending. Native mic
                        // transitions do not manufacture an extra 0x31 write;
                        // their complete audio-mode contract is the 0x32.
                        if (controllerStateReady)
                        {
                            bool transportFault;
                            bool accepted;
                            lock (stateLock)
                            {
                                long nowQpc = Stopwatch.GetTimestamp();
                                bool stillReady =
                                    pendingControllerStateAvailable &&
                                    controllerStateReportsAhead <= 0 &&
                                    (lastControllerStateSubmissionQpc == 0 ||
                                        nowQpc -
                                            lastControllerStateSubmissionQpc >=
                                                ControllerStateIntervalQpc);
                                if (!stillReady)
                                {
                                    continue;
                                }

                                byte[] initializationTemplate =
                                    latestTemplateAvailable ? latestTemplate :
                                    previousTemplateAvailable ?
                                        previousTemplate : null;
                                if (initializationTemplate == null)
                                {
                                    accepted = false;
                                    transportFault = false;
                                }
                                else
                                {
                                    physicalOutputSequence.
                                        PrepareControllerState(
                                            pendingControllerState,
                                            initializationTemplate,
                                            controllerStateReport);
                                    accepted = writer.TryWrite(
                                        controllerStateReport,
                                        out transportFault);
                                    if (accepted)
                                    {
                                        physicalOutputSequence.
                                            CommitControllerState();
                                        pendingControllerStateAvailable =
                                            false;
                                        controllerStateReportsAhead = 0;
                                        lastControllerStateSubmissionQpc =
                                            nowQpc;
                                        RecordPresentationTrace(
                                            controllerStateReport, nowQpc,
                                            reservoir.Count);
                                    }
                                }
                            }

                            if (transportFault)
                            {
                                stopRequested.Set();
                                reservoirChanged.Set();
                                acknowledgementAvailable.Set();
                                break;
                            }

                            if (!accepted)
                            {
                                if (!transportFault)
                                {
                                    lock (stateLock)
                                    {
                                        if (pendingControllerStateAvailable)
                                        {
                                            // A saturated control must not
                                            // monopolize the oldest physical
                                            // credit. Give one due media frame
                                            // the next attempt, then retry the
                                            // same uncommitted state write.
                                            controllerStateReportsAhead =
                                                Math.Max(
                                                    controllerStateReportsAhead,
                                                    1);
                                        }
                                    }
                                }
                                reservoirChanged.WaitOne(1);
                            }

                            continue;
                        }

                        if (microphoneStatusReady)
                        {
                            bool transportFault;
                            bool accepted;
                            lock (stateLock)
                            {
                                if (pendingMicrophoneStatus < 0 ||
                                    microphoneStatusReportsAhead > 0)
                                {
                                    continue;
                                }

                                byte[] initializationTemplate =
                                    latestTemplateAvailable ? latestTemplate :
                                    previousTemplateAvailable ?
                                        previousTemplate : null;
                                if (initializationTemplate == null)
                                {
                                    accepted = false;
                                    transportFault = false;
                                }
                                else
                                {
                                    int status = pendingMicrophoneStatus;
                                    physicalOutputSequence.
                                        PrepareMicrophoneStatus(status != 0,
                                            initializationTemplate,
                                            microphoneStatusReport);
                                    accepted = writer.TryWrite(
                                        microphoneStatusReport,
                                        out transportFault);
                                    if (accepted)
                                    {
                                        physicalOutputSequence.
                                            CommitMicrophoneStatus();
                                        if (pendingMicrophoneStatus == status)
                                        {
                                            committedMicrophoneEnabled =
                                                status != 0;
                                            presentationMicrophoneEnabled =
                                                committedMicrophoneEnabled;
                                            // A mode transition can stop the
                                            // controller's 100 Hz input clock.
                                            // Require fresh post-transition
                                            // anchors before phase-locking the
                                            // speaker again.
                                            microphonePresentationClock.Reset();
                                            pendingMicrophoneStatus = -1;
                                            microphoneStatusReportsAhead = 0;
                                        }
                                    }
                                }
                            }

                            if (transportFault)
                            {
                                stopRequested.Set();
                                reservoirChanged.Set();
                                acknowledgementAvailable.Set();
                                break;
                            }

                            if (!accepted)
                            {
                                if (!transportFault)
                                {
                                    lock (stateLock)
                                    {
                                        if (pendingMicrophoneStatus >= 0 &&
                                            !useNativeAudioTransport)
                                        {
                                            // Compact and paired fallbacks may
                                            // let one media carrier pass before
                                            // retrying. V5-native mode
                                            // keeps the exact 0x32 at the strict
                                            // FIFO head until the oldest writer
                                            // slot accepts it.
                                            microphoneStatusReportsAhead =
                                                Math.Max(
                                                    microphoneStatusReportsAhead,
                                                    1);
                                        }
                                    }
                                }
                                reservoirChanged.WaitOne(1);
                            }

                            continue;
                        }

                        bool microphoneClockedPresentation = false;
                        long microphoneSlotSequence = 0;
                        long microphoneSlotDeadlineQpc = 0;
                        int microphoneClockGeneration = 0;
                        if (!controlPrimeBypass &&
                            !sourceDrivenNativePresentation &&
                            !nativeTransportStartupBurstPresentation)
                        {
                            double controllerClockRatio =
                                BitConverter.Int64BitsToDouble(
                                    Interlocked.Read(ref cadenceRatioBits));
                            if (useV5PresentationCadence)
                            {
                                nativeTransportScheduler.SetRateRatio(Math.Clamp(
                                    controllerClockRatio,
                                    DualSenseV5NativePresentationScheduler.
                                        MinimumRateRatio,
                                    DualSenseV5NativePresentationScheduler.
                                        MaximumRateRatio));
                                if (!nativeTransportScheduler.IsStarted)
                                {
                                    // Defensive recovery for a source that
                                    // resumes after an explicit clear without
                                    // replaying a stale deadline. The normal
                                    // path starts this clock when the eighth
                                    // startup report is accepted below.
                                    long nowQpc = Stopwatch.GetTimestamp();
                                    nativeTransportScheduler.Start(nowQpc);
                                    nativeTransportScheduler.AdvanceAfterSend(nowQpc);
                                }
                                WaitUntil(timer,
                                    nativeTransportScheduler.NextDeadlineQpc,
                                    stopRequested);
                            }
                            else
                            {
                            // Byte 65 remains diagnostic telemetry. Feeding it
                            // into steady cadence resampled audible media and
                            // still failed to raise the measured equilibrium.
                            // Keep the wire clock locked solely to the
                            // controller's long-window clock estimator.
                            scheduler.SetRateRatio(Math.Clamp(
                                controllerClockRatio,
                                DualSenseBluetoothAudioPacerScheduler.
                                    MinimumRateRatio,
                                DualSenseBluetoothAudioPacerScheduler.
                                    MaximumRateRatio));
                            // MeasuredTransport owns one continuous media clock and does
                            // not phase-snap it to asynchronous HID input. The
                            // paired 21.333 ms clock has the same requirement:
                            // a bounded per-report HID nudge still wraps every
                            // few reports and becomes presentation jitter. Keep
                            // only the fractional long-window rate correction.
                            scheduler.SetInputPhaseReference(
                                ShouldApplyInputPhaseCorrection(
                                    useCompactCombinedHapticsTransport,
                                    UsePairedAudioReports) ?
                                        Interlocked.Read(ref inputArrivalQpc) :
                                        0);
                            // the measured transport's Windows path owns one absolute media
                            // deadline and never catch-up bursts. Our logical
                            // reports can also be refreshed by high-rate HID
                            // state, so pair availability alone is not a media
                            // clock. Pace every physical 0x39 at the rational
                            // deadline; advancing twice below preserves its two
                            // 10.667 ms generations and the long-window clock
                            // correction without relying on IRP completion as
                            // an L2CAP credit.
                            bool useMicrophoneClock;
                            lock (stateLock)
                            {
                                useMicrophoneClock =
                                    UseMicrophoneSequencePresentationClock &&
                                    useCompactCombinedHapticsTransport &&
                                    !UsePairedAudioReports &&
                                    committedMicrophoneEnabled &&
                                    reservoir.TryPeek(
                                        out QueuedReport dueReport) &&
                                    IsSpeakerAudioReport(dueReport.Report);
                            }

                            if (useMicrophoneClock)
                            {
                                int candidateGeneration =
                                    microphonePresentationClock.Generation;
                                // The microphone model is the sole owner of
                                // duplex wire cadence once it is locked. Do
                                // not gate its rational lattice with the
                                // fallback scheduler: two independently
                                // corrected clocks can manufacture a late
                                // slot followed by a catch-up write.
                                long earliestTransitionDeadline =
                                    Stopwatch.GetTimestamp();
                                if (microphonePresentationClock.TryGetNextSlot(
                                        earliestTransitionDeadline,
                                        out long candidateSequence,
                                        out long candidateDeadline) &&
                                    candidateGeneration ==
                                        microphonePresentationClock.Generation)
                                {
                                    microphoneClockedPresentation = true;
                                    microphoneClockGeneration =
                                        candidateGeneration;
                                    microphoneSlotSequence = candidateSequence;
                                    microphoneSlotDeadlineQpc =
                                        candidateDeadline;
                                }
                            }

                            if (microphoneClockedPresentation)
                            {
                                // Interpolate fifteen speaker generations
                                // uniformly across every sixteen fitted 10 ms
                                // microphone ticks. This preserves the
                                // controller's long-window clock while never
                                // creating a deliberate 20 ms media hole.
                                WaitUntil(timer, microphoneSlotDeadlineQpc,
                                    stopRequested);
                            }
                            else
                            {
                                // Acquisition and stale-model fallback retain
                                // the proven absolute 93.75 Hz media cadence.
                                WaitUntil(timer,
                                    scheduler.PresentationDeadlineQpc,
                                    stopRequested);
                            }
                            }
                        }
                        if (stopRequested.WaitOne(0))
                        {
                            break;
                        }

                        // Legacy lossless paths wait for an oldest-slot credit.
                        // MeasuredTransport and the paired hybrid instead probe the
                        // strict oldest slot without waiting at the due tick;
                        // if it is busy they spend/drop that new audio
                        // generation rather than delaying or bursting it.
                        bool audioWriteAtHead;
                        lock (stateLock)
                        {
                            audioWriteAtHead = !primeRequired &&
                                reservoir.TryPeek(out QueuedReport creditReport) &&
                                IsSpeakerAudioReport(creditReport.Report);
                        }

                        if (audioWriteAtHead &&
                            ShouldWaitForPhysicalWriteCredit(
                                useMeasuredTransportAudioTransport,
                                UsePairedAudioReports) &&
                            !writer.WaitForNextWriteSlot(
                                HelperAudioCreditPollMilliseconds,
                                out bool creditTransportFault) &&
                            !creditTransportFault)
                        {
                            continue;
                        }

                        if (stopRequested.WaitOne(0))
                        {
                            break;
                        }

                        QueuedReport item;
                        QueuedReport pairedItem = null;
                        long itemId;
                        long pairedItemId = 0;
                        AcknowledgementDisposition disposition;
                        AcknowledgementDisposition pairedDisposition =
                            AcknowledgementDisposition.Rejected;
                        long presentedAt;
                        long pairedPresentedAt = 0;
                        bool advanceScheduler;
                        bool advanceV5Scheduler;
                        bool controlOnly;
                        bool controllerStatePiggybacked = false;
                        bool retainedForRetry = false;
                        lock (stateLock)
                        {
                            // A profile clear or microphone transition can
                            // invalidate a token while this thread is waiting.
                            // Never let an old-clock deadline cross that state
                            // boundary.
                            if (microphoneClockedPresentation &&
                                microphoneClockGeneration !=
                                    microphonePresentationClock.Generation)
                            {
                                continue;
                            }

                            if (controlPrimeBypass)
                            {
                                if (!primeRequired ||
                                    !reservoir.TryPeek(
                                        out QueuedReport bypassReport) ||
                                    IsSpeakerAudioReport(bypassReport.Report))
                                {
                                    continue;
                                }
                            }
                            else if (primeRequired)
                            {
                                continue;
                            }

                            bool pairedAudioAtHead = UsePairedAudioReports &&
                                reservoir.TryPeek(out QueuedReport headReport) &&
                                IsSpeakerAudioReport(headReport.Report);
                            if (pairedAudioAtHead)
                            {
                                // The gate above is intentionally checked before
                                // the presentation wait. Revalidate and remove the
                                // complete 0x39 pair as one FIFO operation after
                                // that wait; Clear/control admission can otherwise
                                // invalidate the earlier observation and leave an
                                // unpaired first half behind.
                                if (!reservoir.TryDequeuePair(
                                        IsQueuedSpeakerReport, out item,
                                        out pairedItem))
                                {
                                    continue;
                                }
                            }
                            else if (!reservoir.TryDequeue(out item))
                            {
                                if (ShouldReprimeAfterEmptyReservoir(
                                        useNativeAudioTransport))
                                {
                                    primeRequired = true;
                                    scheduler.Reset();
                                }
                                continue;
                            }

                            // Capture metadata before returning this reusable
                            // slot to the pool; the IPC thread may refill it as
                            // soon as stateLock is released.
                            itemId = item.Id;
                            // Timestamp the presentation boundary immediately
                            // before patch/write. Measuring again after CRC and
                            // WriteFile would feed fixed processing overhead
                            // into the clock and slowly drain the reservoir.
                            presentedAt = Stopwatch.GetTimestamp();
                            controlOnly = !IsSpeakerAudioReport(item.Report);

                            if (pairedItem != null)
                            {
                                pairedItemId = pairedItem.Id;
                            }

                            if (item.Epoch != currentEpoch ||
                                (pairedItem != null &&
                                    pairedItem.Epoch != currentEpoch))
                            {
                                disposition =
                                    AcknowledgementDisposition.StaleEpoch;
                                pairedDisposition = disposition;
                            }
                            else
                            {
                                DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                                    item.Report, item.HapticsExpiryQpc,
                                    pairedItem != null &&
                                        previousTemplateAvailable ?
                                            previousTemplate :
                                        latestTemplateAvailable ?
                                            latestTemplate : null,
                                    pairedItem != null &&
                                        previousTemplateAvailable ?
                                            previousTemplateHapticsExpiryQpc :
                                        latestTemplateHapticsExpiryQpc,
                                    presentedAt);
                                if (pairedItem != null)
                                {
                                    DualSenseBluetoothAudioReportPatcher.PatchForPresentation(
                                        pairedItem.Report,
                                        pairedItem.HapticsExpiryQpc,
                                        latestTemplateAvailable ?
                                            latestTemplate : null,
                                        latestTemplateHapticsExpiryQpc,
                                        presentedAt);
                                }

                                if (useV5PresentationCadence &&
                                    pairedItem == null && !controlOnly &&
                                    pendingControllerStateAvailable &&
                                    controllerStateReportsAhead <= 0)
                                {
                                    DualSenseBluetoothAudioReportPatcher.
                                        ApplyControllerStateForPresentation(
                                            item.Report,
                                            pendingControllerState);
                                    controllerStatePiggybacked = true;
                                }

                                if (pairedItem == null && !controlOnly)
                                {
                                    // The helper owns the physical microphone
                                    // mode. Reports may have been encoded before
                                    // or after a UI request, so derive every wire
                                    // header from the last accepted native 0x32
                                    // transition rather than stale producer state.
                                    ApplyCommittedMicrophoneMode(item.Report,
                                        presentationMicrophoneEnabled);
                                }

                                // Filter at the last mutable boundary, after
                                // queued media, the current template, pending
                                // game state, and microphone mode have been
                                // composed. Filtering earlier allowed a later
                                // template patch to resurrect validity strobes
                                // for player LEDs, triggers, and other stateful
                                // commands on every media frame.
                                physicalStateTransitionFilter.Filter(
                                    item.Report,
                                    DualSenseBluetoothPhysicalOutputSequence.
                                        ControllerStateSourceOffset);

                                bool transportFault;
                                bool accepted;
                                if (pairedItem == null)
                                {
                                    byte[] physicalReport = item.Report;
                                    if (controlOnly)
                                    {
                                        physicalOutputSequence.PrepareControl(
                                            item.Report);
                                    }
                                    else
                                    {
                                        if (useMeasuredTransportAudioTransport)
                                        {
                                            if (useCompactCombinedHapticsTransport)
                                            {
                                                physicalOutputSequence.
                                                    PrepareMeasuredTransportCombinedAudio(
                                                        item.Report,
                                                        measuredTransportAudioReport);
                                            }
                                            else
                                            {
                                                physicalOutputSequence.
                                                    PrepareMeasuredTransportAudio(
                                                        item.Report,
                                                        measuredTransportAudioReport);
                                            }

                                            physicalReport =
                                                measuredTransportAudioReport;
                                        }
                                        else
                                        {
                                            physicalOutputSequence.
                                                PrepareNativeAudio(item.Report);
                                        }
                                    }
                                    // V5 serializes 0x32 through the same
                                    // strict writer slot as media and commits
                                    // once WriteFile accepts/PENDING. Draining
                                    // every outstanding 0x36 and then waiting
                                    // for 0x32 completion breaks that FIFO.
                                    accepted = writer.TryWrite(physicalReport,
                                        out transportFault);
                                    if (accepted)
                                    {
                                        RecordPresentationTrace(physicalReport,
                                            presentedAt, reservoir.Count);
                                    }
                                }
                                else
                                {
                                    physicalOutputSequence.PreparePairedAudio(
                                        item.Report, pairedItem.Report,
                                        pairedAudioReport);
                                    accepted = writer.TryWrite(
                                        pairedAudioReport,
                                        out transportFault);
                                    if (accepted)
                                    {
                                        RecordPresentationTrace(item.Report,
                                            presentedAt, reservoir.Count + 1);
                                        RecordPresentationTrace(
                                            pairedItem.Report, presentedAt,
                                            reservoir.Count);
                                    }
                                }

                                // MeasuredTransport spends counters and drops the new
                                // audio generation when its strict oldest slot
                                // is busy. Apply that Windows transport rule to
                                // one indivisible 0x39 pair as well. Controls
                                // remain retriable; hard I/O faults still tear
                                // down ownership.
                                bool skippedSaturatedAudio =
                                    ShouldDropSaturatedAudio(
                                        useMeasuredTransportAudioTransport,
                                        pairedItem != null, controlOnly,
                                        accepted, transportFault);

                                if (accepted ||
                                    skippedSaturatedAudio)
                                {
                                    physicalOutputSequence.Commit(
                                        pairedItem != null || !controlOnly);
                                }

                                if (accepted && controllerStatePiggybacked)
                                {
                                    pendingControllerStateAvailable = false;
                                    controllerStateReportsAhead = 0;
                                    lastControllerStateSubmissionQpc =
                                        presentedAt;
                                }

                                disposition = accepted ?
                                    AcknowledgementDisposition.Presented :
                                    transportFault ?
                                        AcknowledgementDisposition.TransportFault :
                                        AcknowledgementDisposition.Rejected;
                                pairedDisposition = disposition;

                                if (!skippedSaturatedAudio &&
                                    ShouldRetainSaturatedWrite(accepted,
                                        transportFault))
                                {
                                    retainedForRetry = pairedItem == null ?
                                        reservoir.TryEnqueueFront(item) :
                                        reservoir.TryEnqueuePairFront(item,
                                            pairedItem);
                                    if (!retainedForRetry)
                                    {
                                        throw new InvalidOperationException(
                                            "The pacer could not restore a saturated report to the FIFO head.");
                                    }
                                }
                            }

                            if (!retainedForRetry &&
                                disposition ==
                                    AcknowledgementDisposition.Presented &&
                                nativeTransportStartupBurstPresentation &&
                                !controlOnly)
                            {
                                nativeTransportStartupBurstReportsRemaining =
                                    Math.Max(0,
                                        nativeTransportStartupBurstReportsRemaining -
                                            1);
                                if (nativeTransportStartupBurstReportsRemaining == 0)
                                {
                                    // The eighth accepted report is position
                                    // zero on the native transport's 16-tick lattice. The
                                    // first steady report is therefore due one
                                    // 10 ms host tick later, while the initial
                                    // eight reports remain an immediate burst.
                                    nativeTransportScheduler.Start(presentedAt);
                                    nativeTransportScheduler.AdvanceAfterSend(
                                        presentedAt);
                                }
                            }

                            if (!retainedForRetry &&
                                disposition ==
                                    AcknowledgementDisposition.Presented &&
                                !controlOnly &&
                                pendingMicrophoneStatus >= 0 &&
                                microphoneStatusReportsAhead > 0)
                            {
                                // Spend the boundary only for speaker media
                                // that the physical writer accepted. Control,
                                // stale, rejected, and saturated/dropped items
                                // do not exist on the controller's wire FIFO.
                                microphoneStatusReportsAhead = Math.Max(0,
                                    microphoneStatusReportsAhead -
                                    (pairedItem == null ? 1 : 2));
                            }
                            if (!retainedForRetry &&
                                pendingControllerStateAvailable &&
                                controllerStateReportsAhead > 0)
                            {
                                controllerStateReportsAhead = Math.Max(0,
                                    controllerStateReportsAhead -
                                        (pairedItem == null ? 1 : 2));
                            }

                            if (!retainedForRetry &&
                                ShouldRequireAudioPrimeAfterPresentation(
                                controlOnly, reservoir.Count))
                            {
                                primeRequired = true;
                                scheduler.Reset();
                                if (controlOnly)
                                {
                                    writer.ResetSubmissionClock();
                                }
                            }

                            if (!retainedForRetry &&
                                !availableReports.TryEnqueue(item))
                            {
                                throw new InvalidOperationException(
                                    "The pacer report pool overflowed after presentation.");
                            }

                            if (!retainedForRetry && pairedItem != null &&
                                !availableReports.TryEnqueue(pairedItem))
                            {
                                throw new InvalidOperationException(
                                    "The pacer report pool overflowed after paired presentation.");
                            }

                            advanceScheduler = !retainedForRetry &&
                                !controlPrimeBypass &&
                                !primeRequired &&
                                !sourceDrivenNativePresentation &&
                                !useV5PresentationCadence;
                            advanceV5Scheduler = !retainedForRetry &&
                                disposition ==
                                    AcknowledgementDisposition.Presented &&
                                !controlOnly &&
                                !controlPrimeBypass &&
                                !primeRequired &&
                                useV5PresentationCadence &&
                                !nativeTransportStartupBurstPresentation;
                        }

                        if (retainedForRetry)
                        {
                            // Do not acknowledge, spend sequence/counter state,
                            // or advance the rational clock. The next loop waits
                            // for the same oldest writer credit and retries this
                            // exact FIFO generation without a catch-up burst.
                            continue;
                        }

                        if (advanceScheduler)
                        {
                            pairedPresentedAt =
                                scheduler.AdvanceAfterSend(presentedAt);
                            if (pairedItem != null)
                            {
                                scheduler.AdvanceAfterSend(presentedAt);
                            }
                        }

                        if (advanceV5Scheduler)
                        {
                            nativeTransportScheduler.AdvanceAfterSend(presentedAt);
                        }

                        if (microphoneClockedPresentation)
                        {
                            microphonePresentationClock.Advance(
                                microphoneSlotSequence);
                        }

                        QueueAcknowledgement(itemId, disposition,
                            controlOnly ? 0 : presentedAt);
                        if (pairedItem != null)
                        {
                            QueueAcknowledgement(pairedItemId,
                                pairedDisposition, pairedPresentedAt);
                        }
                        if (IsFatalAcknowledgementDisposition(disposition))
                        {
                            // The writer is permanently unusable until a new
                            // owner is established. Send the fatal ACK, stop the
                            // presentation loop, and let the parent force a
                            // process-exit barrier if clean retirement fails.
                            stopRequested.Set();
                            reservoirChanged.Set();
                            acknowledgementAvailable.Set();
                            break;
                        }

                    }
                }
                finally
                {
                    if (timer != IntPtr.Zero)
                    {
                        CloseHandle(timer);
                    }

                    timeEndPeriod(1);
                }
            }

            private void ReceiveGameStateAndTemplate(byte[] payload,
                int payloadLength)
            {
                const int stateLength =
                    DualSenseBluetoothPhysicalOutputSequence.
                        ControllerStatePayloadLength;
                const int templateOffset = stateLength + sizeof(long);
                if (payloadLength != templateOffset + ReportLength)
                {
                    throw new InvalidDataException(
                        "Invalid atomic DualSense game-state/template payload length.");
                }

                long hapticsExpiryQpc =
                    BinaryPrimitives.ReadInt64LittleEndian(
                        payload.AsSpan(stateLength, sizeof(long)));
                lock (stateLock)
                {
                    ShiftLatestTemplateToPrevious();
                    Buffer.BlockCopy(payload, templateOffset,
                        latestTemplate, 0, ReportLength);
                    latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
                    latestTemplateAvailable = true;

                    Buffer.BlockCopy(payload, 0, pendingControllerState, 0,
                        stateLength);
                    pendingControllerStateAvailable = true;
                    // A V5 media generation consumes this state atomically on
                    // its next due slot. With no media pending, the normal
                    // controller-state branch emits a serialized 0x31 latch.
                    controllerStateReportsAhead = 0;
                }

                reservoirChanged.Set();
            }

            private void ReceiveResetControllerStateTransitions(
                int payloadLength)
            {
                if (payloadLength != 0)
                {
                    throw new InvalidDataException(
                        "Invalid controller-state transition reset payload.");
                }

                lock (stateLock)
                {
                    physicalStateTransitionFilter.Reset();
                    pendingControllerStateAvailable = false;
                    controllerStateReportsAhead = 0;
                }
            }

            private static bool IsQueuedSpeakerReport(QueuedReport report)
            {
                return report != null && IsSpeakerAudioReport(report.Report);
            }

            private void ShiftLatestTemplateToPrevious()
            {
                if (!latestTemplateAvailable)
                {
                    return;
                }

                Buffer.BlockCopy(latestTemplate, 0, previousTemplate, 0,
                    ReportLength);
                previousTemplateHapticsExpiryQpc =
                    latestTemplateHapticsExpiryQpc;
                previousTemplateAvailable = true;
            }

            private void QueueAcknowledgement(long reportId,
                AcknowledgementDisposition disposition,
                long presentedTimestamp = 0)
            {
                if (!acknowledgements.TryEnqueue(new QueuedAcknowledgement(
                    reportId, disposition, presentedTimestamp)))
                {
                    // Continuing without an acknowledgement would permanently
                    // consume a parent-side reservoir credit. Fail closed.
                    stopRequested.Set();
                    reservoirChanged.Set();
                    return;
                }

                acknowledgementAvailable.Set();
            }

            private void AcknowledgementLoop()
            {
                using global::DS4Windows.MultimediaThreadRegistration mmcss =
                    global::DS4Windows.MultimediaThreadRegistration.EnterProAudio();
                const int writerMetricCount = 13;
                byte[] payload = new byte[
                    sizeof(long) + sizeof(byte) + sizeof(long) +
                    writerMetricCount * sizeof(long)];
                try
                {
                    while (!stopRequested.WaitOne(0) || acknowledgements.Count != 0)
                    {
                        if (!acknowledgements.TryDequeue(
                            out QueuedAcknowledgement acknowledgement))
                        {
                            acknowledgementAvailable.WaitOne(1000);
                            continue;
                        }

                        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0,
                            sizeof(long)), acknowledgement.ReportId);
                        payload[sizeof(long)] =
                            (byte)acknowledgement.Disposition;
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(sizeof(long) + sizeof(byte),
                                sizeof(long)),
                            acknowledgement.PresentedTimestamp);
                        int metricOffset = sizeof(long) + sizeof(byte) +
                            sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.InFlightLimitWaitCount);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.InFlightLimitEscapeCount);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.MaximumInFlightLimitWaitTicks);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.MaximumAudioPendingBeforeSubmission);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.ShallowAudioSubmissionCount);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.FullAudioSubmissionCount);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.CompletedWrites);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.SlowCompletionCount);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.MaximumCompletionTicks);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.LateSubmissionCount);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.MaximumSubmissionGapTicks);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.SlowNativeSubmissionCount);
                        metricOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(metricOffset, sizeof(long)),
                            writer.MaximumNativeSubmissionTicks);
                        lock (pipeWriteLock)
                        {
                            WriteFrame(responsePipe,
                                MessageKind.ReportAcknowledged, payload);
                        }
                    }
                }
                catch
                {
                    stopRequested.Set();
                    reservoirChanged.Set();
                }
            }

            private static void WaitUntil(IntPtr timer, long targetQpc,
                WaitHandle stopEvent)
            {
                while (true)
                {
                    long remaining = targetQpc - Stopwatch.GetTimestamp();
                    if (remaining <= 0 || stopEvent.WaitOne(0))
                    {
                        return;
                    }

                    double remainingMilliseconds = remaining * 1000.0 /
                        Stopwatch.Frequency;
                    if (remainingMilliseconds <= 0.75)
                    {
                        Thread.SpinWait(64);
                        continue;
                    }

                    if (timer != IntPtr.Zero)
                    {
                        // Wake about 0.5 ms before the QPC deadline, then use a
                        // short allocation-free spin to remove scheduler jitter.
                        long relativeHundredNanoseconds = -Math.Max(1,
                            (long)((remainingMilliseconds - 0.5) * 10000.0));
                        if (SetWaitableTimer(timer,
                            ref relativeHundredNanoseconds, 0, IntPtr.Zero,
                            IntPtr.Zero, false))
                        {
                            WaitForSingleObject(timer, 20);
                            continue;
                        }
                    }

                    Thread.Sleep(Math.Max(1,
                        (int)Math.Floor(remainingMilliseconds - 0.5)));
                }
            }

            private void InputClockLoop()
            {
                using global::DS4Windows.MultimediaThreadRegistration mmcss =
                    global::DS4Windows.MultimediaThreadRegistration.EnterProAudio();
                WaitHandle[] waits = { stopRequested, inputArrivalSignal };
                try
                {
                    while (WaitHandle.WaitAny(waits) == 1)
                    {
                        if (TryReadLatestMicrophoneFrame(
                            out long arrivalQpc, out byte sequence))
                        {
                            microphonePresentationClock.Observe(sequence,
                                arrivalQpc);
                        }
                    }
                }
                catch (Exception ex) when (ex is ObjectDisposedException ||
                    ex is InvalidOperationException ||
                    ex is IOException)
                {
                    // The model is optional pacing telemetry. If its shared
                    // page disappears during teardown, invalidate it and let
                    // the absolute media clock remain the fail-open path.
                    microphonePresentationClock.Reset();
                }
            }

            private bool TryReadLatestMicrophoneFrame(
                out long arrivalQpc, out byte sequence)
            {
                arrivalQpc = 0;
                sequence = 0;
                // The parent is the only writer. Its odd/even version protects
                // this three-field snapshot if another 10 ms microphone frame
                // arrives while the helper is reading the shared page.
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    long begin = inputClockView.ReadInt64(
                        InputClockVersionOffset);
                    if (begin <= 0 || (begin & 1) != 0 ||
                        begin == lastMicrophoneFrameVersion)
                    {
                        Thread.SpinWait(32);
                        continue;
                    }

                    Thread.MemoryBarrier();
                    long candidateQpc = inputClockView.ReadInt64(
                        InputClockTimestampOffset);
                    int candidateSequence = inputClockView.ReadInt32(
                        InputClockSequenceOffset);
                    Thread.MemoryBarrier();
                    long end = inputClockView.ReadInt64(
                        InputClockVersionOffset);
                    if (begin == end && (end & 1) == 0)
                    {
                        lastMicrophoneFrameVersion = end;
                        arrivalQpc = candidateQpc;
                        sequence = unchecked((byte)candidateSequence);
                        return arrivalQpc > 0;
                    }

                    Thread.SpinWait(32);
                }

                return false;
            }

            private void RecordPresentationTrace(byte[] report,
                long presentedAt, int reservoirCount)
            {
                int index = presentationTraceCount;
                if (presentationTraceQpc == null ||
                    index >= PresentationTraceCapacity)
                {
                    return;
                }

                presentationTraceQpc[index] = presentedAt;
                presentationTraceReportId[index] = report[0];
                presentationTraceReportSequence[index] =
                    (byte)(report[1] >> 4);
                presentationTracePacketSequence[index] = report[0] == 0x31 ?
                    (byte)0 : report[0] == 0x35 && report[3] == 6 ?
                        report[9] : report[10];
                presentationTraceReservoirCount[index] = (byte)Math.Clamp(
                    reservoirCount, 0, byte.MaxValue);
                presentationTraceMediaBufferLevel[index] = (byte)Math.Clamp(
                    Volatile.Read(ref controllerMediaBufferLevel), 0,
                    byte.MaxValue);
                presentationTraceMediaBufferRatio[index] =
                    BitConverter.Int64BitsToDouble(Interlocked.Read(
                        ref mediaBufferCadenceRatioBits));
                if (report[0] == 0x31)
                {
                    presentationTracePacketType[index] = report[2];
                    presentationTraceAudioFlags0[index] = report[3];
                    presentationTraceAudioFlags1[index] = report[4];
                    presentationTraceHeadphoneVolume[index] = report[7];
                    presentationTraceSpeakerVolume[index] = report[8];
                    presentationTraceAudioRoute[index] = report[10];
                    presentationTraceAudioGain[index] = report[40];
                    uint hash = 2166136261u;
                    for (int offset = 3; offset < 50; offset++)
                    {
                        hash = (hash ^ report[offset]) * 16777619u;
                    }
                    presentationTraceHapticsHash[index] = hash;
                }
                else if (report[0] == 0x35)
                {
                    bool shortenedDuplexHeader = report[3] == 6;
                    int firstHeaderOffset = shortenedDuplexHeader ? 10 : 11;
                    int hapticsAfterSpeakerOffset =
                        firstHeaderOffset + 2 + 200;
                    int speakerAfterHapticsOffset =
                        firstHeaderOffset + 2 + 64;
                    int firstCompactType = report[firstHeaderOffset] & 0x3F;
                    int secondCompactType =
                        report[speakerAfterHapticsOffset] & 0x3F;
                    bool speakerFirst =
                        (firstCompactType == 0x13 ||
                            firstCompactType == 0x16) &&
                        report[firstHeaderOffset + 1] == 200 &&
                        (report[hapticsAfterSpeakerOffset] & 0x3F) == 0x12 &&
                        report[hapticsAfterSpeakerOffset + 1] == 64;
                    bool hapticsFirst = firstCompactType == 0x12 &&
                        report[firstHeaderOffset + 1] == 64 &&
                        (secondCompactType == 0x13 ||
                            secondCompactType == 0x16) &&
                        report[speakerAfterHapticsOffset + 1] == 200;
                    bool combinedHaptics = speakerFirst || hapticsFirst;
                    presentationTracePacketType[index] = speakerFirst ?
                        report[firstHeaderOffset] : hapticsFirst ?
                            report[speakerAfterHapticsOffset] :
                            report[firstHeaderOffset];
                    // Compact 0x35 has no state snapshot, but byte 4 still
                    // carries the audio-section mask (FE playback / shortened
                    // 7F duplex). Record it so a trace can prove which Sony
                    // header shape reached the physical carrier.
                    presentationTraceAudioFlags0[index] = report[4];
                    presentationTraceAudioFlags1[index] = 0;
                    presentationTraceHeadphoneVolume[index] = 0;
                    presentationTraceSpeakerVolume[index] = 0;
                    presentationTraceAudioRoute[index] = 0;
                    presentationTraceAudioGain[index] = 0;
                    if (combinedHaptics)
                    {
                        uint hash = 2166136261u;
                        int hapticsOffset = speakerFirst ?
                            hapticsAfterSpeakerOffset + 2 :
                            firstHeaderOffset + 2;
                        for (int offset = hapticsOffset;
                            offset < hapticsOffset + 64; offset++)
                        {
                            hash = (hash ^ report[offset]) * 16777619u;
                        }
                        presentationTraceHapticsHash[index] = hash;
                    }
                    else
                    {
                        presentationTraceHapticsHash[index] = 0;
                    }
                }
                else
                {
                    presentationTracePacketType[index] = report[142];
                    presentationTraceAudioFlags0[index] = report[13];
                    presentationTraceAudioFlags1[index] = report[14];
                    presentationTraceHeadphoneVolume[index] = report[17];
                    presentationTraceSpeakerVolume[index] = report[18];
                    presentationTraceAudioRoute[index] = report[20];
                    presentationTraceAudioGain[index] = report[50];
                    uint hash = 2166136261u;
                    for (int offset = 78; offset < 142; offset++)
                    {
                        hash = (hash ^ report[offset]) * 16777619u;
                    }
                    presentationTraceHapticsHash[index] = hash;
                }
                presentationTraceCount = index + 1;
            }

            private void WritePresentationTrace()
            {
                if (presentationTraceQpc == null ||
                    string.IsNullOrWhiteSpace(presentationTraceDirectory) ||
                    presentationTraceCount == 0)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(presentationTraceDirectory);
                    string path = Path.Combine(presentationTraceDirectory,
                        $"dualsense-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-reports-{Environment.ProcessId}.csv");
                    using var output = new StreamWriter(path, false,
                        new UTF8Encoding(false));
                    output.WriteLine($"qpcFrequency,{Stopwatch.Frequency}");
                    output.WriteLine(
                        "index,qpc,reportId,reportSequence,packetSequence,packetType,reservoirCount,audioFlags0,audioFlags1,headphoneVolume,speakerVolume,audioRoute,audioGain,hapticsHash,controllerMediaBufferLevel,mediaBufferCadenceRatio");
                    for (int index = 0; index < presentationTraceCount;
                        index++)
                    {
                        output.Write(index);
                        output.Write(',');
                        output.Write(presentationTraceQpc[index]);
                        output.Write(',');
                        output.Write(presentationTraceReportId[index]);
                        output.Write(',');
                        output.Write(presentationTraceReportSequence[index]);
                        output.Write(',');
                        output.Write(presentationTracePacketSequence[index]);
                        output.Write(',');
                        output.Write(presentationTracePacketType[index]);
                        output.Write(',');
                        output.Write(presentationTraceReservoirCount[index]);
                        output.Write(',');
                        output.Write(presentationTraceAudioFlags0[index]);
                        output.Write(',');
                        output.Write(presentationTraceAudioFlags1[index]);
                        output.Write(',');
                        output.Write(presentationTraceHeadphoneVolume[index]);
                        output.Write(',');
                        output.Write(presentationTraceSpeakerVolume[index]);
                        output.Write(',');
                        output.Write(presentationTraceAudioRoute[index]);
                        output.Write(',');
                        output.Write(presentationTraceAudioGain[index]);
                        output.Write(',');
                        output.Write(presentationTraceHapticsHash[index]);
                        output.Write(',');
                        output.Write(
                            presentationTraceMediaBufferLevel[index]);
                        output.Write(',');
                        output.WriteLine(
                            presentationTraceMediaBufferRatio[index].ToString(
                                "R", System.Globalization.CultureInfo.
                                    InvariantCulture));
                    }
                }
                catch
                {
                    // Diagnostics must never affect the audio transport.
                }
            }

            private static IntPtr CreateHighResolutionTimer()
            {
                IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null,
                    CreateWaitableTimerHighResolution, TimerAccess);
                return timer != IntPtr.Zero ? timer :
                    CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAccess);
            }

            private static void TryRaiseHelperProcessPriority()
            {
                try
                {
                    Process.GetCurrentProcess().PriorityClass =
                        ProcessPriorityClass.High;
                }
                catch
                {
                }
            }

            private static void TrySetSustainedLowLatencyGc()
            {
                try
                {
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                stopRequested.Set();
                reservoirChanged.Set();
                acknowledgementAvailable.Set();
                bool pacerStopped = !pacerThread.IsAlive;
                if (!pacerStopped && Thread.CurrentThread != pacerThread)
                {
                    pacerStopped = pacerThread.Join(2000);
                }

                bool inputClockStopped = inputClockThread == null ||
                    !inputClockThread.IsAlive;
                if (!inputClockStopped && inputClockThread != null &&
                    Thread.CurrentThread != inputClockThread)
                {
                    inputClockStopped = inputClockThread.Join(2000);
                }

                bool acknowledgementStopped = !acknowledgementThread.IsAlive;
                if (!acknowledgementStopped &&
                    Thread.CurrentThread != acknowledgementThread)
                {
                    acknowledgementStopped = acknowledgementThread.Join(2000);
                }

                // Do not dispose wait handles out from under a worker that did
                // not observe shutdown in time. The helper process is about to
                // exit, and leaking these three tiny handles is safer than an
                // ObjectDisposedException on a live high-priority thread.
                if (pacerStopped && inputClockStopped &&
                    acknowledgementStopped)
                {
                    reservoirChanged.Dispose();
                    acknowledgementAvailable.Dispose();
                    stopRequested.Dispose();
                }
            }
        }

        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAccess = 0x00000002 | 0x00100000;

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(
            IntPtr timerAttributes, string timerName, uint flags,
            uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(IntPtr timer,
            ref long dueTime, int period, IntPtr completionRoutine,
            IntPtr completionArgument,
            [MarshalAs(UnmanagedType.Bool)] bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>
    /// Pure bounded FIFO used by both sides of the pacer and directly by unit
    /// tests. It never overwrites an older element when full.
    /// </summary>
    internal sealed class DualSenseBluetoothAudioPacerRing<T>
    {
        private readonly object syncRoot = new object();
        private readonly T[] entries;
        private int head;
        private int count;

        public DualSenseBluetoothAudioPacerRing(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            entries = new T[capacity];
        }

        public int Capacity => entries.Length;

        public int Count
        {
            get
            {
                lock (syncRoot)
                {
                    return count;
                }
            }
        }

        public bool TryEnqueue(T item)
        {
            lock (syncRoot)
            {
                if (count == entries.Length)
                {
                    return false;
                }

                entries[(head + count) % entries.Length] = item;
                count++;
                return true;
            }
        }

        /// <summary>
        /// Replaces the newest matching coalescible item in place, or appends
        /// when none exists. Unlike RemoveWhere followed by TryEnqueue this
        /// performs no temporary-list allocation on high-rate telemetry paths.
        /// </summary>
        public bool TryReplaceNewestOrEnqueue(Predicate<T> predicate, T item)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }

            lock (syncRoot)
            {
                for (int offset = count - 1; offset >= 0; offset--)
                {
                    int index = (head + offset) % entries.Length;
                    if (predicate(entries[index]))
                    {
                        entries[index] = item;
                        return true;
                    }
                }

                if (count == entries.Length)
                {
                    return false;
                }

                entries[(head + count) % entries.Length] = item;
                count++;
                return true;
            }
        }

        public bool TryEnqueueFront(T item)
        {
            lock (syncRoot)
            {
                if (count == entries.Length)
                {
                    return false;
                }

                head = (head + entries.Length - 1) % entries.Length;
                entries[head] = item;
                count++;
                return true;
            }
        }

        public bool TryEnqueuePairFront(T first, T second)
        {
            lock (syncRoot)
            {
                if (entries.Length - count < 2)
                {
                    return false;
                }

                head = (head + entries.Length - 2) % entries.Length;
                entries[head] = first;
                entries[(head + 1) % entries.Length] = second;
                count += 2;
                return true;
            }
        }

        public bool TryDequeue(out T item)
        {
            lock (syncRoot)
            {
                if (count == 0)
                {
                    item = default;
                    return false;
                }

                item = entries[head];
                entries[head] = default;
                head = (head + 1) % entries.Length;
                count--;
                return true;
            }
        }

        public bool TryDequeuePair(Predicate<T> predicate, out T first,
            out T second)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }

            lock (syncRoot)
            {
                if (count < 2)
                {
                    first = default;
                    second = default;
                    return false;
                }

                int secondIndex = (head + 1) % entries.Length;
                if (!predicate(entries[head]) ||
                    !predicate(entries[secondIndex]))
                {
                    first = default;
                    second = default;
                    return false;
                }

                first = entries[head];
                second = entries[secondIndex];
                entries[head] = default;
                entries[secondIndex] = default;
                head = (head + 2) % entries.Length;
                count -= 2;
                return true;
            }
        }

        public bool TryPeek(out T item)
        {
            lock (syncRoot)
            {
                if (count == 0)
                {
                    item = default;
                    return false;
                }

                item = entries[head];
                return true;
            }
        }

        public List<T> Clear()
        {
            lock (syncRoot)
            {
                var removed = new List<T>(count);
                while (count != 0)
                {
                    removed.Add(entries[head]);
                    entries[head] = default;
                    head = (head + 1) % entries.Length;
                    count--;
                }

                head = 0;
                return removed;
            }
        }

        public List<T> RemoveWhere(Predicate<T> predicate)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }

            lock (syncRoot)
            {
                var removed = new List<T>();
                if (count == 0)
                {
                    return removed;
                }

                var retained = new List<T>(count);
                for (int index = 0; index < count; index++)
                {
                    T item = entries[(head + index) % entries.Length];
                    if (predicate(item))
                    {
                        removed.Add(item);
                    }
                    else
                    {
                        retained.Add(item);
                    }
                }

                Array.Clear(entries, 0, entries.Length);
                head = 0;
                count = retained.Count;
                for (int index = 0; index < retained.Count; index++)
                {
                    entries[index] = retained[index];
                }

                return removed;
            }
        }

        /// <summary>
        /// Replaces every matching item with one ordered group as a single
        /// capacity transaction. If the group cannot fit after the prospective
        /// removals, the FIFO is left byte-for-byte and order-for-order intact.
        /// </summary>
        public bool TryReplaceWhereWithGroup(Predicate<T> predicate,
            IReadOnlyList<T> replacements)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }
            if (replacements == null)
            {
                throw new ArgumentNullException(nameof(replacements));
            }

            lock (syncRoot)
            {
                int removable = 0;
                for (int index = 0; index < count; index++)
                {
                    if (predicate(entries[(head + index) % entries.Length]))
                    {
                        removable++;
                    }
                }

                if (count - removable + replacements.Count > entries.Length)
                {
                    return false;
                }

                var retained = new List<T>(count - removable);
                for (int index = 0; index < count; index++)
                {
                    T item = entries[(head + index) % entries.Length];
                    if (!predicate(item))
                    {
                        retained.Add(item);
                    }
                }

                Array.Clear(entries, 0, entries.Length);
                head = 0;
                count = retained.Count + replacements.Count;
                for (int index = 0; index < retained.Count; index++)
                {
                    entries[index] = retained[index];
                }
                for (int index = 0; index < replacements.Count; index++)
                {
                    entries[retained.Count + index] = replacements[index];
                }

                return true;
            }
        }

        public int CountLeading(Predicate<T> predicate)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }

            lock (syncRoot)
            {
                int matches = 0;
                for (int index = 0; index < count; index++)
                {
                    if (!predicate(entries[(head + index) % entries.Length]))
                    {
                        break;
                    }

                    matches++;
                }

                return matches;
            }
        }
    }

    /// <summary>
    /// Pure scheduler for the native transport's observed native Windows presentation
    /// lattice. The reference presents fifteen 10 ms Opus generations across
    /// sixteen 10 ms host ticks: fourteen 10 ms intervals followed by one
    /// 20 ms interval. A late host wake re-anchors the next interval instead of
    /// replaying missed deadlines as a catch-up burst.
    /// </summary>
    internal sealed class DualSenseV5NativePresentationScheduler
    {
        internal const int HostTickNumerator = 1;
        internal const int HostTickDenominator = 100;
        internal const int HostTicksPerCycle = 16;
        internal const int ReportsPerCycle = 15;
        internal const double MinimumRateRatio = 0.995;
        internal const double MaximumRateRatio = 1.005;

        private readonly long clockFrequency;
        private long wholeTickTicks;
        private double fractionalTickTicks;
        private double fractionalTickAccumulator;
        private double rateRatio;
        private long nextDeadlineQpc;
        private int reportsIntoCycle;
        private bool started;

        public DualSenseV5NativePresentationScheduler(
            long clockFrequency)
        {
            if (clockFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clockFrequency));
            }

            this.clockFrequency = clockFrequency;
            SetRateRatio(1.0);
        }

        public bool IsStarted => started;

        public double RateRatio => rateRatio;

        public long NextDeadlineQpc => started ? nextDeadlineQpc :
            throw new InvalidOperationException(
                "The V5 presentation clock has not started.");

        public void Start(long nowQpc)
        {
            fractionalTickAccumulator = 0.0;
            reportsIntoCycle = 0;
            nextDeadlineQpc = nowQpc;
            started = true;
        }

        public void Reset()
        {
            fractionalTickAccumulator = 0.0;
            reportsIntoCycle = 0;
            nextDeadlineQpc = 0;
            started = false;
        }

        public void SetRateRatio(double controllerClockRatio)
        {
            if (!double.IsFinite(controllerClockRatio) ||
                controllerClockRatio < MinimumRateRatio ||
                controllerClockRatio > MaximumRateRatio)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(controllerClockRatio));
            }

            if (controllerClockRatio == rateRatio && wholeTickTicks != 0)
            {
                return;
            }

            double exactTickTicks = clockFrequency *
                (double)HostTickNumerator /
                (HostTickDenominator * controllerClockRatio);
            wholeTickTicks = (long)Math.Floor(exactTickTicks);
            fractionalTickTicks = exactTickTicks - wholeTickTicks;
            rateRatio = controllerClockRatio;
            // Keep the accumulated sub-QPC phase when a long-window clock
            // estimate changes. Resetting it would insert a tiny discontinuity
            // into an otherwise continuous V5 host lattice.
        }

        public long AdvanceAfterSend(long presentationQpc)
        {
            if (!started)
            {
                throw new InvalidOperationException(
                    "The V5 presentation clock has not started.");
            }

            reportsIntoCycle++;
            int hostTicks = 1;
            if (reportsIntoCycle == ReportsPerCycle)
            {
                reportsIntoCycle = 0;
                hostTicks = HostTicksPerCycle - ReportsPerCycle + 1;
            }

            long interval = 0;
            for (int tick = 0; tick < hostTicks; tick++)
            {
                interval = checked(interval + NextHostTickTicks());
            }

            long phaseDeadline = checked(nextDeadlineQpc + interval);
            nextDeadlineQpc = phaseDeadline > presentationQpc ?
                phaseDeadline : checked(presentationQpc + interval);
            return nextDeadlineQpc;
        }

        private long NextHostTickTicks()
        {
            long interval = wholeTickTicks;
            fractionalTickAccumulator += fractionalTickTicks;
            if (fractionalTickAccumulator >= 1.0)
            {
                long extraTicks = (long)fractionalTickAccumulator;
                interval += extraTicks;
                fractionalTickAccumulator -= extraTicks;
            }

            return Math.Max(1, interval);
        }
    }

    /// <summary>
    /// Pure rational-clock scheduler. Normal presentation jitter remains locked
    /// to the exact rational phase, with catch-up compression capped at 1 ms.
    /// Larger lateness re-anchors a full cadence after the presentation boundary
    /// so a delayed report can never cause a burst.
    /// </summary>
    internal sealed class DualSenseBluetoothAudioPacerScheduler
    {
        internal const int CadenceNumerator = 32;
        internal const int CadenceDenominator = 3000;
        internal const int InputReportsPerSecond = 800;
        internal const int InputPhaseOffsetMicroseconds = 350;
        internal const int MaximumInputPhaseCorrectionMicroseconds = 250;
        internal static readonly double ControllerReserveCadenceRatio = 1.0;
        internal const int ControllerReserveTransferIntervalMicroseconds =
            5_000;
        internal const double MinimumRateRatio = 0.995;
        internal const double MaximumRateRatio = 1.005;

        private readonly long clockFrequency;
        private long wholeTicks;
        private long remainderTicks;
        private readonly long maximumCatchUpTicks;
        private long remainderAccumulator;
        private double fractionalRemainderTicks;
        private double fractionalRemainderAccumulator;
        private bool nominalRatio;
        private double rateRatio;
        private int controllerLinkWarmupIntervalsRemaining;
        private int controllerReserveTransferIntervalsRemaining;
        private long nextDeadlineQpc;
        private long inputPhaseReferenceQpc;
        private bool started;

        public DualSenseBluetoothAudioPacerScheduler(long clockFrequency)
        {
            if (clockFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clockFrequency));
            }
            this.clockFrequency = clockFrequency;
            maximumCatchUpTicks = Math.Max(1, clockFrequency / 1000);
            SetRateRatio(1.0);
        }

        public bool IsStarted => started;
        public long NextDeadlineQpc => started ? nextDeadlineQpc :
            throw new InvalidOperationException("The pacer clock has not started.");
        public long PresentationDeadlineQpc => GetPresentationDeadlineQpc();
        public double RateRatio => rateRatio;

        public void SetInputPhaseReference(long inputArrivalQpc)
        {
            inputPhaseReferenceQpc = Math.Max(0, inputArrivalQpc);
        }

        public void SetRateRatio(double controllerClockRatio)
        {
            if (!double.IsFinite(controllerClockRatio) ||
                controllerClockRatio < MinimumRateRatio ||
                controllerClockRatio > MaximumRateRatio)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(controllerClockRatio));
            }

            if (controllerClockRatio == rateRatio && wholeTicks != 0)
            {
                return;
            }

            rateRatio = controllerClockRatio;
            nominalRatio = Math.Abs(controllerClockRatio - 1.0) < 1.0e-12;
            if (nominalRatio)
            {
                long scaled = checked(clockFrequency * CadenceNumerator);
                wholeTicks = scaled / CadenceDenominator;
                remainderTicks = scaled % CadenceDenominator;
                fractionalRemainderTicks = 0.0;
            }
            else
            {
                double exactTicks = clockFrequency *
                    (double)CadenceNumerator /
                    (CadenceDenominator * controllerClockRatio);
                wholeTicks = (long)Math.Floor(exactTicks);
                remainderTicks = 0;
                fractionalRemainderTicks = exactTicks - wholeTicks;
            }

            remainderAccumulator = 0;
            fractionalRemainderAccumulator = 0.0;
        }

        public void Start(long nowQpc)
        {
            Start(nowQpc, 0);
        }

        public void Start(long nowQpc,
            int controllerReserveTransferIntervals)
        {
            Start(nowQpc, 0, controllerReserveTransferIntervals);
        }

        public void Start(long nowQpc,
            int controllerLinkWarmupIntervals,
            int controllerReserveTransferIntervals)
        {
            if (controllerLinkWarmupIntervals < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(controllerLinkWarmupIntervals));
            }
            if (controllerReserveTransferIntervals < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(controllerReserveTransferIntervals));
            }

            remainderAccumulator = 0;
            fractionalRemainderAccumulator = 0.0;
            controllerLinkWarmupIntervalsRemaining =
                controllerLinkWarmupIntervals;
            controllerReserveTransferIntervalsRemaining =
                controllerReserveTransferIntervals;
            nextDeadlineQpc = nowQpc;
            started = true;
        }

        public void Reset()
        {
            remainderAccumulator = 0;
            fractionalRemainderAccumulator = 0.0;
            controllerLinkWarmupIntervalsRemaining = 0;
            controllerReserveTransferIntervalsRemaining = 0;
            nextDeadlineQpc = 0;
            started = false;
        }

        public long AdvanceAfterSend(long presentationQpc)
        {
            if (!started)
            {
                throw new InvalidOperationException("The pacer clock has not started.");
            }

            long interval = NextIntervalTicks();
            long phaseDeadline = checked(nextDeadlineQpc + interval);
            long minimumPhaseGap = Math.Max(1,
                interval - maximumCatchUpTicks);
            long phaseGap = phaseDeadline - presentationQpc;
            nextDeadlineQpc = phaseGap >= minimumPhaseGap ?
                phaseDeadline : checked(presentationQpc + interval);
            return nextDeadlineQpc;
        }

        private long GetPresentationDeadlineQpc()
        {
            if (!started)
            {
                throw new InvalidOperationException(
                    "The pacer clock has not started.");
            }

            long inputReference = inputPhaseReferenceQpc;
            if (inputReference <= 0)
            {
                return nextDeadlineQpc;
            }

            long inputPeriodTicks = Math.Max(1,
                clockFrequency / InputReportsPerSecond);
            long phaseOffsetTicks = checked(clockFrequency *
                InputPhaseOffsetMicroseconds / 1_000_000);
            long correctionLimitTicks = Math.Max(1, checked(clockFrequency *
                MaximumInputPhaseCorrectionMicroseconds / 1_000_000));
            long phaseOrigin = inputReference + phaseOffsetTicks;
            long delta = nextDeadlineQpc - phaseOrigin;
            long periods = delta >= 0 ?
                (delta + inputPeriodTicks / 2) / inputPeriodTicks :
                -((-delta + inputPeriodTicks / 2) / inputPeriodTicks);
            long nearestInputPhase = phaseOrigin +
                periods * inputPeriodTicks;
            long correction = nearestInputPhase - nextDeadlineQpc;
            if (correction > correctionLimitTicks)
            {
                correction = correctionLimitTicks;
            }
            else if (correction < -correctionLimitTicks)
            {
                correction = -correctionLimitTicks;
            }

            // Keep the rational audio clock continuous, but bias each
            // presentation a fraction of a millisecond toward the controller's
            // own 800 Hz input phase. The cap avoids the old hard re-snap that
            // quantized 10.667 ms audio cadence into audible 10/11.25 ms jitter.
            return nextDeadlineQpc + correction;
        }

        private long NextIntervalTicks()
        {
            if (controllerLinkWarmupIntervalsRemaining > 0)
            {
                controllerLinkWarmupIntervalsRemaining--;
                return NextNominalIntervalTicks();
            }

            if (controllerReserveTransferIntervalsRemaining > 0)
            {
                controllerReserveTransferIntervalsRemaining--;
                return checked(clockFrequency *
                    ControllerReserveTransferIntervalMicroseconds /
                    1_000_000);
            }

            return NextNominalIntervalTicks();
        }

        private long NextNominalIntervalTicks()
        {
            long interval = wholeTicks;
            if (nominalRatio)
            {
                remainderAccumulator += remainderTicks;
                if (remainderAccumulator >= CadenceDenominator)
                {
                    long extraTicks = remainderAccumulator /
                        CadenceDenominator;
                    interval += extraTicks;
                    remainderAccumulator -= extraTicks *
                        CadenceDenominator;
                }
            }
            else
            {
                fractionalRemainderAccumulator += fractionalRemainderTicks;
                if (fractionalRemainderAccumulator >= 1.0)
                {
                    long extraTicks = (long)fractionalRemainderAccumulator;
                    interval += extraTicks;
                    fractionalRemainderAccumulator -= extraTicks;
                }
            }

            if (ControllerReserveCadenceRatio < 1.0)
            {
                interval = Math.Max(1,
                    (long)Math.Round(interval * ControllerReserveCadenceRatio));
            }

            return Math.Max(1, interval);
        }
    }

    /// <summary>
    /// Composes game-authored validity updates that may be coalesced before
    /// the helper reaches its next physical media slot. A newer report that
    /// does not own a field cannot erase an earlier pending update; a newer
    /// valid value (including zero rumble) replaces it.
    /// </summary>
    internal static class DualSensePendingGameStateComposer
    {
        internal const int StateLength =
            DualSenseBluetoothPhysicalOutputSequence.
                ControllerStatePayloadLength;

        internal static void Merge(byte[] destination, byte[] source,
            int sourceOffset)
        {
            if (destination == null || destination.Length != StateLength)
            {
                throw new ArgumentException(
                    $"Pending state must be exactly {StateLength} bytes.",
                    nameof(destination));
            }

            if (source == null || sourceOffset < 0 ||
                sourceOffset + StateLength > source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceOffset));
            }

            Span<byte> previous = stackalloc byte[StateLength];
            destination.AsSpan().CopyTo(previous);
            source.AsSpan(sourceOffset, StateLength).CopyTo(destination);

            // Rumble validity is one continuous two-motor contract. A later
            // valid zero is the stop command and must replace the prior value;
            // an unrelated report must not discard it before presentation.
            RestoreFieldWhenMissing(destination, previous, 0, 0x03, 2, 2);
            RestoreFieldWhenMissing(destination, previous, 0, 0x04, 10, 11);
            RestoreFieldWhenMissing(destination, previous, 0, 0x08, 21, 11);

            RestoreFieldWhenMissing(destination, previous, 1, 0x20, 39, 1);
            RestoreFieldWhenMissing(destination, previous, 1, 0x40, 36, 1);

            bool incomingRelease = (destination[1] & 0x08) != 0;
            bool incomingVisibleLedState = (destination[1] & 0x14) != 0;
            if (incomingRelease)
            {
                // A newer release supersedes any unpresented visible LED
                // update from the same coalescing window.
                destination[1] &= unchecked((byte)~0x14);
            }
            else
            {
                RestoreFieldWhenMissing(destination, previous, 1, 0x04,
                    44, 3);
                RestoreFieldWhenMissing(destination, previous, 1, 0x10,
                    43, 1);
                if (incomingVisibleLedState)
                {
                    destination[1] &= unchecked((byte)~0x08);
                }
                else if ((previous[1] & 0x08) != 0)
                {
                    destination[1] |= 0x08;
                }
            }

            RestoreFieldWhenMissing(destination, previous, 38, 0x01, 42, 1);
            RestoreFieldWhenMissing(destination, previous, 38, 0x02, 41, 1);
        }

        private static void RestoreFieldWhenMissing(byte[] destination,
            ReadOnlySpan<byte> previous, int flagOffset, byte validityMask,
            int payloadOffset, int payloadLength)
        {
            if ((destination[flagOffset] & validityMask) != 0 ||
                (previous[flagOffset] & validityMask) == 0)
            {
                return;
            }

            destination[flagOffset] |=
                (byte)(previous[flagOffset] & validityMask);
            previous.Slice(payloadOffset, payloadLength).CopyTo(
                destination.AsSpan(payloadOffset, payloadLength));
        }
    }

    /// <summary>
    /// Pure report merger used immediately before a report is presented.
    /// </summary>
    internal static class DualSenseBluetoothAudioReportPatcher
    {
        internal const int ReportLength =
            DualSenseBluetoothAudioPacer.ReportLength;
        private const int CrcLength = sizeof(uint);
        private const int HapticsDataOffset = 78;
        private const int HapticsDataLength = 64;

        /// <summary>
        /// Merges current controller state into one queued media generation.
        /// The queued report's haptics block and matching expiry remain
        /// inseparable from that generation. Replacing those bytes with the
        /// newest template collapses brief ordered effects whenever more than
        /// one source generation is waiting in the host reservoir.
        /// </summary>
        public static void PatchForPresentation(byte[] queuedReport,
            long queuedHapticsExpiryQpc, byte[] latestTemplate,
            long latestTemplateHapticsExpiryQpc, long nowQpc)
        {
            long effectiveExpiryQpc = queuedHapticsExpiryQpc;
            PatchForPresentation(queuedReport, latestTemplate,
                effectiveExpiryQpc, nowQpc);
        }

        public static void PatchForPresentation(byte[] queuedReport,
            byte[] latestTemplate, long hapticsExpiryQpc, long nowQpc)
        {
            if (queuedReport == null || queuedReport.Length != ReportLength)
            {
                throw new ArgumentException(
                    $"Queued report must be exactly {ReportLength} bytes.",
                    nameof(queuedReport));
            }

            if (latestTemplate != null)
            {
                if (latestTemplate.Length != ReportLength)
                {
                    throw new ArgumentException(
                        $"Template must be exactly {ReportLength} bytes.",
                        nameof(latestTemplate));
                }

                // Preserve queued byte 1 (Sony sequence), bytes 5-9 (speaker
                // buffer depths), byte 10 (packet counter), bytes 78-141 (the
                // ordered 0x92 haptics generation), and bytes 142-343 (speaker
                // TLV + 200-byte Opus frame). A live control-only template uses
                // the low-latency depth of 16; copying that over a queued
                // speaker report would replace its independent audio reserve.
                // Controller state remains newest-wins, but media never does.
                Buffer.BlockCopy(latestTemplate, 2, queuedReport, 2, 3);
                Buffer.BlockCopy(latestTemplate, 11, queuedReport, 11,
                    HapticsDataOffset - 11);
            }

            if (hapticsExpiryQpc <= nowQpc)
            {
                Array.Clear(queuedReport, HapticsDataOffset,
                    HapticsDataLength);
            }

            WriteSonyCrc(queuedReport);
        }

        internal static void ApplyControllerStateForPresentation(
            byte[] report, byte[] statePayload)
        {
            if (report == null || report.Length != ReportLength)
            {
                throw new ArgumentException(
                    $"Report must be exactly {ReportLength} bytes.",
                    nameof(report));
            }

            if (statePayload == null || statePayload.Length !=
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStatePayloadLength)
            {
                throw new ArgumentException(
                    "Controller state must be exactly 47 bytes.",
                    nameof(statePayload));
            }

            Buffer.BlockCopy(statePayload, 0, report,
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStateSourceOffset,
                statePayload.Length);
        }

        public static uint ComputeSonyCrc(byte[] report, int length)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            if (length < 0 || length > report.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            uint crc = ~0xEADA2D49u;
            for (int index = 0; index < length; index++)
            {
                crc ^= report[index];
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^
                        ((crc & 1) != 0 ? 0xEDB88320u : 0u);
                }
            }

            return ~crc;
        }

        private static void WriteSonyCrc(byte[] report)
        {
            uint crc = ComputeSonyCrc(report, ReportLength - CrcLength);
            BinaryPrimitives.WriteUInt32LittleEndian(
                report.AsSpan(ReportLength - CrcLength, CrcLength), crc);
        }
    }

    /// <summary>
    /// Owns the sequence numbers that the controller observes on the physical
    /// Bluetooth output lane. Audio reports keep one four-bit sequence while
    /// native 0x32 microphone transitions keep their own. The byte-10 media
    /// counter is shared by both: a 0x32 occupies one media interval even
    /// though it contains no Opus payload. This is the exact ordering observed
    /// from V5 over Windows HidBth.
    /// </summary>
    internal sealed class DualSenseBluetoothPhysicalOutputSequence
    {
        private const int CrcLength = sizeof(uint);
        internal const int MicrophoneStatusReportLength = 142;
        internal const int ControllerStateReportLength = 78;
        internal const int ControllerStatePayloadLength = 47;
        internal const int ControllerStateSourceOffset = 13;
        private const byte NativeAudioBufferLength = 0x80;
        private bool initialized;
        private bool mediaPacketSequenceInitialized;
        private byte nextReportSequence;
        private byte nextMicrophoneStatusSequence;
        private byte mediaPacketSequence;
        private byte preparedMediaPacketSequence;

        internal byte NextReportSequence => nextReportSequence;
        internal byte NextMicrophoneStatusSequence =>
            nextMicrophoneStatusSequence;
        internal byte NextControllerStateSequence =>
            nextReportSequence;
        internal byte MediaPacketSequence => mediaPacketSequence;

        internal void PrepareControl(byte[] report)
        {
            ValidateSource(report, nameof(report));
            EnsureInitialized(report);

            report[1] = (byte)((nextReportSequence & 0x0F) << 4);
            report[10] = mediaPacketSequence;
            WriteSonyCrc(report);
        }

        internal void PreparePairedAudio(byte[] first, byte[] second,
            byte[] destination)
        {
            ValidateSource(first, nameof(first));
            ValidateSource(second, nameof(second));
            EnsureInitialized(first);
            PrepareMediaCounter(second[10], 2);

            DualSenseBluetoothPairedAudioReportBuilder.Build(first, second,
                nextReportSequence, preparedMediaPacketSequence, destination);
        }

        internal void PrepareSingleAudio(byte[] report)
        {
            ValidateSource(report, nameof(report));
            if (!DualSenseBluetoothAudioPacer.IsSpeakerAudioReport(report))
            {
                throw new ArgumentException(
                    "Source must be a complete 398-byte 0x36 speaker report.",
                    nameof(report));
            }

            EnsureInitialized(report);
            PrepareMediaCounter(report[10], 1);
            report[1] = (byte)((nextReportSequence & 0x0F) << 4);
            report[10] = preparedMediaPacketSequence;
            WriteSonyCrc(report);
        }

        internal void PrepareNativeAudio(byte[] report)
        {
            ValidateSource(report, nameof(report));
            if (!DualSenseBluetoothAudioPacer.IsSpeakerAudioReport(report))
            {
                throw new ArgumentException(
                    "Source must be a complete 398-byte 0x36 speaker report.",
                    nameof(report));
            }

            for (int index = 5; index <= 9; index++)
            {
                report[index] = NativeAudioBufferLength;
            }
            PrepareSingleAudio(report);
        }

        internal void PrepareFullDuplexAudio(byte[] report)
        {
            ValidateSource(report, nameof(report));
            if (!DualSenseBluetoothAudioPacer.
                    RequiresFullDuplexAudioReport(report))
            {
                throw new ArgumentException(
                    "Source must be a microphone-enabled 0x36 speaker report.",
                    nameof(report));
            }

            // the native transport's clean Windows duplex stream keeps all five native
            // 0x36 media-lane depths at 0x80 for both FE speaker-only and FF
            // microphone-enabled playback.
            for (int index = 5; index <= 9; index++)
            {
                report[index] = NativeAudioBufferLength;
            }

            PrepareSingleAudio(report);
        }

        internal void PrepareMeasuredTransportAudio(byte[] source, byte[] destination)
        {
            ValidateSource(source, nameof(source));
            if (!DualSenseBluetoothAudioPacer.IsSpeakerAudioReport(source))
            {
                throw new ArgumentException(
                    "Source must be a complete 398-byte 0x36 speaker report.",
                    nameof(source));
            }

            EnsureInitialized(source);
            PrepareMediaCounter(source[10], 1);
            DualSenseBluetoothMeasuredTransportAudioReportBuilder.Build(source,
                nextReportSequence, preparedMediaPacketSequence, destination);
        }

        internal void PrepareMeasuredTransportCombinedAudio(byte[] source,
            byte[] destination)
        {
            ValidateSource(source, nameof(source));
            if (!DualSenseBluetoothAudioPacer.IsSpeakerAudioReport(source))
            {
                throw new ArgumentException(
                    "Source must be a complete 398-byte 0x36 speaker report.",
                    nameof(source));
            }

            EnsureInitialized(source);
            PrepareMediaCounter(source[10], 1);
            DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder.Build(source,
                nextReportSequence, preparedMediaPacketSequence, destination);
        }

        internal void PrepareMicrophoneStatus(bool enabled,
            byte[] initializationReport, byte[] destination)
        {
            ValidateSource(initializationReport,
                nameof(initializationReport));
            if (destination == null ||
                destination.Length != MicrophoneStatusReportLength)
            {
                throw new ArgumentException(
                    $"Microphone status report must be exactly {MicrophoneStatusReportLength} bytes.",
                    nameof(destination));
            }

            EnsureInitialized(initializationReport);
            preparedMediaPacketSequence = unchecked(
                (byte)(mediaPacketSequence + 1));
            Array.Clear(destination, 0, destination.Length);
            destination[0] = 0x32;
            destination[1] = (byte)(
                (nextMicrophoneStatusSequence & 0x0F) << 4);
            destination[2] = 0x91;
            destination[3] = 0x07;
            destination[4] = enabled ? (byte)0xFF : (byte)0xFE;
            for (int index = 5; index <= 9; index++)
            {
                destination[index] = NativeAudioBufferLength;
            }
            destination[10] = preparedMediaPacketSequence;
            WriteSonyCrc(destination);
        }

        internal void CommitMicrophoneStatus()
        {
            if (!initialized)
            {
                throw new InvalidOperationException(
                    "A DualSense microphone status cannot be committed before it is prepared.");
            }

            nextMicrophoneStatusSequence = (byte)(
                (nextMicrophoneStatusSequence + 1) & 0x0F);
            mediaPacketSequence = preparedMediaPacketSequence;
            mediaPacketSequenceInitialized = true;
        }

        internal void PrepareControllerState(byte[] statePayload,
            byte[] initializationReport, byte[] destination)
        {
            ValidateSource(initializationReport,
                nameof(initializationReport));
            if (statePayload == null ||
                statePayload.Length != ControllerStatePayloadLength)
            {
                throw new ArgumentException(
                    $"Controller state payload must be exactly {ControllerStatePayloadLength} bytes.",
                    nameof(statePayload));
            }

            if (destination == null ||
                destination.Length != ControllerStateReportLength)
            {
                throw new ArgumentException(
                    $"Controller state report must be exactly {ControllerStateReportLength} bytes.",
                    nameof(destination));
            }

            EnsureInitialized(initializationReport);
            Array.Clear(destination, 0, destination.Length);
            destination[0] = 0x31;
            destination[1] = (byte)((nextReportSequence & 0x0F) << 4);
            destination[2] = 0x10;
            Buffer.BlockCopy(statePayload, 0, destination, 3,
                statePayload.Length);
            WriteSonyCrc(destination);
        }

        internal void Commit(bool audio)
        {
            if (!initialized)
            {
                throw new InvalidOperationException(
                    "A DualSense output sequence cannot be committed before it is prepared.");
            }

            nextReportSequence = (byte)((nextReportSequence + 1) & 0x0F);
            if (audio)
            {
                mediaPacketSequence = preparedMediaPacketSequence;
                mediaPacketSequenceInitialized = true;
            }
        }

        internal void CommitControllerState()
        {
            if (!initialized)
            {
                throw new InvalidOperationException(
                    "A DualSense controller state cannot be committed before it is prepared.");
            }

            nextReportSequence = (byte)((nextReportSequence + 1) & 0x0F);
        }

        private void EnsureInitialized(byte[] report)
        {
            if (initialized)
            {
                return;
            }

            nextReportSequence = (byte)(report[1] >> 4);
            nextMicrophoneStatusSequence = nextReportSequence;
            mediaPacketSequence = report[10];
            initialized = true;
        }

        private void PrepareMediaCounter(byte firstSourceCounter, int step)
        {
            preparedMediaPacketSequence = mediaPacketSequenceInitialized ?
                unchecked((byte)(mediaPacketSequence + step)) :
                firstSourceCounter;
        }

        private static void ValidateSource(byte[] report, string parameter)
        {
            if (report == null ||
                report.Length != DualSenseBluetoothAudioPacer.ReportLength ||
                report[0] != 0x36)
            {
                throw new ArgumentException(
                    "Source report must be a 398-byte DualSense 0x36 report.",
                    parameter);
            }
        }

        private static void WriteSonyCrc(byte[] report)
        {
            uint crc = DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                report, report.Length - CrcLength);
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(
                report.Length - CrcLength, CrcLength), crc);
        }
    }

    /// <summary>
    /// Converts one logical combined 0x36 speaker generation into the measured transport's
    /// compact physical 0x35 speaker report. The logical report remains the
    /// source of the Opus payload and media counter; controller state and
    /// control-only reports continue to use the normal 0x36 path.
    /// </summary>
    internal static class DualSenseBluetoothMeasuredTransportAudioReportBuilder
    {
        internal const int ReportLength = 334;
        private const int SourceReportLength =
            DualSenseBluetoothAudioPacer.ReportLength;
        private const int CrcLength = sizeof(uint);
        private const int SourceSpeakerDataOffset = 144;
        private const int PlaybackSpeakerHeaderOffset = 11;
        private const int PlaybackSpeakerDataOffset = 13;
        private const int SpeakerFrameLength = 200;
        // CombinedReportReference exposes this Sony media-reserve field over the documented
        // 16..127 range. Live controller feedback (BT input byte 65) showed
        // that 64 ms is exhausted by real 63..88 ms HidBth delivery droughts
        // even though our 10.667 ms submissions remain perfectly sequential.
        // Keep enough controller-side playout cushion for the measured tail;
        // this does not enlarge the host reservoir or permit catch-up bursts.
        private const byte ControllerBufferLength = 96;

        public static void Build(byte[] source, byte reportSequence,
            byte packetSequence, byte[] destination)
        {
            ValidateSource(source);
            if (destination == null || destination.Length != ReportLength)
            {
                throw new ArgumentException(
                    $"Destination report must be exactly {ReportLength} bytes.",
                    nameof(destination));
            }

            Array.Clear(destination, 0, destination.Length);
            destination[0] = 0x35;
            destination[1] = (byte)((reportSequence & 0x0F) << 4);
            destination[2] = 0x91;
            bool microphoneEnabled = (source[4] & 0x01) != 0;
            int speakerHeaderOffset;
            int speakerDataOffset;
            if (microphoneEnabled)
            {
                // Length-six/7F is the paired 0x39 contract. Compact 0x35
                // keeps its seventh audio-control field and media offsets;
                // enable microphone input by setting only bit zero of the
                // proven FE playback mask.
                destination[3] = 7;
                destination[4] = 0xFF;
                for (int index = 5; index <= 9; index++)
                {
                    destination[index] = ControllerBufferLength;
                }
                destination[10] = packetSequence;
                speakerHeaderOffset = PlaybackSpeakerHeaderOffset;
                speakerDataOffset = PlaybackSpeakerDataOffset;
            }
            else
            {
                destination[3] = 7;
                destination[4] = 0xFE;
                for (int index = 5; index <= 9; index++)
                {
                    destination[index] = ControllerBufferLength;
                }
                destination[10] = packetSequence;
                speakerHeaderOffset = PlaybackSpeakerHeaderOffset;
                speakerDataOffset = PlaybackSpeakerDataOffset;
            }
            // Preserve the logical media destination. 0x93 targets the
            // controller speaker; 0x96 targets the headset/AUX DAC. Rewriting
            // both as 0x93 makes Headset Only silently fall back to speaker.
            destination[speakerHeaderOffset] = source[142];
            destination[speakerHeaderOffset + 1] = SpeakerFrameLength;
            Buffer.BlockCopy(source, SourceSpeakerDataOffset, destination,
                speakerDataOffset, SpeakerFrameLength);

            uint crc = DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                destination, ReportLength - CrcLength);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(
                ReportLength - CrcLength, CrcLength), crc);
        }

        private static void ValidateSource(byte[] source)
        {
            if (source == null || source.Length != SourceReportLength ||
                source[0] != 0x36 ||
                !IsSupportedAudioPacketType(source[142]) ||
                source[143] != SpeakerFrameLength)
            {
                throw new ArgumentException(
                    "Source must be a complete 398-byte 0x36 speaker or AUX report.",
                    nameof(source));
            }
        }

        private static bool IsSupportedAudioPacketType(byte packetType)
        {
            int type = packetType & 0x3F;
            return type == 0x13 || type == 0x16;
        }
    }

    /// <summary>
    /// Carries one native haptics block and one Opus speaker frame in the same
    /// compact Sony subpacket container. This preserves the measured transport's proven
    /// 334-byte, one-write-per-generation cadence while keeping haptics and
    /// audio atomic instead of competing for separate Bluetooth transactions.
    /// </summary>
    internal static class DualSenseBluetoothMeasuredTransportCombinedAudioReportBuilder
    {
        internal const int ReportLength =
            DualSenseBluetoothMeasuredTransportAudioReportBuilder.ReportLength;
        private const int SourceReportLength =
            DualSenseBluetoothAudioPacer.ReportLength;
        private const int CrcLength = sizeof(uint);
        private const int SourceHapticsOffset = 78;
        private const int HapticsLength = 64;
        private const int SourceSpeakerDataOffset = 144;
        private const int GoldenHapticsHeaderOffset = 11;
        private const int GoldenHapticsDataOffset = 13;
        private const int GoldenSpeakerHeaderOffset = 77;
        private const int GoldenSpeakerDataOffset = 79;
        private const int SpeakerFrameLength = 200;
        // Match the duplex reserve used by the speaker-only compact builder.
        // The controller reports the resulting live reserve in BT input byte
        // 65, allowing hardware validation instead of timing speculation.
        private const byte ControllerBufferLength = 96;

        internal static void Build(byte[] source, byte reportSequence,
            byte packetSequence, byte[] destination)
        {
            ValidateSource(source);
            if (destination == null || destination.Length != ReportLength)
            {
                throw new ArgumentException(
                    $"Destination report must be exactly {ReportLength} bytes.",
                    nameof(destination));
            }

            Array.Clear(destination, 0, destination.Length);
            destination[0] = 0x35;
            destination[1] = (byte)((reportSequence & 0x0F) << 4);
            destination[2] = 0x91;
            bool microphoneEnabled = (source[4] & 0x01) != 0;
            int hapticsHeaderOffset;
            int hapticsDataOffset;
            int speakerHeaderOffset;
            int speakerDataOffset;
            if (microphoneEnabled)
            {
                destination[3] = 7;
                destination[4] = 0xFF;
                for (int index = 5; index <= 9; index++)
                {
                    destination[index] = ControllerBufferLength;
                }
                destination[10] = packetSequence;
                hapticsHeaderOffset = GoldenHapticsHeaderOffset;
                hapticsDataOffset = GoldenHapticsDataOffset;
                speakerHeaderOffset = GoldenSpeakerHeaderOffset;
                speakerDataOffset = GoldenSpeakerDataOffset;
            }
            else
            {
                destination[3] = 7;
                destination[4] = 0xFE;
                for (int index = 5; index <= 9; index++)
                {
                    destination[index] = ControllerBufferLength;
                }
                destination[10] = packetSequence;
                hapticsHeaderOffset = GoldenHapticsHeaderOffset;
                hapticsDataOffset = GoldenHapticsDataOffset;
                speakerHeaderOffset = GoldenSpeakerHeaderOffset;
                speakerDataOffset = GoldenSpeakerDataOffset;
            }
            destination[hapticsHeaderOffset] = 0x92;
            destination[hapticsHeaderOffset + 1] = HapticsLength;
            Buffer.BlockCopy(source, SourceHapticsOffset, destination,
                hapticsDataOffset, HapticsLength);
            destination[speakerHeaderOffset] = source[142];
            destination[speakerHeaderOffset + 1] = SpeakerFrameLength;
            Buffer.BlockCopy(source, SourceSpeakerDataOffset, destination,
                speakerDataOffset, SpeakerFrameLength);

            uint crc = DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                destination, ReportLength - CrcLength);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(
                ReportLength - CrcLength, CrcLength), crc);
        }

        private static void ValidateSource(byte[] source)
        {
            if (source == null || source.Length != SourceReportLength ||
                source[0] != 0x36 ||
                (source[76] & 0x3F) != 0x12 ||
                source[77] != HapticsLength ||
                !IsSupportedAudioPacketType(source[142]) ||
                source[143] != SpeakerFrameLength)
            {
                throw new ArgumentException(
                    "Source must be a complete 398-byte 0x36 haptics and speaker or AUX report.",
                    nameof(source));
            }
        }

        private static bool IsSupportedAudioPacketType(byte packetType)
        {
            int type = packetType & 0x3F;
            return type == 0x13 || type == 0x16;
        }
    }

    /// <summary>
    /// Packs two sequential 0x36 audio snapshots into Sony's lower-transaction
    /// 0x39 form. The controller receives the same two 64-byte haptics blocks
    /// and two 200-byte Opus frames, but one L2CAP/HID transaction replaces
    /// two independently fragmented writes.
    /// </summary>
    internal static class DualSenseBluetoothPairedAudioReportBuilder
    {
        internal const int ReportLength = 547;
        private const int SourceReportLength =
            DualSenseBluetoothAudioPacer.ReportLength;
        private const int CrcLength = sizeof(uint);

        public static void Build(byte[] first, byte[] second,
            byte reportSequence, byte packetSequence, byte[] destination)
        {
            ValidateSource(first, nameof(first));
            ValidateSource(second, nameof(second));
            if (destination == null || destination.Length != ReportLength)
            {
                throw new ArgumentException(
                    $"Destination report must be exactly {ReportLength} bytes.",
                    nameof(destination));
            }

            Array.Clear(destination, 0, destination.Length);
            destination[0] = 0x39;
            destination[1] = (byte)((reportSequence & 0x0F) << 4);
            destination[2] = 0x91;
            destination[3] = 6;
            destination[4] = (byte)(second[4] & 0x7F);
            destination[5] = second[5];
            destination[6] = second[6];
            destination[7] = second[7];
            destination[8] = second[8];
            destination[9] = packetSequence;

            destination[10] = (byte)((second[76] & 0x3F) | 0xC0);
            destination[11] = 64;
            Buffer.BlockCopy(first, 78, destination, 12, 64);
            Buffer.BlockCopy(second, 78, destination, 76, 64);

            destination[140] = (byte)((second[142] & 0x3F) | 0xC0);
            destination[141] = 200;
            Buffer.BlockCopy(first, 144, destination, 142, 200);
            Buffer.BlockCopy(second, 144, destination, 342, 200);

            uint crc = DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                destination, ReportLength - CrcLength);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(
                ReportLength - CrcLength, CrcLength), crc);
        }

        private static void ValidateSource(byte[] report, string parameter)
        {
            if (report == null || report.Length != SourceReportLength ||
                report[0] != 0x36 || report[143] != 200)
            {
                throw new ArgumentException(
                    "Source must be a complete 398-byte 0x36 speaker report.",
                    parameter);
            }
        }
    }
}
