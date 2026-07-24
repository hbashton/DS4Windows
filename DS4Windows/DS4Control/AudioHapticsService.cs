/*
DS4Windows
Copyright (C) 2026  DS4Windows contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using DS4Windows.InputDevices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Owns per-controller audio capture and presentation for profile-scoped
    /// Audio Haptics. Captured audio is shaped into the native DualSense 3 kHz
    /// stereo haptics lane and merged into game carriers when one is active.
    /// </summary>
    public sealed class AudioHapticsService : IDisposable
    {
        private const int ControllerCount = ControlService.MAX_DS4_CONTROLLER_COUNT;
        private readonly object[] slotLocks = Enumerable.Range(0,
            ControllerCount).Select(_ => new object()).ToArray();
        private readonly SlotRuntime[] slots = new SlotRuntime[ControllerCount];
        private bool disposed;

        public void Start(int slot, DS4Device device,
            AudioHapticsProfileSettings settings, OutContType outputType,
            string requestedPhysicalEndpointId,
            int controllerAudioUsbipPort = -1)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            settings = (settings ?? new AudioHapticsProfileSettings()).Clone();
            if (!settings.Enabled || device is not DualSenseDevice dualSense)
            {
                Stop(slot);
                if (settings.Enabled && device != null)
                {
                    AppLogger.LogToGui(
                        "Audio Haptics requires a physical DualSense or DualSense Edge controller.",
                        true);
                }
                return;
            }

            lock (slotLocks[slot])
            {
                if (disposed)
                {
                    return;
                }

                slots[slot]?.Dispose();
                slots[slot] = null;
                SlotRuntime runtime = new SlotRuntime(slot, dualSense,
                    settings, outputType.Normalize(),
                    requestedPhysicalEndpointId, controllerAudioUsbipPort);
                try
                {
                    runtime.Start();
                    slots[slot] = runtime;
                    AppLogger.LogToGui(
                        $"Audio Haptics started for controller {slot + 1}: {runtime.SourceDisplayName}.",
                        false);
                }
                catch (Exception exception)
                {
                    runtime.Dispose();
                    AppLogger.LogToGui(
                        $"Audio Haptics could not start for controller {slot + 1}: {exception.Message}",
                        true);
                }
            }
        }

        public void Stop(int slot)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return;
            }

            lock (slotLocks[slot])
            {
                SlotRuntime runtime = slots[slot];
                slots[slot] = null;
                runtime?.Dispose();
            }
        }

        /// <summary>
        /// Applies the newest audio-derived haptic frame directly to an
        /// incoming game haptics block. Calling this also grants the game
        /// carrier ownership of cadence for a short lease, preventing a second
        /// standalone write stream from competing with it.
        /// </summary>
        public bool ApplyToGameHaptics(int slot, byte[] report,
            int sampleOffset, int sampleLength)
        {
            if (slot < 0 || slot >= slots.Length || report == null ||
                sampleOffset < 0 || sampleLength < SlotRuntime.FrameBytes ||
                sampleOffset + SlotRuntime.FrameBytes > report.Length)
            {
                return false;
            }

            SlotRuntime runtime;
            lock (slotLocks[slot])
            {
                runtime = slots[slot];
            }
            return runtime?.ApplyToGameHaptics(report, sampleOffset) == true;
        }

        public AudioHapticsRuntimeStatus GetStatus(int slot)
        {
            if (slot < 0 || slot >= slots.Length)
            {
                return AudioHapticsRuntimeStatus.Inactive;
            }
            lock (slotLocks[slot])
            {
                return slots[slot]?.Status ??
                    AudioHapticsRuntimeStatus.Inactive;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            for (int slot = 0; slot < slots.Length; slot++)
            {
                Stop(slot);
            }
        }

        internal sealed class SlotRuntime : IDisposable
        {
            internal const int TargetSampleRate = 3000;
            internal const int FramesPerPacket = 32;
            internal const int FrameBytes = FramesPerPacket * 2;
            private const int QueueCapacity = 4;
            // Audio-derived haptics are live feedback, not media playback.
            // Waiting for a jitter reservoir makes an impact feel detached
            // from the sound that caused it, so begin with the first complete
            // 10.667 ms DualSense haptics packet.
            internal const int WriterPrebufferFrames = 1;
            private const int GameCarrierLeaseMilliseconds = 42;
            internal const int CaptureBufferMilliseconds = 5;
            internal const int MaximumLivePacketAgeMilliseconds = 24;
            internal const int UsbOutputLatencyMilliseconds = 10;
            private const long PacketIntervalNumerator =
                FramesPerPacket * 10000000L;
            private const int TelemetryIntervalMilliseconds = 5000;

            private readonly int slot;
            private readonly DualSenseDevice device;
            private readonly AudioHapticsProfileSettings settings;
            private readonly OutContType outputType;
            private readonly string requestedPhysicalEndpointId;
            private readonly int controllerAudioUsbipPort;
            private readonly object frameLock = new object();
            private readonly byte[][] frameQueue = Enumerable.Range(0,
                QueueCapacity).Select(_ => new byte[FrameBytes]).ToArray();
            private readonly long[] frameQueueTimestamps =
                new long[QueueCapacity];
            private readonly byte[] captureFrame = new byte[FrameBytes];
            private readonly byte[] latestFrame = new byte[FrameBytes];
            private readonly byte[] writerFrame = new byte[FrameBytes];
            private readonly ManualResetEventSlim stopped = new(false);

            private WasapiCapture capture;
            private ProcessLoopbackWaveCapture processCapture;
            private Thread writerThread;
            private AudioHapticsProcessor processor;
            private WaveFormat captureFormat;
            private MMDevice captureEndpoint;
            private MMDevice usbOutputEndpoint;
            private WasapiOut usbOutput;
            private BufferedWaveProvider usbProvider;
            private byte[] usbScratch = Array.Empty<byte>();
            private int captureFramePosition;
            private int queueRead;
            private int queueWrite;
            private int queuedFrames;
            private bool latestFrameAvailable;
            private long latestFrameTimestamp;
            private double resampleCredit;
            private long lastGameCarrierTimestamp;
            private long capturedPackets;
            private long capturedNonSilentPackets;
            private long gameCarrierMixes;
            private long gameCarrierMisses;
            private long standaloneWrites;
            private long standaloneWriteFailures;
            private long standaloneCarrierDeferrals;
            private bool standaloneHapticsActive;
            private int maximumCapturedMagnitude;
            private int started;
            private int disposed;
            private string sourceDisplayName = "audio source";
            private AudioHapticsRuntimeStatus status =
                AudioHapticsRuntimeStatus.Starting;

            public SlotRuntime(int slot, DualSenseDevice device,
                AudioHapticsProfileSettings settings, OutContType outputType,
                string requestedPhysicalEndpointId,
                int controllerAudioUsbipPort)
            {
                this.slot = slot;
                this.device = device;
                this.settings = settings;
                this.outputType = outputType;
                this.requestedPhysicalEndpointId =
                    requestedPhysicalEndpointId ?? string.Empty;
                this.controllerAudioUsbipPort = controllerAudioUsbipPort;
            }

            public string SourceDisplayName => sourceDisplayName;
            public AudioHapticsRuntimeStatus Status => status;

            public void Start()
            {
                if (Interlocked.Exchange(ref started, 1) != 0)
                {
                    return;
                }

                if (settings.Source == AudioHapticsSourceKind.AppSession)
                {
                    StartProcessCapture();
                }
                else
                {
                    StartEndpointCapture();
                }

                if (device.ConnectionType != ConnectionType.BT)
                {
                    StartUsbHapticsOutput();
                }
                else if (!device.EnsureBluetoothCombinedOutputTransport())
                {
                    throw new InvalidOperationException(
                        device.LastBluetoothHapticsWriteStatus);
                }

                writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = $"DS4W Audio Haptics {slot + 1}",
                    Priority = ThreadPriority.Highest,
                };
                writerThread.Start();
                status = settings.AutomaticGameDetection &&
                    processCapture?.CurrentProcessId <= 0
                    ? new AudioHapticsRuntimeStatus(false,
                        "Waiting for a detected game")
                    : AudioHapticsRuntimeStatus.Running;
            }

            public bool ApplyToGameHaptics(byte[] report, int sampleOffset)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return false;
                }

                lock (frameLock)
                {
                    long now = Stopwatch.GetTimestamp();
                    bool liveFrameAvailable = latestFrameAvailable &&
                        !IsLivePacketExpired(latestFrameTimestamp,
                            now);
                    if (!ApplyLiveFrame(settings.Mode, latestFrame,
                            liveFrameAvailable, report, sampleOffset))
                    {
                        Interlocked.Increment(ref gameCarrierMisses);
                        return false;
                    }
                    Volatile.Write(ref lastGameCarrierTimestamp, now);
                    Interlocked.Increment(ref gameCarrierMixes);
                    return true;
                }
            }

            internal static bool ApplyLiveFrame(AudioHapticsMode mode,
                byte[] derivedFrame, bool liveFrameAvailable, byte[] report,
                int sampleOffset)
            {
                // With no fresh audio-derived packet, leave native game
                // haptics intact and do not suppress the standalone cadence.
                // A carrier is only a carrier after it actually receives
                // Audio Haptics.
                if (!liveFrameAvailable)
                {
                    return false;
                }

                if (mode == AudioHapticsMode.Replace)
                {
                    Buffer.BlockCopy(derivedFrame, 0, report, sampleOffset,
                        FrameBytes);
                    return true;
                }

                for (int index = 0; index < FrameBytes; index++)
                {
                    report[sampleOffset + index] =
                        AudioHapticsProcessor.MixSigned8(
                            report[sampleOffset + index],
                            derivedFrame[index]);
                }
                return true;
            }

            private void StartEndpointCapture()
            {
                MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                MMDevice endpoint = null;
                if (settings.Source == AudioHapticsSourceKind.ControllerAudio)
                {
                    endpoint = DualSenseAudioPassthrough.FindActiveGameAudioEndpoint(
                        enumerator, null,
                        DualSenseAudioPassthrough.GetEndpointKind(outputType),
                        controllerAudioUsbipPort);
                    if (endpoint == null)
                    {
                        enumerator.Dispose();
                        throw new InvalidOperationException(
                            "The emulated controller audio endpoint is not available yet.");
                    }
                }
                else
                {
                    endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render,
                        Role.Multimedia);
                }

                enumerator.Dispose();
                captureEndpoint = endpoint;
                sourceDisplayName = endpoint.FriendlyName;
                capture = new LowLatencyLoopbackCapture(endpoint,
                    CaptureBufferMilliseconds);
                captureFormat = capture.WaveFormat;
                processor = new AudioHapticsProcessor(settings,
                    captureFormat.SampleRate);
                capture.DataAvailable += Capture_DataAvailable;
                capture.RecordingStopped += Capture_RecordingStopped;
                capture.StartRecording();
            }

            private void StartProcessCapture()
            {
                if (settings.AutomaticGameDetection)
                {
                    processCapture = ProcessLoopbackWaveCapture
                        .CreateAutomatic(slot);
                    sourceDisplayName = "Waiting for a detected game";
                    status = new AudioHapticsRuntimeStatus(false,
                        "Waiting for a game");
                }
                else
                {
                    int processId = ProcessLoopbackWaveCapture.ResolveProcessId(
                        settings);
                    if (processId <= 0)
                    {
                        throw new InvalidOperationException(
                            "The selected application is not currently producing an audio session.");
                    }
                    processCapture = new ProcessLoopbackWaveCapture(processId);
                    sourceDisplayName = string.IsNullOrWhiteSpace(
                        settings.DisplayName) ? $"process {processId}" :
                        settings.DisplayName;
                }
                captureFormat = processCapture.WaveFormat;
                processor = new AudioHapticsProcessor(settings,
                    captureFormat.SampleRate);
                processCapture.DataAvailable += Capture_DataAvailable;
                processCapture.RecordingStopped += Capture_RecordingStopped;
                processCapture.SourceChanged += ProcessCapture_SourceChanged;
                processCapture.StartRecording();
            }

            private void Capture_DataAvailable(object sender,
                WaveInEventArgs eventArgs)
            {
                if (eventArgs.BytesRecorded > 0 && captureFormat != null)
                {
                    ProcessPcm(eventArgs.Buffer, eventArgs.BytesRecorded,
                        captureFormat);
                }
            }

            private void Capture_RecordingStopped(object sender,
                StoppedEventArgs eventArgs)
            {
                if (eventArgs.Exception != null &&
                    Volatile.Read(ref disposed) == 0)
                {
                    status = new AudioHapticsRuntimeStatus(false,
                        $"Capture stopped: {eventArgs.Exception.Message}");
                    AppLogger.LogToGui(
                        $"Audio Haptics capture stopped for controller {slot + 1}: {eventArgs.Exception.Message}",
                        true);
                }
            }

            private void ProcessCapture_SourceChanged(object sender,
                ProcessAudioSourceChangedEventArgs eventArgs)
            {
                sourceDisplayName = eventArgs.DisplayName;
                status = eventArgs.ProcessId > 0
                    ? new AudioHapticsRuntimeStatus(true,
                        $"Active · {eventArgs.DisplayName}")
                    : new AudioHapticsRuntimeStatus(false,
                        "Waiting for a detected game");
            }

            private void ProcessPcm(byte[] buffer, int byteCount,
                WaveFormat format)
            {
                int channels = Math.Max(1, format.Channels);
                int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
                int frameBytes = Math.Max(1, format.BlockAlign);
                int frameCount = byteCount / frameBytes;
                double outputPerInput = TargetSampleRate /
                    (double)format.SampleRate;
                bool preserveCapturedNativeHaptics =
                    device.ConnectionType != ConnectionType.BT &&
                    settings.Source == AudioHapticsSourceKind.ControllerAudio &&
                    channels >= 4 && settings.Mode == AudioHapticsMode.Mix;

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int offset = frame * frameBytes;
                    float left = ReadSample(buffer, byteCount, offset, format);
                    float right = channels > 1 ? ReadSample(buffer, byteCount,
                        offset + bytesPerSample, format) : left;
                    processor.Process(left, right, out float hapticLeft,
                        out float hapticRight);

                    if (preserveCapturedNativeHaptics)
                    {
                        byte nativeLeft = AudioHapticsProcessor.Quantize(
                            ReadSample(buffer, byteCount,
                                offset + bytesPerSample * 2, format));
                        byte nativeRight = AudioHapticsProcessor.Quantize(
                            ReadSample(buffer, byteCount,
                                offset + bytesPerSample * 3, format));
                        hapticLeft = unchecked((sbyte)
                            AudioHapticsProcessor.MixSigned8(nativeLeft,
                                AudioHapticsProcessor.Quantize(hapticLeft))) /
                            127.0f;
                        hapticRight = unchecked((sbyte)
                            AudioHapticsProcessor.MixSigned8(nativeRight,
                                AudioHapticsProcessor.Quantize(hapticRight))) /
                            127.0f;
                    }

                    resampleCredit += outputPerInput;
                    while (resampleCredit >= 1.0)
                    {
                        PushHapticSample(hapticLeft, hapticRight);
                        resampleCredit -= 1.0;
                    }
                }
            }

            private void PushHapticSample(float left, float right)
            {
                captureFrame[captureFramePosition++] =
                    AudioHapticsProcessor.Quantize(left);
                captureFrame[captureFramePosition++] =
                    AudioHapticsProcessor.Quantize(right);
                if (captureFramePosition < FrameBytes)
                {
                    return;
                }

                lock (frameLock)
                {
                    long capturedAt = Stopwatch.GetTimestamp();
                    Buffer.BlockCopy(captureFrame, 0, latestFrame, 0,
                        FrameBytes);
                    latestFrameAvailable = true;
                    latestFrameTimestamp = capturedAt;
                    if (queuedFrames == QueueCapacity)
                    {
                        queueRead = (queueRead + 1) % QueueCapacity;
                        queuedFrames--;
                    }
                    Buffer.BlockCopy(captureFrame, 0, frameQueue[queueWrite], 0,
                        FrameBytes);
                    frameQueueTimestamps[queueWrite] = capturedAt;
                    queueWrite = (queueWrite + 1) % QueueCapacity;
                    queuedFrames++;
                }
                int maximumMagnitude = MaximumSignedMagnitude(captureFrame);
                Interlocked.Increment(ref capturedPackets);
                if (maximumMagnitude > 0)
                {
                    Interlocked.Increment(ref capturedNonSilentPackets);
                    UpdateMaximum(ref maximumCapturedMagnitude,
                        maximumMagnitude);
                }
                captureFramePosition = 0;
            }

            private void WriterLoop()
            {
                Stopwatch clock = Stopwatch.StartNew();
                long nextPacketTicks = clock.ElapsedTicks;
                long packetIntervalTicks = Stopwatch.Frequency *
                    PacketIntervalNumerator / 10000000L / TargetSampleRate;
                long nextTelemetryTimestamp = Stopwatch.GetTimestamp() +
                    Stopwatch.Frequency * TelemetryIntervalMilliseconds / 1000;
                bool prebuffered = false;

                while (Volatile.Read(ref disposed) == 0)
                {
                    WaitUntil(clock, nextPacketTicks);
                    nextPacketTicks += packetIntervalTicks;
                    if (clock.ElapsedTicks - nextPacketTicks >
                        packetIntervalTicks * 3)
                    {
                        nextPacketTicks = clock.ElapsedTicks +
                            packetIntervalTicks;
                    }

                    bool hasFrame = false;
                    long frameTimestamp = 0;
                    lock (frameLock)
                    {
                        if (!prebuffered)
                        {
                            prebuffered = queuedFrames >=
                                WriterPrebufferFrames;
                        }
                        if (prebuffered && queuedFrames > 0)
                        {
                            // Always consume the newest complete packet. A
                            // delayed vibration cannot catch up meaningfully;
                            // replaying queued history only creates latency.
                            int newest = (queueWrite + QueueCapacity - 1) %
                                QueueCapacity;
                            Buffer.BlockCopy(frameQueue[newest], 0,
                                writerFrame, 0, FrameBytes);
                            frameTimestamp = frameQueueTimestamps[newest];
                            queueRead = queueWrite;
                            queuedFrames = 0;
                            hasFrame = true;
                        }
                    }
                    if (hasFrame && IsLivePacketExpired(frameTimestamp,
                        Stopwatch.GetTimestamp()))
                    {
                        hasFrame = false;
                    }
                    if (!hasFrame)
                    {
                        Array.Clear(writerFrame, 0, FrameBytes);
                    }

                    int frameMagnitude = hasFrame ?
                        MaximumSignedMagnitude(writerFrame) : 0;
                    bool publishStandaloneFrame = ShouldPublishStandaloneFrame(
                        hasFrame, frameMagnitude, standaloneHapticsActive);

                    if (device.ConnectionType == ConnectionType.BT)
                    {
                        long carrierTimestamp = Volatile.Read(
                            ref lastGameCarrierTimestamp);
                        bool gameCarrierOwnsCadence = carrierTimestamp > 0 &&
                            Stopwatch.GetTimestamp() - carrierTimestamp <=
                                Stopwatch.Frequency *
                                    GameCarrierLeaseMilliseconds / 1000;
                        if (!gameCarrierOwnsCadence && publishStandaloneFrame)
                        {
                            if (device.WriteBluetoothHapticsSamples(writerFrame,
                                    0, FrameBytes))
                            {
                                Interlocked.Increment(ref standaloneWrites);
                                standaloneHapticsActive = frameMagnitude > 0;
                            }
                            else
                            {
                                Interlocked.Increment(
                                    ref standaloneWriteFailures);
                            }
                        }
                        else if (gameCarrierOwnsCadence && publishStandaloneFrame)
                        {
                            Interlocked.Increment(
                                ref standaloneCarrierDeferrals);
                        }
                    }
                    else
                    {
                        if (publishStandaloneFrame)
                        {
                            WriteUsbFrame(writerFrame);
                            standaloneHapticsActive = frameMagnitude > 0;
                        }
                    }

                    long telemetryNow = Stopwatch.GetTimestamp();
                    if (telemetryNow >= nextTelemetryTimestamp)
                    {
                        LogTelemetry();
                        nextTelemetryTimestamp = telemetryNow +
                            Stopwatch.Frequency *
                                TelemetryIntervalMilliseconds / 1000;
                    }
                }
            }

            internal static bool ShouldPublishStandaloneFrame(bool hasFrame,
                int maximumMagnitude, bool hapticsActive)
            {
                // Endpoint and app-session capture continue yielding valid
                // zero-filled packets while the selected source is silent.
                // Publishing every one of those packets creates a second HID
                // cadence that can contend with controller speaker audio. A
                // silent frame only needs publication once: to release a
                // previously active derived effect.
                return hasFrame && (maximumMagnitude > 0 || hapticsActive);
            }

            private void LogTelemetry()
            {
                if (!Global.VerboseStartupLogging)
                {
                    return;
                }

                AppLogger.LogToGui(
                    $"Audio Haptics stats controller={slot + 1} " +
                    $"source='{sourceDisplayName}' " +
                    $"captured={Interlocked.Read(ref capturedPackets)} " +
                    $"nonSilent={Interlocked.Read(ref capturedNonSilentPackets)} " +
                    $"peak={Volatile.Read(ref maximumCapturedMagnitude)} " +
                    $"carrierMixes={Interlocked.Read(ref gameCarrierMixes)} " +
                    $"carrierMisses={Interlocked.Read(ref gameCarrierMisses)} " +
                    $"standaloneWrites={Interlocked.Read(ref standaloneWrites)} " +
                    $"standaloneFailures={Interlocked.Read(ref standaloneWriteFailures)} " +
                    $"carrierDeferrals={Interlocked.Read(ref standaloneCarrierDeferrals)}.",
                    false);
            }

            private static int MaximumSignedMagnitude(byte[] frame)
            {
                int maximum = 0;
                for (int index = 0; index < frame.Length; index++)
                {
                    int magnitude = Math.Abs((int)unchecked((sbyte)frame[index]));
                    maximum = Math.Max(maximum, magnitude);
                }
                return maximum;
            }

            private static void UpdateMaximum(ref int target, int candidate)
            {
                int observed;
                do
                {
                    observed = Volatile.Read(ref target);
                    if (candidate <= observed)
                    {
                        return;
                    }
                }
                while (Interlocked.CompareExchange(ref target, candidate,
                    observed) != observed);
            }

            internal static bool IsLivePacketExpired(long capturedAt,
                long now)
            {
                return capturedAt <= 0 || now < capturedAt ||
                    now - capturedAt > Stopwatch.Frequency *
                        MaximumLivePacketAgeMilliseconds / 1000;
            }

            private void StartUsbHapticsOutput()
            {
                using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                MMDevice endpoint = null;
                if (!string.IsNullOrWhiteSpace(requestedPhysicalEndpointId))
                {
                    try
                    {
                        MMDevice requested = enumerator.GetDevice(
                            requestedPhysicalEndpointId);
                        if (requested.State == DeviceState.Active &&
                            requested.AudioClient.MixFormat.Channels >= 4)
                        {
                            endpoint = requested;
                        }
                    }
                    catch { }
                }

                endpoint ??= enumerator.EnumerateAudioEndPoints(DataFlow.Render,
                        DeviceState.Active)
                    .Where(candidate => captureEndpoint == null ||
                        !string.Equals(candidate.ID, captureEndpoint.ID,
                            StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(candidate =>
                        candidate.AudioClient.MixFormat.Channels >= 4 &&
                        DualSenseAudioPassthrough.IsDualSenseEndpoint(candidate));
                if (endpoint == null)
                {
                    throw new InvalidOperationException(
                        "No four-channel physical DualSense audio endpoint was found for USB haptics.");
                }

                usbOutputEndpoint = endpoint;
                WaveFormat format = endpoint.AudioClient.MixFormat;
                usbProvider = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(250),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                };
                usbOutput = new WasapiOut(endpoint,
                    AudioClientShareMode.Shared, true,
                    UsbOutputLatencyMilliseconds);
                usbOutput.Init(usbProvider);
                usbOutput.Play();
            }

            private void WriteUsbFrame(byte[] frame)
            {
                WaveFormat format = usbProvider?.WaveFormat;
                if (format == null)
                {
                    return;
                }

                int outputFrames = Math.Max(1, (int)Math.Round(
                    format.SampleRate * FramesPerPacket /
                    (double)TargetSampleRate));
                int bytesNeeded = checked(outputFrames * format.BlockAlign);
                if (usbScratch.Length < bytesNeeded)
                {
                    usbScratch = new byte[bytesNeeded];
                }
                Array.Clear(usbScratch, 0, bytesNeeded);
                int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
                for (int outputFrame = 0; outputFrame < outputFrames;
                    outputFrame++)
                {
                    double sourcePosition = (outputFrame + 0.5) *
                        FramesPerPacket / outputFrames - 0.5;
                    int sourceIndex = Math.Clamp((int)Math.Floor(
                        sourcePosition), 0, FramesPerPacket - 1);
                    int nextIndex = Math.Min(sourceIndex + 1,
                        FramesPerPacket - 1);
                    float fraction = (float)Math.Clamp(sourcePosition -
                        sourceIndex, 0.0, 1.0);
                    float left = Lerp(unchecked((sbyte)frame[sourceIndex * 2]) /
                            127.0f,
                        unchecked((sbyte)frame[nextIndex * 2]) / 127.0f,
                        fraction);
                    float right = Lerp(unchecked((sbyte)frame[
                            sourceIndex * 2 + 1]) / 127.0f,
                        unchecked((sbyte)frame[nextIndex * 2 + 1]) /
                            127.0f, fraction);
                    int outputOffset = outputFrame * format.BlockAlign;
                    WriteSample(usbScratch,
                        outputOffset + bytesPerSample * 2, format, left);
                    WriteSample(usbScratch,
                        outputOffset + bytesPerSample * 3, format, right);
                }
                usbProvider.AddSamples(usbScratch, 0, bytesNeeded);
            }

            private static float ReadSample(byte[] buffer, int byteCount,
                int offset, WaveFormat format)
            {
                if (offset < 0 || offset >= byteCount)
                {
                    return 0.0f;
                }
                if (format.Encoding == WaveFormatEncoding.IeeeFloat &&
                    format.BitsPerSample == 32 && offset + 3 < byteCount)
                {
                    return Math.Clamp(BitConverter.ToSingle(buffer, offset),
                        -1.0f, 1.0f);
                }
                return format.BitsPerSample switch
                {
                    16 when offset + 1 < byteCount =>
                        BinaryPrimitives.ReadInt16LittleEndian(
                            buffer.AsSpan(offset, 2)) / 32768.0f,
                    24 when offset + 2 < byteCount => ReadInt24(buffer,
                        offset) / 8388608.0f,
                    32 when offset + 3 < byteCount =>
                        BinaryPrimitives.ReadInt32LittleEndian(
                            buffer.AsSpan(offset, 4)) / 2147483648.0f,
                    _ => 0.0f,
                };
            }

            private static int ReadInt24(byte[] buffer, int offset)
            {
                int value = buffer[offset] | buffer[offset + 1] << 8 |
                    buffer[offset + 2] << 16;
                return (value & 0x800000) == 0 ? value :
                    value | unchecked((int)0xFF000000);
            }

            private static void WriteSample(byte[] buffer, int offset,
                WaveFormat format, float sample)
            {
                sample = Math.Clamp(sample, -1.0f, 1.0f);
                if (format.Encoding == WaveFormatEncoding.IeeeFloat &&
                    format.BitsPerSample == 32)
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4),
                        sample);
                    return;
                }
                switch (format.BitsPerSample)
                {
                    case 16:
                        BinaryPrimitives.WriteInt16LittleEndian(
                            buffer.AsSpan(offset, 2),
                            (short)Math.Round(sample * short.MaxValue));
                        break;
                    case 24:
                        int value24 = (int)Math.Round(sample * 8388607.0f);
                        buffer[offset] = (byte)value24;
                        buffer[offset + 1] = (byte)(value24 >> 8);
                        buffer[offset + 2] = (byte)(value24 >> 16);
                        break;
                    case 32:
                        BinaryPrimitives.WriteInt32LittleEndian(
                            buffer.AsSpan(offset, 4),
                            (int)Math.Round(sample * int.MaxValue));
                        break;
                }
            }

            private static float Lerp(float left, float right,
                float amount) => left + (right - left) * amount;

            private static void WaitUntil(Stopwatch clock, long targetTicks)
            {
                while (true)
                {
                    long remaining = targetTicks - clock.ElapsedTicks;
                    if (remaining <= 0)
                    {
                        return;
                    }
                    double remainingMs = remaining * 1000.0 /
                        Stopwatch.Frequency;
                    if (remainingMs > 1.5)
                    {
                        Thread.Sleep(1);
                    }
                    else if (remainingMs > 0.25)
                    {
                        Thread.Yield();
                    }
                    else
                    {
                        Thread.SpinWait(80);
                    }
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }
                status = AudioHapticsRuntimeStatus.Inactive;
                stopped.Set();
                if (processCapture != null)
                {
                    processCapture.DataAvailable -= Capture_DataAvailable;
                    processCapture.RecordingStopped -= Capture_RecordingStopped;
                    processCapture.SourceChanged -= ProcessCapture_SourceChanged;
                    try { processCapture.StopRecording(); } catch { }
                }
                if (capture != null)
                {
                    capture.DataAvailable -= Capture_DataAvailable;
                    capture.RecordingStopped -= Capture_RecordingStopped;
                    try { capture.StopRecording(); } catch { }
                }
                if (writerThread != null &&
                    !ReferenceEquals(writerThread, Thread.CurrentThread))
                {
                    writerThread.Join(1200);
                }
                capture?.Dispose();
                processCapture?.Dispose();
                try { usbOutput?.Stop(); } catch { }
                usbOutput?.Dispose();
                usbOutputEndpoint?.Dispose();
                captureEndpoint?.Dispose();
                stopped.Dispose();
            }

            private sealed class LowLatencyLoopbackCapture : WasapiCapture
            {
                public LowLatencyLoopbackCapture(MMDevice device,
                    int bufferMilliseconds) : base(device, false,
                        bufferMilliseconds)
                {
                }

                protected override AudioClientStreamFlags
                    GetAudioClientStreamFlags() =>
                    AudioClientStreamFlags.Loopback |
                        base.GetAudioClientStreamFlags();
            }
        }
    }

    public readonly struct AudioHapticsRuntimeStatus
    {
        public static AudioHapticsRuntimeStatus Inactive =>
            new AudioHapticsRuntimeStatus(false, "Audio Haptics is disabled.");
        public static AudioHapticsRuntimeStatus Starting =>
            new AudioHapticsRuntimeStatus(false, "Audio Haptics is starting.");
        public static AudioHapticsRuntimeStatus Running =>
            new AudioHapticsRuntimeStatus(true, "Audio Haptics is active.");

        public AudioHapticsRuntimeStatus(bool active, string message)
        {
            Active = active;
            Message = message ?? string.Empty;
        }

        public bool Active { get; }
        public string Message { get; }
    }
}
