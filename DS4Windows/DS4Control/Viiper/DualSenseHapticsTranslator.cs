using System;

namespace DS4Windows
{
    /// <summary>
    /// Reduces DualSense 3 kHz stereo haptics PCM to the two bounded rumble
    /// motors available on a DualShock 4. Low-frequency energy drives the
    /// heavy motor; rapid sample changes drive the light motor.
    /// </summary>
    public static class DualSenseHapticsTranslator
    {
        private const int HapticsSampleLength = 64;
        private const int LegacyHapticsOffset = 13;
        private const int CombinedHapticsOffset = 78;
        private const byte MinimumMotorValue = 8;

        public static void Translate(byte[] feedback, int feedbackLength,
            int reportOffset, out byte lightFast, out byte heavySlow)
        {
            lightFast = feedback != null && feedbackLength > 1 ? feedback[1] :
                (byte)0;
            heavySlow = feedback != null && feedbackLength > 0 ? feedback[0] :
                (byte)0;
            if (feedback == null || reportOffset < 0 || reportOffset >= feedbackLength)
            {
                return;
            }

            int sampleOffset;
            switch (feedback[reportOffset])
            {
                case 0x32:
                    sampleOffset = reportOffset + LegacyHapticsOffset;
                    break;
                case 0x36:
                    sampleOffset = reportOffset + CombinedHapticsOffset;
                    break;
                default:
                    return;
            }

            if (sampleOffset < 0 ||
                sampleOffset + HapticsSampleLength > feedbackLength)
            {
                return;
            }

            double sumSquares = 0.0;
            double sumDifference = 0.0;
            int previousLeft = unchecked((sbyte)feedback[sampleOffset]);
            int previousRight = unchecked((sbyte)feedback[sampleOffset + 1]);
            for (int index = 0; index < HapticsSampleLength; index += 2)
            {
                int left = unchecked((sbyte)feedback[sampleOffset + index]);
                int right = unchecked((sbyte)feedback[sampleOffset + index + 1]);
                sumSquares += left * left + right * right;
                if (index > 0)
                {
                    sumDifference += Math.Abs(left - previousLeft) +
                        Math.Abs(right - previousRight);
                }

                previousLeft = left;
                previousRight = right;
            }

            double rms = Math.Sqrt(sumSquares / HapticsSampleLength) / 128.0;
            double transient = sumDifference /
                ((HapticsSampleLength - 2) * 255.0);
            byte translatedHeavy = ToMotorValue(rms * 1.45);
            byte translatedLight = ToMotorValue(transient * 2.1);
            heavySlow = Math.Max(heavySlow, translatedHeavy);
            lightFast = Math.Max(lightFast, translatedLight);
        }

        private static byte ToMotorValue(double normalized)
        {
            int value = Math.Clamp((int)Math.Round(normalized * byte.MaxValue),
                0, byte.MaxValue);
            return value < MinimumMotorValue ? (byte)0 : (byte)value;
        }
    }
}
