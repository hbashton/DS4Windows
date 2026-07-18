using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseSpeakerProcessorTests
    {
        [TestMethod]
        public void DisabledProcessorLeavesSamplesUntouched()
        {
            float[] samples = CreateSine(1000.0f, 0.25f, 480);
            float[] original = (float[])samples.Clone();
            var processor = new DualSenseSpeakerProcessor(
                DualSenseSpeakerCompression.Off, 0);

            processor.Process(samples, samples.Length / 2);

            CollectionAssert.AreEqual(original, samples);
        }

        [TestMethod]
        public void BalancedCompressionRaisesQuietDetail()
        {
            float[] samples = CreateSine(1000.0f, 0.04f, 48000);
            float inputRms = RootMeanSquare(samples, 24000);
            var processor = new DualSenseSpeakerProcessor(
                DualSenseSpeakerCompression.Balanced, 0);

            processor.Process(samples, samples.Length / 2);

            Assert.IsTrue(RootMeanSquare(samples, 24000) > inputRms * 1.45f);
        }

        [TestMethod]
        public void BalancedCompressionRestrainsLoudAudio()
        {
            float[] samples = CreateSine(1000.0f, 0.8f, 48000);
            float inputRms = RootMeanSquare(samples, 24000);
            var processor = new DualSenseSpeakerProcessor(
                DualSenseSpeakerCompression.Balanced, 0);

            processor.Process(samples, samples.Length / 2);

            Assert.IsTrue(RootMeanSquare(samples, 24000) < inputRms * 0.8f);
            Assert.IsTrue(MaxAbsolute(samples) <= 0.891251f);
        }

        [TestMethod]
        public void BassBoostFavorsLowFrequencies()
        {
            float[] bass = CreateSine(120.0f, 0.05f, 48000);
            float[] midrange = CreateSine(2000.0f, 0.05f, 48000);
            var bassProcessor = new DualSenseSpeakerProcessor(
                DualSenseSpeakerCompression.Off, 6);
            var midrangeProcessor = new DualSenseSpeakerProcessor(
                DualSenseSpeakerCompression.Off, 6);

            bassProcessor.Process(bass, bass.Length / 2);
            midrangeProcessor.Process(midrange, midrange.Length / 2);

            Assert.IsTrue(RootMeanSquare(bass, 24000) >
                RootMeanSquare(midrange, 24000) * 1.35f);
        }

        private static float[] CreateSine(float frequency, float amplitude, int frames)
        {
            float[] samples = new float[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                float value = amplitude * (float)Math.Sin(
                    2.0 * Math.PI * frequency * frame / DualSenseSpeakerProcessor.SampleRate);
                samples[frame * 2] = value;
                samples[frame * 2 + 1] = value;
            }

            return samples;
        }

        private static float RootMeanSquare(float[] samples, int skipFrames)
        {
            double sum = 0.0;
            int firstSample = Math.Min(samples.Length, skipFrames * 2);
            int count = samples.Length - firstSample;
            for (int i = firstSample; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }

            return (float)Math.Sqrt(sum / Math.Max(1, count));
        }

        private static float MaxAbsolute(float[] samples)
        {
            float result = 0.0f;
            foreach (float sample in samples)
            {
                result = Math.Max(result, Math.Abs(sample));
            }

            return result;
        }
    }
}
