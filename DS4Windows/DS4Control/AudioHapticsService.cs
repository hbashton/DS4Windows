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

                OutContType normalizedOutputType = outputType.Normalize();
                SlotRuntime runtime = slots[slot];
                if (runtime != null && runtime.TryUpdateSettings(settings,
                        normalizedOutputType, requestedPhysicalEndpointId,
                        controllerAudioUsbipPort))
                {
                    return;
                }

                slots[slot]?.Dispose();
                slots[slot] = null;
                runtime = new SlotRuntime(slot, dualSense,
                    settings, normalizedOutputType,
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

        public void ResetForServiceStop()
        {
            for (int slot = 0; slot < slots.Length; slot++)
            {
                Stop(slot);
            }

            disposed = false;
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
            private const int CaptureRetryIntervalMilliseconds = 250;
            private const int BluetoothTransportRetryIntervalMilliseconds =
                2000;
            private const long PacketIntervalNumerator =
                FramesPerPacket * 10000000L;
            private const int TelemetryIntervalMilliseconds = 5000;

            private readonly int slot;
            private readonly DualSenseDevice device;
            private AudioHapticsProfileSettings settings;
            private OutContType outputType;
            private string requestedPhysicalEndpointId;
            private int controllerAudioUsbipPort;
            private readonly object captureLifecycleLock = new object();
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
            private int consecutiveBluetoothWriteFailures;
            private string sourceDisplayName = "audio source";
            private AudioHapticsRuntimeStatus status =
                AudioHapticsRuntimeStatus.Starting;
            private long nextCaptureRetryTimestamp;
            private long nextBluetoothTransportRetryTimestamp;
            private int bluetoothTransportReady;

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

                status = AudioHapticsRuntimeStatus.Starting;
                lock (captureLifecycleLock)
                {
                    try
                    {
                        StartCaptureForCurrentSettings();
                    }
                    catch (Exception exception)
                    {
                        // A saved app session or controller endpoint can be
                        // unavailable during profile/bootstrap ordering. Keep
                        // the runtime alive so its capture retry loop can bind
                        // as soon as the source appears.
                        status = new AudioHapticsRuntimeStatus(false,
                            $"Waiting for audio source: {exception.Message}");
                        Volatile.Write(ref nextCaptureRetryTimestamp, 0);
                    }
                }

                if (device.ConnectionType != ConnectionType.BT)
                {
                    StartUsbHapticsOutput();
                    Volatile.Write(ref bluetoothTransportReady, 1);
                }
                else
                {
                    // The combined template is cheap to seed. The dedicated
                    // writer owns pacer preparation and retries it without
                    // coupling Audio Haptics startup to speaker playback.
                    device.EnsureBluetoothCombinedOutputTransport();
                    Volatile.Write(ref bluetoothTransportReady, 0);
                    Volatile.Write(ref nextBluetoothTransportRetryTimestamp,
                        0);
                }

                writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = $"DS4W Audio Haptics {slot + 1}",
                    Priority = ThreadPriority.Highest,
                };
                writerThread.Start();
                UpdateRuntimeStatus();
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
                    AudioHapticsProfileSettings activeSettings =
                        Volatile.Read(ref settings);
                    if (!ApplyLiveFrame(activeSettings.Mode, latestFrame,
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
                using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                MMDevice endpoint = null;
                AudioHapticsProfileSettings activeSettings =
                    Volatile.Read(ref settings);
                if (activeSettings.Source ==
                    AudioHapticsSourceKind.ControllerAudio)
                {
                    endpoint = DualSenseAudioPassthrough.FindActiveGameAudioEndpoint(
                        enumerator, null,
                        DualSenseAudioPassthrough.GetEndpointKind(outputType),
                        controllerAudioUsbipPort);
                    if (endpoint == null)
                    {
                        sourceDisplayName =
                            "Waiting for controller audio source";
                        status = new AudioHapticsRuntimeStatus(false,
                            "Waiting for the emulated controller audio endpoint");
                        return;
                    }
                }
                else
                {
                    endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render,
                        Role.Multimedia);
                }

                captureEndpoint = endpoint;
                sourceDisplayName = endpoint.FriendlyName;
                capture = new LowLatencyLoopbackCapture(endpoint,
                    CaptureBufferMilliseconds);
                captureFormat = capture.WaveFormat;
                processor = new AudioHapticsProcessor(activeSettings,
                    captureFormat.SampleRate);
                capture.DataAvailable += Capture_DataAvailable;
                capture.RecordingStopped += Capture_RecordingStopped;
                capture.StartRecording();
                UpdateRuntimeStatus();
            }

            private void StartProcessCapture()
            {
                AudioHapticsProfileSettings activeSettings =
                    Volatile.Read(ref settings);
                if (activeSettings.AutomaticGameDetection)
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
                        activeSettings);
                    if (processId <= 0)
                    {
                        throw new InvalidOperationException(
                            "The selected application is not currently producing an audio session.");
                    }
                    processCapture = new ProcessLoopbackWaveCapture(processId);
                    sourceDisplayName = string.IsNullOrWhiteSpace(
                        activeSettings.DisplayName) ? $"process {processId}" :
                        activeSettings.DisplayName;
                }
                captureFormat = processCapture.WaveFormat;
                processor = new AudioHapticsProcessor(activeSettings,
                    captureFormat.SampleRate);
                processCapture.DataAvailable += Capture_DataAvailable;
                processCapture.RecordingStopped += Capture_RecordingStopped;
                processCapture.SourceChanged += ProcessCapture_SourceChanged;
                processCapture.StartRecording();
                UpdateRuntimeStatus();
            }

            private void StartCaptureForCurrentSettings()
            {
                AudioHapticsProfileSettings activeSettings =
                    Volatile.Read(ref settings);
                if (activeSettings.Source ==
                    AudioHapticsSourceKind.AppSession)
                {
                    StartProcessCapture();
                }
                else
                {
                    StartEndpointCapture();
                }
            }

            private void EnsureCapture()
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                AudioHapticsProfileSettings activeSettings =
                    Volatile.Read(ref settings);
                bool missing = activeSettings.Source ==
                    AudioHapticsSourceKind.AppSession
                        ? processCapture == null : capture == null;
                if (!missing)
                {
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                if (now < Volatile.Read(ref nextCaptureRetryTimestamp))
                {
                    return;
                }

                lock (captureLifecycleLock)
                {
                    activeSettings = Volatile.Read(ref settings);
                    missing = activeSettings.Source ==
                        AudioHapticsSourceKind.AppSession
                            ? processCapture == null : capture == null;
                    if (!missing || Volatile.Read(ref disposed) != 0)
                    {
                        return;
                    }

                    Volatile.Write(ref nextCaptureRetryTimestamp,
                        now + Stopwatch.Frequency *
                            CaptureRetryIntervalMilliseconds / 1000);
                    try
                    {
                        StartCaptureForCurrentSettings();
                    }
                    catch (Exception exception)
                    {
                        status = new AudioHapticsRuntimeStatus(false,
                            $"Waiting for audio source: {exception.Message}");
                    }
                }
            }

            private void EnsureBluetoothTransport()
            {
                if (device.ConnectionType != ConnectionType.BT ||
                    Volatile.Read(ref bluetoothTransportReady) != 0 ||
                    Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                if (now < Volatile.Read(
                        ref nextBluetoothTransportRetryTimestamp))
                {
                    return;
                }

                Volatile.Write(ref nextBluetoothTransportRetryTimestamp,
                    now + Stopwatch.Frequency *
                        BluetoothTransportRetryIntervalMilliseconds / 1000);
                if (device.PrepareBluetoothSpeakerClockTransport())
                {
                    Volatile.Write(ref bluetoothTransportReady, 1);
                    UpdateRuntimeStatus();
                }
                else
                {
                    status = new AudioHapticsRuntimeStatus(false,
                        "Starting Bluetooth haptics transport");
                }
            }

            private void UpdateRuntimeStatus()
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    status = AudioHapticsRuntimeStatus.Inactive;
                    return;
                }

                AudioHapticsProfileSettings activeSettings =
                    Volatile.Read(ref settings);
                bool captureReady = activeSettings.Source ==
                    AudioHapticsSourceKind.AppSession
                        ? processCapture != null : capture != null;
                if (!captureReady)
                {
                    status = new AudioHapticsRuntimeStatus(false,
                        "Waiting for audio source");
                    return;
                }

                if (activeSettings.AutomaticGameDetection &&
                    processCapture?.CurrentProcessId <= 0)
                {
                    status = new AudioHapticsRuntimeStatus(false,
                        "Waiting for a detected game");
                    return;
                }

                if (device.ConnectionType == ConnectionType.BT &&
                    Volatile.Read(ref bluetoothTransportReady) == 0)
                {
                    status = new AudioHapticsRuntimeStatus(false,
                        "Starting Bluetooth haptics transport");
                    return;
                }

                status = AudioHapticsRuntimeStatus.Running;
            }

            public bool TryUpdateSettings(
                AudioHapticsProfileSettings nextSettings,
                OutContType nextOutputType, string nextPhysicalEndpointId,
                int nextUsbipPort)
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    Volatile.Read(ref started) == 0)
                {
                    return false;
                }

                nextSettings = (nextSettings ??
                    new AudioHapticsProfileSettings()).Clone();
                nextOutputType = nextOutputType.Normalize();
                nextPhysicalEndpointId ??= string.Empty;
                AudioHapticsProfileSettings previousSettings =
                    Volatile.Read(ref settings);

                bool sourceChanged = previousSettings.Source !=
                    nextSettings.Source ||
                    previousSettings.AutomaticGameDetection !=
                        nextSettings.AutomaticGameDetection;
                bool processChanged = nextSettings.Source ==
                    AudioHapticsSourceKind.AppSession &&
                    !nextSettings.AutomaticGameDetection &&
                    !SettingsMatchProcessIdentity(previousSettings,
                        nextSettings);
                bool controllerEndpointChanged = nextSettings.Source ==
                    AudioHapticsSourceKind.ControllerAudio &&
                    (outputType != nextOutputType ||
                        controllerAudioUsbipPort != nextUsbipPort);
                bool restartCapture = sourceChanged || processChanged ||
                    controllerEndpointChanged;
                bool restartUsbOutput = device.ConnectionType !=
                    ConnectionType.BT &&
                    !string.Equals(requestedPhysicalEndpointId,
                        nextPhysicalEndpointId, StringComparison.Ordinal);

                if (restartUsbOutput)
                {
                    return false;
                }

                Volatile.Write(ref settings, nextSettings);
                outputType = nextOutputType;
                requestedPhysicalEndpointId = nextPhysicalEndpointId;
                controllerAudioUsbipPort = nextUsbipPort;

                lock (captureLifecycleLock)
                {
                    if (restartCapture)
                    {
                        RetireCapture(stopRecording: true);
                        RetireProcessCapture(stopRecording: true);
                        ResetCapturedFrames();
                        Volatile.Write(ref nextCaptureRetryTimestamp, 0);
                    }
                    else if (captureFormat != null)
                    {
                        Volatile.Write(ref processor,
                            new AudioHapticsProcessor(nextSettings,
                                captureFormat.SampleRate));
                    }
                }

                EnsureCapture();
                UpdateRuntimeStatus();
                return true;
            }

            private static bool SettingsMatchProcessIdentity(
                AudioHapticsProfileSettings left,
                AudioHapticsProfileSettings right)
            {
                return left.ProcessId == right.ProcessId &&
                    string.Equals(left.ExecutableName,
                        right.ExecutableName,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(left.ProcessPath, right.ProcessPath,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(left.SessionIdentifier,
                        right.SessionIdentifier,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(left.SessionInstanceIdentifier,
                        right.SessionInstanceIdentifier,
                        StringComparison.OrdinalIgnoreCase);
            }

            private void ResetCapturedFrames()
            {
                lock (frameLock)
                {
                    queueRead = 0;
                    queueWrite = 0;
                    queuedFrames = 0;
                    latestFrameAvailable = false;
                    latestFrameTimestamp = 0;
                    captureFramePosition = 0;
                    resampleCredit = 0;
                    Array.Clear(captureFrame, 0, captureFrame.Length);
                    Array.Clear(latestFrame, 0, latestFrame.Length);
                }
            }

            private void Capture_DataAvailable(object sender,
                WaveInEventArgs eventArgs)
            {
                WaveFormat format = captureFormat;
                if (eventArgs.BytesRecorded > 0 && format != null)
                {
                    ProcessPcm(eventArgs.Buffer, eventArgs.BytesRecorded,
                        format);
                }
            }

            private void Capture_RecordingStopped(object sender,
                StoppedEventArgs eventArgs)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                bool recognized = false;
                lock (captureLifecycleLock)
                {
                    if (ReferenceEquals(sender, capture))
                    {
                        RetireCapture(stopRecording: false);
                        recognized = true;
                    }
                    else if (ReferenceEquals(sender, processCapture))
                    {
                        RetireProcessCapture(stopRecording: false);
                        recognized = true;
                    }
                }

                if (recognized)
                {
                    Volatile.Write(ref nextCaptureRetryTimestamp, 0);
                    status = new AudioHapticsRuntimeStatus(false,
                        eventArgs?.Exception == null ?
                            "Audio capture stopped; reconnecting" :
                            $"Capture stopped: {eventArgs.Exception.Message}");
                    if (eventArgs?.Exception != null)
                    {
                        AppLogger.LogToGui(
                            $"Audio Haptics capture stopped for controller {slot + 1}: {eventArgs.Exception.Message}",
                            true);
                    }
                }
            }

            private void RetireCapture(bool stopRecording)
            {
                WasapiCapture current = capture;
                MMDevice endpoint = captureEndpoint;
                capture = null;
                captureEndpoint = null;
                captureFormat = null;
                if (current == null)
                {
                    endpoint?.Dispose();
                    return;
                }

                current.DataAvailable -= Capture_DataAvailable;
                current.RecordingStopped -= Capture_RecordingStopped;
                if (stopRecording)
                {
                    try { current.StopRecording(); } catch { }
                }
                current.Dispose();
                endpoint?.Dispose();
            }

            private void RetireProcessCapture(bool stopRecording)
            {
                ProcessLoopbackWaveCapture current = processCapture;
                processCapture = null;
                captureFormat = null;
                if (current == null)
                {
                    return;
                }

                current.DataAvailable -= Capture_DataAvailable;
                current.RecordingStopped -= Capture_RecordingStopped;
                current.SourceChanged -= ProcessCapture_SourceChanged;
                if (stopRecording)
                {
                    try { current.StopRecording(); } catch { }
                }
                current.Dispose();
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
                AudioHapticsProfileSettings activeSettings =
                    Volatile.Read(ref settings);
                AudioHapticsProcessor activeProcessor =
                    Volatile.Read(ref processor);
                if (activeProcessor == null)
                {
                    return;
                }

                int channels = Math.Max(1, format.Channels);
                int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
                int frameBytes = Math.Max(1, format.BlockAlign);
                int frameCount = byteCount / frameBytes;
                double outputPerInput = TargetSampleRate /
                    (double)format.SampleRate;
                bool preserveCapturedNativeHaptics =
                    device.ConnectionType != ConnectionType.BT &&
                    activeSettings.Source ==
                        AudioHapticsSourceKind.ControllerAudio &&
                    channels >= 4 && activeSettings.Mode ==
                        AudioHapticsMode.Mix;

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int offset = frame * frameBytes;
                    float left = ReadSample(buffer, byteCount, offset, format);
                    float right = channels > 1 ? ReadSample(buffer, byteCount,
                        offset + bytesPerSample, format) : left;
                    activeProcessor.Process(left, right, out float hapticLeft,
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
                    EnsureCapture();
                    EnsureBluetoothTransport();
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
                        if (Volatile.Read(ref bluetoothTransportReady) == 0)
                        {
                            continue;
                        }

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
                                consecutiveBluetoothWriteFailures = 0;
                                Interlocked.Increment(ref standaloneWrites);
                                standaloneHapticsActive = frameMagnitude > 0;
                            }
                            else
                            {
                                Interlocked.Increment(
                                    ref standaloneWriteFailures);
                                if (++consecutiveBluetoothWriteFailures >= 3)
                                {
                                    consecutiveBluetoothWriteFailures = 0;
                                    Volatile.Write(ref bluetoothTransportReady,
                                        0);
                                    Volatile.Write(ref
                                        nextBluetoothTransportRetryTimestamp,
                                        0);
                                    status = new AudioHapticsRuntimeStatus(false,
                                        "Recovering Bluetooth haptics transport");
                                }
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
                return (hasFrame && maximumMagnitude > 0) || hapticsActive;
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
                if (standaloneHapticsActive)
                {
                    Array.Clear(writerFrame, 0, writerFrame.Length);
                    if (device.ConnectionType == ConnectionType.BT)
                    {
                        try
                        {
                            device.WriteBluetoothHapticsSamples(writerFrame, 0,
                                FrameBytes, waitForWrite: true);
                        }
                        catch { }
                    }
                    else
                    {
                        try { WriteUsbFrame(writerFrame); } catch { }
                    }
                    standaloneHapticsActive = false;
                }
                stopped.Set();
                lock (captureLifecycleLock)
                {
                    RetireProcessCapture(stopRecording: true);
                    RetireCapture(stopRecording: true);
                }
                if (writerThread != null &&
                    !ReferenceEquals(writerThread, Thread.CurrentThread))
                {
                    writerThread.Join(1200);
                }
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
