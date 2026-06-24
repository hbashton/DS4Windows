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
        private const int InitialBufferMilliseconds = 48;
        private const int TargetBufferMilliseconds = 32;
        private const int BufferDeadbandMilliseconds = 8;
        private const int DriftAdjustmentFrames = 4;
        private const int CaptureRingFrames = (SampleRate * CaptureBufferMilliseconds) / 1000;
        private const int CapturePumpBufferFrames = 2048;

        private readonly object syncRoot = new object();
        private readonly DualSenseDevice device;
        private readonly string sourceEndpointId;
        private readonly byte speakerVolume;
        private readonly float[] frame = new float[FrameSamples * Channels];
        private readonly float[] sourceFrame = new float[(512 + DriftAdjustmentFrames) * Channels];
        private readonly byte[] opusFrame = new byte[OpusBytes];
        private readonly byte[] report = new byte[ReportLength];
        private readonly float[] captureRing = new float[CaptureRingFrames * Channels];
        private readonly AutoResetEvent captureDataAvailable = new AutoResetEvent(false);
        private readonly AutoResetEvent captureFramesAvailable = new AutoResetEvent(false);

        private WasapiCapture capture;
        private BufferedWaveProvider captureBuffer;
        private Thread worker;
        private Thread capturePump;
        private IOpusEncoder opusEncoder;
        private volatile bool stopping;
        private int captureRingReadIndex;
        private int captureRingWriteIndex;
        private int captureRingBufferedFrames;
        private bool capturePrimed;
        private bool fadeInAfterCaptureUnderrun;
        private float previousOutputLeft;
        private float previousOutputRight;
        private int reportSequence;
        private byte packetCounter;
        private int loggedWriteFailure;
        private long framesSent;
        private long shortCaptureReads;
        private long emptyCaptureReads;
        private long concealedCaptureFrames;
        private long skippedScheduleSlots;
        private long captureDriftAdjustments;
        private long captureStartedTicks;
        private long captureCallbackCount;
        private long captureInputFrames;
        private long lastCaptureCallbackTicks;
        private long captureCallbackGapsOverTwelveMilliseconds;
        private long captureMaximumCallbackGapTicks;
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

                // The Bluetooth transport itself dominates latency. Use Opus's
                // program-audio tuning here: PadForge and the standalone BT
                // speaker reference both use it, and it preserves speech better
                // than the restricted low-delay mode at this fixed 10 ms cadence.
                opusEncoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels,
                    OpusApplication.OPUS_APPLICATION_AUDIO);
                opusEncoder.Bitrate = OpusBytes * 8 * 100;
                opusEncoder.UseVBR = false;
                opusEncoder.ExpertFrameDuration = OpusFramesize.OPUS_FRAMESIZE_10_MS;

                capturePump = new Thread(() => CapturePumpLoop(source))
                {
                    IsBackground = true,
                    Name = "DualSense Bluetooth speaker capture",
                    Priority = ThreadPriority.AboveNormal,
                };
                worker = new Thread(StreamLoop)
                {
                    IsBackground = true,
                    Name = "DualSense Bluetooth speaker audio",
                    Priority = ThreadPriority.Highest,
                };
                Interlocked.Exchange(ref captureStartedTicks, Stopwatch.GetTimestamp());
                capture.StartRecording();
                capturePump.Start();
                worker.Start();
                AppLogger.LogToGui($"DualSense Bluetooth speaker passthrough started: {sourceName}", false);
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
                using var defaultEnumerator = new MMDeviceEnumerator();
                return new StableLoopbackCapture(defaultEnumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render, Role.Multimedia));
            }

            using var endpointEnumerator = new MMDeviceEnumerator();
            MMDevice endpoint = endpointEnumerator.GetDevice(endpointId);
            if (endpoint == null || endpoint.State != DeviceState.Active)
            {
                throw new InvalidOperationException("Selected Bluetooth speaker audio source is not available.");
            }

            sourceName = endpoint.FriendlyName;
            return new StableLoopbackCapture(endpoint);
        }

        /// <summary>
        /// A 10 ms WASAPI buffer follows the Windows audio engine cadence without
        /// the 100 ms default capture latency or the unstable 1 ms request that
        /// caused frequent partial callbacks on the virtual controller endpoint.
        /// </summary>
        private sealed class StableLoopbackCapture : WasapiCapture
        {
            public StableLoopbackCapture(MMDevice captureDevice)
                : base(captureDevice, false, 10)
            {
                ShareMode = AudioClientShareMode.Shared;
            }

            protected override AudioClientStreamFlags GetAudioClientStreamFlags()
            {
                return AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
            }
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
            RecordCaptureCallback(e.BytesRecorded);
            lock (syncRoot)
            {
                if (!stopping)
                {
                    captureBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    captureDataAvailable.Set();
                }
            }
        }

        private void RecordCaptureCallback(int bytesRecorded)
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref lastCaptureCallbackTicks, now);
            Interlocked.Increment(ref captureCallbackCount);
            if (capture?.WaveFormat?.BlockAlign is int blockAlign && blockAlign > 0)
            {
                Interlocked.Add(ref captureInputFrames, bytesRecorded / blockAlign);
            }

            if (previous == 0)
            {
                return;
            }

            long elapsedTicks = now - previous;
            if (elapsedTicks > Stopwatch.Frequency * 12 / 1000)
            {
                Interlocked.Increment(ref captureCallbackGapsOverTwelveMilliseconds);
            }

            UpdateMaximum(ref captureMaximumCallbackGapTicks, elapsedTicks);
        }

        private static void UpdateMaximum(ref long destination, long candidate)
        {
            long current;
            while (candidate > (current = Interlocked.Read(ref destination)) &&
                Interlocked.CompareExchange(ref destination, candidate, current) != current)
            {
            }
        }

        private void Capture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            if (!stopping && e.Exception != null)
            {
                AppLogger.LogToGui($"DualSense Bluetooth speaker capture stopped: {e.Exception.Message}", true);
            }
        }

        private void CapturePumpLoop(ISampleProvider source)
        {
            float[] buffer = new float[CapturePumpBufferFrames * Channels];
            while (!stopping)
            {
                captureDataAvailable.WaitOne(10);
                while (!stopping)
                {
                    int samplesRead;
                    lock (syncRoot)
                    {
                        samplesRead = stopping ? 0 : source.Read(buffer, 0, buffer.Length);
                    }

                    if (samplesRead <= 0)
                    {
                        break;
                    }

                    AppendCaptureSamples(buffer, samplesRead);
                }
            }
        }

        private void AppendCaptureSamples(float[] samples, int sampleCount)
        {
            int frames = Math.Min(sampleCount / Channels, CaptureRingFrames);
            if (frames <= 0)
            {
                return;
            }

            int sourceFrameOffset = (sampleCount / Channels) - frames;
            lock (syncRoot)
            {
                for (int frameIndex = 0; frameIndex < frames; frameIndex++)
                {
                    if (captureRingBufferedFrames == CaptureRingFrames)
                    {
                        captureRingReadIndex = (captureRingReadIndex + 1) % CaptureRingFrames;
                        captureRingBufferedFrames--;
                    }

                    int sourceOffset = (sourceFrameOffset + frameIndex) * Channels;
                    int destinationOffset = captureRingWriteIndex * Channels;
                    captureRing[destinationOffset] = samples[sourceOffset];
                    captureRing[destinationOffset + 1] = samples[sourceOffset + 1];
                    captureRingWriteIndex = (captureRingWriteIndex + 1) % CaptureRingFrames;
                    captureRingBufferedFrames++;
                }
            }

            captureFramesAvailable.Set();
        }

        private int ReadCaptureFrames(int frameCount)
        {
            lock (syncRoot)
            {
                // Do not start consuming an endpoint in the middle of an audio
                // callback. Keeping a small cushion means one late callback does
                // not splice held samples into speech and make it sound robotic.
                if (!capturePrimed)
                {
                    int initialFrames = (SampleRate * InitialBufferMilliseconds) / 1000;
                    if (captureRingBufferedFrames < initialFrames)
                    {
                        return 0;
                    }

                    capturePrimed = true;
                }

                if (captureRingBufferedFrames < frameCount)
                {
                    return 0;
                }

                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    int sourceOffset = captureRingReadIndex * Channels;
                    int destinationOffset = frameIndex * Channels;
                    sourceFrame[destinationOffset] = captureRing[sourceOffset];
                    sourceFrame[destinationOffset + 1] = captureRing[sourceOffset + 1];
                    captureRingReadIndex = (captureRingReadIndex + 1) % CaptureRingFrames;
                }

                captureRingBufferedFrames -= frameCount;
                return frameCount;
            }
        }

        private void RecordCaptureUnderrun()
        {
            // ReadCaptureFrames returns either one complete packet or no packet.
            // sourceFrame is cleared before every read, so an underflow emits a
            // clean silent packet while the ring re-primes instead of repeating
            // stale waveform fragments into the next spoken word.
            Interlocked.Increment(ref emptyCaptureReads);
        }

        private void StreamLoop()
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
                int capturedFrames = ReadCaptureFrames(sourceFramesPerTick);
                bool captureUnderrun = capturedFrames < sourceFramesPerTick;
                if (capturedFrames < sourceFramesPerTick)
                {
                    Interlocked.Increment(ref shortCaptureReads);
                    Interlocked.Add(ref concealedCaptureFrames,
                        sourceFramesPerTick - capturedFrames);
                    RecordCaptureUnderrun();
                }

                if (stopping)
                {
                    break;
                }

                // Combined 0x36 packets carry the firmware speaker-volume
                // byte. Keep PCM at full scale there; applying the profile
                // gain a second time needlessly attenuates the program mix.
                float volume = device.BluetoothCombinedOutputTransportEnabled ?
                    1.0f : speakerVolume / 255.0f;
                for (int i = 0; i < sourceFramesPerTick * Channels; i++)
                {
                    sourceFrame[i] = Math.Clamp(sourceFrame[i] * volume, -1.0f, 1.0f);
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

                if (captureUnderrun)
                {
                    FadeOutCaptureUnderrun();
                    fadeInAfterCaptureUnderrun = true;
                }
                else if (fadeInAfterCaptureUnderrun)
                {
                    FadeInRecoveredCapture();
                    fadeInAfterCaptureUnderrun = false;
                }

                int tailOffset = (FrameSamples - 1) * Channels;
                previousOutputLeft = frame[tailOffset];
                previousOutputRight = frame[tailOffset + 1];

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

        private void FadeOutCaptureUnderrun()
        {
            const int fadeFrames = 48;
            for (int outputFrame = 0; outputFrame < FrameSamples; outputFrame++)
            {
                float gain = outputFrame < fadeFrames ?
                    1.0f - ((outputFrame + 1) / (float)fadeFrames) : 0.0f;
                int offset = outputFrame * Channels;
                frame[offset] = previousOutputLeft * gain;
                frame[offset + 1] = previousOutputRight * gain;
            }
        }

        private void FadeInRecoveredCapture()
        {
            const int fadeFrames = 48;
            for (int outputFrame = 0; outputFrame < fadeFrames; outputFrame++)
            {
                float gain = (outputFrame + 1) / (float)fadeFrames;
                int offset = outputFrame * Channels;
                frame[offset] = previousOutputLeft * (1.0f - gain) + frame[offset] * gain;
                frame[offset + 1] = previousOutputRight * (1.0f - gain) + frame[offset + 1] * gain;
            }
        }

        private int GetSourceFramesPerTick()
        {
            const int baseFrames = 512;
            lock (syncRoot)
            {
                WaveFormat captureFormat = capture?.WaveFormat;
                if (captureBuffer == null || captureFormat == null)
                {
                    return baseFrames;
                }

                int bufferedFrames = captureRingBufferedFrames;
                int targetFrames = (SampleRate * TargetBufferMilliseconds) / 1000;
                int deadbandFrames = (SampleRate * BufferDeadbandMilliseconds) / 1000;
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
            int minimumFrames = (SampleRate * InitialBufferMilliseconds) / 1000;
            long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 2);
            while (!stopping && Stopwatch.GetTimestamp() < deadline)
            {
                lock (syncRoot)
                {
                    if (captureRingBufferedFrames >= minimumFrames)
                    {
                        return;
                    }
                }

                captureFramesAvailable.WaitOne(2);
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

            long elapsedTicks = Stopwatch.GetTimestamp() - Interlocked.Read(ref captureStartedTicks);
            long capturedFrames = Interlocked.Read(ref captureInputFrames);
            double captureInputFramesPerSecond = elapsedTicks > 0 ?
                capturedFrames * (double)Stopwatch.Frequency / elapsedTicks : 0.0;
            double captureMaximumCallbackGapMilliseconds =
                Interlocked.Read(ref captureMaximumCallbackGapTicks) * 1000.0 / Stopwatch.Frequency;

            AppLogger.LogToGui($"DualSense Bluetooth speaker stats: frames={Interlocked.Read(ref framesSent)} shortCaptureReads={Interlocked.Read(ref shortCaptureReads)} emptyCaptureReads={Interlocked.Read(ref emptyCaptureReads)} concealedCaptureFrames={Interlocked.Read(ref concealedCaptureFrames)} captureCallbacks={Interlocked.Read(ref captureCallbackCount)} captureInputFrames={capturedFrames} captureInputFramesPerSecond={captureInputFramesPerSecond:F1} captureCallbacksOver12Ms={Interlocked.Read(ref captureCallbackGapsOverTwelveMilliseconds)} captureMaximumCallbackGapMs={captureMaximumCallbackGapMilliseconds:F1} captureBufferedFrames={GetCaptureBufferedFrames()} capturePrimed={IsCapturePrimed()} driftAdjustments={Interlocked.Read(ref captureDriftAdjustments)} skippedSlots={Interlocked.Read(ref skippedScheduleSlots)} queued={device.PendingBluetoothSpeakerFrames} queueDrops={device.BluetoothSpeakerFramesDropped} queueUnderruns={device.BluetoothSpeakerFramesUnderrun} realtimeQueueDrops={device.BluetoothRealtimeWriterDroppedReports} combinedReports={device.BluetoothCombinedOutputReportCount} combinedLate={device.BluetoothCombinedOutputLateReportCount} combinedMaxGapMs={device.BluetoothCombinedOutputMaxGapMilliseconds:F1} combinedCacheAverageMs={device.BluetoothCombinedSpeakerCacheAverageDelayMilliseconds:F1} combinedCacheMaximumMs={device.BluetoothCombinedSpeakerCacheMaximumDelayMilliseconds:F1} staleHapticsSilenced={device.BluetoothCombinedSpeakerStaleHapticsSilenced} speakerWrites={device.BluetoothCombinedSpeakerReportsWritten} speakerWriteFailures={device.BluetoothCombinedSpeakerWriteFailures} suppressed0x31={device.BluetoothNormalOutputWritesSuppressed} status={device.LastBluetoothHapticsWriteStatus}", false);
        }

        private int GetCaptureBufferedFrames()
        {
            lock (syncRoot)
            {
                return captureRingBufferedFrames;
            }
        }

        private bool IsCapturePrimed()
        {
            lock (syncRoot)
            {
                return capturePrimed;
            }
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
            captureDataAvailable.Set();
            captureFramesAvailable.Set();
            if (worker != null && worker.IsAlive && Thread.CurrentThread.ManagedThreadId != worker.ManagedThreadId)
            {
                worker.Join(500);
            }

            worker = null;
            if (capturePump != null && capturePump.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != capturePump.ManagedThreadId)
            {
                capturePump.Join(500);
            }

            capturePump = null;
            WasapiCapture oldCapture;
            lock (syncRoot)
            {
                oldCapture = capture;
                capture = null;
                captureBuffer = null;
                capturePrimed = false;
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
