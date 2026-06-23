using Concentus;
using Concentus.Enums;
using DS4Windows.InputDevices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Diagnostics;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Mirrors a Windows render endpoint to a physical Bluetooth DualSense
    /// speaker. Before VIIPER activates combined Bluetooth output, frames use
    /// the 0x35 / packet 0x13 Opus lane. Once combined mode is active, frames
    /// are cached for the next vDS-style 0x36 haptics/state write.
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
        private const int OpusBytes = 200;
        private const int ReportLength = 334;
        private const int CaptureBufferMilliseconds = 240;
        private const int InitialBufferMilliseconds = 24;
        private const int TargetBufferMilliseconds = 20;
        private const int BufferDeadbandMilliseconds = 5;
        private const int DriftAdjustmentFrames = 4;

        private readonly object syncRoot = new object();
        private readonly DualSenseDevice device;
        private readonly string sourceEndpointId;
        private readonly byte speakerVolume;
        private readonly float[] frame = new float[FrameSamples * Channels];
        private readonly float[] sourceFrame = new float[(512 + DriftAdjustmentFrames) * Channels];
        private readonly byte[] opusFrame = new byte[OpusBytes];
        private readonly byte[] report = new byte[ReportLength];

        private WasapiLoopbackCapture capture;
        private BufferedWaveProvider captureBuffer;
        private Thread worker;
        private IOpusEncoder opusEncoder;
        private volatile bool stopping;
        private int reportSequence;
        private byte packetCounter;
        private int loggedWriteFailure;
        private long framesSent;
        private long shortCaptureReads;
        private long skippedScheduleSlots;
        private long captureDriftAdjustments;
        private long lastDiagnosticUtcTicks;

        public DualSenseBluetoothSpeakerPassthrough(DualSenseDevice device, byte speakerVolume,
            string sourceEndpointId)
        {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            this.speakerVolume = speakerVolume;
            this.sourceEndpointId = sourceEndpointId ?? string.Empty;
        }

        public bool Matches(DualSenseDevice candidateDevice, byte candidateVolume,
            string candidateSourceEndpointId)
        {
            return !stopping && ReferenceEquals(device, candidateDevice) &&
                speakerVolume == candidateVolume &&
                string.Equals(sourceEndpointId, candidateSourceEndpointId ?? string.Empty,
                    StringComparison.Ordinal);
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
                captureBuffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(CaptureBufferMilliseconds),
                    DiscardOnBufferOverflow = true,
                    ReadFully = false,
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
                    Priority = ThreadPriority.Highest,
                };
                capture.StartRecording();
                worker.Start();
                AppLogger.LogToGui($"DualSense Bluetooth speaker passthrough started: {sourceName}", false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static WasapiLoopbackCapture CreateCapture(string endpointId, out string sourceName)
        {
            if (string.IsNullOrEmpty(endpointId))
            {
                sourceName = "Default audio endpoint";
                return new WasapiLoopbackCapture();
            }

            using var enumerator = new MMDeviceEnumerator();
            MMDevice endpoint = enumerator.GetDevice(endpointId);
            if (endpoint == null || endpoint.State != DeviceState.Active)
            {
                throw new InvalidOperationException("Selected Bluetooth speaker audio source is not available.");
            }

            sourceName = endpoint.FriendlyName;
            return new WasapiLoopbackCapture(endpoint);
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
            const double frameDurationMs = 10.0 + (2.0 / 3.0);
            WaitForInitialCaptureBuffer();
            double frameTicks = Stopwatch.Frequency * frameDurationMs / 1000.0;
            double nextFrame = Stopwatch.GetTimestamp() + frameTicks;

            while (!stopping)
            {
                Array.Clear(sourceFrame, 0, sourceFrame.Length);
                Array.Clear(frame, 0, frame.Length);
                int sourceFramesPerTick = GetSourceFramesPerTick();
                int requestedSamples = sourceFramesPerTick * Channels;
                int samplesRead;
                lock (syncRoot)
                {
                    samplesRead = stopping ? 0 : source.Read(sourceFrame, 0, requestedSamples);
                }

                if (samplesRead < requestedSamples)
                {
                    Interlocked.Increment(ref shortCaptureReads);
                }

                if (samplesRead > 0)
                {
                    float volume = speakerVolume / 255.0f;
                    for (int i = 0; i < samplesRead; i++)
                    {
                        sourceFrame[i] = Math.Clamp(sourceFrame[i] * volume, -1.0f, 1.0f);
                    }
                }

                // SAxense, PadForge, and DS5Dongle converge on this firmware
                // cadence: consume approximately 512 frames every 10.667 ms,
                // then compress them into one 480-sample Opus frame. A small
                // drift trim preserves the capture cushion without adding
                // reports or changing the firmware-facing cadence.
                double step = sourceFramesPerTick / (double)FrameSamples;
                for (int outputFrame = 0; outputFrame < FrameSamples; outputFrame++)
                {
                    double position = outputFrame * step;
                    int first = (int)position;
                    int second = Math.Min(first + 1, sourceFramesPerTick - 1);
                    double fraction = position - first;
                    int outputOffset = outputFrame * Channels;
                    int firstOffset = first * Channels;
                    int secondOffset = second * Channels;
                    frame[outputOffset] = (float)(sourceFrame[firstOffset] * (1.0 - fraction) + sourceFrame[secondOffset] * fraction);
                    frame[outputOffset + 1] = (float)(sourceFrame[firstOffset + 1] * (1.0 - fraction) + sourceFrame[secondOffset + 1] * fraction);
                }

                SendFrame();
                Interlocked.Increment(ref framesSent);
                LogStreamDiagnosticsIfVerbose();

                nextFrame += frameTicks;
                double remainingTicks = nextFrame - Stopwatch.GetTimestamp();
                if (remainingTicks > 0)
                {
                    int sleepMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                    if (sleepMs > 1)
                    {
                        Thread.Sleep(sleepMs - 1);
                    }

                    while (!stopping && Stopwatch.GetTimestamp() < nextFrame)
                    {
                        Thread.SpinWait(64);
                    }
                }
                else
                {
                    // Never emit catch-up bursts. Missing one audio slot is
                    // preferable to overflowing the controller receive queue.
                    Interlocked.Increment(ref skippedScheduleSlots);
                    nextFrame = Stopwatch.GetTimestamp() + frameTicks;
                }
            }
        }

        private int GetSourceFramesPerTick()
        {
            const int baseFrames = 512;
            lock (syncRoot)
            {
                WaveFormat captureFormat = capture?.WaveFormat;
                if (captureBuffer == null || captureFormat == null || captureFormat.BlockAlign <= 0)
                {
                    return baseFrames;
                }

                int bufferedFrames = captureBuffer.BufferedBytes / captureFormat.BlockAlign;
                int targetFrames = (captureFormat.SampleRate * TargetBufferMilliseconds) / 1000;
                int deadbandFrames = (captureFormat.SampleRate * BufferDeadbandMilliseconds) / 1000;
                if (bufferedFrames > targetFrames + deadbandFrames)
                {
                    Interlocked.Increment(ref captureDriftAdjustments);
                    return baseFrames + DriftAdjustmentFrames;
                }

                if (bufferedFrames < targetFrames - deadbandFrames)
                {
                    Interlocked.Increment(ref captureDriftAdjustments);
                    return baseFrames - DriftAdjustmentFrames;
                }
            }

            return baseFrames;
        }

        private void WaitForInitialCaptureBuffer()
        {
            int minimumBytes;
            lock (syncRoot)
            {
                minimumBytes = capture?.WaveFormat == null ? 0 :
                    (capture.WaveFormat.AverageBytesPerSecond * InitialBufferMilliseconds) / 1000;
            }

            if (minimumBytes <= 0)
            {
                return;
            }

            long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 2);
            while (!stopping && Stopwatch.GetTimestamp() < deadline)
            {
                lock (syncRoot)
                {
                    if (captureBuffer?.BufferedBytes >= minimumBytes)
                    {
                        return;
                    }
                }

                Thread.Sleep(2);
            }
        }

        private void LogStreamDiagnosticsIfVerbose()
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            long previous = Interlocked.Read(ref lastDiagnosticUtcTicks);
            if (previous != 0 && now - previous < TimeSpan.FromSeconds(5).Ticks)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref lastDiagnosticUtcTicks, now, previous) != previous)
            {
                return;
            }

            AppLogger.LogToGui($"DualSense Bluetooth speaker stats: frames={Interlocked.Read(ref framesSent)} shortCaptureReads={Interlocked.Read(ref shortCaptureReads)} driftAdjustments={Interlocked.Read(ref captureDriftAdjustments)} skippedSlots={Interlocked.Read(ref skippedScheduleSlots)} queued={device.PendingBluetoothSpeakerFrames} queueDrops={device.BluetoothSpeakerFramesDropped} queueUnderruns={device.BluetoothSpeakerFramesUnderrun} combinedReports={device.BluetoothCombinedOutputReportCount} combinedLate={device.BluetoothCombinedOutputLateReportCount} combinedMaxGapMs={device.BluetoothCombinedOutputMaxGapMilliseconds:F1} speakerWrites={device.BluetoothCombinedSpeakerReportsWritten} speakerWriteFailures={device.BluetoothCombinedSpeakerWriteFailures} suppressed0x31={device.BluetoothNormalOutputWritesSuppressed} status={device.LastBluetoothHapticsWriteStatus}", false);
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

            if (encoded <= 0 || encoded > OpusBytes)
            {
                if (Interlocked.Exchange(ref loggedWriteFailure, 1) == 0)
                {
                    AppLogger.LogToGui($"DualSense Bluetooth speaker encoder returned an invalid frame length: {encoded}.", true);
                }

                return;
            }

            // The Bluetooth speaker lane requires a fixed 200-byte CBR Opus
            // payload. A short encoder result is not a valid speaker frame.
            if (encoded < OpusBytes)
            {
                Array.Clear(opusFrame, encoded, OpusBytes - encoded);
            }

            if (stopping)
            {
                return;
            }

            if (device.BluetoothCombinedOutputTransportEnabled)
            {
                device.SetBluetoothSpeakerAudioFrame(opusFrame, encoded);
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
            WasapiLoopbackCapture oldCapture;
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

            device.ClearBluetoothSpeakerAudioFrame();
        }
    }
}
