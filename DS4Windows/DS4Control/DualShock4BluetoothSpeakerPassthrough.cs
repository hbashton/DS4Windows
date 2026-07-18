using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SBC;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Encodes a selected Windows playback endpoint as the SBC-over-HID stream
    /// understood by a physical Bluetooth DualShock 4.
    /// </summary>
    internal sealed class DualShock4BluetoothSpeakerPassthrough : IDisposable
    {
        private const int SampleRate = 32000;
        private const int Channels = 2;
        private const int SamplesPerSbcFrame = 128;
        private const int SamplesPerReport = SamplesPerSbcFrame *
            DualShock4BluetoothAudioProtocol.SpeakerFramesPerReport;
        private const int CaptureBufferMs = 240;
        private const int IdleKeepAliveMs = 2000;
        private const double ReportCadenceMilliseconds = 8.0;

        private readonly object syncRoot = new object();
        private readonly DS4Device device;
        private readonly byte speakerVolume;
        private readonly DualSenseSpeakerCompression compression;
        private readonly byte bassBoost;
        private readonly string sourceEndpointId;
        private readonly DualSenseSpeakerProcessor processor;
        private readonly SbcEncoder encoder = new SbcEncoder();
        private readonly SbcFrame frameConfiguration = new SbcFrame
        {
            Frequency = SbcFrequency.Freq32K,
            Mode = SbcMode.JointStereo,
            AllocationMethod = SbcBitAllocationMethod.SNR,
            Blocks = 16,
            Subbands = 8,
            Bitpool = 48,
        };
        private readonly float[] reportSamples = new float[SamplesPerReport * Channels];
        private readonly short[] pcmLeft = new short[SamplesPerSbcFrame];
        private readonly short[] pcmRight = new short[SamplesPerSbcFrame];
        private readonly AutoResetEvent captureAvailable = new AutoResetEvent(false);
        private readonly ManualResetEvent stoppingSignal = new ManualResetEvent(false);

        private WasapiCapture capture;
        private BufferedWaveProvider captureBuffer;
        private ISampleProvider sampleProvider;
        private Thread worker;
        private volatile bool stopping;
        private ushort frameNumber;
        private DateTime lastAudibleUtc = DateTime.MinValue;
        private int writeFailureLogged;

        public DualShock4BluetoothSpeakerPassthrough(DS4Device device, byte speakerVolume,
            DualSenseSpeakerCompression compression, byte bassBoost,
            string sourceEndpointId)
        {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            this.speakerVolume = speakerVolume;
            this.compression = (DualSenseSpeakerCompression)Math.Clamp((int)compression,
                (int)DualSenseSpeakerCompression.Off,
                (int)DualSenseSpeakerCompression.Strong);
            this.bassBoost = Math.Min(bassBoost,
                DualSenseSpeakerProcessor.MaximumBassBoostDb);
            this.sourceEndpointId = sourceEndpointId ?? string.Empty;
            processor = new DualSenseSpeakerProcessor(this.compression,
                this.bassBoost, SampleRate);
        }

        public bool Matches(DS4Device candidate, byte candidateVolume,
            DualSenseSpeakerCompression candidateCompression, byte candidateBassBoost,
            string candidateSourceEndpointId)
        {
            return !stopping && ReferenceEquals(device, candidate) &&
                speakerVolume == candidateVolume &&
                compression == (DualSenseSpeakerCompression)Math.Clamp(
                    (int)candidateCompression, (int)DualSenseSpeakerCompression.Off,
                    (int)DualSenseSpeakerCompression.Strong) &&
                bassBoost == Math.Min(candidateBassBoost,
                    DualSenseSpeakerProcessor.MaximumBassBoostDb) &&
                string.Equals(sourceEndpointId, candidateSourceEndpointId ?? string.Empty,
                    StringComparison.Ordinal);
        }

        public void Start()
        {
            if (!IsGenuineBluetoothDualShock4(device))
            {
                throw new InvalidOperationException(
                    "Bluetooth speaker passthrough requires a physical Sony DualShock 4.");
            }

            try
            {
                capture = CreateCapture(sourceEndpointId, out string sourceName);
                captureBuffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(CaptureBufferMs),
                    DiscardOnBufferOverflow = true,
                    ReadFully = false,
                };
                capture.DataAvailable += Capture_DataAvailable;
                capture.RecordingStopped += Capture_RecordingStopped;

                ISampleProvider source = ToStereo(captureBuffer.ToSampleProvider());
                sampleProvider = source.WaveFormat.SampleRate == SampleRate ? source :
                    new WdlResamplingSampleProvider(source, SampleRate);
                worker = new Thread(StreamLoop)
                {
                    IsBackground = true,
                    Name = "DualShock 4 Bluetooth speaker audio",
                    Priority = ThreadPriority.AboveNormal,
                };

                capture.StartRecording();
                worker.Start();
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth speaker passthrough started: {sourceName}" +
                    (processor.Enabled ?
                        $" (dynamic range={compression}, bass/body={bassBoost} dB)" :
                        string.Empty),
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
                return new WasapiLoopbackCapture();
            }

            using var enumerator = new MMDeviceEnumerator();
            bool autoDetect = string.Equals(endpointId,
                DualSenseAudioPassthrough.AutoDetectGameAudioEndpointId,
                StringComparison.Ordinal);
            MMDevice endpoint = null;
            if (!autoDetect)
            {
                try
                {
                    endpoint = enumerator.GetDevice(endpointId);
                    if (endpoint?.State != DeviceState.Active)
                    {
                        endpoint?.Dispose();
                        endpoint = null;
                    }
                }
                catch (COMException)
                {
                    endpoint = null;
                }
            }

            if (endpoint == null)
            {
                endpoint = DualSenseAudioPassthrough.FindActiveGameAudioEndpoint(
                    enumerator, autoDetect ? null : endpointId);
            }

            if (endpoint == null)
            {
                throw new InvalidOperationException(autoDetect ?
                    "Controller / game audio endpoint is not available." :
                    "Selected speaker audio source is not available.");
            }

            sourceName = endpoint.FriendlyName;
            return new WasapiLoopbackCapture(endpoint);
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

        private static bool IsGenuineBluetoothDualShock4(DS4Device candidate)
        {
            if (candidate?.ConnectionType != ConnectionType.BT ||
                candidate.HidDevice?.Attributes == null ||
                candidate.HidDevice.Attributes.VendorId != DS4Devices.SONY_VID)
            {
                return false;
            }

            int productId = candidate.HidDevice.Attributes.ProductId;
            return productId == 0x05C4 || productId == 0x09CC;
        }

        private void Capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            lock (syncRoot)
            {
                if (!stopping)
                {
                    captureBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    captureAvailable.Set();
                }
            }
        }

        private void Capture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            if (!stopping && e.Exception != null)
            {
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth speaker capture stopped: {e.Exception.Message}",
                    true);
            }
        }

        private void StreamLoop()
        {
            timeBeginPeriod(1);
            long cadenceTicks = (long)(Stopwatch.Frequency *
                ReportCadenceMilliseconds / 1000.0);
            long nextTick = Stopwatch.GetTimestamp();
            try
            {
                captureAvailable.WaitOne(40);
                while (!stopping)
                {
                    Array.Clear(reportSamples, 0, reportSamples.Length);
                    int samplesRead;
                    lock (syncRoot)
                    {
                        samplesRead = stopping || sampleProvider == null ? 0 :
                            sampleProvider.Read(reportSamples, 0, reportSamples.Length);
                    }

                    bool audible = HasAudibleSamples(reportSamples, samplesRead);
                    if (audible)
                    {
                        lastAudibleUtc = DateTime.UtcNow;
                    }

                    bool recentlyAudible = lastAudibleUtc != DateTime.MinValue &&
                        DateTime.UtcNow - lastAudibleUtc <=
                        TimeSpan.FromMilliseconds(IdleKeepAliveMs);
                    if (recentlyAudible)
                    {
                        processor.Process(reportSamples, SamplesPerReport);
                        SendReport();
                    }

                    nextTick += cadenceTicks;
                    long now = Stopwatch.GetTimestamp();
                    if (nextTick <= now)
                    {
                        nextTick = now;
                        continue;
                    }

                    WaitUntil(nextTick);
                }
            }
            finally
            {
                timeEndPeriod(1);
            }
        }

        private void SendReport()
        {
            byte[] first = EncodeFrame(0);
            byte[] second = EncodeFrame(SamplesPerSbcFrame);
            if (first == null || second == null ||
                first.Length != DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength ||
                second.Length != DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength)
            {
                if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        "DualShock 4 Bluetooth speaker SBC encoder returned an invalid frame.",
                        true);
                }
                return;
            }

            byte[] report = DualShock4BluetoothAudioProtocol.BuildSpeakerReport(
                frameNumber, first, second);
            frameNumber += DualShock4BluetoothAudioProtocol.SpeakerFramesPerReport;
            if (!device.WriteBluetoothAudioOutputReport(report) &&
                Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
            {
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth speaker write failed: {device.LastBluetoothAudioWriteStatus}",
                    true);
            }
        }

        private byte[] EncodeFrame(int sourceFrameOffset)
        {
            for (int frame = 0; frame < SamplesPerSbcFrame; frame++)
            {
                int source = (sourceFrameOffset + frame) * Channels;
                pcmLeft[frame] = FloatToPcm16(reportSamples[source]);
                pcmRight[frame] = FloatToPcm16(reportSamples[source + 1]);
            }

            return encoder.Encode(pcmLeft, pcmRight, frameConfiguration);
        }

        private static short FloatToPcm16(float value)
        {
            return (short)Math.Clamp((int)Math.Round(
                Math.Clamp(value, -1.0f, 1.0f) * short.MaxValue),
                short.MinValue, short.MaxValue);
        }

        private static bool HasAudibleSamples(float[] samples, int count)
        {
            int length = Math.Min(count, samples.Length);
            for (int index = 0; index < length; index++)
            {
                if (Math.Abs(samples[index]) > 0.0001f)
                {
                    return true;
                }
            }

            return false;
        }

        private void WaitUntil(long timestamp)
        {
            while (!stopping)
            {
                long remainingTicks = timestamp - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return;
                }

                int remainingMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                if (remainingMs > 1)
                {
                    if (stoppingSignal.WaitOne(remainingMs - 1))
                    {
                        return;
                    }
                }
                else
                {
                    Thread.SpinWait(64);
                }
            }
        }

        public void Dispose()
        {
            stopping = true;
            stoppingSignal.Set();
            captureAvailable.Set();
            if (worker != null && worker.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != worker.ManagedThreadId)
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
                sampleProvider = null;
            }

            if (oldCapture != null)
            {
                oldCapture.DataAvailable -= Capture_DataAvailable;
                oldCapture.RecordingStopped -= Capture_RecordingStopped;
                try
                {
                    oldCapture.StopRecording();
                }
                catch
                {
                }
                oldCapture.Dispose();
            }
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint milliseconds);
    }
}
