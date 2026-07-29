using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Dsp;
using System;
using System.Collections.Generic;

namespace DS4Windows.Tests
{
    [TestClass]
    public class DualSenseSpeakerFrameResamplerTests
    {
        [TestMethod]
        public void ReferenceRouteAlwaysFeeds512AndEmits480WithClockCorrection()
        {
            const int packetCount = 10000;
            var converter = new DualSenseReferenceSpeakerFrameResampler();
            float[] source = new float[
                DualSenseReferenceSpeakerFrameResampler.MaximumInputFrames * 2];
            float[] output = new float[
                DualSenseReferenceSpeakerFrameResampler.OutputFrames * 2];
            long sourceFrame = 0;
            double expectedConsumedFrames = 0.0;
            int minimumRequest = int.MaxValue;
            int maximumRequest = 0;
            float previousLeft = 0.0f;
            bool hasPreviousLeft = false;
            float maximumStep = 0.0f;
            for (int packet = 0; packet < packetCount; packet++)
            {
                // Exercise more correction than the direct production servo's
                // +/-350 ppm bound while slewing continuously across zero.
                double ratio = 1.0 + Math.Sin(packet * 0.003) * 0.001;
                expectedConsumedFrames += ratio *
                    DualSenseReferenceSpeakerFrameResampler.
                        ReferenceInputFrames;
                converter.SetInputRateRatio(ratio);
                int requested = converter.PrepareOutputFrame();
                minimumRequest = Math.Min(minimumRequest, requested);
                maximumRequest = Math.Max(maximumRequest, requested);
                Assert.IsTrue(requested > 0 && requested <=
                    DualSenseReferenceSpeakerFrameResampler.MaximumInputFrames);
                FillStereoFloat(source, requested, sourceFrame,
                    DualSenseReferenceSpeakerFrameResampler.SourceRate,
                    523.0, 997.0, 0.65);
                Assert.AreEqual(
                    DualSenseReferenceSpeakerFrameResampler.OutputFrames,
                    converter.ConvertPreparedOutput(source, 0, requested,
                        output, 0));
                for (int frame = 0;
                    frame < DualSenseReferenceSpeakerFrameResampler.OutputFrames;
                    frame++)
                {
                    float left = output[frame * 2];
                    Assert.IsTrue(float.IsFinite(left));
                    if (hasPreviousLeft)
                    {
                        maximumStep = Math.Max(maximumStep,
                            Math.Abs(left - previousLeft));
                    }

                    previousLeft = left;
                    hasPreviousLeft = true;
                }
                sourceFrame += requested;
            }

            Assert.IsTrue(maximumRequest > minimumRequest,
                "Dynamic clock correction did not advance source phase.");
            Assert.IsTrue(Math.Abs(sourceFrame - expectedConsumedFrames) < 6.0,
                "The dynamic stage accumulated or drained a hidden frame reserve.");
            Assert.IsTrue(maximumStep < 0.12f,
                $"The corrected reference stages introduced a waveform " +
                $"jump of {maximumStep:F6}.");
        }

        [TestMethod]
        public void ReferenceRouteDoesNotAllocateAfterWarmup()
        {
            var converter = new DualSenseReferenceSpeakerFrameResampler();
            float[] source = new float[
                DualSenseReferenceSpeakerFrameResampler.MaximumInputFrames * 2];
            float[] output = new float[
                DualSenseReferenceSpeakerFrameResampler.OutputFrames * 2];

            for (int warmup = 0; warmup < 4; warmup++)
            {
                converter.SetInputRateRatio(warmup % 2 == 0 ?
                    0.99965 : 1.00035);
                int requested = converter.PrepareOutputFrame();
                converter.ConvertPreparedOutput(source, 0, requested,
                    output, 0);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                converter.SetInputRateRatio(iteration % 2 == 0 ?
                    0.99965 : 1.00035);
                int requested = converter.PrepareOutputFrame();
                converter.ConvertPreparedOutput(source, 0, requested,
                    output, 0);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                "The corrected reference hot path allocated after warmup.");
        }

