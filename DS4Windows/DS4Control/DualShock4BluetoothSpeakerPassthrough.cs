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
    /// Stateful exact-ratio converter for VIIPER's 48 kHz stereo DualSense
    /// speaker pair. Three source frames become two 32 kHz frames, with the
    /// fractional output linearly interpolated between adjacent samples.
    /// Residual frames are retained so arbitrary USB transfer boundaries do
    /// not reset phase or duplicate audio.
    /// </summary>
    internal sealed class StereoPcm48To32LinearResampler
    {
        private const double Step = 1.5;
        private double phase;
        private float carryLeft;
        private float carryRight;

        internal int Convert(float[] source, int frameCount,
            byte[] destination)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }
            if (frameCount < 0 || frameCount * 2 > source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            }

            double position = phase;
            int destinationOffset = 0;
            while (position < frameCount)
            {
                if (destinationOffset > destination.Length -
                    2 * sizeof(short))
                {
                    throw new ArgumentException(
                        "The 32 kHz destination buffer is too small.",
                        nameof(destination));
                }

                int current = (int)position;
                double fraction = position - current;
                float left0 = current == 0 ? carryLeft :
                    source[(current - 1) * 2];
                float right0 = current == 0 ? carryRight :
                    source[(current - 1) * 2 + 1];
                float left1 = source[current * 2];
                float right1 = source[current * 2 + 1];
                WriteSample(destination, ref destinationOffset,
                    FloatToPcm16((float)(left0 * (1.0 - fraction) +
                        left1 * fraction)));
                WriteSample(destination, ref destinationOffset,
                    FloatToPcm16((float)(right0 * (1.0 - fraction) +
                        right1 * fraction)));
                position += Step;
            }

            phase = Math.Max(0.0, position - frameCount);
            if (frameCount > 0)
            {
                carryLeft = source[(frameCount - 1) * 2];
                carryRight = source[(frameCount - 1) * 2 + 1];
            }
            return destinationOffset;
        }

        private static void WriteSample(byte[] destination, ref int offset,
            short sample)
        {
            destination[offset++] = (byte)sample;
            destination[offset++] = (byte)(sample >> 8);
        }

        private static short FloatToPcm16(float value)
        {
            return (short)Math.Clamp((int)Math.Round(
                Math.Clamp(value, -1.0f, 1.0f) * short.MaxValue),
                short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// Encodes a selected Windows playback endpoint as the SBC-over-HID stream
    /// understood by a physical Bluetooth DualShock 4.
    /// </summary>
    internal sealed class DualShock4BluetoothSpeakerPassthrough : IDisposable
    {
        private const int CaptureSampleRate = 48000;
        private const int SpeakerSampleRate =
            DualShock4BluetoothAudioProtocol.SpeakerSampleRate;
        private const int Channels = 2;
        private const int SamplesPerSbcFrame =
            DualShock4BluetoothAudioProtocol.SpeakerSamplesPerFrame;
        private const int PcmValuesPerSbcFrame = SamplesPerSbcFrame * Channels;
        // The legacy WASAPI lane pulls one 128-sample SBC frame every 4 ms.
        // The direct VIIPER lane below presents four of those frames in one
        // 0x17 report every 16 ms.
        private const int SourceFramesPerTick = CaptureSampleRate *
            DualShock4BluetoothAudioProtocol.
                SpeakerRealtimeReportDurationMilliseconds /
            1000;
        private const int EncodedFrameQueueLimit =
            DualShock4BluetoothAudioProtocol.SpeakerEncodedFrameQueueLimit;
        private const int DirectPcmPacketQueueLimit = 64;
        private const int DirectPcmPacketBufferLength = 4096;
        private const int DirectPcmMaximumSourceFrames =
            DirectPcmPacketBufferLength / (Channels * sizeof(short));
        // The legacy PCM-queue lane still uses these bounds. The direct VIIPER
        // callback encodes complete source packets, while its presenter below
        // consumes the encoded queue on the Sony 16 ms report clock.
        private const int DirectLagDeadbandFrames =
            SpeakerSampleRate * 5 / 1000;
        private const int DirectLagTrimFrames = 3;
        private const int DirectMaximumFramesPerTick =
            SpeakerSampleRate * 512 / CaptureSampleRate + 1 +
            DirectLagTrimFrames;
        private const int CaptureBufferMs = 240;
        private const int IdleStreamTimeoutMs = 2000;
        private const int DirectSourceIdleThresholdMilliseconds = 200;
        private const int MaxFramesAvailableWaitMilliseconds = 20;
        private const int PadForgeAsyncTailFlushIdleMilliseconds = 60;
        private const int PadForgeAsyncBackpressureWaitMilliseconds = 1;
        private static readonly bool EnableDiagnosticCapture =
            string.Equals(Environment.GetEnvironmentVariable(
                "DS4WINDOWS_DS4_AUDIO_DIAGNOSTIC_CAPTURE"), "1",
                StringComparison.Ordinal);
        // Keep one bounded, in-memory diagnostic sample from the direct VIIPER
        // lane. Disk I/O never runs on either real-time path: after both sides
        // cover the same minute, a ThreadPool worker writes the raw 32 kHz
        // stereo PCM and concatenated 109-byte SBC frames beside the normal
        // DS4Windows log. This lets an audible cut be located before or after
        // the encoder without changing presentation timing.
        private const int DiagnosticCaptureSeconds = 60;
        private const int DiagnosticPcmBytes = SpeakerSampleRate * Channels *
            sizeof(short) * DiagnosticCaptureSeconds;
        private const int DiagnosticSbcFrames = SpeakerSampleRate /
            SamplesPerSbcFrame * DiagnosticCaptureSeconds;
        private const int DiagnosticSbcBytes = DiagnosticSbcFrames *
            DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength;
        private const double ReportCadenceMilliseconds =
            DualShock4BluetoothAudioProtocol.
                SpeakerRealtimeReportDurationMilliseconds;
        private const double DirectReportCadenceMilliseconds =
            DualShock4BluetoothAudioProtocol.
                SpeakerDirectReportDurationMilliseconds;
        private const double ResampleStep =
            CaptureSampleRate / (double)SpeakerSampleRate;

        private enum PadForgeAsyncSubmissionResult
        {
            Submitted,
            Saturated,
            NoFrames,
            Failed,
        }

        private enum ProductionReplaySubmissionResult
        {
            Submitted,
            Saturated,
            NoFrames,
            Failed,
        }

        private enum FifoBufferedSubmissionResult
        {
            Submitted,
            Saturated,
            NoFrames,
            Failed,
        }

        private enum CreditBufferedSubmissionResult
        {
            Submitted,
            Saturated,
            NoFrames,
            Failed,
        }

        private readonly object syncRoot = new object();
        private readonly DS4Device device;
        private readonly byte speakerVolume;
        private readonly DualSenseSpeakerCompression compression;
        private readonly byte bassBoost;
        private readonly string sourceEndpointId;
        private readonly ControllerAudioEndpointKind sourceEndpointKind;
        private readonly ViiperOutDevice directSpeakerSource;
        private readonly int directSpeakerSampleRate;
        private readonly DualShock4AudioDriftMode directDriftMode;
        private readonly DualShock4AudioTransportMode directTransportMode;
        private readonly DualSenseSpeakerProcessor processor;
        private readonly StereoPcm48To32LinearResampler directResampler =
            new StereoPcm48To32LinearResampler();
        private readonly DualShock4SbcEncoder encoder =
            new DualShock4SbcEncoder();
        private readonly float[] sourceSamples = new float[
            Math.Max(SourceFramesPerTick, DirectPcmMaximumSourceFrames) *
                Channels];
        private readonly short[] pendingPcm =
            new short[PcmValuesPerSbcFrame * 4];
        private readonly short[] pcmLeft = new short[SamplesPerSbcFrame];
        private readonly short[] pcmRight = new short[SamplesPerSbcFrame];
        private readonly byte[] speakerSilenceFrame = new byte[
            DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength];
        private readonly Queue<byte[]> encodedFrames =
            new Queue<byte[]>(EncodedFrameQueueLimit);
        private readonly Queue<byte[]> freeEncodedFrames =
            new Queue<byte[]>(EncodedFrameQueueLimit);
        private readonly Queue<DirectPcmPacket> directPcmPackets =
            new Queue<DirectPcmPacket>(DirectPcmPacketQueueLimit);
        private readonly Queue<byte[]> freeDirectPcmPackets =
            new Queue<byte[]>(DirectPcmPacketQueueLimit);
        private readonly byte[] directTickPcm =
            new byte[DirectPcmPacketBufferLength];
        private readonly short[] directDriftPcm = new short[
            (DirectPcmMaximumSourceFrames + 16) * Channels];
        private readonly StereoPcm16FractionalResampler
            directFractionalResampler = new StereoPcm16FractionalResampler();
        private readonly byte[][] speakerFrameBatch = new byte[
            DualShock4BluetoothAudioProtocol.SpeakerLargeFramesPerReport][];
        private readonly byte[] speakerRealtimeReport = new byte[
            DualShock4BluetoothAudioProtocol.SpeakerRealtimeReportLength];
        private readonly byte[] speakerSmallReport = new byte[
            DualShock4BluetoothAudioProtocol.SpeakerSmallReportLength];
        private readonly byte[] speakerLargeReport = new byte[
            DualShock4BluetoothAudioProtocol.SpeakerLargeReportLength];
        private readonly AutoResetEvent captureAvailable = new AutoResetEvent(false);
        private readonly ManualResetEvent stoppingSignal = new ManualResetEvent(false);

        private byte[] diagnosticPcm;
        private byte[] diagnosticSbc;
        private int diagnosticPcmCount;
        private int diagnosticSbcCount;
        private int diagnosticCaptureWritten;
        private DateTime diagnosticCaptureStartedUtc;

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
        private long directPacketsReceived;
        private long directPcmBytesReceived;
        private long directPacketsDropped;
        private long directFramesEncoded;
        private long directFramesDroppedForLatency;
        private long directWriteSaturations;
        private long directHardWriteFailures;
        private long lastDirectPacketTimestamp;
        private long maximumDirectPacketGapTicks;
        private long lastDirectReportTimestamp;
        private long maximumDirectReportGapTicks;
        private long minimumDirectReportGapTicks = long.MaxValue;
        private long directCadencePrimes;
        private long directCadenceUnderruns;
        private long syntheticSilenceReports;
        private long directLateDeadlines;
        private long directLagAccelerations;
        private long directLagDecelerations;
        private long directRealtimeReports;
        private long directSmallReports;
        private long directLargeReports;
        private long directSilentPackets;
        private long directCurrentSilentRun;
        private long directMaximumSilentRun;
        private long directExactZeroFrames;
        private long directCurrentExactZeroFrameRun;
        private long directMaximumExactZeroFrameRun;
        private long directExactZeroRunEvents;
        private long directRepeatedPackets;
        private long directLastPacketFingerprint;
        private long directPeakSample;
        private long lastDirectTraceTimestamp;
        private int directTracePending;
        private int directPcmPacketOffset;
        private bool directHasPreviousSample;
        private short directPreviousLeft;
        private short directPreviousRight;
        private double directDriftCorrectionAccumulator;
        private double directAsrcBaseRatio = 1.0;
        private double directCurrentDriftRatio = 1.0;
        private double directTargetDriftRatio = 1.0;
        private long directDriftInputFrames;
        private long directDriftOutputFrames;
        private int directMinimumEncodedQueueDepth = int.MaxValue;
        private int directMaximumEncodedQueueDepth;
        private long directCurrentCadenceTicks;
        private long directTargetCadenceTicks;
        private volatile bool directDriftCorrectionEnabled;
        private long productionReplayPrimeReports;
        private long productionReplayReprimes;
        private long productionReplaySkippedTicks;
        private int productionReplayPrimeLogged;
        private long fifoBufferedPrimeReports;
        private long fifoBufferedReprimes;
        private long fifoBufferedSkippedTicks;
        private int fifoBufferedPrimeLogged;
        private long creditBufferedPrimeReports;
        private long creditBufferedReprimes;
        private long creditBufferedSkippedTicks;
        private int creditBufferedPrimeLogged;

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
            directSpeakerSampleRate = directSpeakerSource?.
                DirectSpeakerPcmSampleRate ?? 0;
            directDriftMode = DualShock4AudioDriftSettings.Parse(
                Environment.GetEnvironmentVariable(
                    DualShock4AudioDriftSettings.EnvironmentVariableName));
            directTransportMode = DualShock4AudioTransportSettings.Parse(
                Environment.GetEnvironmentVariable(
                    DualShock4AudioTransportSettings.EnvironmentVariableName));
            processor = new DualSenseSpeakerProcessor(this.compression,
                this.bassBoost, CaptureSampleRate);
            var silenceEncoder = new DualShock4SbcEncoder();
            silenceEncoder.Encode(new short[SamplesPerSbcFrame],
                new short[SamplesPerSbcFrame], speakerSilenceFrame);
            for (int index = 0; index < EncodedFrameQueueLimit; index++)
            {
                freeEncodedFrames.Enqueue(new byte[
                    DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength]);
            }
            if (directSpeakerSource != null)
            {
                for (int index = 0; index < DirectPcmPacketQueueLimit; index++)
                {
                    freeDirectPcmPackets.Enqueue(
                        new byte[DirectPcmPacketBufferLength]);
                }
            }
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
                    // Match the proven production transport: establish the
                    // dedicated HID write lane during startup, but do not arm
                    // the physical speaker until a complete source cushion is
                    // available. Keeping one handle for the full stream also
                    // preserves write ordering across every audio segment.
                    if (!EnsureSpeakerWritePool())
                    {
                        throw new IOException(
                            "Could not open the DualShock 4 Bluetooth audio transport.");
                    }
                    worker = new Thread(DirectStreamLoop)
                    {
                        IsBackground = true,
                        Name = "DualShock 4 direct VIIPER SBC encoder",
                        Priority = ThreadPriority.Highest,
                    };
                    worker.Start();
                    AppLogger.LogToGui(
                        $"DualShock 4 Bluetooth speaker is using the direct VIIPER PCM stream ({directSpeakerSampleRate / 1000} kHz source, no WASAPI loopback, transport={DualShock4AudioTransportSettings.Format(directTransportMode)}, drift={directDriftMode.ToString().ToLowerInvariant()}).",
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

        private void DirectSpeakerPcmReceived(ViiperOutDevice source, byte[] pcm,
            int length)
        {
            if (stopping || !ReferenceEquals(source, directSpeakerSource) ||
                pcm == null || length < Channels * sizeof(short) ||
                length > pcm.Length || length > DirectPcmPacketBufferLength)
            {
                if (length > DirectPcmPacketBufferLength)
                {
                    Interlocked.Increment(ref directPacketsDropped);
                }
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(
                ref lastDirectPacketTimestamp, now);
            int completeLength = length - length %
                (Channels * sizeof(short));
            if (Global.VerboseStartupLogging)
            {
                if (previous != 0)
                {
                    RecordMaximum(ref maximumDirectPacketGapTicks,
                        now - previous);
                }
                Interlocked.Increment(ref directPacketsReceived);
                Interlocked.Add(ref directPcmBytesReceived, length);
                int peak = 0;
                ulong fingerprint = 1469598103934665603UL;
                for (int offset = 0; offset < completeLength;
                    offset += sizeof(short))
                {
                    short sample = (short)(pcm[offset] |
                        pcm[offset + 1] << 8);
                    int magnitude = sample == short.MinValue ?
                        short.MaxValue : Math.Abs(sample);
                    if (magnitude > peak)
                    {
                        peak = magnitude;
                    }
                    fingerprint ^= pcm[offset];
                    fingerprint *= 1099511628211UL;
                    fingerprint ^= pcm[offset + 1];
                    fingerprint *= 1099511628211UL;
                }
                long previousFingerprint = Interlocked.Exchange(
                    ref directLastPacketFingerprint,
                    unchecked((long)fingerprint));
                if (previousFingerprint != 0 && previousFingerprint ==
                    unchecked((long)fingerprint))
                {
                    Interlocked.Increment(ref directRepeatedPackets);
                }
                for (int offset = 0; offset < completeLength;
                    offset += Channels * sizeof(short))
                {
                    bool exactZero = pcm[offset] == 0 &&
                        pcm[offset + 1] == 0 && pcm[offset + 2] == 0 &&
                        pcm[offset + 3] == 0;
                    if (exactZero)
                    {
                        Interlocked.Increment(ref directExactZeroFrames);
                        long run = Interlocked.Increment(
                            ref directCurrentExactZeroFrameRun);
                        RecordMaximum(ref directMaximumExactZeroFrameRun, run);
                    }
                    else
                    {
                        long run = Interlocked.Exchange(
                            ref directCurrentExactZeroFrameRun, 0);
                        if (run >= 16)
                        {
                            Interlocked.Increment(
                                ref directExactZeroRunEvents);
                        }
                    }
                }
                RecordMaximum(ref directPeakSample, peak);
                if (peak <= 16)
                {
                    Interlocked.Increment(ref directSilentPackets);
                    long run = Interlocked.Increment(
                        ref directCurrentSilentRun);
                    RecordMaximum(ref directMaximumSilentRun, run);
                }
                else
                {
                    Interlocked.Exchange(ref directCurrentSilentRun, 0);
                    Interlocked.Exchange(ref lastAudibleTick,
                        Environment.TickCount64);
                }
            }

            lock (syncRoot)
            {
                if (stopping)
                {
                    return;
                }

                if (directSpeakerSampleRate == CaptureSampleRate)
                {
                    completeLength = ResampleDirectSpeakerPacket(pcm,
                        completeLength);
                    CaptureDiagnosticPcm(directTickPcm, completeLength);
                    ProcessDirectPcmPacket(directTickPcm, completeLength);
                }
                else if (directSpeakerSampleRate == SpeakerSampleRate)
                {
                    CaptureDiagnosticPcm(pcm, completeLength);
                    ProcessDirectPcmPacket(pcm, completeLength);
                }
                else
                {
                    Interlocked.Increment(ref directPacketsDropped);
                    return;
                }
            }
            captureAvailable.Set();
        }

        private int ResampleDirectSpeakerPacket(byte[] pcm, int length)
        {
            int inputFrames = Math.Min(length / (Channels * sizeof(short)),
                DirectPcmMaximumSourceFrames);
            for (int frame = 0; frame < inputFrames; frame++)
            {
                int byteOffset = frame * Channels * sizeof(short);
                int sampleOffset = frame * Channels;
                short left = (short)(pcm[byteOffset] |
                    pcm[byteOffset + 1] << 8);
                short right = (short)(pcm[byteOffset + 2] |
                    pcm[byteOffset + 3] << 8);
                sourceSamples[sampleOffset] = left / 32768.0f;
                sourceSamples[sampleOffset + 1] = right / 32768.0f;
            }

            if (inputFrames == 0)
            {
                return 0;
            }

            processor.Process(sourceSamples, inputFrames);
            return directResampler.Convert(sourceSamples, inputFrames,
                directTickPcm);
        }

        private void ProcessDirectPcmPacket(byte[] pcm, int length)
        {
            int inputFrames = length / (Channels * sizeof(short));
            if (inputFrames <= 0)
            {
                return;
            }

            RecordDirectEncodedQueueDepthLocked();
            Interlocked.Add(ref directDriftInputFrames, inputFrames);
            int outputFrames = 0;
            bool historicalSlipServo = DualShock4AudioTransportSettings.
                    UsesProductionReplayPolicy(directTransportMode) ||
                directTransportMode ==
                    DualShock4AudioTransportMode.FifoBuffered;
            if (directDriftMode == DualShock4AudioDriftMode.Fractional &&
                !historicalSlipServo)
            {
                directAsrcBaseRatio = device.
                    BluetoothControllerClockRatio;
                if (directTransportMode !=
                    DualShock4AudioTransportMode.Scheduled)
                {
                    // Keep the stateful, allocation-free ASRC in the source
                    // path, but run it at unity in every isolated transport
                    // replay. The controller clock remains measured and logged;
                    // it does not create a second pacing mechanism.
                    directTargetDriftRatio = 1.0;
                }
                else
                {
                    directTargetDriftRatio = directDriftCorrectionEnabled ?
                        DualShock4AudioDriftSettings.CalculateAsrcOutputRatio(
                            directAsrcBaseRatio, encodedFrames.Count,
                            DualShock4BluetoothAudioProtocol.
                                SpeakerRealtimeSourceCushionFrames) :
                        DualShock4AudioDriftSettings.ClampAsrcOutputRatio(
                            directAsrcBaseRatio);
                }
                directCurrentDriftRatio =
                    DualShock4AudioDriftSettings.SlewOutputRatio(
                        directCurrentDriftRatio, directTargetDriftRatio);
                outputFrames = directFractionalResampler.Convert(pcm,
                    inputFrames * Channels * sizeof(short), directDriftPcm,
                    directCurrentDriftRatio);
                for (int frame = 0; frame < outputFrames; frame++)
                {
                    AppendDirectPcmSample(directDriftPcm[frame * Channels],
                        directDriftPcm[frame * Channels + 1]);
                }
            }
            else
            {
                directAsrcBaseRatio = 1.0;
                directTargetDriftRatio = 1.0;
                directCurrentDriftRatio = 1.0;
                for (int offset = 0;
                    offset + Channels * sizeof(short) <= length;
                    offset += Channels * sizeof(short))
                {
                    short left = (short)(pcm[offset] | pcm[offset + 1] << 8);
                    short right = (short)(pcm[offset + 2] |
                        pcm[offset + 3] << 8);
                    bool dropCurrentSample = false;
                    if ((directDriftMode == DualShock4AudioDriftMode.Slip ||
                            (historicalSlipServo && directDriftMode !=
                                DualShock4AudioDriftMode.Off)) &&
                        directDriftCorrectionEnabled)
                    {
                        directTargetDriftRatio = historicalSlipServo ?
                            (directTransportMode ==
                                    DualShock4AudioTransportMode.FifoBuffered ?
                                DualShock4AudioTransportSettings.
                                    GetFifoBufferedQueueServoRatio(
                                        encodedFrames.Count, enabled: true) :
                                DualShock4AudioTransportSettings.
                                    GetProductionReplayQueueServoRatio(
                                        encodedFrames.Count, enabled: true)) :
                            DualShock4AudioDriftSettings.
                                CalculateTargetOutputRatio(encodedFrames.Count,
                                    DualShock4BluetoothAudioProtocol.
                                        SpeakerRealtimeSourceCushionFrames);
                        directCurrentDriftRatio = directTargetDriftRatio;
                        directDriftCorrectionAccumulator +=
                            directTargetDriftRatio - 1.0;
                        if (directDriftCorrectionAccumulator >= 1.0 &&
                            directHasPreviousSample)
                        {
                            AppendDirectPcmSample((short)(
                                (directPreviousLeft + (int)left) / 2),
                                (short)((directPreviousRight + (int)right) / 2));
                            outputFrames++;
                            directDriftCorrectionAccumulator -= 1.0;
                            Interlocked.Increment(ref directLagAccelerations);
                        }
                        else if (directDriftCorrectionAccumulator <= -1.0)
                        {
                            directDriftCorrectionAccumulator += 1.0;
                            dropCurrentSample = true;
                            Interlocked.Increment(ref directLagDecelerations);
                        }
                    }
                    else
                    {
                        directDriftCorrectionAccumulator = 0.0;
                    }

                    if (!dropCurrentSample)
                    {
                        AppendDirectPcmSample(left, right);
                        outputFrames++;
                    }
                    directPreviousLeft = left;
                    directPreviousRight = right;
                    directHasPreviousSample = true;
                }
            }

            Interlocked.Add(ref directDriftOutputFrames, outputFrames);
            EncodePendingPcmFrames();
            RecordDirectEncodedQueueDepthLocked();
        }

        private void RecordDirectEncodedQueueDepthLocked()
        {
            int depth = encodedFrames.Count;
            directMinimumEncodedQueueDepth = Math.Min(
                directMinimumEncodedQueueDepth, depth);
            directMaximumEncodedQueueDepth = Math.Max(
                directMaximumEncodedQueueDepth, depth);
        }

        private void AppendDirectPcmSample(short left, short right)
        {
            if (pendingPcmCount > pendingPcm.Length - Channels)
            {
                EncodePendingPcmFrames();
            }
            pendingPcm[pendingPcmCount++] = left;
            pendingPcm[pendingPcmCount++] = right;
        }

        private void DirectStreamLoop()
        {
            if (directTransportMode == DualShock4AudioTransportMode.Reference)
            {
                ReferenceDirectStreamLoop();
                return;
            }
            if (directTransportMode ==
                DualShock4AudioTransportMode.SourceDriven)
            {
                SourceDrivenDirectStreamLoop();
                return;
            }
            if (directTransportMode ==
                    DualShock4AudioTransportMode.PadForgeAsync ||
                directTransportMode ==
                    DualShock4AudioTransportMode.PadForgeSpeakerOnly)
            {
                PadForgeAsyncDirectStreamLoop();
                return;
            }
            if (directTransportMode ==
                DualShock4AudioTransportMode.InputSynchronized)
            {
                InputSynchronizedDirectStreamLoop();
                return;
            }
            if (DualShock4AudioTransportSettings.
                UsesProductionReplayPolicy(directTransportMode))
            {
                ProductionReplayDirectStreamLoop();
                return;
            }
            if (directTransportMode ==
                DualShock4AudioTransportMode.FifoBuffered)
            {
                FifoBufferedDirectStreamLoop();
                return;
            }
            if (directTransportMode ==
                DualShock4AudioTransportMode.CreditBuffered)
            {
                CreditBufferedDirectStreamLoop();
                return;
            }

            ScheduledDirectStreamLoop();
        }

        private void ReferenceDirectStreamLoop()
        {
            var waitHandles = new WaitHandle[]
            {
                captureAvailable,
                stoppingSignal,
            };
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            try
            {
                while (!stopping)
                {
                    int bufferedFrames;
                    lock (syncRoot)
                    {
                        bufferedFrames = encodedFrames.Count;
                    }

                    if (!DualShock4AudioTransportSettings.
                            ShouldWakeReferenceSender(bufferedFrames))
                    {
                        TraceDirectStreamStatus();
                        int signaled = WaitHandle.WaitAny(waitHandles,
                            MaxFramesAvailableWaitMilliseconds);
                        if (signaled == 1 || stopping)
                        {
                            return;
                        }
                        continue;
                    }

                    if (!speakerTransportEnabled &&
                        !EnsureSpeakerTransportEnabled())
                    {
                        return;
                    }

                    // Reference transport has one clock only: source
                    // availability followed by in-order HID completion.
                    directDriftCorrectionEnabled = false;
                    while (!stopping)
                    {
                        lock (syncRoot)
                        {
                            bufferedFrames = encodedFrames.Count;
                        }
                        int reportFrames = DualShock4AudioTransportSettings.
                            SelectReferenceReportFrameCount(bufferedFrames);
                        if (reportFrames == 0)
                        {
                            break;
                        }

                        if (!SubmitEncodedFramesAndWait(reportFrames))
                        {
                            return;
                        }
                        TraceDirectStreamStatus();
                    }
                }
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                {
                    AvRevertMmThreadCharacteristics(mmcssHandle);
                }
            }
        }

        /// <summary>
        /// Replays the direct-speaker submission policy from the last known
        /// clean DualShock 4 build (af57bca). Source availability is the only
        /// presentation clock: once four SBC frames are buffered, drain the
        /// batch as a 0x17 report and any two-frame remainder as 0x14. Unlike
        /// the synchronous reference probe, this retains the historical
        /// eight-slot overlapped writer and therefore adds no completion wait
        /// to the speaker path.
        /// </summary>
        private void SourceDrivenDirectStreamLoop()
        {
            var waitHandles = new WaitHandle[]
            {
                captureAvailable,
                stoppingSignal,
            };
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            try
            {
                while (!stopping)
                {
                    int bufferedFrames;
                    lock (syncRoot)
                    {
                        bufferedFrames = encodedFrames.Count;
                    }

                    if (!DualShock4AudioTransportSettings.
                            ShouldWakeReferenceSender(bufferedFrames))
                    {
                        TraceDirectStreamStatus();
                        int signaled = WaitHandle.WaitAny(waitHandles,
                            MaxFramesAvailableWaitMilliseconds);
                        if (signaled == 1 || stopping)
                        {
                            return;
                        }
                        continue;
                    }

                    if (!speakerTransportEnabled &&
                        !EnsureSpeakerTransportEnabled())
                    {
                        return;
                    }

                    directDriftCorrectionEnabled = false;
                    while (!stopping)
                    {
                        lock (syncRoot)
                        {
                            bufferedFrames = encodedFrames.Count;
                        }

                        int reportFrames = DualShock4AudioTransportSettings.
                            SelectReferenceReportFrameCount(bufferedFrames);
                        if (reportFrames == 0)
                        {
                            break;
                        }

                        PadForgeAsyncSubmissionResult result =
                            SubmitEncodedFramesPadForgeAsync(reportFrames);
                        if (result == PadForgeAsyncSubmissionResult.Failed)
                        {
                            return;
                        }
                        if (result == PadForgeAsyncSubmissionResult.NoFrames)
                        {
                            break;
                        }
                        if (result == PadForgeAsyncSubmissionResult.Saturated)
                        {
                            if (stoppingSignal.WaitOne(
                                PadForgeAsyncBackpressureWaitMilliseconds))
                            {
                                return;
                            }
                            continue;
                        }

                        TraceDirectStreamStatus();
                    }
                }
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                {
                    AvRevertMmThreadCharacteristics(mmcssHandle);
                }
            }
        }

        private void PadForgeAsyncDirectStreamLoop()
        {
            var waitHandles = new WaitHandle[]
            {
                captureAvailable,
                stoppingSignal,
            };
            timeBeginPeriod(1);
            IntPtr highResolutionTimer = CreateHighResolutionTimer();
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            long cadenceTicks = (long)Math.Round(Stopwatch.Frequency *
                DirectReportCadenceMilliseconds / 1000.0);
            Interlocked.Exchange(ref directCurrentCadenceTicks,
                cadenceTicks);
            Interlocked.Exchange(ref directTargetCadenceTicks,
                cadenceTicks);
            try
            {
                while (!stopping)
                {
                    int bufferedFrames;
                    lock (syncRoot)
                    {
                        bufferedFrames = encodedFrames.Count;
                    }
                    if (bufferedFrames >= DualShock4BluetoothAudioProtocol.
                            SpeakerLargeFramesPerReport)
                    {
                        break;
                    }

                    TraceDirectStreamStatus();
                    int signaled = WaitHandle.WaitAny(waitHandles,
                        MaxFramesAvailableWaitMilliseconds);
                    if (signaled == 1 || stopping)
                    {
                        return;
                    }
                }

                if (stopping || !EnsureSpeakerTransportEnabled())
                {
                    return;
                }

                long nextTick = Stopwatch.GetTimestamp();
                while (!stopping)
                {
                    WaitForNextTick(ref nextTick, cadenceTicks,
                        highResolutionTimer,
                        DualShock4AudioTransportSettings.
                            PadForgeAsyncSlotCount);

                    int bufferedFrames;
                    lock (syncRoot)
                    {
                        bufferedFrames = encodedFrames.Count;
                    }

                    int reportFrames = DualShock4AudioTransportSettings.
                        SelectPadForgeAsyncReportFrameCount(bufferedFrames,
                            IsPadForgeAsyncTailFlushReady());
                    if (reportFrames == 0)
                    {
                        TraceDirectStreamStatus();
                        int signaled = WaitHandle.WaitAny(waitHandles,
                            MaxFramesAvailableWaitMilliseconds);
                        if (signaled == 1 || stopping)
                        {
                            return;
                        }
                        Interlocked.Increment(ref directCadenceUnderruns);
                        continue;
                    }

                    // The sender uses PadForge's report shape, but presents it
                    // on the controller's 16 ms clock. Source callbacks are
                    // not a transport clock: they can arrive at 10/20 ms
                    // boundaries and produced the measured 7-22 ms write
                    // jitter that correlated with acoustic dropouts.
                    directDriftCorrectionEnabled = false;
                    PadForgeAsyncSubmissionResult result =
                        SubmitEncodedFramesPadForgeAsync(reportFrames);
                    if (result == PadForgeAsyncSubmissionResult.Failed)
                    {
                        return;
                    }
                    if (result == PadForgeAsyncSubmissionResult.NoFrames)
                    {
                        continue;
                    }
                    if (result == PadForgeAsyncSubmissionResult.Saturated)
                    {
                        // This is backpressure, not presentation cadence. The
                        // ordered eight-slot ring leaves every SBC frame queued
                        // until its oldest in-flight write releases a credit.
                        if (stoppingSignal.WaitOne(
                            PadForgeAsyncBackpressureWaitMilliseconds))
                        {
                            return;
                        }
                        continue;
                    }

                    TraceDirectStreamStatus();
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
                    CloseNativeHandle(highResolutionTimer);
                }
                timeEndPeriod(1);
            }
        }

        /// <summary>
        /// Presents one complete 16 ms report after each newly received,
        /// CRC-valid physical DS4 input report. This phase-locks host writes to
        /// the controller's actual Bluetooth connection-event clock instead of
        /// allowing an independent 16 ms timer to drift across that window.
        /// </summary>
        private void InputSynchronizedDirectStreamLoop()
        {
            var waitHandles = new WaitHandle[]
            {
                captureAvailable,
                stoppingSignal,
            };
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            long lastPresentedInputTick = 0;
            try
            {
                while (!stopping)
                {
                    int bufferedFrames;
                    lock (syncRoot)
                    {
                        bufferedFrames = encodedFrames.Count;
                    }

                    long inputTick = device.LastBluetoothInputReportTick;
                    if (bufferedFrames < DualShock4BluetoothAudioProtocol.
                            SpeakerLargeFramesPerReport ||
                        inputTick == 0 || inputTick == lastPresentedInputTick)
                    {
                        TraceDirectStreamStatus();
                        int signaled = WaitHandle.WaitAny(waitHandles, 2);
                        if (signaled == 1 || stopping)
                        {
                            return;
                        }
                        continue;
                    }

                    if (!speakerTransportEnabled &&
                        !EnsureSpeakerTransportEnabled())
                    {
                        return;
                    }

                    directDriftCorrectionEnabled = false;
                    PadForgeAsyncSubmissionResult result =
                        SubmitEncodedFramesPadForgeAsync(
                            DualShock4BluetoothAudioProtocol.
                                SpeakerLargeFramesPerReport);
                    if (result == PadForgeAsyncSubmissionResult.Failed)
                    {
                        return;
                    }
                    if (result == PadForgeAsyncSubmissionResult.Saturated)
                    {
                        if (stoppingSignal.WaitOne(
                            PadForgeAsyncBackpressureWaitMilliseconds))
                        {
                            return;
                        }
                        continue;
                    }
                    if (result == PadForgeAsyncSubmissionResult.Submitted)
                    {
                        lastPresentedInputTick = inputTick;
                    }
                    TraceDirectStreamStatus();
                }
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                {
                    AvRevertMmThreadCharacteristics(mmcssHandle);
                }
            }
        }

        private bool IsPadForgeAsyncTailFlushReady()
        {
            long lastPacket = Interlocked.Read(ref lastDirectPacketTimestamp);
            if (lastPacket == 0)
            {
                return false;
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - lastPacket;
            long elapsedMilliseconds = elapsedTicks * 1000 /
                Stopwatch.Frequency;
            return elapsedMilliseconds >=
                PadForgeAsyncTailFlushIdleMilliseconds;
        }

        private void ProductionReplayDirectStreamLoop()
        {
            var waitHandles = new WaitHandle[]
            {
                captureAvailable,
                stoppingSignal,
            };
            timeBeginPeriod(1);
            IntPtr highResolutionTimer = CreateHighResolutionTimer();
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            long cadenceTicks = DualShock4AudioTransportSettings.
                GetProductionReplayCadenceTicks(Stopwatch.Frequency);
            Interlocked.Exchange(ref directCurrentCadenceTicks,
                cadenceTicks);
            Interlocked.Exchange(ref directTargetCadenceTicks,
                cadenceTicks);
            try
            {
                while (!stopping)
                {
                    int bufferedFrames;
                    lock (syncRoot)
                    {
                        bufferedFrames = encodedFrames.Count;
                    }
                    if (DualShock4AudioTransportSettings.
                        ShouldStartProductionReplay(bufferedFrames))
                    {
                        break;
                    }

                    TraceDirectStreamStatus();
                    int signaled = WaitHandle.WaitAny(waitHandles,
                        MaxFramesAvailableWaitMilliseconds);
                    if (signaled == 1 || stopping)
                    {
                        return;
                    }
                }

                if (stopping || !EnsureSpeakerTransportEnabled())
                {
                    return;
                }

                SetProductionReplaySourceServo(enabled: false);
                if (!SubmitProductionReplayPrime())
                {
                    return;
                }
                Interlocked.Increment(ref directCadencePrimes);
                SetProductionReplaySourceServo(enabled: true);

                long nextTick = Stopwatch.GetTimestamp() + cadenceTicks;
                bool sourceReprimePending = false;
                while (!stopping)
                {
                    WaitForNextTick(ref nextTick, cadenceTicks,
                        highResolutionTimer,
                        DualShock4AudioTransportSettings.
                            ProductionReplayPrimeReports);

                    int availableFrames;
                    lock (syncRoot)
                    {
                        availableFrames = encodedFrames.Count;
                    }
                    long lastPacket = Interlocked.Read(
                        ref lastDirectPacketTimestamp);
                    long sourceIdleMilliseconds = lastPacket == 0 ? 0 :
                        Math.Max(0, (Stopwatch.GetTimestamp() - lastPacket) *
                            1000 / Stopwatch.Frequency);
                    if (!sourceReprimePending &&
                        DualShock4AudioTransportSettings.
                            ShouldBeginProductionReplayReprime(availableFrames,
                                sourceIdleMilliseconds))
                    {
                        sourceReprimePending = true;
                        SetProductionReplaySourceServo(enabled: false);
                    }

                    if (sourceReprimePending &&
                        DualShock4AudioTransportSettings.
                            ShouldStartProductionReplay(availableFrames))
                    {
                        if (!SubmitProductionReplayPrime())
                        {
                            return;
                        }
                        Interlocked.Increment(ref directCadencePrimes);
                        Interlocked.Increment(ref productionReplayReprimes);
                        sourceReprimePending = false;
                        SetProductionReplaySourceServo(enabled: true);
                    }
                    else
                    {
                        ProductionReplaySubmissionResult result =
                            SubmitProductionReplayFrame(
                                allowSilence: true,
                                forceSilence: sourceReprimePending);
                        if (result ==
                            ProductionReplaySubmissionResult.Failed)
                        {
                            return;
                        }
                        if (result !=
                            ProductionReplaySubmissionResult.Submitted)
                        {
                            Interlocked.Increment(
                                ref productionReplaySkippedTicks);
                        }
                    }
                    TraceDirectStreamStatus();
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
                    CloseNativeHandle(highResolutionTimer);
                }
                timeEndPeriod(1);
            }
        }

        private void SetProductionReplaySourceServo(bool enabled)
        {
            lock (syncRoot)
            {
                // The historical production lane accumulated a bounded,
                // unity-base queue correction per incoming stereo sample.
                // Reprime explicitly discarded the old fractional remainder.
                directDriftCorrectionAccumulator = 0.0;
                directAsrcBaseRatio = 1.0;
                directCurrentDriftRatio = 1.0;
                directTargetDriftRatio = 1.0;
                directDriftCorrectionEnabled = enabled;
            }
        }

        private bool SubmitProductionReplayPrime()
        {
            int submitted = 0;
            while (!stopping && submitted <
                DualShock4AudioTransportSettings.
                    ProductionReplayPrimeReports)
            {
                ProductionReplaySubmissionResult result =
                    SubmitProductionReplayFrame(allowSilence: false,
                        forceSilence: false);
                if (result == ProductionReplaySubmissionResult.Submitted)
                {
                    submitted++;
                    Interlocked.Increment(
                        ref productionReplayPrimeReports);
                    continue;
                }
                if (result == ProductionReplaySubmissionResult.Saturated)
                {
                    // A startup prime is intentionally contiguous. Capacity
                    // backpressure may delay it, but never consumes or replaces
                    // one of its unique source frames.
                    if (stoppingSignal.WaitOne(
                        PadForgeAsyncBackpressureWaitMilliseconds))
                    {
                        return false;
                    }
                    continue;
                }
                return false;
            }
            if (!stopping && Interlocked.CompareExchange(
                ref productionReplayPrimeLogged, 1, 0) == 0)
            {
                byte reportId = DualShock4AudioTransportSettings.
                    ProductionReplayFramesPerReport switch
                {
                    DualShock4BluetoothAudioProtocol.
                        SpeakerRealtimeFramesPerReport => 0x12,
                    DualShock4BluetoothAudioProtocol.
                        SpeakerSmallFramesPerReport => 0x14,
                    _ => 0x17,
                };
                byte[] report = reportId switch
                {
                    0x12 => speakerRealtimeReport,
                    0x14 => speakerSmallReport,
                    _ => speakerLargeReport,
                };
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth production replay primed " +
                    $"{DualShock4AudioTransportSettings.ProductionReplayPrimeReports} " +
                    $"unique 0x{reportId:X2} source reports back-to-back, retained " +
                    $"{DualShock4AudioTransportSettings.ProductionReplayRetainedSourceFrames} " +
                    $"source frames, mode=0x{report[2]:X2}, " +
                    $"slots={DualShock4AudioTransportSettings.ProductionReplaySlotCount}, " +
                    $"sourceServo=historical-slip@" +
                    $"{DualShock4AudioTransportSettings.ProductionReplayQueueServoTargetFrames}.",
                    false);
            }
            return !stopping;
        }

        private void FifoBufferedDirectStreamLoop()
        {
            var waitHandles = new WaitHandle[]
            {
                captureAvailable,
                stoppingSignal,
            };
            timeBeginPeriod(1);
            IntPtr highResolutionTimer = CreateHighResolutionTimer();
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            long cadenceTicks = DualShock4AudioTransportSettings.
                GetFifoBufferedCadenceTicks(Stopwatch.Frequency);
            Interlocked.Exchange(ref directCurrentCadenceTicks,
                cadenceTicks);
            Interlocked.Exchange(ref directTargetCadenceTicks,
                cadenceTicks);
            try
            {
                // Build exactly sixteen prime frames plus the independent
                // sixteen-frame source cushion before the A0/A1 control
                // barrier. Normal HID input remains active during the prime.
                while (!stopping)
                {
                    int bufferedFrames;
                    lock (syncRoot)
                    {
                        bufferedFrames = encodedFrames.Count;
                    }
                    if (DualShock4AudioTransportSettings.
                        ShouldStartFifoBuffered(bufferedFrames))
                    {
                        break;
                    }

                    TraceDirectStreamStatus();
                    int signaled = WaitHandle.WaitAny(waitHandles,
                        MaxFramesAvailableWaitMilliseconds);
                    if (signaled == 1 || stopping)
                    {
                        return;
                    }
                }

                if (stopping || !EnsureSpeakerTransportEnabled())
                {
                    return;
                }

                SetProductionReplaySourceServo(enabled: false);
                if (!SubmitFifoBufferedPrime())
                {
                    return;
                }
                Interlocked.Increment(ref directCadencePrimes);
                SetProductionReplaySourceServo(enabled: true);

                long nextTick = Stopwatch.GetTimestamp() + cadenceTicks;
                bool sourceReprimePending = false;
                while (!stopping)
                {
                    WaitForNextTick(ref nextTick, cadenceTicks,
                        highResolutionTimer,
                        DualShock4AudioTransportSettings.
                            FifoBufferedPrimeFrames);

                    int availableFrames;
                    lock (syncRoot)
                    {
                        availableFrames = encodedFrames.Count;
                    }
                    long lastPacket = Interlocked.Read(
                        ref lastDirectPacketTimestamp);
                    long sourceIdleMilliseconds = lastPacket == 0 ? 0 :
                        Math.Max(0, (Stopwatch.GetTimestamp() - lastPacket) *
                            1000 / Stopwatch.Frequency);
                    if (!sourceReprimePending &&
                        DualShock4AudioTransportSettings.
                            ShouldBeginFifoBufferedReprime(availableFrames,
                                sourceIdleMilliseconds))
                    {
                        sourceReprimePending = true;
                        SetProductionReplaySourceServo(enabled: false);
                    }

                    if (sourceReprimePending &&
                        DualShock4AudioTransportSettings.
                            ShouldStartFifoBuffered(availableFrames))
                    {
                        if (!SubmitFifoBufferedPrime())
                        {
                            return;
                        }
                        Interlocked.Increment(ref directCadencePrimes);
                        Interlocked.Increment(ref fifoBufferedReprimes);
                        sourceReprimePending = false;
                        SetProductionReplaySourceServo(enabled: true);
                    }
                    else
                    {
                        FifoBufferedSubmissionResult result =
                            SubmitFifoBufferedSteadyFrame(
                                allowSilence: true,
                                forceSilence: sourceReprimePending);
                        if (result == FifoBufferedSubmissionResult.Failed)
                        {
                            return;
                        }
                        if (result != FifoBufferedSubmissionResult.Submitted)
                        {
                            Interlocked.Increment(
                                ref fifoBufferedSkippedTicks);
                        }
                    }
                    TraceDirectStreamStatus();
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
                    CloseNativeHandle(highResolutionTimer);
                }
                timeEndPeriod(1);
            }
        }

        private bool SubmitFifoBufferedPrime()
        {
            if (!speakerWritePool.TryDrainOutstanding(1000,
                    out string drainError))
            {
                Interlocked.Increment(ref directHardWriteFailures);
                if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        "DualShock 4 Bluetooth fifo-buffered prime could not " +
                        $"drain the HID lane: {drainError}.", true);
                }
                return false;
            }

            int submitted = 0;
            while (!stopping && submitted <
                DualShock4AudioTransportSettings.FifoBufferedPrimeReports)
            {
                FifoBufferedSubmissionResult result =
                    SubmitFifoBufferedPrimeReport();
                if (result == FifoBufferedSubmissionResult.Submitted)
                {
                    submitted++;
                    Interlocked.Increment(ref fifoBufferedPrimeReports);
                    continue;
                }
                if (result == FifoBufferedSubmissionResult.Saturated)
                {
                    // Four dedicated slots bound this shallow FIFO probe. A
                    // capacity wait never consumes or replaces a source frame.
                    if (stoppingSignal.WaitOne(
                        PadForgeAsyncBackpressureWaitMilliseconds))
                    {
                        return false;
                    }
                    continue;
                }
                return false;
            }

            if (!stopping && Interlocked.CompareExchange(
                ref fifoBufferedPrimeLogged, 1, 0) == 0)
            {
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth fifo-buffered transport primed " +
                    $"{DualShock4AudioTransportSettings.FifoBufferedPrimeReports} " +
                    $"unique 0x17 source reports back-to-back " +
                    $"({DualShock4AudioTransportSettings.FifoBufferedPrimeFrames} " +
                    $"frames/{DualShock4AudioTransportSettings.FifoBufferedPrimeFrames * DualShock4AudioTransportSettings.FifoBufferedCadenceMilliseconds} ms), retained " +
                    $"{DualShock4AudioTransportSettings.FifoBufferedRetainedSourceFrames} " +
                    $"source frames, mode=0x{speakerLargeReport[2]:X2}, " +
                    $"primeSlots={DualShock4AudioTransportSettings.FifoBufferedPrimeSlotCount}, " +
                    $"steady=0x12@{DualShock4AudioTransportSettings.FifoBufferedCadenceMilliseconds}ms, " +
                    $"sourceServo=historical-slip@{DualShock4AudioTransportSettings.FifoBufferedQueueServoTargetFrames}.",
                    false);
            }
            return !stopping;
        }

        private void CreditBufferedDirectStreamLoop()
        {
            var waitHandles = new WaitHandle[]
            {
                captureAvailable,
                stoppingSignal,
            };
            timeBeginPeriod(1);
            IntPtr highResolutionTimer = CreateHighResolutionTimer();
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            long cadenceTicks = DualShock4AudioTransportSettings.
                GetCreditBufferedCadenceTicks(Stopwatch.Frequency);
            Interlocked.Exchange(ref directCurrentCadenceTicks,
                cadenceTicks);
            Interlocked.Exchange(ref directTargetCadenceTicks,
                cadenceTicks);
            try
            {
                // A physical HCI credit can carry four sequential SBC frames
                // in report 0x17. Wait for both the fourteen-report hardware
                // prime and a separate live-source cushion before arming A2.
                while (!stopping)
                {
                    int bufferedFrames;
                    lock (syncRoot)
                    {
                        bufferedFrames = encodedFrames.Count;
                    }
                    if (DualShock4AudioTransportSettings.
                        ShouldStartCreditBuffered(bufferedFrames))
                    {
                        break;
                    }

                    TraceDirectStreamStatus();
                    int signaled = WaitHandle.WaitAny(waitHandles,
                        MaxFramesAvailableWaitMilliseconds);
                    if (signaled == 1 || stopping)
                    {
                        return;
                    }
                }

                if (stopping || !EnsureSpeakerTransportEnabled())
                {
                    return;
                }

                ResetCreditBufferedSourceClock();
                if (!SubmitCreditBufferedPrime())
                {
                    return;
                }
                Interlocked.Increment(ref directCadencePrimes);

                long nextTick = Stopwatch.GetTimestamp() + cadenceTicks;
                bool sourceReprimePending = false;
                while (!stopping)
                {
                    WaitForNextTick(ref nextTick, cadenceTicks,
                        highResolutionTimer,
                        DualShock4AudioTransportSettings.
                            CreditBufferedPrimeReports);

                    int availableFrames;
                    lock (syncRoot)
                    {
                        availableFrames = encodedFrames.Count;
                    }
                    long lastPacket = Interlocked.Read(
                        ref lastDirectPacketTimestamp);
                    long sourceIdleMilliseconds = lastPacket == 0 ? 0 :
                        Math.Max(0, (Stopwatch.GetTimestamp() - lastPacket) *
                            1000 / Stopwatch.Frequency);
                    if (!sourceReprimePending &&
                        DualShock4AudioTransportSettings.
                            ShouldBeginCreditBufferedReprime(availableFrames,
                                sourceIdleMilliseconds))
                    {
                        sourceReprimePending = true;
                        ResetCreditBufferedSourceClock();
                    }

                    if (sourceReprimePending &&
                        DualShock4AudioTransportSettings.
                            ShouldStartCreditBuffered(availableFrames))
                    {
                        if (!SubmitCreditBufferedPrime())
                        {
                            return;
                        }
                        Interlocked.Increment(ref directCadencePrimes);
                        Interlocked.Increment(ref creditBufferedReprimes);
                        sourceReprimePending = false;
                    }
                    else
                    {
                        CreditBufferedSubmissionResult result =
                            SubmitCreditBufferedReport(
                                allowSilence: true,
                                forceSilence: sourceReprimePending);
                        if (result == CreditBufferedSubmissionResult.Failed)
                        {
                            return;
                        }
                        if (result != CreditBufferedSubmissionResult.Submitted)
                        {
                            Interlocked.Increment(
                                ref creditBufferedSkippedTicks);
                        }
                    }
                    TraceDirectStreamStatus();
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
                    CloseNativeHandle(highResolutionTimer);
                }
                timeEndPeriod(1);
            }
        }

        private void ResetCreditBufferedSourceClock()
        {
            lock (syncRoot)
            {
                // This first packing experiment deliberately has one clock:
                // the fixed 16 ms report cadence. Do not steer it from the
                // controller clock or apply the production-replay slip servo.
                directDriftCorrectionAccumulator = 0.0;
                directAsrcBaseRatio = 1.0;
                directCurrentDriftRatio = 1.0;
                directTargetDriftRatio = 1.0;
                directDriftCorrectionEnabled = false;
            }
        }

        private bool SubmitCreditBufferedPrime()
        {
            int submitted = 0;
            while (!stopping && submitted <
                DualShock4AudioTransportSettings.CreditBufferedPrimeReports)
            {
                CreditBufferedSubmissionResult result =
                    SubmitCreditBufferedReport(allowSilence: false,
                        forceSilence: false);
                if (result == CreditBufferedSubmissionResult.Submitted)
                {
                    submitted++;
                    Interlocked.Increment(ref creditBufferedPrimeReports);
                    continue;
                }
                if (result == CreditBufferedSubmissionResult.Saturated)
                {
                    // Preserve all four source frames until a physical-credit
                    // slot is available. Priming never substitutes silence for
                    // a unique source frame.
                    if (stoppingSignal.WaitOne(
                        PadForgeAsyncBackpressureWaitMilliseconds))
                    {
                        return false;
                    }
                    continue;
                }
                return false;
            }

            if (!stopping && Interlocked.CompareExchange(
                ref creditBufferedPrimeLogged, 1, 0) == 0)
            {
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth credit-buffered transport primed " +
                    $"{DualShock4AudioTransportSettings.CreditBufferedPrimeReports} " +
                    $"unique 0x17 source reports back-to-back " +
                    $"({DualShock4AudioTransportSettings.CreditBufferedPrimeFrames} " +
                    $"frames/{DualShock4AudioTransportSettings.CreditBufferedPrimeReports * DualShock4AudioTransportSettings.CreditBufferedCadenceMilliseconds} ms), retained " +
                    $"{DualShock4AudioTransportSettings.CreditBufferedRetainedSourceFrames} " +
                    $"source frames, mode=0x{speakerLargeReport[2]:X2}, " +
                    $"slots={DualShock4AudioTransportSettings.CreditBufferedSlotCount}, " +
                    "sourceServo=off.", false);
            }
            return !stopping;
        }

        private void ScheduledDirectStreamLoop()
        {
            var waitHandles = new WaitHandle[]
            {
                captureAvailable,
                stoppingSignal,
            };
            timeBeginPeriod(1);
            IntPtr highResolutionTimer = CreateHighResolutionTimer();
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            try
            {
                // The physical speaker is deliberately still disarmed here.
                // Wait until VIIPER's virtual speaker has supplied the complete
                // source cushion; then the control report, hardware prime, and
                // live stream are contiguous with no unserviced audio interval.
                while (!stopping)
                {
                    int bufferedFrames;
                    lock (syncRoot)
                    {
                        bufferedFrames = encodedFrames.Count;
                    }

                    if (DualShock4AudioTransportSettings.
                        ShouldStartScheduled(bufferedFrames))
                    {
                        break;
                    }

                    TraceDirectStreamStatus();
                    int signaled = WaitHandle.WaitAny(waitHandles,
                        MaxFramesAvailableWaitMilliseconds);
                    if (signaled == 1 || stopping)
                    {
                        return;
                    }
                }

                if (stopping || !EnsureSpeakerTransportEnabled())
                {
                    return;
                }

                // Build the controller cushion with paced 0x17 reports. The
                // independent PadForge/DS4AudioStreamer trace showed that
                // zero-interval 0x17+0x14 pairs produce 48-89 ms completion
                // holes on this Windows Bluetooth stack. Four-millisecond
                // spacing is fast enough to accumulate coverage but never
                // submits two reports in one scheduler turn.
                long primeCadenceTicks = DualShock4AudioTransportSettings.
                    GetScheduledPrimeCadenceTicks(Stopwatch.Frequency);
                long nextPrimeTick = Stopwatch.GetTimestamp();
                for (int index = 0; index <
                        DualShock4AudioTransportSettings.
                            ScheduledPrimeReports && !stopping; index++)
                {
                    if (index != 0)
                    {
                        WaitUntil(highResolutionTimer, nextPrimeTick);
                    }
                    SubmitEncodedFrames(DualShock4BluetoothAudioProtocol.
                        SpeakerDirectFramesPerReport);
                    // Rebase from the actual submission time. A delayed HID
                    // write must never compress the next prime interval.
                    nextPrimeTick = Stopwatch.GetTimestamp() +
                        primeCadenceTicks;
                }
                Interlocked.Increment(ref directCadencePrimes);
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth scheduled speaker primed " +
                    $"{DualShock4AudioTransportSettings.ScheduledPrimeReports} " +
                    $"paced 0x17 reports at " +
                    $"{DualShock4AudioTransportSettings.ScheduledPrimeCadenceMilliseconds} ms " +
                    $"and retained " +
                    $"{DualShock4AudioTransportSettings.ScheduledRetainedSourceFrames} " +
                    "source frames; steady cadence=16 ms, catch-up=disabled.",
                    false);
                directDriftCorrectionEnabled = directDriftMode !=
                    DualShock4AudioDriftMode.Off;

                long nominalCadenceTicks = DualShock4AudioTransportSettings.
                    GetScheduledSteadyCadenceTicks(Stopwatch.Frequency);
                long cadenceTicks = nominalCadenceTicks;
                Interlocked.Exchange(ref directCurrentCadenceTicks,
                    cadenceTicks);
                Interlocked.Exchange(ref directTargetCadenceTicks,
                    cadenceTicks);
                long rebaseLatenessTicks = DualShock4AudioReportScheduler.
                    GetDirectRebaseLatenessTicks(Stopwatch.Frequency);
                long nextTick = Stopwatch.GetTimestamp() + cadenceTicks;
                bool sourceReprimePending = false;
                while (!stopping)
                {
                    double controllerClockRatio = device.
                        BluetoothControllerClockRatio;
                    cadenceTicks = DualShock4AudioReportScheduler.
                        SteerCadenceTicks(cadenceTicks,
                            nominalCadenceTicks, controllerClockRatio);
                    long targetCadenceTicks =
                        DualShock4AudioReportScheduler.
                            MapControllerClockToCadenceTicks(
                                nominalCadenceTicks,
                                controllerClockRatio);
                    Interlocked.Exchange(ref directCurrentCadenceTicks,
                        cadenceTicks);
                    Interlocked.Exchange(ref directTargetCadenceTicks,
                        targetCadenceTicks);
                    WaitForNextTick(ref nextTick, cadenceTicks,
                        highResolutionTimer,
                        DualShock4AudioTransportSettings.
                            ScheduledPrimeReports,
                        rebaseLatenessTicks);

                    int availableFrames;
                    lock (syncRoot)
                    {
                        availableFrames = encodedFrames.Count;
                    }
                    long lastPacket = Interlocked.Read(
                        ref lastDirectPacketTimestamp);
                    if (!sourceReprimePending && availableFrames == 0 &&
                        lastPacket != 0 && Stopwatch.GetTimestamp() -
                            lastPacket >= Stopwatch.Frequency *
                                DirectSourceIdleThresholdMilliseconds / 1000)
                    {
                        sourceReprimePending = true;
                        directDriftCorrectionEnabled = false;
                    }

                    if (sourceReprimePending && availableFrames >=
                        DualShock4AudioTransportSettings.
                            ScheduledRetainedSourceFrames)
                    {
                        // Silence reports have kept the controller clock and
                        // FIFO alive while the source was idle. Resume with one
                        // live report on this same 16 ms tick; a second startup
                        // burst would destroy the no-catch-up invariant.
                        sourceReprimePending = false;
                        directDriftCorrectionEnabled = directDriftMode !=
                            DualShock4AudioDriftMode.Off;
                    }
                    else
                    {
                        SubmitEncodedFrames(DualShock4BluetoothAudioProtocol.
                            SpeakerDirectFramesPerReport,
                            allowSilence: true,
                            forceSilence: sourceReprimePending);
                    }
                    TraceDirectStreamStatus();
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
                    CloseNativeHandle(highResolutionTimer);
                }
                timeEndPeriod(1);
            }
        }

        private int GetDirectPcmFrameCount()
        {
            lock (syncRoot)
            {
                return GetDirectPcmFrameCountLocked();
            }
        }

        private int GetDirectPcmFrameCountLocked()
        {
            int bytes = -directPcmPacketOffset;
            foreach (DirectPcmPacket packet in directPcmPackets)
            {
                bytes += packet.Length;
            }
            return Math.Max(0, bytes / (Channels * sizeof(short)));
        }

        private int ReadDirectPcmFrames(int frameCount)
        {
            Array.Clear(directTickPcm, 0,
                frameCount * Channels * sizeof(short));
            lock (syncRoot)
            {
                if (frameCount <= 0 || frameCount >
                    DirectMaximumFramesPerTick)
                {
                    return 0;
                }

                int framesAvailable = Math.Min(frameCount,
                    GetDirectPcmFrameCountLocked());
                int bytesRemaining = framesAvailable * Channels *
                    sizeof(short);
                int destinationOffset = 0;
                while (bytesRemaining > 0)
                {
                    DirectPcmPacket packet = directPcmPackets.Peek();
                    int available = packet.Length - directPcmPacketOffset;
                    int copyLength = Math.Min(available, bytesRemaining);
                    Buffer.BlockCopy(packet.Buffer, directPcmPacketOffset,
                        directTickPcm, destinationOffset, copyLength);
                    directPcmPacketOffset += copyLength;
                    destinationOffset += copyLength;
                    bytesRemaining -= copyLength;
                    if (directPcmPacketOffset == packet.Length)
                    {
                        directPcmPackets.Dequeue();
                        freeDirectPcmPackets.Enqueue(packet.Buffer);
                        directPcmPacketOffset = 0;
                    }
                }
                return framesAvailable;
            }
        }

        private void ProcessDirectPcmTick(int frameCount)
        {
            for (int frame = 0; frame < frameCount; frame++)
            {
                if (pendingPcmCount > pendingPcm.Length - Channels)
                {
                    EncodePendingPcmFrames();
                }

                int offset = frame * Channels * sizeof(short);
                pendingPcm[pendingPcmCount++] = (short)(directTickPcm[offset] |
                    directTickPcm[offset + 1] << 8);
                pendingPcm[pendingPcmCount++] = (short)(directTickPcm[offset + 2] |
                    directTickPcm[offset + 3] << 8);
            }

            EncodePendingPcmFrames();
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
            IntPtr highResolutionTimer = CreateHighResolutionTimer();
            IntPtr mmcssHandle = RegisterMultimediaScheduler();
            long cadenceTicks = (long)Math.Round(Stopwatch.Frequency *
                ReportCadenceMilliseconds / 1000.0);
            try
            {
                // Build 36 frames, queue the 20-frame (80 ms) hardware cushion,
                // and retain sixteen source frames for capture callback jitter.
                captureAvailable.WaitOne(100);
                for (int prime = 0; prime <
                        DualShock4BluetoothAudioProtocol.
                            SpeakerMinimumBufferedFrames && !stopping; prime++)
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
                    processor.Process(sourceSamples, SourceFramesPerTick);
                    ResampleAndEncode(SourceFramesPerTick);
                }
                for (int prime = 0; prime <
                        DualShock4BluetoothAudioProtocol.
                            SpeakerRealtimePrimeFrames && !stopping; prime++)
                {
                    SubmitEncodedFrames(DualShock4BluetoothAudioProtocol.
                        SpeakerRealtimeFramesPerReport, allowSilence: true);
                }
                TraceDirectStreamStatus();

                long nextTick = Stopwatch.GetTimestamp() + cadenceTicks;
                while (!stopping)
                {
                    WaitForNextTick(ref nextTick, cadenceTicks,
                        highResolutionTimer);
                    Array.Clear(sourceSamples, 0, sourceSamples.Length);
                    int samplesRead;
                    lock (syncRoot)
                    {
                        samplesRead = stopping || sampleProvider == null ? 0 :
                            sampleProvider.Read(sourceSamples, 0,
                                sourceSamples.Length);
                    }
                    if (HasAudibleSamples(sourceSamples, samplesRead))
                    {
                        lastAudibleTick = Environment.TickCount64;
                    }
                    processor.Process(sourceSamples, SourceFramesPerTick);
                    ResampleAndEncode(SourceFramesPerTick);
                    DrainEncodedFrames();
                    TraceDirectStreamStatus();
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
                    CloseNativeHandle(highResolutionTimer);
                }
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
            int queueLimit = directTransportMode ==
                    DualShock4AudioTransportMode.PadForgeAsync ||
                    directTransportMode ==
                        DualShock4AudioTransportMode.PadForgeSpeakerOnly ||
                    directTransportMode ==
                        DualShock4AudioTransportMode.InputSynchronized ||
                    directTransportMode ==
                        DualShock4AudioTransportMode.SourceDriven ?
                DualShock4AudioTransportSettings.
                    PadForgeAsyncEncodedFrameQueueLimit :
                EncodedFrameQueueLimit;
            while (pendingPcmCount - consumed >= PcmValuesPerSbcFrame)
            {
                for (int sample = 0; sample < SamplesPerSbcFrame; sample++)
                {
                    pcmLeft[sample] = pendingPcm[consumed + sample * Channels];
                    pcmRight[sample] = pendingPcm[consumed + sample * Channels + 1];
                }

                byte[] frame;
                if (encodedFrames.Count >= queueLimit)
                {
                    // Match the reference's 12-frame (48 ms) latency bound.
                    // Reuse the oldest queued frame rather than allowing a
                    // Bluetooth credit stall to accumulate audible latency.
                    frame = encodedFrames.Dequeue();
                    Interlocked.Increment(ref directFramesDroppedForLatency);
                }
                else if (freeEncodedFrames.Count > 0)
                {
                    frame = freeEncodedFrames.Dequeue();
                }
                else if (encodedFrames.Count > 0)
                {
                    // All fixed pool buffers are queued. Reuse the oldest one
                    // to bound latency without allocating on the audio lane.
                    frame = encodedFrames.Dequeue();
                }
                else
                {
                    break;
                }

                bool encoded = encoder.Encode(pcmLeft, pcmRight, frame);
                consumed += PcmValuesPerSbcFrame;
                if (!encoded)
                {
                    freeEncodedFrames.Enqueue(frame);
                    if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                    {
                        AppLogger.LogToGui(
                            "DualShock 4 Bluetooth speaker SBC encoder returned an invalid frame.",
                            true);
                    }

                    continue;
                }

                encodedFrames.Enqueue(frame);
                Interlocked.Increment(ref directFramesEncoded);
                CaptureDiagnosticSbc(frame);
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
            SubmitEncodedFrames(DualShock4BluetoothAudioProtocol.
                SpeakerRealtimeFramesPerReport, allowSilence: true);
        }

        private byte[] GetEncodedSilenceFrame()
        {
            return speakerSilenceFrame;
        }

        internal static int GetRealFrameCountForSubmission(int requestedFrames,
            int availableFrames, bool allowSilence, bool forceSilence)
        {
            if (requestedFrames <= 0 || availableFrames < 0)
            {
                throw new ArgumentOutOfRangeException(requestedFrames <= 0 ?
                    nameof(requestedFrames) : nameof(availableFrames));
            }

            if (forceSilence)
            {
                return 0;
            }

            if (availableFrames >= requestedFrames)
            {
                return requestedFrames;
            }

            // A negative result means that the caller must wait. Otherwise the
            // available tail is presented first and only the missing positions
            // are padded with a valid encoded-silence frame.
            return allowSilence ? availableFrames : -1;
        }

        private void SubmitEncodedFrames(int count, bool allowSilence = false,
            bool forceSilence = false)
        {
            byte[] report;
            bool containsSyntheticSilence = false;
            bool submitted = EnsureSpeakerWritePool();
            bool hardFailure = false;
            lock (syncRoot)
            {
                if ((count != DualShock4BluetoothAudioProtocol.
                        SpeakerRealtimeFramesPerReport &&
                     count != DualShock4BluetoothAudioProtocol.
                        SpeakerSmallFramesPerReport &&
                     count != DualShock4BluetoothAudioProtocol.
                        SpeakerLargeFramesPerReport) || count <= 0)
                {
                    return;
                }

                int realFrameCount = GetRealFrameCountForSubmission(count,
                    encodedFrames.Count, allowSilence, forceSilence);
                if (realFrameCount < 0)
                {
                    return;
                }

                for (int index = 0; index < realFrameCount; index++)
                {
                    speakerFrameBatch[index] = encodedFrames.Dequeue();
                }
                for (int index = realFrameCount; index < count; index++)
                {
                    speakerFrameBatch[index] = GetEncodedSilenceFrame();
                }

                containsSyntheticSilence = realFrameCount < count;
                if (containsSyntheticSilence)
                {
                    Interlocked.Increment(ref syntheticSilenceReports);
                    long lastPacket = Interlocked.Read(
                        ref lastDirectPacketTimestamp);
                    bool activeDirectStream = directSpeakerSource == null ||
                        (lastPacket != 0 && Stopwatch.GetTimestamp() -
                            lastPacket < Stopwatch.Frequency / 5);
                    if (activeDirectStream)
                    {
                        Interlocked.Increment(ref directCadenceUnderruns);
                    }
                }

                report = count switch
                {
                    DualShock4BluetoothAudioProtocol.
                        SpeakerRealtimeFramesPerReport => speakerRealtimeReport,
                    DualShock4BluetoothAudioProtocol.
                        SpeakerLargeFramesPerReport => speakerLargeReport,
                    _ => speakerSmallReport,
                };
                if (submitted)
                {
                    // Hold the physical audio-state gate from the moment the
                    // A0/A1 mode byte is chosen until this report is queued.
                    // A microphone 0x11 transition takes the same gate, drains
                    // all older speaker writes, and therefore cannot be
                    // followed by a stale speaker-only A0 packet.
                    device.ReadDualShock4BluetoothAudioModeSynchronized(
                        microphoneEnabled =>
                        {
                            DualShock4BluetoothAudioProtocol.WriteSpeakerReport(
                                report, frameNumber, speakerFrameBatch, count,
                                microphoneEnabled: microphoneEnabled,
                                bluetoothPollRate: GetBluetoothPollRate());
                            submitted = speakerWritePool.TrySend(report,
                                out hardFailure);
                        });
                }
                frameNumber += (ushort)count;
                for (int index = 0; index < count; index++)
                {
                    if (index < realFrameCount)
                    {
                        freeEncodedFrames.Enqueue(speakerFrameBatch[index]);
                    }
                    speakerFrameBatch[index] = null;
                }
            }

            if (!submitted)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                }
                else
                {
                    Interlocked.Increment(ref directWriteSaturations);
                }
                if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        $"DualShock 4 Bluetooth speaker HID transport failed: " +
                        (hardFailure ? "dedicated audio handle write failed" :
                            "dedicated overlapped audio queue is full"),
                        true);
                }
            }
            else if (Interlocked.Increment(ref reportsSubmitted) == 1)
            {
                RecordReportSize(count);
                RecordDirectReportTimestamp();
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth speaker submitted its first SBC report " +
                    $"(id=0x{report[0]:X2}, mode=0x{report[2]:X2}, " +
                    $"frames={count}, bytes={report.Length}, " +
                    $"syntheticSilence={containsSyntheticSilence}).",
                    false);
            }
            else if (submitted)
            {
                RecordReportSize(count);
                RecordDirectReportTimestamp();
            }
        }

        /// <summary>
        /// Presents one complete reference-transport report and waits for its
        /// HID completion before returning. Queue ownership is held only while
        /// frames are removed or recycled; VIIPER can therefore keep encoding
        /// source PCM while Bluetooth completes the in-flight report.
        /// </summary>
        private bool SubmitEncodedFramesAndWait(int count)
        {
            if (count != DualShock4BluetoothAudioProtocol.
                    SpeakerSmallFramesPerReport &&
                count != DualShock4BluetoothAudioProtocol.
                    SpeakerLargeFramesPerReport)
            {
                return false;
            }

            if (!EnsureSpeakerWritePool())
            {
                return false;
            }

            byte[] report;
            ushort reportFrameNumber;
            lock (syncRoot)
            {
                if (encodedFrames.Count < count)
                {
                    return false;
                }

                for (int index = 0; index < count; index++)
                {
                    speakerFrameBatch[index] = encodedFrames.Dequeue();
                }
                report = count == DualShock4BluetoothAudioProtocol.
                    SpeakerLargeFramesPerReport ? speakerLargeReport :
                    speakerSmallReport;
                reportFrameNumber = frameNumber;
                frameNumber += (ushort)count;
            }

            bool submitted = false;
            bool hardFailure = false;
            device.ReadDualShock4BluetoothAudioModeSynchronized(
                microphoneEnabled =>
                {
                    DualShock4BluetoothAudioProtocol.WriteSpeakerReport(
                        report, reportFrameNumber, speakerFrameBatch, count,
                        microphoneEnabled: microphoneEnabled,
                        bluetoothPollRate: GetBluetoothPollRate());
                    submitted = speakerWritePool.SendAndWait(report,
                        out hardFailure);
                });

            lock (syncRoot)
            {
                for (int index = 0; index < count; index++)
                {
                    freeEncodedFrames.Enqueue(speakerFrameBatch[index]);
                    speakerFrameBatch[index] = null;
                }
            }

            if (!submitted)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                }
                else
                {
                    Interlocked.Increment(ref directWriteSaturations);
                }
                if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        "DualShock 4 Bluetooth reference speaker transport " +
                        (hardFailure ? "write completion failed." :
                            "could not acquire an in-order HID slot."), true);
                }
                return false;
            }

            RecordReportSize(count);
            RecordDirectReportTimestamp();
            if (Interlocked.Increment(ref reportsSubmitted) == 1)
            {
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth speaker submitted its first " +
                    $"reference SBC report (id=0x{report[0]:X2}, " +
                    $"mode=0x{report[2]:X2}, frames={count}, " +
                    $"bytes={report.Length}, completion-paced=true).", false);
            }
            return true;
        }

        private PadForgeAsyncSubmissionResult
            SubmitEncodedFramesPadForgeAsync(int count)
        {
            if (count != DualShock4BluetoothAudioProtocol.
                    SpeakerSmallFramesPerReport &&
                count != DualShock4BluetoothAudioProtocol.
                    SpeakerLargeFramesPerReport)
            {
                return PadForgeAsyncSubmissionResult.Failed;
            }

            if (!EnsureSpeakerWritePool())
            {
                return PadForgeAsyncSubmissionResult.Failed;
            }

            byte[] report = count == DualShock4BluetoothAudioProtocol.
                SpeakerLargeFramesPerReport ? speakerLargeReport :
                speakerSmallReport;
            bool prepared = false;
            bool saturated = false;
            bool hardFailure = false;
            bool submitted = false;
            device.ReadDualShock4BluetoothAudioModeSynchronized(
                microphoneEnabled =>
                {
                    submitted = speakerWritePool.TrySendPrepared(report,
                        DualShock4AudioTransportSettings.
                            PadForgeAsyncSlotCount,
                        () =>
                        {
                            lock (syncRoot)
                            {
                                if (encodedFrames.Count < count)
                                {
                                    return false;
                                }

                                for (int index = 0; index < count; index++)
                                {
                                    speakerFrameBatch[index] =
                                        encodedFrames.Dequeue();
                                }
                                DualShock4BluetoothAudioProtocol.
                                    WriteSpeakerReport(report, frameNumber,
                                        speakerFrameBatch, count,
                                        microphoneEnabled:
                                            microphoneEnabled,
                                        bluetoothPollRate:
                                            GetBluetoothPollRate());
                                if (directTransportMode ==
                                    DualShock4AudioTransportMode.
                                        PadForgeSpeakerOnly)
                                {
                                    ApplySpeakerOnlyAudioMode(report,
                                        "padforge-speaker-only", 0xA2);
                                }
                                frameNumber += (ushort)count;
                                prepared = true;
                                return true;
                            }
                        }, out hardFailure, out saturated);
                });

            if (prepared)
            {
                // TrySendPrepared has already copied the complete report into
                // its pinned slot, so the fixed SBC buffers can be reused even
                // while that slot remains pending.
                lock (syncRoot)
                {
                    for (int index = 0; index < count; index++)
                    {
                        freeEncodedFrames.Enqueue(speakerFrameBatch[index]);
                        speakerFrameBatch[index] = null;
                    }
                }
            }

            if (saturated)
            {
                Interlocked.Increment(ref directWriteSaturations);
                return PadForgeAsyncSubmissionResult.Saturated;
            }
            if (!prepared)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                    if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                    {
                        AppLogger.LogToGui(
                            "DualShock 4 Bluetooth PadForge async speaker " +
                            "transport could not reserve a healthy HID slot.",
                            true);
                    }
                    return PadForgeAsyncSubmissionResult.Failed;
                }
                return PadForgeAsyncSubmissionResult.NoFrames;
            }
            if (!submitted)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                }
                if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        "DualShock 4 Bluetooth PadForge async speaker " +
                        "transport write failed.", true);
                }
                return PadForgeAsyncSubmissionResult.Failed;
            }

            RecordReportSize(count);
            RecordDirectReportTimestamp();
            if (Interlocked.Increment(ref reportsSubmitted) == 1)
            {
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth speaker submitted its first " +
                    $"PadForge async SBC report (id=0x{report[0]:X2}, " +
                    $"mode=0x{report[2]:X2}, frames={count}, " +
                    $"bytes={report.Length}, slots=" +
                    $"{DualShock4AudioTransportSettings.PadForgeAsyncSlotCount}).",
                    false);
            }
            return PadForgeAsyncSubmissionResult.Submitted;
        }

        private ProductionReplaySubmissionResult
            SubmitProductionReplayFrame(bool allowSilence,
                bool forceSilence,
                string transportLabel = "production replay")
        {
            if (!EnsureSpeakerWritePool())
            {
                return ProductionReplaySubmissionResult.Failed;
            }

            const int count = DualShock4AudioTransportSettings.
                ProductionReplayFramesPerReport;
            byte[] report = count switch
            {
                DualShock4BluetoothAudioProtocol.
                    SpeakerRealtimeFramesPerReport => speakerRealtimeReport,
                DualShock4BluetoothAudioProtocol.
                    SpeakerLargeFramesPerReport => speakerLargeReport,
                _ => speakerSmallReport,
            };
            bool prepared = false;
            bool saturated = false;
            bool hardFailure = false;
            bool submitted = false;
            bool containsSyntheticSilence = false;
            int realFrameCount = 0;
            bool productionA0 = directTransportMode ==
                DualShock4AudioTransportMode.ProductionA0;
            bool productionDuplexA1 = directTransportMode ==
                DualShock4AudioTransportMode.ProductionDuplexA1;
            bool fifoBufferedDuplex = directTransportMode ==
                DualShock4AudioTransportMode.FifoBuffered;
            device.ReadDualShock4BluetoothAudioModeSynchronized(
                microphoneEnabled =>
                {
                    bool effectiveMicrophoneEnabled = !productionA0 &&
                        microphoneEnabled;
                    submitted = speakerWritePool.TrySendPrepared(report,
                        DualShock4AudioTransportSettings.
                            ProductionReplaySlotCount,
                        () =>
                        {
                            lock (syncRoot)
                            {
                                realFrameCount =
                                    GetRealFrameCountForSubmission(count,
                                        encodedFrames.Count, allowSilence,
                                        forceSilence);
                                if (realFrameCount < 0)
                                {
                                    return false;
                                }

                                for (int index = 0;
                                    index < realFrameCount; index++)
                                {
                                    speakerFrameBatch[index] =
                                        encodedFrames.Dequeue();
                                }
                                for (int index = realFrameCount;
                                    index < count; index++)
                                {
                                    speakerFrameBatch[index] =
                                        GetEncodedSilenceFrame();
                                }
                                containsSyntheticSilence =
                                    realFrameCount < count;
                                DualShock4BluetoothAudioProtocol.
                                    WriteSpeakerReport(report, frameNumber,
                                        speakerFrameBatch, count,
                                        microphoneEnabled:
                                            effectiveMicrophoneEnabled,
                                        bluetoothPollRate:
                                            GetBluetoothPollRate());
                                if (productionA0)
                                {
                                    ApplyProductionA0AudioMode(report);
                                }
                                else if (productionDuplexA1)
                                {
                                    ApplyProductionDuplexAudioMode(report,
                                        effectiveMicrophoneEnabled);
                                }
                                else if (fifoBufferedDuplex)
                                {
                                    ApplyFifoBufferedAudioMode(report,
                                        effectiveMicrophoneEnabled);
                                }
                                else
                                {
                                    ApplyProductionReplayAudioMode(report,
                                        effectiveMicrophoneEnabled);
                                }
                                frameNumber = unchecked((ushort)(frameNumber +
                                    count));
                                prepared = true;
                                return true;
                            }
                        }, out hardFailure, out saturated);
                });

            if (prepared)
            {
                lock (syncRoot)
                {
                    for (int index = 0; index < count; index++)
                    {
                        if (index < realFrameCount)
                        {
                            freeEncodedFrames.Enqueue(
                                speakerFrameBatch[index]);
                        }
                        speakerFrameBatch[index] = null;
                    }
                }
            }

            if (saturated)
            {
                Interlocked.Increment(ref directWriteSaturations);
                return ProductionReplaySubmissionResult.Saturated;
            }
            if (!prepared)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                    if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                    {
                        AppLogger.LogToGui(
                            $"DualShock 4 Bluetooth {transportLabel} " +
                            "could not reserve a healthy HID slot.", true);
                    }
                    return ProductionReplaySubmissionResult.Failed;
                }
                return ProductionReplaySubmissionResult.NoFrames;
            }
            if (!submitted)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                }
                if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        $"DualShock 4 Bluetooth {transportLabel} write " +
                        "failed.", true);
                }
                return ProductionReplaySubmissionResult.Failed;
            }

            if (containsSyntheticSilence)
            {
                Interlocked.Increment(ref syntheticSilenceReports);
                long lastPacket = Interlocked.Read(
                    ref lastDirectPacketTimestamp);
                if (lastPacket != 0 && Stopwatch.GetTimestamp() - lastPacket <
                    Stopwatch.Frequency / 5)
                {
                    Interlocked.Increment(ref directCadenceUnderruns);
                }
            }
            RecordReportSize(count);
            RecordDirectReportTimestamp();
            Interlocked.Increment(ref reportsSubmitted);
            return ProductionReplaySubmissionResult.Submitted;
        }

        private FifoBufferedSubmissionResult SubmitFifoBufferedSteadyFrame(
            bool allowSilence, bool forceSilence)
        {
            ProductionReplaySubmissionResult result =
                SubmitProductionReplayFrame(allowSilence, forceSilence,
                    transportLabel: "fifo-buffered steady transport");
            return result switch
            {
                ProductionReplaySubmissionResult.Submitted =>
                    FifoBufferedSubmissionResult.Submitted,
                ProductionReplaySubmissionResult.Saturated =>
                    FifoBufferedSubmissionResult.Saturated,
                ProductionReplaySubmissionResult.NoFrames =>
                    FifoBufferedSubmissionResult.NoFrames,
                _ => FifoBufferedSubmissionResult.Failed,
            };
        }

        private FifoBufferedSubmissionResult
            SubmitFifoBufferedPrimeReport()
        {
            if (!EnsureSpeakerWritePool())
            {
                return FifoBufferedSubmissionResult.Failed;
            }

            const int count = DualShock4AudioTransportSettings.
                FifoBufferedPrimeFramesPerReport;
            byte[] report = speakerLargeReport;
            bool prepared = false;
            bool saturated = false;
            bool hardFailure = false;
            bool submitted = false;
            device.ReadDualShock4BluetoothAudioModeSynchronized(
                microphoneEnabled =>
                {
                    submitted = speakerWritePool.TrySendPrepared(report,
                        DualShock4AudioTransportSettings.
                            FifoBufferedPrimeSlotCount,
                        () =>
                        {
                            lock (syncRoot)
                            {
                                int selected = DualShock4AudioTransportSettings.
                                    SelectFifoBufferedPrimeFrameCount(
                                        encodedFrames.Count);
                                if (selected != count)
                                {
                                    return false;
                                }

                                for (int index = 0; index < count; index++)
                                {
                                    speakerFrameBatch[index] =
                                        encodedFrames.Dequeue();
                                }
                                DualShock4BluetoothAudioProtocol.
                                    WriteSpeakerReport(report, frameNumber,
                                        speakerFrameBatch, count,
                                        microphoneEnabled:
                                            microphoneEnabled,
                                        bluetoothPollRate:
                                            GetBluetoothPollRate());
                                ApplyFifoBufferedAudioMode(report,
                                    microphoneEnabled);
                                frameNumber = DualShock4AudioTransportSettings.
                                    AdvanceFifoBufferedPrimeFrameNumber(
                                        frameNumber);
                                prepared = true;
                                return true;
                            }
                        }, out hardFailure, out saturated);
                });

            if (prepared)
            {
                lock (syncRoot)
                {
                    for (int index = 0; index < count; index++)
                    {
                        freeEncodedFrames.Enqueue(speakerFrameBatch[index]);
                        speakerFrameBatch[index] = null;
                    }
                }
            }

            if (saturated)
            {
                Interlocked.Increment(ref directWriteSaturations);
                return FifoBufferedSubmissionResult.Saturated;
            }
            if (!prepared)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                    if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                    {
                        AppLogger.LogToGui(
                            "DualShock 4 Bluetooth fifo-buffered prime " +
                            "could not reserve a healthy HID slot.", true);
                    }
                    return FifoBufferedSubmissionResult.Failed;
                }
                return FifoBufferedSubmissionResult.NoFrames;
            }
            if (!submitted)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                }
                if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        "DualShock 4 Bluetooth fifo-buffered prime write " +
                        "failed.", true);
                }
                return FifoBufferedSubmissionResult.Failed;
            }

            RecordReportSize(count);
            RecordDirectReportTimestamp();
            Interlocked.Increment(ref reportsSubmitted);
            return FifoBufferedSubmissionResult.Submitted;
        }

        private CreditBufferedSubmissionResult
            SubmitCreditBufferedReport(bool allowSilence,
                bool forceSilence)
        {
            if (!EnsureSpeakerWritePool())
            {
                return CreditBufferedSubmissionResult.Failed;
            }

            const int count = DualShock4AudioTransportSettings.
                CreditBufferedFramesPerReport;
            byte[] report = speakerLargeReport;
            bool prepared = false;
            bool saturated = false;
            bool hardFailure = false;
            bool submitted = false;
            bool containsSyntheticSilence = false;
            int realFrameCount = 0;
            device.ReadDualShock4BluetoothAudioModeSynchronized(
                _ =>
                {
                    submitted = speakerWritePool.TrySendPrepared(report,
                        DualShock4AudioTransportSettings.
                            CreditBufferedSlotCount,
                        () =>
                        {
                            lock (syncRoot)
                            {
                                realFrameCount =
                                    GetRealFrameCountForSubmission(count,
                                        encodedFrames.Count, allowSilence,
                                        forceSilence);
                                if (realFrameCount < 0)
                                {
                                    return false;
                                }

                                for (int index = 0;
                                    index < realFrameCount; index++)
                                {
                                    speakerFrameBatch[index] =
                                        encodedFrames.Dequeue();
                                }
                                for (int index = realFrameCount;
                                    index < count; index++)
                                {
                                    // This is a complete SBC frame produced by
                                    // the same encoder configuration as live
                                    // audio, never a zero-filled HID payload.
                                    speakerFrameBatch[index] =
                                        GetEncodedSilenceFrame();
                                }
                                containsSyntheticSilence =
                                    realFrameCount < count;
                                DualShock4BluetoothAudioProtocol.
                                    WriteSpeakerReport(report, frameNumber,
                                        speakerFrameBatch, count,
                                        microphoneEnabled: false,
                                        bluetoothPollRate:
                                            GetBluetoothPollRate());
                                ApplyCreditBufferedAudioMode(report);
                                frameNumber = DualShock4AudioTransportSettings.
                                    AdvanceCreditBufferedFrameNumber(
                                        frameNumber);
                                prepared = true;
                                return true;
                            }
                        }, out hardFailure, out saturated);
                });

            if (prepared)
            {
                lock (syncRoot)
                {
                    for (int index = 0; index < count; index++)
                    {
                        if (index < realFrameCount)
                        {
                            freeEncodedFrames.Enqueue(
                                speakerFrameBatch[index]);
                        }
                        speakerFrameBatch[index] = null;
                    }
                }
            }

            if (saturated)
            {
                Interlocked.Increment(ref directWriteSaturations);
                return CreditBufferedSubmissionResult.Saturated;
            }
            if (!prepared)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                    if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                    {
                        AppLogger.LogToGui(
                            "DualShock 4 Bluetooth credit-buffered transport " +
                            "could not reserve a healthy HID slot.", true);
                    }
                    return CreditBufferedSubmissionResult.Failed;
                }
                return CreditBufferedSubmissionResult.NoFrames;
            }
            if (!submitted)
            {
                if (hardFailure)
                {
                    Interlocked.Increment(ref directHardWriteFailures);
                }
                if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                {
                    AppLogger.LogToGui(
                        "DualShock 4 Bluetooth credit-buffered transport " +
                        "write failed.", true);
                }
                return CreditBufferedSubmissionResult.Failed;
            }

            if (containsSyntheticSilence)
            {
                Interlocked.Increment(ref syntheticSilenceReports);
                long lastPacket = Interlocked.Read(
                    ref lastDirectPacketTimestamp);
                if (lastPacket != 0 && Stopwatch.GetTimestamp() - lastPacket <
                    Stopwatch.Frequency / 5)
                {
                    Interlocked.Increment(ref directCadenceUnderruns);
                }
            }
            RecordReportSize(count);
            RecordDirectReportTimestamp();
            if (Interlocked.Increment(ref reportsSubmitted) == 1)
            {
                AppLogger.LogToGui(
                    $"DualShock 4 Bluetooth speaker submitted its first " +
                    $"credit-buffered SBC report (id=0x{report[0]:X2}, " +
                    $"mode=0x{report[2]:X2}, frames={count}, " +
                    $"bytes={report.Length}, slots=" +
                    $"{DualShock4AudioTransportSettings.CreditBufferedSlotCount}, " +
                    $"syntheticSilence={containsSyntheticSilence}).", false);
            }
            return CreditBufferedSubmissionResult.Submitted;
        }

        internal static void ApplyProductionReplayAudioMode(byte[] report,
            bool microphoneEnabled)
        {
            if (report == null || report.Length < 7)
            {
                throw new ArgumentException(
                    "A production-replay report must include a Bluetooth CRC.",
                    nameof(report));
            }

            report[2] = DualShock4AudioTransportSettings.
                GetProductionReplayAudioMode(microphoneEnabled);
            int crcOffset = report.Length - sizeof(uint);
            uint crc = DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                0xA2, report, crcOffset);
            report[crcOffset] = (byte)crc;
            report[crcOffset + 1] = (byte)(crc >> 8);
            report[crcOffset + 2] = (byte)(crc >> 16);
            report[crcOffset + 3] = (byte)(crc >> 24);
        }

        internal static void ApplyCreditBufferedAudioMode(byte[] report)
        {
            ApplySpeakerOnlyAudioMode(report, "credit-buffered",
                DualShock4AudioTransportSettings.
                    CreditBufferedSpeakerAudioMode);
        }

        internal static void ApplyFifoBufferedAudioMode(byte[] report,
            bool microphoneEnabled = false)
        {
            ApplySpeakerOnlyAudioMode(report, "fifo-buffered",
                DualShock4AudioTransportSettings.
                    GetFifoBufferedAudioMode(microphoneEnabled));
        }

        internal static void ApplyProductionA0AudioMode(byte[] report)
        {
            ApplySpeakerOnlyAudioMode(report, "production-a0",
                DualShock4AudioTransportSettings.
                    ProductionA0SpeakerAudioMode);
        }

        internal static void ApplyProductionDuplexAudioMode(byte[] report,
            bool microphoneEnabled)
        {
            ApplySpeakerOnlyAudioMode(report, "production-duplex-a1",
                DualShock4AudioTransportSettings.
                    GetProductionDuplexAudioMode(microphoneEnabled));
        }

        private static void ApplySpeakerOnlyAudioMode(byte[] report,
            string transportLabel, byte audioMode)
        {
            if (report == null || report.Length < 7)
            {
                throw new ArgumentException(
                    $"A {transportLabel} report must include a Bluetooth CRC.",
                    nameof(report));
            }

            report[2] = audioMode;
            int crcOffset = report.Length - sizeof(uint);
            uint crc = DualShock4BluetoothAudioProtocol.ComputeBluetoothCrc(
                0xA2, report, crcOffset);
            report[crcOffset] = (byte)crc;
            report[crcOffset + 1] = (byte)(crc >> 8);
            report[crcOffset + 2] = (byte)(crc >> 16);
            report[crcOffset + 3] = (byte)(crc >> 24);
        }

        private void RecordReportSize(int count)
        {
            if (count == DualShock4BluetoothAudioProtocol.
                SpeakerRealtimeFramesPerReport)
            {
                Interlocked.Increment(ref directRealtimeReports);
            }
            else if (count ==
                DualShock4BluetoothAudioProtocol.SpeakerSmallFramesPerReport)
            {
                Interlocked.Increment(ref directSmallReports);
            }
            else if (count ==
                DualShock4BluetoothAudioProtocol.SpeakerLargeFramesPerReport)
            {
                Interlocked.Increment(ref directLargeReports);
            }
        }

        private void RecordDirectReportTimestamp()
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref lastDirectReportTimestamp,
                now);
            if (previous == 0)
            {
                return;
            }

            long gap = now - previous;
            RecordMaximum(ref maximumDirectReportGapTicks, gap);
            RecordMinimum(ref minimumDirectReportGapTicks, gap);
        }

        private void TraceDirectStreamStatus(bool force = false)
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref lastDirectTraceTimestamp);
            // These detailed strings exist for diagnostics and allocate enough
            // to perturb a realtime presenter when formatted inline. Keep all
            // formatting and logging on a ThreadPool worker.
            long traceInterval = Stopwatch.Frequency * 10;
            if (!force && previous != 0 && now - previous < traceInterval)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref lastDirectTraceTimestamp,
                    now, previous) != previous)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref directTracePending, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(static state =>
            {
                var owner = (DualShock4BluetoothSpeakerPassthrough)state;
                try
                {
                    owner.WriteDirectStreamStatus();
                }
                finally
                {
                    Volatile.Write(ref owner.directTracePending, 0);
                }
            }, this);
        }

        private void WriteDirectStreamStatus()
        {
            int pcmQueueDepth;
            int pcmQueueFrames;
            int sbcQueueDepth;
            int minimumSbcQueueDepth;
            int maximumSbcQueueDepth;
            double currentDriftRatio;
            double targetDriftRatio;
            double asrcBaseRatio;
            lock (syncRoot)
            {
                pcmQueueDepth = directPcmPackets.Count;
                pcmQueueFrames = GetDirectPcmFrameCountLocked();
                sbcQueueDepth = encodedFrames.Count;
                minimumSbcQueueDepth = directMinimumEncodedQueueDepth ==
                    int.MaxValue ? sbcQueueDepth :
                    directMinimumEncodedQueueDepth;
                maximumSbcQueueDepth = Math.Max(sbcQueueDepth,
                    directMaximumEncodedQueueDepth);
                directMinimumEncodedQueueDepth = sbcQueueDepth;
                directMaximumEncodedQueueDepth = sbcQueueDepth;
                currentDriftRatio = directCurrentDriftRatio;
                targetDriftRatio = directTargetDriftRatio;
                asrcBaseRatio = directAsrcBaseRatio;
            }
            double maximumPacketGapMilliseconds =
                Interlocked.Exchange(ref maximumDirectPacketGapTicks, 0) *
                1000.0 / Stopwatch.Frequency;
            double maximumReportGapMilliseconds =
                Interlocked.Exchange(ref maximumDirectReportGapTicks, 0) *
                1000.0 / Stopwatch.Frequency;
            long minimumReportGapTicks = Interlocked.Exchange(
                ref minimumDirectReportGapTicks, long.MaxValue);
            double minimumReportGapMilliseconds = minimumReportGapTicks ==
                long.MaxValue ? 0.0 : minimumReportGapTicks * 1000.0 /
                Stopwatch.Frequency;
            long lastEffectTick =
                device.LastBluetoothEffectReportDuringAudioTick;
            long effectAgeMilliseconds = lastEffectTick == 0 ? -1 :
                Math.Max(0, Environment.TickCount64 - lastEffectTick);
            long lastInputTick = device.LastBluetoothInputReportTick;
            long lastValidInputAgeMilliseconds = lastInputTick == 0 ? -1 :
                Math.Max(0, Environment.TickCount64 - lastInputTick);
            NativeOverlappedWritePool.Status writeStatus =
                speakerWritePool?.GetStatus() ?? default;
            long currentCadenceTicks = Interlocked.Read(
                ref directCurrentCadenceTicks);
            long targetCadenceTicks = Interlocked.Read(
                ref directTargetCadenceTicks);
            double currentCadenceMilliseconds = currentCadenceTicks <= 0 ?
                DirectReportCadenceMilliseconds : currentCadenceTicks *
                    1000.0 / Stopwatch.Frequency;
            double targetCadenceMilliseconds = targetCadenceTicks <= 0 ?
                DirectReportCadenceMilliseconds : targetCadenceTicks *
                    1000.0 / Stopwatch.Frequency;
            AppLogger.LogToGui(
                $"DS4 Bluetooth audio trace: pcmPackets=" +
                $"{Interlocked.Read(ref directPacketsReceived)}, " +
                $"pcmBytes={Interlocked.Read(ref directPcmBytesReceived)}, " +
                $"packetDrops={Interlocked.Read(ref directPacketsDropped)}, " +
                $"silentPackets={Interlocked.Read(ref directSilentPackets)}, " +
                $"maxSilentRun={Interlocked.Exchange(ref directMaximumSilentRun, 0)}, " +
                $"zeroFrames={Interlocked.Read(ref directExactZeroFrames)}, " +
                $"maxZeroRun={Interlocked.Exchange(ref directMaximumExactZeroFrameRun, 0)}, " +
                $"zeroRuns={Interlocked.Read(ref directExactZeroRunEvents)}, " +
                $"repeatedPackets={Interlocked.Read(ref directRepeatedPackets)}, " +
                $"pcmPeak={Interlocked.Exchange(ref directPeakSample, 0)}, " +
                $"encodedFrames={Interlocked.Read(ref directFramesEncoded)}, " +
                $"encodedDrops={Interlocked.Read(ref directFramesDroppedForLatency)}, " +
                $"reports={Volatile.Read(ref reportsSubmitted)}, " +
                $"syntheticReports={Interlocked.Read(ref syntheticSilenceReports)}, " +
                $"writeSaturation={Interlocked.Read(ref directWriteSaturations)}, " +
                $"writeFailures={Interlocked.Read(ref directHardWriteFailures)}, " +
                $"hidPending={writeStatus.Pending}, " +
                $"hidCompleted={writeStatus.Completed}, " +
                $"hidCompletionFailures={writeStatus.Failures}, " +
                $"hidShort={writeStatus.ShortTransfers}, " +
                $"hidLastError={writeStatus.LastError}, " +
                $"hidLastTransfer={writeStatus.LastTransferred}/" +
                $"{writeStatus.LastExpected}, " +
                 $"hidPendingAgeMs={writeStatus.OldestPendingMilliseconds:F2}, " +
                 $"hidCompletionMs={writeStatus.MaximumCompletionMilliseconds:F2}, " +
                 $"hidIntervalCompletionMs=" +
                 $"{writeStatus.MaximumIntervalCompletionMilliseconds:F2}, " +
                 $"hidCompletionBuckets=" +
                 $"{writeStatus.CompletionsUnder16Milliseconds}/" +
                 $"{writeStatus.Completions16To24Milliseconds}/" +
                 $"{writeStatus.Completions24To32Milliseconds}/" +
                 $"{writeStatus.CompletionsAtLeast32Milliseconds}, " +
                 $"hidSubmitPending=" +
                 $"{writeStatus.SubmissionsWithNoPendingWrites}/" +
                 $"{writeStatus.SubmissionsWithOnePendingWrite}/" +
                 $"{writeStatus.SubmissionsWithAtLeastTwoPendingWrites}, " +
                 $"hidPendingHighWater={writeStatus.MaximumPendingWrites}, " +
                 $"effectWrites={device.BluetoothEffectReportsDuringAudio}, " +
                $"effectDeferred={device.BluetoothEffectReportsDeferredDuringAudio}, " +
                $"effectAgeMs={effectAgeMilliseconds}, " +
                $"lastValidInputAgeMs={lastValidInputAgeMilliseconds}, " +
                $"pcmQueue={pcmQueueDepth}/{pcmQueueFrames}f, " +
                $"sbcQueue={sbcQueueDepth}, " +
                $"sbcQueueMinMax={minimumSbcQueueDepth}-" +
                $"{maximumSbcQueueDepth}, " +
                $"reports12/14/17=" +
                $"{Interlocked.Read(ref directRealtimeReports)}/" +
                $"{Interlocked.Read(ref directSmallReports)}/" +
                $"{Interlocked.Read(ref directLargeReports)}, " +
                $"maxPacketGapMs={maximumPacketGapMilliseconds:F2}, " +
                $"reportGapMs={minimumReportGapMilliseconds:F2}-" +
                $"{maximumReportGapMilliseconds:F2}, " +
                $"primes={Interlocked.Read(ref directCadencePrimes)}, " +
                $"productionPrimeReports=" +
                $"{Interlocked.Read(ref productionReplayPrimeReports)}, " +
                $"productionReprimes=" +
                $"{Interlocked.Read(ref productionReplayReprimes)}, " +
                $"productionSkippedTicks=" +
                $"{Interlocked.Read(ref productionReplaySkippedTicks)}, " +
                $"productionSourceServo=" +
                $"{(DualShock4AudioTransportSettings.UsesProductionReplayPolicy(
                        directTransportMode) ?
                    (directDriftCorrectionEnabled ? "on" : "paused") : "n/a")}" +
                $"@{DualShock4AudioTransportSettings.ProductionReplayQueueServoTargetFrames}, " +
                $"fifoPrimeReports=" +
                $"{Interlocked.Read(ref fifoBufferedPrimeReports)}, " +
                $"fifoReprimes=" +
                $"{Interlocked.Read(ref fifoBufferedReprimes)}, " +
                $"fifoSkippedTicks=" +
                $"{Interlocked.Read(ref fifoBufferedSkippedTicks)}, " +
                $"fifoSourceServo=" +
                $"{(directTransportMode == DualShock4AudioTransportMode.FifoBuffered ?
                    (directDriftCorrectionEnabled ? "on" : "paused") : "n/a")}" +
                $"@{DualShock4AudioTransportSettings.FifoBufferedQueueServoTargetFrames}, " +
                $"creditPrimeReports=" +
                $"{Interlocked.Read(ref creditBufferedPrimeReports)}, " +
                $"creditReprimes=" +
                $"{Interlocked.Read(ref creditBufferedReprimes)}, " +
                $"creditSkippedTicks=" +
                $"{Interlocked.Read(ref creditBufferedSkippedTicks)}, " +
                $"underruns={Interlocked.Read(ref directCadenceUnderruns)}, " +
                $"lateDeadlines={Interlocked.Read(ref directLateDeadlines)}, " +
                $"clockRatio={device.BluetoothControllerClockRawRatio:F7}/" +
                $"{device.BluetoothControllerClockRatio:F7}, " +
                $"clockLocked={device.BluetoothControllerClockLocked}, " +
                $"clockFits={device.BluetoothControllerClockAcceptedFits}/" +
                $"{device.BluetoothControllerClockRejectedFits}, " +
                $"cadenceMs={currentCadenceMilliseconds:F5}/" +
                $"{targetCadenceMilliseconds:F5}, " +
                $"transport={DualShock4AudioTransportSettings.Format(directTransportMode)}, " +
                $"driftMode={directDriftMode.ToString().ToLowerInvariant()}, " +
                $"asrcBase={asrcBaseRatio:F7}, " +
                $"driftRatio={currentDriftRatio:F7}/" +
                $"{targetDriftRatio:F7}, " +
                $"driftFrames={Interlocked.Read(ref directDriftInputFrames)}/" +
                $"{Interlocked.Read(ref directDriftOutputFrames)}, " +
                $"sampleAdjust+/-={Interlocked.Read(ref directLagAccelerations)}/" +
                $"{Interlocked.Read(ref directLagDecelerations)}, " +
                $"GC={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/" +
                $"{GC.CollectionCount(2)}.", false);
        }

        private void CaptureDiagnosticPcm(byte[] pcm, int completeLength)
        {
            if (!EnableDiagnosticCapture)
            {
                return;
            }
            EnsureDiagnosticCapture();
            byte[] destination = diagnosticPcm;
            if (destination == null || completeLength <= 0)
            {
                return;
            }

            int offset = Volatile.Read(ref diagnosticPcmCount);
            int count = Math.Min(completeLength, destination.Length - offset);
            if (count <= 0)
            {
                TryWriteDiagnosticCapture();
                return;
            }

            Buffer.BlockCopy(pcm, 0, destination, offset, count);
            Volatile.Write(ref diagnosticPcmCount, offset + count);
            TryWriteDiagnosticCapture();
        }

        private void CaptureDiagnosticSbc(byte[] frame)
        {
            if (!EnableDiagnosticCapture)
            {
                return;
            }
            byte[] destination = diagnosticSbc;
            if (destination == null || frame == null)
            {
                return;
            }

            int offset = Volatile.Read(ref diagnosticSbcCount);
            int count = Math.Min(frame.Length, destination.Length - offset);
            if (count <= 0)
            {
                TryWriteDiagnosticCapture();
                return;
            }

            Buffer.BlockCopy(frame, 0, destination, offset, count);
            Volatile.Write(ref diagnosticSbcCount, offset + count);
            TryWriteDiagnosticCapture();
        }

        private void EnsureDiagnosticCapture()
        {
            if (diagnosticPcm != null ||
                Volatile.Read(ref diagnosticCaptureWritten) != 0)
            {
                return;
            }

            lock (syncRoot)
            {
                if (diagnosticPcm != null ||
                    Volatile.Read(ref diagnosticCaptureWritten) != 0)
                {
                    return;
                }

                diagnosticCaptureStartedUtc = DateTime.UtcNow;
                diagnosticPcm = new byte[DiagnosticPcmBytes];
                diagnosticSbc = new byte[DiagnosticSbcBytes];
            }
        }

        private void TryWriteDiagnosticCapture()
        {
            byte[] pcm = diagnosticPcm;
            byte[] sbc = diagnosticSbc;
            if (pcm == null || sbc == null ||
                Volatile.Read(ref diagnosticPcmCount) < pcm.Length ||
                Volatile.Read(ref diagnosticSbcCount) < sbc.Length ||
                Interlocked.CompareExchange(ref diagnosticCaptureWritten, 1, 0) != 0)
            {
                return;
            }

            DateTime startedUtc = diagnosticCaptureStartedUtc;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string logDirectory = Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.ApplicationData),
                        "DS4Windows", "Logs");
                    Directory.CreateDirectory(logDirectory);
                    string stem = Path.Combine(logDirectory,
                        $"ds4-bt-audio-{startedUtc:yyyyMMdd-HHmmss}");
                    File.WriteAllBytes(stem + ".pcm", pcm);
                    File.WriteAllBytes(stem + ".sbc", sbc);
                    File.WriteAllText(stem + ".txt",
                        "PCM: signed 16-bit little-endian, stereo, 32000 Hz\r\n" +
                        $"PCM bytes: {pcm.Length}\r\n" +
                        "SBC: concatenated 109-byte frames, 32000 Hz, joint stereo, " +
                        "16 blocks, 8 subbands, SNR allocation, bitpool 48\r\n" +
                        $"SBC frames: {sbc.Length / DualShock4BluetoothAudioProtocol.SpeakerSbcFrameLength}\r\n");
                    AppLogger.LogToGui(
                        $"DS4 Bluetooth audio diagnostic capture saved: {stem}.*",
                        false);
                }
                catch (Exception exception)
                {
                    AppLogger.LogToGui(
                        $"DS4 Bluetooth audio diagnostic capture failed: {exception.Message}",
                        true);
                }
            });
        }

        private static void RecordMaximum(ref long destination, long value)
        {
            long current = Interlocked.Read(ref destination);
            while (value > current)
            {
                long observed = Interlocked.CompareExchange(ref destination,
                    value, current);
                if (observed == current)
                {
                    return;
                }
                current = observed;
            }
        }

        private static void RecordMinimum(ref long destination, long value)
        {
            long current = Interlocked.Read(ref destination);
            while (value < current)
            {
                long observed = Interlocked.CompareExchange(ref destination,
                    value, current);
                if (observed == current)
                {
                    return;
                }
                current = observed;
            }
        }

        private byte GetBluetoothPollRate()
        {
            return (byte)Math.Clamp(device.getBTPollRate(), 0, 16);
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
                if (!device.RegisterDualShock4BluetoothAudioControlLane(this,
                        WriteBluetoothAudioControlBarrier))
                {
                    speakerWritePool.Dispose();
                    speakerWritePool = null;
                    speakerWriteHandle = null;
                    handle.Dispose();
                    return false;
                }
                return true;
            }
            catch
            {
                device.UnregisterDualShock4BluetoothAudioControlLane(this);
                speakerWritePool?.Dispose();
                speakerWritePool = null;
                speakerWriteHandle = null;
                handle.Dispose();
                throw;
            }
        }

        private bool WriteBluetoothAudioControlBarrier(byte[] report)
        {
            if (directTransportMode ==
                    DualShock4AudioTransportMode.PadForgeSpeakerOnly &&
                report != null && report.Length >= 7 && report[2] != 0)
            {
                ApplySpeakerOnlyAudioMode(report,
                    "padforge-speaker-only control", 0xA2);
            }
            return TrySendBluetoothAudioControl(report, out _);
        }

        private bool TrySendBluetoothAudioControl(byte[] report,
            out string error)
        {
            NativeOverlappedWritePool pool = speakerWritePool;
            if (pool == null)
            {
                error = "audio write pool unavailable";
                return false;
            }
            if (directTransportMode ==
                    DualShock4AudioTransportMode.ProductionA0 &&
                report != null && report.Length > 2 && report[2] != 0)
            {
                if (report[2] == DualShock4AudioTransportSettings.
                    ProductionDuplexMicrophoneAudioMode)
                {
                    // A speaker-only rollback lane must never acknowledge a
                    // microphone transition that it did not put on the wire.
                    // Returning failure keeps the published audio state equal
                    // to the controller's actual A0 mode.
                    error = "production-a0 is a speaker-only transport";
                    return false;
                }

                ApplyProductionA0AudioMode(report);
            }
            else if (directTransportMode ==
                    DualShock4AudioTransportMode.ProductionDuplexA1 &&
                report != null && report.Length > 2 && report[2] != 0)
            {
                // Preserve the caller's requested microphone state, but
                // normalize both sides of the ordered control barrier to the
                // production duplex contract: A0 while capture is closed and
                // A1 while capture is open.
                ApplyProductionDuplexAudioMode(report,
                    report[2] == DualShock4AudioTransportSettings.
                        ProductionDuplexMicrophoneAudioMode);
            }
            else if (directTransportMode ==
                    DualShock4AudioTransportMode.ProductionReplay &&
                report != null && report.Length > 2 && report[2] != 0)
            {
                ApplyProductionReplayAudioMode(report,
                    report[2] == DualShock4AudioTransportSettings.
                        ProductionReplayMicrophoneAudioMode);
            }
            else if (directTransportMode ==
                    DualShock4AudioTransportMode.FifoBuffered &&
                report != null && report.Length > 2 && report[2] != 0)
            {
                // Normalize the ordered control barrier to the same A0/A1
                // mode used by the prime and every steady speaker report.
                ApplyFifoBufferedAudioMode(report,
                    report[2] == DualShock4AudioTransportSettings.
                        FifoBufferedMicrophoneAudioMode);
            }
            else if (directTransportMode ==
                    DualShock4AudioTransportMode.CreditBuffered &&
                report != null && report.Length > 2 && report[2] != 0)
            {
                // The isolated packing experiment is speaker-only. Keep both
                // its 0x11 enable barrier and every 0x17 data packet in A2.
                ApplyCreditBufferedAudioMode(report);
            }
            return pool.TrySendControl(report, out error);
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
                    speakerVolume, report =>
                        TrySendBluetoothAudioControl(report,
                            out controlError)))
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
                    speakerVolume, report =>
                        TrySendBluetoothAudioControl(report, out _));
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

        private void WaitForNextTick(ref long nextTick, long cadenceTicks,
            IntPtr highResolutionTimer, int maximumRecoverablePeriods =
                DualShock4BluetoothAudioProtocol.SpeakerRealtimePrimeFrames,
            long catchUpRebaseLatenessTicks = long.MaxValue)
        {
            bool rebasedFromPreviousReport = false;
            if (catchUpRebaseLatenessTicks != long.MaxValue)
            {
                nextTick = DualShock4AudioReportScheduler.
                    SelectCurrentDeadline(nextTick,
                        Interlocked.Read(ref lastDirectReportTimestamp),
                        cadenceTicks, catchUpRebaseLatenessTicks,
                        out rebasedFromPreviousReport);
            }

            long now = Stopwatch.GetTimestamp();
            if (nextTick > now)
            {
                WaitUntil(highResolutionTimer, nextTick);
                now = Stopwatch.GetTimestamp();
            }

            if (catchUpRebaseLatenessTicks != long.MaxValue)
            {
                bool rebased;
                nextTick = DualShock4AudioReportScheduler.AdvanceDeadline(
                    nextTick, now, cadenceTicks,
                    catchUpRebaseLatenessTicks, out rebased);
                if (rebased || rebasedFromPreviousReport)
                {
                    Interlocked.Increment(ref directLateDeadlines);
                }
                return;
            }

            if (now > nextTick + cadenceTicks)
            {
                Interlocked.Increment(ref directLateDeadlines);
            }

            long maximumRecoverableLateness = cadenceTicks *
                Math.Max(1, maximumRecoverablePeriods);
            if (now - nextTick > maximumRecoverableLateness)
            {
                // A pause longer than the entire hardware cushion cannot be
                // concealed. Rebase instead of flooding stale audio forever.
                nextTick = now;
            }

            // Preserve the source timeline. When a short GC or scheduler pause
            // makes this deadline late, following iterations submit the overdue
            // reports until the selected lane's clock is caught up.
            nextTick += cadenceTicks;
        }

        private void WaitUntil(IntPtr highResolutionTimer, long timestamp)
        {
            while (!stopping)
            {
                long remainingTicks = timestamp - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    return;
                }

                double remainingMs = remainingTicks * 1000.0 /
                    Stopwatch.Frequency;
                if (remainingMs > 0.75 && highResolutionTimer != IntPtr.Zero)
                {
                    // Wake just ahead of the deadline, then stay resident for
                    // the final half millisecond. This avoids the 10-15 ms
                    // Thread.Sleep outliers observed in the physical trace
                    // without burning a full core for the entire report period.
                    WaitHighResolution(highResolutionTimer,
                        remainingMs - 0.5);
                }
                else if (remainingMs > 2.0)
                {
                    if (stoppingSignal.WaitOne((int)remainingMs - 1)) return;
                }
                else
                {
                    Thread.SpinWait(16);
                }
            }
        }

        private static IntPtr RegisterMultimediaScheduler()
        {
            try
            {
                uint taskIndex = 0;
                IntPtr handle = AvSetMmThreadCharacteristicsW("Pro Audio",
                    ref taskIndex);
                if (handle != IntPtr.Zero)
                {
                    AvSetMmThreadPriority(handle, AvrtPriority.Critical);
                }
                return handle;
            }
            catch (DllNotFoundException)
            {
                return IntPtr.Zero;
            }
            catch (EntryPointNotFoundException)
            {
                return IntPtr.Zero;
            }
        }

        private static IntPtr CreateHighResolutionTimer()
        {
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null,
                CreateWaitableTimerHighResolution, TimerAccess);
            if (timer == IntPtr.Zero)
            {
                timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0,
                    TimerAccess);
            }
            return timer;
        }

        private static void WaitHighResolution(IntPtr timer,
            double milliseconds)
        {
            if (milliseconds <= 0.0)
            {
                return;
            }

            if (timer != IntPtr.Zero)
            {
                long dueTime = -Math.Max(1L,
                    (long)(milliseconds * TimeSpan.TicksPerMillisecond));
                if (SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero,
                    IntPtr.Zero, false))
                {
                    WaitForNativeObject(timer, Infinite);
                    return;
                }
            }

            Thread.Sleep(Math.Max(1, (int)Math.Round(milliseconds)));
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
            device.UnregisterDualShock4BluetoothAudioControlLane(this);
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
                freeDirectPcmPackets.Clear();
                directPcmPacketOffset = 0;
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

        private readonly struct DirectPcmPacket
        {
            public DirectPcmPacket(byte[] buffer, int length)
            {
                Buffer = buffer;
                Length = length;
            }

            public byte[] Buffer { get; }

            public int Length { get; }
        }

        /// <summary>
        /// Dedicated audio session. The one-shot 0x11 control report and the
        /// realtime 0x12/0x17 stream use this same overlapped handle. Input
        /// remains
        /// exclusively owned by DS4Windows' primary HID session, as in the
        /// independently verified PadForge transport architecture.
        /// </summary>
        private sealed class NativeOverlappedWritePool : IDisposable
        {
            private const int SlotCount = 32;
            private const int OverlappedSize = 32;
            // DS4AudioStreamer uses a 640-byte pinned buffer even for the
            // variable-length 78/270/462-byte reports. Genuine CUH-ZCT2 HIDCLASS
            // completes these writes as 547 bytes, so a 462-byte backing array
            // lets the native stack read beyond the pinned object.
            private const int NativeBackingBufferLength = 640;
            private const uint WaitObject0 = 0;
            private const uint WaitTimeout = 258;
            private const int ErrorIoPending = 997;
            private readonly object gate = new object();
            private readonly IntPtr handle;
            private readonly byte[][] buffers = new byte[SlotCount][];
            private readonly GCHandle[] pins = new GCHandle[SlotCount];
            private readonly IntPtr[] events = new IntPtr[SlotCount];
            private readonly IntPtr[] overlapped = new IntPtr[SlotCount];
            private readonly bool[] outstanding = new bool[SlotCount];
            private readonly int[] expectedLengths = new int[SlotCount];
            private readonly long[] submittedTimestamps = new long[SlotCount];
            private int next;
            private volatile bool disposed;
            private long completedWrites;
            private long completionFailures;
            private long shortTransfers;
            private long maximumCompletionTicks;
            private long maximumIntervalCompletionTicks;
            private long completionsUnder16Milliseconds;
            private long completions16To24Milliseconds;
            private long completions24To32Milliseconds;
            private long completionsAtLeast32Milliseconds;
            private long submissionsWithNoPendingWrites;
            private long submissionsWithOnePendingWrite;
            private long submissionsWithAtLeastTwoPendingWrites;
            private int maximumPendingWrites;
            private int lastCompletionError;
            private int lastTransferred;
            private int lastExpected;

            public readonly struct Status
            {
                public Status(int pending, long completed, long failures,
                    long shortTransfers, int lastError,
                    int lastTransferred, int lastExpected,
                    double oldestPendingMilliseconds,
                    double maximumCompletionMilliseconds,
                    double maximumIntervalCompletionMilliseconds,
                    long completionsUnder16Milliseconds,
                    long completions16To24Milliseconds,
                    long completions24To32Milliseconds,
                    long completionsAtLeast32Milliseconds,
                    long submissionsWithNoPendingWrites,
                    long submissionsWithOnePendingWrite,
                    long submissionsWithAtLeastTwoPendingWrites,
                    int maximumPendingWrites)
                {
                    Pending = pending;
                    Completed = completed;
                    Failures = failures;
                    ShortTransfers = shortTransfers;
                    LastError = lastError;
                    LastTransferred = lastTransferred;
                    LastExpected = lastExpected;
                    OldestPendingMilliseconds = oldestPendingMilliseconds;
                    MaximumCompletionMilliseconds =
                        maximumCompletionMilliseconds;
                    MaximumIntervalCompletionMilliseconds =
                        maximumIntervalCompletionMilliseconds;
                    CompletionsUnder16Milliseconds =
                        completionsUnder16Milliseconds;
                    Completions16To24Milliseconds =
                        completions16To24Milliseconds;
                    Completions24To32Milliseconds =
                        completions24To32Milliseconds;
                    CompletionsAtLeast32Milliseconds =
                        completionsAtLeast32Milliseconds;
                    SubmissionsWithNoPendingWrites =
                        submissionsWithNoPendingWrites;
                    SubmissionsWithOnePendingWrite =
                        submissionsWithOnePendingWrite;
                    SubmissionsWithAtLeastTwoPendingWrites =
                        submissionsWithAtLeastTwoPendingWrites;
                    MaximumPendingWrites = maximumPendingWrites;
                }

                public int Pending { get; }
                public long Completed { get; }
                public long Failures { get; }
                public long ShortTransfers { get; }
                public int LastError { get; }
                public int LastTransferred { get; }
                public int LastExpected { get; }
                public double OldestPendingMilliseconds { get; }
                public double MaximumCompletionMilliseconds { get; }
                public double MaximumIntervalCompletionMilliseconds { get; }
                public long CompletionsUnder16Milliseconds { get; }
                public long Completions16To24Milliseconds { get; }
                public long Completions24To32Milliseconds { get; }
                public long CompletionsAtLeast32Milliseconds { get; }
                public long SubmissionsWithNoPendingWrites { get; }
                public long SubmissionsWithOnePendingWrite { get; }
                public long SubmissionsWithAtLeastTwoPendingWrites { get; }
                public int MaximumPendingWrites { get; }
            }

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
                    buffers[slot] = new byte[Math.Max(reportSize,
                        NativeBackingBufferLength)];
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

                lock (gate)
                {
                    if (disposed)
                    {
                        error = "audio transport disposed";
                        return false;
                    }
                    if (!DrainOutstandingNoLock(1000, out error))
                    {
                        return false;
                    }

                    byte[] nativeReport = new byte[Math.Max(report.Length,
                        NativeBackingBufferLength)];
                    Buffer.BlockCopy(report, 0, nativeReport, 0,
                        report.Length);
                    GCHandle pin = GCHandle.Alloc(nativeReport,
                        GCHandleType.Pinned);
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
                            // synchronous overlapped WriteFile need not report
                            // the transfer count through a second result query.
                            return true;
                        }
                        if (submitError != ErrorIoPending)
                        {
                            error = $"WriteFile failed: Win32 {submitError}";
                            return false;
                        }

                        uint wait = WaitForSingleObject(completionEvent, 1000);
                        // PadForge's WriteOneShot treats a signaled OVERLAPPED
                        // event as completion. HIDCLASS commonly reports zero
                        // via GetOverlappedResult even though the output report
                        // was accepted, so requiring a byte count creates false
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
            }

            public bool TryDrainOutstanding(int timeoutMilliseconds,
                out string error)
            {
                if (timeoutMilliseconds <= 0)
                {
                    error = "invalid speaker write drain timeout";
                    return false;
                }

                lock (gate)
                {
                    if (disposed)
                    {
                        error = "audio transport disposed";
                        return false;
                    }
                    return DrainOutstandingNoLock(timeoutMilliseconds,
                        out error);
                }
            }

            private bool DrainOutstandingNoLock(int timeoutMilliseconds,
                out string error)
            {
                error = "none";
                long deadline = Stopwatch.GetTimestamp() +
                    Stopwatch.Frequency * Math.Max(1, timeoutMilliseconds) /
                    1000;
                while (true)
                {
                    long failuresBefore = completionFailures;
                    ReapCompletedNoLock();
                    if (completionFailures != failuresBefore)
                    {
                        error = $"speaker write completion failed: Win32 " +
                            $"{lastCompletionError}";
                        return false;
                    }

                    int pendingSlot = -1;
                    for (int slot = 0; slot < SlotCount; slot++)
                    {
                        if (outstanding[slot])
                        {
                            pendingSlot = slot;
                            break;
                        }
                    }
                    if (pendingSlot < 0)
                    {
                        return true;
                    }

                    long remainingTicks = deadline - Stopwatch.GetTimestamp();
                    if (remainingTicks <= 0)
                    {
                        error = "speaker write drain timed out";
                        return false;
                    }
                    uint remainingMilliseconds = (uint)Math.Clamp(
                        (int)Math.Ceiling(remainingTicks * 1000.0 /
                            Stopwatch.Frequency), 1, timeoutMilliseconds);
                    uint wait = WaitForSingleObject(events[pendingSlot],
                        remainingMilliseconds);
                    if (wait == WaitTimeout)
                    {
                        error = "speaker write drain timed out";
                        return false;
                    }
                    if (wait != WaitObject0)
                    {
                        error = $"speaker write drain wait failed: Win32 " +
                            $"{Marshal.GetLastWin32Error()}";
                        return false;
                    }
                }
            }

            /// <summary>
            /// Atomically checks bounded async capacity, then invokes the
            /// caller's report builder before copying and submitting the
            /// report. The builder is never called while all PadForge slots
            /// are occupied, so source frames remain queued on backpressure.
            /// </summary>
            public bool TrySendPrepared(byte[] report,
                int maximumOutstanding, Func<bool> prepare,
                out bool hardFailure, out bool saturated)
            {
                hardFailure = false;
                saturated = false;
                if (report == null || prepare == null ||
                    maximumOutstanding <= 0 ||
                    maximumOutstanding > SlotCount)
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

                    long failuresBefore = completionFailures;
                    ReapCompletedNoLock();
                    if (completionFailures != failuresBefore)
                    {
                        hardFailure = true;
                        return false;
                    }

                    bool padForgeOrderedRing = maximumOutstanding ==
                        DualShock4AudioTransportSettings.
                            PadForgeAsyncSlotCount;
                    int slotLimit = padForgeOrderedRing ?
                        DualShock4AudioTransportSettings.
                            PadForgeAsyncSlotCount : SlotCount;
                    int pending = 0;
                    int slot = -1;
                    for (int offset = 0; offset < slotLimit; offset++)
                    {
                        int candidate = padForgeOrderedRing ? offset :
                            (next + offset) % SlotCount;
                        if (outstanding[candidate])
                        {
                            pending++;
                        }
                        else if (slot < 0)
                        {
                            slot = candidate;
                        }
                    }
                    if (padForgeOrderedRing)
                    {
                        // PadForge's pool probes exactly the oldest ring slot.
                        // Do not skip over that still-pending write and spend a
                        // newer Bluetooth ACL credit out of presentation order.
                        int oldest = next % slotLimit;
                        slot = outstanding[oldest] ? -1 : oldest;
                    }
                    bool boundedCapacity;
                    if (maximumOutstanding ==
                        DualShock4AudioTransportSettings.
                            PadForgeAsyncSlotCount)
                    {
                        boundedCapacity = DualShock4AudioTransportSettings.
                            CanSubmitPadForgeAsync(pending);
                    }
                    else if (maximumOutstanding ==
                        DualShock4AudioTransportSettings.
                            ProductionReplaySlotCount)
                    {
                        boundedCapacity = DualShock4AudioTransportSettings.
                            CanSubmitProductionReplay(pending);
                    }
                    else if (maximumOutstanding ==
                        DualShock4AudioTransportSettings.
                            CreditBufferedSlotCount)
                    {
                        boundedCapacity = DualShock4AudioTransportSettings.
                            CanSubmitCreditBuffered(pending);
                    }
                    else if (maximumOutstanding ==
                        DualShock4AudioTransportSettings.
                            FifoBufferedPrimeSlotCount)
                    {
                        boundedCapacity = DualShock4AudioTransportSettings.
                            CanSubmitFifoBufferedPrime(pending);
                    }
                    else
                    {
                        boundedCapacity = pending < maximumOutstanding;
                    }
                    if (!boundedCapacity || slot < 0)
                    {
                        saturated = true;
                        return false;
                    }

                    if (pending == 0)
                    {
                        submissionsWithNoPendingWrites++;
                    }
                    else if (pending == 1)
                    {
                        submissionsWithOnePendingWrite++;
                    }
                    else
                    {
                        submissionsWithAtLeastTwoPendingWrites++;
                    }
                    maximumPendingWrites = Math.Max(maximumPendingWrites,
                        pending);

                    if (!prepare())
                    {
                        return false;
                    }

                    int length = Math.Min(report.Length,
                        buffers[slot].Length);
                    Array.Clear(buffers[slot], 0, buffers[slot].Length);
                    Buffer.BlockCopy(report, 0, buffers[slot], 0, length);
                    ResetEvent(events[slot]);
                    ZeroOverlapped(overlapped[slot], events[slot]);
                    bool submitted = WriteFile(handle,
                        pins[slot].AddrOfPinnedObject(), (uint)length,
                        IntPtr.Zero, overlapped[slot]);
                    int submitError = submitted ? 0 :
                        Marshal.GetLastWin32Error();
                    if (!submitted && submitError != ErrorIoPending)
                    {
                        SetEvent(events[slot]);
                        lastCompletionError = submitError;
                        hardFailure = true;
                        return false;
                    }

                    outstanding[slot] = true;
                    expectedLengths[slot] = length;
                    submittedTimestamps[slot] = Stopwatch.GetTimestamp();
                    next = (slot + 1) % slotLimit;
                    return true;
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

                    long failuresBefore = completionFailures;
                    ReapCompletedNoLock();
                    if (completionFailures != failuresBefore)
                    {
                        hardFailure = true;
                        return false;
                    }

                    int slot = -1;
                    for (int offset = 0; offset < SlotCount; offset++)
                    {
                        int candidate = (next + offset) % SlotCount;
                        if (!outstanding[candidate])
                        {
                            slot = candidate;
                            break;
                        }
                    }
                    if (slot < 0)
                    {
                        return false;
                    }

                    int length = Math.Min(report.Length, buffers[slot].Length);
                    Array.Clear(buffers[slot], 0, buffers[slot].Length);
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

                    outstanding[slot] = true;
                    expectedLengths[slot] = length;
                    submittedTimestamps[slot] = Stopwatch.GetTimestamp();
                    next = (slot + 1) % SlotCount;
                    return true;
                }
            }

            /// <summary>
            /// DS4AudioStreamer-compatible in-order write. The caller does
            /// not present the next SBC report until the HID stack completes
            /// this one. This is completion-paced, not an HCI acknowledgement
            /// from the physical controller.
            /// </summary>
            public bool SendAndWait(byte[] report, out bool hardFailure)
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

                    long failuresBefore = completionFailures;
                    ReapCompletedNoLock();
                    if (completionFailures != failuresBefore)
                    {
                        hardFailure = true;
                        return false;
                    }

                    int slot = -1;
                    for (int offset = 0; offset < SlotCount; offset++)
                    {
                        int candidate = (next + offset) % SlotCount;
                        if (!outstanding[candidate])
                        {
                            slot = candidate;
                            break;
                        }
                    }
                    if (slot < 0)
                    {
                        return false;
                    }

                    int length = Math.Min(report.Length,
                        buffers[slot].Length);
                    Array.Clear(buffers[slot], 0, buffers[slot].Length);
                    Buffer.BlockCopy(report, 0, buffers[slot], 0, length);
                    ResetEvent(events[slot]);
                    ZeroOverlapped(overlapped[slot], events[slot]);
                    bool submitted = WriteFile(handle,
                        pins[slot].AddrOfPinnedObject(), (uint)length,
                        IntPtr.Zero, overlapped[slot]);
                    int submitError = submitted ? 0 :
                        Marshal.GetLastWin32Error();
                    if (!submitted && submitError != ErrorIoPending)
                    {
                        SetEvent(events[slot]);
                        lastCompletionError = submitError;
                        hardFailure = true;
                        return false;
                    }

                    outstanding[slot] = true;
                    expectedLengths[slot] = length;
                    submittedTimestamps[slot] = Stopwatch.GetTimestamp();
                    next = (slot + 1) % SlotCount;

                    uint wait = WaitForSingleObject(events[slot], 1000);
                    if (wait != WaitObject0)
                    {
                        lastCompletionError = wait == WaitTimeout ? 1460 :
                            Marshal.GetLastWin32Error();
                        // Keep the pinned slot valid until the kernel releases
                        // its OVERLAPPED. On a bounded timeout, explicitly
                        // cancel and reap when possible; if the driver still
                        // does not signal, Dispose retains the existing bounded
                        // leak protection rather than freeing live memory.
                        CancelIoEx(handle, overlapped[slot]);
                        if (WaitForSingleObject(events[slot], 250) ==
                            WaitObject0)
                        {
                            ReapCompletedNoLock();
                        }
                        hardFailure = true;
                        return false;
                    }

                    failuresBefore = completionFailures;
                    ReapCompletedNoLock();
                    if (completionFailures != failuresBefore ||
                        outstanding[slot])
                    {
                        hardFailure = true;
                        return false;
                    }
                    return true;
                }
            }

            public Status GetStatus()
            {
                lock (gate)
                {
                    if (disposed)
                    {
                        return new Status(0, completedWrites,
                            completionFailures, shortTransfers,
                            lastCompletionError, lastTransferred,
                            lastExpected, 0.0,
                            maximumCompletionTicks * 1000.0 /
                            Stopwatch.Frequency, 0.0,
                            completionsUnder16Milliseconds,
                            completions16To24Milliseconds,
                            completions24To32Milliseconds,
                            completionsAtLeast32Milliseconds,
                            submissionsWithNoPendingWrites,
                            submissionsWithOnePendingWrite,
                            submissionsWithAtLeastTwoPendingWrites,
                            maximumPendingWrites);
                    }

                    ReapCompletedNoLock();
                    int pending = 0;
                    long now = Stopwatch.GetTimestamp();
                    long oldestPendingTicks = 0;
                    for (int slot = 0; slot < SlotCount; slot++)
                    {
                        if (!outstanding[slot])
                        {
                            continue;
                        }

                        pending++;
                        long age = now - submittedTimestamps[slot];
                        if (age > oldestPendingTicks)
                        {
                            oldestPendingTicks = age;
                        }
                    }

                    long intervalMaximumTicks =
                        maximumIntervalCompletionTicks;
                    maximumIntervalCompletionTicks = 0;
                    int intervalMaximumPending = maximumPendingWrites;
                    maximumPendingWrites = pending;
                    return new Status(pending, completedWrites,
                        completionFailures, shortTransfers,
                        lastCompletionError, lastTransferred, lastExpected,
                        oldestPendingTicks * 1000.0 / Stopwatch.Frequency,
                        maximumCompletionTicks * 1000.0 /
                        Stopwatch.Frequency,
                        intervalMaximumTicks * 1000.0 /
                        Stopwatch.Frequency,
                        completionsUnder16Milliseconds,
                        completions16To24Milliseconds,
                        completions24To32Milliseconds,
                        completionsAtLeast32Milliseconds,
                        submissionsWithNoPendingWrites,
                        submissionsWithOnePendingWrite,
                        submissionsWithAtLeastTwoPendingWrites,
                        intervalMaximumPending);
                }
            }

            private void ReapCompletedNoLock()
            {
                for (int slot = 0; slot < SlotCount; slot++)
                {
                    if (!outstanding[slot] ||
                        WaitForSingleObject(events[slot], 0) != WaitObject0)
                    {
                        continue;
                    }

                    bool completed = GetOverlappedResult(handle,
                        overlapped[slot], out uint transferred, false);
                    long completionTicks = Stopwatch.GetTimestamp() -
                        submittedTimestamps[slot];
                    if (completionTicks > maximumCompletionTicks)
                    {
                        maximumCompletionTicks = completionTicks;
                    }
                    if (completionTicks > maximumIntervalCompletionTicks)
                    {
                        maximumIntervalCompletionTicks = completionTicks;
                    }
                    double completionMilliseconds = completionTicks * 1000.0 /
                        Stopwatch.Frequency;
                    if (completionMilliseconds < 16.0)
                    {
                        completionsUnder16Milliseconds++;
                    }
                    else if (completionMilliseconds < 24.0)
                    {
                        completions16To24Milliseconds++;
                    }
                    else if (completionMilliseconds < 32.0)
                    {
                        completions24To32Milliseconds++;
                    }
                    else
                    {
                        completionsAtLeast32Milliseconds++;
                    }

                    outstanding[slot] = false;
                    if (!completed)
                    {
                        completionFailures++;
                        lastCompletionError = Marshal.GetLastWin32Error();
                        continue;
                    }

                    completedWrites++;
                    lastTransferred = (int)transferred;
                    lastExpected = expectedLengths[slot];
                    // HIDCLASS legitimately reports zero bytes for some output
                    // report completions. Preserve that observation as telemetry
                    // instead of retrying and creating an audio duplicate.
                    if (transferred != 0 &&
                        transferred < expectedLengths[slot])
                    {
                        shortTransfers++;
                    }
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

                        outstanding[slot] = false;

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

        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAccess = 0x00000002 | 0x00100000;
        private const uint Infinite = 0xFFFFFFFF;

        private enum AvrtPriority
        {
            Normal = 0,
            High = 1,
            Critical = 2,
        }

        [DllImport("avrt.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(
            string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle,
            AvrtPriority priority);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvRevertMmThreadCharacteristics(
            IntPtr avrtHandle);

        [DllImport("kernel32.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(
            IntPtr attributes, string name, uint flags, uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(IntPtr timer,
            ref long dueTime, int period, IntPtr completionRoutine,
            IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

        [DllImport("kernel32.dll", SetLastError = true,
            EntryPoint = "WaitForSingleObject")]
        private static extern uint WaitForNativeObject(IntPtr handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true,
            EntryPoint = "CloseHandle")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseNativeHandle(IntPtr handle);
    }
}
