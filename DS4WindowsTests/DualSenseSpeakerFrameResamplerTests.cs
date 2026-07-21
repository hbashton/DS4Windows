using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseSpeakerFrameResamplerTests
    {
        [TestMethod]
        public void NominalBlocksProduceExactlyOneOpusFrameAndMatchSingleChunk()
        {
            const int blockCount = 64;
            int sourceFrames = blockCount *
                DualSenseSpeakerFrameResampler.NominalInputFrames;
            float[] source = CreateStereoFloat(sourceFrames,
                DualSenseSpeakerFrameResampler.NominalInputRate,
                431.0, 997.0, 0.72);

            var blockConverter = new DualSenseSpeakerFrameResampler();
            float[] blockOutput = new float[blockCount *
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            for (int block = 0; block < blockCount; block++)
            {
                blockConverter.ConvertNominalFrame(source,
                    block * DualSenseSpeakerFrameResampler.NominalInputFrames * 2,
                    blockOutput,
                    block * DualSenseSpeakerFrameResampler.OutputFrames * 2);
            }

            var singleConverter = new DualSenseSpeakerFrameResampler();
            float[] singleOutput = new float[
                singleConverter.GetMaximumOutputFrames(sourceFrames) * 2];
            int singleFrames = singleConverter.Convert(source, 0,
                sourceFrames, singleOutput, 0, singleOutput.Length / 2);

            Assert.AreEqual(blockCount *
                DualSenseSpeakerFrameResampler.OutputFrames, singleFrames);
            AssertSamplesEqual(singleOutput, blockOutput,
                blockOutput.Length, 1.0e-7f);
        }

        [TestMethod]
        public void NominalFrameMatchesReferenceLinearPhase()
        {
            var source = new float[
                DualSenseSpeakerFrameResampler.NominalInputFrames * 2];
            for (int frame = 0;
                frame < DualSenseSpeakerFrameResampler.NominalInputFrames;
                frame++)
            {
                source[frame * 2] = frame / 1024.0f;
                source[frame * 2 + 1] = -frame / 2048.0f;
            }

            var output = new float[
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            var converter = new DualSenseSpeakerFrameResampler();
            converter.ConvertNominalFrame(source, 0, output, 0);

            double sourceStep =
                DualSenseSpeakerFrameResampler.NominalInputRate /
                DualSenseSpeakerFrameResampler.OutputRate;
            for (int frame = 0;
                frame < DualSenseSpeakerFrameResampler.OutputFrames; frame++)
            {
                double sourcePosition = frame * sourceStep;
                float expectedLeft = (float)(sourcePosition / 1024.0);
                float expectedRight = (float)(-sourcePosition / 2048.0);
                Assert.AreEqual(expectedLeft, output[frame * 2], 1.0e-7f,
                    $"Left reference phase differed at frame {frame}.");
                Assert.AreEqual(expectedRight, output[frame * 2 + 1],
                    1.0e-7f,
                    $"Right reference phase differed at frame {frame}.");
            }
        }

        [TestMethod]
        public void ArbitraryFloatChunkBoundariesMatchSingleBuffer()
        {
            const int sourceFrames = 8193;
            float[] source = CreateStereoFloat(sourceFrames,
                DualSenseSpeakerFrameResampler.NominalInputRate,
                613.0, 7043.0, 0.65);

            float[] single = ConvertFloatInChunks(source, sourceFrames);
            float[] chunked = ConvertFloatInChunks(source,
                1, 7, 31, 2, 257, 3, 64, 5, 509, 11, 127, 1024);

            Assert.AreEqual(single.Length, chunked.Length,
                "Input chunking changed the streaming output count.");
            AssertSamplesEqual(single, chunked, single.Length, 1.0e-7f);
        }

        [TestMethod]
        public void SlewedClockCorrectionPreservesWaveformContinuity()
        {
            const int blockCount = 200;
            const double frequency = 997.0;
            int sourceFrames = blockCount *
                DualSenseSpeakerFrameResampler.NominalInputFrames;
            float[] source = CreateStereoFloat(sourceFrames,
                DualSenseSpeakerFrameResampler.NominalInputRate,
                frequency, frequency, 0.7);
            var converter = new DualSenseSpeakerFrameResampler();
            var output = new List<float>();
            int sourceFrame = 0;
            int totalFrames = 0;
            for (int block = 0; block < blockCount; block++)
            {
                double ratio = 1.0 + Math.Sin(block * 0.04) * 0.001;
                converter.SetInputRateRatio(ratio);
                int capacity = converter.GetMaximumOutputFrames(
                    DualSenseSpeakerFrameResampler.NominalInputFrames);
                float[] converted = new float[capacity * 2];
                int produced = converter.Convert(source, sourceFrame * 2,
                    DualSenseSpeakerFrameResampler.NominalInputFrames,
                    converted, 0, capacity);
                for (int sample = 0; sample < produced * 2; sample++)
                {
                    output.Add(converted[sample]);
                }

                sourceFrame +=
                    DualSenseSpeakerFrameResampler.NominalInputFrames;
                totalFrames += produced;
            }

            int nominalFrames = blockCount *
                DualSenseSpeakerFrameResampler.OutputFrames;
            Assert.IsTrue(Math.Abs(totalFrames - nominalFrames) < 80,
                $"Clock correction produced an implausible count " +
                $"({totalFrames} versus {nominalFrames}).");

            float maximumStep = 0.0f;
            for (int frame = 1; frame < totalFrames; frame++)
            {
                float previous = output[(frame - 1) * 2];
                float current = output[frame * 2];
                Assert.IsTrue(float.IsFinite(current));
                maximumStep = Math.Max(maximumStep,
                    Math.Abs(current - previous));
            }

            Assert.IsTrue(maximumStep < 0.11f,
                $"A rate update introduced a waveform jump of " +
                $"{maximumStep:F6}.");
        }

        [TestMethod]
        public void NominalFrameHotPathDoesNotAllocateAfterWarmup()
        {
            float[] source = CreateStereoFloat(
                DualSenseSpeakerFrameResampler.NominalInputFrames,
                DualSenseSpeakerFrameResampler.NominalInputRate,
                1000.0, 1000.0, 0.5);
            float[] output = new float[
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            var converter = new DualSenseSpeakerFrameResampler();

            converter.ConvertNominalFrame(source, 0, output, 0);
            converter.ConvertNominalFrame(source, 0, output, 0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                converter.ConvertNominalFrame(source, 0, output, 0);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                "The fixed-frame realtime path allocated after warmup.");
        }

        [TestMethod]
        public void ResetRestartsFramePhaseDeterministically()
        {
            float[] source = CreateStereoFloat(
                DualSenseSpeakerFrameResampler.NominalInputFrames,
                DualSenseSpeakerFrameResampler.NominalInputRate,
                317.0, 881.0, 0.7);
            float[] first = new float[
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            float[] second = new float[first.Length];
            var converter = new DualSenseSpeakerFrameResampler();

            converter.ConvertNominalFrame(source, 0, first, 0);
            converter.Reset();
            converter.ConvertNominalFrame(source, 0, second, 0);

            AssertSamplesEqual(first, second, first.Length, 0.0f);
        }

        [TestMethod]
        public void PcmThirtyTwoToFortyEightChunkingMatchesSingleBuffer()
        {
            const int sourceFrames = 8193;
            byte[] source = CreateStereoPcm16(sourceFrames, 32000,
                431.0, 12917.0, 0.72);

            float[] single = ConvertPcmInChunks(source, 32000, 48000,
                sourceFrames);
            float[] chunked = ConvertPcmInChunks(source, 32000, 48000,
                1, 7, 31, 2, 257, 3, 64, 5, 509, 11, 127, 1024);

            Assert.AreEqual(single.Length, chunked.Length,
                "PCM chunking changed the streaming output count.");
            AssertSamplesEqual(single, chunked, single.Length, 1.0e-6f);
        }

        [TestMethod]
        public void PcmThirtyTwoToFortyEightHasStableLongRunCount()
        {
            const int sourceFrames = 32000 * 5;
            byte[] source = CreateStereoPcm16(sourceFrames, 32000,
                997.0, 1499.0, 0.7);
            float[] converted = ConvertPcmInChunks(source, 32000, 48000,
                1024);

            // Linear streaming interpolation retains exactly one input edge
            // until the next callback. It is not dropped; the following chunk
            // supplies the look-ahead needed to emit it.
            int expectedFrames = sourceFrames * 3 / 2 - 1;
            Assert.AreEqual(expectedFrames * 2, converted.Length);
        }

        [TestMethod]
        public void PcmThirtyTwoToFortyEightPreservesPhaseAcrossViiperCallbacks()
        {
            const int callbackFrames = 320;
            const int callbackCount = 1000;
            const int sourceFrames = callbackFrames * callbackCount;
            byte[] source = CreateStereoPcm16(sourceFrames, 32000,
                997.0, 1499.0, 0.7);

            float[] converted = ConvertPcmInChunks(source, 32000, 48000,
                callbackFrames);

            Assert.AreEqual((sourceFrames * 3 / 2 - 1) * 2,
                converted.Length,
                "A transient consumer shortage must not reset the converter " +
                "between VIIPER's 320-frame callbacks or discard its staged " +
                "source frame.");
        }

        [TestMethod]
        public void PcmFortyEightToFortyEightIsBitExactAcrossArbitraryChunks()
        {
            const int sourceFrames = 48000 * 5 + 137;
            byte[] source = CreateStereoPcm16(sourceFrames, 48000,
                997.0, 1499.0, 0.83);
            float[] converted = ConvertPcmInChunks(source, 48000, 48000,
                1, 7, 31, 2, 257, 3, 64, 5, 509, 11, 127, 1024);

            Assert.AreEqual(sourceFrames * 2, converted.Length,
                "Unity-rate conversion dropped or duplicated frames.");
            for (int sample = 0; sample < converted.Length; sample++)
            {
                int byteOffset = sample * sizeof(short);
                short expected = (short)(source[byteOffset] |
                    source[byteOffset + 1] << 8);
                Assert.AreEqual(expected / 32768.0f, converted[sample],
                    0.0f, $"Unity-rate sample differed at {sample}.");
            }
        }

        [TestMethod]
        public void PcmFortyEightToFortyEightHonorsDestinationWindow()
        {
            const int sourceFrames = 1537;
            const int destinationOffset = 7;
            byte[] source = CreateStereoPcm16(sourceFrames, 48000,
                431.0, 7043.0, 0.72);
            var converter = new DualSensePcm16SourceRateConverter(48000,
                48000);
            int capacity = converter.GetMaximumOutputFrames(sourceFrames);
            float[] destination = new float[
                destinationOffset + capacity * 2 + 5];
            Array.Fill(destination, float.NaN);

            int produced = converter.Convert(source, 0, source.Length,
                destination, destinationOffset, capacity);

            Assert.AreEqual(sourceFrames, produced);
            for (int index = 0; index < destinationOffset; index++)
            {
                Assert.IsTrue(float.IsNaN(destination[index]));
            }
            for (int sample = 0; sample < sourceFrames * 2; sample++)
            {
                int byteOffset = sample * sizeof(short);
                short expected = (short)(source[byteOffset] |
                    source[byteOffset + 1] << 8);
                Assert.AreEqual(expected / 32768.0f,
                    destination[destinationOffset + sample], 0.0f);
            }
            for (int index = destinationOffset + sourceFrames * 2;
                index < destination.Length; index++)
            {
                Assert.IsTrue(float.IsNaN(destination[index]));
            }
        }

        [TestMethod]
        public void PcmDownsamplingSuppressesAboveNyquistAlias()
        {
            const int sourceRate = 48000;
            const int outputRate = 32000;
            const int sourceFrames = sourceRate * 2;
            byte[] passband = CreateStereoPcm16(sourceFrames, sourceRate,
                5000.0, 5000.0, 0.75);
            byte[] stopband = CreateStereoPcm16(sourceFrames, sourceRate,
                20000.0, 20000.0, 0.75);

            float[] passbandOutput = ConvertPcmInChunks(passband,
                sourceRate, outputRate, 1024);
            float[] stopbandOutput = ConvertPcmInChunks(stopband,
                sourceRate, outputRate, 1024);
            double passbandRms = CalculateChannelRms(passbandOutput, 2000);
            double stopbandRms = CalculateChannelRms(stopbandOutput, 2000);

            Assert.IsTrue(passbandRms > 0.35,
                $"Passband was unexpectedly attenuated ({passbandRms:F6}).");
            Assert.IsTrue(stopbandRms < passbandRms * 0.2,
                $"Above-Nyquist energy was not filtered enough " +
                $"({stopbandRms:F6} versus {passbandRms:F6}).");
        }

        [TestMethod]
        public void PcmConversionRemainsFiniteAndWithinSignalRange()
        {
            const int sourceFrames = 32000;
            byte[] source = CreateStereoPcm16(sourceFrames, 32000,
                14000.0, 15000.0, 0.8);
            float[] converted = ConvertPcmInChunks(source, 32000, 48000,
                257, 1024, 31, 509);

            float maximum = 0.0f;
            foreach (float sample in converted)
            {
                Assert.IsTrue(float.IsFinite(sample));
                maximum = Math.Max(maximum, Math.Abs(sample));
            }

            Assert.IsTrue(maximum <= 1.0f,
                $"Resampling clipped or overflowed the signal " +
                $"({maximum:F6}).");
        }

        [TestMethod]
        public void PcmResetClearsInterpolationAndFilterHistory()
        {
            const int sourceFrames = 4096;
            byte[] source = CreateStereoPcm16(sourceFrames, 32000,
                1000.0, 3000.0, 0.7);
            var converter = new DualSensePcm16SourceRateConverter(32000,
                48000);
            int capacity = converter.GetMaximumOutputFrames(sourceFrames);
            float[] first = new float[capacity * 2];
            float[] second = new float[capacity * 2];

            int firstFrames = converter.Convert(source, 0, source.Length,
                first, 0, capacity);
            converter.Reset();
            int secondFrames = converter.Convert(source, 0, source.Length,
                second, 0, capacity);

            Assert.AreEqual(firstFrames, secondFrames);
            AssertSamplesEqual(first, second, firstFrames * 2, 0.0f);
        }

        [TestMethod]
        public void PcmFixedBlockPathDoesNotAllocateAfterWarmup()
        {
            const int sourceFrames = 1024;
            byte[] source = CreateStereoPcm16(sourceFrames, 32000,
                1000.0, 3000.0, 0.6);
            var converter = new DualSensePcm16SourceRateConverter(32000,
                48000);
            int capacity = converter.GetMaximumOutputFrames(sourceFrames);
            float[] output = new float[capacity * 2];

            converter.Convert(source, 0, source.Length, output, 0,
                capacity);
            converter.Convert(source, 0, source.Length, output, 0,
                capacity);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                converter.Convert(source, 0, source.Length, output, 0,
                    capacity);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                "The fixed-block PCM path allocated after warmup.");
        }

        [TestMethod]
        public void PcmUnityRatePathDoesNotAllocateAfterWarmup()
        {
            const int sourceFrames = 1024;
            byte[] source = CreateStereoPcm16(sourceFrames, 48000,
                1000.0, 3000.0, 0.6);
            var converter = new DualSensePcm16SourceRateConverter(48000,
                48000);
            int capacity = converter.GetMaximumOutputFrames(sourceFrames);
            float[] output = new float[capacity * 2];

            converter.Convert(source, 0, source.Length, output, 0,
                capacity);
            converter.Convert(source, 0, source.Length, output, 0,
                capacity);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                converter.Convert(source, 0, source.Length, output, 0,
                    capacity);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                "The unity-rate PCM path allocated after warmup.");
        }

        private static float[] ConvertFloatInChunks(float[] source,
            params int[] chunkPattern)
        {
            var converter = new DualSenseSpeakerFrameResampler();
            var result = new List<float>();
            int totalFrames = source.Length / 2;
            int sourceFrame = 0;
            int patternIndex = 0;
            while (sourceFrame < totalFrames)
            {
                int requested = chunkPattern[patternIndex %
                    chunkPattern.Length];
                int frames = Math.Min(requested, totalFrames - sourceFrame);
                int capacity = converter.GetMaximumOutputFrames(frames);
                float[] converted = new float[capacity * 2];
                int produced = converter.Convert(source, sourceFrame * 2,
                    frames, converted, 0, capacity);
                for (int sample = 0; sample < produced * 2; sample++)
                {
                    result.Add(converted[sample]);
                }

                sourceFrame += frames;
                patternIndex++;
            }

            return result.ToArray();
        }

        private static float[] ConvertPcmInChunks(byte[] source,
            int sourceRate, int outputRate, params int[] chunkPattern)
        {
            var converter = new DualSensePcm16SourceRateConverter(sourceRate,
                outputRate);
            var result = new List<float>();
            int totalFrames = source.Length /
                DualSensePcm16SourceRateConverter.BytesPerFrame;
            int sourceFrame = 0;
            int patternIndex = 0;
            while (sourceFrame < totalFrames)
            {
                int requested = chunkPattern[patternIndex %
                    chunkPattern.Length];
                int frames = Math.Min(requested, totalFrames - sourceFrame);
                int capacity = converter.GetMaximumOutputFrames(frames);
                float[] converted = new float[capacity * 2];
                int produced = converter.Convert(source,
                    sourceFrame *
                        DualSensePcm16SourceRateConverter.BytesPerFrame,
                    frames * DualSensePcm16SourceRateConverter.BytesPerFrame,
                    converted, 0, capacity);
                for (int sample = 0; sample < produced * 2; sample++)
                {
                    result.Add(converted[sample]);
                }

                sourceFrame += frames;
                patternIndex++;
            }

            return result.ToArray();
        }

        private static float[] CreateStereoFloat(int frames,
            double sampleRate, double leftFrequency, double rightFrequency,
            double amplitude)
        {
            var result = new float[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                result[frame * 2] = (float)(amplitude * Math.Sin(
                    2.0 * Math.PI * leftFrequency * frame / sampleRate));
                result[frame * 2 + 1] = (float)(amplitude * Math.Sin(
                    2.0 * Math.PI * rightFrequency * frame / sampleRate));
            }

            return result;
        }

        private static byte[] CreateStereoPcm16(int frames, int sampleRate,
            double leftFrequency, double rightFrequency, double amplitude)
        {
            var result = new byte[frames *
                DualSensePcm16SourceRateConverter.BytesPerFrame];
            for (int frame = 0; frame < frames; frame++)
            {
                short left = (short)Math.Round(short.MaxValue * amplitude *
                    Math.Sin(2.0 * Math.PI * leftFrequency * frame /
                        sampleRate));
                short right = (short)Math.Round(short.MaxValue * amplitude *
                    Math.Sin(2.0 * Math.PI * rightFrequency * frame /
                        sampleRate));
                WriteInt16(result, frame *
                    DualSensePcm16SourceRateConverter.BytesPerFrame, left);
                WriteInt16(result, frame *
                    DualSensePcm16SourceRateConverter.BytesPerFrame + 2,
                    right);
            }

            return result;
        }

        private static double CalculateChannelRms(float[] samples,
            int skipFrames)
        {
            int frames = samples.Length / 2;
            double sum = 0.0;
            int count = 0;
            for (int frame = Math.Min(skipFrames, frames); frame < frames;
                frame++)
            {
                double value = samples[frame * 2];
                sum += value * value;
                count++;
            }

            return count == 0 ? 0.0 : Math.Sqrt(sum / count);
        }

        private static void AssertSamplesEqual(float[] expected,
            float[] actual, int sampleCount, float tolerance)
        {
            for (int sample = 0; sample < sampleCount; sample++)
            {
                Assert.AreEqual(expected[sample], actual[sample], tolerance,
                    $"Resampled streams differ at sample {sample}.");
            }
        }

        private static void WriteInt16(byte[] destination, int offset,
            short value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
        }
    }
}