        [TestMethod]
        public void AllocationFreeReferenceRouteMatchesWdlLinearStages()
        {
            const int packetCount = 64;
            var converter = new DualSenseReferenceSpeakerFrameResampler();
            var dynamicReference = new WdlResampler();
            dynamicReference.SetMode(true, 0, false);
            dynamicReference.SetFeedMode(false);
            dynamicReference.SetRates(48000.0, 48000.0);
            var fixedReference = new WdlResampler();
            fixedReference.SetMode(true, 0, false);
            fixedReference.SetFeedMode(true);
            fixedReference.SetRates(51200.0, 48000.0);

            float[] source = new float[
                DualSenseReferenceSpeakerFrameResampler.MaximumInputFrames * 2];
            float[] actual = new float[
                DualSenseReferenceSpeakerFrameResampler.OutputFrames * 2];
            float[] correctedReference = new float[
                DualSenseReferenceSpeakerFrameResampler.ReferenceInputFrames * 2];
            float[] expected = new float[
                DualSenseReferenceSpeakerFrameResampler.OutputFrames * 2];
            long sourceFrame = 0;
            for (int packet = 0; packet < packetCount; packet++)
            {
                double ratio = (packet % 4) switch
                {
                    0 => 0.99965,
                    1 => 1.00035,
                    2 => 1.00010,
                    _ => 0.99990,
                };
                converter.SetInputRateRatio(ratio);
                int requested = converter.PrepareOutputFrame();
                dynamicReference.SetRates(48000.0 * ratio, 48000.0);
                int referenceRequested = dynamicReference.ResamplePrepare(
                    DualSenseReferenceSpeakerFrameResampler.ReferenceInputFrames,
                    2, out float[] dynamicInput, out int dynamicOffset);
                Assert.AreEqual(referenceRequested, requested);

                FillStereoFloat(source, requested, sourceFrame, 48000.0,
                    523.0, 997.0, 0.65);
                Array.Copy(source, 0, dynamicInput, dynamicOffset,
                    requested * 2);
                Assert.AreEqual(512, dynamicReference.ResampleOut(
                    correctedReference, 0, requested, 512, 2));

                int fixedRequested = fixedReference.ResamplePrepare(512, 2,
                    out float[] fixedInput, out int fixedOffset);
                Assert.AreEqual(512, fixedRequested);
                Array.Copy(correctedReference, 0, fixedInput, fixedOffset,
                    correctedReference.Length);
                Assert.AreEqual(480, fixedReference.ResampleOut(expected, 0,
                    fixedRequested, 480, 2));
                Assert.AreEqual(480, converter.ConvertPreparedOutput(source, 0,
                    requested, actual, 0));

                for (int sample = 0; sample < actual.Length; sample++)
                {
                    Assert.AreEqual(expected[sample], actual[sample], 1.0e-6,
                        $"Packet {packet}, sample {sample} diverged from WDL.");
                }

                sourceFrame += requested;
            }
        }

