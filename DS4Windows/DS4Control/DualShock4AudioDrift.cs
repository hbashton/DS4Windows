using System;

namespace DS4Windows
{
    internal enum DualShock4AudioDriftMode
    {
        Off,
        Fractional,
        Slip,
    }

    /// <summary>
    /// Runtime selection and bounded queue steering for the physical DS4
    /// speaker clock. The ratio is expressed as output frames per input frame:
    /// a value above one grows the source cushion and a value below one drains
    /// it.
    /// </summary>
    internal static class DualShock4AudioDriftSettings
    {
        internal const string EnvironmentVariableName =
            "DS4WINDOWS_DS4_AUDIO_DRIFT_MODE";
        internal const double CorrectionGain = 1.0 / 2048.0;
        internal const double MaximumCorrection = 0.002;
        internal const double MaximumAsrcRatioDeviation = 0.008;
        internal const double RatioSlewPerPacket = 0.0001;

        internal static DualShock4AudioDriftMode Parse(string value)
        {
            if (string.Equals(value?.Trim(), "off",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioDriftMode.Off;
            }
            if (string.Equals(value?.Trim(), "slip",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DualShock4AudioDriftMode.Slip;
            }

            // Fractional is intentionally also the fallback for an unset or
            // misspelled value. It is the production-safe default, while the
            // two experimental controls remain explicitly opt-in.
            return DualShock4AudioDriftMode.Fractional;
        }

        internal static double CalculateTargetOutputRatio(int queueDepth,
            int targetQueueDepth)
        {
            double correction = Math.Clamp(
                (targetQueueDepth - queueDepth) * CorrectionGain,
                -MaximumCorrection, MaximumCorrection);
            return 1.0 + correction;
        }

        internal static double SlewOutputRatio(double currentRatio,
            double targetRatio)
        {
            currentRatio = ClampAsrcOutputRatio(currentRatio);
            targetRatio = ClampAsrcOutputRatio(targetRatio);
            return currentRatio + Math.Clamp(targetRatio - currentRatio,
                -RatioSlewPerPacket, RatioSlewPerPacket);
        }

        internal static double ClampOutputRatio(double ratio)
        {
            if (!double.IsFinite(ratio))
            {
                return 1.0;
            }
            return Math.Clamp(ratio, 1.0 - MaximumCorrection,
                1.0 + MaximumCorrection);
        }

        internal static double CalculateAsrcOutputRatio(
            double controllerClockRatio, int queueDepth,
            int targetQueueDepth)
        {
            double boundedClockRatio = Math.Clamp(controllerClockRatio,
                DualShock4ControllerClockDiscipline.MinimumPublishedRatio,
                DualShock4ControllerClockDiscipline.MaximumPublishedRatio);
            return ClampAsrcOutputRatio(boundedClockRatio *
                CalculateTargetOutputRatio(queueDepth, targetQueueDepth));
        }

        internal static double ClampAsrcOutputRatio(double ratio)
        {
            if (!double.IsFinite(ratio))
            {
                return 1.0;
            }
            return Math.Clamp(ratio,
                1.0 - MaximumAsrcRatioDeviation,
                1.0 + MaximumAsrcRatioDeviation);
        }
    }

    /// <summary>
    /// Allocation-free, stateful linear ASRC for interleaved stereo PCM16.
    /// The interpolation position and final source frame survive callback
    /// boundaries, so fractional steering never becomes a copied or discarded
    /// whole sample at a packet edge.
    /// </summary>
    internal sealed class StereoPcm16FractionalResampler
    {
        private const double IntegerTolerance = 1.0e-10;
        private double sourcePosition;
        private bool hasCarry;
        private short carryLeft;
        private short carryRight;

        internal void Reset()
        {
            sourcePosition = 0.0;
            hasCarry = false;
            carryLeft = 0;
            carryRight = 0;
        }

        internal int Convert(byte[] source, int length, short[] destination,
            double outputRatio)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }
            if (length < 0 || length > source.Length || length % 4 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            int inputFrames = length / 4;
            if (inputFrames == 0)
            {
                return 0;
            }

            outputRatio = DualShock4AudioDriftSettings.ClampAsrcOutputRatio(
                outputRatio);
            double sourceStep = 1.0 / outputRatio;
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
                        "The fractional PCM destination buffer is too small.",
                        nameof(destination));
                }

                short left0 = ReadSample(source, lowerFrame, 0, carryLeft);
                short right0 = ReadSample(source, lowerFrame, 1, carryRight);
                short left1 = ReadSample(source, upperFrame, 0, carryLeft);
                short right1 = ReadSample(source, upperFrame, 1, carryRight);
                int destinationOffset = outputFrames * 2;
                destination[destinationOffset] = Interpolate(left0, left1,
                    fraction);
                destination[destinationOffset + 1] = Interpolate(right0,
                    right1, fraction);
                outputFrames++;
                sourcePosition += sourceStep;
            }

            sourcePosition -= inputFrames;
            int finalOffset = (inputFrames - 1) * 4;
            carryLeft = ReadInt16(source, finalOffset);
            carryRight = ReadInt16(source, finalOffset + 2);
            hasCarry = true;
            return outputFrames;
        }

        private static short ReadSample(byte[] source, int frame, int channel,
            short carry)
        {
            return frame < 0 ? carry : ReadInt16(source,
                frame * 4 + channel * 2);
        }

        private static short ReadInt16(byte[] source, int offset)
        {
            return (short)(source[offset] | source[offset + 1] << 8);
        }

        private static short Interpolate(short first, short second,
            double fraction)
        {
            if (fraction <= IntegerTolerance || first == second)
            {
                return first;
            }
            return (short)Math.Clamp((int)Math.Round(first +
                (second - (double)first) * fraction), short.MinValue,
                short.MaxValue);
        }
    }
}
