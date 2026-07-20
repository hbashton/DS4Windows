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

        private readonly WdlResampler resampler;
        private readonly float[] stagedInput = new float[
            NominalInputFrames * Channels];
        private double inputRateRatio = 1.0;
        private int stagedInputFrames;
        private bool nominalFramePhase = true;

        internal DualSenseSpeakerFrameResampler()
        {
            resampler = new WdlResampler();
            resampler.SetMode(true, 0, false);
            resampler.SetFeedMode(true);
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
            if (ratio != 1.0)
            {
                nominalFramePhase = false;
            }
        }

        /// <summary>
        /// Converts one reference-sized block. This allocation-free hot path
        /// is valid at the nominal ratio and always produces one Opus frame.
        /// Offsets are interleaved-float sample offsets, not frame offsets.
        /// </summary>
        internal void ConvertNominalFrame(float[] source,
            int sourceSampleOffset, float[] destination,
            int destinationSampleOffset)
        {
            if (inputRateRatio != 1.0 || !nominalFramePhase ||
                stagedInputFrames != 0)
            {
                throw new InvalidOperationException(
                    "Reset or use Convert when a non-nominal streaming " +
                    "phase is active.");
            }

            ValidateFloatRange(source, sourceSampleOffset,
                NominalInputFrames, nameof(source));
            ValidateFloatRange(destination, destinationSampleOffset,
                OutputFrames, nameof(destination));

            int produced = ConvertPreparedBlock(source, sourceSampleOffset,
                NominalInputFrames, destination, destinationSampleOffset,
                OutputFrames);
            if (produced != OutputFrames)
            {
                throw new InvalidOperationException(
                    $"The nominal DualSense frame resampler produced " +
                    $"{produced} frames instead of {OutputFrames}.");
            }
        }

        /// <summary>
        /// Converts an arbitrary streaming input block. This form is intended
        /// for clock-corrected operation, where a block can yield one frame
        /// more or less than the nominal count. The caller must accumulate the
        /// returned frames before taking fixed 480-frame Opus packets.
        /// Offsets are interleaved-float sample offsets.
        /// </summary>
        internal int Convert(float[] source, int sourceSampleOffset,
            int sourceFrames, float[] destination,
            int destinationSampleOffset, int destinationCapacityFrames)
        {
            ValidateFloatRange(source, sourceSampleOffset, sourceFrames,
                nameof(source));
            ValidateFloatRange(destination, destinationSampleOffset,
                destinationCapacityFrames, nameof(destination));

            int requiredCapacity = GetMaximumOutputFrames(sourceFrames);
            if (destinationCapacityFrames < requiredCapacity)
            {
                throw new ArgumentException(
                    $"Destination capacity must be at least " +
                    $"{requiredCapacity} frames for this input block.",
                    nameof(destinationCapacityFrames));
            }

            int remainingFrames = sourceFrames;
            int sourceOffset = sourceSampleOffset;
            int producedFrames = 0;
            while (remainingFrames > 0)
            {
                int copiedFrames = Math.Min(remainingFrames,
                    NominalInputFrames - stagedInputFrames);
                Array.Copy(source, sourceOffset, stagedInput,
                    stagedInputFrames * Channels, copiedFrames * Channels);
                stagedInputFrames += copiedFrames;
                sourceOffset += copiedFrames * Channels;
                remainingFrames -= copiedFrames;

                if (stagedInputFrames != NominalInputFrames)
                {
                    continue;
                }

                producedFrames += ConvertPreparedBlock(stagedInput, 0,
                    NominalInputFrames, destination,
                    destinationSampleOffset + producedFrames * Channels,
                    destinationCapacityFrames - producedFrames);
                stagedInputFrames = 0;
            }

            return producedFrames;
        }

        internal int GetMaximumOutputFrames(int sourceFrames)
        {
            if (sourceFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceFrames));
            }

            long availableFrames = (long)stagedInputFrames + sourceFrames;
            long completeInputFrames = availableFrames /
                NominalInputFrames * NominalInputFrames;
            if (completeInputFrames == 0)
            {
                return 0;
            }

            double inputFramesPerOutputFrame =
                NominalInputRate * inputRateRatio / OutputRate;
            return CheckedCeiling(completeInputFrames /
                inputFramesPerOutputFrame + 2.0);
        }

        internal void Reset()
        {
            resampler.Reset();
            stagedInputFrames = 0;
            nominalFramePhase = inputRateRatio == 1.0;
        }

        private int ConvertPreparedBlock(float[] source,
            int sourceSampleOffset,
            int sourceFrames, float[] destination,
            int destinationSampleOffset, int destinationCapacityFrames)
        {
            if (sourceFrames == 0)
            {
                return 0;
            }

            int requested = resampler.ResamplePrepare(sourceFrames, Channels,
                out float[] inputBuffer, out int inputBufferOffset);
            if (requested != sourceFrames)
            {
                throw new InvalidOperationException(
                    $"WDL accepted {requested} of {sourceFrames} input " +
                    "frames.");
            }

            Array.Copy(source, sourceSampleOffset, inputBuffer,
                inputBufferOffset, sourceFrames * Channels);
            return resampler.ResampleOut(destination,
                destinationSampleOffset, sourceFrames,
                destinationCapacityFrames, Channels);
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

        private static int CheckedCeiling(double value)
        {
            if (!double.IsFinite(value) || value > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            return (int)Math.Ceiling(value);
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
