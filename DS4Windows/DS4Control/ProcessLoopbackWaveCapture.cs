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
        private const int CaptureBufferMilliseconds = 5;
        private readonly AudioClient audioClient;
        private readonly AudioCaptureClient captureClient;
        private readonly EventWaitHandle captureEvent = new(false,
            EventResetMode.AutoReset);
        private readonly ManualResetEvent stopped = new(false);
        private readonly Thread captureThread;
        private byte[] scratch = Array.Empty<byte>();
        private int started;
        private int disposed;

        public ProcessLoopbackWaveCapture(int processId)
        {
            if (processId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(processId));
            }

            WaveFormat = new WaveFormat(44100, 16, 2);
            audioClient = ProcessLoopbackAudioClient.Activate(processId,
                TimeSpan.FromSeconds(5));
            audioClient.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback |
                    AudioClientStreamFlags.EventCallback,
                CaptureBufferMilliseconds * 10000L, 0, WaveFormat,
                Guid.Empty);
            audioClient.SetEventHandle(
                captureEvent.SafeWaitHandle.DangerousGetHandle());
            captureClient = audioClient.AudioCaptureClient;
            captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = $"DS4W app speaker capture {processId}",
                Priority = ThreadPriority.AboveNormal,
            };
        }

        public WaveFormat WaveFormat { get; set; }
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;

        public void StartRecording()
        {
            if (Volatile.Read(ref disposed) != 0 ||
                Interlocked.Exchange(ref started, 1) != 0)
            {
                return;
            }

            captureThread.Start();
            audioClient.Start();
        }

        public void StopRecording()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            stopped.Set();
            try { audioClient.Stop(); } catch { }
            captureEvent.Set();
            if (captureThread.IsAlive &&
                !ReferenceEquals(Thread.CurrentThread, captureThread))
            {
                captureThread.Join(1200);
            }
        }

        public void Dispose()
        {
            StopRecording();
            captureClient.Dispose();
            audioClient.Dispose();
            captureEvent.Dispose();
            stopped.Dispose();
        }

        public static string BuildEndpointId(int processId) =>
            $"{EndpointPrefix}{processId}";

        public static bool IsProcessEndpointId(string endpointId) =>
            endpointId?.StartsWith(EndpointPrefix,
                StringComparison.Ordinal) == true;

        public static bool TryParseEndpointId(string endpointId,
            out int processId)
        {
            processId = 0;
            return IsProcessEndpointId(endpointId) &&
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

        private void CaptureLoop()
        {
            Exception stoppedWith = null;
            WaitHandle[] waits = { stopped, captureEvent };
            try
            {
                while (Volatile.Read(ref disposed) == 0)
                {
                    int signaled = WaitHandle.WaitAny(waits, 1000);
                    if (signaled == 0)
                    {
                        break;
                    }
                    if (signaled == 1)
                    {
                        DrainCapture();
                    }
                }
            }
            catch (Exception ex) when (Volatile.Read(ref disposed) == 0)
            {
                stoppedWith = ex;
            }
            finally
            {
                RecordingStopped?.Invoke(this,
                    new StoppedEventArgs(stoppedWith));
            }
        }

        private void DrainCapture()
        {
            while (Volatile.Read(ref disposed) == 0)
            {
                int nextFrames = captureClient.GetNextPacketSize();
                if (nextFrames <= 0)
                {
                    return;
                }

                IntPtr buffer = captureClient.GetBuffer(
                    out int framesAvailable, out AudioClientBufferFlags flags,
                    out _, out _);
                try
                {
                    int byteCount = checked(framesAvailable *
                        WaveFormat.BlockAlign);
                    if (scratch.Length < byteCount)
                    {
                        scratch = new byte[byteCount];
                    }
                    if ((flags & AudioClientBufferFlags.Silent) != 0 ||
                        buffer == IntPtr.Zero)
                    {
                        Array.Clear(scratch, 0, byteCount);
                    }
                    else
                    {
                        Marshal.Copy(buffer, scratch, 0, byteCount);
                    }

                    DataAvailable?.Invoke(this,
                        new WaveInEventArgs(scratch, byteCount));
                }
                finally
                {
                    captureClient.ReleaseBuffer(framesAvailable);
                }
            }
        }
    }
}
