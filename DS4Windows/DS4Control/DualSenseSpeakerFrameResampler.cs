using NAudio.Dsp;
using System;

namespace DS4Windows
{
    /// <summary>
    /// Streaming resampler for the physical DualSense speaker clock.
    ///
    /// The controller consumes 480 stereo samples in each 10 ms Opus packet,
    /// while the reference transport pulls 512 samples from the nominal 48 kHz
    /// render stream. Treating those 512 samples as 51.2 kHz reproduces the
    /// reference 512-to-480 stage without periodically deleting a sample or
    /// resetting interpolation state at packet boundaries.
    /// </summary>
    internal sealed class DualSenseSpeakerFrameResampler
    {
        internal const int Channels = 2;
        internal const int NominalInputFrames = 512;
        internal const int OutputFrames = 480;
        internal const double NominalInputRate = 51200.0;
        internal const double OutputRate = 48000.0;
        internal const double MinimumInputRateRatio = 0.99;
        internal const double MaximumInputRateRatio = 1.01;
        // WDL retains a small interpolation look-ahead. At the maximum
        // permitted correction, 480 output frames require at most 518 new
        // source frames plus that look-ahead. Keep the bound explicit so the
        // realtime caller never allocates or accepts an unbounded request.
        internal const int MaximumInputFrames = 522;

        private readonly WdlResampler resampler;
        private double inputRateRatio = 1.0;
        private float[] preparedInputBuffer;
        private int preparedInputBufferOffset;
        private int preparedInputFrames;
        private int preparedOutputFrames;

        internal DualSenseSpeakerFrameResampler()
        {
            resampler = new WdlResampler();
            resampler.SetMode(true, 0, false);
            // Output-driven operation is essential here. A feed-driven fixed
            // 512-frame block occasionally produces only 479 frames under a
            // fractional clock correction, which made the caller pull a
            // second full block and silently added one 10.7 ms packet of
            // latency. WDL now retains only its interpolation look-ahead and
            // tells the caller whether this packet needs 512 or 513 frames.
            resampler.SetFeedMode(false);
            resampler.SetRates(NominalInputRate, OutputRate);
        }

        internal double InputRateRatio => inputRateRatio;

        /// <summary>
        /// Updates the small clock correction without resetting fractional
        /// position. The caller should slew this value (the existing speaker
        /// servo is +/-0.1%) rather than make large instantaneous changes.
        /// </summary>
        internal void SetInputRateRatio(double ratio)
        {
            if (!double.IsFinite(ratio) ||
                ratio < MinimumInputRateRatio ||
                ratio > MaximumInputRateRatio)
            {
                throw new ArgumentOutOfRangeException(nameof(ratio));
            }

            if (ratio == inputRateRatio)
            {
                return;
            }

            inputRateRatio = ratio;
            resampler.SetRates(NominalInputRate * ratio, OutputRate);
        }

        /// <summary>
        /// Prepares one fixed-size output packet and returns the exact number
        /// of source frames WDL requires. It is safe to call this again if the
        /// source ring does not yet contain the returned count.
        /// </summary>
        internal int PrepareOutputFrame(int outputFrames = OutputFrames)
        {
            if (outputFrames <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(outputFrames));
            }

            int requested = resampler.ResamplePrepare(outputFrames, Channels,
                out preparedInputBuffer, out preparedInputBufferOffset);
            if (requested < 0 || requested > MaximumInputFrames)
            {
                throw new InvalidOperationException(
                    $"WDL requested {requested} source frames for " +
                    $"{outputFrames} DualSense output frames.");
            }

            preparedInputFrames = requested;
            preparedOutputFrames = outputFrames;
            return requested;
        }

