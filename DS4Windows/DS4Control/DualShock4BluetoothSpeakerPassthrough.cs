using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SBC;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private const int CaptureSampleRate = 48000;
        private const int SpeakerSampleRate = 32000;
        private const int Channels = 2;
        private const int SamplesPerSbcFrame = 128;
        private const int PcmValuesPerSbcFrame = SamplesPerSbcFrame * Channels;
        private const int SourceFramesPerTick = 512;
        private const int EncodedFrameQueueLimit = 12;
        private const int DirectPcmPacketQueueLimit = 64;
        private const int CaptureBufferMs = 240;
        private const int IdleStreamTimeoutMs = 2000;
        private const double ReportCadenceMilliseconds = 10.0 + 2.0 / 3.0;
        private const double ResampleStep =
            CaptureSampleRate / (double)SpeakerSampleRate;

        private readonly object syncRoot = new object();
        private readonly DS4Device device;
        private readonly byte speakerVolume;
        private readonly DualSenseSpeakerCompression compression;
        private readonly byte bassBoost;
        private readonly string sourceEndpointId;
        private readonly ControllerAudioEndpointKind sourceEndpointKind;
        private readonly ViiperOutDevice directSpeakerSource;
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
        private readonly float[] sourceSamples =
            new float[SourceFramesPerTick * Channels];
        private readonly short[] pendingPcm =
            new short[PcmValuesPerSbcFrame * 4];
        private readonly short[] pcmLeft = new short[SamplesPerSbcFrame];
        private readonly short[] pcmRight = new short[SamplesPerSbcFrame];
        private readonly Queue<byte[]> encodedFrames = new Queue<byte[]>();
        private readonly Queue<byte[]> directPcmPackets = new Queue<byte[]>();
        private readonly AutoResetEvent captureAvailable = new AutoResetEvent(false);
        private readonly ManualResetEvent stoppingSignal = new ManualResetEvent(false);

        private WasapiCapture capture;
        private BufferedWaveProvider captureBuffer;
        private ISampleProvider sampleProvider;
        private Thread worker;
        private NativeOverlappedWritePool speakerWritePool;
        private SafeFileHandle speakerWriteHandle;
        private volatile bool stopping;
        private ushort frameNumber;
        private int pendingPcmCount;
        private double resamplePhase;
        private float carryLeft;
        private float carryRight;
        private long lastAudibleTick;
        private int writeFailureLogged;
        private int reportsSubmitted;
        private bool speakerTransportEnabled;

        public DualShock4BluetoothSpeakerPassthrough(DS4Device device, byte speakerVolume,
            DualSenseSpeakerCompression compression, byte bassBoost,
            string sourceEndpointId, ControllerAudioEndpointKind sourceEndpointKind,
            ViiperOutDevice directSpeakerSource = null)
        {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            this.speakerVolume = speakerVolume;
            this.compression = (DualSenseSpeakerCompression)Math.Clamp((int)compression,
                (int)DualSenseSpeakerCompression.Off,
                (int)DualSenseSpeakerCompression.Strong);
            this.bassBoost = Math.Min(bassBoost,
                DualSenseSpeakerProcessor.MaximumBassBoostDb);
            this.sourceEndpointId = sourceEndpointId ?? string.Empty;
            this.sourceEndpointKind = sourceEndpointKind;
            this.directSpeakerSource = directSpeakerSource;
            processor = new DualSenseSpeakerProcessor(this.compression,
                this.bassBoost, CaptureSampleRate);
        }

        public bool Matches(DS4Device candidate, byte candidateVolume,
            DualSenseSpeakerCompression candidateCompression, byte candidateBassBoost,
            string candidateSourceEndpointId,
            ControllerAudioEndpointKind candidateSourceEndpointKind,
            ViiperOutDevice candidateDirectSpeakerSource = null)
        {
            return !stopping && ReferenceEquals(device, candidate) &&
                speakerVolume == candidateVolume &&
                compression == (DualSenseSpeakerCompression)Math.Clamp(
                    (int)candidateCompression, (int)DualSenseSpeakerCompression.Off,
                    (int)DualSenseSpeakerCompression.Strong) &&
                bassBoost == Math.Min(candidateBassBoost,
                    DualSenseSpeakerProcessor.MaximumBassBoostDb) &&
                sourceEndpointKind == candidateSourceEndpointKind &&
                ReferenceEquals(directSpeakerSource,
                    candidateDirectSpeakerSource) &&
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

            if (directSpeakerSource != null)
            {
                if (!directSpeakerSource.SupportsDirectSpeakerPcm)
                {
                    throw new InvalidOperationException(
                        "VIIPER direct DualShock 4 speaker stream is not active.");
                }

                directSpeakerSource.VirtualSpeakerPcmReceived +=
                    DirectSpeakerPcmReceived;
                try
                {
                    if (!EnsureSpeakerTransportEnabled())
                    {
                        throw new IOException(
                            $"Could not arm the DualShock 4 Bluetooth audio transport: {device.LastBluetoothAudioWriteStatus}");
                    }
                    worker = new Thread(DirectStreamLoop)
                    {
                        IsBackground = true,
                        Name = "DualShock 4 direct VIIPER SBC encoder",
                        Priority = ThreadPriority.Highest,
                    };
                    worker.Start();
                    AppLogger.LogToGui(
                        "DualShock 4 Bluetooth speaker is using the direct VIIPER PCM stream (32 kHz, no WASAPI loopback).",
                        false);
                    return;
                }
                catch
                {
                    directSpeakerSource.VirtualSpeakerPcmReceived -=
                        DirectSpeakerPcmReceived;
                    DisableSpeakerTransport();
                    throw;
                }
            }

            try
            {
                capture = CreateCapture(sourceEndpointId, sourceEndpointKind,
                    out string sourceName);
                captureBuffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(CaptureBufferMs),
                    DiscardOnBufferOverflow = true,
                    ReadFully = false,
                };
                capture.DataAvailable += Capture_DataAvailable;
                capture.RecordingStopped += Capture_RecordingStopped;

                ISampleProvider source = ToStereo(captureBuffer.ToSampleProvider());
                sampleProvider = source.WaveFormat.SampleRate == CaptureSampleRate ? source :
                    new WdlResamplingSampleProvider(source, CaptureSampleRate);
                worker = new Thread(StreamLoop)
                {
                    IsBackground = true,
                    Name = "DualShock 4 Bluetooth SBC encoder",
                    Priority = ThreadPriority.Highest,
                };
                if (!EnsureSpeakerTransportEnabled())
                {
                    throw new IOException(
                        $"Could not arm the DualShock 4 Bluetooth audio transport: {device.LastBluetoothAudioWriteStatus}");
                }
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

        private static WasapiCapture CreateCapture(string endpointId,
            ControllerAudioEndpointKind endpointKind, out string sourceName)
        {
            bool useSystemDefault = string.Equals(endpointId,
                DualSenseAudioPassthrough.DefaultSystemAudioEndpointId,
                StringComparison.Ordinal) ||
                (string.IsNullOrEmpty(endpointId) &&
                    endpointKind == ControllerAudioEndpointKind.Any);
            if (useSystemDefault)
            {
                sourceName = "Default audio endpoint";
                return new WasapiLoopbackCapture();
            }

            using var enumerator = new MMDeviceEnumerator();
            bool autoDetect = string.IsNullOrEmpty(endpointId) ||
                string.Equals(endpointId,
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
                    enumerator, autoDetect ? null : endpointId, endpointKind);
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

        private void DirectSpeakerPcmReceived(ViiperOutDevice source, byte[] pcm)
        {
            if (stopping || !ReferenceEquals(source, directSpeakerSource) ||
                pcm == null || pcm.Length < Channels * sizeof(short))
            {
                return;
            }

            lock (syncRoot)
            {
                if (stopping)
                {
                    return;
                }

                while (directPcmPackets.Count >= DirectPcmPacketQueueLimit)
                {
                    directPcmPackets.Dequeue();
                }
                directPcmPackets.Enqueue(pcm);
            }
            captureAvailable.Set();
        }

        private void DirectStreamLoop()
        {
            while (!stopping)
            {
                captureAvailable.WaitOne();
                while (!stopping)
                {
                    byte[] packet;
                    lock (syncRoot)
                    {
                        packet = directPcmPackets.Count > 0 ?
                            directPcmPackets.Dequeue() : null;
                    }

                    if (packet == null)
                    {
                        break;
                    }

                    ProcessDirectPcmPacket(packet);
                }
            }
        }

        private void ProcessDirectPcmPacket(byte[] packet)
        {
            int completeLength = packet.Length - packet.Length %
                (Channels * sizeof(short));
            for (int offset = 0; offset < completeLength; offset +=
                Channels * sizeof(short))
            {
                if (pendingPcmCount > pendingPcm.Length - Channels)
                {
                    EncodePendingPcmFrames();
                }

                pendingPcm[pendingPcmCount++] = (short)(packet[offset] |
                    packet[offset + 1] << 8);
                pendingPcm[pendingPcmCount++] = (short)(packet[offset + 2] |
                    packet[offset + 3] << 8);
            }

            EncodePendingPcmFrames();
            DrainEncodedFrames();
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
            long nextTick = Stopwatch.GetTimestamp() + cadenceTicks;
            try
            {
                // Give WASAPI one short period to prime. The stream still emits
                // valid silence if the endpoint has not produced data yet, which
                // keeps the controller decoder clocked and avoids start/stop pops.
                captureAvailable.WaitOne(20);
                while (!stopping)
                {
                    Array.Clear(sourceSamples, 0, sourceSamples.Length);
                    int samplesRead;
                    lock (syncRoot)
                    {
                        samplesRead = stopping || sampleProvider == null ? 0 :
                            sampleProvider.Read(sourceSamples, 0, sourceSamples.Length);
                    }

                    if (HasAudibleSamples(sourceSamples, samplesRead))
                    {
                        lastAudibleTick = Environment.TickCount64;
                    }

                    // Pad the tick with silence when WASAPI delivers a short burst.
                    // Real time still advances by one complete 512-frame period.
                    if (lastAudibleTick == 0 ||
                        Environment.TickCount64 - lastAudibleTick > IdleStreamTimeoutMs)
                    {
                        WaitForNextTick(ref nextTick, cadenceTicks);
                        continue;
                    }

                    processor.Process(sourceSamples, SourceFramesPerTick);
                    ResampleAndEncode(SourceFramesPerTick);
                    DrainEncodedFrames();
                    WaitForNextTick(ref nextTick, cadenceTicks);
                }
            }
            finally
            {
                timeEndPeriod(1);
            }
        }

        private void ResampleAndEncode(int inputFrames)
        {
            double position = resamplePhase;
            while (position < inputFrames &&
                pendingPcmCount <= pendingPcm.Length - Channels)
            {
                int current = (int)position;
                double fraction = position - current;
                float left0 = current == 0 ? carryLeft :
                    sourceSamples[(current - 1) * Channels];
                float right0 = current == 0 ? carryRight :
                    sourceSamples[(current - 1) * Channels + 1];
                float left1 = sourceSamples[current * Channels];
                float right1 = sourceSamples[current * Channels + 1];
                pendingPcm[pendingPcmCount++] = FloatToPcm16(
                    (float)(left0 + (left1 - left0) * fraction));
                pendingPcm[pendingPcmCount++] = FloatToPcm16(
                    (float)(right0 + (right1 - right0) * fraction));
                position += ResampleStep;
            }

            resamplePhase = Math.Max(0.0, position - inputFrames);
            carryLeft = sourceSamples[(inputFrames - 1) * Channels];
            carryRight = sourceSamples[(inputFrames - 1) * Channels + 1];

            EncodePendingPcmFrames();
        }

        private void EncodePendingPcmFrames()
        {
            int consumed = 0;
            while (pendingPcmCount - consumed >= PcmValuesPerSbcFrame)
            {
                for (int sample = 0; sample < SamplesPerSbcFrame; sample++)
                {
                    pcmLeft[sample] = pendingPcm[consumed + sample * Channels];
                    pcmRight[sample] = pendingPcm[consumed + sample * Channels + 1];
                }

                byte[] frame = encoder.Encode(pcmLeft, pcmRight,
                    frameConfiguration);
                consumed += PcmValuesPerSbcFrame;
                if (frame == null ||
                    frame.Length != DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength)
                {
                    if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                    {
                        AppLogger.LogToGui(
                            "DualShock 4 Bluetooth speaker SBC encoder returned an invalid frame.",
                            true);
                    }

                    continue;
                }

                if (encodedFrames.Count >= EncodedFrameQueueLimit)
                {
                    encodedFrames.Dequeue();
                }

                encodedFrames.Enqueue(frame);
            }

            if (consumed > 0)
            {
                Array.Copy(pendingPcm, consumed, pendingPcm, 0,
                    pendingPcmCount - consumed);
                pendingPcmCount -= consumed;
            }
        }

        private void DrainEncodedFrames()
        {
            if (encodedFrames.Count <
                DualShock4BluetoothAudioProtocol.SpeakerMinimumBufferedFrames)
            {
                return;
            }

            while (encodedFrames.Count >=
                DualShock4BluetoothAudioProtocol.SpeakerSmallFramesPerReport)
            {
                int count = encodedFrames.Count >=
                    DualShock4BluetoothAudioProtocol.SpeakerLargeFramesPerReport ?
                    DualShock4BluetoothAudioProtocol.SpeakerLargeFramesPerReport :
                    DualShock4BluetoothAudioProtocol.SpeakerSmallFramesPerReport;
                byte[][] frames = new byte[count][];
                for (int index = 0; index < count; index++)
                {
                    frames[index] = encodedFrames.Dequeue();
                }

                byte[] report = DualShock4BluetoothAudioProtocol.BuildSpeakerReport(
                    frameNumber, frames, microphoneEnabled:
                    device.BluetoothMicrophoneStreaming);
                frameNumber += (ushort)count;
                bool submitted = EnsureSpeakerWritePool();
                bool hardFailure = false;
                if (submitted)
                {
                    submitted = speakerWritePool.TrySend(report,
                        out hardFailure);
                }
                if (!submitted)
                {
                    if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                    {
                        AppLogger.LogToGui(
                            $"DualShock 4 Bluetooth speaker HID transport failed: " +
                            (hardFailure ? "dedicated audio handle write failed" :
                                "dedicated audio write pool saturated"),
                            true);
                    }
                }
                else if (submitted &&
                    Interlocked.Increment(ref reportsSubmitted) == 1)
                {
                    AppLogger.LogToGui(
                        $"DualShock 4 Bluetooth speaker submitted its first SBC report " +
                        $"(id=0x{report[0]:X2}, frames={count}, bytes={report.Length}).",
                        false);
                }
            }
        }

        private bool EnsureSpeakerWritePool()
        {
            if (speakerWritePool != null)
            {
                return true;
            }

            if (device.HidDevice?.TryOpenDedicatedAudioHandle(
                    out SafeFileHandle handle) !=
                true)
            {
                return false;
            }

            try
            {
                speakerWriteHandle = handle;
                speakerWritePool = new NativeOverlappedWritePool(
                    handle.DangerousGetHandle(),
                    DualShock4BluetoothAudioProtocol.SpeakerLargeReportLength);
                return true;
            }
            catch
            {
                speakerWriteHandle = null;
                handle.Dispose();
                throw;
            }
        }

        private bool EnsureSpeakerTransportEnabled()
        {
            if (speakerTransportEnabled)
            {
                return true;
            }

            string controlError = "not submitted";
            if (!EnsureSpeakerWritePool() ||
                !device.SetDualShock4BluetoothSpeakerStreaming(true,
                    speakerVolume, flushControlReport: false) ||
                !speakerWritePool.TrySendControl(
                    device.CreateDualShock4BluetoothAudioControlReport(),
                    out controlError))
            {
                if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        $"DualShock 4 Bluetooth speaker control could not be enabled: " +
                        $"{device.LastBluetoothAudioWriteStatus}; {controlError}",
                        true);
                }

                return false;
            }

            speakerTransportEnabled = true;
            return true;
        }

        private void DisableSpeakerTransport()
        {
            if (!speakerTransportEnabled)
            {
                return;
            }

            speakerTransportEnabled = false;
            if (device.HidDevice?.IsOpen == true && speakerWritePool != null)
            {
                device.SetDualShock4BluetoothSpeakerStreaming(false,
                    speakerVolume, flushControlReport: false);
                speakerWritePool.TrySendControl(
                    device.CreateDualShock4BluetoothAudioControlReport(),
                    out _);
            }
        }

        private static short FloatToPcm16(float value)
        {
            return (short)Math.Clamp((int)Math.Round(
                Math.Clamp(value, -1.0f, 1.0f) * short.MaxValue),
                short.MinValue, short.MaxValue);
        }

        private static bool HasAudibleSamples(float[] samples, int count)
        {
            int length = Math.Min(Math.Max(count, 0), samples.Length);
            for (int index = 0; index < length; index++)
            {
                if (Math.Abs(samples[index]) > 0.0001f)
                {
                    return true;
                }
            }

            return false;
        }

        private void WaitForNextTick(ref long nextTick, long cadenceTicks)
        {
            long now = Stopwatch.GetTimestamp();
            if (nextTick > now)
            {
                WaitUntil(nextTick);
                nextTick += cadenceTicks;
            }
            else
            {
                // Never repay lateness with a burst of back-to-back reports.
                nextTick = now + cadenceTicks;
            }
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
            if (directSpeakerSource != null)
            {
                directSpeakerSource.VirtualSpeakerPcmReceived -=
                    DirectSpeakerPcmReceived;
            }
            stoppingSignal.Set();
            captureAvailable.Set();
            if (worker != null && worker.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != worker.ManagedThreadId)
            {
                worker.Join(500);
            }
            worker = null;
            DisableSpeakerTransport();
            speakerWritePool?.Dispose();
            speakerWritePool = null;
            if (speakerWriteHandle != null)
            {
                speakerWriteHandle.Dispose();
                speakerWriteHandle = null;
            }

            WasapiCapture oldCapture;
            lock (syncRoot)
            {
                oldCapture = capture;
                capture = null;
                captureBuffer = null;
                sampleProvider = null;
                directPcmPackets.Clear();
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

        /// <summary>
        /// PadForge-compatible dedicated audio session. The one-shot 0x11
        /// audio control report and the bounded 0x17/0x14 stream use this same
        /// overlapped handle. Input remains exclusively owned by DS4Windows'
        /// primary HID session, exactly as PadForge leaves input to SDL HIDAPI.
        /// </summary>
        private sealed class NativeOverlappedWritePool : IDisposable
        {
            private const int SlotCount = 8;
            private const int OverlappedSize = 32;
            private const uint WaitObject0 = 0;
            private const uint WaitTimeout = 258;
            private const int ErrorIoPending = 997;
            private readonly object gate = new object();
            private readonly IntPtr handle;
            private readonly byte[][] buffers = new byte[SlotCount][];
            private readonly GCHandle[] pins = new GCHandle[SlotCount];
            private readonly IntPtr[] events = new IntPtr[SlotCount];
            private readonly IntPtr[] overlapped = new IntPtr[SlotCount];
            private int next;
            private volatile bool disposed;

            public NativeOverlappedWritePool(IntPtr handle, int reportSize)
            {
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    throw new ArgumentException("Invalid HID handle.",
                        nameof(handle));
                }

                this.handle = handle;
                for (int slot = 0; slot < SlotCount; slot++)
                {
                    buffers[slot] = new byte[reportSize];
                    pins[slot] = GCHandle.Alloc(buffers[slot],
                        GCHandleType.Pinned);
                    events[slot] = CreateEventW(IntPtr.Zero, true, true, null);
                    if (events[slot] == IntPtr.Zero)
                    {
                        throw new IOException(
                            "Could not create a DS4 audio completion event.");
                    }
                    overlapped[slot] = Marshal.AllocHGlobal(OverlappedSize);
                }
            }

            public bool TrySendControl(byte[] report, out string error)
            {
                error = "none";
                if (report == null || report.Length == 0)
                {
                    error = "invalid control report";
                    return false;
                }

                GCHandle pin = GCHandle.Alloc(report, GCHandleType.Pinned);
                IntPtr completionEvent = CreateEventW(IntPtr.Zero, true,
                    false, null);
                IntPtr controlOverlapped = Marshal.AllocHGlobal(
                    OverlappedSize);
                bool leak = false;
                try
                {
                    if (completionEvent == IntPtr.Zero)
                    {
                        error = $"CreateEvent failed: Win32 " +
                            Marshal.GetLastWin32Error();
                        return false;
                    }

                    ZeroOverlapped(controlOverlapped, completionEvent);
                    bool submitted = WriteFile(handle,
                        pin.AddrOfPinnedObject(), (uint)report.Length,
                        IntPtr.Zero, controlOverlapped);
                    int submitError = submitted ? 0 :
                        Marshal.GetLastWin32Error();
                    if (submitted)
                    {
                        // This is the common HIDCLASS fast path. PadForge's
                        // WriteOneShot returns immediately here as well; a
                        // synchronous overlapped WriteFile need not report the
                        // transfer count through a second result query.
                        return true;
                    }
                    if (!submitted && submitError != ErrorIoPending)
                    {
                        error = $"WriteFile failed: Win32 {submitError}";
                        return false;
                    }

                    uint wait = WaitForSingleObject(completionEvent, 1000);
                    // PadForge's WriteOneShot treats a signaled OVERLAPPED
                    // event as completion. HIDCLASS commonly reports zero via
                    // GetOverlappedResult even though the output report was
                    // accepted, so requiring a byte count creates false
                    // failures and retry storms.
                    if (wait == WaitObject0)
                    {
                        return true;
                    }

                    CancelIoEx(handle, controlOverlapped);
                    leak = WaitForSingleObject(completionEvent, 250) !=
                        WaitObject0;
                    error = wait == WaitTimeout ?
                        "control report timed out" :
                        $"control wait failed: Win32 " +
                        $"{Marshal.GetLastWin32Error()}";
                    return false;
                }
                finally
                {
                    if (!leak)
                    {
                        if (controlOverlapped != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(controlOverlapped);
                        }
                        if (completionEvent != IntPtr.Zero)
                        {
                            CloseHandle(completionEvent);
                        }
                        if (pin.IsAllocated)
                        {
                            pin.Free();
                        }
                    }
                }
            }

            public bool TrySend(byte[] report, out bool hardFailure)
            {
                hardFailure = false;
                if (report == null)
                {
                    hardFailure = true;
                    return false;
                }

                lock (gate)
                {
                    if (disposed)
                    {
                        hardFailure = true;
                        return false;
                    }

                    int slot = next;
                    if (WaitForSingleObject(events[slot], 0) != WaitObject0)
                    {
                        return false;
                    }

                    int length = Math.Min(report.Length, buffers[slot].Length);
                    Buffer.BlockCopy(report, 0, buffers[slot], 0, length);
                    ResetEvent(events[slot]);
                    ZeroOverlapped(overlapped[slot], events[slot]);
                    bool submitted = WriteFile(handle,
                        pins[slot].AddrOfPinnedObject(), (uint)length,
                        IntPtr.Zero, overlapped[slot]);
                    if (!submitted && Marshal.GetLastWin32Error() !=
                        ErrorIoPending)
                    {
                        SetEvent(events[slot]);
                        hardFailure = true;
                        return false;
                    }

                    next = (slot + 1) % SlotCount;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed)
                    {
                        return;
                    }
                    disposed = true;
                }

                CancelIoEx(handle, IntPtr.Zero);

                lock (gate)
                {
                    for (int slot = 0; slot < SlotCount; slot++)
                    {
                        if (events[slot] == IntPtr.Zero)
                        {
                            continue;
                        }

                        if (WaitForSingleObject(events[slot], 0) != WaitObject0)
                        {
                            CancelIoEx(handle, overlapped[slot]);
                        }
                        bool drained = WaitForSingleObject(events[slot], 100) ==
                            WaitObject0;
                        if (!drained)
                        {
                            // The kernel may still reference this slot. A bounded
                            // leak on device-loss teardown is safer than freeing
                            // memory underneath a late HID completion.
                            events[slot] = IntPtr.Zero;
                            overlapped[slot] = IntPtr.Zero;
                            pins[slot] = default;
                            continue;
                        }

                        CloseHandle(events[slot]);
                        events[slot] = IntPtr.Zero;
                        Marshal.FreeHGlobal(overlapped[slot]);
                        overlapped[slot] = IntPtr.Zero;
                        if (pins[slot].IsAllocated)
                        {
                            pins[slot].Free();
                        }
                    }

                }
            }

            private static void ZeroOverlapped(IntPtr value, IntPtr completionEvent)
            {
                for (int offset = 0; offset < OverlappedSize; offset += 8)
                {
                    Marshal.WriteInt64(value, offset, 0);
                }
                Marshal.WriteIntPtr(value, 24, completionEvent);
            }

            [DllImport("kernel32.dll", SetLastError = true,
                EntryPoint = "WriteFile")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool WriteFile(IntPtr handle, IntPtr buffer,
                uint bytesToWrite, IntPtr bytesWritten, IntPtr overlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetOverlappedResult(IntPtr handle,
                IntPtr overlapped, out uint bytesTransferred,
                [MarshalAs(UnmanagedType.Bool)] bool wait);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr CreateEventW(IntPtr attributes,
                [MarshalAs(UnmanagedType.Bool)] bool manualReset,
                [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

            [DllImport("kernel32.dll")]
            private static extern uint WaitForSingleObject(IntPtr handle,
                uint milliseconds);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool ResetEvent(IntPtr handle);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetEvent(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CancelIoEx(IntPtr handle,
                IntPtr overlapped);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr handle);
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint milliseconds);
    }
}
