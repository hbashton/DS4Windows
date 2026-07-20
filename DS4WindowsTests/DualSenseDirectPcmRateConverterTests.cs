using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseDirectPcmRateConverterTests
    {
        private const int StereoFrameBytes = sizeof(short) * 2;

        [TestMethod]
        public void FortyEightKilohertzConversionIsSampleForSampleIdentity()
        {
            const int frames = 513;
            byte[] source = CreateStereoPcm(frames, frame =>
            {
                short left = (short)(-16000 + frame * 61);
                short right = (short)(15000 - frame * 53);
                return (left, right);
            });
            float[] destination = new float[frames * 2];
            var converter = new DualSenseDirectPcmRateConverter(48000, 48000);

            int convertedFrames = converter.Convert(source, 0, source.Length,
                destination);

            Assert.AreEqual(frames, convertedFrames);
            for (int frame = 0; frame < frames; frame++)
            {
                Assert.AreEqual(ReadInt16(source, frame * StereoFrameBytes) /
                    32768.0f, destination[frame * 2], 1.0e-7f,
                    $"Left sample {frame} changed during identity conversion.");
                Assert.AreEqual(ReadInt16(source,
                    frame * StereoFrameBytes + sizeof(short)) / 32768.0f,
                    destination[frame * 2 + 1], 1.0e-7f,
                    $"Right sample {frame} changed during identity conversion.");
            }
        }

        [TestMethod]
        public void ThirtyTwoToFortyEightKilohertzPreservesCountFrequencyAndContinuity()
        {
            const int sourceRate = 32000;
            const int outputRate = 48000;
            const int inputFrames = sourceRate;
            const double frequency = 1000.0;
            byte[] source = CreateStereoPcm(inputFrames, frame =>
            {
                short sample = (short)Math.Round(short.MaxValue * 0.7 *
                    Math.Sin(2.0 * Math.PI * frequency * frame / sourceRate));
                return (sample, sample);
            });
            float[] destination = new float[(inputFrames * 3 / 2 + 2) * 2];
            var converter = new DualSenseDirectPcmRateConverter(sourceRate,
                outputRate);

            int convertedFrames = converter.Convert(source, 0, source.Length,
                destination);

            int expectedFrames = (int)Math.Floor((inputFrames - 1) *
                (outputRate / (double)sourceRate)) + 1;
            Assert.AreEqual(expectedFrames, convertedFrames);

            List<double> positiveCrossings = FindPositiveZeroCrossings(
                destination, convertedFrames);
            Assert.IsTrue(positiveCrossings.Count > 900,
                "The converted tone did not retain its expected cycles.");
            double measuredFrequency = (positiveCrossings.Count - 1) *
                outputRate / (positiveCrossings[^1] - positiveCrossings[0]);
            Assert.AreEqual(frequency, measuredFrequency, 0.05,
                "Sample-rate conversion changed the tone frequency.");

            float maximumStep = 0.0f;
            for (int frame = 1; frame < convertedFrames; frame++)
            {
                float current = destination[frame * 2];
                Assert.IsFalse(float.IsNaN(current) || float.IsInfinity(current));
                maximumStep = Math.Max(maximumStep,
                    Math.Abs(current - destination[(frame - 1) * 2]));
            }

            Assert.IsTrue(maximumStep < 0.11f,
                $"Converted tone contains a discontinuity ({maximumStep:F6}).");
        }

        [TestMethod]
        public void ArbitraryChunkBoundariesMatchSingleBufferConversion()
        {
            const int inputFrames = 4097;
            byte[] source = CreateStereoPcm(inputFrames, frame =>
            {
                short left = (short)Math.Round(12000.0 * Math.Sin(
                    2.0 * Math.PI * 431.0 * frame / 32000.0) +
                    3500.0 * Math.Sin(2.0 * Math.PI * 997.0 * frame / 32000.0));
                short right = (short)Math.Round(11000.0 * Math.Sin(
                    2.0 * Math.PI * 613.0 * frame / 32000.0) -
                    4200.0 * Math.Sin(2.0 * Math.PI * 1231.0 * frame / 32000.0));
                return (left, right);
            });

            float[] singleBuffer = ConvertInChunks(source, inputFrames);
            float[] chunked = ConvertInChunks(source,
                1, 7, 31, 2, 257, 3, 64, 5, 509, 11, 127);

            Assert.AreEqual(singleBuffer.Length, chunked.Length,
                "Chunking changed the number of converted samples.");
            for (int sample = 0; sample < singleBuffer.Length; sample++)
            {
                Assert.AreEqual(singleBuffer[sample], chunked[sample], 1.0e-6f,
                    $"Chunked conversion diverged at sample {sample}.");
            }
        }

        [TestMethod]
        public void ChunkBoundariesNeitherDuplicateNorDropRampSamples()
        {
            const int inputFrames = 241;
            byte[] source = CreateStereoPcm(inputFrames, frame =>
                ((short)(-12000 + frame * 96),
                    (short)(12000 - frame * 48)));

            float[] converted = ConvertInChunks(source,
                17, 1, 2, 31, 1, 43, 3, 5, 64, 1, 7);

            const int expectedOutputFrames = 361;
            Assert.AreEqual(expectedOutputFrames * 2, converted.Length);
            for (int frame = 0; frame < expectedOutputFrames; frame++)
            {
                float expectedLeft = (-12000 + frame * 64) / 32768.0f;
                float expectedRight = (12000 - frame * 32) / 32768.0f;
                Assert.AreEqual(expectedLeft, converted[frame * 2], 1.0e-6f,
                    $"Left ramp duplicated or skipped at output frame {frame}.");
                Assert.AreEqual(expectedRight, converted[frame * 2 + 1],
                    1.0e-6f,
                    $"Right ramp duplicated or skipped at output frame {frame}.");

                if (frame == 0)
                {
                    continue;
                }

                Assert.AreEqual(64.0f / 32768.0f,
                    converted[frame * 2] - converted[(frame - 1) * 2],
                    1.0e-6f,
                    $"Left boundary step changed at output frame {frame}.");
                Assert.AreEqual(-32.0f / 32768.0f,
                    converted[frame * 2 + 1] -
                    converted[(frame - 1) * 2 + 1], 1.0e-6f,
                    $"Right boundary step changed at output frame {frame}.");
            }
        }

        [TestMethod]
        public void ResetStartsANewStreamWithoutInterpolatingFromOldCarry()
        {
            var converter = new DualSenseDirectPcmRateConverter(32000, 48000);
            byte[] oldStream = CreateStereoPcm(5, _ => ((short)-20000,
                (short)-20000));
            float[] scratch = new float[32];
            converter.Convert(oldStream, 0, oldStream.Length, scratch);

            converter.Reset();

            byte[] newStream = CreateStereoPcm(5, _ => ((short)20000,
                (short)20000));
            Array.Clear(scratch, 0, scratch.Length);
            int converted = converter.Convert(newStream, 0,
                newStream.Length, scratch);

            Assert.IsTrue(converted > 0);
            Assert.AreEqual(20000 / 32768.0f, scratch[0], 1.0e-7f,
                "Reset blended the old stream carry into the new stream.");
            Assert.AreEqual(20000 / 32768.0f, scratch[1], 1.0e-7f,
                "Reset blended the old stream carry into the new stream.");
        }

        private static float[] ConvertInChunks(byte[] source,
            params int[] chunkPattern)
        {
            var converter = new DualSenseDirectPcmRateConverter(32000, 48000);
            var converted = new List<float>();
            int totalFrames = source.Length / StereoFrameBytes;
            int sourceFrame = 0;
            int patternIndex = 0;
            while (sourceFrame < totalFrames)
            {
                int requestedFrames = chunkPattern[patternIndex %
                    chunkPattern.Length];
                int chunkFrames = Math.Min(requestedFrames,
                    totalFrames - sourceFrame);
                float[] destination = new float[(chunkFrames * 3 / 2 + 3) * 2];
                int outputFrames = converter.Convert(source,
                    sourceFrame * StereoFrameBytes,
                    chunkFrames * StereoFrameBytes, destination);
                for (int sample = 0; sample < outputFrames * 2; sample++)
                {
                    converted.Add(destination[sample]);
                }

                sourceFrame += chunkFrames;
                patternIndex++;
            }

            return converted.ToArray();
        }

        private static byte[] CreateStereoPcm(int frames,
            Func<int, (short Left, short Right)> sampleFactory)
        {
            byte[] result = new byte[frames * StereoFrameBytes];
            for (int frame = 0; frame < frames; frame++)
            {
                (short left, short right) = sampleFactory(frame);
                WriteInt16(result, frame * StereoFrameBytes, left);
                WriteInt16(result, frame * StereoFrameBytes + sizeof(short),
                    right);
            }

            return result;
        }

        private static List<double> FindPositiveZeroCrossings(float[] samples,
            int frames)
        {
            var result = new List<double>();
            for (int frame = 1; frame < frames; frame++)
            {
                float previous = samples[(frame - 1) * 2];
                float current = samples[frame * 2];
                if (previous <= 0.0f && current > 0.0f)
                {
                    double fraction = previous == current ? 0.0 :
                        -previous / (current - (double)previous);
                    result.Add(frame - 1 + fraction);
                }
            }

            return result;
        }

        private static short ReadInt16(byte[] source, int offset)
        {
            return (short)(source[offset] | source[offset + 1] << 8);
        }

        private static void WriteInt16(byte[] destination, int offset,
            short value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
        }
    }
}
