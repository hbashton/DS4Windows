using Concentus;
using Concentus.Enums;
using DS4Windows.InputDevices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        private const int SourcePullFrames = 512;
        private const int DriftAdjustmentFrames = 4;
        private const int OpusBytes = 200;
        private const int ReportLength = 334;
        private const int LowLatencyCaptureBufferMs = 10;
        private const int CaptureBufferMs = 240;
        private const int InitialBufferMs = 48;
        private const int TargetBufferMs = 32;
        private const int BufferDeadbandMs = 8;
        private const int CaptureRingFrames = (SampleRate * CaptureBufferMs) / 1000;
        private const int CapturePumpBufferFrames = 2048;
        private const int IdleKeepAliveMs = 2000;
        private const double BluetoothSpeakerCadenceMs = 10.0 + (2.0 / 3.0);

        private readonly object syncRoot = new object();
        private readonly DualSenseDevice device;
        private readonly string sourceEndpointId;
        private readonly byte speakerVolume;
        private readonly float[] sourceFrame = new float[(SourcePullFrames + DriftAdjustmentFrames) * Channels];
        private readonly float[] frame = new float[FrameSamples * Channels];
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
        private bool isGameAudioEndpoint;
        private bool audioSegmentActive;
        private DateTime lastAudibleUtc = DateTime.MinValue;
        private long framesSent;
        private long silentFramesSent;
        private long skippedScheduleSlots;
        private long maximumScheduleLatenessTicks;
        private long activeScheduleMisses;
        private long maximumActiveScheduleLatenessTicks;
        private long audioSegmentStarts;
        private long audioSegmentStops;
        private long captureCallbackCount;
        private long captureInputFrames;
        private long captureUnderruns;
        private long activeCaptureUnderruns;
        private long captureDriftAdjustments;
        private long lastDiagnosticUtcTicks;
        private int diagnosticLogPending;
        private int mmcssRegistered;
        private int mmcssHighPriority;
        private int mmcssRegistrationError;

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
                isGameAudioEndpoint = IsLikelyGameAudioEndpoint(sourceName);
                captureBuffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(CaptureBufferMs),
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
                capture.StartRecording();
                capturePump.Start();
                worker.Start();
                AppLogger.LogToGui(
                    $"DualSense Bluetooth speaker passthrough started: {sourceName}" +
                    (isGameAudioEndpoint ? " (low latency game-audio mode)" : string.Empty),
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
            bool autoDetectGameAudio = string.Equals(endpointId,
                DualSenseAudioPassthrough.AutoDetectGameAudioEndpointId,
                StringComparison.Ordinal);
            MMDevice endpoint = null;

            if (!autoDetectGameAudio)
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
                endpoint = DualSenseAudioPassthrough.FindActiveGameAudioEndpoint(enumerator,
                    autoDetectGameAudio ? null : endpointId);
            }

            if (endpoint == null)
            {
                throw new InvalidOperationException(autoDetectGameAudio ?
                    "DualSense / game audio endpoint is not available." :
                    "Selected Bluetooth speaker audio source is not available and no active DualSense / game audio replacement was found.");
            }

            if (!autoDetectGameAudio && !string.Equals(endpoint.ID, endpointId, StringComparison.Ordinal))
            {
                AppLogger.LogToGui(
                    $"Selected Bluetooth speaker audio source was recreated; rebound to {endpoint.FriendlyName}.",
                    false);
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
                    int blockAlign = capture?.WaveFormat?.BlockAlign ?? 0;
                    if (blockAlign > 0)
                    {
                        Interlocked.Add(ref captureInputFrames, e.BytesRecorded / blockAlign);
                    }

                    Interlocked.Increment(ref captureCallbackCount);
                    captureDataAvailable.Set();
                }
            }
        }

        private void Capture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            device.ClearBluetoothSpeakerAudioFrame();
            audioSegmentActive = false;
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
                if (!capturePrimed)
                {
                    int initialFrames = (SampleRate * InitialBufferMs) / 1000;
                    if (captureRingBufferedFrames < initialFrames)
                    {
                        return 0;
                    }

                    capturePrimed = true;
                }

                if (captureRingBufferedFrames < frameCount)
                {
                    capturePrimed = false;
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

        private int GetSourceFramesPerTick()
        {
            lock (syncRoot)
            {
                int targetFrames = (SampleRate * TargetBufferMs) / 1000;
                int deadbandFrames = (SampleRate * BufferDeadbandMs) / 1000;
                if (captureRingBufferedFrames > targetFrames + deadbandFrames)
                {
                    Interlocked.Increment(ref captureDriftAdjustments);
                    return SourcePullFrames + DriftAdjustmentFrames;
                }

                if (captureRingBufferedFrames < targetFrames - deadbandFrames)
                {
                    Interlocked.Increment(ref captureDriftAdjustments);
                    return SourcePullFrames - DriftAdjustmentFrames;
                }
            }

            return SourcePullFrames;
        }

        private void WaitForInitialCaptureBuffer()
        {
            int minimumFrames = (SampleRate * InitialBufferMs) / 1000;
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

        private void StreamLoop()
        {
            timeBeginPeriod(1);
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            IntPtr highResolutionTimer = CreateHighResolutionTimer();
            long nextTick = DateTime.UtcNow.Ticks;
            long cadenceTicks = (long)(BluetoothSpeakerCadenceMs * TimeSpan.TicksPerMillisecond);
            try
            {
                WaitForInitialCaptureBuffer();
                nextTick = DateTime.UtcNow.Ticks;
                while (!stopping)
                {
                    Array.Clear(sourceFrame, 0, sourceFrame.Length);
                    Array.Clear(frame, 0, frame.Length);
                    int sourceFrames = GetSourceFramesPerTick();
                    bool wasAudioSegmentActive = audioSegmentActive;
                    int capturedFrames = ReadCaptureFrames(sourceFrames);
                    bool captureUnderrun = capturedFrames < sourceFrames;
                    if (captureUnderrun)
                    {
                        Interlocked.Increment(ref captureUnderruns);
                        if (wasAudioSegmentActive)
                        {
                            Interlocked.Increment(ref activeCaptureUnderruns);
                        }
                    }

                    bool audible = capturedFrames > 0 &&
                        HasAudibleSamples(sourceFrame, capturedFrames * Channels);
                    if (audible)
                    {
                        lastAudibleUtc = DateTime.UtcNow;
                        if (!audioSegmentActive)
                        {
                            audioSegmentActive = true;
                            Interlocked.Increment(ref audioSegmentStarts);
                        }
                    }

                    FillOutputFrame(capturedFrames);
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

                    // Combined reports already carry the firmware speaker
                    // volume. Applying the profile gain to PCM as well would
                    // attenuate the stream twice.
                    float volume = device.BluetoothCombinedOutputTransportEnabled ?
                        1.0f : speakerVolume / 255.0f;
                    for (int i = 0; i < frame.Length; i++)
                    {
                        frame[i] = Math.Clamp(frame[i] * volume, -1.0f, 1.0f);
                    }

                    int tailOffset = (FrameSamples - 1) * Channels;
                    previousOutputLeft = frame[tailOffset];
                    previousOutputRight = frame[tailOffset + 1];

                    bool recentlyAudible = lastAudibleUtc != DateTime.MinValue &&
                        DateTime.UtcNow - lastAudibleUtc <= TimeSpan.FromMilliseconds(IdleKeepAliveMs);
                    bool submittedFrameThisTick = false;
                    if (audioSegmentActive && recentlyAudible)
                    {
                        SendFrame();
                        submittedFrameThisTick = true;
                        Interlocked.Increment(ref framesSent);
                        if (!audible)
                        {
                            // This is a real CBR Opus silence frame inside the
                            // shared 0x36 stream, not an empty speaker TLV.
                            Interlocked.Increment(ref silentFramesSent);
                        }
                    }
                    else if (audioSegmentActive)
                    {
                        device.ClearBluetoothSpeakerAudioFrame();
                        audioSegmentActive = false;
                        Interlocked.Increment(ref audioSegmentStops);
                    }

                    nextTick += cadenceTicks;
                    long nowTicks = DateTime.UtcNow.Ticks;
                    if (nextTick <= nowTicks)
                    {
                        long lateness = nowTicks - nextTick;
                        Interlocked.Increment(ref skippedScheduleSlots);
                        UpdateMaximum(ref maximumScheduleLatenessTicks, lateness);
                        if (submittedFrameThisTick)
                        {
                            Interlocked.Increment(ref activeScheduleMisses);
                            UpdateMaximum(ref maximumActiveScheduleLatenessTicks,
                                lateness);
                        }
                        nextTick = nowTicks + cadenceTicks;
                        LogStreamDiagnosticsIfVerbose();
                        continue;
                    }

                    LogStreamDiagnosticsIfVerbose();
                    WaitUntil(highResolutionTimer, nextTick);
                }
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                {
                    AvRevertMmThreadCharacteristics(mmcssHandle);
                }

                if (highResolutionTimer != IntPtr.Zero)
                {
                    CloseHandle(highResolutionTimer);
                }

                timeEndPeriod(1);
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

            if (Interlocked.CompareExchange(ref diagnosticLogPending, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AppLogger.LogToGui(
                        $"DualSense Bluetooth combined stream stats: frames={Interlocked.Read(ref framesSent)} " +
                        $"silentFrames={Interlocked.Read(ref silentFramesSent)} " +
                        $"scheduleMisses={Interlocked.Read(ref skippedScheduleSlots)} " +
                        $"maximumScheduleLatenessMs={Interlocked.Read(ref maximumScheduleLatenessTicks) / (double)TimeSpan.TicksPerMillisecond:F1} " +
                        $"activeScheduleMisses={Interlocked.Read(ref activeScheduleMisses)} " +
                        $"maximumActiveScheduleLatenessMs={Interlocked.Read(ref maximumActiveScheduleLatenessTicks) / (double)TimeSpan.TicksPerMillisecond:F1} " +
                        $"segmentStarts={Interlocked.Read(ref audioSegmentStarts)} " +
                        $"segmentStops={Interlocked.Read(ref audioSegmentStops)} " +
                        $"captureCallbacks={Interlocked.Read(ref captureCallbackCount)} " +
                        $"captureInputFrames={Interlocked.Read(ref captureInputFrames)} " +
                        $"captureBufferedFrames={GetCaptureBufferedFrames()} " +
                        $"capturePrimed={IsCapturePrimed()} " +
                        $"captureUnderruns={Interlocked.Read(ref captureUnderruns)} " +
                        $"activeCaptureUnderruns={Interlocked.Read(ref activeCaptureUnderruns)} " +
                        $"driftAdjustments={Interlocked.Read(ref captureDriftAdjustments)} " +
                        $"mmcssRegistered={Volatile.Read(ref mmcssRegistered) != 0} " +
                        $"mmcssHighPriority={Volatile.Read(ref mmcssHighPriority) != 0} " +
                        $"mmcssError={Volatile.Read(ref mmcssRegistrationError)} " +
                        $"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} " +
                        $"queuedFrames={device.PendingBluetoothSpeakerFrames} " +
                        $"queueDrops={device.BluetoothSpeakerFramesDropped} " +
                        $"writerDrops={device.BluetoothRealtimeWriterDroppedReports} " +
                        $"writerCompletions={device.BluetoothRealtimeWriterCompletedReports} " +
                        $"writerSlowCompletions={device.BluetoothRealtimeWriterSlowCompletionCount} " +
                        $"writerMaximumCompletionMs={device.BluetoothRealtimeWriterMaximumCompletionMilliseconds:F1} " +
                        $"writerLateSubmissions={device.BluetoothRealtimeWriterLateSubmissionCount} " +
                        $"writerMaximumSubmissionGapMs={device.BluetoothRealtimeWriterMaximumSubmissionGapMilliseconds:F1} " +
                        $"speakerWrites={device.BluetoothCombinedSpeakerReportsWritten} " +
                        $"speakerWriteFailures={device.BluetoothCombinedSpeakerWriteFailures} " +
                        $"hapticsPairedWrites={device.BluetoothCombinedHapticsPairedWrites} " +
                        $"speakerFallbackWrites={device.BluetoothCombinedSpeakerFallbackWrites} " +
                        $"staleHapticsSilenced={device.BluetoothCombinedSpeakerStaleHapticsSilenced} " +
                        $"status={device.LastBluetoothHapticsWriteStatus}",
                        false);
                }
                finally
                {
                    Volatile.Write(ref diagnosticLogPending, 0);
                }
            });
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

        private static void UpdateMaximum(ref long target, long value)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref target);
                if (value <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, current) != current);
        }

        private void FillOutputFrame(int sourceFrames)
        {
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

        private IntPtr RegisterMultimediaScheduler()
        {
            try
            {
                uint taskIndex = 0;
                IntPtr handle = AvSetMmThreadCharacteristicsW("Pro Audio",
                    ref taskIndex);
                if (handle == IntPtr.Zero)
                {
                    Volatile.Write(ref mmcssRegistrationError,
                        Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }

                Volatile.Write(ref mmcssRegistered, 1);
                if (AvSetMmThreadPriority(handle, AvrtPriority.High))
                {
                    Volatile.Write(ref mmcssHighPriority, 1);
                }
                else
                {
                    Volatile.Write(ref mmcssRegistrationError,
                        Marshal.GetLastWin32Error());
                }

                return handle;
            }
            catch (DllNotFoundException)
            {
                Volatile.Write(ref mmcssRegistrationError, -1);
                return IntPtr.Zero;
            }
            catch (EntryPointNotFoundException)
            {
                Volatile.Write(ref mmcssRegistrationError, -2);
                return IntPtr.Zero;
            }
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

        private enum AvrtPriority
        {
            Normal = 0,
            High = 1,
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uMilliseconds);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName,
            ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle,
            AvrtPriority priority);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

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

            // vDS uses a fixed 200-byte CBR frame in the 0x36 speaker block.
            // Do not pad a short frame with zeros: that changes its Opus packet
            // structure and can make the controller reject the combined report.
            if (encoded != OpusBytes)
            {
                if (Interlocked.Exchange(ref loggedWriteFailure, 1) == 0)
                {
                    AppLogger.LogToGui($"DualSense Bluetooth speaker encoder produced {encoded} bytes; expected {OpusBytes} for the combined transport.", true);
                }

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