        /// <summary>
        /// Supplies the exact source request returned by PrepareOutputFrame
        /// and always emits the requested fixed-size output packet. Offsets
        /// are interleaved-float sample offsets.
        /// </summary>
        internal int ConvertPreparedOutput(float[] source,
            int sourceSampleOffset, int sourceFrames, float[] destination,
            int destinationSampleOffset)
        {
            if (preparedInputBuffer == null ||
                sourceFrames != preparedInputFrames ||
                preparedOutputFrames <= 0)
            {
                throw new InvalidOperationException(
                    "PrepareOutputFrame must immediately precede conversion " +
                    "with its exact source-frame request.");
            }

            ValidateFloatRange(source, sourceSampleOffset, sourceFrames,
                nameof(source));
            ValidateFloatRange(destination, destinationSampleOffset,
                preparedOutputFrames, nameof(destination));

            Array.Copy(source, sourceSampleOffset, preparedInputBuffer,
                preparedInputBufferOffset, sourceFrames * Channels);
            int expectedOutputFrames = preparedOutputFrames;
            int produced = resampler.ResampleOut(destination,
                destinationSampleOffset, sourceFrames,
                expectedOutputFrames, Channels);
            preparedInputBuffer = null;
            preparedInputBufferOffset = 0;
            preparedInputFrames = 0;
            preparedOutputFrames = 0;
            if (produced != expectedOutputFrames)
            {
                throw new InvalidOperationException(
                    $"WDL produced {produced} frames instead of the " +
                    $"prepared {expectedOutputFrames} frames.");
            }

            return produced;
        }

        internal void Reset()
        {
            resampler.Reset();
            preparedInputBuffer = null;
            preparedInputBufferOffset = 0;
            preparedInputFrames = 0;
            preparedOutputFrames = 0;
        }

        private static void ValidateFloatRange(float[] buffer,
            int sampleOffset, int frames, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(buffer, parameterName);
            if (sampleOffset < 0 || frames < 0 ||
                sampleOffset > buffer.Length ||
                frames > (buffer.Length - sampleOffset) / Channels)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

    }

    /// <summary>
    /// Applies only the small physical-controller clock correction to the
    /// PadSense-compatible source produced by VIIPER V5. That source has
    /// already undergone the continuous 48-to-45 kHz conversion and arrives
    /// as 480-frame media generations, so the legacy fixed 512-to-480 stage
    /// must not run again.
    /// </summary>
    internal sealed class DualSensePadSenseSpeakerClockResampler
    {
        internal const int Channels = 2;
        internal const int OutputFrames = 480;
        internal const double NominalInputRate = 48000.0;
        internal const double OutputRate = 48000.0;
        internal const double MinimumInputRateRatio = 0.99;
        internal const double MaximumInputRateRatio = 1.01;
        internal const int MaximumInputFrames = 490;

        private readonly WdlResampler resampler;
        private double inputRateRatio = 1.0;
        private float[] preparedInputBuffer;
        private int preparedInputBufferOffset;
        private int preparedInputFrames;
        private int preparedOutputFrames;

        internal DualSensePadSenseSpeakerClockResampler()
        {
            resampler = new WdlResampler();
            resampler.SetMode(true, 0, false);
            resampler.SetFeedMode(false);
            resampler.SetRates(NominalInputRate, OutputRate);
        }

        internal double InputRateRatio => inputRateRatio;

        internal void SetInputRateRatio(double ratio)
        {
            if (!double.IsFinite(ratio) ||
                ratio < MinimumInputRateRatio ||
                ratio > MaximumInputRateRatio)
            {
                throw new ArgumentOutOfRangeException(nameof(ratio));
            }

            if (ratio == inputRateRatio)
            {
                return;
            }

            inputRateRatio = ratio;
            resampler.SetRates(NominalInputRate * ratio, OutputRate);
        }

        internal int PrepareOutputFrame(int outputFrames = OutputFrames)
        {
            if (outputFrames <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(outputFrames));
            }

            int requested = resampler.ResamplePrepare(outputFrames, Channels,
                out preparedInputBuffer, out preparedInputBufferOffset);
            if (requested < 0 || requested > MaximumInputFrames)
            {
                throw new InvalidOperationException(
                    $"WDL requested {requested} PadSense source frames for " +
                    $"{outputFrames} DualSense output frames.");
            }

            preparedInputFrames = requested;
            preparedOutputFrames = outputFrames;
            return requested;
        }

