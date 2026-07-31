using Concentus;
using Concentus.Enums;
using DS4Windows.InputDevices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Opt-in diagnostic WAV sink used to split virtual-USB discontinuities
    /// from the Bluetooth presentation path. It is never constructed unless
    /// DS4WINDOWS_DUALSENSE_PCM_TRACE_DIRECTORY is set for the process.
    /// </summary>
    internal sealed class Pcm16WaveTraceWriter : IDisposable
    {
        private const int HeaderLength = 44;
        private const int QueueCapacity = 64;
        private const int QueueSlotBytes = 64 * 1024;
        private readonly FileStream stream;
        private readonly int sampleRate;
        private readonly short channels;
        private readonly object queueLock = new object();
        private readonly byte[][] queueSlots = new byte[QueueCapacity][];
        private readonly int[] queueLengths = new int[QueueCapacity];
        private readonly AutoResetEvent dataAvailable = new AutoResetEvent(false);
        private readonly Thread writerThread;
        private int queueReadIndex;
        private int queueWriteIndex;
        private int queuedSlots;
        private long dataBytes;
        private volatile bool stopping;
        private bool disposed;

        private Pcm16WaveTraceWriter(string path, int sampleRate,
            short channels)
        {
            this.sampleRate = sampleRate;
            this.channels = channels;
            stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            for (int index = 0; index < queueSlots.Length; index++)
            {
                queueSlots[index] = new byte[QueueSlotBytes];
            }

            // A streaming-size header keeps an abruptly terminated diagnostic
            // trace recoverable. Dispose replaces it with exact RIFF lengths.
            WriteWaveHeader(uint.MaxValue, uint.MaxValue);
            stream.Flush();
            writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "DualSense PCM trace writer",
                Priority = ThreadPriority.BelowNormal,
            };
            writerThread.Start();
        }

        internal static Pcm16WaveTraceWriter TryCreate(string path,
            int sampleRate, short channels)
        {
            try
            {
                return new Pcm16WaveTraceWriter(path, sampleRate, channels);
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense PCM trace could not open '{path}': {ex.Message}",
                    true);
                return null;
            }
        }

        internal void Write(byte[] source, int offset, int length)
        {
            if (disposed || stopping || source == null || length <= 0)
            {
                return;
            }

            int boundedLength = Math.Min(length, source.Length - offset);
            if (offset < 0 || boundedLength <= 0)
            {
                return;
            }

            int copied = 0;
            lock (queueLock)
            {
                while (copied < boundedLength && queuedSlots < QueueCapacity)
                {
                    int slotLength = Math.Min(QueueSlotBytes,
                        boundedLength - copied);
                    Buffer.BlockCopy(source, offset + copied,
                        queueSlots[queueWriteIndex], 0, slotLength);
                    queueLengths[queueWriteIndex] = slotLength;
                    queueWriteIndex = (queueWriteIndex + 1) % QueueCapacity;
                    queuedSlots++;
                    copied += slotLength;
                }
            }

            if (copied > 0)
            {
                dataAvailable.Set();
            }
        }

        internal void Write(float[] source, int sampleCount)
        {
            if (disposed || stopping || source == null || sampleCount <= 0)
            {
                return;
            }

            int boundedSamples = Math.Min(sampleCount, source.Length);
            int maximumSamplesPerSlot = QueueSlotBytes / sizeof(short);
            int converted = 0;
            lock (queueLock)
            {
                while (converted < boundedSamples &&
                    queuedSlots < QueueCapacity)
                {
                    int slotSamples = Math.Min(maximumSamplesPerSlot,
                        boundedSamples - converted);
                    byte[] destination = queueSlots[queueWriteIndex];
                    for (int index = 0; index < slotSamples; index++)
                    {
                        short sample = (short)Math.Clamp((int)Math.Round(
                            source[converted + index] * short.MaxValue),
                            short.MinValue, short.MaxValue);
                        BinaryPrimitives.WriteInt16LittleEndian(
                            destination.AsSpan(index * sizeof(short),
                                sizeof(short)), sample);
                    }

                    queueLengths[queueWriteIndex] = slotSamples *
                        sizeof(short);
                    queueWriteIndex = (queueWriteIndex + 1) % QueueCapacity;
                    queuedSlots++;
                    converted += slotSamples;
                }
            }

            if (converted > 0)
            {
                dataAvailable.Set();
            }
        }

        private void WriterLoop()
        {
            try
            {
                while (true)
                {
                    int slotIndex;
                    int length;
                    lock (queueLock)
                    {
                        if (queuedSlots == 0)
                        {
                            if (stopping)
                            {
                                break;
                            }

                            slotIndex = -1;
                            length = 0;
                        }
                        else
                        {
                            slotIndex = queueReadIndex;
                            length = queueLengths[slotIndex];
                        }
                    }

                    if (slotIndex < 0)
                    {
                        dataAvailable.WaitOne();
                        continue;
                    }

                    stream.Write(queueSlots[slotIndex], 0, length);
                    dataBytes += length;
                    lock (queueLock)
                    {
                        queueLengths[slotIndex] = 0;
                        queueReadIndex = (queueReadIndex + 1) % QueueCapacity;
                        queuedSlots--;
                    }
                }
            }
            catch
            {
                // Diagnostics must never terminate the controller transport.
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            stopping = true;
            dataAvailable.Set();
            if (writerThread != null && writerThread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId !=
                    writerThread.ManagedThreadId)
            {
                writerThread.Join(10000);
            }

            disposed = true;
            try
            {
                uint boundedDataBytes = (uint)Math.Min(dataBytes,
                    uint.MaxValue - 36L);
                WriteWaveHeader(boundedDataBytes + 36, boundedDataBytes);
                stream.SetLength(HeaderLength + boundedDataBytes);
                stream.Flush(true);
            }
            catch
            {
            }
            finally
            {
                stream.Dispose();
                dataAvailable.Dispose();
            }
        }

        private void WriteWaveHeader(uint riffLength, uint dataLength)
        {
            Span<byte> header = stackalloc byte[HeaderLength];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..8],
                riffLength);
            "WAVEfmt "u8.CopyTo(header[8..16]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header[20..22], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[22..24],
                (ushort)channels);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..28],
                (uint)sampleRate);
            uint byteRate = (uint)(sampleRate * channels * sizeof(short));
            BinaryPrimitives.WriteUInt32LittleEndian(header[28..32],
                byteRate);
            BinaryPrimitives.WriteUInt16LittleEndian(header[32..34],
                (ushort)(channels * sizeof(short)));
            BinaryPrimitives.WriteUInt16LittleEndian(header[34..36], 16);
            "data"u8.CopyTo(header[36..40]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..44],
                dataLength);
            stream.Position = 0;
            stream.Write(header);
            stream.Position = HeaderLength + dataBytes;
        }
    }

    /// <summary>
    /// Stateful sample-rate converter for VIIPER's interleaved stereo PCM16
    /// stream. The fractional source position and final input frame survive
    /// transport-frame boundaries, so a 32 kHz virtual DS4 can feed the 48 kHz
    /// DualSense speaker clock without duplicating or dropping a boundary
    /// sample.
    /// </summary>
    internal sealed class DualSenseDirectPcmRateConverter
    {
        private const double IntegerTolerance = 1.0e-10;
        private readonly double outputFramesPerInputFrame;
        private double sourcePosition;
        private bool hasCarry;
        private short carryLeft;
        private short carryRight;

        internal DualSenseDirectPcmRateConverter(int sourceSampleRate,
            int outputSampleRate)
        {
            if (sourceSampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
            }
            if (outputSampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
            }

            outputFramesPerInputFrame = outputSampleRate /
                (double)sourceSampleRate;
        }

        internal int Convert(byte[] source, int offset, int length,
            float[] destination)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }
            if (offset < 0 || length < 0 || offset + length > source.Length ||
                length % (sizeof(short) * 2) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            int inputFrames = length / (sizeof(short) * 2);
            if (inputFrames == 0)
            {
                return 0;
            }

            double sourceStep = 1.0 / outputFramesPerInputFrame;
            int outputFrames = 0;
            while (true)
            {
                double roundedPosition = Math.Round(sourcePosition);
                if (Math.Abs(sourcePosition - roundedPosition) <=
                    IntegerTolerance)
                {
                    sourcePosition = roundedPosition;
                }

                int lowerFrame = (int)Math.Floor(sourcePosition);
                double fraction = sourcePosition - lowerFrame;
                int upperFrame = fraction <= IntegerTolerance ? lowerFrame :
                    lowerFrame + 1;
                if (lowerFrame < -1 || upperFrame >= inputFrames ||
                    (lowerFrame < 0 && !hasCarry))
                {
                    break;
                }
                if (outputFrames > destination.Length / 2 - 1)
                {
                    throw new ArgumentException(
                        "The direct PCM destination buffer is too small.",
                        nameof(destination));
                }

                short left0 = ReadSample(source, offset, lowerFrame, 0,
                    carryLeft);
                short right0 = ReadSample(source, offset, lowerFrame, 1,
                    carryRight);
                short left1 = ReadSample(source, offset, upperFrame, 0,
                    carryLeft);
                short right1 = ReadSample(source, offset, upperFrame, 1,
                    carryRight);
                int destinationOffset = outputFrames * 2;
                destination[destinationOffset] = (float)(Interpolate(left0,
                    left1, fraction) / 32768.0);
                destination[destinationOffset + 1] = (float)(Interpolate(
                    right0, right1, fraction) / 32768.0);
                outputFrames++;
                sourcePosition += sourceStep;
            }

            sourcePosition -= inputFrames;
            int finalOffset = offset + (inputFrames - 1) * 4;
            carryLeft = ReadInt16(source, finalOffset);
            carryRight = ReadInt16(source, finalOffset + 2);
            hasCarry = true;
            return outputFrames;
        }

        internal void Reset()
        {
            sourcePosition = 0.0;
            hasCarry = false;
            carryLeft = 0;
            carryRight = 0;
        }

        private static short ReadSample(byte[] source, int offset, int frame,
            int channel, short carry)
        {
            return frame < 0 ? carry : ReadInt16(source,
                offset + frame * 4 + channel * 2);
        }

        private static short ReadInt16(byte[] source, int offset)
        {
            return (short)(source[offset] | source[offset + 1] << 8);
        }

        private static double Interpolate(short first, short second,
            double fraction)
        {
            return fraction <= IntegerTolerance || first == second ? first :
                first + (second - (double)first) * fraction;
        }
    }

    /// <summary>
    /// Mirrors a Windows render endpoint to a physical Bluetooth DualSense
    /// speaker. Every frame uses the vDS-style 0x36 combined transport so
    /// speaker audio, microphone state, haptics, and controller output never
    /// compete through separate Bluetooth report IDs.
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
        internal const int LowLatencyCaptureBufferMs = 5;
        // Ownership recovery is bounded by helper process-exit plus startup
        // waits. Retain source history for the entire bounded transition
        // instead of silently draining it.
        private const int CaptureBufferMs = 24000;
        // Start as soon as a few causal source packets exist. The isolated
        // writer already requires eight 10.667 ms reports; a 160 ms source
        // prebuffer duplicated that protection and made game audio feel late.
        internal const int InitialBufferMs = 20;
        internal const int TargetBufferMs = 20;
        // Legacy capture sources retain the existing stop-and-wait policy.
        // The native V5 source owns a bounded eight-generation FIFO and
        // drains every complete retained generation into its strict writer
        // after a host stall, so it uses the separate target below.
        internal const int PacerReservoirTargetFrames = 1;
        internal const int StartupWarmupReportCount = 8;
        internal const int V5SourceReservoirTargetFrames =
            StartupWarmupReportCount;
        internal const int V5InitialSourceBufferFrames =
            StartupWarmupReportCount *
                DualSenseV5SpeakerClockResampler.ReferenceInputFrames +
            DualSenseReferenceSpeakerFrameResampler.
                InterpolationLookaheadFrames;
        private const int CaptureRingFrames = (SampleRate * CaptureBufferMs) / 1000;
        private const int CapturePumpBufferFrames = 2048;
        private const int DirectPcmChunkBytes = 4096;
        private const int DirectPcmMaximumOutputFrames =
            (DirectPcmChunkBytes / (sizeof(short) * Channels) * 3 / 2) + 2;
        private const int IdleKeepAliveMs = 2000;
        // V5 waits 100 ms after the last produced PCM before replacing
        // a temporarily incomplete source block with timed silence.
        internal const int TransientCaptureShortageLeaseMs = 100;
        // Reuse the existing audible-generation boundary for V5 callbacks. A
        // scheduler pause just beyond the 100 ms shortage lease must preserve
        // the live resampler, while a producer returning after this boundary
        // starts a new source generation.
        internal const int V5HardSourceDiscontinuityMs =
            IdleKeepAliveMs;
        private const int PacerPrewarmRetryMs = 2000;
        private const double BluetoothSpeakerCadenceMs = 10.0 + (2.0 / 3.0);
        // The render and Bluetooth clocks are independent. Correct their
        // long-run drift without audibly pitch-shifting the stream: filter the
        // callback sawtooth, cap correction at 1000 ppm, and slew at two ppm
        // per packet. The previous +/-4-of-512 frame snap was a +/-7812 ppm
        // pitch step; the acoustic trace measured the resulting 523.41 Hz tone
        // at about 519.08 Hz before it jumped back to nominal.
        internal const double CaptureClockMaximumCorrection = 0.001;
        internal const double CaptureClockCorrectionGain = 1.0 / 65536.0;
        internal const double CaptureClockRatioSlewPerPacket = 0.000002;
        private const double CaptureClockSmoothingAlpha = 0.005319148936170213;
        internal const double CaptureClockErrorDeadbandFrames = 2.0;
        private const double DirectCaptureClockMaximumCorrection = 0.001;
        private const double DirectCaptureClockRatioSlewPerPacket = 0.000002;

        private readonly object syncRoot = new object();
        private readonly object directPcmSync = new object();
        private readonly DualSenseDevice device;
        private readonly bool headsetOnlyAudio;
        private readonly string sourceEndpointId;
        private readonly ControllerAudioEndpointKind sourceEndpointKind;
        private readonly byte speakerVolume;
        private readonly DualSenseSpeakerCompression speakerCompression;
        private readonly byte speakerBassBoost;
        private readonly DualSenseSpeakerProcessor speakerProcessor;
        private readonly ViiperOutDevice directSpeakerSource;
        private readonly int directSpeakerSampleRate;
        private readonly bool directSpeakerUsesV5Source;
        private readonly DualSensePcm16SourceRateConverter directPcmRateConverter;
        private readonly DualSenseSourceClockEstimator directSourceClockEstimator;
        private readonly DualSenseDirectPcmBalanceClockServo
            directPcmBalanceClockServo;
        private readonly DualSenseSpeakerFrameResampler speakerFrameResampler =
            new DualSenseSpeakerFrameResampler();
        private readonly DualSenseReferenceSpeakerFrameResampler
            directSpeakerFrameResampler;
        private readonly DualSenseV5SpeakerClockResampler
            directV5FrameResampler;
        private Pcm16WaveTraceWriter rawDirectPcmTrace;
        private Pcm16WaveTraceWriter preOpusPcmTrace;
        private Pcm16WaveTraceWriter postOpusPcmTrace;
        private IOpusDecoder traceOpusDecoder;
        private readonly float[] traceDecodedFrame = new float[
            FrameSamples * Channels];
        private readonly float[] directPcmFrame = new float[
            DirectPcmMaximumOutputFrames * Channels];
        private readonly byte[] atomicFeedback = new byte[
            ViiperOutDevice.DualSenseAtomicFeedbackLength];
        private readonly float[] frame = new float[FrameSamples * Channels];
        private readonly float[] speakerResampleInput = new float[
            DualSenseSpeakerFrameResampler.MaximumInputFrames * Channels];
        private readonly byte[] opusFrame = new byte[OpusBytes];
        private readonly float[] captureRing = new float[CaptureRingFrames * Channels];
        private readonly AutoResetEvent captureDataAvailable = new AutoResetEvent(false);
        private readonly AutoResetEvent captureFramesAvailable = new AutoResetEvent(false);
        private readonly AutoResetEvent pacerLifecycleRequested =
            new AutoResetEvent(false);

        private IWaveIn capture;
        private BufferedWaveProvider captureBuffer;
        private Thread worker;
        private Thread capturePump;
        private Thread pacerLifecycleWorker;
        private IOpusEncoder opusEncoder;
        private volatile bool stopping;
        private int captureRingReadIndex;
        private int captureRingWriteIndex;
        private int captureRingBufferedFrames;
        private bool capturePrimed;
        private double captureSmoothedBufferedFrames;
        private double captureCurrentClockRatio = 1.0;
        private double captureTargetClockRatio = 1.0;
        private bool captureClockInitialized;
        // A WDL output-driven prepare reserves its interpolation state. Cache
        // that exact request until the source ring can satisfy it; otherwise
        // the readiness check can wake on 512 frames while this packet really
        // needs 513 and unnecessarily defer an entire controller interval.
        private int preparedDirectSpeakerSourceFrames;
        private bool fadeInAfterCaptureUnderrun;
        private float previousOutputLeft;
        private float previousOutputRight;
        private int loggedWriteFailure;
        private int loggedDirectPcmFailure;
        private int loggedPacerPrewarmFailure;
        private int loggedPacerLifecycleFailure;
        private int disposeStarted;
        private bool isGameAudioEndpoint;
        private volatile bool audioSegmentActive;
        private readonly long speakerSessionId;
        private long speakerGeneration;
        private int startupWarmupFramesRemaining;
        private bool pendingEncodedFrame;
        private bool pendingEncodedFrameWasAudible;
        private long lastAudibleTimestamp;
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
        private long transientCaptureDeferrals;
        private long captureDriftAdjustments;
        private long captureOverflowFrames;
        private long directPcmCallbacks;
        private long directPcmInputFrames;
        private long directPcmOutputFrames;
        private long directPcmConsumedFrames;
        private long directPcmPreviousCallbackTimestamp;
        private long directPcmMaximumCallbackGapTicks;
        private long directPcmMaximumCallbackLockWaitTicks;
        private int pacerPrewarmRequested;
        private int pacerPrewarmInProgress;
        private int pacerRecoveryRequested;
        private int pacerLifecycleGateSource;
        private int pacerLifecycleStopping;
        private int pacerFinalClearRequested;
        private long pacerFinalClearGeneration;
        private int pacerPrewarmAttemptedForSegment;
        private long pacerPrewarmRetryAfterTimestamp;
        private long lastRawAudibleTimestamp;
        private long lastSourcePcmTimestamp;
        private long pacerPrewarmAttempts;
        private long pacerPrewarmSuccesses;
        private long pacerPrewarmFailures;
        private long pacerPrewarmMaximumTicks;
        private long lastDiagnosticUtcTicks;
        private int diagnosticLogPending;
        private int mmcssRegistered;
        private int mmcssHighPriority;
        private int mmcssRegistrationError;
        private int deferredDisposeCleanupScheduled;
        private int disposeCleanupCompleted;
        private long startupWarmupReportsSent;
        private bool directPcmRecoveryWindowInvalidated;

        public DualSenseBluetoothSpeakerPassthrough(DualSenseDevice device, byte speakerVolume,
            DualSenseSpeakerCompression speakerCompression, byte speakerBassBoost,
            string sourceEndpointId, ControllerAudioEndpointKind sourceEndpointKind,
            ViiperOutDevice directSpeakerSource = null)
        {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            headsetOnlyAudio = device.HeadsetOnlyAudio;
            speakerSessionId = this.device.CreateBluetoothSpeakerSession();
            this.speakerVolume = speakerVolume;
            this.speakerCompression = (DualSenseSpeakerCompression)Math.Clamp(
                (int)speakerCompression, (int)DualSenseSpeakerCompression.Off,
                (int)DualSenseSpeakerCompression.Strong);
            this.speakerBassBoost = Math.Min(speakerBassBoost,
                DualSenseSpeakerProcessor.MaximumBassBoostDb);
            speakerProcessor = new DualSenseSpeakerProcessor(this.speakerCompression,
                this.speakerBassBoost);
            this.sourceEndpointId = sourceEndpointId ?? string.Empty;
            this.sourceEndpointKind = sourceEndpointKind;
            this.directSpeakerSource = directSpeakerSource;
            directSpeakerSampleRate = directSpeakerSource?.DirectSpeakerPcmSampleRate ?? 0;
            directSpeakerUsesV5Source =
                directSpeakerSource?.UsesV5AudioSource == true;
            if (directSpeakerSampleRate > 0)
            {
                directPcmRateConverter = new DualSensePcm16SourceRateConverter(
                    directSpeakerSampleRate, SampleRate);
                directSourceClockEstimator = new DualSenseSourceClockEstimator(
                    directSpeakerSampleRate);
                directPcmBalanceClockServo =
                    new DualSenseDirectPcmBalanceClockServo();
                if (directSpeakerUsesV5Source)
                {
                    directV5FrameResampler =
                        new DualSenseV5SpeakerClockResampler();
                }
                else
                {
                    directSpeakerFrameResampler =
                        new DualSenseReferenceSpeakerFrameResampler();
                }
                TryCreateDirectPcmTraces();
            }
        }

        private int InitialSourceBufferFrames =>
            directSpeakerUsesV5Source ?
                V5InitialSourceBufferFrames :
                (SampleRate * InitialBufferMs) / 1000;

        private int StartupWarmupReportsForCurrentSource =>
            directSpeakerUsesV5Source ? 0 :
                StartupWarmupReportCount;

        private void TryCreateDirectPcmTraces()
        {
            string directory = Environment.GetEnvironmentVariable(
                "DS4WINDOWS_DUALSENSE_PCM_TRACE_DIRECTORY");
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                directory = Path.GetFullPath(directory);
                Directory.CreateDirectory(directory);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                string rawPath = Path.Combine(directory,
                    $"dualsense-{stamp}-raw-{directSpeakerSampleRate}hz.wav");
                string preOpusPath = Path.Combine(directory,
                    $"dualsense-{stamp}-pre-opus-48000hz.wav");
                string postOpusPath = Path.Combine(directory,
                    $"dualsense-{stamp}-post-opus-48000hz.wav");
                rawDirectPcmTrace = Pcm16WaveTraceWriter.TryCreate(rawPath,
                    directSpeakerSampleRate, Channels);
                preOpusPcmTrace = Pcm16WaveTraceWriter.TryCreate(preOpusPath,
                    SampleRate, Channels);
                postOpusPcmTrace = Pcm16WaveTraceWriter.TryCreate(
                    postOpusPath, SampleRate, Channels);
                if (postOpusPcmTrace != null)
                {
                    traceOpusDecoder = OpusCodecFactory.CreateDecoder(
                        SampleRate, Channels);
                }
                if (rawDirectPcmTrace != null && preOpusPcmTrace != null &&
                    postOpusPcmTrace != null)
                {
                    AppLogger.LogToGui(
                        $"DualSense PCM trace enabled: raw='{rawPath}', preOpus='{preOpusPath}', postOpus='{postOpusPath}'",
                        false);
                }
            }
            catch (Exception ex)
            {
                rawDirectPcmTrace?.Dispose();
                preOpusPcmTrace?.Dispose();
                postOpusPcmTrace?.Dispose();
                traceOpusDecoder?.Dispose();
                rawDirectPcmTrace = null;
                preOpusPcmTrace = null;
                postOpusPcmTrace = null;
                traceOpusDecoder = null;
                AppLogger.LogToGui(
                    $"DualSense PCM trace could not initialize: {ex.Message}",
                    true);
            }
        }

        public bool Matches(DualSenseDevice candidateDevice, byte candidateVolume,
            DualSenseSpeakerCompression candidateCompression, byte candidateBassBoost,
            string candidateSourceEndpointId,
            ControllerAudioEndpointKind candidateSourceEndpointKind,
            ViiperOutDevice candidateDirectSpeakerSource = null)
        {
            return !stopping && ReferenceEquals(device, candidateDevice) &&
                headsetOnlyAudio == candidateDevice.HeadsetOnlyAudio &&
                speakerVolume == candidateVolume &&
                speakerCompression == (DualSenseSpeakerCompression)Math.Clamp(
                    (int)candidateCompression, (int)DualSenseSpeakerCompression.Off,
                    (int)DualSenseSpeakerCompression.Strong) &&
                speakerBassBoost == Math.Min(candidateBassBoost,
                    DualSenseSpeakerProcessor.MaximumBassBoostDb) &&
                sourceEndpointKind == candidateSourceEndpointKind &&
                ReferenceEquals(directSpeakerSource,
                    candidateDirectSpeakerSource) &&
                string.Equals(sourceEndpointId, candidateSourceEndpointId ?? string.Empty,
                    StringComparison.Ordinal);
        }

        public void Start()
        {
            if (!IsGenuineBluetoothDualSense(device))
            {
                throw new InvalidOperationException("Bluetooth speaker passthrough requires a physical Sony DualSense or DualSense Edge.");
            }

            if (!device.EnsureBluetoothCombinedOutputTransport())
            {
                throw new InvalidOperationException(
                    $"Could not initialize the DualSense combined Bluetooth transport: {device.LastBluetoothHapticsWriteStatus}");
            }

            if (!device.ActivateBluetoothSpeakerSession(speakerSessionId))
            {
                throw new InvalidOperationException(
                    "Could not activate the DualSense Bluetooth speaker session.");
            }

            // Serialize the selected physical output before the media lane
            // starts. A fresh speaker session is a no-op; an AUX session gets
            // Sony's speaker-then-headset primer, and the first speaker session
            // after AUX explicitly restores the internal-speaker route.
            if (!device.RearmBluetoothHeadsetOutputRoute())
            {
                throw new InvalidOperationException(
                    $"Could not arm the selected DualSense audio output route: {device.LastBluetoothHapticsWriteStatus}");
            }

            try
            {
                opusEncoder = CreateSpeakerOpusEncoder();
                // Start the physical 0x36 lane before an application produces
                // sound. A continuous valid Opus-silence carrier lets the
                // Bluetooth link settle and the controller reserve fill while
                // idle, so the first real frame replaces silence instead of
                // paying the adapter's measured startup stall.
                Volatile.Write(ref startupWarmupFramesRemaining,
                    StartupWarmupReportsForCurrentSource);
                StartPacerLifecycleWorker();
                RequestPacerLifecyclePreparation(recovery: false,
                    gateSource: true);

                if (directSpeakerSource != null)
                {
                    if (!directSpeakerSource.SupportsDirectSpeakerPcm ||
                        directSpeakerSampleRate <= 0 ||
                        directPcmRateConverter == null)
                    {
                        throw new InvalidOperationException(
                            "VIIPER's direct speaker PCM stream is not available.");
                    }

                    isGameAudioEndpoint = true;
                    if (directSpeakerSource.SupportsAtomicAudioHaptics)
                    {
                        directSpeakerSource.VirtualAtomicAudioHapticsReceived +=
                            DirectSpeakerSource_VirtualAtomicAudioHapticsReceived;
                    }
                    else
                    {
                        directSpeakerSource.VirtualSpeakerPcmReceived +=
                            DirectSpeakerSource_VirtualSpeakerPcmReceived;
                    }
                    worker = new Thread(StreamLoop)
                    {
                        IsBackground = true,
                        Name = "DualSense Bluetooth direct speaker audio",
                        Priority = ThreadPriority.Highest,
                    };
                    // Establish the reusable HID owner while the endpoint is
                    // idle. This removes helper process startup from the first
                    // speaker-only segment without blocking profile/UI setup.
                    worker.Start();
                    AppLogger.LogToGui(
                        $"DualSense Bluetooth speaker passthrough started: direct VIIPER PCM ({directSpeakerSampleRate / 1000} kHz source, no WASAPI loopback, phase-continuous clock correction)",
                        false);
                    return;
                }

                capture = CreateCapture(sourceEndpointId, sourceEndpointKind,
                    out string sourceName);
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

                capturePump = new Thread(() => CapturePumpLoop(source))
                {
                    IsBackground = true,
                    Name = "DualSense Bluetooth speaker capture",
                    Priority = ThreadPriority.Highest,
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
                    (isGameAudioEndpoint ? " (low latency game-audio mode)" : string.Empty) +
                    (speakerProcessor.Enabled ?
                        $" (dynamic range={speakerCompression}, bass/body={speakerBassBoost} dB)" :
                        string.Empty),
                    false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static IWaveIn CreateCapture(string endpointId,
            ControllerAudioEndpointKind endpointKind, out string sourceName)
        {
            if (ProcessLoopbackWaveCapture.TryParseAutomaticEndpointId(
                    endpointId, out int automaticSlot))
            {
                sourceName = "automatic game detection";
                return ProcessLoopbackWaveCapture.CreateAutomatic(
                    automaticSlot);
            }

            if (ProcessLoopbackWaveCapture.TryParseEndpointId(endpointId,
                    out int processId))
            {
                sourceName = $"selected app (process {processId})";
                return new ProcessLoopbackWaveCapture(processId);
            }

            if (ProcessLoopbackWaveCapture.IsProcessEndpointId(endpointId))
            {
                throw new InvalidOperationException(
                    "The selected app is not running, so its audio cannot be streamed to the controller.");
            }

            bool useSystemDefault = string.Equals(endpointId,
                DualSenseAudioPassthrough.DefaultSystemAudioEndpointId,
                StringComparison.Ordinal) ||
                (string.IsNullOrEmpty(endpointId) &&
                    endpointKind == ControllerAudioEndpointKind.Any);
            if (useSystemDefault)
            {
                sourceName = "Default audio endpoint";
                return new LowLatencyWasapiLoopbackCapture(
                    WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice(),
                    LowLatencyCaptureBufferMs);
            }

            using var enumerator = new MMDeviceEnumerator();
            bool autoDetectGameAudio = string.IsNullOrEmpty(endpointId) ||
                string.Equals(endpointId,
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
                    autoDetectGameAudio ? null : endpointId, endpointKind);
            }

            if (endpoint == null)
            {
                throw new InvalidOperationException(autoDetectGameAudio ?
                    "Emulated controller audio endpoint is not available." :
                    "Selected Bluetooth speaker audio source is not available and no active controller audio replacement was found.");
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

        private void DirectSpeakerSource_VirtualSpeakerPcmReceived(
            ViiperOutDevice source, byte[] pcm, int length)
        {
            long callbackEntered = Stopwatch.GetTimestamp();
            try
            {
                lock (directPcmSync)
                {
                    UpdateMaximum(ref directPcmMaximumCallbackLockWaitTicks,
                        Stopwatch.GetTimestamp() - callbackEntered);
                    ProcessDirectSpeakerPcmLocked(source, pcm, 0, length,
                        callbackEntered);
                }
            }
            catch (Exception ex)
            {
                // This callback runs on VIIPER's isolated speaker consumer.
                // Never allow an audio conversion fault to terminate that
                // consumer or starve later controller audio.
                if (Interlocked.Exchange(ref loggedDirectPcmFailure, 1) == 0)
                {
                    string message = ex.Message;
                    ThreadPool.QueueUserWorkItem(_ => AppLogger.LogToGui(
                        $"DualSense direct speaker PCM conversion failed: {message}",
                        true));
                }
            }
        }

        private void DirectSpeakerSource_VirtualAtomicAudioHapticsReceived(
            ViiperOutDevice source, byte[] payload, int feedbackOffset,
            int feedbackLength, int speakerPcmOffset, int speakerPcmLength,
            int targetDeviceIndex)
        {
            long callbackEntered = Stopwatch.GetTimestamp();
            try
            {
                lock (directPcmSync)
                {
                    UpdateMaximum(ref directPcmMaximumCallbackLockWaitTicks,
                        Stopwatch.GetTimestamp() - callbackEntered);
                    if (stopping || !ReferenceEquals(source,
                            directSpeakerSource) || payload == null ||
                        feedbackLength != atomicFeedback.Length ||
                        feedbackOffset < 0 || speakerPcmOffset < 0 ||
                        feedbackOffset + feedbackLength > payload.Length ||
                        speakerPcmOffset + speakerPcmLength > payload.Length ||
                        (directSpeakerUsesV5Source &&
                            speakerPcmLength != FrameSamples * Channels *
                                sizeof(short)))
                    {
                        return;
                    }

                    // Claim the physical speaker clock before publishing this
                    // generation's haptics. The feedback update can therefore
                    // only refresh the speaker template; it cannot escape as a
                    // separate control report at the exact segment boundary.
                    bool speakerAudible = HasAudiblePcm16(payload,
                        speakerPcmOffset, speakerPcmLength);
                    if (speakerAudible &&
                        !device.BeginBluetoothAtomicSpeakerFrame(
                            speakerSessionId))
                    {
                        return;
                    }

                    Buffer.BlockCopy(payload, feedbackOffset, atomicFeedback,
                        0, feedbackLength);
                    source.ApplyAtomicAudioHapticsFeedback(atomicFeedback,
                        feedbackLength, targetDeviceIndex);
                    ProcessDirectSpeakerPcmLocked(source, payload,
                        speakerPcmOffset, speakerPcmLength, callbackEntered);
                }
            }
            catch (Exception ex)
            {
                LogDirectPcmFailure(ex);
            }
        }

        private void ProcessDirectSpeakerPcmLocked(ViiperOutDevice source,
            byte[] pcm, int offset, int length, long callbackEntered)
        {
            if (stopping || !ReferenceEquals(source, directSpeakerSource) ||
                pcm == null || offset < 0 || length <= 0 ||
                offset + length > pcm.Length)
            {
                return;
            }

            int alignedLength = length & ~(sizeof(short) * Channels - 1);
            if (alignedLength <= 0)
            {
                return;
            }

            long previousCallback = Interlocked.Exchange(
                ref directPcmPreviousCallbackTimestamp, callbackEntered);
            if (previousCallback != 0)
            {
                UpdateMaximum(ref directPcmMaximumCallbackGapTicks,
                    callbackEntered - previousCallback);
            }

            if (ShouldResetV5SourceBeforeAppendingCallback(
                    directSpeakerUsesV5Source, previousCallback,
                    callbackEntered, Stopwatch.Frequency))
            {
                // This is the first PCM callback of a genuinely new V5 source
                // generation. Discard the old ring tail and both fractional
                // resampler stages before observing or appending new samples.
                // The physical idle carrier remains armed.
                lock (syncRoot)
                {
                    ResetCapturePipelineAtSegmentBoundaryLocked();
                }

                if (audioSegmentActive)
                {
                    audioSegmentActive = false;
                    Interlocked.Increment(ref audioSegmentStops);
                }

                Interlocked.Exchange(ref lastRawAudibleTimestamp, 0);
                Interlocked.Exchange(ref lastSourcePcmTimestamp, 0);
                Volatile.Write(ref pacerPrewarmAttemptedForSegment, 0);
            }

            Interlocked.Increment(ref directPcmCallbacks);
            long totalInputFrames = Interlocked.Add(ref directPcmInputFrames,
                alignedLength / (sizeof(short) * Channels));
            directSourceClockEstimator?.Observe(totalInputFrames,
                callbackEntered);
            rawDirectPcmTrace?.Write(pcm, offset, alignedLength);
            int consumed = 0;
            while (consumed < alignedLength && !stopping)
            {
                int chunkLength = Math.Min(DirectPcmChunkBytes,
                    alignedLength - consumed) &
                    ~(sizeof(short) * Channels - 1);
                if (chunkLength <= 0)
                {
                    break;
                }

                int convertedFrames = directPcmRateConverter.Convert(
                    pcm, offset + consumed, chunkLength, directPcmFrame);
                if (convertedFrames > 0)
                {
                    AppendCaptureSamples(directPcmFrame,
                        convertedFrames * Channels);
                    Interlocked.Add(ref directPcmOutputFrames,
                        convertedFrames);
                    Interlocked.Add(ref captureInputFrames,
                        convertedFrames);
                }

                consumed += chunkLength;
            }

            Interlocked.Increment(ref captureCallbackCount);
        }

        private void LogDirectPcmFailure(Exception ex)
        {
            if (Interlocked.Exchange(ref loggedDirectPcmFailure, 1) == 0)
            {
                string message = ex.Message;
                ThreadPool.QueueUserWorkItem(_ => AppLogger.LogToGui(
                    $"DualSense direct speaker PCM conversion failed: {message}",
                    true));
            }
        }

        /// <summary>
        /// Creates the exact proven MeasuredTransport DualSense speaker encoder. The
        /// 480-sample input passed to Encode selects a 10 ms Opus frame; leaving
        /// complexity at Concentus' recommended default preserves music quality.
        /// </summary>
        internal static IOpusEncoder CreateSpeakerOpusEncoder()
        {
            IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(SampleRate,
                Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate = OpusBytes * 8 * 100;
            encoder.UseVBR = false;
            encoder.Complexity = 0;
            return encoder;
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
            audioSegmentActive = false;
            RequestGenerationEnd(Interlocked.Read(ref speakerGeneration));
            Volatile.Write(ref pacerPrewarmAttemptedForSegment, 0);
            Interlocked.Exchange(ref lastRawAudibleTimestamp, 0);
            Interlocked.Exchange(ref lastSourcePcmTimestamp, 0);
            if (!stopping && e.Exception != null)
            {
                AppLogger.LogToGui($"DualSense Bluetooth speaker capture stopped: {e.Exception.Message}", true);
            }
        }

        private void CapturePumpLoop(ISampleProvider source)
        {
            using MultimediaThreadRegistration mmcss =
                MultimediaThreadRegistration.EnterProAudio();
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

            // Source presence is defined by produced PCM, not amplitude.
            // Quiet USB/VIIPER blocks are still media and must not be replaced
            // by a separately clocked idle carrier.
            Interlocked.Exchange(ref lastSourcePcmTimestamp,
                Stopwatch.GetTimestamp());

            if (HasAudibleSamples(samples, sampleCount))
            {
                // The helper process is deliberately started by StreamLoop,
                // not this producer callback. That lets the transport handoff
                // overlap PCM accumulation without ever blocking VIIPER's
                // isolated speaker consumer.
                long now = Stopwatch.GetTimestamp();
                long previousRawAudible = Interlocked.Exchange(
                    ref lastRawAudibleTimestamp, now);
                bool newRawGeneration = previousRawAudible == 0 ||
                    now - previousRawAudible > Stopwatch.Frequency *
                        IdleKeepAliveMs / 1000;
                if (newRawGeneration)
                {
                    long generation = Interlocked.Increment(
                        ref speakerGeneration);
                    if (generation == 0)
                    {
                        Interlocked.Increment(ref speakerGeneration);
                    }

                    Volatile.Write(
                        ref pacerPrewarmAttemptedForSegment, 0);
                    // The idle 0x36 carrier keeps an active helper primed. Do
                    // not clear it or insert another six silent frames when a
                    // new application begins rendering; real audio can replace
                    // the carrier on the next 10.667 ms boundary. Recovery and
                    // first ownership still use the full gated warmup path.
                    if (!device.BluetoothAudioPacerActive ||
                        device.BluetoothAudioPacerRecoveryRequired ||
                        device.BluetoothAudioLifecycleTransitioning)
                    {
                        Volatile.Write(ref startupWarmupFramesRemaining,
                            StartupWarmupReportsForCurrentSource);
                        // Request during cooldown without consuming the attempt
                        // latch. The lifecycle worker waits until retryAfter
                        // while the stream gate preserves source history.
                        RequestPacerLifecyclePreparation(recovery: false,
                            gateSource: true);
                    }
                }
            }

            int sourceFrameOffset = (sampleCount / Channels) - frames;
            lock (syncRoot)
            {
                int overflowFrames = Math.Max(0,
                    captureRingBufferedFrames + frames - CaptureRingFrames);
                if (overflowFrames > 0)
                {
                    captureRingReadIndex = (captureRingReadIndex +
                        overflowFrames) % CaptureRingFrames;
                    captureRingBufferedFrames -= overflowFrames;
                    capturePrimed = false;
                    ResetCaptureClockLocked(resetSourceClock: false);
                    ResetDirectPcmBalanceWindowLocked();
                    directPcmRateConverter?.Reset();
                    ResetSpeakerResamplingLocked();
                    fadeInAfterCaptureUnderrun = true;
                    Interlocked.Add(ref captureOverflowFrames,
                        overflowFrames);
                }

                for (int frameIndex = 0; frameIndex < frames; frameIndex++)
                {
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

        private bool TryFillOutputFrame(out int consumedSourceFrames)
        {
            if (directSpeakerSource != null)
            {
                lock (directPcmSync)
                {
                    lock (syncRoot)
                    {
                        bool filled = TryFillOutputFrameLocked(
                            out consumedSourceFrames);
                        if (filled)
                        {
                            RecordDirectPcmBalanceLocked(
                                consumedSourceFrames);
                        }

                        return filled;
                    }
                }
            }

            lock (syncRoot)
            {
                return TryFillOutputFrameLocked(out consumedSourceFrames);
            }
        }

        private bool TryFillOutputFrameLocked(out int consumedSourceFrames)
        {
            consumedSourceFrames = 0;
            int initialFrames = InitialSourceBufferFrames;
            if (!capturePrimed)
            {
                if (captureRingBufferedFrames < initialFrames)
                {
                    return false;
                }

                capturePrimed = true;
            }

            int requestedSourceFrames;
            if (directV5FrameResampler != null)
            {
                requestedSourceFrames = preparedDirectSpeakerSourceFrames;
                if (requestedSourceFrames == 0)
                {
                    UpdateCaptureClockRatioLocked();
                    directV5FrameResampler.SetInputRateRatio(
                        captureCurrentClockRatio);
                    requestedSourceFrames =
                        directV5FrameResampler.PrepareOutputFrame();
                    preparedDirectSpeakerSourceFrames =
                        requestedSourceFrames;
                }
            }
            else if (directSpeakerFrameResampler != null)
            {
                requestedSourceFrames = preparedDirectSpeakerSourceFrames;
                if (requestedSourceFrames == 0)
                {
                    UpdateCaptureClockRatioLocked();
                    directSpeakerFrameResampler.SetInputRateRatio(
                        captureCurrentClockRatio);
                    requestedSourceFrames =
                        directSpeakerFrameResampler.PrepareOutputFrame();
                    preparedDirectSpeakerSourceFrames =
                        requestedSourceFrames;
                }
            }
            else
            {
                UpdateCaptureClockRatioLocked();
                speakerFrameResampler.SetInputRateRatio(
                    captureCurrentClockRatio);
                requestedSourceFrames =
                    speakerFrameResampler.PrepareOutputFrame(FrameSamples);
            }
            if (captureRingBufferedFrames < requestedSourceFrames)
            {
                // A V5 VIIPER stream arrives in exact 480-frame source
                // callbacks while this lane consumes about 512 frames per
                // physical report. Seeing the ring briefly below the exact
                // fractional request is normal callback phase, not a new
                // audio generation. Preserve all source and resampler state.
                return false;
            }

            CopyCaptureFramesToSpeakerResamplerLocked(
                requestedSourceFrames);
            int producedFrames = directV5FrameResampler != null ?
                directV5FrameResampler.ConvertPreparedOutput(
                    speakerResampleInput, 0, requestedSourceFrames,
                    frame, 0) :
                directSpeakerFrameResampler != null ?
                    directSpeakerFrameResampler.ConvertPreparedOutput(
                        speakerResampleInput, 0, requestedSourceFrames,
                        frame, 0) :
                    speakerFrameResampler.ConvertPreparedOutput(
                        speakerResampleInput, 0, requestedSourceFrames,
                        frame, 0);
            if (directV5FrameResampler != null ||
                directSpeakerFrameResampler != null)
            {
                preparedDirectSpeakerSourceFrames = 0;
            }
            consumedSourceFrames = requestedSourceFrames;
            return producedFrames == FrameSamples;
        }

        private void RecordDirectPcmBalanceLocked(int consumedSourceFrames)
        {
            if (directPcmBalanceClockServo == null ||
                consumedSourceFrames <= 0)
            {
                return;
            }

            long totalConsumedFrames = Interlocked.Add(
                ref directPcmConsumedFrames, consumedSourceFrames);
            directPcmBalanceClockServo.Observe(
                Interlocked.Read(ref captureInputFrames),
                totalConsumedFrames, Stopwatch.GetTimestamp());
            // Advance only for a successfully produced radio packet. Callback
            // batching and retry loops therefore cannot accelerate the slew.
            directPcmBalanceClockServo.AdvanceAppliedTrim(
                BluetoothSpeakerCadenceMs / (1000.0 *
                    device.DualSenseBluetoothPresentationClockRatio));
        }

        private void CopyCaptureFramesToSpeakerResamplerLocked(
            int sourceFrames)
        {
            int firstFrames = Math.Min(sourceFrames,
                CaptureRingFrames - captureRingReadIndex);
            Array.Copy(captureRing, captureRingReadIndex * Channels,
                speakerResampleInput, 0, firstFrames * Channels);
            int remainingFrames = sourceFrames - firstFrames;
            if (remainingFrames > 0)
            {
                Array.Copy(captureRing, 0, speakerResampleInput,
                    firstFrames * Channels, remainingFrames * Channels);
            }

            captureRingReadIndex = (captureRingReadIndex +
                sourceFrames) % CaptureRingFrames;
            captureRingBufferedFrames -= sourceFrames;
        }

        private void ResetSpeakerResamplingLocked()
        {
            speakerFrameResampler.Reset();
            directSpeakerFrameResampler?.Reset();
            directV5FrameResampler?.Reset();
            preparedDirectSpeakerSourceFrames = 0;
        }

        private void ResetDirectPcmBalanceWindow()
        {
            if (directPcmBalanceClockServo == null)
            {
                return;
            }

            lock (directPcmSync)
            {
                lock (syncRoot)
                {
                    ResetDirectPcmBalanceWindowLocked();
                }
            }
        }

        private void ResetDirectPcmBalanceWindowLocked()
        {
            directPcmBalanceClockServo?.ResetWindow();
        }

        private void ResetCaptureClockLocked(bool resetSourceClock)
        {
            captureClockInitialized = false;
            captureSmoothedBufferedFrames =
                (SampleRate * TargetBufferMs) / 1000;
            captureTargetClockRatio = 1.0;
            captureCurrentClockRatio = 1.0;
            if (resetSourceClock)
            {
                directSourceClockEstimator?.Reset();
            }
        }

        private void ResetCapturePipelineAtSegmentBoundary()
        {
            if (directSpeakerSource != null)
            {
                lock (directPcmSync)
                {
                    lock (syncRoot)
                    {
                        ResetCapturePipelineAtSegmentBoundaryLocked();
                    }
                }
                return;
            }

            lock (syncRoot)
            {
                ResetCapturePipelineAtSegmentBoundaryLocked();
            }
        }

        private void ResetCapturePipelineAtSegmentBoundaryLocked()
        {
            captureRingReadIndex = captureRingWriteIndex;
            captureRingBufferedFrames = 0;
            capturePrimed = false;
            ResetCaptureClockLocked(resetSourceClock: true);
            ResetDirectPcmBalanceWindowLocked();
            directPcmRateConverter?.Reset();
            ResetSpeakerResamplingLocked();
        }

        private void UpdateCaptureClockRatioLocked()
        {
            int targetFrames = (SampleRate * TargetBufferMs) / 1000;
            if (directSpeakerSource != null)
            {
                // VIIPER V5 publishes untouched 48 kHz PCM in exact 480-frame
                // callbacks; its independent rear-haptics assembler still uses
                // 512-frame blocks. Windows may batch speaker callbacks by tens
                // of milliseconds, so instantaneous occupancy is a callback
                // sawtooth, not a clock signal. Follow the controller's
                // long-window clock plus the independent exact
                // produced-minus-consumed balance trim.
                double directControllerLockedRatio =
                    CalculateControllerLockedInputRateRatio(
                        device.DualSenseBluetoothPresentationClockRatio,
                        clockStable: true);
                double directBalanceTrimRatio =
                    directPcmBalanceClockServo?.AppliedTrimRatio ?? 1.0;
                captureTargetClockRatio = Math.Clamp(
                    directControllerLockedRatio * directBalanceTrimRatio,
                    1.0 - DirectCaptureClockMaximumCorrection,
                    1.0 + DirectCaptureClockMaximumCorrection);
                captureCurrentClockRatio = SlewCaptureClockRatio(
                    captureCurrentClockRatio, captureTargetClockRatio,
                    DirectCaptureClockRatioSlewPerPacket);
                if (Math.Abs(captureCurrentClockRatio - 1.0) > 0.000001)
                {
                    Interlocked.Increment(ref captureDriftAdjustments);
                }
                captureClockInitialized = true;
                return;
            }

            if (!captureClockInitialized)
            {
                // Do not bias the servo with whichever side of the normal
                // 480/512 callback sawtooth happened to prime the stream.
                captureSmoothedBufferedFrames = targetFrames;
                captureTargetClockRatio = 1.0;
                captureCurrentClockRatio = 1.0;
                captureClockInitialized = true;
                return;
            }

            captureSmoothedBufferedFrames +=
                (captureRingBufferedFrames - captureSmoothedBufferedFrames) *
                CaptureClockSmoothingAlpha;
            double controllerLockedRatio =
                CalculateControllerLockedInputRateRatio(
                    device.DualSenseBluetoothPresentationClockRatio,
                    clockStable: true);
            captureTargetClockRatio = Math.Clamp(
                controllerLockedRatio * CalculateCaptureClockTargetRatio(
                    captureSmoothedBufferedFrames, targetFrames),
                1.0 - CaptureClockMaximumCorrection,
                1.0 + CaptureClockMaximumCorrection);
            captureCurrentClockRatio = SlewCaptureClockRatio(
                captureCurrentClockRatio, captureTargetClockRatio);
            if (Math.Abs(captureCurrentClockRatio - 1.0) > 0.000001)
            {
                Interlocked.Increment(ref captureDriftAdjustments);
            }
        }

        internal static double CalculateCaptureClockTargetRatio(
            double smoothedBufferedFrames, int targetFrames)
        {
            double errorFrames = smoothedBufferedFrames - targetFrames;
            if (Math.Abs(errorFrames) <= CaptureClockErrorDeadbandFrames)
            {
                errorFrames = 0.0;
            }

            return 1.0 + Math.Clamp(
                errorFrames * CaptureClockCorrectionGain,
                -CaptureClockMaximumCorrection,
                CaptureClockMaximumCorrection);
        }

        internal static double SlewCaptureClockRatio(double currentRatio,
            double targetRatio)
        {
            return SlewCaptureClockRatio(currentRatio, targetRatio,
                CaptureClockRatioSlewPerPacket);
        }

        internal static double SlewCaptureClockRatio(double currentRatio,
            double targetRatio, double maximumStep)
        {
            return currentRatio + Math.Clamp(targetRatio - currentRatio,
                -maximumStep, maximumStep);
        }

        internal static double CalculateControllerLockedInputRateRatio(
            double controllerClockRatio, bool clockStable)
        {
            return CalculateSourceControllerLockedInputRateRatio(
                1.0, false, controllerClockRatio, clockStable);
        }

        internal static double CalculateSourceControllerLockedInputRateRatio(
            double sourceClockRatio, bool sourceClockStable,
            double controllerClockRatio, bool controllerClockStable)
        {
            double sourceRatio = sourceClockStable &&
                double.IsFinite(sourceClockRatio) &&
                sourceClockRatio >= 0.995 && sourceClockRatio <= 1.005 ?
                sourceClockRatio : 1.0;
            double controllerRatio = controllerClockStable &&
                double.IsFinite(controllerClockRatio) &&
                controllerClockRatio >= 0.995 &&
                controllerClockRatio <= 1.005 ?
                controllerClockRatio : 1.0;

            return Math.Clamp(sourceRatio / controllerRatio,
                1.0 - CaptureClockMaximumCorrection,
                1.0 + CaptureClockMaximumCorrection);
        }

        private void WaitForInitialCaptureBuffer()
        {
            int minimumFrames = InitialSourceBufferFrames;
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

        private bool HasEnoughBufferedCaptureForNextFrame()
        {
            if (directSpeakerSource != null)
            {
                lock (directPcmSync)
                {
                    lock (syncRoot)
                    {
                        return HasEnoughBufferedCaptureForNextFrameLocked();
                    }
                }
            }

            lock (syncRoot)
            {
                return HasEnoughBufferedCaptureForNextFrameLocked();
            }
        }

        private bool HasEnoughBufferedCaptureForNextFrameLocked()
        {
            if (!capturePrimed)
            {
                return captureRingBufferedFrames >= InitialSourceBufferFrames;
            }

            if (directV5FrameResampler != null)
            {
                if (preparedDirectSpeakerSourceFrames == 0)
                {
                    UpdateCaptureClockRatioLocked();
                    directV5FrameResampler.SetInputRateRatio(
                        captureCurrentClockRatio);
                    preparedDirectSpeakerSourceFrames =
                        directV5FrameResampler.PrepareOutputFrame();
                }

                return captureRingBufferedFrames >=
                    preparedDirectSpeakerSourceFrames;
            }

            if (directSpeakerFrameResampler != null)
            {
                if (preparedDirectSpeakerSourceFrames == 0)
                {
                    UpdateCaptureClockRatioLocked();
                    directSpeakerFrameResampler.SetInputRateRatio(
                        captureCurrentClockRatio);
                    preparedDirectSpeakerSourceFrames =
                        directSpeakerFrameResampler.PrepareOutputFrame();
                }

                return captureRingBufferedFrames >=
                    preparedDirectSpeakerSourceFrames;
            }

            // The WASAPI fallback retains its original output-driven route.
            return captureRingBufferedFrames >= SourcePullFrames;
        }

        internal static bool ShouldAttemptPacerLifecycle(
            bool recoveryRequested, bool helperActive, bool segmentAttempted,
            long retryAfterTimestamp, long nowTimestamp)
        {
            return !helperActive && retryAfterTimestamp <= nowTimestamp &&
                (recoveryRequested || !segmentAttempted);
        }

        internal static int GetPacerRetryWaitMilliseconds(
            long retryAfterTimestamp, long nowTimestamp, long frequency)
        {
            if (retryAfterTimestamp <= nowTimestamp || frequency <= 0)
            {
                return 0;
            }

            long remainingTicks = retryAfterTimestamp - nowTimestamp;
            long milliseconds = (remainingTicks * 1000L + frequency - 1) /
                frequency;
            return (int)Math.Clamp(milliseconds, 1L, 1000L);
        }

        internal static bool ShouldEmitStartupWarmup(int reportsRemaining,
            bool lifecycleGateActive, bool recoveryRequired)
        {
            return reportsRemaining > 0 && !lifecycleGateActive &&
                !recoveryRequired;
        }

        internal static bool ShouldBackpressurePacerProducer(
            bool helperActive, int pendingFrames,
            bool usesV5Source, long presentedReports = 1)
        {
            if (!helperActive)
            {
                return false;
            }

            int target;
            if (presentedReports <= 0)
            {
                target = DualSenseBluetoothAudioPacer.NativePrimeReportCount;
            }
            else
            {
                target = usesV5Source ?
                    V5SourceReservoirTargetFrames :
                    PacerReservoirTargetFrames;
            }
            return pendingFrames >= target;
        }

        internal static bool ShouldMaintainIdleCarrierDuringV5Prime(
            bool usesV5Source, bool sourcePrimePending,
            bool captureReady, bool sourceRecentlyActive)
        {
            return usesV5Source && sourcePrimePending &&
                !captureReady && sourceRecentlyActive;
        }

        internal static bool ShouldEmitV5IdleCarrier(
            bool usesV5Source, bool sourceRecentlyActive)
        {
            return usesV5Source && !sourceRecentlyActive;
        }

        internal static bool ShouldResetV5SourceBeforeAppendingCallback(
            bool usesV5Source, long previousCallbackTimestamp,
            long callbackTimestamp, long timestampFrequency)
        {
            if (!usesV5Source || previousCallbackTimestamp <= 0 ||
                callbackTimestamp < previousCallbackTimestamp ||
                timestampFrequency <= 0)
            {
                return false;
            }

            double callbackGapMilliseconds =
                (callbackTimestamp - previousCallbackTimestamp) * 1000.0 /
                timestampFrequency;
            return callbackGapMilliseconds >=
                V5HardSourceDiscontinuityMs;
        }

        // V5 transfers its eight complete source blocks into the strict
        // OVERLAPPED FIFO as one startup burst. HidBth still owns their radio
        // cadence; spacing the WriteFile admissions here prevents the
        // controller-side reserve from ever reaching the native transport's operating
        // level and leaves ordinary completion droughts audible.
        internal static double StartupWarmupLatencyMilliseconds => 0.0;

        internal static bool ShouldDeferDisposeCleanup(bool workerAlive,
            bool capturePumpAlive, bool lifecycleWorkerAlive)
        {
            return workerAlive || capturePumpAlive || lifecycleWorkerAlive;
        }

        private void StartPacerLifecycleWorker()
        {
            pacerLifecycleWorker = new Thread(PacerLifecycleLoop)
            {
                IsBackground = true,
                Name = "DualSense Bluetooth audio lifecycle",
                Priority = ThreadPriority.AboveNormal,
            };
            pacerLifecycleWorker.Start();
        }

        private void RequestPacerLifecyclePreparation(bool recovery,
            bool gateSource)
        {
            if (stopping || Volatile.Read(ref pacerLifecycleStopping) != 0)
            {
                return;
            }

            if (gateSource)
            {
                Volatile.Write(ref pacerLifecycleGateSource, 1);
            }

            if (recovery)
            {
                Volatile.Write(ref pacerRecoveryRequested, 1);
            }
            Volatile.Write(ref pacerPrewarmRequested, 1);
            pacerLifecycleRequested.Set();
        }

        private void RequestGenerationEnd(long generation)
        {
            if (generation == 0)
            {
                return;
            }

            Interlocked.Exchange(ref pacerFinalClearGeneration, generation);
            Volatile.Write(ref pacerFinalClearRequested, 1);
            pacerLifecycleRequested.Set();
        }

        internal static long SelectPacerFinalClearGenerationForRetry(
            long pendingGeneration, long failedGeneration)
        {
            return Math.Max(pendingGeneration, failedGeneration);
        }

        private void RestoreGenerationEndRequest(long failedGeneration)
        {
            if (failedGeneration == 0)
            {
                return;
            }

            long observed;
            long replacement;
            do
            {
                observed = Interlocked.Read(ref pacerFinalClearGeneration);
                replacement = SelectPacerFinalClearGenerationForRetry(
                    observed, failedGeneration);
                if (replacement == observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref pacerFinalClearGeneration,
                replacement, observed) != observed);

            Volatile.Write(ref pacerFinalClearRequested, 1);
        }

        private bool HasRecentRawAudible(long now,
            int maximumAgeMilliseconds = IdleKeepAliveMs)
        {
            long rawAudible = Interlocked.Read(ref lastRawAudibleTimestamp);
            long age = now - rawAudible;
            return rawAudible != 0 && age >= 0 && age <=
                Stopwatch.Frequency * maximumAgeMilliseconds / 1000;
        }

        private bool HasRecentSourcePcm(long now,
            int maximumAgeMilliseconds = TransientCaptureShortageLeaseMs)
        {
            long sourcePcm = Interlocked.Read(ref lastSourcePcmTimestamp);
            long age = now - sourcePcm;
            return sourcePcm != 0 && age >= 0 && age <=
                Stopwatch.Frequency * maximumAgeMilliseconds / 1000;
        }

        private bool IsV5SourcePrimePending()
        {
            if (!directSpeakerUsesV5Source)
            {
                return false;
            }

            lock (directPcmSync)
            {
                lock (syncRoot)
                {
                    return !capturePrimed;
                }
            }
        }

        private bool TryConfirmInactiveV5Source()
        {
            if (!directSpeakerUsesV5Source)
            {
                return true;
            }

            // Recheck source freshness under the producer lock. A new VIIPER
            // callback can arrive after StreamLoop's optimistic idle check;
            // never erase that callback or replace it with an idle carrier.
            lock (directPcmSync)
            {
                bool sourceRecentlyActive = HasRecentSourcePcm(
                    Stopwatch.GetTimestamp());
                if (sourceRecentlyActive)
                {
                    return false;
                }

                // A missing callback is not a source-generation boundary.
                // the native source and VIIPER can pause longer than the freshness lease
                // while the same endpoint generation remains active. Emit a
                // paced idle carrier, but preserve the ring, fractional
                // resampler history, prime, and segment state so the next
                // callback resumes immediately. An explicit lifecycle change,
                // or the first callback after the conservative hard-gap
                // boundary, resets those fields before new PCM is appended.
                return ShouldEmitV5IdleCarrier(
                    usesV5Source: true,
                    sourceRecentlyActive: false);
            }
        }

        internal static bool ShouldDeferTransientCaptureShortage(
            bool captureUnderrun, bool wasAudioSegmentActive,
            bool rawAudioIsFresh)
        {
            return captureUnderrun && wasAudioSegmentActive &&
                rawAudioIsFresh;
        }

        private void PacerLifecycleLoop()
        {
            try
            {
                while (Volatile.Read(ref pacerLifecycleStopping) == 0)
                {
                    long finalClearGenerationInProgress = 0;
                    try
                    {
                        if (Interlocked.Exchange(
                            ref pacerFinalClearRequested, 0) != 0)
                        {
                            finalClearGenerationInProgress = Interlocked.Exchange(
                            ref pacerFinalClearGeneration, 0);
                            bool ended = device.EndBluetoothSpeakerGeneration(
                                speakerSessionId,
                                finalClearGenerationInProgress);
                            // If no speaker report ever claimed this generation,
                            // there is no active token to end. A session reset is
                            // still required to commit a microphone transition at
                            // the boundary, but only while this remains the newest
                            // local generation; an old request must never clear a
                            // newer generation.
                            if (!ended && finalClearGenerationInProgress != 0 &&
                                Interlocked.Read(ref speakerGeneration) ==
                                    finalClearGenerationInProgress)
                            {
                                device.ResetBluetoothSpeakerSession(
                                    speakerSessionId);
                            }

                            finalClearGenerationInProgress = 0;
                        }

                        bool recovery = Volatile.Read(
                            ref pacerRecoveryRequested) != 0 ||
                            device.BluetoothAudioPacerRecoveryRequired;
                        if (Volatile.Read(ref pacerPrewarmRequested) == 0 &&
                            !recovery)
                        {
                            pacerLifecycleRequested.WaitOne(250);
                            continue;
                        }

                        long now = Stopwatch.GetTimestamp();
                        bool rawAudible = HasRecentRawAudible(now);
                        bool attempted = Volatile.Read(
                            ref pacerPrewarmAttemptedForSegment) != 0;
                        long retryAfter = Volatile.Read(
                            ref pacerPrewarmRetryAfterTimestamp);

                        if (device.BluetoothAudioPacerActive)
                        {
                            Volatile.Write(ref pacerPrewarmRequested, 0);
                            Volatile.Write(ref pacerRecoveryRequested, 0);
                            Volatile.Write(ref pacerLifecycleGateSource, 0);
                            captureFramesAvailable.Set();
                            continue;
                        }

                        if (!ShouldAttemptPacerLifecycle(recovery,
                            device.BluetoothAudioPacerActive, attempted,
                            retryAfter, now))
                        {
                            if (!recovery && attempted)
                            {
                                Volatile.Write(ref pacerPrewarmRequested, 0);
                                Volatile.Write(ref pacerLifecycleGateSource, 0);
                                captureFramesAvailable.Set();
                                continue;
                            }

                            int waitMilliseconds = GetPacerRetryWaitMilliseconds(
                                retryAfter, now, Stopwatch.Frequency);
                            pacerLifecycleRequested.WaitOne(
                                waitMilliseconds == 0 ? 10 : waitMilliseconds);
                            continue;
                        }

                        if (rawAudible && !recovery &&
                            Interlocked.CompareExchange(
                                ref pacerPrewarmAttemptedForSegment, 1, 0) != 0)
                        {
                            Volatile.Write(ref pacerPrewarmRequested, 0);
                            Volatile.Write(ref pacerLifecycleGateSource, 0);
                            captureFramesAvailable.Set();
                            continue;
                        }

                        Volatile.Write(ref pacerPrewarmRequested, 0);
                        Volatile.Write(ref pacerRecoveryRequested, 0);
                        Volatile.Write(ref pacerPrewarmInProgress, 1);
                        long started = Stopwatch.GetTimestamp();
                        Interlocked.Increment(ref pacerPrewarmAttempts);
                        bool prepared = false;
                        try
                        {
                            prepared = recovery ?
                                device.RecoverBluetoothSpeakerClockTransport(
                                    directSpeakerUsesV5Source) :
                                device.PrepareBluetoothSpeakerClockTransport(
                                    directSpeakerUsesV5Source);
                            // The session boundary clears stale queued audio and
                            // physically commits any pending microphone transition
                            // before speaker traffic is released again.
                            device.ResetBluetoothSpeakerSession(speakerSessionId);
                        }
                        catch (Exception ex)
                        {
                            if (Interlocked.Exchange(
                                    ref loggedPacerPrewarmFailure, 1) == 0)
                            {
                                AppLogger.LogToGui(
                                    $"DualSense Bluetooth audio lifecycle failed: {ex.GetType().Name}: {ex.Message}",
                                    true);
                            }
                        }
                        finally
                        {
                            if (prepared)
                            {
                                Interlocked.Increment(ref pacerPrewarmSuccesses);
                                Volatile.Write(ref pacerPrewarmRetryAfterTimestamp, 0);
                                Volatile.Write(ref pacerPrewarmRequested, 0);
                                Volatile.Write(ref pacerRecoveryRequested, 0);
                            }
                            else
                            {
                                Interlocked.Increment(ref pacerPrewarmFailures);
                                Volatile.Write(ref pacerPrewarmRetryAfterTimestamp,
                                    Stopwatch.GetTimestamp() + Stopwatch.Frequency *
                                        PacerPrewarmRetryMs / 1000);
                                // Once ownership retirement has started, never fall
                                // through to source consumption or a competing
                                // direct HID writer merely because replacement
                                // startup failed. Preserve the source gate and retry
                                // asynchronously after cooldown.
                                Volatile.Write(ref pacerRecoveryRequested, 1);
                                Volatile.Write(ref pacerPrewarmRequested, 1);
                            }

                            UpdateMaximum(ref pacerPrewarmMaximumTicks,
                                Stopwatch.GetTimestamp() - started);
                            Volatile.Write(ref pacerPrewarmInProgress, 0);
                            Volatile.Write(ref pacerLifecycleGateSource,
                                prepared ? 0 : 1);
                            captureFramesAvailable.Set();
                        }
                    }
                    catch (Exception ex)
                    {
                        RestoreGenerationEndRequest(
                            finalClearGenerationInProgress);
                        if (Volatile.Read(ref pacerLifecycleStopping) != 0 ||
                            stopping)
                        {
                            break;
                        }

                        // Keep the source gated and force a full transport retry.
                        // Most importantly, do not allow a transient End/Reset
                        // cleanup failure to terminate the sole lifecycle worker.
                        Volatile.Write(ref pacerLifecycleGateSource, 1);
                        Volatile.Write(ref pacerRecoveryRequested, 1);
                        Volatile.Write(ref pacerPrewarmRequested, 1);
                        Volatile.Write(ref pacerPrewarmRetryAfterTimestamp,
                            Stopwatch.GetTimestamp() + Stopwatch.Frequency *
                                PacerPrewarmRetryMs / 1000);
                        if (Interlocked.Exchange(
                                ref loggedPacerLifecycleFailure, 1) == 0)
                        {
                            try
                            {
                                AppLogger.LogToGui(
                                    $"DualSense Bluetooth lifecycle worker recovered from {ex.GetType().Name}: {ex.Message}",
                                    true);
                            }
                            catch
                            {
                            }
                        }

                        // Use the wakeable cooldown so a persistent cleanup
                        // fault cannot spin, while shutdown/new work can still
                        // interrupt the wait immediately.
                        pacerLifecycleRequested.WaitOne(PacerPrewarmRetryMs);
                    }
                }
            }
            finally
            {
                try
                {
                    long finalGeneration = Interlocked.Read(
                        ref speakerGeneration);
                    if (!device.EndBluetoothSpeakerGeneration(
                            speakerSessionId, finalGeneration))
                    {
                        // Session validation makes this harmless if a newer
                        // passthrough instance has already replaced us. For the
                        // current session it also commits pending microphone
                        // state when no speaker report claimed the generation.
                        device.ResetBluetoothSpeakerSession(speakerSessionId);
                    }
                }
                catch
                {
                }

                Volatile.Write(ref pacerLifecycleGateSource, 0);
                captureFramesAvailable.Set();
            }
        }

        private void StreamLoop()
        {
            timeBeginPeriod(1);
            using MultimediaThreadRegistration mmcss =
                MultimediaThreadRegistration.EnterProAudio();
            Volatile.Write(ref mmcssRegistered, mmcss.IsActive ? 1 : 0);
            Volatile.Write(ref mmcssHighPriority,
                mmcss.IsActive && mmcss.Error == 0 ? 1 : 0);
            Volatile.Write(ref mmcssRegistrationError, mmcss.Error);
            IntPtr highResolutionTimer = CreateHighResolutionTimer();
            long nextTick = Stopwatch.GetTimestamp();
            try
            {
                nextTick = Stopwatch.GetTimestamp();
                while (!stopping)
                {
                    bool recoveryRequired =
                        device.BluetoothAudioPacerRecoveryRequired;
                    if (recoveryRequired)
                    {
                        if (!directPcmRecoveryWindowInvalidated)
                        {
                            ResetDirectPcmBalanceWindow();
                            directPcmRecoveryWindowInvalidated = true;
                        }
                        // A replacement helper must receive the same six valid
                        // Opus warmup reports before retained content. The
                        // source gate below keeps both the ring and any already
                        // encoded content intact throughout ownership recovery.
                        Volatile.Write(ref startupWarmupFramesRemaining,
                            StartupWarmupReportsForCurrentSource);
                        RequestPacerLifecyclePreparation(recovery: true,
                            gateSource: true);
                    }

                    bool lifecycleGateActive = Volatile.Read(
                        ref pacerLifecycleGateSource) != 0 ||
                        device.BluetoothAudioLifecycleTransitioning;
                    if (lifecycleGateActive)
                    {
                        if (!directPcmRecoveryWindowInvalidated)
                        {
                            ResetDirectPcmBalanceWindow();
                            directPcmRecoveryWindowInvalidated = true;
                        }
                        // Do not consume, resample, process, or encode while HID
                        // ownership is moving. Resetting the local deadline also
                        // prevents the recovery duration from being counted as a
                        // real-time schedule miss when the gate opens.
                        nextTick = Stopwatch.GetTimestamp();
                        captureFramesAvailable.WaitOne(2);
                        continue;
                    }

                    directPcmRecoveryWindowInvalidated = false;

                    // The helper owns physical presentation. Native V5
                    // V5 retains its newest eight complete source generations
                    // independently; after a host stall, admit that bounded
                    // backlog immediately into the strict writer just as the
                    // reference does. Legacy capture sources preserve their
                    // existing one-report stop-and-wait policy.
                    if (ShouldBackpressurePacerProducer(
                        device.BluetoothAudioPacerActive,
                        device.PendingBluetoothSpeakerFrames,
                        directSpeakerUsesV5Source,
                        device.BluetoothAudioPacerPresentedReports))
                    {
                        nextTick = Stopwatch.GetTimestamp();
                        captureFramesAvailable.WaitOne(1);
                        continue;
                    }

                    bool captureReady =
                        HasEnoughBufferedCaptureForNextFrame();
                    bool sourceRecentlyActive = HasRecentSourcePcm(
                        Stopwatch.GetTimestamp(),
                        TransientCaptureShortageLeaseMs);
                    if (!pendingEncodedFrame && !captureReady &&
                        sourceRecentlyActive)
                    {
                        bool sourcePrimePending =
                            IsV5SourcePrimePending();
                        if (ShouldMaintainIdleCarrierDuringV5Prime(
                            directSpeakerUsesV5Source,
                            sourcePrimePending, captureReady,
                            sourceRecentlyActive))
                        {
                            // The physical lane is already armed with valid
                            // timer-paced Opus silence. Keep that carrier
                            // continuous while the first eight real V5
                            // blocks accumulate; the first complete source
                            // frame replaces it atomically on the next pass.
                            // Stopping here previously opened a seven-interval
                            // host hole and drained the controller reserve.
                            Array.Clear(frame, 0, frame.Length);
                            bool submittedPrimeCarrier = EncodeCurrentFrame() &&
                                SendEncodedFrame();
                            if (submittedPrimeCarrier)
                            {
                                Interlocked.Increment(ref framesSent);
                                Interlocked.Increment(ref silentFramesSent);
                            }
                            else if (device.BluetoothAudioPacerRecoveryRequired ||
                                device.BluetoothAudioLifecycleTransitioning)
                            {
                                RequestPacerLifecyclePreparation(
                                    recovery: true, gateSource: true);
                            }

                            ScheduleNextStreamTick(submittedPrimeCarrier,
                                ref nextTick, highResolutionTimer);
                            continue;
                        }

                        // V5 does not advance an independent producer
                        // deadline while a real source block is incomplete.
                        // The source callback wakes this worker as soon as the
                        // stateful 512-ish -> 480 conversion can complete.
                        nextTick = Stopwatch.GetTimestamp();
                        captureFramesAvailable.WaitOne(2);
                        continue;
                    }

                    int warmupReportsRemaining = Volatile.Read(
                        ref startupWarmupFramesRemaining);
                    if (ShouldEmitStartupWarmup(warmupReportsRemaining,
                        lifecycleGateActive, recoveryRequired))
                    {
                        // Encode real fixed-size CBR Opus silence with the same
                        // encoder and 0x36 combined report path as content. No
                        // source frame is removed until all eight reports have
                        // been accepted by the transport. V5 submits this
                        // one-time prime into its OVERLAPPED FIFO without a
                        // user-mode cadence wait; HidBth schedules the radio and
                        // the controller starts with roughly eight reports of
                        // media reserve.
                        Array.Clear(frame, 0, frame.Length);
                        bool submittedWarmup = EncodeCurrentFrame() &&
                            SendEncodedFrame();
                        if (submittedWarmup)
                        {
                            Interlocked.Decrement(
                                ref startupWarmupFramesRemaining);
                            Interlocked.Increment(
                                ref startupWarmupReportsSent);
                            Interlocked.Increment(ref framesSent);
                            Interlocked.Increment(ref silentFramesSent);
                        }
                        else if (device.BluetoothAudioPacerRecoveryRequired ||
                            device.BluetoothAudioLifecycleTransitioning)
                        {
                            RequestPacerLifecyclePreparation(recovery: true,
                                gateSource: true);
                        }

                        if (submittedWarmup)
                        {
                            // Continue immediately so the helper receives the
                            // whole one-time prime. The helper's eight-report
                            // gate then admits it to the strict writer exactly
                            // as V5 does.
                            nextTick = Stopwatch.GetTimestamp();
                        }
                        else
                        {
                            // A rejected prime report remains the current
                            // generation. Avoid a retry spin while ownership or
                            // a writer slot is recovering.
                            captureFramesAvailable.WaitOne(1);
                        }
                        continue;
                    }

                    if (!captureReady && !sourceRecentlyActive)
                    {
                        if (!TryConfirmInactiveV5Source())
                        {
                            // A producer callback won the idle-boundary race.
                            // Re-evaluate readiness without sending an unrelated
                            // carrier or discarding the new source generation.
                            nextTick = Stopwatch.GetTimestamp();
                            continue;
                        }

                        // Preserve a fully armed controller-side stream while
                        // Windows is silent. This is real fixed-size CBR Opus
                        // silence over the same atomic 0x36 path; no source
                        // audio is consumed or delayed. It prevents the first
                        // audible frame from re-triggering Bluetooth startup
                        // credit stalls and retains the measured reserve.
                        Array.Clear(frame, 0, frame.Length);
                        bool submittedIdleCarrier = EncodeCurrentFrame() &&
                            SendEncodedFrame();
                        if (submittedIdleCarrier)
                        {
                            Interlocked.Increment(ref framesSent);
                            Interlocked.Increment(ref silentFramesSent);
                        }
                        else if (device.BluetoothAudioPacerRecoveryRequired ||
                            device.BluetoothAudioLifecycleTransitioning)
                        {
                            Volatile.Write(ref startupWarmupFramesRemaining,
                                StartupWarmupReportsForCurrentSource);
                            RequestPacerLifecyclePreparation(recovery: true,
                                gateSource: true);
                        }

                        ScheduleNextStreamTick(submittedIdleCarrier,
                            ref nextTick, highResolutionTimer);
                        continue;
                    }

                    if (pendingEncodedFrame)
                    {
                        bool submittedPending = SendEncodedFrame();
                        if (submittedPending)
                        {
                            pendingEncodedFrame = false;
                            Interlocked.Increment(ref framesSent);
                            if (!pendingEncodedFrameWasAudible)
                            {
                                Interlocked.Increment(ref silentFramesSent);
                            }
                        }
                        else if (device.BluetoothAudioPacerRecoveryRequired ||
                            device.BluetoothAudioLifecycleTransitioning)
                        {
                            Volatile.Write(ref startupWarmupFramesRemaining,
                                StartupWarmupReportsForCurrentSource);
                            RequestPacerLifecyclePreparation(recovery: true,
                                gateSource: true);
                        }

                        // A retained source frame is retried without consuming
                        // more PCM and without inventing a 10.667 ms media
                        // deadline. Successful submission immediately drains
                        // the next complete source block; rejection polls the
                        // bounded helper queue briefly.
                        nextTick = Stopwatch.GetTimestamp();
                        if (!submittedPending)
                        {
                            captureFramesAvailable.WaitOne(1);
                        }
                        continue;
                    }

                    Array.Clear(frame, 0, frame.Length);
                    bool wasAudioSegmentActive = audioSegmentActive;
                    bool captured = TryFillOutputFrame(out _);
                    bool captureUnderrun = !captured;
                    if (captureUnderrun)
                    {
                        Interlocked.Increment(ref captureUnderruns);
                        if (wasAudioSegmentActive)
                        {
                            Interlocked.Increment(ref activeCaptureUnderruns);
                            ResetDirectPcmBalanceWindow();
                        }
                    }

                    long frameTimestamp = Stopwatch.GetTimestamp();
                    bool rawAudioIsFresh = HasRecentSourcePcm(frameTimestamp,
                        TransientCaptureShortageLeaseMs);
                    if (ShouldDeferTransientCaptureShortage(captureUnderrun,
                            wasAudioSegmentActive, rawAudioIsFresh))
                    {
                        // The isolated writer reservoir already contains the
                        // next physical reports. Do not replace one of them
                        // with an invented zero frame just because the drift
                        // resampler is waiting for the next callback block.
                        // CombinedReportReference only hands a paired report to L2CAP after
                        // the speaker queue contains the next two frames; when
                        // our Windows source is one fractional frame short,
                        // wait for that source edge instead of sleeping an
                        // entire 10.667 ms radio cadence and creating an
                        // audible amplitude notch.
                        Interlocked.Increment(
                            ref transientCaptureDeferrals);
                        captureFramesAvailable.WaitOne(1);
                        nextTick = Stopwatch.GetTimestamp();
                        LogStreamDiagnosticsIfVerbose();
                        continue;
                    }

                    bool audible = captured &&
                        HasAudibleSamples(frame, frame.Length);
                    if (audible)
                    {
                        lastAudibleTimestamp = Stopwatch.GetTimestamp();
                        if (!audioSegmentActive)
                        {
                            audioSegmentActive = true;
                            Interlocked.Increment(ref audioSegmentStarts);
                        }
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

                    speakerProcessor.Process(frame, FrameSamples);

                    // The combined report carries firmware speaker volume.
                    // Applying the profile gain to PCM too would attenuate the
                    // stream twice.
                    const float volume = 1.0f;
                    for (int i = 0; i < frame.Length; i++)
                    {
                        frame[i] = Math.Clamp(frame[i] * volume, -1.0f, 1.0f);
                    }

                    int tailOffset = (FrameSamples - 1) * Channels;
                    previousOutputLeft = frame[tailOffset];
                    previousOutputRight = frame[tailOffset + 1];

                    bool submittedFrameThisTick = false;
                    if (captured)
                    {
                        // Every complete source block is media, including
                        // digital silence. V5 drains it without an
                        // amplitude gate or a second presentation clock.
                        preOpusPcmTrace?.Write(frame, frame.Length);
                        if (EncodeCurrentFrame())
                        {
                            submittedFrameThisTick = SendEncodedFrame();
                            if (submittedFrameThisTick)
                            {
                                Interlocked.Increment(ref framesSent);
                                if (!audible)
                                {
                                    // This is a real CBR Opus silence frame
                                    // inside the shared 0x36 stream, not an
                                    // empty speaker TLV.
                                    Interlocked.Increment(ref silentFramesSent);
                                }
                            }
                            else
                            {
                                // The exact encoded packet survives queue
                                // backpressure or a helper fault. No additional
                                // source is drained until it is accepted.
                                pendingEncodedFrame = true;
                                pendingEncodedFrameWasAudible = audible;
                                if (device.BluetoothAudioPacerRecoveryRequired ||
                                    device.BluetoothAudioLifecycleTransitioning)
                                {
                                    Volatile.Write(
                                        ref startupWarmupFramesRemaining,
                                        StartupWarmupReportsForCurrentSource);
                                    RequestPacerLifecyclePreparation(
                                        recovery: true, gateSource: true);
                                }
                            }
                        }
                    }
                    else if (audioSegmentActive)
                    {
                        // Transition directly from source silence to the idle
                        // carrier without an empty 0x36 cadence slot. The
                        // helper/session remains alive so the next segment can
                        // replace this frame immediately.
                        Array.Clear(frame, 0, frame.Length);
                        bool submittedIdleCarrier = EncodeCurrentFrame() &&
                            SendEncodedFrame();
                        if (submittedIdleCarrier)
                        {
                            submittedFrameThisTick = true;
                            Interlocked.Increment(ref framesSent);
                            Interlocked.Increment(ref silentFramesSent);
                        }
                        audioSegmentActive = false;
                        Volatile.Write(
                            ref pacerPrewarmAttemptedForSegment, 0);
                        Interlocked.Exchange(
                            ref lastRawAudibleTimestamp, 0);
                        Interlocked.Exchange(
                            ref lastSourcePcmTimestamp, 0);
                        ResetCapturePipelineAtSegmentBoundary();
                        Interlocked.Increment(ref audioSegmentStops);
                    }

                    if (captured &&
                        (submittedFrameThisTick || pendingEncodedFrame))
                    {
                        // Real PCM follows source completion exactly as in
                        // V5. Drain every complete resampler block now;
                        // only no-source/warmup silence uses the host timer.
                        nextTick = Stopwatch.GetTimestamp();
                        if (pendingEncodedFrame)
                        {
                            captureFramesAvailable.WaitOne(1);
                        }
                        LogStreamDiagnosticsIfVerbose();
                    }
                    else
                    {
                        ScheduleNextStreamTick(submittedFrameThisTick,
                            ref nextTick, highResolutionTimer);
                    }
                }
            }
            finally
            {
                // The lifecycle worker owns the tokenized Clear -> pending mic
                // commit boundary. Never perform it on this sole real-time
                // speaker thread.
                RequestGenerationEnd(Interlocked.Read(ref speakerGeneration));
                if (highResolutionTimer != IntPtr.Zero)
                {
                    CloseHandle(highResolutionTimer);
                }

                timeEndPeriod(1);
            }
        }

        private void ScheduleNextStreamTick(bool submittedFrameThisTick,
            ref long nextTick, IntPtr highResolutionTimer)
        {
            long cadenceTicks = CalculateBluetoothSpeakerCadenceTicks(
                Stopwatch.Frequency,
                device.DualSenseBluetoothPresentationClockRatio);
            nextTick += cadenceTicks;
            long nowTicks = Stopwatch.GetTimestamp();
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
                return;
            }

            LogStreamDiagnosticsIfVerbose();
            WaitUntil(highResolutionTimer, nextTick);
        }

        internal static long CalculateBluetoothSpeakerCadenceTicks(
            long clockFrequency, double presentationClockRatio)
        {
            if (clockFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clockFrequency));
            }

            double ratio = double.IsFinite(presentationClockRatio) ?
                Math.Clamp(presentationClockRatio,
                    DualSenseBluetoothAudioPacerScheduler.MinimumRateRatio,
                    DualSenseBluetoothAudioPacerScheduler.MaximumRateRatio) :
                1.0;
            return Math.Max(1, (long)Math.Round(
                BluetoothSpeakerCadenceMs * clockFrequency /
                (1000.0 * ratio)));
        }

        private void LogStreamDiagnosticsIfVerbose()
        {
            // This snapshot is intentionally large. Building it while the
            // controller owns a live media segment can force a managed GC and
            // starve the 10.667 ms source/presentation chain long enough to be
            // audible. Keep collecting the lock-free counters, but defer their
            // formatted GUI snapshot until the media segment is idle.
            if (!Global.VerboseStartupLogging || audioSegmentActive)
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
                    // Playback can resume between queuing this work item and
                    // its execution. Never let delayed diagnostics become a
                    // live-stream observer effect.
                    if (audioSegmentActive)
                    {
                        return;
                    }

                    double directBalanceErrorPpm = 0.0;
                    double directBalanceTargetPpm = 0.0;
                    double directBalanceAppliedPpm = 0.0;
                    int directBalanceWindows = 0;
                    int directBalanceRejectedWindows = 0;
                    int directBalanceResetWindows = 0;
                    lock (syncRoot)
                    {
                        if (directPcmBalanceClockServo != null)
                        {
                            directBalanceErrorPpm =
                                directPcmBalanceClockServo.
                                    LastMeasuredErrorPpm;
                            directBalanceTargetPpm =
                                directPcmBalanceClockServo.TargetTrimPpm;
                            directBalanceAppliedPpm =
                                directPcmBalanceClockServo.AppliedTrimPpm;
                            directBalanceWindows =
                                directPcmBalanceClockServo.CompletedWindows;
                            directBalanceRejectedWindows =
                                directPcmBalanceClockServo.RejectedWindows;
                            directBalanceResetWindows =
                                directPcmBalanceClockServo.ResetWindows;
                        }
                    }

                    AppLogger.LogToGui(
                        $"DualSense Bluetooth combined stream stats: frames={Interlocked.Read(ref framesSent)} " +
                        $"silentFrames={Interlocked.Read(ref silentFramesSent)} " +
                        $"scheduleMisses={Interlocked.Read(ref skippedScheduleSlots)} " +
                        $"maximumScheduleLatenessMs={StopwatchTicksToMilliseconds(Interlocked.Read(ref maximumScheduleLatenessTicks)):F1} " +
                        $"activeScheduleMisses={Interlocked.Read(ref activeScheduleMisses)} " +
                        $"maximumActiveScheduleLatenessMs={StopwatchTicksToMilliseconds(Interlocked.Read(ref maximumActiveScheduleLatenessTicks)):F1} " +
                        $"segmentStarts={Interlocked.Read(ref audioSegmentStarts)} " +
                        $"segmentStops={Interlocked.Read(ref audioSegmentStops)} " +
                        $"captureCallbacks={Interlocked.Read(ref captureCallbackCount)} " +
                        $"captureInputFrames={Interlocked.Read(ref captureInputFrames)} " +
                        $"captureBufferedFrames={GetCaptureBufferedFrames()} " +
                        $"capturePrimed={IsCapturePrimed()} " +
                         $"captureUnderruns={Interlocked.Read(ref captureUnderruns)} " +
                         $"activeCaptureUnderruns={Interlocked.Read(ref activeCaptureUnderruns)} " +
                         $"transientCaptureDeferrals={Interlocked.Read(ref transientCaptureDeferrals)} " +
                         $"captureOverflowFrames={Interlocked.Read(ref captureOverflowFrames)} " +
                         $"driftAdjustments={Interlocked.Read(ref captureDriftAdjustments)} " +
                         $"clockMode={(directSpeakerSource != null ? "controller-locked" : "adaptive-controller-locked")} " +
                         $"clockRatio={GetCaptureCurrentClockRatio():F7}/" +
                         $"{GetCaptureTargetClockRatio():F7} " +
                         $"controllerClock={device.DualSenseControllerClockRatio:F7} " +
                         $"controllerClockWindows={device.DualSenseControllerClockCompletedWindows} " +
                         $"controllerClockStable={device.DualSenseControllerClockStable} " +
                         $"mediaClock={device.DualSenseMediaBufferCadenceRatio:F7} " +
                         $"presentationClock={device.DualSenseBluetoothPresentationClockRatio:F7} " +
                         $"sourceClock={directSourceClockEstimator?.Ratio ?? 1.0:F7} " +
                         $"sourceClockWindows={directSourceClockEstimator?.CompletedWindows ?? 0} " +
                         $"sourceClockStable={directSourceClockEstimator?.IsStable ?? false} " +
                         $"smoothedBufferedFrames={GetCaptureSmoothedBufferedFrames():F1} " +
                         $"directPcmCallbacks={Interlocked.Read(ref directPcmCallbacks)} " +
                         $"directPcmFrames={Interlocked.Read(ref directPcmInputFrames)}/" +
                         $"{Interlocked.Read(ref directPcmOutputFrames)} " +
                         $"directPcmConsumedFrames={Interlocked.Read(ref directPcmConsumedFrames)} " +
                         $"directBalanceErrorPpm={directBalanceErrorPpm:F2} " +
                         $"directBalanceTargetPpm={directBalanceTargetPpm:F2} " +
                         $"directBalanceAppliedPpm={directBalanceAppliedPpm:F2} " +
                         $"directBalanceWindows={directBalanceWindows} " +
                         $"directBalanceRejects={directBalanceRejectedWindows} " +
                         $"directBalanceResets={directBalanceResetWindows} " +
                         $"directPcmCallbackGapMaxMs={StopwatchTicksToMilliseconds(Interlocked.Read(ref directPcmMaximumCallbackGapTicks)):F1} " +
                         $"directPcmCallbackLockWaitMaxMs={StopwatchTicksToMilliseconds(Interlocked.Read(ref directPcmMaximumCallbackLockWaitTicks)):F1} " +
                         $"pacerPrewarm={Interlocked.Read(ref pacerPrewarmAttempts)}/" +
                         $"{Interlocked.Read(ref pacerPrewarmSuccesses)}/" +
                         $"{Interlocked.Read(ref pacerPrewarmFailures)} " +
                         $"pacerPrewarmMaxMs={StopwatchTicksToMilliseconds(Interlocked.Read(ref pacerPrewarmMaximumTicks)):F1} " +
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
                         $"isolatedPacer={device.BluetoothAudioPacerActive} " +
                         $"pacerPresented={device.BluetoothAudioPacerPresentedReports} " +
                         $"pacerLate={device.BluetoothAudioPacerLatePresentations} " +
                         $"pacerGapMaxMs={device.BluetoothAudioPacerMaximumPresentationGapMilliseconds:F2} " +
                         $"pacerRejected={device.BluetoothAudioPacerRejectedReports} " +
                         $"inFlightWaits={device.BluetoothAudioPacerInFlightLimitWaits} " +
                         $"inFlightEscapes={device.BluetoothAudioPacerInFlightLimitEscapes} " +
                         $"inFlightWaitMaxMs={device.BluetoothAudioPacerMaximumInFlightLimitWaitMilliseconds:F2} " +
                         $"inFlightSubmitMax={device.BluetoothAudioPacerMaximumAudioPendingBeforeSubmission} " +
                         $"inFlightSubmitShallow={device.BluetoothAudioPacerShallowAudioSubmissions} " +
                         $"inFlightSubmitFull={device.BluetoothAudioPacerFullAudioSubmissions} " +
                         $"helperWriteCompletions={device.BluetoothAudioPacerCompletedWrites} " +
                         $"helperSlowCompletions={device.BluetoothAudioPacerSlowCompletions} " +
                         $"helperCompletionMaxMs={device.BluetoothAudioPacerMaximumCompletionMilliseconds:F2} " +
                         $"helperLateSubmissions={device.BluetoothAudioPacerLateSubmissions} " +
                         $"helperSubmissionGapMaxMs={device.BluetoothAudioPacerMaximumSubmissionGapMilliseconds:F2} " +
                         $"helperSlowNativeSubmissions={device.BluetoothAudioPacerSlowNativeSubmissions} " +
                         $"helperNativeSubmissionMaxMs={device.BluetoothAudioPacerMaximumNativeSubmissionMilliseconds:F2} " +
                         $"pacerError='{device.BluetoothAudioPacerLastError}' " +
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

        private double GetCaptureCurrentClockRatio()
        {
            lock (syncRoot)
            {
                return captureCurrentClockRatio;
            }
        }

        private double GetCaptureTargetClockRatio()
        {
            lock (syncRoot)
            {
                return captureTargetClockRatio;
            }
        }

        private double GetCaptureSmoothedBufferedFrames()
        {
            lock (syncRoot)
            {
                return captureSmoothedBufferedFrames;
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

        private static double StopwatchTicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static void WaitUntil(IntPtr highResolutionTimer,
            long targetStopwatchTicks)
        {
            double waitMs = (targetStopwatchTicks - Stopwatch.GetTimestamp()) *
                1000.0 / Stopwatch.Frequency;
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

        private bool EncodeCurrentFrame()
        {
            if (stopping)
            {
                return false;
            }

            int encoded;
            try
            {
                encoded = opusEncoder.Encode(frame.AsSpan(), FrameSamples,
                    opusFrame.AsSpan(), OpusBytes);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref loggedWriteFailure, 1) == 0)
                {
                    AppLogger.LogToGui($"DualSense Bluetooth speaker encoder failed: {ex.Message}", true);
                }

                return false;
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

                return false;
            }

            if (traceOpusDecoder != null && postOpusPcmTrace != null)
            {
                try
                {
                    int decodedFrames = traceOpusDecoder.Decode(
                        opusFrame.AsSpan(), traceDecodedFrame.AsSpan(),
                        FrameSamples, false);
                    if (decodedFrames > 0)
                    {
                        postOpusPcmTrace.Write(traceDecodedFrame,
                            decodedFrames * Channels);
                    }
                }
                catch
                {
                    // Diagnostic decoding must never affect presentation.
                }
            }

            return true;
        }

        private static bool HasAudiblePcm16(byte[] pcm, int offset, int length)
        {
            if (pcm == null || offset < 0 || length <= 0 ||
                offset + length > pcm.Length)
            {
                return false;
            }

            int end = offset + (length & ~1);
            for (int index = offset; index < end; index += sizeof(short))
            {
                short sample = (short)(pcm[index] | (pcm[index + 1] << 8));
                if (sample > 4 || sample < -4)
                {
                    return true;
                }
            }

            return false;
        }

        private bool SendEncodedFrame()
        {
            try
            {
                bool accepted = !stopping &&
                    device.SetBluetoothSpeakerAudioFrame(opusFrame, OpusBytes,
                        speakerSessionId,
                        Interlocked.Read(ref speakerGeneration));
                if (!accepted && !stopping)
                {
                    ResetDirectPcmBalanceWindow();
                }

                return accepted;
            }
            catch (Exception ex)
            {
                ResetDirectPcmBalanceWindow();
                if (Interlocked.Exchange(ref loggedWriteFailure, 1) == 0)
                {
                    AppLogger.LogToGui(
                        $"DualSense Bluetooth speaker transport failed: {ex.GetType().Name}: {ex.Message}",
                        true);
                }

                return false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            {
                return;
            }

            stopping = true;
            if (directSpeakerSource != null)
            {
                directSpeakerSource.VirtualSpeakerPcmReceived -=
                    DirectSpeakerSource_VirtualSpeakerPcmReceived;
                directSpeakerSource.VirtualAtomicAudioHapticsReceived -=
                    DirectSpeakerSource_VirtualAtomicAudioHapticsReceived;
                // Unsubscription does not cancel a delegate already in
                // flight. This is the drain barrier for the reused VIIPER PCM
                // buffer and the stateful converter.
                lock (directPcmSync)
                {
                }
            }

            IWaveIn oldCapture;
            lock (syncRoot)
            {
                oldCapture = capture;
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
            }

            // The RT worker's finally and the lifecycle worker's finally both
            // preserve this boundary. Signalling before requesting lifecycle
            // shutdown also gives the worker an opportunity to perform the
            // tokenized End -> pending microphone commit normally.
            RequestGenerationEnd(Interlocked.Read(ref speakerGeneration));
            Volatile.Write(ref pacerLifecycleStopping, 1);
            captureDataAvailable.Set();
            captureFramesAvailable.Set();
            pacerLifecycleRequested.Set();

            Thread oldWorker = worker;
            Thread oldCapturePump = capturePump;
            Thread oldLifecycleWorker = pacerLifecycleWorker;
            bool workerStopped = JoinThreadWithin(oldWorker, 2000);
            bool capturePumpStopped = JoinThreadWithin(oldCapturePump, 2000);
            bool lifecycleWorkerStopped = JoinThreadWithin(
                oldLifecycleWorker, 2000);

            if (ShouldDeferDisposeCleanup(!workerStopped,
                !capturePumpStopped, !lifecycleWorkerStopped))
            {
                ScheduleDeferredDisposeCleanup(oldWorker, oldCapturePump,
                    oldLifecycleWorker);
                return;
            }

            FinalizeDisposeResources();
        }

        private static bool JoinThreadWithin(Thread thread,
            int millisecondsTimeout)
        {
            if (thread == null || !thread.IsAlive)
            {
                return true;
            }
            if (Thread.CurrentThread.ManagedThreadId == thread.ManagedThreadId)
            {
                return false;
            }

            try
            {
                return thread.Join(millisecondsTimeout) || !thread.IsAlive;
            }
            catch (ThreadStateException)
            {
                return !thread.IsAlive;
            }
        }

        private void ScheduleDeferredDisposeCleanup(Thread oldWorker,
            Thread oldCapturePump, Thread oldLifecycleWorker)
        {
            if (Interlocked.CompareExchange(
                    ref deferredDisposeCleanupScheduled, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                JoinThreadToExit(oldWorker);
                JoinThreadToExit(oldCapturePump);
                JoinThreadToExit(oldLifecycleWorker);
                FinalizeDisposeResources();
            });
        }

        private static void JoinThreadToExit(Thread thread)
        {
            if (thread == null || !thread.IsAlive ||
                Thread.CurrentThread.ManagedThreadId == thread.ManagedThreadId)
            {
                return;
            }

            try
            {
                thread.Join();
            }
            catch (ThreadStateException)
            {
            }
        }

        private void FinalizeDisposeResources()
        {
            if (Interlocked.CompareExchange(
                    ref disposeCleanupCompleted, 1, 0) != 0)
            {
                return;
            }

            long finalGeneration = Interlocked.Read(ref speakerGeneration);
            if (!device.EndBluetoothSpeakerGeneration(speakerSessionId,
                    finalGeneration))
            {
                device.ResetBluetoothSpeakerSession(speakerSessionId);
            }

            IWaveIn oldCapture;
            lock (syncRoot)
            {
                oldCapture = capture;
                capture = null;
                captureBuffer = null;
                capturePrimed = false;
                ResetCaptureClockLocked(resetSourceClock: true);
                directPcmBalanceClockServo?.ResetLifecycle();
                directPcmRateConverter?.Reset();
                ResetSpeakerResamplingLocked();
            }

            oldCapture?.Dispose();
            worker = null;
            capturePump = null;
            pacerLifecycleWorker = null;
            captureDataAvailable.Dispose();
            captureFramesAvailable.Dispose();
            pacerLifecycleRequested.Dispose();
            rawDirectPcmTrace?.Dispose();
            preOpusPcmTrace?.Dispose();
            postOpusPcmTrace?.Dispose();
            traceOpusDecoder?.Dispose();
            rawDirectPcmTrace = null;
            preOpusPcmTrace = null;
            postOpusPcmTrace = null;
            traceOpusDecoder = null;
        }
    }
}
