using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        internal const int DoubleFrameReportLength = 547;
        internal const int HapticsReportLength = 142;
        internal const int StateReportLength = 78;
        internal const int MaximumReportLength = DoubleFrameReportLength;
        // This byte controls the DualSense's own presentation reservoir. 128
        // was smooth but accumulated roughly 2.5 seconds of speaker and
        // feedback latency on real hardware. Use the smallest value accepted
        // by the reference protocol; host-side pacing absorbs radio jitter.
        internal const byte DoubleFrameBufferLength = 16;
        internal const int PrimeReportCount = 8;
        internal const int DoubleFramePrimeReportCount = 2;
        internal const int HostReservoirCapacity = 64;

        private const string HelperArgument = "--dualsense-bt-audio-pacer-helper";
        private const int ProtocolVersion = 5;
        private const int PipeConnectTimeoutMilliseconds = 5000;
        private const int HelperReadyTimeoutMilliseconds = 5000;
        private const int HelperStopTimeoutMilliseconds = 3000;
        private const int HelperProcessExitTimeoutMilliseconds = 3000;
        private const uint HelperWriterReleaseTimeoutMilliseconds = 3000;
        private const uint HelperControlWriteTimeoutMilliseconds = 750;
        private const uint HelperPresentationOrderingTimeoutMilliseconds = 100;
        private const int OutboundCommandCapacity = HostReservoirCapacity + 16;
        private const int InitialEpoch = 1;
        private const uint DuplicateSameAccess = 0x00000002;

        private enum MessageKind : byte
        {
            Hello = 1,
            QueueReport = 2,
            UpdateTemplate = 3,
            Clear = 4,
            Stop = 5,
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
        private readonly NamedPipeServerStream pipe;
        private readonly Process helperProcess;
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
        private long latestTemplateHapticsExpiryQpc;
        private long nextReportId;
        private long acknowledgedReports;
        private long rejectedReports;
        private long presentedReports;
        private long lastPresentedTimestamp;
        private long maximumPresentationGapTicks;
        private long latePresentationCount;
        private long helperWriterCompletedWrites;
        private long helperWriterSlowCompletionCount;
        private long helperWriterMaximumCompletionTicks;
        private long helperWriterLateSubmissionCount;
        private long helperWriterMaximumSubmissionGapTicks;
        private long helperInvalidLayoutCount;
        private long helperInvalidCrcCount;
        private long helperReportSequenceDiscontinuityCount;
        private long helperPacketSequenceDiscontinuityCount;
        private long clearedReports;
        private long transportFaultReports;
        private int currentEpoch = InitialEpoch;
        private int stopping;
        private int disposed;
        private int cleanStopAcknowledged;
        private string lastError = string.Empty;

        private DualSenseBluetoothAudioPacer(NamedPipeServerStream pipe,
            Process helperProcess)
        {
            this.pipe = pipe;
            this.helperProcess = helperProcess;
            senderThread = new Thread(SenderLoop)
            {
                IsBackground = true,
                Name = "DualSense BT audio pacer IPC sender",
            };
            receiverThread = new Thread(ReceiverLoop)
            {
                IsBackground = true,
                Name = "DualSense BT audio pacer IPC receiver",
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
        public long HelperWriterCompletedWrites =>
            Interlocked.Read(ref helperWriterCompletedWrites);
        public long HelperWriterSlowCompletionCount =>
            Interlocked.Read(ref helperWriterSlowCompletionCount);
        public double HelperWriterMaximumCompletionMilliseconds =>
            Interlocked.Read(ref helperWriterMaximumCompletionTicks) * 1000.0 /
            Stopwatch.Frequency;
        public long HelperWriterLateSubmissionCount =>
            Interlocked.Read(ref helperWriterLateSubmissionCount);
        public double HelperWriterMaximumSubmissionGapMilliseconds =>
            Interlocked.Read(ref helperWriterMaximumSubmissionGapTicks) *
            1000.0 / Stopwatch.Frequency;
        public long HelperInvalidLayoutCount =>
            Interlocked.Read(ref helperInvalidLayoutCount);
        public long HelperInvalidCrcCount =>
            Interlocked.Read(ref helperInvalidCrcCount);
        public long HelperReportSequenceDiscontinuityCount =>
            Interlocked.Read(ref helperReportSequenceDiscontinuityCount);
        public long HelperPacketSequenceDiscontinuityCount =>
            Interlocked.Read(ref helperPacketSequenceDiscontinuityCount);
        public long ClearedReports => Interlocked.Read(ref clearedReports);
        public long TransportFaultReports =>
            Interlocked.Read(ref transportFaultReports);
        public bool IsReady => readyEvent.IsSet && !IsFaulted;
        public bool IsFaulted => !string.IsNullOrEmpty(LastError);
        public bool IsRunning => Volatile.Read(ref stopping) == 0 &&
            Volatile.Read(ref disposed) == 0 && !IsFaulted;

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
            if (!TryParseHelperArguments(args, out string pipeName,
                out Guid authenticationToken, out int parentProcessId))
            {
                return false;
            }

            RunHelper(pipeName, authenticationToken, parentProcessId);
            return true;
        }

        /// <summary>
        /// Starts a helper using the exact currently-running executable and
        /// duplicates the already-open overlapped HID handle into that process.
        /// No device path is reopened, so this also works with an exclusive
        /// physical-controller handle.
        /// </summary>
        public static bool TryStart(SafeFileHandle activeOverlappedHidHandle,
            byte[] initialTemplate,
            out DualSenseBluetoothAudioPacer pacer,
            out string error)
        {
            return TryStart(activeOverlappedHidHandle, initialTemplate,
                hapticsExpiryQpc: 0, out pacer, out error);
        }

        /// <summary>
        /// Starts the helper with an atomic initial control/haptics template.
        /// The absolute QPC expiry belongs to the haptics bytes in that
        /// template, not to any older queued audio report.
        /// </summary>
        public static bool TryStart(SafeFileHandle activeOverlappedHidHandle,
            byte[] initialTemplate, long hapticsExpiryQpc,
            out DualSenseBluetoothAudioPacer pacer,
            out string error)
        {
            pacer = null;
            error = string.Empty;

            if (activeOverlappedHidHandle == null ||
                activeOverlappedHidHandle.IsInvalid ||
                activeOverlappedHidHandle.IsClosed)
            {
                error = "The active overlapped DualSense HID handle is unavailable.";
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
            Guid authenticationToken = Guid.NewGuid();
            NamedPipeServerStream server = null;
            Process child = null;
            DualSenseBluetoothAudioPacer candidate = null;

            try
            {
                server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                    1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough |
                    PipeOptions.CurrentUserOnly,
                    4096, 4096);

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
                startInfo.ArgumentList.Add(pipeName);
                startInfo.ArgumentList.Add(authenticationToken.ToString("N"));
                startInfo.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString());

                child = Process.Start(startInfo);
                if (child == null)
                {
                    error = "Windows did not create the DualSense audio pacer process.";
                    server.Dispose();
                    return false;
                }

                Task connection = server.WaitForConnectionAsync();
                if (!connection.Wait(PipeConnectTimeoutMilliseconds))
                {
                    error = "Timed out waiting for the DualSense audio pacer pipe.";
                    server.Dispose();
                    TryTerminateUninitializedHelper(child);
                    return false;
                }

                connection.GetAwaiter().GetResult();
                if (!TryDuplicateHandleIntoChild(activeOverlappedHidHandle,
                    child, out IntPtr childHandle, out int duplicateError))
                {
                    error = "Could not duplicate the active DualSense HID handle " +
                        $"into the pacer. Win32Error={duplicateError}.";
                    server.Dispose();
                    TryTerminateUninitializedHelper(child);
                    return false;
                }

                candidate = new DualSenseBluetoothAudioPacer(server, child);
                candidate.latestTemplate = (byte[])initialTemplate.Clone();
                candidate.latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
                candidate.receiverThread.Start();
                candidate.SendHello(childHandle, authenticationToken);

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
                    server?.Dispose();
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
            if (!IsSupportedQueuedReport(report) || !IsRunning)
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
            return IsSpeakerAudioReport(report, report?.Length ?? 0);
        }

        internal static bool IsSpeakerAudioReport(byte[] report,
            int reportLength)
        {
            if (report == null || reportLength <= 0 ||
                reportLength > report.Length)
            {
                return false;
            }

            return reportLength == ReportLength && report[0] == 0x36 &&
                    report[142] == 0x93 && report[143] == 200 ||
                reportLength == DoubleFrameReportLength &&
                    report[0] == 0x39 && report[140] == 0xD3 &&
                    report[141] == 200;
        }

        internal static int GetSpeakerAudioFrameCount(byte[] report,
            int reportLength)
        {
            if (!IsSpeakerAudioReport(report, reportLength))
            {
                return 0;
            }

            return reportLength == DoubleFrameReportLength ? 2 : 1;
        }

        internal static bool RequiresPriorWriteCompletion(byte[] report,
            int reportLength)
        {
            return report != null &&
                reportLength == DoubleFrameReportLength &&
                reportLength <= report.Length && report[0] == 0x39;
        }

        private static bool IsSupportedQueuedReport(byte[] report)
        {
            if (report == null)
            {
                return false;
            }

            return report.Length == ReportLength && report[0] == 0x36 ||
                report.Length == DoubleFrameReportLength &&
                    report[0] == 0x39 ||
                report.Length == HapticsReportLength && report[0] == 0x32 ||
                report.Length == StateReportLength && report[0] == 0x31;
        }

        internal static bool CanPresentFromPrimeGate(bool primeRequired,
            int speakerReportCount, byte[] nextReport)
        {
            return CanPresentFromPrimeGate(primeRequired,
                speakerReportCount, nextReport, nextReport?.Length ?? 0);
        }

        internal static bool CanPresentFromPrimeGate(bool primeRequired,
            int speakerReportCount, byte[] nextReport, int reportLength)
        {
            int requiredReports = reportLength == DoubleFrameReportLength ?
                    DoubleFramePrimeReportCount : PrimeReportCount;
            return !primeRequired ||
                (nextReport != null &&
                    !IsSpeakerAudioReport(nextReport, reportLength)) ||
                speakerReportCount >= requiredReports;
        }

        internal static bool ShouldRequireAudioPrimeAfterPresentation(
            bool presentedControlReport, int remainingReportCount)
        {
            return presentedControlReport || remainingReportCount == 0;
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
                    // that the helper released its duplicated HID handle.
                    ClosePipeNoThrow();
                    EnsureHelperProcessExited();
                }
            }
        }

        private void SendHello(IntPtr childHandle, Guid authenticationToken)
        {
            byte[] payload = new byte[sizeof(int) + sizeof(long) + 16];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, sizeof(int)),
                ProtocolVersion);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(int),
                sizeof(long)), childHandle.ToInt64());
            authenticationToken.TryWriteBytes(payload.AsSpan(sizeof(int) +
                sizeof(long), 16));
            SendFrame(MessageKind.Hello, payload);
        }

        private void SenderLoop()
        {
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
            try
            {
                while (Volatile.Read(ref disposed) == 0)
                {
                    ReadFrame(pipe, out MessageKind kind, out byte[] payload);
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
            const int writerMetricCount = 5;
            const int reportMetricCount = 4;
            int expectedLength = sizeof(long) + sizeof(byte) + sizeof(long) +
                (writerMetricCount + reportMetricCount) * sizeof(long);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException("Invalid pacer acknowledgement length.");
            }

            long reportId = BinaryPrimitives.ReadInt64LittleEndian(
                payload.AsSpan(0, sizeof(long)));
            AcknowledgementDisposition disposition =
                (AcknowledgementDisposition)payload[sizeof(long)];
            long presentedTimestamp = BinaryPrimitives.ReadInt64LittleEndian(
                payload.AsSpan(sizeof(long) + sizeof(byte), sizeof(long)));
            int writerOffset = sizeof(long) + sizeof(byte) + sizeof(long);
            Interlocked.Exchange(ref helperWriterCompletedWrites,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    writerOffset, sizeof(long))));
            writerOffset += sizeof(long);
            Interlocked.Exchange(ref helperWriterSlowCompletionCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    writerOffset, sizeof(long))));
            writerOffset += sizeof(long);
            Interlocked.Exchange(ref helperWriterMaximumCompletionTicks,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    writerOffset, sizeof(long))));
            writerOffset += sizeof(long);
            Interlocked.Exchange(ref helperWriterLateSubmissionCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    writerOffset, sizeof(long))));
            writerOffset += sizeof(long);
            Interlocked.Exchange(ref helperWriterMaximumSubmissionGapTicks,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    writerOffset, sizeof(long))));
            writerOffset += sizeof(long);
            Interlocked.Exchange(ref helperInvalidLayoutCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    writerOffset, sizeof(long))));
            writerOffset += sizeof(long);
            Interlocked.Exchange(ref helperInvalidCrcCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    writerOffset, sizeof(long))));
            writerOffset += sizeof(long);
            Interlocked.Exchange(
                ref helperReportSequenceDiscontinuityCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    writerOffset, sizeof(long))));
            writerOffset += sizeof(long);
            Interlocked.Exchange(
                ref helperPacketSequenceDiscontinuityCount,
                BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(
                    writerOffset, sizeof(long))));

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
                WriteFrame(pipe, kind, payload);
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
                sizeof(ushort) + report.Length];
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0,
                sizeof(long)), reportId);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sizeof(long),
                sizeof(int)), epoch);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(long) +
                sizeof(int), sizeof(long)), hapticsExpiryQpc);
            int reportLengthOffset = sizeof(long) + sizeof(int) + sizeof(long);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(
                reportLengthOffset, sizeof(ushort)), (ushort)report.Length);
            Buffer.BlockCopy(report, 0, payload,
                reportLengthOffset + sizeof(ushort), report.Length);
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
            out string pipeName, out Guid authenticationToken,
            out int parentProcessId)
        {
            pipeName = string.Empty;
            authenticationToken = Guid.Empty;
            parentProcessId = 0;
            return args != null && args.Length >= 4 &&
                string.Equals(args[0], HelperArgument,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pipeName = args[1]) &&
                Guid.TryParseExact(args[2], "N", out authenticationToken) &&
                int.TryParse(args[3], out parentProcessId) &&
                parentProcessId > 0;
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

        private static bool TryDuplicateHandleIntoChild(
            SafeFileHandle sourceHandle, Process child,
            out IntPtr childHandle, out int error)
        {
            childHandle = IntPtr.Zero;
            error = 0;
            bool sourceReferenceAdded = false;
            try
            {
                sourceHandle.DangerousAddRef(ref sourceReferenceAdded);
                bool duplicated = DuplicateHandle(GetCurrentProcessNative(),
                    sourceHandle.DangerousGetHandle(), child.Handle,
                    out childHandle, 0, false, DuplicateSameAccess);
                if (!duplicated)
                {
                    error = Marshal.GetLastWin32Error();
                }

                return duplicated;
            }
            catch
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            finally
            {
                if (sourceReferenceAdded)
                {
                    sourceHandle.DangerousRelease();
                }
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
            // an orphan can still own the duplicated controller handle.
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
                pipe.Dispose();
            }
            catch
            {
            }
        }

        private static void RunHelper(string pipeName, Guid authenticationToken,
            int parentProcessId)
        {
            using var helperPipe = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous |
                PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);

            try
            {
                helperPipe.Connect(PipeConnectTimeoutMilliseconds);
                ReadFrame(helperPipe, out MessageKind kind, out byte[] payload);
                long duplicatedHandleValue = 0;
                string helloError = string.Empty;
                if (kind != MessageKind.Hello || !TryParseHello(payload,
                    authenticationToken, out duplicatedHandleValue,
                    out helloError))
                {
                    TryWriteError(helperPipe, string.IsNullOrEmpty(helloError) ?
                        "Invalid pacer hello message." : helloError);
                    return;
                }

                if (!IsExpectedParentAlive(parentProcessId))
                {
                    TryWriteError(helperPipe,
                        "The pacer parent process exited during initialization.");
                    return;
                }

                using var duplicatedHandle = new SafeFileHandle(
                    new IntPtr(duplicatedHandleValue), true);
                int writerError = 6;
                if (duplicatedHandle.IsInvalid ||
                    !DualSenseBluetoothRealtimeWriter.TryCreate(duplicatedHandle,
                        MaximumReportLength,
                        out DualSenseBluetoothRealtimeWriter writer,
                        out writerError, slotCount: PrimeReportCount))
                {
                    TryWriteError(helperPipe,
                        "Could not initialize the duplicated DualSense HID handle. " +
                        $"Win32Error={writerError}.");
                    return;
                }

                using (writer)
                using (var host = new HelperHost(helperPipe, writer,
                    duplicatedHandle, parentProcessId))
                {
                    WriteFrame(helperPipe, MessageKind.Ready, Array.Empty<byte>());
                    host.Run();
                }
            }
            catch (Exception ex)
            {
                TryWriteError(helperPipe, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool TryParseHello(byte[] payload,
            Guid expectedAuthenticationToken, out long duplicatedHandle,
            out string error)
        {
            duplicatedHandle = 0;
            error = string.Empty;
            if (payload == null || payload.Length != sizeof(int) + sizeof(long) + 16)
            {
                error = "Invalid pacer hello payload length.";
                return false;
            }

            int version = BinaryPrimitives.ReadInt32LittleEndian(
                payload.AsSpan(0, sizeof(int)));
            duplicatedHandle = BinaryPrimitives.ReadInt64LittleEndian(
                payload.AsSpan(sizeof(int), sizeof(long)));
            Guid token = new Guid(payload.AsSpan(sizeof(int) + sizeof(long), 16));
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

            if (duplicatedHandle == 0 || duplicatedHandle == -1)
            {
                error = "The duplicated DualSense HID handle is invalid.";
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

            private sealed class QueuedReport
            {
                public long Id;
                public int Epoch;
                public long HapticsExpiryQpc;
                public int Length;
                public readonly byte[] Report =
                    new byte[MaximumReportLength];

                public void Reset(long id, int epoch,
                    long hapticsExpiryQpc, byte[] source, int sourceOffset,
                    int reportLength)
                {
                    Id = id;
                    Epoch = epoch;
                    HapticsExpiryQpc = hapticsExpiryQpc;
                    Length = reportLength;
                    Buffer.BlockCopy(source, sourceOffset, Report, 0,
                        reportLength);
                }
            }

            private readonly struct QueuedAcknowledgement
            {
                public readonly long ReportId;
                public readonly AcknowledgementDisposition Disposition;
                public readonly long PresentedTimestamp;
                public readonly long WriterCompletedWrites;
                public readonly long WriterSlowCompletionCount;
                public readonly long WriterMaximumCompletionTicks;
                public readonly long WriterLateSubmissionCount;
                public readonly long WriterMaximumSubmissionGapTicks;
                public readonly long InvalidLayoutCount;
                public readonly long InvalidCrcCount;
                public readonly long ReportSequenceDiscontinuityCount;
                public readonly long PacketSequenceDiscontinuityCount;

                public QueuedAcknowledgement(long reportId,
                    AcknowledgementDisposition disposition,
                    long presentedTimestamp,
                    DualSenseBluetoothRealtimeWriter writer,
                    long invalidLayoutCount, long invalidCrcCount,
                    long reportSequenceDiscontinuityCount,
                    long packetSequenceDiscontinuityCount)
                {
                    ReportId = reportId;
                    Disposition = disposition;
                    PresentedTimestamp = presentedTimestamp;
                    WriterCompletedWrites = writer?.CompletedWrites ?? 0;
                    WriterSlowCompletionCount =
                        writer?.SlowCompletionCount ?? 0;
                    WriterMaximumCompletionTicks =
                        writer?.MaximumCompletionTicks ?? 0;
                    WriterLateSubmissionCount =
                        writer?.LateSubmissionCount ?? 0;
                    WriterMaximumSubmissionGapTicks =
                        writer?.MaximumSubmissionGapTicks ?? 0;
                    InvalidLayoutCount = invalidLayoutCount;
                    InvalidCrcCount = invalidCrcCount;
                    ReportSequenceDiscontinuityCount =
                        reportSequenceDiscontinuityCount;
                    PacketSequenceDiscontinuityCount =
                        packetSequenceDiscontinuityCount;
                }
            }

            private readonly object stateLock = new object();
            private readonly object pipeWriteLock = new object();
            private readonly Stream pipe;
            private readonly DualSenseBluetoothRealtimeWriter writer;
            private readonly SafeFileHandle duplicatedDeviceHandle;
            private readonly int parentProcessId;
            private readonly DualSenseBluetoothAudioPacerRing<QueuedReport>
                reservoir = new DualSenseBluetoothAudioPacerRing<QueuedReport>(
                    HostReservoirCapacity);
            private readonly DualSenseBluetoothAudioPacerRing<QueuedReport>
                controlReservoir =
                    new DualSenseBluetoothAudioPacerRing<QueuedReport>(
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
            private readonly Thread pacerThread;
            private readonly Thread acknowledgementThread;
            private readonly byte[] commandHeader =
                new byte[sizeof(byte) + sizeof(int)];
            private readonly byte[] commandPayload = new byte[
                sizeof(long) + sizeof(int) + sizeof(long) + sizeof(ushort) +
                MaximumReportLength];

            private readonly byte[] latestTemplate = new byte[ReportLength];
            private long latestTemplateHapticsExpiryQpc;
            private bool latestTemplateAvailable;
            private int currentEpoch = InitialEpoch;
            private bool primeRequired = true;
            private bool sequenceValidationInitialized;
            private bool packetSequenceValidationInitialized;
            private byte expectedReportSequence;
            private byte expectedPacketSequence;
            private bool presentationSequenceInitialized;
            private bool presentationPacketSequenceInitialized;
            private byte presentationReportSequence;
            private byte presentationPacketSequence;
            private long invalidLayoutCount;
            private long invalidCrcCount;
            private long reportSequenceDiscontinuityCount;
            private long packetSequenceDiscontinuityCount;
            private int disposed;

            public HelperHost(Stream pipe,
                DualSenseBluetoothRealtimeWriter writer,
                SafeFileHandle duplicatedDeviceHandle,
                int parentProcessId)
            {
                this.pipe = pipe;
                this.writer = writer;
                this.duplicatedDeviceHandle = duplicatedDeviceHandle;
                this.parentProcessId = parentProcessId;
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
                acknowledgementThread = new Thread(AcknowledgementLoop)
                {
                    IsBackground = true,
                    Name = "DualSense BT audio pacer acknowledgements",
                };
            }

            public void Run()
            {
                TryRaiseHelperProcessPriority();
                TrySetSustainedLowLatencyGc();
                acknowledgementThread.Start();
                pacerThread.Start();

                try
                {
                    while (!stopRequested.WaitOne(0))
                    {
                        int payloadLength = ReadFrameInto(pipe, commandHeader,
                            commandPayload, out MessageKind kind);
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
                    bool acknowledgementsStopped =
                        !acknowledgementThread.IsAlive ||
                        acknowledgementThread.Join(2000);

                    // Stopped is a cross-process transport-ownership barrier,
                    // not merely a thread-lifecycle notification. Publish it
                    // only after no helper thread can submit another report and
                    // the duplicated HID handle plus every OVERLAPPED buffer
                    // have been definitively retired.
                    bool transportReleased = false;
                    if (pacerStopped && acknowledgementsStopped)
                    {
                        writer.Dispose();
                        if (writer.WaitForDisposal(
                            HelperWriterReleaseTimeoutMilliseconds))
                        {
                            // WaitForDisposal retires the writer's SafeHandle
                            // reference. The wrapper that owns the duplicated
                            // child-process handle must also close before the
                            // parent may safely establish a new writer.
                            duplicatedDeviceHandle.Dispose();
                            transportReleased = duplicatedDeviceHandle.IsClosed;
                        }
                    }

                    if (CanPublishStopped(pacerStopped,
                        acknowledgementsStopped, transportReleased))
                    {
                        try
                        {
                            lock (pipeWriteLock)
                            {
                                WriteFrame(pipe, MessageKind.Stopped,
                                    Array.Empty<byte>());
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            private void ReceiveQueuedReport(byte[] payload, int payloadLength)
            {
                int reportLengthOffset = sizeof(long) + sizeof(int) +
                    sizeof(long);
                int reportOffset = reportLengthOffset + sizeof(ushort);
                if (payloadLength < reportOffset)
                {
                    throw new InvalidDataException(
                        "Invalid queued DualSense report payload length.");
                }

                int reportLength = BinaryPrimitives.ReadUInt16LittleEndian(
                    payload.AsSpan(reportLengthOffset, sizeof(ushort)));
                if (reportLength <= 0 ||
                    reportLength > MaximumReportLength ||
                    payloadLength != reportOffset + reportLength ||
                    !IsSupportedQueuedReport(payload, reportOffset,
                        reportLength))
                {
                    throw new InvalidDataException(
                        "Invalid queued DualSense report shape.");
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
                        reportOffset, reportLength);
                    // Every queued speaker report already contains the current
                    // control/haptics snapshot. Make it the presentation
                    // template atomically with queue admission so the parent
                    // does not need to send a redundant UpdateTemplate command
                    // (and allocate another clone/payload/command) every
                    // 10.667 ms. Explicit UpdateTemplate remains available for
                    // state changes that arrive between audio reports.
                    if (reportLength == ReportLength)
                    {
                        Buffer.BlockCopy(report.Report, 0, latestTemplate, 0,
                            ReportLength);
                        latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
                        latestTemplateAvailable = true;
                    }

                    bool speakerReport = IsSpeakerAudioReport(report.Report,
                        report.Length);
                    bool enqueued = speakerReport ?
                        reservoir.TryEnqueue(report) :
                        controlReservoir.TryEnqueue(report);
                    if (!enqueued)
                    {
                        availableReports.TryEnqueue(report);
                        QueueAcknowledgement(id,
                            AcknowledgementDisposition.Rejected);
                        return;
                    }
                }

                reservoirChanged.Set();
            }

            private static bool IsSupportedQueuedReport(byte[] source,
                int offset, int length)
            {
                if (source == null || offset < 0 || length <= 0 ||
                    offset + length > source.Length)
                {
                    return false;
                }

                return length == ReportLength && source[offset] == 0x36 ||
                    length == DoubleFrameReportLength &&
                        source[offset] == 0x39 ||
                    length == HapticsReportLength &&
                        source[offset] == 0x32 ||
                    length == StateReportLength && source[offset] == 0x31;
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
                    while (controlReservoir.TryDequeue(
                        out QueuedReport controlReport))
                    {
                        QueueAcknowledgement(controlReport.Id,
                            AcknowledgementDisposition.Cleared);
                        if (!availableReports.TryEnqueue(controlReport))
                        {
                            throw new InvalidOperationException(
                                "The pacer report pool overflowed while clearing controls.");
                        }
                    }
                }

                reservoirChanged.Set();
            }

            private void PacerLoop()
            {
                timeBeginPeriod(1);
                IntPtr multimediaHandle = RegisterMultimediaScheduler();
                IntPtr timer = CreateHighResolutionTimer();
                var scheduler = new DualSenseBluetoothAudioPacerScheduler(
                    Stopwatch.Frequency);

                try
                {
                    while (!stopRequested.WaitOne(0))
                    {
                        bool canPresent;
                        bool controlPrimeBypass;
                        lock (stateLock)
                        {
                            controlReservoir.TryPeek(
                                out QueuedReport nextControlReport);
                            reservoir.TryPeek(out QueuedReport nextReport);
                            int speakerReportCount = primeRequired ?
                                reservoir.CountLeading(IsQueuedSpeakerReport) : 0;
                            controlPrimeBypass = nextControlReport != null;
                            canPresent = controlPrimeBypass ||
                                CanPresentFromPrimeGate(primeRequired,
                                    speakerReportCount, nextReport?.Report,
                                    nextReport?.Length ?? 0);
                            if (canPresent && primeRequired &&
                                !controlPrimeBypass)
                            {
                                primeRequired = false;
                                scheduler.Start(Stopwatch.GetTimestamp());
                            }
                        }

                        if (!canPresent)
                        {
                            reservoirChanged.WaitOne(1000);
                            if (!IsExpectedParentAlive(parentProcessId))
                            {
                                stopRequested.Set();
                            }

                            continue;
                        }

                        if (!controlPrimeBypass)
                        {
                            WaitUntil(timer, scheduler.NextDeadlineQpc,
                                stopRequested);
                        }
                        if (stopRequested.WaitOne(0))
                        {
                            break;
                        }

                        QueuedReport item;
                        long itemId;
                        AcknowledgementDisposition disposition;
                        long presentedAt;
                        bool advanceScheduler;
                        bool controlOnly;
                        int audioFrameCount;
                        lock (stateLock)
                        {
                            if (controlPrimeBypass)
                            {
                                if (!controlReservoir.TryDequeue(out item))
                                {
                                    continue;
                                }
                            }
                            else if (primeRequired)
                            {
                                continue;
                            }

                            else if (!reservoir.TryDequeue(out item))
                            {
                                primeRequired = true;
                                scheduler.Reset();
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
                            audioFrameCount = GetSpeakerAudioFrameCount(
                                item.Report, item.Length);
                            controlOnly = audioFrameCount == 0;

                            if (item.Epoch != currentEpoch)
                            {
                                disposition =
                                    AcknowledgementDisposition.StaleEpoch;
                            }
                            else
                            {
                                if (item.Length == ReportLength &&
                                    item.Report[0] == 0x36)
                                {
                                    DualSenseBluetoothAudioReportPatcher
                                        .PatchForPresentation(
                                            item.Report,
                                            item.HapticsExpiryQpc,
                                            latestTemplateAvailable ?
                                                latestTemplate : null,
                                            latestTemplateHapticsExpiryQpc,
                                            presentedAt);
                                }
                                StampPresentationSequence(item.Report,
                                    item.Length);
                                bool transportFault;
                                bool accepted;
                                if (controlOnly)
                                {
                                    accepted = writer.TryWriteAndWait(
                                        item.Report, item.Length,
                                        HelperControlWriteTimeoutMilliseconds,
                                        out transportFault);
                                }
                                else if (RequiresPriorWriteCompletion(
                                    item.Report, item.Length))
                                {
                                    accepted = writer
                                        .TryWriteAfterPriorCompletion(
                                            item.Report, item.Length,
                                            HelperPresentationOrderingTimeoutMilliseconds,
                                            out transportFault);
                                }
                                else
                                {
                                    accepted = writer.TryWrite(item.Report,
                                        item.Length, out transportFault);
                                }
                                if (accepted)
                                {
                                    CommitPresentationSequence(item.Report,
                                        item.Length);
                                    ValidatePresentedReport(item.Report,
                                        item.Length);
                                }
                                disposition = accepted ?
                                    AcknowledgementDisposition.Presented :
                                    transportFault ?
                                        AcknowledgementDisposition.TransportFault :
                                        AcknowledgementDisposition.Rejected;
                            }

                            if (!controlOnly &&
                                ShouldRequireAudioPrimeAfterPresentation(
                                    false, reservoir.Count))
                            {
                                primeRequired = true;
                                scheduler.Reset();
                            }

                            if (!availableReports.TryEnqueue(item))
                            {
                                throw new InvalidOperationException(
                                    "The pacer report pool overflowed after presentation.");
                            }

                            advanceScheduler = !controlOnly &&
                                !primeRequired;
                        }

                        QueueAcknowledgement(itemId, disposition,
                            controlOnly ? 0 : presentedAt);
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

                        if (advanceScheduler)
                        {
                            scheduler.AdvanceAfterSend(presentedAt,
                                audioFrameCount);
                        }
                    }
                }
                finally
                {
                    if (timer != IntPtr.Zero)
                    {
                        CloseHandle(timer);
                    }

                    if (multimediaHandle != IntPtr.Zero)
                    {
                        AvRevertMmThreadCharacteristics(multimediaHandle);
                    }

                    timeEndPeriod(1);
                }
            }

            private static bool IsQueuedSpeakerReport(QueuedReport report)
            {
                return report != null && IsSpeakerAudioReport(report.Report,
                    report.Length);
            }

            /// <summary>
            /// Assigns Sony sequence values at the one true presentation
            /// boundary. Parent-side sequence reservations describe creation
            /// order, but a latency-sensitive state report may legitimately
            /// overtake queued audio. Stamping here keeps the physical stream
            /// contiguous in actual WriteFile order.
            /// </summary>
            private void StampPresentationSequence(byte[] report,
                int reportLength)
            {
                byte reportSequence = presentationSequenceInitialized ?
                    presentationReportSequence : (byte)(report[1] >> 4);
                report[1] = (byte)(reportSequence << 4);

                int packetSequenceOffset = GetPacketSequenceOffset(report,
                    reportLength);
                if (packetSequenceOffset >= 0)
                {
                    byte packetSequence =
                        presentationPacketSequenceInitialized ?
                            presentationPacketSequence :
                            report[packetSequenceOffset];
                    report[packetSequenceOffset] = packetSequence;
                }

                WriteSonyCrc(report, reportLength);
            }

            private void CommitPresentationSequence(byte[] report,
                int reportLength)
            {
                presentationReportSequence = (byte)(
                    ((report[1] >> 4) + 1) & 0x0F);
                presentationSequenceInitialized = true;

                int packetSequenceOffset = GetPacketSequenceOffset(report,
                    reportLength);
                if (packetSequenceOffset >= 0)
                {
                    presentationPacketSequence = unchecked((byte)(
                        report[packetSequenceOffset] +
                        (report[0] == 0x39 ? 2 : 1)));
                    presentationPacketSequenceInitialized = true;
                }
            }

            private static int GetPacketSequenceOffset(byte[] report,
                int reportLength)
            {
                if (report == null || reportLength <= 0)
                {
                    return -1;
                }

                return reportLength == DoubleFrameReportLength &&
                    report[0] == 0x39 ? 9 :
                    reportLength == ReportLength && report[0] == 0x36 ? 10 :
                    reportLength == HapticsReportLength &&
                        report[0] == 0x32 ? 10 :
                    -1;
            }

            private static void WriteSonyCrc(byte[] report, int reportLength)
            {
                const int crcLength = sizeof(uint);
                uint crc = DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                    report, reportLength - crcLength);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    report.AsSpan(reportLength - crcLength, crcLength), crc);
            }

            /// <summary>
            /// Validates the exact bytes presented to the physical controller.
            /// This runs after the live state/haptics merge and CRC rewrite, so
            /// it observes the same report that WriteFile receives.
            /// </summary>
            private void ValidatePresentedReport(byte[] report,
                int reportLength)
            {
                bool validLayout = false;
                if (report != null && reportLength > 0 &&
                    reportLength <= report.Length &&
                    (report[1] & 0x0F) == 0)
                {
                    if (reportLength == ReportLength && report[0] == 0x36)
                    {
                        bool speakerReport = IsSpeakerAudioReport(report,
                            reportLength);
                        validLayout = report[2] == 0x91 &&
                            report[3] == 0x07 && report[11] == 0x90 &&
                            report[12] == 63 && report[76] == 0x92 &&
                            report[77] == 64;
                        if (validLayout && speakerReport)
                        {
                            validLayout = report[5] == 64 &&
                                report[6] == 64 && report[7] == 64 &&
                                report[8] == 64 && report[9] == 64;
                        }
                    }
                    else if (reportLength == DoubleFrameReportLength &&
                        report[0] == 0x39)
                    {
                        validLayout = report[2] == 0x91 &&
                            report[3] == 0x06 &&
                            (report[4] == 0x7E || report[4] == 0x7F) &&
                            report[5] == DoubleFrameBufferLength &&
                            report[6] == DoubleFrameBufferLength &&
                            report[7] == DoubleFrameBufferLength &&
                            report[8] == DoubleFrameBufferLength &&
                            report[10] == 0xD2 && report[11] == 64 &&
                            report[140] == 0xD3 && report[141] == 200;
                    }
                    else if (reportLength == HapticsReportLength &&
                        report[0] == 0x32)
                    {
                        validLayout = report[2] == 0x91 &&
                            report[3] == 0x07 &&
                            (report[4] == 0xFE || report[4] == 0xFF) &&
                            report[5] == 0 && report[6] == 0 &&
                            report[7] == 0 && report[8] == 0 &&
                            report[9] == 0xFF && report[11] == 0x92 &&
                            report[12] == 64;
                    }
                    else if (reportLength == StateReportLength &&
                        report[0] == 0x31)
                    {
                        validLayout = report[2] == 0x10;
                    }
                }

                if (!validLayout)
                {
                    Interlocked.Increment(ref invalidLayoutCount);
                }

                if (report != null && reportLength > sizeof(uint) &&
                    reportLength <= report.Length)
                {
                    uint expectedCrc =
                        DualSenseBluetoothAudioReportPatcher.ComputeSonyCrc(
                            report, reportLength - sizeof(uint));
                    uint actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                        report.AsSpan(reportLength - sizeof(uint),
                            sizeof(uint)));
                    if (actualCrc != expectedCrc)
                    {
                        Interlocked.Increment(ref invalidCrcCount);
                    }

                    byte reportSequence = (byte)(report[1] >> 4);
                    if (sequenceValidationInitialized)
                    {
                        if (reportSequence != expectedReportSequence)
                        {
                            Interlocked.Increment(
                                ref reportSequenceDiscontinuityCount);
                        }
                    }

                    expectedReportSequence =
                        (byte)((reportSequence + 1) & 0x0F);
                    sequenceValidationInitialized = true;

                    int packetSequenceOffset = report[0] == 0x39 ? 9 :
                        report[0] == 0x36 || report[0] == 0x32 ? 10 : -1;
                    if (packetSequenceOffset >= 0)
                    {
                        byte packetSequence = report[packetSequenceOffset];
                        if (packetSequenceValidationInitialized &&
                            packetSequence != expectedPacketSequence)
                        {
                            Interlocked.Increment(
                                ref packetSequenceDiscontinuityCount);
                        }

                        expectedPacketSequence = unchecked((byte)(
                            packetSequence + (report[0] == 0x39 ? 2 : 1)));
                        packetSequenceValidationInitialized = true;
                    }
                }
            }

            private void QueueAcknowledgement(long reportId,
                AcknowledgementDisposition disposition,
                long presentedTimestamp = 0)
            {
                if (!acknowledgements.TryEnqueue(new QueuedAcknowledgement(
                    reportId, disposition, presentedTimestamp, writer,
                    Interlocked.Read(ref invalidLayoutCount),
                    Interlocked.Read(ref invalidCrcCount),
                    Interlocked.Read(
                        ref reportSequenceDiscontinuityCount),
                    Interlocked.Read(
                        ref packetSequenceDiscontinuityCount))))
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
                const int writerMetricCount = 5;
                const int reportMetricCount = 4;
                byte[] payload = new byte[
                    sizeof(long) + sizeof(byte) + sizeof(long) +
                    (writerMetricCount + reportMetricCount) * sizeof(long)];
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
                        int writerOffset = sizeof(long) + sizeof(byte) +
                            sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(writerOffset, sizeof(long)),
                            acknowledgement.WriterCompletedWrites);
                        writerOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(writerOffset, sizeof(long)),
                            acknowledgement.WriterSlowCompletionCount);
                        writerOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(writerOffset, sizeof(long)),
                            acknowledgement.WriterMaximumCompletionTicks);
                        writerOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(writerOffset, sizeof(long)),
                            acknowledgement.WriterLateSubmissionCount);
                        writerOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(writerOffset, sizeof(long)),
                            acknowledgement.WriterMaximumSubmissionGapTicks);
                        writerOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(writerOffset, sizeof(long)),
                            acknowledgement.InvalidLayoutCount);
                        writerOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(writerOffset, sizeof(long)),
                            acknowledgement.InvalidCrcCount);
                        writerOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(writerOffset, sizeof(long)),
                            acknowledgement
                                .ReportSequenceDiscontinuityCount);
                        writerOffset += sizeof(long);
                        BinaryPrimitives.WriteInt64LittleEndian(
                            payload.AsSpan(writerOffset, sizeof(long)),
                            acknowledgement
                                .PacketSequenceDiscontinuityCount);
                        lock (pipeWriteLock)
                        {
                            WriteFrame(pipe, MessageKind.ReportAcknowledged,
                                payload);
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

            private static IntPtr RegisterMultimediaScheduler()
            {
                try
                {
                    uint taskIndex = 0;
                    IntPtr handle = AvSetMmThreadCharacteristicsW("Pro Audio",
                        ref taskIndex);
                    if (handle != IntPtr.Zero)
                    {
                        AvSetMmThreadPriority(handle, AvrtPriority.High);
                    }

                    return handle;
                }
                catch
                {
                    return IntPtr.Zero;
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
                if (pacerStopped && acknowledgementStopped)
                {
                    reservoirChanged.Dispose();
                    acknowledgementAvailable.Dispose();
                    stopRequested.Dispose();
                }
            }
        }

        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAccess = 0x00000002 | 0x00100000;

        private enum AvrtPriority
        {
            Normal = 0,
            High = 1,
        }

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr GetCurrentProcessNative();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(IntPtr sourceProcessHandle,
            IntPtr sourceHandle, IntPtr targetProcessHandle,
            out IntPtr targetHandle, uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint milliseconds);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(
            string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle,
            AvrtPriority priority);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvRevertMmThreadCharacteristics(
            IntPtr avrtHandle);

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
    /// Pure rational-clock scheduler. Normal presentation jitter remains locked
    /// to the exact rational phase, with catch-up compression capped at 1 ms.
    /// Larger lateness re-anchors a full cadence after the presentation boundary
    /// so a delayed report can never cause a burst.
    /// </summary>
    internal sealed class DualSenseBluetoothAudioPacerScheduler
    {
        internal const int CadenceNumerator = 32;
        internal const int CadenceDenominator = 3000;

        private readonly long clockFrequency;
        private readonly long wholeTicks;
        private readonly long remainderTicks;
        private readonly long maximumCatchUpTicks;
        private long remainderAccumulator;
        private long nextDeadlineQpc;
        private bool started;

        public DualSenseBluetoothAudioPacerScheduler(long clockFrequency)
        {
            if (clockFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clockFrequency));
            }

            this.clockFrequency = clockFrequency;
            long scaled = checked(clockFrequency * CadenceNumerator);
            wholeTicks = scaled / CadenceDenominator;
            remainderTicks = scaled % CadenceDenominator;
            maximumCatchUpTicks = Math.Max(1, clockFrequency / 1000);
        }

        public bool IsStarted => started;
        public long NextDeadlineQpc => started ? nextDeadlineQpc :
            throw new InvalidOperationException("The pacer clock has not started.");

        public void Start(long nowQpc)
        {
            remainderAccumulator = 0;
            nextDeadlineQpc = nowQpc;
            started = true;
        }

        public void Reset()
        {
            remainderAccumulator = 0;
            nextDeadlineQpc = 0;
            started = false;
        }

        public long AdvanceAfterSend(long presentationQpc)
        {
            return AdvanceAfterSend(presentationQpc, 1);
        }

        public long AdvanceAfterSend(long presentationQpc,
            int audioFrameCount)
        {
            if (!started)
            {
                throw new InvalidOperationException("The pacer clock has not started.");
            }
            if (audioFrameCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(audioFrameCount));
            }

            long interval = 0;
            for (int frame = 0; frame < audioFrameCount; frame++)
            {
                interval = checked(interval + NextIntervalTicks());
            }
            long phaseDeadline = checked(nextDeadlineQpc + interval);
            long minimumPhaseGap = Math.Max(1,
                interval - maximumCatchUpTicks);
            long phaseGap = phaseDeadline - presentationQpc;
            nextDeadlineQpc = phaseGap >= minimumPhaseGap ?
                phaseDeadline : checked(presentationQpc + interval);
            return nextDeadlineQpc;
        }

        private long NextIntervalTicks()
        {
            long interval = wholeTicks;
            remainderAccumulator += remainderTicks;
            if (remainderAccumulator >= CadenceDenominator)
            {
                long extraTicks = remainderAccumulator / CadenceDenominator;
                interval += extraTicks;
                remainderAccumulator -= extraTicks * CadenceDenominator;
            }

            return Math.Max(1, interval);
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
        /// Merges a queued audio report with the newest template. When a
        /// template exists, its matching haptics expiry always wins over the
        /// queued report's older expiry. The queued expiry is only a fallback
        /// for a protocol-startup report received before any template.
        /// </summary>
        public static void PatchForPresentation(byte[] queuedReport,
            long queuedHapticsExpiryQpc, byte[] latestTemplate,
            long latestTemplateHapticsExpiryQpc, long nowQpc)
        {
            long effectiveExpiryQpc = latestTemplate != null ?
                latestTemplateHapticsExpiryQpc : queuedHapticsExpiryQpc;
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
                // buffer depths), byte 10 (packet counter), and bytes 142-343
                // (speaker TLV + 200-byte Opus frame). A live control-only
                // template uses the low-latency depth of 16; copying that over
                // a queued speaker report would replace its required depth of
                // 64 immediately before presentation.
                Buffer.BlockCopy(latestTemplate, 2, queuedReport, 2, 3);
                Buffer.BlockCopy(latestTemplate, 11, queuedReport, 11, 131);
            }

            if (hapticsExpiryQpc <= nowQpc)
            {
                Array.Clear(queuedReport, HapticsDataOffset,
                    HapticsDataLength);
            }

            WriteSonyCrc(queuedReport);
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
}