        internal int ConvertPreparedOutput(float[] source,
            int sourceSampleOffset, int sourceFrames, float[] destination,
            int destinationSampleOffset)
        {
            if (preparedInputBuffer == null ||
                sourceFrames != preparedInputFrames ||
                preparedOutputFrames <= 0)
            {
                throw new InvalidOperationException(
                    "PrepareOutputFrame must immediately precede conversion " +
                    "with its exact source-frame request.");
            }

            ValidateFloatRange(source, sourceSampleOffset, sourceFrames,
                nameof(source));
            ValidateFloatRange(destination, destinationSampleOffset,
                preparedOutputFrames, nameof(destination));

            Array.Copy(source, sourceSampleOffset, preparedInputBuffer,
                preparedInputBufferOffset, sourceFrames * Channels);
            int expectedOutputFrames = preparedOutputFrames;
            int produced = resampler.ResampleOut(destination,
                destinationSampleOffset, sourceFrames,
                expectedOutputFrames, Channels);
            preparedInputBuffer = null;
            preparedInputBufferOffset = 0;
            preparedInputFrames = 0;
            preparedOutputFrames = 0;
            if (produced != expectedOutputFrames)
            {
                throw new InvalidOperationException(
                    $"WDL produced {produced} PadSense frames instead of " +
                    $"the prepared {expectedOutputFrames} frames.");
            }

            return produced;
        }

        internal void Reset()
        {
            resampler.Reset();
            preparedInputBuffer = null;
            preparedInputBufferOffset = 0;
            preparedInputFrames = 0;
            preparedOutputFrames = 0;
        }

