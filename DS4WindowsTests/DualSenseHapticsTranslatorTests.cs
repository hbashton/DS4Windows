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
        public void AudioHapticsPcmUsesTheSameStereoAnalyzerAsNativeFeedback()
        {
            byte[] samples = new byte[64];
            for (int i = 0; i < 32; i++)
            {
                samples[i * 2] = unchecked((byte)(sbyte)(i < 10 ? 40 : i < 21 ? (i % 2 == 0 ? 100 : -100) : 0));
                samples[i * 2 + 1] = unchecked((byte)(sbyte)(i >= 21 ? -60 : 0));
            }
            Assert.IsTrue(DualSenseHapticsTranslator.TryTranslatePcmToSwitch2Groups(
                samples, out var rawLeft, out var rawRight));
            foreach (bool combined in new[] { false, true })
            {
                byte[] feedback = new byte[28 + 398];
                feedback[28] = combined ? (byte)0x36 : (byte)0x32;
                samples.CopyTo(feedback, 28 + (combined ? 78 : 13));
                Assert.IsTrue(DualSenseHapticsTranslator.TryTranslateToSwitch2Groups(
                    feedback, feedback.Length, 28, out var nativeLeft, out var nativeRight));
                Assert.AreEqual(nativeLeft, rawLeft);
                Assert.AreEqual(nativeRight, rawRight);
            }
            Assert.AreNotEqual(rawLeft, rawRight);
            Assert.AreNotEqual(rawLeft.First, rawLeft.Second);
            Assert.IsFalse(rawLeft.Third.HasNonzeroAmplitude);
            Assert.IsFalse(rawRight.First.HasNonzeroAmplitude);
        }

        [TestMethod]
        public void AudioHapticsPcmRejectsPartialAndOversizedWindows()
        {
            foreach (int length in new[] { 0, 1, 63, 65, 128 })
            {
                Assert.IsFalse(DualSenseHapticsTranslator.TryTranslatePcmToSwitch2Groups(
                    new byte[length], out var left, out var right));
                Assert.AreEqual(default(Switch2HdRumbleGroup), left);
                Assert.AreEqual(default(Switch2HdRumbleGroup), right);
            }
        }

        [TestMethod]
        public void AudioHapticsShapingAndHdConversionKeepTheSilentSideSilent()
        {
            foreach (bool rightOnly in new[] { false, true })
            {
                var processor = new AudioHapticsProcessor(
                    new AudioHapticsProfileSettings { GainPercent = 100 }, 48000);
                byte[] samples = new byte[64];
                bool feltSignal = false;
                int position = 0;
                for (int i = 0; i < 48000; i++)
                {
                    float signal = (float)(Math.Sin(i * 2 * Math.PI * 110 / 48000) * 0.5);
                    processor.Process(rightOnly ? 0 : signal, rightOnly ? signal : 0,
                        out float left, out float right);
                    if (i % 16 != 15) continue;
                    samples[position++] = AudioHapticsProcessor.Quantize(left);
                    samples[position++] = AudioHapticsProcessor.Quantize(right);
                    if (position != 64) continue;
                    position = 0;
                    Assert.IsTrue(DualSenseHapticsTranslator.TryTranslatePcmToSwitch2Groups(
                        samples, out var leftGroup, out var rightGroup));
                    var silent = rightOnly ? leftGroup : rightGroup;
                    var active = rightOnly ? rightGroup : leftGroup;
                    Assert.IsFalse(silent.First.HasNonzeroAmplitude ||
                        silent.Second.HasNonzeroAmplitude || silent.Third.HasNonzeroAmplitude);
                    feltSignal |= active.First.HasNonzeroAmplitude ||
                        active.Second.HasNonzeroAmplitude || active.Third.HasNonzeroAmplitude;
                }
                Assert.IsTrue(feltSignal, "The sounding side must produce HD rumble, not merely pass silence checks.");
            }
        }

        [TestMethod]
        public void NintendoAudioMixUsesDominantCarriersPerBandWithoutFlatteningSlices()
        {
            var native = new Switch2HdRumbleGroup(
                new(100, 400, 200, 30), new(110, 40, 210, 400), new(120, 40, 220, 40));
            var audio = new Switch2HdRumbleGroup(
                new(300, 30, 400, 400), new(310, 400, 410, 30), new(320, 40, 420, 40));
            var mixed = DualSenseHapticsTranslator.MixSwitch2AudioGroups(native, audio);
            Assert.AreEqual((ushort)100, mixed.First.Oscillator0ControlCode);
            Assert.AreEqual((ushort)400, mixed.First.Oscillator1ControlCode);
            Assert.AreEqual((ushort)310, mixed.Second.Oscillator0ControlCode);
            Assert.AreEqual((ushort)210, mixed.Second.Oscillator1ControlCode);
            Assert.AreEqual((ushort)120, mixed.Third.Oscillator0ControlCode, "Native wins a tie.");
            Assert.AreEqual((ushort)220, mixed.Third.Oscillator1ControlCode);
            Assert.IsTrue(mixed.First.Oscillator0AmplitudeCode > native.First.Oscillator0AmplitudeCode);
            Assert.IsTrue(mixed.First.Oscillator0AmplitudeCode <= 1023);
            Assert.AreEqual(native, DualSenseHapticsTranslator.MixSwitch2AudioGroups(native, default));
            Assert.AreEqual(audio, DualSenseHapticsTranslator.MixSwitch2AudioGroups(default, audio));
        }

        [TestMethod]
        public void AudioHapticsHdConversionAndMixAllocateNothingAfterWarmup()
        {
            var samples = Enumerable.Range(0, 64).Select(i => unchecked((byte)(sbyte)(i * 31 % 255 - 127))).ToArray();
            for (int i = 0; i < 1000; i++)
            {
                DualSenseHapticsTranslator.TryTranslatePcmToSwitch2Groups(samples, out var l, out var r);
                _ = DualSenseHapticsTranslator.MixSwitch2AudioGroups(l, r);
            }
            bool valid = true;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                valid &= DualSenseHapticsTranslator.TryTranslatePcmToSwitch2Groups(samples, out var l, out var r);
                _ = DualSenseHapticsTranslator.MixSwitch2AudioGroups(l, r);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.IsTrue(valid);
            Assert.AreEqual(0L, allocated);
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
