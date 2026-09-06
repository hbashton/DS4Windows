using System;
using DS4Windows.Switch2;

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

        /// <summary>
        /// Preserves the audited stereo PCM window as independent left/right,
        /// three-slice HD-rumble envelopes with packet-local band analysis.
        /// One 64-byte interleaved window contains 32 samples per side; keeping
        /// three chronological slices maps directly onto the three Switch 2
        /// wire subframes instead of averaging the complete window into one
        /// value. This is used only when the physical target is Switch 2;
        /// conventional controllers keep the historical two-motor reduction
        /// above.
        /// </summary>
        internal static bool TryTranslateToSwitch2Groups(byte[] feedback,
            int feedbackLength, int reportOffset,
            out Switch2HdRumbleGroup left,
            out Switch2HdRumbleGroup right)
        {
            left = default;
            right = default;
            if (feedback == null || feedbackLength < 0 ||
                feedbackLength > feedback.Length || reportOffset < 0 ||
                reportOffset >= feedbackLength)
            {
                return false;
            }

            int sampleOffset = feedback[reportOffset] switch
            {
                0x32 => reportOffset + LegacyHapticsOffset,
                0x36 => reportOffset + CombinedHapticsOffset,
                _ => -1,
            };
            if (sampleOffset < 0 ||
                sampleOffset + HapticsSampleLength > feedbackLength)
            {
                return false;
            }

            var samples = feedback.AsSpan(sampleOffset, HapticsSampleLength);
            Span<Switch2PcmSlice> slices = stackalloc Switch2PcmSlice[3];
            Switch2PcmBandAnalyzer.AnalyzeSlices(samples, 0, slices);
            left = new Switch2HdRumbleGroup(
                CreateSwitch2PcmSubframe(slices[0]),
                CreateSwitch2PcmSubframe(slices[1]),
                CreateSwitch2PcmSubframe(slices[2]));
            Switch2PcmBandAnalyzer.AnalyzeSlices(samples, 1, slices);
            right = new Switch2HdRumbleGroup(
                CreateSwitch2PcmSubframe(slices[0]),
                CreateSwitch2PcmSubframe(slices[1]),
                CreateSwitch2PcmSubframe(slices[2]));
            return true;
        }

        private static Switch2HdRumbleSubframe CreateSwitch2PcmSubframe(
            in Switch2PcmSlice slice)
        {
            ushort low = ToCanonicalAmplitude(slice.LowAmplitude);
            ushort high = ToCanonicalAmplitude(slice.HighAmplitude);
            return new Switch2HdRumbleSubframe(slice.HighControl,
                Switch2HdRumbleFeedbackTranslator.ScaleCanonicalAmplitude(high),
                slice.LowControl, Switch2HdRumbleFeedbackTranslator.ScaleCanonicalAmplitude(low));
        }

        private static byte ToMotorValue(double normalized)
        {
            int value = Math.Clamp((int)Math.Round(normalized * byte.MaxValue),
                0, byte.MaxValue);
            return value < MinimumMotorValue ? (byte)0 : (byte)value;
        }

        private static ushort ToCanonicalAmplitude(double normalized)
        {
            int value = Math.Clamp((int)Math.Round(normalized *
                ushort.MaxValue), 0, ushort.MaxValue);
            // HD voice-coil synthesis must not inherit the conventional motor
            // startup dead zone. The bounded wire quantizer decides the floor.
            return (ushort)value;
        }
    }
}
