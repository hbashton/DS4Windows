using Concentus;
using Concentus.Enums;
using DS4Windows.InputDevices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Mirrors a Windows render endpoint to a physical Bluetooth DualSense
    /// speaker. The transport is the 0x35 / packet 0x13 Opus lane documented
    /// by SAxense and the MIT-licensed dualsense-bt-haptics research project.
    ///
    /// VIIPER's virtual DualSense audio interface is a valid source here: its
    /// first two channels are controller speaker audio. Channels three and four
    /// remain owned by VIIPER's separate haptics bridge and are never mixed into
    /// this speaker stream.
    /// </summary>
    internal sealed class DualSenseBluetoothSpeakerPassthrough : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int FrameSamples = 480;
        private const int SourcePullFrames = 512;
        private const int OpusBytes = 200;
        private const int ReportLength = 334;
        private const int LowLatencyCaptureBufferMs = 10;
        private const int CaptureBufferMs = 60;
        private const int IdleKeepAliveMs = 2000;
        private const double BluetoothSpeakerCadenceMs = 10.0 + (2.0 / 3.0);

        private readonly object syncRoot = new object();
        private readonly DualSenseDevice device;
        private readonly string sourceEndpointId;
        private readonly byte speakerVolume;
        private readonly float[] sourceFrame = new float[SourcePullFrames * Channels];
        private readonly float[] frame = new float[FrameSamples * Channels];
        private readonly byte[] opusFrame = new byte[OpusBytes];
        private readonly byte[] report = new byte[ReportLength];

        private WasapiCapture capture;
        private BufferedWaveProvider captureBuffer;
        private Thread worker;
        private IOpusEncoder opusEncoder;
        private volatile bool stopping;
        private int reportSequence;
        private byte packetCounter;
        private int loggedWriteFailure;
        private bool keepStreamWarm;
        private DateTime lastAudibleUtc = DateTime.MinValue;

        public DualSenseBluetoothSpeakerPassthrough(DualSenseDevice device, byte speakerVolume,
            string sourceEndpointId)
        {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            this.speakerVolume = speakerVolume;
            this.sourceEndpointId = sourceEndpointId ?? string.Empty;
        }

        public void Start()
        {
            if (!IsGenuineBluetoothDualSense(device))
            {
                throw new InvalidOperationException("Bluetooth speaker passthrough requires a physical Sony DualSense or DualSense Edge.");
            }

            try
            {
                capture = CreateCapture(sourceEndpointId, out string sourceName);
                keepStreamWarm = IsLikelyGameAudioEndpoint(sourceName);
                captureBuffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(CaptureBufferMs),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                };
                capture.DataAvailable += Capture_DataAvailable;
                capture.RecordingStopped += Capture_RecordingStopped;

                ISampleProvider source = captureBuffer.ToSampleProvider();
                source = ToStereo(source);
                if (source.WaveFormat.SampleRate != SampleRate)
                {
                    source = new WdlResamplingSampleProvider(source, SampleRate);
                }

                opusEncoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
                opusEncoder.Bitrate = OpusBytes * 8 * 100;
                opusEncoder.UseVBR = false;

                worker = new Thread(() => StreamLoop(source))
                {
                    IsBackground = true,
                    Name = "DualSense Bluetooth speaker audio",
                    Priority = ThreadPriority.AboveNormal,
                };
                capture.StartRecording();
                worker.Start();
                AppLogger.LogToGui(
                    $"DualSense Bluetooth speaker passthrough started: {sourceName}" +
                    (keepStreamWarm ? " (low latency game-audio mode)" : string.Empty),
                    false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static WasapiCapture CreateCapture(string endpointId, out string sourceName)
        {
            if (string.IsNullOrEmpty(endpointId))
            {
                sourceName = "Default audio endpoint";
                return new LowLatencyWasapiLoopbackCapture(
                    WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice(),
                    LowLatencyCaptureBufferMs);
            }

            using var enumerator = new MMDeviceEnumerator();
            MMDevice endpoint = enumerator.GetDevice(endpointId);
            if (endpoint == null || endpoint.State != DeviceState.Active)
            {
                throw new InvalidOperationException("Selected Bluetooth speaker audio source is not available.");
            }

            sourceName = endpoint.FriendlyName;
            return new LowLatencyWasapiLoopbackCapture(endpoint, LowLatencyCaptureBufferMs);
        }

        private static bool IsLikelyGameAudioEndpoint(string sourceName)
        {
            if (string.IsNullOrEmpty(sourceName))
            {
                return false;
            }

            return sourceName.IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sourceName.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sourceName.IndexOf("VIIPER", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsGenuineBluetoothDualSense(DualSenseDevice device)
        {
            if (device?.ConnectionType != ConnectionType.BT || device.HidDevice?.Attributes == null)
            {
                return false;
            }

            int vendorId = device.HidDevice.Attributes.VendorId;
            int productId = device.HidDevice.Attributes.ProductId;
            return vendorId == DS4Devices.SONY_VID && (productId == 0x0CE6 || productId == 0x0DF2);
        }

        private static ISampleProvider ToStereo(ISampleProvider source)
        {
            if (source.WaveFormat.Channels == Channels)
            {
                return source;
            }

            if (source.WaveFormat.Channels == 1)
            {
                return new MonoToStereoSampleProvider(source);
            }

            var mux = new MultiplexingSampleProvider(new[] { source }, Channels);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            return mux;
        }

        private void Capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            lock (syncRoot)
            {
                if (!stopping)
                {
                    captureBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }
            }
        }

        private void Capture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            if (!stopping && e.Exception != null)
            {
                AppLogger.LogToGui($"DualSense Bluetooth speaker capture stopped: {e.Exception.Message}", true);
            }
        }

        private void StreamLoop(ISampleProvider source)
        {
            timeBeginPeriod(1);
            IntPtr highResolutionTimer = CreateHighResolutionTimer();
            long nextTick = DateTime.UtcNow.Ticks;
            long cadenceTicks = (long)(BluetoothSpeakerCadenceMs * TimeSpan.TicksPerMillisecond);
            try
            {
                while (!stopping)
                {
                    Array.Clear(sourceFrame, 0, sourceFrame.Length);
                    int samplesRead;
                    lock (syncRoot)
                    {
                        samplesRead = stopping ? 0 : source.Read(sourceFrame, 0, sourceFrame.Length);
                    }

                    bool audible = samplesRead > 0 && HasAudibleSamples(sourceFrame, samplesRead);
                    if (audible)
                    {
                        lastAudibleUtc = DateTime.UtcNow;
                    }

                    FillOutputFrame(samplesRead);
                    float volume = speakerVolume / 255.0f;
                    for (int i = 0; i < frame.Length; i++)
                    {
                        frame[i] = Math.Clamp(frame[i] * volume, -1.0f, 1.0f);
                    }

                    bool recentlyAudible = lastAudibleUtc != DateTime.MinValue &&
                        DateTime.UtcNow - lastAudibleUtc <= TimeSpan.FromMilliseconds(IdleKeepAliveMs);
                    if (keepStreamWarm || audible || recentlyAudible)
                    {
                        SendFrame();
                    }

                    nextTick += cadenceTicks;
                    long nowTicks = DateTime.UtcNow.Ticks;
                    if (nextTick <= nowTicks)
                    {
                        nextTick = nowTicks + cadenceTicks;
                        continue;
                    }

                    WaitUntil(highResolutionTimer, nextTick);
                }
            }
            finally
            {
                if (highResolutionTimer != IntPtr.Zero)
                {
                    CloseHandle(highResolutionTimer);
                }

                timeEndPeriod(1);
            }
        }

        private void FillOutputFrame(int samplesRead)
        {
            Array.Clear(frame, 0, frame.Length);
            int sourceFrames = Math.Min(SourcePullFrames, Math.Max(0, samplesRead / Channels));
            if (sourceFrames <= 0)
            {
                return;
            }

            double step = sourceFrames / (double)FrameSamples;
            for (int outputFrame = 0; outputFrame < FrameSamples; outputFrame++)
            {
                double position = outputFrame * step;
                int sourceIndex0 = Math.Min((int)position, sourceFrames - 1);
                int sourceIndex1 = Math.Min(sourceIndex0 + 1, sourceFrames - 1);
                double blend = position - sourceIndex0;
                int outputOffset = outputFrame * Channels;
                int sourceOffset0 = sourceIndex0 * Channels;
                int sourceOffset1 = sourceIndex1 * Channels;
                frame[outputOffset] = (float)(sourceFrame[sourceOffset0] * (1.0 - blend) +
                    sourceFrame[sourceOffset1] * blend);
                frame[outputOffset + 1] = (float)(sourceFrame[sourceOffset0 + 1] * (1.0 - blend) +
                    sourceFrame[sourceOffset1 + 1] * blend);
            }
        }

        private static void WaitUntil(IntPtr highResolutionTimer, long targetUtcTicks)
        {
            double waitMs = (targetUtcTicks - DateTime.UtcNow.Ticks) / (double)TimeSpan.TicksPerMillisecond;
            if (waitMs <= 0)
            {
                return;
            }

            if (highResolutionTimer != IntPtr.Zero)
            {
                long dueTime = -Math.Max(1, (long)(waitMs * 10000.0));
                if (SetWaitableTimer(highResolutionTimer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitForSingleObject(highResolutionTimer, Infinite);
                    return;
                }
            }

            Thread.Sleep(Math.Max(1, (int)Math.Round(waitMs)));
        }

        private static IntPtr CreateHighResolutionTimer()
        {
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null,
                CreateWaitableTimerHighResolution, TimerAccess);
            if (timer == IntPtr.Zero)
            {
                timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAccess);
            }

            return timer;
        }

        private sealed class LowLatencyWasapiLoopbackCapture : WasapiCapture
        {
            public LowLatencyWasapiLoopbackCapture(MMDevice captureDevice, int audioBufferMilliseconds)
                : base(captureDevice, true, audioBufferMilliseconds)
            {
            }

            protected override AudioClientStreamFlags GetAudioClientStreamFlags()
            {
                return AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
            }
        }

        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAccess = 0x00000002 | 0x00100000;
        private const uint Infinite = 0xFFFFFFFF;

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr lpTimerAttributes, string lpTimerName,
            uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod,
            IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static bool HasAudibleSamples(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(samples[i]) > 0.0001f)
                {
                    return true;
                }
            }

            return false;
        }

        private void SendFrame()
        {
            int encoded;
            try
            {
                encoded = opusEncoder.Encode(frame.AsSpan(), FrameSamples, opusFrame.AsSpan(), OpusBytes);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref loggedWriteFailure, 1) == 0)
                {
                    AppLogger.LogToGui($"DualSense Bluetooth speaker encoder failed: {ex.Message}", true);
                }

                return;
            }

            Array.Clear(report, 0, report.Length);
            report[0] = 0x35;
            report[1] = (byte)((reportSequence & 0x0F) << 4);
            reportSequence = (reportSequence + 1) & 0x0F;

            // Packet 0x11 starts the audio stream. Packet 0x13 is the
            // internal speaker lane; packet 0x16 would target the headset.
            report[2] = 0x91;
            report[3] = 0x07;
            report[4] = 0xFE;
            report[9] = 0xFF;
            report[10] = packetCounter++;
            report[11] = 0x93;
            report[12] = OpusBytes;
            Array.Copy(opusFrame, 0, report, 13, Math.Min(encoded, OpusBytes));

            uint crc = CalculateBluetoothCrc(report, ReportLength - 4);
            report[ReportLength - 4] = (byte)crc;
            report[ReportLength - 3] = (byte)(crc >> 8);
            report[ReportLength - 2] = (byte)(crc >> 16);
            report[ReportLength - 1] = (byte)(crc >> 24);

            if (!device.WriteBluetoothSpeakerAudioOutputReport(report, 0, report.Length) &&
                Interlocked.Exchange(ref loggedWriteFailure, 1) == 0)
            {
                AppLogger.LogToGui($"DualSense Bluetooth speaker write failed: {device.LastBluetoothHapticsWriteStatus}", true);
            }
        }

        private static uint CalculateBluetoothCrc(byte[] data, int length)
        {
            // Sony Bluetooth output reports use CRC32 with the 0xA2 report
            // prefix pre-applied. This is the SAxense seed expressed directly.
            uint crc = ~0xEADA2D49u;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
                }
            }

            return ~crc;
        }

        public void Dispose()
        {
            stopping = true;
            if (worker != null && worker.IsAlive && Thread.CurrentThread.ManagedThreadId != worker.ManagedThreadId)
            {
                worker.Join(500);
            }

            worker = null;
            WasapiCapture oldCapture;
            lock (syncRoot)
            {
                oldCapture = capture;
                capture = null;
                captureBuffer = null;
            }

            if (oldCapture != null)
            {
                oldCapture.DataAvailable -= Capture_DataAvailable;
                oldCapture.RecordingStopped -= Capture_RecordingStopped;
                try
                {
                    oldCapture.StopRecording();
                }
                catch { }

                oldCapture.Dispose();
            }
        }
    }
}