        [TestMethod]
        public void OutputDrivenNominalPacketsAreExactAndBounded()
        {
            const int packetCount = 2000;
            var converter = new DualSenseSpeakerFrameResampler();
            float[] source = new float[
                DualSenseSpeakerFrameResampler.MaximumInputFrames * 2];
            float[] output = new float[
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            long sourceFrame = 0;
            for (int packet = 0; packet < packetCount; packet++)
            {
                int requested = converter.PrepareOutputFrame();
                Assert.IsTrue(requested > 0 && requested <=
                    DualSenseSpeakerFrameResampler.MaximumInputFrames);
                FillStereoFloat(source, requested, sourceFrame,
                    DualSenseSpeakerFrameResampler.NominalInputRate,
                    431.0, 997.0, 0.72);
                Assert.AreEqual(
                    DualSenseSpeakerFrameResampler.OutputFrames,
                    converter.ConvertPreparedOutput(source, 0, requested,
                        output, 0));
                sourceFrame += requested;
            }

            long nominalSourceFrames = packetCount *
                DualSenseSpeakerFrameResampler.NominalInputFrames;
            Assert.IsTrue(Math.Abs(sourceFrame - nominalSourceFrames) <=
                DualSenseSpeakerFrameResampler.MaximumInputFrames,
                $"Output-driven conversion consumed {sourceFrame} source " +
                $"frames instead of approximately {nominalSourceFrames}.");
        }

        [TestMethod]
        public void RepeatedPrepareWithoutConversionDoesNotAdvancePhase()
        {
            var converter = new DualSenseSpeakerFrameResampler();
            int firstRequest = converter.PrepareOutputFrame();
            int secondRequest = converter.PrepareOutputFrame();
            Assert.AreEqual(firstRequest, secondRequest);
            float[] source = CreateStereoFloat(secondRequest,
                DualSenseSpeakerFrameResampler.NominalInputRate,
                431.0, 997.0, 0.72);
            float[] output = new float[
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            Assert.AreEqual(DualSenseSpeakerFrameResampler.OutputFrames,
                converter.ConvertPreparedOutput(source, 0, secondRequest,
                    output, 0));
        }

        [TestMethod]
        public void FractionalClockCorrectionNeverPrefetchesAnOutputPacket()
        {
            const int packetCount = 4000;
            var converter = new DualSenseSpeakerFrameResampler();
            converter.SetInputRateRatio(1.000035);
            float[] source = new float[
                DualSenseSpeakerFrameResampler.MaximumInputFrames * 2];
            float[] output = new float[
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            int minimumRequest = int.MaxValue;
            int maximumRequest = 0;
            long consumed = 0;
            for (int packet = 0; packet < packetCount; packet++)
            {
                int requested = converter.PrepareOutputFrame();
                minimumRequest = Math.Min(minimumRequest, requested);
                maximumRequest = Math.Max(maximumRequest, requested);
                Assert.IsTrue(requested <=
                    DualSenseSpeakerFrameResampler.MaximumInputFrames);
                Assert.AreEqual(DualSenseSpeakerFrameResampler.OutputFrames,
                    converter.ConvertPreparedOutput(source, 0, requested,
                        output, 0));
                consumed += requested;
            }

            // The old feed-driven design sometimes consumed a second full
            // 512-frame block just to complete one 480-frame packet. An exact
            // output-driven request remains one source packet (plus WDL's
            // one-time look-ahead), even while the fractional phase advances.
            Assert.IsTrue(maximumRequest <
                DualSenseSpeakerFrameResampler.NominalInputFrames + 16,
                $"A packet unexpectedly requested {maximumRequest} frames.");
            Assert.IsTrue(maximumRequest > minimumRequest,
                "The fractional correction never advanced source phase.");
            double expected = packetCount *
                DualSenseSpeakerFrameResampler.NominalInputFrames * 1.000035;
            Assert.IsTrue(Math.Abs(consumed - expected) < 16.0,
                $"Fractional input count was {consumed}, expected " +
                $"approximately {expected:F3}.");
        }

        [TestMethod]
        public void SlewedClockCorrectionPreservesWaveformContinuity()
        {
            const int blockCount = 2000;
            const double frequency = 997.0;
            var converter = new DualSenseSpeakerFrameResampler();
            var output = new List<float>();
            float[] source = new float[
                DualSenseSpeakerFrameResampler.MaximumInputFrames * 2];
            float[] converted = new float[
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            long sourceFrame = 0;
            for (int block = 0; block < blockCount; block++)
            {
                double ratio = 1.0 + Math.Sin(block * 0.04) * 0.001;
                converter.SetInputRateRatio(ratio);
                int requested = converter.PrepareOutputFrame();
                FillStereoFloat(source, requested, sourceFrame,
                    DualSenseSpeakerFrameResampler.NominalInputRate,
                    frequency, frequency, 0.7);
                int produced = converter.ConvertPreparedOutput(source, 0,
                    requested, converted, 0);
                Assert.AreEqual(
                    DualSenseSpeakerFrameResampler.OutputFrames, produced);
                for (int sample = 0; sample < produced * 2; sample++)
                {
                    output.Add(converted[sample]);
                }

                sourceFrame += requested;
            }

            int totalFrames = blockCount *
                DualSenseSpeakerFrameResampler.OutputFrames;
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
            float[] source = new float[
                DualSenseSpeakerFrameResampler.MaximumInputFrames * 2];
            float[] output = new float[
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            var converter = new DualSenseSpeakerFrameResampler();

            ConvertOneOutputFrame(converter, source, output);
            ConvertOneOutputFrame(converter, source, output);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                ConvertOneOutputFrame(converter, source, output);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                "The fixed-frame realtime path allocated after warmup.");
        }

        [TestMethod]
        public void ResetRestartsFramePhaseDeterministically()
        {
            float[] source = new float[
                DualSenseSpeakerFrameResampler.MaximumInputFrames * 2];
            float[] first = new float[
                DualSenseSpeakerFrameResampler.OutputFrames * 2];
            float[] second = new float[first.Length];
            var converter = new DualSenseSpeakerFrameResampler();

            int firstRequest = converter.PrepareOutputFrame();
            FillStereoFloat(source, firstRequest, 0,
                DualSenseSpeakerFrameResampler.NominalInputRate,
                317.0, 881.0, 0.7);
            converter.ConvertPreparedOutput(source, 0, firstRequest,
                first, 0);
            converter.Reset();
            int secondRequest = converter.PrepareOutputFrame();
            Assert.AreEqual(firstRequest, secondRequest);
            converter.ConvertPreparedOutput(source, 0, secondRequest,
                second, 0);

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

        private static void ConvertOneOutputFrame(
            DualSenseSpeakerFrameResampler converter, float[] source,
            float[] output)
        {
            int requested = converter.PrepareOutputFrame();
            converter.ConvertPreparedOutput(source, 0, requested, output,
                0);
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

        private static void FillStereoFloat(float[] destination, int frames,
            long startFrame, double sampleRate, double leftFrequency,
            double rightFrequency, double amplitude)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                long position = startFrame + frame;
                destination[frame * 2] = (float)(amplitude * Math.Sin(
                    2.0 * Math.PI * leftFrequency * position / sampleRate));
                destination[frame * 2 + 1] = (float)(amplitude * Math.Sin(
                    2.0 * Math.PI * rightFrequency * position / sampleRate));
            }
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