        private static void ValidateFloatRange(float[] buffer,
            int sampleOffset, int frames, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(buffer, parameterName);
            if (sampleOffset < 0 || frames < 0 ||
                sampleOffset > buffer.Length ||
                frames > (buffer.Length - sampleOffset) / Channels)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    /// <summary>
    /// DualSense direct-PCM route matching DS5 Bridge's signal stages while
    /// retaining DS4Windows' long-window source/controller clock correction.
    ///
    /// Stage one applies only the small dynamic clock ratio and always emits
    /// exactly 512 intermediate frames. Stage two is the reference feed-driven
    /// linear conversion: a fixed 512-frame block at 51.2 kHz becomes one
    /// 480-frame/10 ms Opus input block at 48 kHz. Keeping correction out of
    /// the feed-driven stage prevents its historical 479-frame fractional-rate
    /// result without allowing long-run endpoint/controller drift to drain the
    /// source ring.
    /// </summary>
    internal sealed class DualSenseReferenceSpeakerFrameResampler
    {
        internal const int Channels = 2;
        internal const int ReferenceInputFrames = 512;
        internal const int OutputFrames = 480;
        internal const double SourceRate = 48000.0;
        internal const double ReferenceInputRate = 51200.0;
        internal const double OutputRate = 48000.0;
        internal const double MinimumInputRateRatio = 0.99;
        internal const double MaximumInputRateRatio = 1.01;
        internal const int MaximumInputFrames = 522;

        // NAudio's WDL wrapper calls Array.Resize whenever the integer source
        // request changes. A clock servo hovering around unity therefore
        // allocated about 4 KB each time it crossed the 511/512 boundary. Keep
        // the same streaming linear-interpolation state in a fixed buffer so a
        // clock correction can never trigger GC on the audio producer.
        private readonly float[] clockInput = new float[
            MaximumInputFrames * Channels];
        private readonly float[] correctedBlock = new float[
            ReferenceInputFrames * Channels];
        private readonly float[] referenceInput = new float[
            MaximumInputFrames * Channels];
        private double inputRateRatio = 1.0;
        private double preparedInputRateRatio = 1.0;
        private double clockFractionalPosition;
        private double referenceFractionalPosition;
        private int clockBufferedFrames;
        private int referenceBufferedFrames;
        private int preparedSourceFrames;

        internal DualSenseReferenceSpeakerFrameResampler()
        {
        }

        internal double InputRateRatio => inputRateRatio;

        internal void SetInputRateRatio(double ratio)
        {
            if (!double.IsFinite(ratio) ||
                ratio < MinimumInputRateRatio ||
                ratio > MaximumInputRateRatio)
            {
                throw new ArgumentOutOfRangeException(nameof(ratio));
            }

            if (ratio == inputRateRatio)
            {
                return;
            }

            // A prepared packet freezes its own ratio. Any newer servo value
            // becomes the desired ratio for the following packet.
            inputRateRatio = ratio;
        }

        /// <summary>
        /// Returns the exact number of source-ring frames needed to create the
        /// next corrected 512-frame reference block.
        /// </summary>
        internal int PrepareOutputFrame()
        {
            if (preparedSourceFrames != 0)
            {
                return preparedSourceFrames;
            }

            // This is WDL's output-driven linear request rule with its four
            // look-ahead frames, but backed by a fixed-capacity buffer.
            int targetBufferedFrames =
                (int)(inputRateRatio * ReferenceInputFrames) + 4;
            int requested = targetBufferedFrames - clockBufferedFrames;
            if (requested < 0 || requested > MaximumInputFrames)
            {
                throw new InvalidOperationException(
                    $"WDL requested {requested} source frames for the " +
                    "DualSense reference block.");
            }

            preparedSourceFrames = requested;
            preparedInputRateRatio = inputRateRatio;
            return requested;
        }

        internal int ConvertPreparedOutput(float[] source,
            int sourceSampleOffset, int sourceFrames, float[] destination,
            int destinationSampleOffset)
        {
            if (preparedSourceFrames == 0 ||
                sourceFrames != preparedSourceFrames)
            {
                throw new InvalidOperationException(
                    "PrepareOutputFrame must immediately precede conversion " +
                    "with its exact source-frame request.");
            }

            ValidateFloatRange(source, sourceSampleOffset, sourceFrames,
                nameof(source));
            ValidateFloatRange(destination, destinationSampleOffset,
                OutputFrames, nameof(destination));

            int availableFrames = clockBufferedFrames + sourceFrames;
            if (availableFrames > MaximumInputFrames)
            {
                throw new InvalidOperationException(
                    $"The clock-correction buffer requires {availableFrames} " +
                    $"frames but is bounded to {MaximumInputFrames}.");
            }

            Array.Copy(source, sourceSampleOffset, clockInput,
                clockBufferedFrames * Channels, sourceFrames * Channels);
            double sourcePosition = clockFractionalPosition;
            for (int outputFrame = 0;
                outputFrame < ReferenceInputFrames; outputFrame++)
            {
                int sourceFrame = (int)sourcePosition;
                if (sourceFrame >= availableFrames - 1)
                {
                    throw new InvalidOperationException(
                        "The prepared clock-correction block did not contain " +
                        "enough interpolation look-ahead.");
                }

                double fraction = sourcePosition - sourceFrame;
                double inverseFraction = 1.0 - fraction;
                int sourceIndex = sourceFrame * Channels;
                int outputIndex = outputFrame * Channels;
                correctedBlock[outputIndex] = (float)(
                    clockInput[sourceIndex] * inverseFraction +
                    clockInput[sourceIndex + Channels] * fraction);
                correctedBlock[outputIndex + 1] = (float)(
                    clockInput[sourceIndex + 1] * inverseFraction +
                    clockInput[sourceIndex + Channels + 1] * fraction);
                sourcePosition += preparedInputRateRatio;
            }

            int consumedFrames = (int)sourcePosition;
            clockFractionalPosition = sourcePosition - consumedFrames;
            clockBufferedFrames = availableFrames - consumedFrames;
            if (clockBufferedFrames > 0)
            {
                Array.Copy(clockInput, consumedFrames * Channels,
                    clockInput, 0, clockBufferedFrames * Channels);
            }

            preparedSourceFrames = 0;
            // Match DS5 Bridge's feed-driven WDL stage with fixed storage. Its
            // nominal ratio is exactly 16:15, but retaining WDL's fractional
            // position and boundary frame also preserves its floating-point
            // continuity indefinitely without any Array.Resize calls.
            int referenceAvailableFrames = referenceBufferedFrames +
                ReferenceInputFrames;
            if (referenceAvailableFrames > MaximumInputFrames)
            {
                throw new InvalidOperationException(
                    "The fixed 512-to-480 reference buffer overflowed.");
            }

            Array.Copy(correctedBlock, 0, referenceInput,
                referenceBufferedFrames * Channels, correctedBlock.Length);
            double referencePosition = referenceFractionalPosition;
            double referenceRatio = ReferenceInputRate / OutputRate;
            for (int outputFrame = 0; outputFrame < OutputFrames; outputFrame++)
            {
                int sourceFrame = (int)referencePosition;
                if (sourceFrame >= referenceAvailableFrames - 1)
                {
                    throw new InvalidOperationException(
                        "The fixed 512-to-480 reference block did not contain " +
                        "enough interpolation look-ahead.");
                }

                double fraction = referencePosition - sourceFrame;
                double inverseFraction = 1.0 - fraction;
                int sourceIndex = sourceFrame * Channels;
                int destinationIndex = destinationSampleOffset +
                    outputFrame * Channels;
                destination[destinationIndex] = (float)(
                    referenceInput[sourceIndex] * inverseFraction +
                    referenceInput[sourceIndex + Channels] * fraction);
                destination[destinationIndex + 1] = (float)(
                    referenceInput[sourceIndex + 1] * inverseFraction +
                    referenceInput[sourceIndex + Channels + 1] * fraction);
                referencePosition += referenceRatio;
            }

            int referenceConsumedFrames = (int)referencePosition;
            referenceFractionalPosition = referencePosition -
                referenceConsumedFrames;
            referenceBufferedFrames = referenceAvailableFrames -
                referenceConsumedFrames;
            if (referenceBufferedFrames > 0)
            {
                Array.Copy(referenceInput,
                    referenceConsumedFrames * Channels, referenceInput, 0,
                    referenceBufferedFrames * Channels);
            }

            return OutputFrames;
        }

        internal void Reset()
        {
            clockFractionalPosition = 0.0;
            referenceFractionalPosition = 0.0;
            clockBufferedFrames = 0;
            referenceBufferedFrames = 0;
            preparedSourceFrames = 0;
            preparedInputRateRatio = inputRateRatio;
        }

        private static void ValidateFloatRange(float[] buffer,
            int sampleOffset, int frames, string parameterName)
        {
            ArgumentNullException.ThrowIfNull(buffer, parameterName);
            if (sampleOffset < 0 || frames < 0 ||
                sampleOffset > buffer.Length ||
                frames > (buffer.Length - sampleOffset) / Channels)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    /// <summary>
    /// High-quality streaming converter for interleaved stereo PCM16 supplied
    /// by a VIIPER audio endpoint (notably 32 kHz virtual DS4 audio) before it
    /// enters the 48 kHz DualSense speaker pipeline.
    /// </summary>
    internal sealed class DualSensePcm16SourceRateConverter
    {
        internal const int Channels = 2;
        internal const int BytesPerFrame = sizeof(short) * Channels;
        internal const int DefaultOutputRate = 48000;
        internal const int ProcessingBlockFrames = 256;

        private readonly WdlResampler resampler;
        private readonly float[] stagedInput = new float[
            ProcessingBlockFrames * Channels];
        private readonly float[] outputScratch;
        private readonly int sourceSampleRate;
        private readonly int outputSampleRate;
        private readonly bool bypassResampling;
        private int stagedInputFrames;

        internal DualSensePcm16SourceRateConverter(int sourceSampleRate,
            int outputSampleRate = DefaultOutputRate)
        {
            if (sourceSampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceSampleRate));
            }
            if (outputSampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(outputSampleRate));
            }

            this.sourceSampleRate = sourceSampleRate;
            this.outputSampleRate = outputSampleRate;
            bypassResampling = sourceSampleRate == outputSampleRate;
            resampler = new WdlResampler();
            resampler.SetMode(true, 2, false);
            resampler.SetFeedMode(true);
            resampler.SetRates(sourceSampleRate, outputSampleRate);
            double maximumBlockOutput = (ProcessingBlockFrames + 2.0) *
                outputSampleRate / sourceSampleRate + 2.0;
            if (!double.IsFinite(maximumBlockOutput) ||
                maximumBlockOutput > int.MaxValue / Channels)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(outputSampleRate));
            }

