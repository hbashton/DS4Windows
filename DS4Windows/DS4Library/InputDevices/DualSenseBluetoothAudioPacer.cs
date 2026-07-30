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
        internal const string AudioTransportEnvironmentVariable =
            "DS4WINDOWS_DUALSENSE_AUDIO_TRANSPORT";
        internal const string TestHapticsEnvironmentVariable =
            "DS4WINDOWS_DUALSENSE_TEST_HAPTICS";
        // Keep media on the hardware-validated PadForge-sized carrier: one
        // complete speaker/haptics generation per 10.667 ms. The 547-byte
        // paired carrier is valid on DS5Dongle's raw L2CAP stack, but Windows
        // HID does not expose its send credits and acoustic traces showed
        // discontinuities after otherwise-clean encoding. Microphone and
        // controller-state transitions are serialized separately below.
        internal const bool UsePairedAudioReports = false;
        // Sony's 0x39 report is complete as soon as one two-frame pair exists.
        // DS5Dongle does not wait for an additional host-side prime before it
        // hands that indivisible report to L2CAP.
        internal const int PrimeReportCount = UsePairedAudioReports ? 2 : 1;
        internal const int PairedAudioInFlightLimit =
            PairedAudioTransportSlotCount;
        // PadForge's Windows transport uses eight pinned OVERLAPPED slots in a
        // strict oldest-first ring. DS5Dongle's one-at-a-time CAN_SEND_NOW
        // discipline cannot be reproduced from HidBth completion events:
        // completion is an IRP boundary, not an exposed L2CAP send credit.
        // Keep PadForge's eight-slot Windows cushion around DS5Dongle's 0x39
        // wire image so normal 39-82 ms completion droughts do not starve the
        // controller while newer reports can never pass the oldest slot.
        internal const int PairedAudioTransportSlotCount = 8;
        // Windows does not expose BTstack's CAN_SEND_NOW credit. A strict
        // ten-slot OVERLAPPED FIFO covers the measured 39-82 ms HidBth service
        // drought while its oldest completion event remains our credit proxy.
        // Ten 0x36 frames span 106.7 ms and still carry fewer queued bytes than
        // the golden ten-slot 0x39 writer. Never scan around an unfinished
        // oldest slot.
        internal const int SingleAudioTransportSlotCount = 10;
        internal const int SingleAudioInFlightLimit = 10;
        internal const int HostReservoirCapacity = 64;
        // The paired path is source-driven like DS5Dongle. It must not replay
        // 398-era startup phases after a normal two-frame queue boundary.
        internal const int ControllerLinkWarmupIntervals = 0;
        // A single-report stream starts directly at native cadence. The old
        // paired path used a short 5 ms reserve-transfer phase; applying that
        // phase to 0x36 would burst reports at twice the firmware cadence.
        internal const int ControllerReserveTransferIntervals = 0;
        // Before the microphone clock is armed, redistribute 38.3 ms of the
        // existing ten-report host runway into the controller. Twenty-four
        // sequential reports contain twenty-three shortened gaps; at 9 ms the
        // producer supplies about 19.4 reports while the helper presents 24,
        // so a full ten-report host FIFO remains above five. Unlike a continuous rate
        // servo, this finite transfer never resamples audible media or changes
        // the steady playout clock.
        internal const int MicrophoneReserveTransferReports = 24;
        internal const int MicrophoneReserveTransferIntervals =
            MicrophoneReserveTransferReports - 1;
        // Only transfer reserve from a completely full ten-report host FIFO.
        // If that state is not observed, the bounded fail-open preserves the
        // ordinary nominal-cadence microphone transition.
        internal const int MicrophoneReserveTransferMinimumHostReports =
            SingleAudioTransportSlotCount;
        internal const int MicrophoneReserveTransferIntervalMicroseconds =
            9_000;
        internal const int MicrophoneReserveTransferTimeoutMilliseconds = 500;

        internal static bool UsePadForgeAudioTransport(string value)
        {
            return string.Equals(value, "35", StringComparison.Ordinal) ||
                UseCompactCombinedHapticsTransport(value);
        }

        internal static bool UseCompactCombinedHapticsTransport(string value)
        {
            return string.Equals(value, "35combined",
                StringComparison.Ordinal);
        }

        internal static bool RequiresFullDuplexAudioReport(byte[] report)
        {
            return IsSpeakerAudioReport(report) && (report[4] & 0x01) != 0;
        }

        internal static bool ShouldWaitForPhysicalWriteCredit(
            bool padForgeAudioTransport, bool pairedAudioReports)
        {
            return !padForgeAudioTransport && !pairedAudioReports;
        }

        internal static bool ShouldApplyInputPhaseCorrection(
            bool compactCombinedTransport, bool pairedAudioReports)
        {
            return !compactCombinedTransport && !pairedAudioReports;
        }

        internal static bool ShouldDropSaturatedAudio(
            bool padForgeAudioTransport, bool pairedAudioReport,
            bool controlOnly, bool accepted, bool transportFault,
            bool preserveForMicrophoneReserve = false)
        {
            return !preserveForMicrophoneReserve &&
                !controlOnly && !accepted && !transportFault &&
                (padForgeAudioTransport || pairedAudioReport);
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

        internal static int CompletePairedReportBoundary(
            int leadingSpeakerReports)
        {
            return Math.Max(0, leadingSpeakerReports & ~1);
        }

        private const string HelperArgument = "--dualsense-bt-audio-pacer-helper";
        private const int ProtocolVersion = 9;
        private const int PipeConnectTimeoutMilliseconds = 5000;
        private const int HelperReadyTimeoutMilliseconds = 5000;
        private const int HelperStopTimeoutMilliseconds = 3000;
        private const int HelperProcessExitTimeoutMilliseconds = 3000;
        private const uint HelperWriterReleaseTimeoutMilliseconds = 3000;
        private const uint HelperControlWriteTimeoutMilliseconds = 750;
        private const uint HelperAudioCreditPollMilliseconds = 1;
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
            UpdateCadence = 6,
            UpdateMicrophoneStatus = 7,
            UpdateControllerState = 8,
            UpdateControllerMediaBuffer = 9,
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
        private readonly byte[] latestControllerState = new byte[
            DualSenseBluetoothPhysicalOutputSequence.
                ControllerStatePayloadLength];
        private bool latestControllerStateAvailable;
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
                if (!candidate.UpdateControllerState(
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
            int speakerReportCount, byte[] nextReport)
        {
            return !primeRequired ||
                (nextReport != null && !IsSpeakerAudioReport(nextReport)) ||
                speakerReportCount >= PrimeReportCount;
        }

        internal static bool CanPresentFromTransportGate(bool primeRequired,
            int speakerReportCount, byte[] nextReport)
        {
            if (!CanPresentFromPrimeGate(primeRequired, speakerReportCount,
                nextReport))
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

        internal static bool ShouldRequireAudioPrimeAfterPresentation(
            bool presentedControlReport, int remainingReportCount)
        {
            // A momentarily empty producer queue is normal at the boundary
            // between source callbacks. DS5Dongle simply waits until the next
            // complete pair exists. Resetting here made DS4Windows wait for a
            // fresh prime and replay its startup rate transfer on every
            // shortage, creating 77 ms gaps followed by a 20 ms burst cadence.
            // A native 0x36 stream has no half-report generation to discard or
            // rebuild. DS5 Bridge resumes audio on the next send opportunity
            // after state, so keep the rational cadence continuous. Only the
            // dormant paired transport requires a new complete pair.
            return UsePairedAudioReports && presentedControlReport;
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
        /// Serializes Sony's native 0x32 microphone-state transition through
        /// the same physical FIFO and report-sequence owner as speaker audio.
        /// The controller must observe this transition before the steady audio
        /// header changes between playback and duplex; a template update alone
        /// can start microphone packets without coherently arming its audio
        /// clock.
        /// </summary>
        public bool UpdateMicrophoneStatus(bool enabled)
        {
            if (!IsRunning)
            {
                return false;
            }

            byte[] payload = { enabled ? (byte)1 : (byte)0 };
            lock (stateLock)
            {
                foreach (OutboundCommand removed in
                    outboundCommands.RemoveWhere(command =>
                        command.Kind == MessageKind.UpdateMicrophoneStatus))
                {
                }

                if (!outboundCommands.TryEnqueue(new OutboundCommand(
                    MessageKind.UpdateMicrophoneStatus, payload)))
                {
                    return false;
                }
            }

            outboundAvailable.Set();
            return true;
        }

        /// <summary>
        /// Atomically queues microphone intent, its matching live template,
        /// and the controller-state snapshot required before native 0x32.
        /// Reserving all three queue entries before publishing the first one
        /// prevents a successful status request from surviving a later state
        /// enqueue failure and crossing the helper's fail-open path alone.
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
            byte[] controllerState = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStatePayloadLength];
            Buffer.BlockCopy(template,
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStateSourceOffset,
                controllerState, 0, controllerState.Length);
            byte[] status = { enabled ? (byte)1 : (byte)0 };

            lock (stateLock)
            {
                foreach (OutboundCommand removed in
                    outboundCommands.RemoveWhere(command =>
                        command.Kind == MessageKind.UpdateMicrophoneStatus ||
                        command.Kind == MessageKind.UpdateTemplate ||
                        command.Kind == MessageKind.UpdateControllerState))
                {
                    // Transition commands never consume report credits.
                }

                const int transitionCommandCount = 3;
                if (outboundCommands.Capacity - outboundCommands.Count <
                    transitionCommandCount)
                {
                    return false;
                }

                // Intent is deliberately first: the helper freezes the
                // physical media header in its committed mode before the new
                // template can reach presentation. State still commits before
                // 0x32 after the finite audio boundary.
                if (!outboundCommands.TryEnqueue(new OutboundCommand(
                        MessageKind.UpdateMicrophoneStatus, status)) ||
                    !outboundCommands.TryEnqueue(new OutboundCommand(
                        MessageKind.UpdateTemplate,
                        BuildTemplatePayload(template, hapticsExpiryQpc))) ||
                    !outboundCommands.TryEnqueue(new OutboundCommand(
                        MessageKind.UpdateControllerState, controllerState)))
                {
                    throw new InvalidOperationException(
                        "The reserved microphone transition command group could not be queued atomically.");
                }

                latestTemplate = template;
                latestTemplateHapticsExpiryQpc = hapticsExpiryQpc;
                Buffer.BlockCopy(controllerState, 0, latestControllerState, 0,
                    controllerState.Length);
                latestControllerStateAvailable = true;
            }

            outboundAvailable.Set();
            return true;
        }

        /// <summary>
        /// Publishes PadForge's complete 47-byte common effect snapshot for a
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
                        UsePairedAudioReports ?
                            DualSenseBluetoothPairedAudioReportBuilder.ReportLength :
                            ReportLength,
                        out DualSenseBluetoothRealtimeWriter writer,
                        out writerError,
                        slotCount: UsePairedAudioReports ?
                            PairedAudioTransportSlotCount :
                            SingleAudioTransportSlotCount,
                        audioInFlightLimit: UsePairedAudioReports ?
                            PairedAudioInFlightLimit :
                            SingleAudioInFlightLimit))
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
            private const int PresentationTraceCapacity = 65536;
            private static readonly long ControllerStateIntervalQpc =
                Math.Max(1, Stopwatch.Frequency / 30);

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
            private readonly Stream pipe;
            private readonly DualSenseBluetoothRealtimeWriter writer;
            private readonly SafeFileHandle duplicatedDeviceHandle;
            private readonly int parentProcessId;
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
            private readonly Thread pacerThread;
            private readonly Thread acknowledgementThread;
            private readonly byte[] commandHeader =
                new byte[sizeof(byte) + sizeof(int)];
            private readonly byte[] commandPayload = new byte[
                sizeof(long) + sizeof(int) + sizeof(long) + ReportLength];

            private readonly byte[] latestTemplate = new byte[ReportLength];
            private readonly byte[] previousTemplate = new byte[ReportLength];
            private readonly byte[] pairedAudioReport = new byte[
                DualSenseBluetoothPairedAudioReportBuilder.ReportLength];
            private readonly byte[] padForgeAudioReport = new byte[
                DualSenseBluetoothPadForgeAudioReportBuilder.ReportLength];
            private readonly byte[] microphoneStatusReport = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    MicrophoneStatusReportLength];
            private readonly byte[] controllerStateReport = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStateReportLength];
            private readonly byte[] pendingControllerState = new byte[
                DualSenseBluetoothPhysicalOutputSequence.
                    ControllerStatePayloadLength];
            private readonly bool usePadForgeAudioTransport;
            private readonly bool useCompactCombinedHapticsTransport;
            private readonly bool injectTestHaptics;
            private int testHapticsSampleIndex;
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
            private long controllerMediaBufferObservationQpc;
            private int controllerMediaBufferLevel = -1;
            private long mediaBufferCadenceRatioBits =
                BitConverter.DoubleToInt64Bits(1.0);
            private bool primeRequired = true;
            private int pendingMicrophoneStatus = -1;
            private int microphoneStatusReportsAhead;
            private bool committedMicrophoneEnabled;
            private bool microphoneControllerStateRequired;
            private bool microphoneReserveTransferActive;
            private bool microphoneReserveTransferStarted;
            private bool microphoneReserveTransferCancelRequested;
            private int microphoneReserveTransferRequest;
            private long microphoneReserveTransferDeadlineQpc;
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

            public HelperHost(Stream pipe,
                DualSenseBluetoothRealtimeWriter writer,
                SafeFileHandle duplicatedDeviceHandle,
                int parentProcessId)
            {
                this.pipe = pipe;
                this.writer = writer;
                this.duplicatedDeviceHandle = duplicatedDeviceHandle;
                this.parentProcessId = parentProcessId;
                usePadForgeAudioTransport = !UsePairedAudioReports &&
                    UsePadForgeAudioTransport(
                        Environment.GetEnvironmentVariable(
                            AudioTransportEnvironmentVariable));
                useCompactCombinedHapticsTransport =
                    usePadForgeAudioTransport &&
                    UseCompactCombinedHapticsTransport(
                        Environment.GetEnvironmentVariable(
                            AudioTransportEnvironmentVariable));
                injectTestHaptics = string.Equals(
                    Environment.GetEnvironmentVariable(
                        TestHapticsEnvironmentVariable), "1",
                    StringComparison.Ordinal);
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

                    // DS5 Bridge serializes/coalesces controller state without
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

                    if (pendingMicrophoneStatus >= 0)
                    {
                        bool restartEnableTransfer =
                            pendingMicrophoneStatus > 0 &&
                            !committedMicrophoneEnabled;
                        microphoneReserveTransferActive =
                            restartEnableTransfer;
                        microphoneReserveTransferStarted = false;
                        microphoneReserveTransferCancelRequested = true;
                        microphoneReserveTransferRequest =
                            restartEnableTransfer ?
                                MicrophoneReserveTransferIntervals : 0;
                        microphoneReserveTransferDeadlineQpc =
                            restartEnableTransfer ?
                                Stopwatch.GetTimestamp() +
                                    Stopwatch.Frequency *
                                        MicrophoneReserveTransferTimeoutMilliseconds /
                                        1000 : 0;
                        microphoneStatusReportsAhead =
                            restartEnableTransfer ?
                                MicrophoneReserveTransferReports : 0;
                    }
                    if (pendingControllerStateAvailable)
                    {
                        controllerStateReportsAhead =
                            microphoneReserveTransferActive ?
                                microphoneStatusReportsAhead : 0;
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
                    if (pendingMicrophoneStatus == status ||
                        (pendingMicrophoneStatus < 0 &&
                            committedMicrophoneEnabled == (status != 0)))
                    {
                        // Coalesce an identical request without extending the
                        // accepted-report boundary or replaying the finite
                        // transfer. A duplicate enable used to reset the gate
                        // to 24 after some of those reports had already moved.
                        return;
                    }

                    pendingMicrophoneStatus = status;
                    microphoneControllerStateRequired = true;
                    // 0x39 takes its mic/audio-clock header from the second
                    // logical frame. Preserve only complete physical pairs
                    // ahead of 0x32; an odd old-mode half stays behind the
                    // transition and pairs with the next new-mode frame.
                    int reportsAhead =
                        CompletePairedReportBoundary(
                            reservoir.CountLeading(
                                IsQueuedSpeakerReport));
                    if (status != 0 && !committedMicrophoneEnabled)
                    {
                        microphoneReserveTransferActive = true;
                        microphoneReserveTransferStarted = false;
                        microphoneReserveTransferCancelRequested = false;
                        microphoneReserveTransferRequest =
                            MicrophoneReserveTransferIntervals;
                        // Bound both the wait for a complete host
                        // runway and the transfer itself. The deadline is
                        // renewed when the first shortened gap is armed.
                        microphoneReserveTransferDeadlineQpc =
                            Stopwatch.GetTimestamp() +
                            Stopwatch.Frequency *
                                MicrophoneReserveTransferTimeoutMilliseconds /
                                1000;

                        reportsAhead = Math.Max(reportsAhead,
                            MicrophoneReserveTransferReports);
                        if (pendingControllerStateAvailable)
                        {
                            controllerStateReportsAhead = Math.Max(
                                controllerStateReportsAhead, reportsAhead);
                        }
                    }
                    else
                    {
                        microphoneReserveTransferActive = false;
                        microphoneReserveTransferStarted = false;
                        microphoneReserveTransferCancelRequested = true;
                        microphoneReserveTransferRequest = 0;
                        microphoneReserveTransferDeadlineQpc = 0;
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
                    // PadForge treats 0x31 as a latest-value state latch. The
                    // first snapshot reserves its physical-pair boundary;
                    // later snapshots coalesce at that same position rather
                    // than jumping ahead of audio already admitted.
                    if (!pendingControllerStateAvailable)
                    {
                        controllerStateReportsAhead =
                            CompletePairedReportBoundary(
                                reservoir.CountLeading(
                                    IsQueuedSpeakerReport));
                        if (microphoneReserveTransferActive)
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
                        bool microphoneStatusReady;
                        bool controllerStateReady;
                        int idleWaitMilliseconds = 1000;
                        lock (stateLock)
                        {
                            long nowQpc = Stopwatch.GetTimestamp();
                            if (microphoneReserveTransferCancelRequested)
                            {
                                if (scheduler.IsStarted)
                                {
                                    scheduler.CancelControllerReserveTransfer();
                                }
                                microphoneReserveTransferCancelRequested =
                                    false;
                            }
                            if (microphoneReserveTransferActive &&
                                microphoneReserveTransferDeadlineQpc > 0 &&
                                nowQpc >=
                                    microphoneReserveTransferDeadlineQpc)
                            {
                                // Never leave microphone activation waiting on
                                // a producer or writer that has stopped making
                                // progress. The ordinary ordered transition is
                                // the bounded fallback.
                                microphoneReserveTransferActive = false;
                                microphoneReserveTransferStarted = false;
                                microphoneReserveTransferRequest = 0;
                                microphoneReserveTransferDeadlineQpc = 0;
                                microphoneStatusReportsAhead = 0;
                                microphoneControllerStateRequired = false;
                                if (pendingControllerStateAvailable)
                                {
                                    controllerStateReportsAhead = 0;
                                }
                                if (scheduler.IsStarted)
                                {
                                    scheduler.CancelControllerReserveTransfer();
                                }
                            }

                            microphoneStatusReady =
                                pendingMicrophoneStatus >= 0 &&
                                microphoneStatusReportsAhead <= 0 &&
                                !microphoneControllerStateRequired;
                            reservoir.TryPeek(out QueuedReport nextReport);
                            controllerStateReady =
                                pendingControllerStateAvailable &&
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
                            controlPrimeBypass = primeRequired &&
                                nextReport != null &&
                                !IsSpeakerAudioReport(nextReport.Report);
                            canPresent = microphoneStatusReady ||
                                controllerStateReady ||
                                CanPresentFromTransportGate(
                                    primeRequired, speakerReportCount,
                                    nextReport?.Report);
                            if (canPresent && primeRequired &&
                                !controlPrimeBypass &&
                                !microphoneStatusReady &&
                                !controllerStateReady)
                            {
                                primeRequired = false;
                                scheduler.Start(Stopwatch.GetTimestamp(),
                                    ControllerLinkWarmupIntervals,
                                    ControllerReserveTransferIntervals);
                            }

                            if (scheduler.IsStarted &&
                                microphoneReserveTransferRequest > 0 &&
                                speakerReportCount >=
                                    MicrophoneReserveTransferMinimumHostReports)
                            {
                                scheduler.BeginControllerReserveTransfer(
                                    microphoneReserveTransferRequest,
                                    MicrophoneReserveTransferIntervalMicroseconds);
                                microphoneReserveTransferRequest = 0;
                                microphoneReserveTransferStarted = true;
                                microphoneReserveTransferDeadlineQpc =
                                    Stopwatch.GetTimestamp() +
                                    Stopwatch.Frequency *
                                        MicrophoneReserveTransferTimeoutMilliseconds /
                                        1000;
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

                        // A microphone transition has two ordered pieces:
                        // first publish the full controller/mic state, then
                        // arm or disarm the native microphone clock. This
                        // is the ordering used by vDS and prevents 0x32
                        // from overtaking the state that makes it valid.
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
                                        if (pendingMicrophoneStatus >= 0)
                                        {
                                            microphoneControllerStateRequired =
                                                false;
                                        }
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
                                        physicalOutputSequence.Commit(
                                            audio: false);
                                        if (pendingMicrophoneStatus == status)
                                        {
                                            committedMicrophoneEnabled =
                                                status != 0;
                                            pendingMicrophoneStatus = -1;
                                            microphoneStatusReportsAhead = 0;
                                            microphoneControllerStateRequired =
                                                false;
                                            microphoneReserveTransferActive =
                                                false;
                                            microphoneReserveTransferStarted =
                                                false;
                                            microphoneReserveTransferCancelRequested =
                                                false;
                                            microphoneReserveTransferRequest =
                                                0;
                                            microphoneReserveTransferDeadlineQpc =
                                                0;
                                            scheduler.
                                                CancelControllerReserveTransfer();
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
                                reservoirChanged.WaitOne(1);
                            }

                            continue;
                        }

                        if (!controlPrimeBypass)
                        {
                            double controllerClockRatio =
                                BitConverter.Int64BitsToDouble(
                                    Interlocked.Read(ref cadenceRatioBits));
                            // Byte 65 remains diagnostic telemetry. Feeding it
                            // into steady cadence resampled audible media and
                            // still failed to raise the measured equilibrium.
                            // Keep the wire clock locked solely to the
                            // controller's long-window clock estimator; reserve
                            // is moved only by the bounded pre-mic transfer.
                            scheduler.SetRateRatio(Math.Clamp(
                                controllerClockRatio,
                                DualSenseBluetoothAudioPacerScheduler.
                                    MinimumRateRatio,
                                DualSenseBluetoothAudioPacerScheduler.
                                    MaximumRateRatio));
                            // PadForge owns one continuous media clock and does
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
                            // PadForge's Windows path owns one absolute media
                            // deadline and never catch-up bursts. Our logical
                            // reports can also be refreshed by high-rate HID
                            // state, so pair availability alone is not a media
                            // clock. Pace every physical 0x39 at the rational
                            // deadline; advancing twice below preserves its two
                            // 10.667 ms generations and the long-window clock
                            // correction without relying on IRP completion as
                            // an L2CAP credit.
                            WaitUntil(timer,
                                scheduler.PresentationDeadlineQpc,
                                stopRequested);
                        }
                        if (stopRequested.WaitOne(0))
                        {
                            break;
                        }

                        // Legacy lossless paths wait for an oldest-slot credit.
                        // PadForge and the paired hybrid instead probe the
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
                                usePadForgeAudioTransport,
                                UsePairedAudioReports) &&
                            !writer.WaitForNextWriteSlot(
                                HelperAudioCreditPollMilliseconds,
                                out bool creditTransportFault) &&
                            !creditTransportFault)
                        {
                            continue;
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
                        bool controlOnly;
                        bool retainedForRetry = false;
                        lock (stateLock)
                        {
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

                                bool protectMicrophoneReserveTransfer =
                                    microphoneReserveTransferActive &&
                                    microphoneReserveTransferStarted &&
                                    pendingMicrophoneStatus > 0 &&
                                    pairedItem == null && !controlOnly;
                                if (usePadForgeAudioTransport &&
                                    pendingMicrophoneStatus >= 0 &&
                                    pairedItem == null && !controlOnly)
                                {
                                    // Reports queued after the UI request may
                                    // already carry the desired FF header. Keep
                                    // the physical stream in its last committed
                                    // mode until the finite reserve transfer,
                                    // state write, and native 0x32 boundary have
                                    // all completed in FIFO order.
                                    ApplyCommittedMicrophoneMode(item.Report,
                                        committedMicrophoneEnabled);
                                }

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
                                        if (usePadForgeAudioTransport &&
                                            useCompactCombinedHapticsTransport &&
                                            injectTestHaptics)
                                        {
                                            ApplyTestHaptics(item.Report);
                                        }

                                        if (usePadForgeAudioTransport)
                                        {
                                            if (useCompactCombinedHapticsTransport)
                                            {
                                                physicalOutputSequence.
                                                    PreparePadForgeCombinedAudio(
                                                        item.Report,
                                                        padForgeAudioReport);
                                            }
                                            else
                                            {
                                                physicalOutputSequence.
                                                    PreparePadForgeAudio(
                                                        item.Report,
                                                        padForgeAudioReport);
                                            }

                                            physicalReport =
                                                padForgeAudioReport;
                                        }
                                        else
                                        {
                                            physicalOutputSequence.
                                                PrepareSingleAudio(item.Report);
                                        }
                                    }
                                    accepted = controlOnly ?
                                        writer.TryWriteAndWait(item.Report,
                                            HelperControlWriteTimeoutMilliseconds,
                                            out transportFault) :
                                        writer.TryWrite(physicalReport,
                                            out transportFault);
                                    if (accepted)
                                    {
                                        RecordPresentationTrace(physicalReport,
                                            presentedAt, reservoir.Count);
                                    }
                                }
                                else
                                {
                                    if (injectTestHaptics)
                                    {
                                        ApplyTestHaptics(item.Report);
                                        ApplyTestHaptics(pairedItem.Report);
                                    }
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

                                // PadForge spends counters and drops the new
                                // audio generation when its strict oldest slot
                                // is busy. Apply that Windows transport rule to
                                // one indivisible 0x39 pair as well. Controls
                                // remain retriable; hard I/O faults still tear
                                // down ownership.
                                bool skippedSaturatedAudio =
                                    ShouldDropSaturatedAudio(
                                        usePadForgeAudioTransport,
                                        pairedItem != null, controlOnly,
                                        accepted, transportFault,
                                        protectMicrophoneReserveTransfer);

                                if (accepted ||
                                    skippedSaturatedAudio)
                                {
                                    physicalOutputSequence.Commit(
                                        pairedItem != null || !controlOnly);
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

                            bool microphoneBoundaryCanAdvance =
                                !microphoneReserveTransferActive ||
                                microphoneReserveTransferStarted;
                            if (!retainedForRetry &&
                                microphoneBoundaryCanAdvance &&
                                pendingMicrophoneStatus >= 0 &&
                                microphoneStatusReportsAhead > 0)
                            {
                                // Spend the ordering boundary only when these
                                // logical generations actually leave the FIFO.
                                // A saturated 0x39 retry must not make 0x32
                                // overtake the same restored audio pair.
                                microphoneStatusReportsAhead = Math.Max(0,
                                    microphoneStatusReportsAhead -
                                    (pairedItem == null ? 1 : 2));
                            }
                            if (!retainedForRetry &&
                                microphoneBoundaryCanAdvance &&
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
                                !primeRequired;
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

                    if (multimediaHandle != IntPtr.Zero)
                    {
                        AvRevertMmThreadCharacteristics(multimediaHandle);
                    }

                    timeEndPeriod(1);
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

            private void ApplyTestHaptics(byte[] report)
            {
                const int sampleRate = 3000;
                const int framesPerPacket = 32;
                const double frequency = 85.0;
                const double amplitude = 18.0;
                report[76] = 0x92;
                report[77] = 64;
                for (int frame = 0; frame < framesPerPacket; frame++)
                {
                    double phase = 2.0 * Math.PI * frequency *
                        testHapticsSampleIndex++ / sampleRate;
                    sbyte value = (sbyte)Math.Round(
                        Math.Sin(phase) * amplitude);
                    report[78 + frame * 2] = unchecked((byte)value);
                    report[79 + frame * 2] = unchecked((byte)value);
                }
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
                        AvSetMmThreadPriority(handle, AvrtPriority.Critical);
                    }

                    return handle;
                }
                catch
                {
                    return IntPtr.Zero;
                }
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
                presentationTracePacketSequence[index] =
                    report[0] == 0x31 ? (byte)0 : report[10];
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
                    int firstCompactType = report[11] & 0x3F;
                    int secondCompactType = report[77] & 0x3F;
                    bool speakerFirst =
                        (firstCompactType == 0x13 ||
                            firstCompactType == 0x16) &&
                        report[12] == 200 &&
                        (report[213] & 0x3F) == 0x12 &&
                        report[214] == 64;
                    bool hapticsFirst = firstCompactType == 0x12 &&
                        report[12] == 64 &&
                        (secondCompactType == 0x13 ||
                            secondCompactType == 0x16) &&
                        report[78] == 200;
                    bool combinedHaptics = speakerFirst || hapticsFirst;
                    presentationTracePacketType[index] = speakerFirst ?
                        report[11] : hapticsFirst ? report[77] : report[11];
                    // Compact 0x35 has no state snapshot, but byte 4 still
                    // carries the audio-section mask (FE playback / FF
                    // duplex). Record it so a trace can prove the microphone
                    // request reached the physical carrier.
                    presentationTraceAudioFlags0[index] = report[4];
                    presentationTraceAudioFlags1[index] = 0;
                    presentationTraceHeadphoneVolume[index] = 0;
                    presentationTraceSpeakerVolume[index] = 0;
                    presentationTraceAudioRoute[index] = 0;
                    presentationTraceAudioGain[index] = 0;
                    if (combinedHaptics)
                    {
                        uint hash = 2166136261u;
                        int hapticsOffset = speakerFirst ? 215 : 13;
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
            Critical = 2,
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
        private int controllerReserveTransferIntervalMicroseconds =
            ControllerReserveTransferIntervalMicroseconds;
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
            controllerReserveTransferIntervalMicroseconds =
                ControllerReserveTransferIntervalMicroseconds;
            nextDeadlineQpc = nowQpc;
            started = true;
        }

        public void Reset()
        {
            remainderAccumulator = 0;
            fractionalRemainderAccumulator = 0.0;
            controllerLinkWarmupIntervalsRemaining = 0;
            controllerReserveTransferIntervalsRemaining = 0;
            controllerReserveTransferIntervalMicroseconds =
                ControllerReserveTransferIntervalMicroseconds;
            nextDeadlineQpc = 0;
            started = false;
        }

        public void BeginControllerReserveTransfer(int transferIntervals,
            int intervalMicroseconds)
        {
            if (transferIntervals < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transferIntervals));
            }
            if (intervalMicroseconds <= 0 ||
                intervalMicroseconds >= 10_667)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(intervalMicroseconds));
            }
            if (!started)
            {
                throw new InvalidOperationException(
                    "The pacer clock has not started.");
            }

            // This moves already-buffered, sequential reports from the host
            // FIFO to the controller. It deliberately leaves the rational
            // phase, media counters, producer cadence, and PCM untouched.
            controllerReserveTransferIntervalsRemaining = Math.Max(
                controllerReserveTransferIntervalsRemaining,
                transferIntervals);
            controllerReserveTransferIntervalMicroseconds =
                intervalMicroseconds;
        }

        public void CancelControllerReserveTransfer()
        {
            controllerReserveTransferIntervalsRemaining = 0;
            controllerReserveTransferIntervalMicroseconds =
                ControllerReserveTransferIntervalMicroseconds;
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
                    controllerReserveTransferIntervalMicroseconds /
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
    /// Pure report merger used immediately before a report is presented.
    /// </summary>
    internal static class DualSenseBluetoothAudioReportPatcher
    {
        internal const int ReportLength =
            DualSenseBluetoothAudioPacer.ReportLength;
        private const int CrcLength = sizeof(uint);
        private const int StateFlag0Offset = 13;
        private const int StateFlag1Offset = 14;
        private const int MicrophoneVolumeOffset = 19;
        private const int MicrophoneMuteLedOffset = 21;
        private const byte MicrophoneVolumeValidityBit = 0x40;
        private const byte MicrophoneMuteLedValidityBit = 0x01;
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

                // The speaker producer deliberately strips one-shot microphone
                // volume / mute-LED validity from every ordinary audio
                // snapshot. Preserve that decision while overlaying current
                // lightbar, trigger, rumble, routing, and haptics state. Copying
                // those two validity bits back from the controller-update
                // template made the physical audio engine alternate between
                // two state shapes every few reports; the captured acoustic
                // pops land on those transitions.
                byte microphoneVolumeValidity = (byte)(
                    queuedReport[StateFlag0Offset] &
                    MicrophoneVolumeValidityBit);
                byte microphoneMuteLedValidity = (byte)(
                    queuedReport[StateFlag1Offset] &
                    MicrophoneMuteLedValidityBit);
                byte microphoneVolume = queuedReport[MicrophoneVolumeOffset];
                byte microphoneMuteLed =
                    queuedReport[MicrophoneMuteLedOffset];

                // Preserve queued byte 1 (Sony sequence), bytes 5-9 (speaker
                // buffer depths), byte 10 (packet counter), and bytes 142-343
                // (speaker TLV + 200-byte Opus frame). A live control-only
                // template uses the low-latency depth of 16; copying that over
                // a queued speaker report would replace its independent audio
                // playback reserve immediately before presentation.
                Buffer.BlockCopy(latestTemplate, 2, queuedReport, 2, 3);
                Buffer.BlockCopy(latestTemplate, 11, queuedReport, 11, 131);
                queuedReport[StateFlag0Offset] = (byte)(
                    (queuedReport[StateFlag0Offset] &
                        ~MicrophoneVolumeValidityBit) |
                    microphoneVolumeValidity);
                queuedReport[StateFlag1Offset] = (byte)(
                    (queuedReport[StateFlag1Offset] &
                        ~MicrophoneMuteLedValidityBit) |
                    microphoneMuteLedValidity);
                queuedReport[MicrophoneVolumeOffset] = microphoneVolume;
                queuedReport[MicrophoneMuteLedOffset] = microphoneMuteLed;
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

    /// <summary>
    /// Owns the sequence numbers that the controller observes on the physical
    /// Bluetooth output lane. DS5Dongle consumes one global four-bit report
    /// sequence across 0x31 state, 0x32 microphone status, and 0x39 audio.
    /// The media packet counter remains independent and advances only when the
    /// two Opus frames in a 0x39 are accepted. Keeping the counters here keeps
    /// logical 0x36 staging frames from consuming values that never appear on
    /// the wire.
    /// </summary>
    internal sealed class DualSenseBluetoothPhysicalOutputSequence
    {
        private const int CrcLength = sizeof(uint);
        internal const int MicrophoneStatusReportLength = 142;
        internal const int ControllerStateReportLength = 78;
        internal const int ControllerStatePayloadLength = 47;
        internal const int ControllerStateSourceOffset = 13;
        private bool initialized;
        private byte nextReportSequence;
        private byte mediaPacketSequence;
        private byte preparedMediaPacketSequence;

        internal byte NextReportSequence => nextReportSequence;
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
            preparedMediaPacketSequence = second[10];

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
            preparedMediaPacketSequence = report[10];
            report[1] = (byte)((nextReportSequence & 0x0F) << 4);
            WriteSonyCrc(report);
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

            // vDS and DS5 Bridge both use five equal 64-byte lane depths for
            // true duplex. This is deliberately confined to FF reports; the
            // proven FE compact speaker/AUX carrier is not changed.
            for (int index = 5; index <= 9; index++)
            {
                report[index] = 64;
            }

            PrepareSingleAudio(report);
        }

        internal void PreparePadForgeAudio(byte[] source, byte[] destination)
        {
            ValidateSource(source, nameof(source));
            if (!DualSenseBluetoothAudioPacer.IsSpeakerAudioReport(source))
            {
                throw new ArgumentException(
                    "Source must be a complete 398-byte 0x36 speaker report.",
                    nameof(source));
            }

            EnsureInitialized(source);
            preparedMediaPacketSequence = source[10];
            DualSenseBluetoothPadForgeAudioReportBuilder.Build(source,
                nextReportSequence, preparedMediaPacketSequence, destination);
        }

        internal void PreparePadForgeCombinedAudio(byte[] source,
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
            preparedMediaPacketSequence = source[10];
            DualSenseBluetoothPadForgeCombinedAudioReportBuilder.Build(source,
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
            Array.Clear(destination, 0, destination.Length);
            destination[0] = 0x32;
            destination[1] = (byte)((nextReportSequence & 0x0F) << 4);
            destination[2] = 0x91;
            destination[3] = 1;
            destination[4] = enabled ? (byte)0x03 : (byte)0x02;
            WriteSonyCrc(destination);
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
            mediaPacketSequence = report[10];
            initialized = true;
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
    /// Converts one logical combined 0x36 speaker generation into PadForge's
    /// compact physical 0x35 speaker report. The logical report remains the
    /// source of the Opus payload and media counter; controller state and
    /// control-only reports continue to use the normal 0x36 path.
    /// </summary>
    internal static class DualSenseBluetoothPadForgeAudioReportBuilder
    {
        internal const int ReportLength = 334;
        private const int SourceReportLength =
            DualSenseBluetoothAudioPacer.ReportLength;
        private const int CrcLength = sizeof(uint);
        private const int SourceSpeakerDataOffset = 144;
        private const int SpeakerDataOffset = 13;
        private const int SpeakerFrameLength = 200;
        // DS5Dongle exposes this Sony media-reserve field over the documented
        // 16..127 range. Live controller feedback (BT input byte 65) showed
        // that 64 ms is exhausted by real 63..88 ms HidBth delivery droughts
        // even though our 10.667 ms submissions remain perfectly sequential.
        // Keep enough controller-side playout cushion for the measured tail;
        // this does not enlarge the host reservoir or permit catch-up bursts.
        private const byte DuplexBufferLength = 96;

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
            destination[3] = 7;
            bool microphoneEnabled = (source[4] & 0x01) != 0;
            destination[4] = microphoneEnabled ? (byte)0xFF : (byte)0xFE;
            if (microphoneEnabled)
            {
                for (int index = 5; index <= 9; index++)
                {
                    destination[index] = DuplexBufferLength;
                }
            }
            else
            {
                destination[9] = 0xFF;
            }
            destination[10] = packetSequence;
            // Preserve the logical media destination. 0x93 targets the
            // controller speaker; 0x96 targets the headset/AUX DAC. Rewriting
            // both as 0x93 makes Headset Only silently fall back to speaker.
            destination[11] = source[142];
            destination[12] = SpeakerFrameLength;
            Buffer.BlockCopy(source, SourceSpeakerDataOffset, destination,
                SpeakerDataOffset, SpeakerFrameLength);

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
    /// compact Sony subpacket container. This preserves PadForge's proven
    /// 334-byte, one-write-per-generation cadence while keeping haptics and
    /// audio atomic instead of competing for separate Bluetooth transactions.
    /// </summary>
    internal static class DualSenseBluetoothPadForgeCombinedAudioReportBuilder
    {
        internal const int ReportLength =
            DualSenseBluetoothPadForgeAudioReportBuilder.ReportLength;
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
        private const byte DuplexBufferLength = 96;

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
            destination[3] = 7;
            bool microphoneEnabled = (source[4] & 0x01) != 0;
            destination[4] = microphoneEnabled ? (byte)0xFF : (byte)0xFE;
            if (microphoneEnabled)
            {
                // Every mic-capable reference reasserts the inbound audio
                // section and real lane depths on each steady carrier. Keep
                // that header rule while preserving the golden haptics-first
                // TLV order byte-for-byte below.
                for (int index = 5; index <= 9; index++)
                {
                    destination[index] = DuplexBufferLength;
                }
            }
            else
            {
                destination[9] = 0xFF;
            }
            destination[10] = packetSequence;
            destination[GoldenHapticsHeaderOffset] = 0x92;
            destination[GoldenHapticsHeaderOffset + 1] = HapticsLength;
            Buffer.BlockCopy(source, SourceHapticsOffset, destination,
                GoldenHapticsDataOffset, HapticsLength);
            destination[GoldenSpeakerHeaderOffset] = source[142];
            destination[GoldenSpeakerHeaderOffset + 1] = SpeakerFrameLength;
            Buffer.BlockCopy(source, SourceSpeakerDataOffset, destination,
                GoldenSpeakerDataOffset, SpeakerFrameLength);

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
