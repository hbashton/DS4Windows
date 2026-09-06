using DS4Windows;
using DS4Windows.Switch2;

namespace DS4WindowsTests
{
    [TestClass]
    public class DualSenseHapticsTranslatorTests
    {
        [TestMethod]
        public void SilencePreservesBaseRumble()
        {
            byte[] feedback = new byte[141];
            feedback[0] = 20;
            feedback[1] = 30;
            feedback[28] = 0x32;

            DualSenseHapticsTranslator.Translate(feedback, feedback.Length, 28,
                out byte light, out byte heavy);

            Assert.AreEqual((byte)30, light);
            Assert.AreEqual((byte)20, heavy);
        }

        [TestMethod]
        public void LegacyHapticsDriveBothMotorsWithinBounds()
        {
            byte[] feedback = new byte[28 + 141];
            feedback[28] = 0x32;
            for (int index = 0; index < 64; index += 2)
            {
                byte value = (index / 2) % 2 == 0 ?
                    unchecked((byte)110) : unchecked((byte)-110);
                feedback[28 + 13 + index] = value;
                feedback[28 + 13 + index + 1] = value;
            }

            DualSenseHapticsTranslator.Translate(feedback, feedback.Length, 28,
                out byte light, out byte heavy);

            Assert.IsTrue(light > 0);
            Assert.IsTrue(heavy > 0);
            Assert.IsTrue(light <= byte.MaxValue);
            Assert.IsTrue(heavy <= byte.MaxValue);
        }

        [TestMethod]
        public void CombinedHapticsUsesCombinedPayloadOffset()
        {
            byte[] feedback = new byte[28 + 398];
            feedback[28] = 0x36;
            for (int index = 0; index < 64; index += 2)
            {
                feedback[28 + 78 + index] = 80;
                feedback[28 + 78 + index + 1] = 80;
            }

            DualSenseHapticsTranslator.Translate(feedback, feedback.Length, 28,
                out byte light, out byte heavy);

            Assert.AreEqual((byte)0, light);
            Assert.IsTrue(heavy > 0);
        }

        [TestMethod]
        public void Switch2TranslationPreservesStereoDualBandEnergy()
        {
            const int reportOffset = 28;
            byte[] feedback = new byte[reportOffset + 141];
            feedback[reportOffset] = 0x32;
            for (int index = 0; index < 64; index += 2)
            {
                feedback[reportOffset + 13 + index] =
                    (index / 2 & 1) == 0 ? (byte)110 :
                    unchecked((byte)-110);
                feedback[reportOffset + 13 + index + 1] = 0;
            }

            Assert.IsTrue(DualSenseHapticsTranslator.
                TryTranslateToSwitch2Groups(feedback, feedback.Length,
                    reportOffset, out Switch2HdRumbleGroup left,
                    out Switch2HdRumbleGroup right));
            Assert.IsTrue(left.First.Oscillator0AmplitudeCode > 0,
                "Left transient energy must retain a high-band value.");
            Assert.AreEqual((ushort)0, left.First.Oscillator1AmplitudeCode,
                "A pure Nyquist tone must not be duplicated into low-band rumble.");
            Assert.IsTrue(left.Second.Oscillator0AmplitudeCode > 0);
            Assert.IsTrue(left.Third.Oscillator0AmplitudeCode > 0);
            Assert.IsFalse(right.First.HasNonzeroAmplitude,
                "A silent right PCM channel must stay silent.");
            Assert.IsFalse(right.Second.HasNonzeroAmplitude);
            Assert.IsFalse(right.Third.HasNonzeroAmplitude);
        }

        [TestMethod]
        public void Switch2TranslationKeepsThreeChronologicalPcmSlices()
        {
            const int reportOffset = 28;
            const int sampleOffset = reportOffset + 13;
            byte[] feedback = new byte[reportOffset + 141];
            feedback[reportOffset] = 0x32;
            for (int sample = 0; sample < 32; sample++)
            {
                int value = sample < 10 ? 20 : sample < 21 ?
                    ((sample & 1) == 0 ? 110 : -110) : 0;
                feedback[sampleOffset + sample * 2] =
                    unchecked((byte)(sbyte)value);
            }

            Assert.IsTrue(DualSenseHapticsTranslator.
                TryTranslateToSwitch2Groups(feedback, feedback.Length,
                    reportOffset, out Switch2HdRumbleGroup left,
                    out Switch2HdRumbleGroup right));
            Assert.IsTrue(left.Second.Oscillator0AmplitudeCode >
                left.First.Oscillator0AmplitudeCode,
                "The transient-rich middle slice must remain distinct.");
            Assert.IsTrue(left.First.Oscillator1AmplitudeCode >
                left.Second.Oscillator1AmplitudeCode,
                "The low-frequency opening must not gain low-band buzz from the high-frequency middle.");
            Assert.AreEqual((ushort)0,
                left.Third.Oscillator1AmplitudeCode,
                "The silent final slice must not inherit the earlier RMS.");
            Assert.AreNotEqual(left.First, left.Second);
            Assert.AreNotEqual(left.Second, left.Third);
            Assert.IsFalse(right.First.HasNonzeroAmplitude);
            Assert.IsFalse(right.Second.HasNonzeroAmplitude);
            Assert.IsFalse(right.Third.HasNonzeroAmplitude);
        }

        [TestMethod]
        public void Switch2PcmTranslationAllocatesNothingAfterWarmup()
        {
            const int reportOffset = 28;
            byte[] feedback = new byte[reportOffset + 141];
            feedback[reportOffset] = 0x32;
            for (int index = 0; index < 64; index++)
            {
                feedback[reportOffset + 13 + index] =
                    unchecked((byte)(sbyte)((index * 29 & 0x7f) - 64));
            }
            for (int index = 0; index < 128; index++)
            {
                DualSenseHapticsTranslator.TryTranslateToSwitch2Groups(
                    feedback, feedback.Length, reportOffset, out _, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            bool valid = true;
            for (int index = 0; index < 10_000; index++)
            {
                valid &= DualSenseHapticsTranslator.
                    TryTranslateToSwitch2Groups(feedback, feedback.Length,
                        reportOffset, out _, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(valid);
            Assert.AreEqual(0L, allocated);
        }
    }
}
