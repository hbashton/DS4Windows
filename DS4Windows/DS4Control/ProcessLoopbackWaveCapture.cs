using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Presents Windows process-loopback capture as an NAudio IWaveIn source so
    /// the existing low-latency controller speaker pipelines can consume one
    /// application without also capturing the rest of the system mix.
    /// </summary>
    internal sealed class ProcessLoopbackWaveCapture : IWaveIn
    {
        public const string EndpointPrefix = "DS4Windows:AudioHapticsApp:";
        public const string AutomaticEndpointPrefix =
            "DS4Windows:AudioHapticsAuto:";
        private const int CapturePollMilliseconds = 4;
        private const int DetectionIntervalMilliseconds = 500;
        private readonly int fixedProcessId;
        private readonly int automaticSlot = -1;
        private readonly AudioHapticsProfileSettings automaticSettings;
        private readonly AutomaticGameAudioDetector automaticDetector;
        private readonly object sessionLock = new();
        private readonly ManualResetEvent stopped = new(false);
        private ProcessCaptureSession session;
        private Thread monitorThread;
        private int currentProcessId;
        private string currentSourceDisplayName = string.Empty;
        private int started;
        private int disposed;

        public ProcessLoopbackWaveCapture(int processId)
        {
            if (processId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(processId));
            }

            fixedProcessId = processId;
            // Match the controller media clock so app-only capture does not
            // incur a mandatory 44.1 -> 48 kHz conversion before encoding.
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        }

        private ProcessLoopbackWaveCapture(int automaticSlot,
            AudioHapticsProfileSettings settings)
        {
            if (automaticSlot < 0 ||
                automaticSlot >= Global.TEST_PROFILE_ITEM_COUNT)
            {
                throw new ArgumentOutOfRangeException(nameof(automaticSlot));
            }
            this.automaticSlot = automaticSlot;
            automaticSettings = (settings ??
                new AudioHapticsProfileSettings()).Clone();
            automaticDetector = new AutomaticGameAudioDetector();
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        }

        public WaveFormat WaveFormat { get; set; }
        // Process-loopback activation is a virtual render endpoint. Windows'
        // reference implementation explicitly asks the audio engine to
        // convert the selected app's native mix into our fixed PCM format.
        // Omitting these flags made 48 kHz float apps sound harsh and
        // time-warped when streamed through the controller.
        internal const AudioClientStreamFlags CaptureStreamFlags =
            AudioClientStreamFlags.Loopback |
            AudioClientStreamFlags.EventCallback |
            AudioClientStreamFlags.AutoConvertPcm |
            AudioClientStreamFlags.SrcDefaultQuality;
        public int CurrentProcessId => Volatile.Read(ref currentProcessId);
        public string CurrentSourceDisplayName
        {
            get
            {
                lock (sessionLock) return currentSourceDisplayName;
            }
        }
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        public event EventHandler<ProcessAudioSourceChangedEventArgs>
            SourceChanged;

        public static ProcessLoopbackWaveCapture CreateAutomatic(int slot) =>
            new(slot, Global.store.audioHapticsSettings[slot]);

        public void StartRecording()
        {
            if (Volatile.Read(ref disposed) != 0 ||
                Interlocked.Exchange(ref started, 1) != 0)
            {
                return;
            }

            if (automaticSlot >= 0)
            {
                monitorThread = new Thread(AutomaticMonitorLoop)
                {
                    IsBackground = true,
                    Name = $"DS4W automatic game audio {automaticSlot + 1}",
                    Priority = ThreadPriority.AboveNormal,
                };
                monitorThread.Start();
            }
            else
            {
                SwitchToProcess(fixedProcessId,
                    DescribeProcess(fixedProcessId), "selected app");
            }
        }

        public void StopRecording()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            stopped.Set();
            ProcessCaptureSession oldSession;
            lock (sessionLock)
            {
                oldSession = session;
                session = null;
                currentProcessId = 0;
            }
            oldSession?.Dispose();
            if (automaticSlot >= 0)
            {
                SourceChanged?.Invoke(this,
                    new ProcessAudioSourceChangedEventArgs(0,
                        "Waiting for a detected game", "waiting"));
            }
            if (monitorThread?.IsAlive == true &&
                !ReferenceEquals(Thread.CurrentThread, monitorThread))
            {
                monitorThread.Join(1200);
            }
        }

        public void Dispose()
        {
            StopRecording();
            stopped.Dispose();
        }

        public static string BuildEndpointId(int processId) =>
            $"{EndpointPrefix}{processId}";

        public static string BuildAutomaticEndpointId(int slot) =>
            $"{AutomaticEndpointPrefix}{slot}";

        public static bool IsProcessEndpointId(string endpointId) =>
            endpointId?.StartsWith(EndpointPrefix,
                StringComparison.Ordinal) == true ||
            IsAutomaticEndpointId(endpointId);

        public static bool IsAutomaticEndpointId(string endpointId) =>
            endpointId?.StartsWith(AutomaticEndpointPrefix,
                StringComparison.Ordinal) == true;

        public static bool TryParseAutomaticEndpointId(string endpointId,
            out int slot)
        {
            slot = -1;
            return IsAutomaticEndpointId(endpointId) &&
                int.TryParse(endpointId.Substring(
                    AutomaticEndpointPrefix.Length), out slot) &&
                slot >= 0 && slot < Global.TEST_PROFILE_ITEM_COUNT;
        }

        public static bool TryParseEndpointId(string endpointId,
            out int processId)
        {
            processId = 0;
            return endpointId?.StartsWith(EndpointPrefix,
                    StringComparison.Ordinal) == true &&
                int.TryParse(endpointId.Substring(EndpointPrefix.Length),
                    out processId) && processId > 0;
        }

        public static int ResolveProcessId(
            AudioHapticsProfileSettings settings)
        {
            if (settings == null)
            {
                return 0;
            }

            if (settings.ProcessId > 0)
            {
                try
                {
                    using Process process = Process.GetProcessById(
                        settings.ProcessId);
                    if (!process.HasExited)
                    {
                        return process.Id;
                    }
                }
                catch { }
            }

            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        string path = process.MainModule?.FileName ??
                            string.Empty;
                        if (!string.IsNullOrWhiteSpace(settings.ProcessPath) &&
                            string.Equals(path, settings.ProcessPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return process.Id;
                        }
                        if (!string.IsNullOrWhiteSpace(
                                settings.ExecutableName) &&
                            string.Equals(process.ProcessName,
                                settings.ExecutableName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return process.Id;
                        }
                    }
                    catch { }
                }
            }

            return 0;
        }

        private void AutomaticMonitorLoop()
        {
            int proposedProcessId = 0;
            int proposedCount = 0;
            int misses = 0;
            try
            {
                while (Volatile.Read(ref disposed) == 0)
                {
                    int current = CurrentProcessId;
                    GameAudioCandidate candidate = null;
                    bool detected = automaticDetector.TryDetect(current,
                        out candidate);
                    if (!detected && current == 0)
                    {
                        int fallback = ResolveProcessId(automaticSettings);
                        if (fallback > 0)
                        {
                            candidate = new GameAudioCandidate
                            {
                                ProcessId = fallback,
                                DisplayName = DescribeProcess(fallback),
                                Evidence = GameDetectionEvidence.None,
                            };
                            detected = true;
                        }
                    }

                    if (detected && candidate.ProcessId == current)
                    {
                        proposedProcessId = 0;
                        proposedCount = 0;
                        misses = 0;
                    }
                    else if (detected)
                    {
                        misses = 0;
                        if (candidate.ProcessId == proposedProcessId)
                        {
                            proposedCount++;
                        }
                        else
                        {
                            proposedProcessId = candidate.ProcessId;
                            proposedCount = 1;
                        }
                        // Acquire the first game immediately. Require two
                        // consistent scans before changing an active stream.
                        if (current == 0 || proposedCount >= 2)
                        {
                            try
                            {
                                SwitchToProcess(candidate.ProcessId,
                                    candidate.DisplayName,
                                    candidate.EvidenceDescription);
                            }
                            catch (Exception exception)
                            {
                                AppLogger.LogToGui(
                                    $"Automatic game audio could not attach to '{candidate.DisplayName}': {exception.Message}",
                                    true);
                            }
                            proposedProcessId = 0;
                            proposedCount = 0;
                        }
                    }
                    else if (current > 0 && ++misses >= 10 &&
                        !ProcessIsAlive(current))
                    {
                        ClearCurrentSession();
                        misses = 0;
                    }

                    if (stopped.WaitOne(DetectionIntervalMilliseconds)) break;
                }
            }
            catch (Exception exception) when (
                Volatile.Read(ref disposed) == 0)
            {
                RecordingStopped?.Invoke(this,
                    new StoppedEventArgs(exception));
            }
        }

        private void SwitchToProcess(int processId, string displayName,
            string evidence)
        {
            if (processId <= 0 || processId == CurrentProcessId ||
                Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            ProcessCaptureSession replacement = null;
            replacement = new ProcessCaptureSession(processId, WaveFormat,
                (buffer, count) => OnSessionData(replacement, buffer, count),
                (_, exception) => OnSessionStopped(replacement, exception));
            ProcessCaptureSession oldSession;
            int oldProcessId;
            string oldDisplayName;
            lock (sessionLock)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    replacement.Dispose();
                    return;
                }
                oldSession = session;
                oldProcessId = currentProcessId;
                oldDisplayName = currentSourceDisplayName;
                session = replacement;
                currentProcessId = processId;
                currentSourceDisplayName = string.IsNullOrWhiteSpace(
                    displayName) ? DescribeProcess(processId) : displayName;
            }
            try
            {
                replacement.Start();
            }
            catch
            {
                lock (sessionLock)
                {
                    if (ReferenceEquals(session, replacement))
                    {
                        session = oldSession;
                        currentProcessId = oldProcessId;
                        currentSourceDisplayName = oldDisplayName;
                    }
                }
                replacement.Dispose();
                throw;
            }
            oldSession?.Dispose();
            string selector = automaticSlot >= 0 ?
                "Automatic game audio" : "App audio";
            AppLogger.LogToGui(
                $"{selector} selected '{CurrentSourceDisplayName}' " +
                $"(process {processId}, {evidence}).", false);
            SourceChanged?.Invoke(this,
                new ProcessAudioSourceChangedEventArgs(processId,
                    CurrentSourceDisplayName, evidence));
        }

        private void ClearCurrentSession()
        {
            ProcessCaptureSession oldSession;
            lock (sessionLock)
            {
                oldSession = session;
                session = null;
                currentProcessId = 0;
                currentSourceDisplayName = string.Empty;
            }
            oldSession?.Dispose();
            SourceChanged?.Invoke(this,
                new ProcessAudioSourceChangedEventArgs(0,
                    "Waiting for a detected game", "waiting"));
        }

        private void OnSessionData(ProcessCaptureSession source,
            byte[] buffer, int byteCount)
        {
            if (ReferenceEquals(session, source))
            {
                DataAvailable?.Invoke(this,
                    new WaveInEventArgs(buffer, byteCount));
            }
        }

        private void OnSessionStopped(ProcessCaptureSession source,
            Exception exception)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            bool wasCurrent = false;
            lock (sessionLock)
            {
                if (ReferenceEquals(session, source))
                {
                    session = null;
                    currentProcessId = 0;
                    currentSourceDisplayName = string.Empty;
                    wasCurrent = true;
                }
            }
            source?.Dispose();
            if (wasCurrent && automaticSlot >= 0)
            {
                SourceChanged?.Invoke(this,
                    new ProcessAudioSourceChangedEventArgs(0,
                        "Waiting for a detected game", "waiting"));
            }
            else if (wasCurrent)
            {
                RecordingStopped?.Invoke(this,
                    new StoppedEventArgs(exception));
            }
        }

        private static bool ProcessIsAlive(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch { return false; }
        }

        private static string DescribeProcess(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !string.IsNullOrWhiteSpace(process.MainWindowTitle)
                    ? process.MainWindowTitle : process.ProcessName;
            }
            catch { return $"process {processId}"; }
        }

        private sealed class ProcessCaptureSession : IDisposable
        {
            private readonly AudioClient audioClient;
            private readonly AudioCaptureClient captureClient;
            private readonly EventWaitHandle captureEvent = new(false,
                EventResetMode.AutoReset);
            private readonly ManualResetEvent stopped = new(false);
            private readonly Thread captureThread;
            private readonly WaveFormat waveFormat;
            private readonly Action<byte[], int> dataAvailable;
            private readonly Action<int, Exception> recordingStopped;
            private byte[] scratch = Array.Empty<byte>();
            private int disposed;
            private int loggedPollingRecovery;

            public ProcessCaptureSession(int processId, WaveFormat waveFormat,
                Action<byte[], int> dataAvailable,
                Action<int, Exception> recordingStopped)
            {
                ProcessId = processId;
                this.waveFormat = waveFormat;
                this.dataAvailable = dataAvailable;
                this.recordingStopped = recordingStopped;
                audioClient = ProcessLoopbackAudioClient.Activate(processId,
                    TimeSpan.FromSeconds(5));
                // Process loopback is a virtual audio device, not a physical
                // endpoint. Match Microsoft's ApplicationLoopback reference:
                // in shared event-driven mode both requested durations are
                // zero so the audio engine selects the native periodicity.
                // Supplying our controller's 5 ms capture preference here can
                // successfully initialize while never producing sample-ready
                // notifications on some Windows audio-engine configurations.
                audioClient.Initialize(AudioClientShareMode.Shared,
                    CaptureStreamFlags,
                    0, 0, waveFormat, Guid.Empty);
                audioClient.SetEventHandle(
                    captureEvent.SafeWaitHandle.DangerousGetHandle());
                captureClient = audioClient.AudioCaptureClient;
                captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = $"DS4W app audio capture {processId}",
                    Priority = ThreadPriority.Highest,
                };
            }

            public int ProcessId { get; }

            public void Start()
            {
                // Arm the engine before dispatching the event consumer, as in
                // the Windows reference. Auto-reset preserves an early signal.
                audioClient.Start();
                captureThread.Start();
            }

            private void CaptureLoop()
            {
                using MultimediaThreadRegistration mmcss =
                    MultimediaThreadRegistration.EnterProAudio();
                Exception stoppedWith = null;
                WaitHandle[] waits = { stopped, captureEvent };
                try
                {
                    while (Volatile.Read(ref disposed) == 0)
                    {
                        int signaled = WaitHandle.WaitAny(waits,
                            CapturePollMilliseconds);
                        if (signaled == 0) break;
                        // The process-loopback virtual device occasionally
                        // queues packets without signaling its event when it
                        // is hosted beside another WASAPI capture client in a
                        // WPF process. GetNextPacketSize is the authoritative
                        // readiness contract, so poll it at a bounded interval
                        // as well as draining every event. This preserves the
                        // event-driven fast path and prevents a successfully
                        // activated app source from remaining silent forever.
                        int drainedPackets = DrainCapture();
                        if (signaled == WaitHandle.WaitTimeout &&
                            drainedPackets > 0 && Interlocked.Exchange(
                                ref loggedPollingRecovery, 1) == 0)
                        {
                            int recoveredProcessId = ProcessId;
                            ThreadPool.QueueUserWorkItem(_ =>
                                AppLogger.LogToGui(
                                    $"Per-app audio capture recovered queued packets for process {recoveredProcessId} after a missing WASAPI sample-ready signal.",
                                    false));
                        }
                    }
                }
                catch (Exception exception) when (
                    Volatile.Read(ref disposed) == 0)
                {
                    stoppedWith = exception;
                }
                finally
                {
                    if (Volatile.Read(ref disposed) == 0)
                    {
                        recordingStopped?.Invoke(ProcessId, stoppedWith);
                    }
                }
            }

            private int DrainCapture()
            {
                int drainedPackets = 0;
                while (Volatile.Read(ref disposed) == 0)
                {
                    int nextFrames = captureClient.GetNextPacketSize();
                    if (nextFrames <= 0) return drainedPackets;
                    IntPtr buffer = captureClient.GetBuffer(
                        out int framesAvailable,
                        out AudioClientBufferFlags flags, out _, out _);
                    try
                    {
                        int byteCount = checked(framesAvailable *
                            waveFormat.BlockAlign);
                        if (scratch.Length < byteCount)
                            scratch = new byte[byteCount];
                        if ((flags & AudioClientBufferFlags.Silent) != 0 ||
                            buffer == IntPtr.Zero)
                            Array.Clear(scratch, 0, byteCount);
                        else
                            Marshal.Copy(buffer, scratch, 0, byteCount);
                        dataAvailable?.Invoke(scratch, byteCount);
                        drainedPackets++;
                    }
                    finally
                    {
                        captureClient.ReleaseBuffer(framesAvailable);
                    }
                }
                return drainedPackets;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                stopped.Set();
                try { audioClient.Stop(); } catch { }
                captureEvent.Set();
                if (captureThread.IsAlive &&
                    !ReferenceEquals(Thread.CurrentThread, captureThread))
                    captureThread.Join(1200);
                captureClient.Dispose();
                audioClient.Dispose();
                captureEvent.Dispose();
                stopped.Dispose();
            }
        }
    }

    internal sealed class ProcessAudioSourceChangedEventArgs : EventArgs
    {
        public ProcessAudioSourceChangedEventArgs(int processId,
            string displayName, string evidence)
        {
            ProcessId = processId;
            DisplayName = displayName ?? string.Empty;
            Evidence = evidence ?? string.Empty;
        }

        public int ProcessId { get; }
        public string DisplayName { get; }
        public string Evidence { get; }
    }
}