            outputScratch = new float[
                (int)Math.Ceiling(maximumBlockOutput) * Channels];
        }

        internal int SourceSampleRate => sourceSampleRate;

        internal int OutputSampleRate => outputSampleRate;

        /// <summary>
        /// Converts little-endian stereo PCM16 through a fixed staging buffer,
        /// avoiding per-callback temporary arrays. The destination offset is
        /// an interleaved-float sample offset.
        /// </summary>
        internal int Convert(byte[] source, int sourceByteOffset,
            int sourceByteCount, float[] destination,
            int destinationSampleOffset, int destinationCapacityFrames)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            if (sourceByteOffset < 0 || sourceByteCount < 0 ||
                sourceByteOffset > source.Length ||
                sourceByteCount > source.Length - sourceByteOffset ||
                sourceByteCount % BytesPerFrame != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceByteCount));
            }
            if (destinationSampleOffset < 0 ||
                destinationCapacityFrames < 0 ||
                destinationSampleOffset > destination.Length ||
                destinationCapacityFrames >
                    (destination.Length - destinationSampleOffset) / Channels)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(destinationCapacityFrames));
            }

            int sourceFrames = sourceByteCount / BytesPerFrame;
            if (sourceFrames == 0)
            {
                return 0;
            }

            int requiredCapacity = GetMaximumOutputFrames(sourceFrames);
            if (destinationCapacityFrames < requiredCapacity)
            {
                throw new ArgumentException(
                    $"Destination capacity must be at least " +
                    $"{requiredCapacity} frames for this input block.",
                    nameof(destinationCapacityFrames));
            }

            // A unity-rate stream needs no interpolation or anti-aliasing.
            // Convert PCM16 directly so no partial block is staged outside the
            // capture ring. In particular, overflow recovery can reset the
            // converter without silently discarding a staged block tail.
            if (bypassResampling)
            {
                int destinationOffset = destinationSampleOffset;
                int sourceEnd = sourceByteOffset + sourceByteCount;
                for (int pcmOffset = sourceByteOffset;
                    pcmOffset < sourceEnd; pcmOffset += sizeof(short))
                {
                    short value = (short)(source[pcmOffset] |
                        source[pcmOffset + 1] << 8);
                    destination[destinationOffset++] = value / 32768.0f;
                }

                return sourceFrames;
            }

            int sourceOffset = sourceByteOffset;
            int remainingFrames = sourceFrames;
            int producedFrames = 0;
            while (remainingFrames > 0)
            {
                int copiedFrames = Math.Min(remainingFrames,
                    ProcessingBlockFrames - stagedInputFrames);
                int inputOffset = stagedInputFrames * Channels;
                int sampleCount = copiedFrames * Channels;
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    short value = (short)(source[sourceOffset] |
                        source[sourceOffset + 1] << 8);
                    stagedInput[inputOffset++] = value / 32768.0f;
                    sourceOffset += sizeof(short);
                }

                stagedInputFrames += copiedFrames;
                remainingFrames -= copiedFrames;
                if (stagedInputFrames != ProcessingBlockFrames)
                {
                    continue;
                }

                producedFrames += ConvertPreparedBlock(destination,
                    destinationSampleOffset + producedFrames * Channels,
                    destinationCapacityFrames - producedFrames);
                stagedInputFrames = 0;
            }

            return producedFrames;
        }

        /// <summary>
        /// Convenience overload matching the existing direct-PCM converter.
        /// </summary>
        internal int Convert(byte[] source, int sourceByteOffset,
            int sourceByteCount, float[] destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            return Convert(source, sourceByteOffset, sourceByteCount,
                destination, 0, destination.Length / Channels);
        }

        internal int GetMaximumOutputFrames(int sourceFrames)
        {
            if (sourceFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceFrames));
            }

            if (bypassResampling)
            {
                return sourceFrames;
            }

            long availableFrames = (long)stagedInputFrames + sourceFrames;
            long completeInputFrames = availableFrames /
                ProcessingBlockFrames * ProcessingBlockFrames;
            if (completeInputFrames == 0)
            {
                return 0;
            }

            double outputFrames = completeInputFrames *
                outputSampleRate / sourceSampleRate + 2.0;
            if (!double.IsFinite(outputFrames) ||
                outputFrames > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceFrames));
            }

            return (int)Math.Ceiling(outputFrames);
        }

        internal void Reset()
        {
            resampler.Reset();
            stagedInputFrames = 0;
        }

        private int ConvertPreparedBlock(float[] destination,
            int destinationSampleOffset, int destinationCapacityFrames)
        {
            int requested = resampler.ResamplePrepare(ProcessingBlockFrames,
                Channels, out float[] inputBuffer,
                out int inputBufferOffset);
            if (requested != ProcessingBlockFrames)
            {
                throw new InvalidOperationException(
                    $"WDL accepted {requested} of " +
                    $"{ProcessingBlockFrames} input frames.");
            }

            Array.Copy(stagedInput, 0, inputBuffer, inputBufferOffset,
                stagedInput.Length);
            int produced = resampler.ResampleOut(outputScratch, 0,
                ProcessingBlockFrames, outputScratch.Length / Channels,
                Channels);
            if (produced > destinationCapacityFrames)
            {
                throw new InvalidOperationException(
                    "The PCM destination capacity calculation was too " +
                    "small for WDL output.");
            }

            // WdlResampler's IIR path in NAudio 2.2.1 filters from index zero
            // even when ResampleOut receives a non-zero output index. Always
            // filter in scratch, then place the finished block ourselves.
            Array.Copy(outputScratch, 0, destination,
                destinationSampleOffset, produced * Channels);
            return produced;
        }
    }
}
